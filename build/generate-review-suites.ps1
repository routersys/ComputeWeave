#Requires -Version 7

<#
.SYNOPSIS
    Writes the map the pull request review recommends test suites from, derived from the projects by MSBuild.

.DESCRIPTION
    The review workflow runs without checking the pull request out, so it cannot ask MSBuild which test
    projects a changed file reaches. It reads build/review-suites.json from the default branch instead, and
    that file is what this writes: for every directory under src, the suites whose projects reference it, and
    for every file one project includes straight out of another project's directory, the suites that reach the
    including project. The map used to be a table written by hand inside the workflow, and a reference added
    or removed later left it stale without anything failing.

    Every project under src and tests is evaluated with MSBuild, so conditions, the imports of shared projects
    and the spelling of an include are MSBuild's concern and not this script's. ProjectReference items make the
    graph the suites are followed through; Compile, AdditionalFiles, EmbeddedResource and Content items that
    resolve into another directory under src are the shared files. A directory without a project of its own,
    a shared project, gets the suites every one of its included files reaches, and a file that reaches more
    than its directory carries an entry of its own.

    With -Check the file is regenerated and compared with the committed one instead of being written, and a
    difference fails, so CI refuses a project change that leaves the map behind.

.PARAMETER Check
    Compares the committed file with what would be written and fails on a difference, writing nothing.

.PARAMETER Path
    The repository root. Defaults to the parent of this script.

.EXAMPLE
    pwsh build/generate-review-suites.ps1

.EXAMPLE
    pwsh build/generate-review-suites.ps1 -Check
#>

[CmdletBinding()]
param(
    [switch] $Check,
    [string] $Path
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Path))
{
    $Path = Split-Path -Parent $PSScriptRoot
}

$root = (Resolve-Path -LiteralPath $Path).Path
$outputFile = 'build/review-suites.json'

# The suites the review recommends, in the order it lists them.
$suites = @(
    'ComputeWeave.Tests.SourceGenerators', 'ComputeWeave.Tests.Internals', 'ComputeWeave.Tests', 'ComputeWeave.Tests.DeviceLost',
    'ComputeWeave.D2D1.Tests.SourceGenerators', 'ComputeWeave.D2D1.Tests', 'ComputeWeave.D2D1.Tests.AssemblyLevelAttributes'
)

# Test projects that build on src without being recommended: the debug layer suite is asked for through the guarded areas of the review instead, and the global statements program is run, not tested. A new test project has to join the suites or this list.
$leftOut = @('ComputeWeave.Tests.DebugLayer', 'ComputeWeave.Tests.GlobalStatements')

# The item types that go into a build. A file of another project reached through one of these is a dependency on that file.
$itemTypes = @('Compile', 'AdditionalFiles', 'EmbeddedResource', 'Content')

