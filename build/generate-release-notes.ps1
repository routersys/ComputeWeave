#Requires -Version 7

<#
.SYNOPSIS
    Writes the release notes for a tag from the commits and the merged pull requests in its range.

.DESCRIPTION
    The notes were the commit subjects of the range and nothing else. A release carrying two
    hundred of them buries the few entries an upgrading caller has to read, and nothing in a
    subject says whether it moved the public API or the build. Every pull request here declares
    its kinds and carries the matching labels, so this reads those labels and lists the pull
    requests that carry one first, grouped by kind. The commit list follows unchanged, because
    commits also reach the default branch without a pull request and dropping them loses them.

    A pull request is listed once, under the first kind it carries in the order below, so one that
    is both a public API addition and documentation reads as the addition. A pull request carrying
    no kind is listed apart rather than dropped, because a missing label is exactly the thing that
    should be visible in the published notes.

    The grouped section is not a precondition for describing the release. When nothing resolves to
    a pull request, a line in its place says the section is missing and the commit list still
    carries the whole range. Whether that was the API being out of reach or the numbers naming
    something else goes to the log, because it is not something a reader of the notes can act on.

    A tag that does not exist yet, given with the tip of the default branch, produces the notes the
    next release would carry. That is the same thing to read before deciding to cut one, and before
    merging a pull request whose label decides where it lands.

    The order below is not the order of the template. It is the order an upgrading caller reads:
    first what can break them, then what they can adopt, then what changed underneath them. A
    public API addition cannot break an existing caller, so it does not lead.

    That order rests on what the template says each kind means, not on how the labels have been
    applied, because the application is not uniform. Two merged pull requests turn a dispatch that
    used to run into a throw: the one that introduced the requirement carries no behavior label,
    and the one that widened where the requirement is detected carries it. This orders the labels
    it is given; which labels a pull request carries is settled in that pull request, and a
    caller-breaking change labelled as neither of the first two kinds is ranked below them.

.PARAMETER Version
    The version being released, as it appears in the install line.

.PARAMETER CurrentTag
    The tag of this release. It is excluded when the previous tag is looked up.

.PARAMETER CurrentSha
    The commit being released.

.PARAMETER Repository
    The owner and name of the repository the pull requests are read from. Defaults to
    GITHUB_REPOSITORY, and to the origin remote when that is not set.

.PARAMETER OutputPath
    The file the notes are written to.

.PARAMETER TestsNotRun
    States that the release was packed without running the test suites.

.PARAMETER Path
    The repository root. Defaults to the parent of this script.

.EXAMPLE
    pwsh build/generate-release-notes.ps1 -Version 2.5.0 -CurrentTag v2.5.0 -CurrentSha v2.5.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $CurrentTag,
    [string] $CurrentSha = 'HEAD',
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $OutputPath = 'release-notes.md',
    [switch] $TestsNotRun,
    [string] $Path
)

$ErrorActionPreference = 'Stop'

# A number that names an issue rather than a pull request answers 404, and that has to be skipped
# rather than end the release. Whether a non-zero native exit throws is a preference whose default
# has moved between PowerShell versions, so it is set here and the exit codes are read below.
$PSNativeCommandUseErrorActionPreference = $false

# git and gh answer in UTF-8, and PowerShell decodes a native command's output with the console
# encoding, which is not UTF-8 everywhere. Run from a shell whose console is Shift-JIS, the subjects
# arrive mangled and the JSON of a Japanese title stops parsing, so this is set rather than
# inherited. Setting it works with the output redirected, which is how a workflow runs it.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if ([string]::IsNullOrEmpty($Path))
{
    $Path = Split-Path -Parent $PSScriptRoot
}

$root = (Resolve-Path -LiteralPath $Path).Path

# The kinds of the pull request template, paired with the labels CONTRIBUTING gives them, in the
# order an upgrading caller reads. build/verify-release-note-categories.ps1 holds this to both.
$kinds = @(
    @{ Label = 'behavior change';       Heading = 'API変更を伴わない挙動の変更' }
    @{ Label = 'public api';            Heading = '公開APIの追加' }
    @{ Label = 'analyzer or generator'; Heading = 'アナライザーまたはジェネレーター' }
    @{ Label = 'bug';                   Heading = '不具合修正' }
    @{ Label = 'performance';           Heading = '性能' }
    @{ Label = 'documentation';         Heading = '文書' }
    @{ Label = 'build and ci';          Heading = 'ビルド、パッケージ、CI' }
)

# Runs git in the repository and fails the whole script when git does, so that an empty range is
# never mistaken for a release with nothing in it.
function Invoke-Git
{
    param([string[]] $Arguments)

    $output = @(git -C $root @Arguments)

    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output)
}

if ([string]::IsNullOrWhiteSpace($Repository))
{
    $url = @(git -C $root remote get-url origin 2>$null)

    if ($LASTEXITCODE -eq 0 -and $url.Count -gt 0)
    {
        $match = [regex]::Match($url[0], '[:/]([^/:]+/[^/]+?)(?:\.git)?$')

        if ($match.Success)
        {
            $Repository = $match.Groups[1].Value
        }
    }
}

# One line, taken through the pipeline so that a single line does not index by character.
$sha = [string] (Invoke-Git @('rev-parse', $CurrentSha) | Select-Object -First 1)

# The tag of the previous release, so the range is what this release adds. Tags that are not
# ancestors of this commit are not releases of this branch and are left out by --merged.
$previous = Invoke-Git @('tag', '--merged', $sha, '--sort=-version:refname') |
    Where-Object { $_ -match '^v[0-9]+\.[0-9]+\.[0-9]+$' -and $_ -ne $CurrentTag } |
    Select-Object -First 1

