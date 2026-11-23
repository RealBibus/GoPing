<#
.SYNOPSIS
Builds a GoPing Messenger release publish and optionally compiles the Inno Setup installer.

.DESCRIPTION
1. Runs dotnet publish for GoPing.Messenger (self-contained, win-x64).
2. Compiles installer/GoPing.iss with ISCC.exe if Inno Setup 6 is installed.

Prerequisites:
- .NET SDK 10 (or matching SDK for net10.0-windows target)
- Inno Setup 6 to compile the .iss (optional; publish still runs without it)
  https://jrsoftware.org/isdl.php

Usage:
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -Configuration Release
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -SkipInstaller
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [bool]$SelfContained = $true,

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$project = Join-Path $repoRoot 'GoPing.Messenger\GoPing.Messenger.csproj'
$publishDir = Join-Path $scriptDir 'publish\win-x64'
$outputDir = Join-Path $scriptDir 'output'
$issPath = Join-Path $scriptDir 'GoPing.iss'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

Write-Host "==> Publishing GoPing Messenger" -ForegroundColor Cyan
Write-Host "    Project: $project"
Write-Host "    Config:  $Configuration"
Write-Host "    Runtime: $Runtime"
Write-Host "    Self-contained: $SelfContained"
Write-Host "    Output:  $publishDir"

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$sc = if ($SelfContained) { 'true' } else { 'false' }

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained $sc `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:IncludeNativeLibrariesForSelfExtract=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "==> Publish complete: $publishDir" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "==> Skipping installer compilation (-SkipInstaller)." -ForegroundColor Yellow
    exit 0
}

$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)

$iscc = $null
$cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
if (-not $iscc) {
    foreach ($candidate in $isccCandidates) {
        if (Test-Path $candidate) { $iscc = $candidate; break }
    }
}

if (-not $iscc) {
    Write-Warning "Inno Setup 6 (ISCC.exe) was not found."
    Write-Warning "Publish output is ready at: $publishDir"
    Write-Warning "Install Inno Setup 6 from https://jrsoftware.org/isdl.php, then run:"
    Write-Warning "  ISCC.exe `"$issPath`""
    exit 0
}

Write-Host "==> Compiling installer with $iscc" -ForegroundColor Cyan
& $iscc $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE"
}

Write-Host "==> Installer ready in: $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir -Filter *.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