# Returns the repository-relative path of a file with forward slashes.
function Get-RelativePath
{
    param([string] $FullPath)

    return [System.IO.Path]::GetRelativePath($root, $FullPath).Replace('\', '/')
}

# Returns the src or tests directory a file path belongs to, or nothing when it belongs to neither.
function Get-ProjectName
{
    param([string] $FullPath)

    $relative = (Get-RelativePath -FullPath $FullPath).Split('/')

    if ($relative.Length -gt 2 -and $relative[0] -in @('src', 'tests'))
    {
        return $relative[1]
    }

    return $null
}

# Asks MSBuild for the evaluated items of a project.
function Get-EvaluatedItems
{
    param([string] $Project)

    $output = & dotnet msbuild $Project "-getItem:ProjectReference,$($itemTypes -join ',')" -p:Platform=x64 -nologo -nodeReuse:false 2>&1

    if ($LASTEXITCODE -ne 0)
    {
        throw "Evaluating $Project failed:`n$($output -join [Environment]::NewLine)"
    }

    return ($output | Out-String | ConvertFrom-Json).Items
}

# Every project a suite reaches through its references, itself included, so a shared generator layer counts for each generator that references it.
function Get-Reach
{
    param([string] $Start)

    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $null = $seen.Add($Start)
    $stack.Push($Start)

    while ($stack.Count -gt 0)
    {
        $current = $stack.Pop()

        if (-not $references.ContainsKey($current))
        {
            continue
        }

        foreach ($other in $references[$current])
        {
            if ($seen.Add($other))
            {
                $stack.Push($other)
            }
        }
    }

    return $seen
}

# The suites that reach any of the given projects, in the order the review lists them.
function Get-Suites
{
    param([string[]] $Projects)

    return @($suites | Where-Object { $suite = $_; @($Projects | Where-Object { $reach[$suite].Contains($_) }).Count -gt 0 })
}

# The references of every project, and the files a project includes out of another directory under src.
$references = @{}
$included = @{}
$testProjects = [System.Collections.Generic.SortedSet[string]]::new()
$projectFiles = @(Get-ChildItem -Path (Join-Path $root 'src/*/*.csproj'), (Join-Path $root 'tests/*/*.csproj') -File | Sort-Object FullName)

foreach ($projectFile in $projectFiles)
{
    $me = Get-ProjectName -FullPath $projectFile.FullName
    $items = Get-EvaluatedItems -Project $projectFile.FullName
    $references[$me] = [System.Collections.Generic.HashSet[string]]::new()

    if ((Get-RelativePath -FullPath $projectFile.FullName).StartsWith('tests/'))
    {
        $null = $testProjects.Add($me)
    }

    foreach ($item in $items.ProjectReference)
    {
        $other = Get-ProjectName -FullPath $item.FullPath

        if ($other -and $other -ne $me)
        {
            $null = $references[$me].Add($other)
        }
    }

    foreach ($type in $itemTypes)
    {
        foreach ($item in $items.$type)
        {
            $relative = Get-RelativePath -FullPath $item.FullPath
            $other = Get-ProjectName -FullPath $item.FullPath

            if ($other -and $other -ne $me -and $relative.StartsWith('src/'))
            {
                if (-not $included.ContainsKey($relative))
                {
                    $included[$relative] = [System.Collections.Generic.HashSet[string]]::new()
                }

                $null = $included[$relative].Add($me)
            }
        }
    }
}

$reach = @{}

foreach ($suite in $suites)
{
    $reach[$suite] = Get-Reach -Start $suite
}

# A directory under src is a project of its own when it holds a csproj, and a shared project when it holds a shproj. The order is ordinal so that the file reads the same on every machine.
$sourceDirectories = [string[]] @(Get-ChildItem -Path (Join-Path $root 'src') -Directory | Where-Object { @(Get-ChildItem -Path $_.FullName -Filter '*.??proj' -File).Count -gt 0 } | ForEach-Object { $_.Name })
[System.Array]::Sort($sourceDirectories, [System.StringComparer]::Ordinal)
$ownProjects = @($sourceDirectories | Where-Object { @(Get-ChildItem -Path (Join-Path $root "src/$_") -Filter '*.csproj' -File).Count -gt 0 })

# A test project that builds on src is either recommended or left out on purpose; a new one is not allowed to slip through unnamed.
foreach ($test in $testProjects)
{
    if ($suites -contains $test -or $leftOut -contains $test)
    {
        continue
    }

    if (@((Get-Reach -Start $test) | Where-Object { $ownProjects -contains $_ }).Count -gt 0)
    {
        throw "$test builds on src, and neither the suites nor the list of what is left out names it. Add it to one of them in this script."
    }
}

# The row of a directory: the suites that reference its project, or for a shared project the suites every included file reaches.
$rows = [ordered]@{}

foreach ($name in $sourceDirectories)
{
    if ($ownProjects -contains $name)
    {
        $rows[$name] = @(Get-Suites -Projects @($name))

        continue
    }

    $common = $null

    foreach ($file in $included.Keys)
    {
        if (-not $file.StartsWith("src/$name/"))
        {
            continue
        }

        $needed = @(Get-Suites -Projects @($included[$file]))
        $common = if ($null -eq $common) { $needed } else { @($common | Where-Object { $needed -contains $_ }) }
    }

    $rows[$name] = @($suites | Where-Object { $common -contains $_ })
}

# A file that reaches more suites than the row of its directory carries them itself.
$entries = [ordered]@{}

$sharedFiles = [string[]] $included.Keys
[System.Array]::Sort($sharedFiles, [System.StringComparer]::Ordinal)

foreach ($file in $sharedFiles)
{
    $name = $file.Split('/')[1]
    $needed = @(Get-Suites -Projects @($included[$file]))
    $row = if ($rows.Contains($name)) { $rows[$name] } else { @() }

    if (@($needed | Where-Object { $row -notcontains $_ }).Count -gt 0)
    {
        $entries[$file] = $needed
    }
}

# The file is spelled by hand so that a row is one line and the diff of a change reads as the change.
function Format-List
{
    param([string[]] $Values)

    return '[' + (@($Values | Where-Object { $null -ne $_ } | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress }) -join ', ') + ']'
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('{')
$lines.Add('  "suites": ' + (Format-List -Values $suites) + ',')
$lines.Add('  "projects": {')
$rowLines = @($rows.Keys | ForEach-Object { '    ' + (ConvertTo-Json -InputObject $_ -Compress) + ': ' + (Format-List -Values $rows[$_]) })
$lines.Add($rowLines -join ",`n")
$lines.Add('  },')
$lines.Add('  "files": {')
$entryLines = @($entries.Keys | ForEach-Object { '    ' + (ConvertTo-Json -InputObject $_ -Compress) + ': ' + (Format-List -Values $entries[$_]) })
$lines.Add($entryLines -join ",`n")
$lines.Add('  }')
$lines.Add('}')
$generated = ($lines -join "`n") + "`n"

Write-Host ("{0,5} project(s) evaluated" -f $projectFiles.Count)
Write-Host ("{0,5} directory(ies) under src, {1} of them shared projects" -f $sourceDirectories.Count, ($sourceDirectories.Count - $ownProjects.Count))
Write-Host ("{0,5} file(s) included out of another directory, {1} with an entry of their own" -f $included.Count, $entries.Count)
Write-Host ''

$outputPath = Join-Path $root $outputFile

if (-not $Check)
{
    [System.IO.File]::WriteAllText($outputPath, $generated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "$outputFile written."

    exit 0
}

if (-not (Test-Path -LiteralPath $outputPath))
{
    Write-Host "$outputFile is missing."
    Write-Host ''
    Write-Host "Run pwsh build/generate-review-suites.ps1 and commit $outputFile."

    exit 1
}

$committed = [System.IO.File]::ReadAllText($outputPath).Replace("`r`n", "`n")

if ($committed -ceq $generated)
{
    Write-Host "$outputFile is what the projects say."

    exit 0
}

# git shows the difference as a diff, so an inserted or moved row reads as one change rather than as every line after it.
$generatedPath = Join-Path ([System.IO.Path]::GetTempPath()) 'review-suites.generated.json'
[System.IO.File]::WriteAllText($generatedPath, $generated, [System.Text.UTF8Encoding]::new($false))

Write-Host "$outputFile differs from what the projects say:"
Write-Host ''

& git --no-pager diff --no-index --no-color --unified=0 -- $outputPath $generatedPath | Select-Object -Skip 4 | ForEach-Object { Write-Host "  $_" }
Remove-Item -LiteralPath $generatedPath

Write-Host ''
Write-Host "Run pwsh build/generate-review-suites.ps1 and commit $outputFile with the change to the projects."

exit 1