# The first release has no previous tag, so it falls back to the most recent commits.
$range = if ($previous) { @("$previous..$sha") } else { @('-n', '30', $sha) }

# A revision range and a path can be spelled the same, so the range is closed off from paths.
$terminator = if ($previous) { @('--') } else { @() }

$commits = @(Invoke-Git (@('log') + $range + @('--no-merges', '--pretty=format:- %s (%h)') + $terminator) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$subjects = @(Invoke-Git (@('log') + $range + @('--pretty=format:%s') + $terminator) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

# The pull requests of the range, in the order their commits appear. GitHub writes the number into
# the subject of a merge commit, and into the subject itself when the merge is squashed; this tree
# had 122 of the first and none of the second on 2026-09-08, and reading both keeps the section
# alive if the merge strategy changes.
$numbers = [System.Collections.Generic.List[int]]::new()

foreach ($subject in $subjects)
{
    $match = [regex]::Match($subject, '^Merge pull request #([0-9]+)\b')

    if (-not $match.Success)
    {
        $match = [regex]::Match($subject, '\(#([0-9]+)\)\s*$')
    }

    if (-not $match.Success)
    {
        continue
    }

    $number = [int] $match.Groups[1].Value

    if (-not $numbers.Contains($number))
    {
        $numbers.Add($number)
    }
}

$pulls = [System.Collections.Generic.List[object]]::new()

$available = ($null -ne (Get-Command gh -ErrorAction SilentlyContinue)) -and -not [string]::IsNullOrWhiteSpace($Repository)

if ($numbers.Count -gt 0 -and $available)
{
    foreach ($number in $numbers)
    {
        $json = gh api "repos/$Repository/pulls/$number" --jq '{number: .number, title: .title, labels: [.labels[].name]}' 2>$null

        # A number that names an issue rather than a pull request answers 404 and is skipped.
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json))
        {
            continue
        }

        $pulls.Add(($json | ConvertFrom-Json))
    }
}

# Nothing resolving is either the API being out of reach or every number naming something else. Both
# leave the grouped section with no content, so the notes state that outcome and the log says which.
$resolvedAny = $pulls.Count -gt 0

$grouped = [ordered]@{}

foreach ($kind in $kinds)
{
    $grouped[$kind.Heading] = [System.Collections.Generic.List[string]]::new()
}

$uncategorized = [System.Collections.Generic.List[string]]::new()

foreach ($pull in $pulls)
{
    $heading = $null

    foreach ($kind in $kinds)
    {
        if ($pull.labels -contains $kind.Label)
        {
            $heading = $kind.Heading

            break
        }
    }

    $entry = "- $($pull.title) (#$($pull.number))"

    if ($null -ne $heading)
    {
        $grouped[$heading].Add($entry)
    }
    else
    {
        $uncategorized.Add($entry)
    }
}

$lines = [System.Collections.Generic.List[string]]::new()

if ($numbers.Count -gt 0)
{
    $lines.Add('### 種別ごとの変更')
    $lines.Add('')

    if (-not $resolvedAny)
    {
        $lines.Add('この範囲では種別ごとの一覧を作れませんでした。次の一覧が範囲の全体です。')
        $lines.Add('')
    }
    else
    {
        foreach ($heading in $grouped.Keys)
        {
            if ($grouped[$heading].Count -eq 0)
            {
                continue
            }

            $lines.Add("#### $heading")
            $lines.Add('')
            $lines.AddRange($grouped[$heading])
            $lines.Add('')
        }

        # A pull request without a kind is listed rather than dropped, so the missing label shows.
        if ($uncategorized.Count -gt 0)
        {
            $lines.Add('#### 種別の指定が無いもの')
            $lines.Add('')
            $lines.AddRange($uncategorized)
            $lines.Add('')
        }
    }
}

$lines.Add('### すべてのコミット')
$lines.Add('')

if ($commits.Count -gt 0)
{
    $lines.AddRange([string[]] $commits)
}
else
{
    # git failing throws above, so an empty list is a range that holds no commit of its own.
    $lines.Add('この範囲に見出しはありません。')
}

$lines.Add('')
$lines.Add('### インストール')
$lines.Add('')
$lines.Add('```sh')
$lines.Add("dotnet add package ComputeWeave --version $Version")
$lines.Add('```')
$lines.Add('')
$lines.Add('### NuGet')
$lines.Add('')
$lines.Add("https://www.nuget.org/packages/ComputeWeave/$Version")

if ($TestsNotRun)
{
    $lines.Add('')
    $lines.Add('テストは実行していません。パッケージ作成時のビルドだけを実行しています。')
}

$text = ($lines -join "`n") + "`n"

[System.IO.File]::WriteAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))

$counted = @($grouped.Keys | ForEach-Object { $grouped[$_].Count } | Measure-Object -Sum).Sum

if ($null -eq $counted)
{
    $counted = 0
}

# The range as git was given it, rather than as it was asked for, so the two cannot drift apart.
$described = if ($previous) { "$previous..$($sha.Substring(0, 8))" } else { "the most recent 30 commits of $($sha.Substring(0, 8))" }

Write-Host ("Range {0}, {1} commit(s), {2} reference(s), {3} resolved, {4} grouped, {5} without a kind." -f
    $described,
    $commits.Count,
    $numbers.Count,
    $pulls.Count,
    $counted,
    $uncategorized.Count)

# The notes say only that the grouped section is missing. Which of the two produced that is here.
if ($numbers.Count -gt 0 -and -not $resolvedAny)
{
    if (-not $available)
    {
        Write-Host 'The GitHub CLI or the repository name was not available, so the notes carry the commit list alone.'
    }
    else
    {
        Write-Host 'No reference in the range resolved to a pull request, so the notes carry the commit list alone.'
    }
}

exit 0
