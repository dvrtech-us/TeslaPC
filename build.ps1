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
