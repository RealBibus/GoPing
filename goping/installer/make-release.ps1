<#
.SYNOPSIS
Prepares GoPing release files for upload to GitHub Releases.

.DESCRIPTION
1. Publishes GoPing.Messenger (self-contained, win-x64).
2. Packs the publish output into output\GoPing-win-x64.zip.
3. Writes the sha256 next to it as output\GoPing-win-x64.zip.sha256.

Manual steps after this script:
1. Create a GitHub release with tag v{version from csproj}, for example v1.0.2.
2. Attach GoPing-win-x64.zip and GoPing-win-x64.zip.sha256.
3. Clients discover the update automatically. The repository RealBibus/GoPing is built in.

Usage:
  powershell -ExecutionPolicy Bypass -File .\installer\make-release.ps1
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$project = Join-Path $repoRoot 'GoPing.Messenger\GoPing.Messenger.csproj'
$publishDir = Join-Path $scriptDir 'publish\win-x64'
$outputDir = Join-Path $scriptDir 'output'

# Version from csproj
[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.2' }

Write-Host "==> Publishing GoPing v$version" -ForegroundColor Cyan
& (Join-Path $scriptDir 'build-installer.ps1') -Configuration $Configuration -SkipInstaller
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$zipPath = Join-Path $outputDir 'GoPing-win-x64.zip'
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Write-Host "==> Packing $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$zipPath.sha256"
Set-Content -Path $shaPath -Value "$hash  GoPing-win-x64.zip" -Encoding ascii

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "==> Release files ready:" -ForegroundColor Green
Write-Host "    $zipPath ($sizeMb MB)"
Write-Host "    $shaPath"
Write-Host "    sha256: $hash"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Create a GitHub release with tag v$version"
Write-Host "  2. Attach GoPing-win-x64.zip and GoPing-win-x64.zip.sha256"
Write-Host "  3. In GoPing: Settings -> Updates -> Check for updates -> Install update"
