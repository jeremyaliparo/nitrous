# Publishes the app into bin\<release>\Nitrous-<version>.exe.
#
# The folder is the release line (0.14.0) and the file name is the full version, suffix included, so a
# tester build is never mistaken for the release of the same number:
#
#   bin\0.14.0\Nitrous-0.14.0-test.36.exe
#   bin\0.14.0\Nitrous-0.14.0.exe
#
# Refuses to overwrite an existing file: a tester may still be running it, and a name that silently
# changed contents is worse than a failed command. Bump the version instead.
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
Push-Location $root
try {
    $csproj = Get-Content app\Nitrous.csproj -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') {
        throw 'No <Version> element in app/Nitrous.csproj.'
    }

    $version = $Matches[1]
    $release = ($version -split '-')[0]
    $folder = Join-Path $root "bin\$release"
    $exe = Join-Path $folder "Nitrous-$version.exe"

    if (Test-Path $exe) {
        throw "$exe already exists. Bump the version in app/Nitrous.csproj, or delete the file deliberately."
    }

    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $staging = Join-Path $env:TEMP ("nitrous-publish-" + [guid]::NewGuid().ToString('N'))

    try {
        dotnet publish app/Nitrous.csproj -c $Configuration -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none -p:DebugSymbols=false -o $staging | Out-Host

        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

        Move-Item (Join-Path $staging 'Nitrous.exe') $exe
    }
    finally {
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    }

    $info = (Get-Item $exe).VersionInfo
    Write-Output ''
    Write-Output "file    : $exe"
    Write-Output "version : $($info.ProductVersion)"
    Write-Output "sha256  : $((Get-FileHash $exe -Algorithm SHA256).Hash)"
}
finally {
    Pop-Location
}
