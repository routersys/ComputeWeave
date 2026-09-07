#Requires -Version 7

<#
.SYNOPSIS
    Verifies that each argument check speaks about one axis and one resource.

.DESCRIPTION
    The copy operations carry the same range check once per axis, so a check is normally written by
    copying its neighbor and renaming the axis. A rename that misses one name leaves a check that
    compares an offset on one axis against the extent of another, or against the extent of the other
    resource in the copy. Nothing reports that: the shape stays valid, the build succeeds, and the
    check keeps refusing and accepting ranges, just the wrong ones.

    Every call whose name begins with Throw is read here, the framework helpers and the ones this
    repository declares alike, and the axis and the resource each argument speaks about are taken
    from the names it is built from. A message is prose rather than a name, so a literal counts only
    when it is an axis label of one letter. An argument naming several axes is a volume rather than
    an axis, as in a total element count, so it is left out of the comparison. What is left has to
    agree: one axis across the arguments that name one, and one resource across the arguments that
    name one.

    The names are read as text rather than resolved, so a local standing for an axis is only seen
    when it is named after that axis. That is the convention the sources follow, and a name outside
    it is invisible here rather than reported.

.PARAMETER Path
    The repository root. Defaults to the parent of this script.

.EXAMPLE
    pwsh build/verify-axis-argument-checks.ps1
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
$sources = Join-Path $root 'src'

if (-not (Test-Path -LiteralPath $sources))
{
    throw "The sources are missing from $root."
}

# Reads the argument list opening at the given index and returns its arguments, commas inside nested
# parentheses, brackets, braces and literals belonging to the argument they sit in.
function Split-ArgumentList
{
    param([string] $Text, [int] $Start)

    $arguments = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $depth = 1
    $index = $Start

    while ($index -lt $Text.Length)
    {
        $character = $Text[$index]

        if ($character -eq '"' -or $character -eq "'")
        {
            $quote = $character
            $null = $current.Append($character)
            $index++

            while ($index -lt $Text.Length)
            {
                $null = $current.Append($Text[$index])

                if ($Text[$index] -eq '\')
                {
                    $index++

                    if ($index -lt $Text.Length)
                    {
                        $null = $current.Append($Text[$index])
                        $index++
                    }

                    continue
                }

                if ($Text[$index] -eq $quote)
                {
                    $index++

                    break
                }

                $index++
            }

            continue
        }

        if ($character -eq '(' -or $character -eq '[' -or $character -eq '{')
        {
            $depth++
        }
        elseif ($character -eq ')' -or $character -eq ']' -or $character -eq '}')
        {
            $depth--

            if ($depth -eq 0)
            {
                break
            }
        }

        if ($character -eq ',' -and $depth -eq 1)
        {
            $arguments.Add($current.ToString().Trim())
            $null = $current.Clear()
        }
        else
        {
            $null = $current.Append($character)
        }

        $index++
    }

    if ($current.ToString().Trim().Length -gt 0)
    {
        $arguments.Add($current.ToString().Trim())
    }

    return , $arguments
}

# Reads the axis a name speaks about, an extent standing for the axis it measures.
function Get-Axis
{
    param([string] $Name)

    if ($Name -match 'idth')
    {
        return 'X'
    }

    if ($Name -match 'eight')
    {
        return 'Y'
    }

    if ($Name -match 'epth')
    {
        return 'Z'
    }

    # A name of one letter is an axis whichever case it is written in, a message label included.
    if ($Name -match '^[xyz]$')
    {
        return $Name.ToUpperInvariant()
    }

    # A compound name carries its axis as a capital at the end, as in sourceOffsetX.
    if ($Name.Length -gt 1 -and $Name[-1] -cmatch '[XYZ]' -and $Name[-2] -cmatch '[a-z0-9]')
    {
        return [string] $Name[-1]
    }

    return $null
}

$files = Get-ChildItem -LiteralPath $sources -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

$calls = 0
$compared = 0
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files)
{
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')

    # A declaration is preceded by its return type, so the lookbehind leaves the helpers themselves out.
    foreach ($match in [regex]::Matches($text, '(?<!\w\s+)\bThrow\w*\s*\('))
    {
        $calls++

        $arguments = Split-ArgumentList -Text $text -Start ($match.Index + $match.Length)
        $axes = [System.Collections.Generic.List[string]]::new()
        $sides = [System.Collections.Generic.List[string]]::new()

        foreach ($argument in $arguments)
        {
            # A message is prose rather than a name, so a literal is read only when it is an axis label.
            $labels = [regex]::Matches($argument, '"([XYZxyz])"') | ForEach-Object { $_.Groups[1].Value }
            $stripped = [regex]::Replace($argument, '@?"(?:[^"\\]|\\.)*"', ' ')
            $names = @([regex]::Matches($stripped, '[A-Za-z_]\w*') | ForEach-Object { $_.Value }) + $labels
            $carried = @($names | ForEach-Object { Get-Axis -Name $_ } | Where-Object { $null -ne $_ } | Sort-Object -Unique)

            # An argument naming several axes is a volume rather than an axis, as in width * height.
            if ($carried.Count -eq 1)
            {
                $axes.Add($carried[0])
            }

            foreach ($name in $names)
            {
                if ($name -cmatch '^source')
                {
                    $sides.Add('source')

                    break
                }

                if ($name -cmatch '^destination')
                {
                    $sides.Add('destination')

                    break
                }
            }
        }

        if ($axes.Count -eq 0)
        {
            continue
        }

        $compared++

        $line = ($text.Substring(0, $match.Index) -split "`n").Count
        $written = ($arguments -join ', ') -replace '\s+', ' '
        $distinctAxes = @($axes | Sort-Object -Unique)
        $distinctSides = @($sides | Sort-Object -Unique)

        if ($distinctAxes.Count -gt 1)
        {
            $failures.Add("${relative}:${line} speaks about $($distinctAxes -join ' and ') in one check: $written")
        }

        if ($distinctSides.Count -gt 1)
        {
            $failures.Add("${relative}:${line} speaks about $($distinctSides -join ' and ') in one check: $written")
        }
    }
}

if ($calls -eq 0)
{
    throw 'No argument check was found, so this verified nothing. The pattern this reads has stopped matching the sources.'
}

Write-Host ("{0,5} calls to a throw helper" -f $calls)
Write-Host ("{0,5} of them name an axis and are compared" -f $compared)
Write-Host ''

if ($failures.Count -eq 0)
{
    Write-Host 'Every argument check names one axis and one resource.'

    exit 0
}

Write-Host "$($failures.Count) check(s) do not:"
Write-Host ''

foreach ($failure in $failures)
{
    Write-Host "  $failure"
}

Write-Host ''
Write-Host 'A check copied from the neighboring axis has to carry every name over, the extent and the resource included.'

exit 1
