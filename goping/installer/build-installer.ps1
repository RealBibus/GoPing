<#
.SYNOPSIS
Publishes GoPing Messenger and compiles the branded native Inno Setup installer.

.DESCRIPTION
1. Runs dotnet publish for GoPing.Messenger (self-contained, win-x64).
2. Compiles installer/GoPing.iss with ISCC.exe into installer\output:
   GoPing-Setup-v{version}-win-x64.exe

The result is a native Windows installer with custom branded wizard images,
per-user install, desktop/Start menu shortcuts and a normal uninstall entry.
It is roughly half the size of the old single-file WPF installer because the
published files are LZMA2-compressed by Inno Setup instead of being embedded
twice (zip inside a self-contained .NET host).

Prerequisites:
- .NET SDK 10 (net10.0-windows target)
- Inno Setup 6: https://jrsoftware.org/isdl.php

Usage:
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -Configuration Debug
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -SkipInstaller  # publish only
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
$issFile = Join-Path $scriptDir 'GoPing.iss'
$publishDir = Join-Path $scriptDir 'publish\win-x64'
$outputDir = Join-Path $scriptDir 'output'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

[xml]$csprojXml = Get-Content $project
$version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.2' }

Write-Host "==> Publishing GoPing Messenger v$version" -ForegroundColor Cyan
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

$publishMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "==> App publish complete: $publishDir ($publishMb MB)" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "==> Skipping installer build (-SkipInstaller)." -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $issFile)) {
    throw "Inno Setup script not found: $issFile"
}

# Inno Setup is installed per-user by winget on this machine; check common
# locations before giving up.
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $iscc = $found.Source }
}
if (-not $iscc) {
    throw "Inno Setup 6 compiler (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php"
}

$finalName = "GoPing-Setup-v$version-win-x64.exe"
$finalPath = Join-Path $outputDir $finalName
if (Test-Path $finalPath) {
    Remove-Item -Force $finalPath
}

Write-Host "==> Compiling branded installer with Inno Setup 6" -ForegroundColor Cyan
Write-Host "    Compiler: $iscc"
Write-Host "    Script:   $issFile"
& $iscc "/dAppVersion=$version" $issFile

if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $finalPath)) {
    throw "Installer was not produced: $finalPath"
}

$sizeMb = [math]::Round((Get-Item $finalPath).Length / 1MB, 1)
Write-Host ""
Write-Host "==> Installer ready:" -ForegroundColor Green
Write-Host "    $finalPath ($sizeMb MB)"
Write-Host "    Native Inno Setup wizard with custom GoPing branding."
Write-Host "    Per-user install, no admin rights required."
