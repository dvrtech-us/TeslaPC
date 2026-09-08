<#
.SYNOPSIS
    Builds TeslaPC (TeslaPCInterface.sln).

.DESCRIPTION
    Wraps dotnet build/publish with the repo's paths so a build is one command from the root.

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER Publish
    Publish a runnable framework-dependent folder instead of just building.

.PARAMETER Output
    Publish output directory (default: .\publish). Only used with -Publish.

.EXAMPLE
    .\build.ps1                     # Release build
    .\build.ps1 -Configuration Debug
    .\build.ps1 -Publish            # runnable folder in .\publish
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Publish,

    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'TeslaPCInterface\TeslaPCInterface.sln'
$project = Join-Path $PSScriptRoot 'TeslaPCInterface\TeslaPCInterface.csproj'

if ($Publish) {
    $outDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $PSScriptRoot $Output }
    Write-Host "Publishing $Configuration build to $outDir ..."
    dotnet publish $project -c $Configuration -o $outDir --self-contained false
}
else {
    Write-Host "Building $Configuration ..."
    dotnet build $solution -c $Configuration
}

exit $LASTEXITCODE
