# build-shellmenu.ps1 - build the BetterDesktopShellMenu native shell extension (C++).
#
# Output: packages/shell/shell-context-menu/native/BetterDesktopShellMenu.dll
#         (deployed next to the sources, same convention as shell-taskbar/native/ExplorerTAP.dll,
#          so the plugin csproj can ship it via <None Include="native\..." CopyToOutputDirectory>).
#
# Also builds and runs ShellMenuSmoke.exe (pure-function assertions for config parse + predicates).
#
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI; non-ASCII breaks parsing).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build-shellmenu.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build-shellmenu.ps1 -SkipSmoke

[CmdletBinding()]
param(
    [switch]$SkipSmoke,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$nativeDir = Join-Path $repoRoot 'packages\shell\shell-context-menu\native'
$buildDir = Join-Path $nativeDir 'build'
$binDir = Join-Path $buildDir "bin\$Configuration"
$deployTarget = Join-Path $nativeDir 'BetterDesktopShellMenu.dll'

if (-not (Test-Path $nativeDir)) {
    throw "native project dir not found: $nativeDir"
}

Write-Host "[shellmenu] configuring CMake ..." -ForegroundColor Cyan
cmake -S $nativeDir -B $buildDir -A x64 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed (exit $LASTEXITCODE)" }

Write-Host "[shellmenu] building $Configuration ..." -ForegroundColor Cyan
cmake --build $buildDir --config $Configuration --parallel | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake build failed (exit $LASTEXITCODE)" }

$product = Join-Path $binDir 'BetterDesktopShellMenu.dll'
if (-not (Test-Path $product)) { throw "build product missing: $product" }

# Deploy. If explorer already loaded the previous DLL the copy fails - that is expected
# (explorer/dllhost keeps it locked). Report clearly instead of failing the build.
try {
    Copy-Item $product $deployTarget -Force
    Write-Host "[shellmenu] deployed: $deployTarget" -ForegroundColor Green
}
catch {
    Write-Host "[shellmenu][WARN] could not overwrite $deployTarget (likely loaded by explorer/dllhost)." -ForegroundColor Yellow
    Write-Host "[shellmenu][WARN] close/restart explorer and re-run, or copy manually." -ForegroundColor Yellow
}

# Deploy the out-of-proc broker next to the DLL (same convention). The plugin csproj ships both via
# <None Include="native\..." CopyToOutputDirectory>; a missing broker is NOT a build failure --
# the host falls back to the in-proc path (and says so in the log).
$brokerProduct = Join-Path $binDir 'BetterDesktopMenuBroker.exe'
$brokerTarget = Join-Path $nativeDir 'BetterDesktopMenuBroker.exe'
if (Test-Path $brokerProduct) {
    try {
        Copy-Item $brokerProduct $brokerTarget -Force
        Write-Host "[shellmenu] deployed: $brokerTarget" -ForegroundColor Green
    }
    catch {
        Write-Host "[shellmenu][WARN] could not overwrite $brokerTarget (a running host may hold it)." -ForegroundColor Yellow
    }
}
else {
    Write-Host "[shellmenu][WARN] broker product missing: $brokerProduct" -ForegroundColor Yellow
}

if (-not $SkipSmoke) {
    $smoke = Join-Path $binDir 'ShellMenuSmoke.exe'
    if (Test-Path $smoke) {
        Write-Host "[shellmenu] running smoke tests ..." -ForegroundColor Cyan
        & $smoke
        if ($LASTEXITCODE -ne 0) { throw "ShellMenuSmoke reported failures (exit $LASTEXITCODE)" }
    }
    else {
        Write-Host "[shellmenu][WARN] smoke exe missing: $smoke" -ForegroundColor Yellow
    }

    # Broker self-test: stdio JSON protocol + graceful skip for unknown CLSIDs (no third-party needed).
    $broker = Join-Path $binDir 'BetterDesktopMenuBroker.exe'
    if (Test-Path $broker) {
        Write-Host "[shellmenu] running broker self-test ..." -ForegroundColor Cyan
        & $broker --self-test
        if ($LASTEXITCODE -ne 0) { throw "BetterDesktopMenuBroker self-test reported failures (exit $LASTEXITCODE)" }
    }
    else {
        Write-Host "[shellmenu][WARN] broker exe missing: $broker" -ForegroundColor Yellow
    }
}

Write-Host "[shellmenu] done." -ForegroundColor Cyan
