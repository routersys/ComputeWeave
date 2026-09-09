#Requires -Version 7

<#
.SYNOPSIS
    Verifies that the kinds the release notes group by are the kinds this repository declares.

.DESCRIPTION
    build/generate-release-notes.ps1 groups the pull requests of a release by the labels they
    carry. The labels live in CONTRIBUTING, the kinds they mirror live in the pull request
    template, and the generator holds a copy of both so that a release does not depend on parsing
    prose. A copy drifts silently: a renamed label or a new kind leaves the generator with a group
    nothing lands in, and the pull requests fall into the section for a missing kind instead. That
    only becomes visible once a release is published, so it is compared here instead.

    Only the sets are compared. The generator lists the kinds in the order an upgrading caller
    reads them, which is deliberately not the order either document uses, so the order is not
    compared. The pairing of a label to its heading is the generator's own, and it is read there.

    A scan that reads nothing agrees with everything, so a document this cannot read at all is
    reported rather than passed over.

.PARAMETER Path
    The repository root. Defaults to the parent of this script.

.EXAMPLE
    pwsh build/verify-release-note-categories.ps1
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
$contributingFile = 'CONTRIBUTING.md'
$templateFile = '.github/pull_request_template.md'

# Reads a file that has to exist, because a missing one is the scan reading nothing.
function Get-RequiredText
{
    param([string] $File)

    $full = Join-Path $root $File

    if (-not (Test-Path -LiteralPath $full))
    {
        throw "$File is missing, so the kinds cannot be compared against it."
    }

    return [System.IO.File]::ReadAllText($full)
}

$failures = [System.Collections.Generic.List[string]]::new()

# The generator's own table, as label and heading pairs.
$generator = Get-RequiredText -File $generatorFile

$declared = [ordered]@{}

foreach ($match in [regex]::Matches($generator, "@\{\s*Label\s*=\s*'([^']+)';\s*Heading\s*=\s*'([^']+)'\s*\}"))
{
    $label = $match.Groups[1].Value

    if ($declared.Contains($label))
    {
        $failures.Add("$generatorFile lists the label ``$label`` twice.")

        continue
    }

    $declared[$label] = $match.Groups[2].Value
}

# The labels CONTRIBUTING names, in the sentence that states the correspondence.
$contributing = Get-RequiredText -File $contributingFile

$sentence = [regex]::Match($contributing, 'The kinds and the labels correspond one for one:([^.]+)\.')

$labels = @()

if (-not $sentence.Success)
{
    $failures.Add("$contributingFile no longer states the correspondence between the kinds and the labels in the wording this reads, so nothing names the labels. Update the pattern in this script when that sentence is reworded.")
}
else
{
    $labels = @([regex]::Matches($sentence.Groups[1].Value, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
}

# The kinds the template offers, taken from the Japanese half of each line of its Kind section.
$template = Get-RequiredText -File $templateFile

$section = [regex]::Match($template, '(?ms)^## Kind / 種別\s*$(.*?)^## ')

$kinds = @()

if (-not $section.Success)
{
    $failures.Add("$templateFile no longer has a Kind section in the shape this reads, so nothing names the kinds. Update the pattern in this script when that section is reshaped.")
}
else
{
    $kinds = @([regex]::Matches($section.Groups[1].Value, '(?m)^- (?:.+) / (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
}

if ($declared.Count -eq 0)
{
    $failures.Add("$generatorFile declares no kinds, so every pull request would be listed as having none.")
}

if ($sentence.Success -and $labels.Count -eq 0)
{
    $failures.Add("$contributingFile states the correspondence and names no label.")
}

if ($section.Success -and $kinds.Count -eq 0)
{
    $failures.Add("$templateFile has a Kind section and offers no kind.")
}

# Compares two sets and names what each side holds alone, so the drift reads as a direction.
function Compare-Set
{
    param([string[]] $Declared, [string[]] $Documented, [string] $What, [string] $File)

    foreach ($value in ($Declared | Where-Object { $Documented -notcontains $_ }))
    {
        $failures.Add("$generatorFile groups by the $What ``$value`` and $File does not name it.")
    }

    foreach ($value in ($Documented | Where-Object { $Declared -notcontains $_ }))
    {
        $failures.Add("$File names the $What ``$value`` and $generatorFile does not group by it.")
    }
}

if ($declared.Count -gt 0 -and $labels.Count -gt 0)
{
    Compare-Set -Declared @($declared.Keys) -Documented $labels -What 'label' -File $contributingFile
}

if ($declared.Count -gt 0 -and $kinds.Count -gt 0)
{
    Compare-Set -Declared @($declared.Values) -Documented $kinds -What 'kind' -File $templateFile
}

Write-Host ("{0,-44} {1} kind(s)" -f $generatorFile, $declared.Count)
Write-Host ("{0,-44} {1} label(s)" -f $contributingFile, $labels.Count)
Write-Host ("{0,-44} {1} kind(s)" -f $templateFile, $kinds.Count)
Write-Host ''

if ($failures.Count -eq 0)
{
    Write-Host 'The release notes group by every kind this repository declares, and by nothing else.'

    exit 0
}

Write-Host "$($failures.Count) kind(s) do not line up:"
Write-Host ''

foreach ($failure in $failures)
{
    Write-Host "  $failure"
}

Write-Host ''
Write-Host 'A pull request that renames a label or changes the kinds has to carry the change into the release notes. A pull request that only rewords the sentence or the section this reads has to carry the new wording into the patterns above.'

exit 1
