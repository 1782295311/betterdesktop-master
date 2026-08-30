# build_native.ps1 - build all C++ native wrapper DLLs and copy into shell-status/natives/
# Usage: powershell -ExecutionPolicy Bypass -File build_native.ps1
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads ps1 as ANSI; non-ASCII breaks parsing).
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$buildDir = Join-Path $root "build"
$binDir = Join-Path $buildDir "bin\Release"
$parent = Split-Path $root -Parent
$nativeRoot = Join-Path $parent "natives"   # .../shell-status/natives

Write-Host "[native] configuring CMake ..." -ForegroundColor Cyan
Push-Location $root
try {
    cmake -S . -B build -A x64 | Out-Host
    Write-Host "[native] building Release ..." -ForegroundColor Cyan
    cmake --build build --config Release --parallel | Out-Host
}
finally {
    Pop-Location
}

if (-not (Test-Path $binDir)) {
    Write-Host "[native][ERROR] output dir not found: $binDir" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $nativeRoot)) {
    New-Item -ItemType Directory -Path $nativeRoot -Force | Out-Null
}

$targets = @("AudioCore.dll", "WlanCore.dll", "PowerCore.dll", "DisplayCore.dll",
    "NetworkCore.dll", "CpuCore.dll", "MemoryCore.dll", "MediaCore.dll")
foreach ($t in $targets) {
    $src = Join-Path $binDir $t
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $nativeRoot $t) -Force
        Write-Host "[native] copied $t" -ForegroundColor Green
    }
    else {
        Write-Host "[native][WARN] missing output $t" -ForegroundColor Yellow
    }
}

Write-Host "[native] done." -ForegroundColor Cyan