<#
.SYNOPSIS
    Builds the TeslaPC Rust server.

.DESCRIPTION
    Wraps cargo build so a build is one command from the repo root.
    The binary is teslapc/target/<config>/teslapc.exe. Static pages are compiled in.

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER Publish
    Copy the built executable into the output folder.

.PARAMETER Output
    Publish output directory (default: .\publish). Only used with -Publish.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Publish
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Publish,

    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
$manifest = Join-Path $PSScriptRoot 'teslapc\Cargo.toml'

# A normal PowerShell has no LIB. VS 18 link.exe then cannot open msvcrt.lib.
if (-not $env:LIB -or -not (Test-Path -LiteralPath (Join-Path ($env:LIB -split ';' | Select-Object -First 1) 'msvcrt.lib'))) {
    $env:LIB = 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Tools\MSVC\14.44.35207\lib\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\um\x64'
    $env:INCLUDE = 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Tools\MSVC\14.44.35207\include;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\ucrt;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um;C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared'
}

if ($Configuration -eq 'Release') {
    Write-Host "Building Release ..."
    cargo build --manifest-path $manifest --release
    $built = Join-Path $PSScriptRoot 'teslapc\target\release\teslapc.exe'
} else {
    Write-Host "Building Debug ..."
    cargo build --manifest-path $manifest
    $built = Join-Path $PSScriptRoot 'teslapc\target\debug\teslapc.exe'
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Publish) {
    $outDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $PSScriptRoot $Output }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Copy-Item $built (Join-Path $outDir 'teslapc.exe') -Force
    Write-Host "Published $built to $outDir"
}

exit 0
