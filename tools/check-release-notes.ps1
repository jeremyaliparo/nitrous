<#
.SYNOPSIS
    Dry-run the release workflow's changelog extraction.

.DESCRIPTION
    Mirrors the "Create GitHub release" step of .github/workflows/release.yml: find the
    '## <version>' heading, stop at the next '## ', strip a release-name marker, and report
    what the release would be called. Exits 1 when the section is missing or empty, which is
    what the workflow does.

.PARAMETER Version
    Version to look up, for example 0.11.0.

.PARAMETER ChangelogPath
    Defaults to <repo>\CHANGELOG.md.

.EXAMPLE
    pwsh -File tools\check-release-notes.ps1 -Version 0.11.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ChangelogPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ChangelogPath)) { throw "no changelog at $ChangelogPath" }

$lines = Get-Content $ChangelogPath
$start = -1
$heading = '^##\s+\[?' + [regex]::Escape($Version) + '\]?(\s|$)'
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no '## $Version' section." }

$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { $end = $i; break }
}

$section = ($lines[$start..($end - 1)] -join "`n").TrimEnd()
if ([string]::IsNullOrWhiteSpace($section)) { throw "The changelog section for $Version is empty." }

$title = "Nitrous $Version"
$nameMatch = [regex]::Match($section, '<!--\s*release-name:\s*(.+?)\s*-->')
if ($nameMatch.Success) {
    $title = $nameMatch.Groups[1].Value.Trim()
    $section = ($section -replace [regex]::Escape($nameMatch.Value), '').TrimEnd()
}

Write-Host "title   : $title"
Write-Host "lines   : $(($section -split "`n").Count)"
Write-Host "marker  : $(if ($section -match 'release-name') { 'STILL PRESENT (check for a second marker)' } else { 'stripped' })"
Write-Host '--- first lines ---'
($section -split "`n") | Select-Object -First 6 | ForEach-Object { Write-Host $_ }
