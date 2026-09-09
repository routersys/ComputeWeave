#Requires -Version 7

<#
.SYNOPSIS
    Verifies that the release notes generator can be invoked the way the shipping paths invoke it.

.DESCRIPTION
    build/generate-release-notes.ps1 is the one script under build that no ordinary change runs. The
    checks run on a push or a pull request; this one runs once, when a version is tagged or the
    manual path is started, in a job that comes after the packages are published. A script broken by
    an edit is therefore found after the packages are on NuGet, with the release left without notes.

    Two shapes of break are not visible to the check that compares the kinds, because that one reads
    the script as text: a syntax error anywhere in the file, and a parameter renamed on one side of
    the call. The second matters most on the manual path, which runs neither locally nor in CI.

    Parsing the script answers both from one step. The parser fails on a syntax error, and the tree
    it returns names the parameters and says which of them are mandatory. The invocation is read out
    of the workflow by the indentation of its run block, which is what makes it a block in YAML, so
    a single line and a continued line are read the same way.

    Running the script is not part of this. Resolving a range needs the tags, which the document
    path does not fetch, and grouping needs the API, which would make a check depend on credentials.

.PARAMETER Path
    The repository root. Defaults to the parent of this script.

.EXAMPLE
    pwsh build/verify-release-note-invocation.ps1
#>

[CmdletBinding()]
param(
    [string] $Path
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Path))
{
    $Path = Split-Path -Parent $PSScriptRoot
}

$root = (Resolve-Path -LiteralPath $Path).Path

$generatorFile = 'build/generate-release-notes.ps1'

# The paths that publish. Both invoke the generator, and one of them never runs anywhere else.
$workflowFiles = @(
    '.github/workflows/release.yml'
    '.github/workflows/quick-release.yml'
)

$failures = [System.Collections.Generic.List[string]]::new()

function Get-RequiredPath
{
    param([string] $File)

    $full = Join-Path $root $File

    if (-not (Test-Path -LiteralPath $full))
    {
        throw "$File is missing, so the invocation cannot be compared against it."
    }

    return $full
}

# The parameters the script declares, and the ones a caller has to pass.
$tokens = $null
$parseErrors = $null

$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Get-RequiredPath -File $generatorFile), [ref] $tokens, [ref] $parseErrors)

$declared = @()
$mandatory = @()

if ($parseErrors.Count -gt 0)
{
    foreach ($parseError in $parseErrors)
    {
        $failures.Add("$generatorFile does not parse: line $($parseError.Extent.StartLineNumber), $($parseError.Message)")
    }
}
elseif ($null -eq $ast.ParamBlock)
{
    $failures.Add("$generatorFile declares no parameters, so the workflows have nothing to pass.")
}
else
{
    foreach ($parameter in $ast.ParamBlock.Parameters)
    {
        $name = $parameter.Name.VariablePath.UserPath
        $declared += $name

        foreach ($attribute in $parameter.Attributes)
        {
            if ($attribute -isnot [System.Management.Automation.Language.AttributeAst])
            {
                continue
            }

            if ($attribute.TypeName.Name -ne 'Parameter')
            {
                continue
            }

            foreach ($argument in $attribute.NamedArguments)
            {
                # Mandatory on its own means true, so an omitted expression counts as set.
                if ($argument.ArgumentName -ne 'Mandatory')
                {
                    continue
                }

                if ($argument.ExpressionOmitted -or "$($argument.Argument)" -eq '$true')
                {
                    $mandatory += $name
                }
            }
        }
    }
}

# The run block holding the invocation. YAML delimits a block scalar by indentation, so the block is
# the run line plus every following line that is blank or indented further than it.
function Get-Invocations
{
    param([string] $File)

    $lines = [System.IO.File]::ReadAllText((Get-RequiredPath -File $File)) -split '\r?\n'

    $found = @()

    for ($index = 0; $index -lt $lines.Count; $index++)
    {
        if ($lines[$index] -notmatch '^(\s*)run:')
        {
            continue
        }

        $indent = $Matches[1].Length
        $block = @($lines[$index])

        for ($next = $index + 1; $next -lt $lines.Count; $next++)
        {
            $line = $lines[$next]

            if ([string]::IsNullOrWhiteSpace($line))
            {
                $block += $line

                continue
            }

            if (($line.Length - $line.TrimStart().Length) -le $indent)
            {
                break
            }

            $block += $line
        }

        $text = $block -join "`n"

        if ($text -match [regex]::Escape($generatorFile))
        {
            $found += , @($text)
        }
    }

    return $found
}

foreach ($file in $workflowFiles)
{
    $invocations = @(Get-Invocations -File $file)

    if ($invocations.Count -ne 1)
    {
        $failures.Add("$file invokes $generatorFile $($invocations.Count) time(s); a shipping path invokes it once.")

        continue
    }

    # Only the switches of the call, so an expression carrying a hyphen is not read as one.
    $passed = @([regex]::Matches($invocations[0], '(?<=\s)-([A-Za-z][A-Za-z0-9]*)\b') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique)

    foreach ($name in ($passed | Where-Object { $declared -notcontains $_ }))
    {
        $failures.Add("$file passes ``-$name`` and $generatorFile does not declare it.")
    }

    foreach ($name in ($mandatory | Where-Object { $passed -notcontains $_ }))
    {
        $failures.Add("$file does not pass ``-$name``, which $generatorFile requires.")
    }

    Write-Host ("{0,-40} {1} switch(es) passed" -f $file, $passed.Count)
}

Write-Host ("{0,-40} {1} parameter(s), {2} mandatory" -f $generatorFile, $declared.Count, $mandatory.Count)
Write-Host ''

if ($failures.Count -eq 0)
{
    Write-Host 'The release notes generator parses, and both shipping paths invoke it with names it declares.'

    exit 0
}

Write-Host "$($failures.Count) problem(s) with the invocation:"
Write-Host ''

foreach ($failure in $failures)
{
    Write-Host "  $failure"
}

Write-Host ''
Write-Host 'A pull request that renames a parameter has to carry the new name into both shipping paths.'

exit 1
