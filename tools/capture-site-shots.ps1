<#
.SYNOPSIS
    Re-takes the site screenshots (docs/assets) from the current build.

.DESCRIPTION
    Builds the app with an asInvoker manifest so it can be launched without the normal
    administrator prompt, sets a presentable Global preset, captures the four pages at
    375x660, then restores the registry and the EC backlight.

    Run it elevated for real temperatures and fan RPM: a non-elevated capture renders
    "--" for both. The UAC prompt is expected once; the capture itself then runs per page.

    LIGHT is captured with sound sync on. With sync off the page shows
    "Fn keys are at N%" whenever the physical backlight differs from the saved preset,
    and sync also puts the SYNC card in view.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's folder.

.PARAMETER OutputDir
    Where to write the PNGs. Defaults to <RepoRoot>\docs\assets.

.PARAMETER Effect
    Global effect used for the shots. Default Neon.

.PARAMETER Colour
    Global colour used for the shots. Default the Nitrous accent.

.EXAMPLE
    Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File',
      'D:\...\nitrous\tools\capture-site-shots.ps1' -Wait
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$OutputDir = '',
    [string]$Effect = 'Neon',
    [string]$Colour = '#B388FF'
)

$ErrorActionPreference = 'Stop'

if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'docs\assets' }
$reg = 'HKCU:\Software\Nitrous'
$manifest = Join-Path $PSScriptRoot 'asinvoker.manifest'
$buildDir = Join-Path $RepoRoot 'bin\Release\net10.0-windows'
$exe = Join-Path $buildDir 'Nitrous.exe'

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevated: $elevated"
if (-not $elevated) {
    Write-Warning 'Not elevated: temperatures and fan RPM will render as "--".'
}

Push-Location $RepoRoot
try {
    dotnet build app\Nitrous.csproj -c Release "-p:ApplicationManifest=$manifest" -v q --nologo |
        Select-Object -Last 2
}
finally {
    Pop-Location
}
if (-not (Test-Path $exe)) { throw "capture exe not found: $exe" }

# Keys the shots depend on. Anything missing is simply not restored.
$keys = 'KbLightingSource', 'KbGlobal_Effect', 'KbGlobal_Color', 'KbGlobal_Brightness',
        'KbGlobal_Speed', 'KbStyle', 'KbSyncEnabled', 'KbSyncSource', 'KbSoundMode',
        'KbSyncTarget', 'KbSoundSensitivity'
$saved = @{}
foreach ($k in $keys) {
    $saved[$k] = (Get-ItemProperty $reg -Name $k -ErrorAction SilentlyContinue).$k
}

function Set-Key([string]$name, $value, [string]$type) {
    Set-ItemProperty $reg -Name $name -Value $value -Type $type
}

function Restore-Keys {
    foreach ($k in $keys) {
        $v = $saved[$k]
        if ($null -eq $v) {
            Remove-ItemProperty $reg -Name $k -ErrorAction SilentlyContinue
        }
        else {
            $type = if ($v -is [int]) { 'DWord' } else { 'String' }
            Set-ItemProperty $reg -Name $k -Value $v -Type $type
        }
    }
}

function Capture([string]$page, [string]$file) {
    $target = Join-Path $OutputDir $file
    if (Test-Path $target) { Remove-Item $target -Force }
    & $exe --screenshot $page --scale 1 --out $target | Out-Null
    if (-not (Test-Path $target)) { throw "capture failed: $page" }
    Write-Host ('{0,-14} {1,8:N0} bytes' -f $file, (Get-Item $target).Length)
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

try {
    # A presentable preset; the shots should not show a zeroed slider.
    Set-Key KbLightingSource 'Global' 'String'
    Set-Key KbGlobal_Effect $Effect 'String'
    Set-Key KbGlobal_Color $Colour 'String'
    Set-Key KbGlobal_Brightness 100 'DWord'
    Set-Key KbGlobal_Speed 4 'DWord'
    Set-Key KbStyle 'Static' 'String'
    Set-Key KbSyncEnabled 0 'DWord'

    Capture 'dash' 'dashboard.png'
    Capture 'gpu' 'gpu.png'
    Capture 'setup' 'settings.png'

    # LIGHT: sync on hides the Fn hint and shows the SYNC card.
    Set-Key KbSyncSource 'Sound' 'String'
    Set-Key KbSoundMode 'Vibe' 'String'
    Set-Key KbSyncTarget 'Both' 'String'
    Set-Key KbSoundSensitivity 6 'DWord'
    Set-Key KbSyncEnabled 1 'DWord'
    Start-Sleep -Milliseconds 300
    Capture 'kb' 'keyboard.png'
}
finally {
    Restore-Keys
    Write-Host 'registry restored'
}
