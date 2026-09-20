# deploy-desktop-service.ps1 -- build & deploy the "desktop service" (BetterDesktop.DesktopControl.exe)
#
# NOTE (2026-09-17): this script is deliberately ASCII-only.
# Windows PowerShell 5.1 reads BOM-less files as ANSI, so Chinese text written by tooling
# turns into mojibake and the parser dies on the first non-ASCII string. Keep it ASCII.
#
# WHY THIS SCRIPT EXISTS
#   The desktop service is a multi-assembly bundle (exe + deps.json + desktop.yml + its own
#   plugin dlls: Shell.Desktop / Shell.Core [toggle catalog] / Shell.Clipboard / Shell.Clipboard.Ipc ...).
#   Deploying only the dll you just edited is a well-known trap: the service then runs a mix of
#   old and new assemblies and behaves like "the menu item exists but the action is unknown".
#   Always deploy the WHOLE output folder.
#
# LOOKUP ORDER (must match shell-core/DesktopControl/DesktopControlLocator.cs)
#   1) same folder as the caller (production, merged layout)
#   2) %LOCALAPPDATA%\BetterDesktop
#   3) %LOCALAPPDATA%\BetterDesktop\DesktopControl   <-- this script's default target
#
# USAGE: powershell -ExecutionPolicy Bypass -File scripts/deploy-desktop-service.ps1 [-SkipBuild] [-Target <dir>]
param(
    [switch]$SkipBuild,
    [string]$Target
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'packages\shell\shell-desktop-control\BetterDesktop.DesktopControl.csproj'
$out  = Join-Path $root 'packages\shell\shell-desktop-control\bin\Debug\net8.0-windows10.0.19041.0'

if (-not $Target) {
    $Target = Join-Path $env:LOCALAPPDATA 'BetterDesktop\DesktopControl'
}

if (-not $SkipBuild) {
    Push-Location $root
    try {
        dotnet build $proj -v m --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "[desktop-service] dotnet build failed (exit $LASTEXITCODE); deploying existing output"
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $out)) {
    Write-Warning "[desktop-service] output folder not found: $out (run dotnet build first)"
    exit 1
}

# Stop the running service first -- its own assemblies are locked, and a half-copied bundle is
# exactly the "the menu item exists but the action is unknown" trap this script prevents.
# Graceful stop first (it restores explorer icons / taskbar), force as fallback.
$deployedExe = Join-Path $Target 'BetterDesktop.DesktopControl.exe'
$wasRunning = $false
if (Get-Process -Name 'BetterDesktop.DesktopControl' -ErrorAction SilentlyContinue) {
    $wasRunning = $true
    if (Test-Path $deployedExe) {
        Write-Output '[desktop-service] graceful stop before deploy...'
        & $deployedExe --stop
        Start-Sleep -Seconds 4
    }
    Stop-Process -Name 'BetterDesktop.DesktopControl' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path (Join-Path $out '*') -Destination $Target -Recurse -Force

$exe = Join-Path $Target 'BetterDesktop.DesktopControl.exe'
$dll = Join-Path $Target 'BetterDesktop.DesktopControl.dll'
$yml = Join-Path $Target 'desktop.yml'
if (-not (Test-Path $exe) -or -not (Test-Path $dll) -or -not (Test-Path $yml)) {
    Write-Warning "[desktop-service] incomplete deployment (need exe + entry dll + desktop.yml): $Target"
    exit 1
}

Write-Output "[desktop-service] deployed to $Target"
Write-Output '[desktop-service] Copied files (key assemblies):'
Get-ChildItem $Target -Filter 'BetterDesktop.*.dll' |
    Sort-Object Name |
    Select-Object -ExpandProperty Name |
    ForEach-Object { Write-Output "  $_" }
Write-Output '[desktop-service] Restart the service (or the main program) to take effect.'

if ($wasRunning) {
    Start-Process -FilePath $exe | Out-Null
    Write-Output '[desktop-service] service restarted (it was running before the deploy).'
}
