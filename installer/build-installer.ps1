# -----------------------------------------------------------------------------
#  WSL USB Manager - release build: single-file exe + Inno Setup installer
#  Copyright (c) 2026 KabukiFlux
#  Licensed under the MIT License. See LICENSE in the project root.
# -----------------------------------------------------------------------------
#  Usage: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.2.3]
#  Output: dist\WslUsbManager-<version>-setup-x64.exe and dist\WslUsbManager-<version>-portable-x64.zip
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'WslUsbManager\WslUsbManager.csproj'

if (-not $Version) { $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$Version = $Version.TrimStart('v')
Write-Host "Building WSL USB Manager $Version" -ForegroundColor Cyan

$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish $csproj -c Release -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install --exact --id JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'WslUsbManager.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }

$zip = Join-Path $dist "WslUsbManager-$Version-portable-x64.zip"
Compress-Archive -Force -Path (Join-Path $publish 'WslUsbManager.exe'), (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') -DestinationPath $zip

Get-ChildItem $dist -Filter "WslUsbManager-$Version-*" | ForEach-Object { Write-Host "  $($_.FullName)  ($([math]::Round($_.Length / 1MB, 1)) MB)" -ForegroundColor Green }
