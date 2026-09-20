# ============================================================
#  BetterDesktop module-based publish
#
#  Re-organizes a full publish output (scripts/publish.ps1) into per-feature
#  module folders, so a GitHub/binary distribution can ship each feature
#  separately:
#
#    01-主程序        full family (Host/Agent/Tray/Cli/DesktopControl/Settings/
#                     Updater/Watchdog/Recovery + all plugin DLLs + config +
#                     install/uninstall scripts + Rust engines made self-contained)
#    02-截屏          BetterDesktop.Capture.exe (self-contained publish)
#    03-剪贴板面板    BetterDesktop.Clipboard.Panel.exe (self-contained publish)
#    04-剪贴板引擎    BetterDesktop.Clipboard.Engine.exe (Rust)
#    05-搜索索引引擎  BetterDesktop.Index.Engine.exe (Rust)
#    06-格式转换引擎  engines/ (calibre/ffmpeg/libreoffice/tesseract third-party)
#    07-右键菜单扩展  native/BetterDesktopShellMenu.dll
#
#  NOTE: this file is intentionally ASCII-only (Windows PowerShell 5.1 parses
#  .ps1 as ANSI/GBK without a BOM). Chinese README files are written by the
#  caller (the agent) directly, not by this script.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\publish-modules.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\publish-modules.ps1 -Source dist\BetterDesktop-2026.09.17.1354
# ============================================================
[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$OutRoot = 'dist\modules'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$required = @(
    'BetterDesktop.Host.exe',
    'BetterDesktop.Agent.exe',
    'BetterDesktop.Cli.exe',
    'BetterDesktop.DesktopControl.exe',
    'BetterDesktop.Settings.exe',
    'BetterDesktop.Tray.exe',
    'BetterDesktop.Updater.exe',
    'BetterDesktop.Watchdog.exe',
    'BetterDesktop.Recovery.exe',
    'convert-engine.exe',
    'cordis.yml',
    'agent.yml',
    'native\BetterDesktopShellMenu.dll'
)

function Fail([string]$message) {
    Write-Host "MODULES FAILED: $message" -ForegroundColor Red
    exit 1
}

# ---- resolve source: newest COMPLETE dist\BetterDesktop-* ----
$src = $Source
if ([string]::IsNullOrWhiteSpace($src)) {
    $distRoot = Join-Path $root 'dist'
    if (-not (Test-Path $distRoot)) { Fail 'no dist folder; run scripts\publish.ps1 first' }
    $src = Get-ChildItem $distRoot -Directory -Filter 'BetterDesktop-*' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Test-Path $src)) { Fail "source folder not found: $src" }
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $src $file))) { Fail "source incomplete, missing: $file" }
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm')
$out = Join-Path $root "$OutRoot\BetterDesktop-$stamp-modules"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "=== BetterDesktop modules ===" -ForegroundColor Cyan
Write-Host "source: $src"
Write-Host "out   : $out"

# ---- 01-主程序: full family WITHOUT engines/ (kept self-contained by adding Rust engines) ----
$modMain = Join-Path $out '01-主程序'
New-Item -ItemType Directory -Path $modMain -Force | Out-Null
Get-ChildItem -Path $src -Force | Where-Object { $_.Name -ne 'engines' } |
    ForEach-Object { Copy-Item -Path $_.FullName -Destination $modMain -Recurse -Force }
Write-Host "01-主程序: copied family (engines/ excluded)"

# ---- 02-截屏 + 03-剪贴板面板: self-contained publish ----
foreach ($mod in @(
    @{ Name = '02-截屏'; Proj = 'packages\shell\shell-capture\BetterDesktop.Shell.Capture.csproj' },
    @{ Name = '03-剪贴板面板'; Proj = 'packages\shell\shell-clipboard-panel\BetterDesktop.Clipboard.Panel.csproj' }
)) {
    $proj = Join-Path $root $mod.Proj
    if (-not (Test-Path $proj)) { Fail "module project missing: $proj" }
    $modOut = Join-Path $out $mod.Name
    Write-Host "publishing $($mod.Name) ..."
    # 2026-09-18 方案 A：与 publish.ps1 一致改为**自包含**（02/03 也要能在没装 .NET 的机器上直接跑）。
    & dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 -p:SelfContained=true -o $modOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for $($mod.Name) (exit $LASTEXITCODE)" }
    Get-ChildItem -Path $modOut -Recurse -Include *.pdb,*.xml -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

# ---- make 01-主程序 self-contained for the optional executables ----
# The app locates these NEXT TO THE HOST (AppContext.BaseDirectory) or in
# %LOCALAPPDATA%\BetterDesktop; shipping them ONLY in 02/03 means the feature exists
# but its UI entry can never be found. Real regression (2026-09-18): a clean install
# from 01-main had NO clipboard panel -> the side handle / hotkey / tray entry all
# failed and clipboard history "could not be started at all".
# Only the module-own files (exe/dll/deps.json/runtimeconfig.json) are copied: the
# shared assemblies are already in 01-主程序 and must keep their single source.
foreach ($mod in @(
    @{ Dir = '02-截屏'; Prefix = 'BetterDesktop.Capture' },
    @{ Dir = '03-剪贴板面板'; Prefix = 'BetterDesktop.Clipboard.Panel' }
)) {
    $modDir = Join-Path $out $mod.Dir
    if (-not (Test-Path $modDir)) { continue }
    $copied = 0
    Get-ChildItem -Path $modDir -File | Where-Object { $_.Name -like "$($mod.Prefix)*" } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $modMain $_.Name) -Force
        $copied++
    }
    Write-Host "$($mod.Dir): $copied file(s) also copied into 01-主程序 (self-contained)"
}

# ---- 04-剪贴板引擎 / 05-搜索索引引擎: Rust release binaries ----
$enginePairs = @(
    @{ Name = '04-剪贴板引擎'; Src = 'engine\target\release\betterdesktop-clipboard-engine.exe'; Dst = 'BetterDesktop.Clipboard.Engine.exe' },
    @{ Name = '05-搜索索引引擎'; Src = 'engine-index\target\release\betterdesktop-index-engine.exe'; Dst = 'BetterDesktop.Index.Engine.exe' }
)
foreach ($p in $enginePairs) {
    $engineSrc = Join-Path $root $p.Src
    if (-not (Test-Path $engineSrc)) { Fail "rust engine binary missing (run cargo build --release): $engineSrc" }
    $modOut = Join-Path $out $p.Name
    New-Item -ItemType Directory -Path $modOut -Force | Out-Null
    Copy-Item $engineSrc (Join-Path $modOut $p.Dst) -Force
    # also make 01-主程序 self-contained: launcher resolves AppContext.BaseDirectory
    Copy-Item $engineSrc (Join-Path $modMain $p.Dst) -Force
    Write-Host "$($p.Name): $($p.Dst) copied (also into 01-主程序)"
}

# ---- 06-格式转换引擎: third-party engines tree ----
# 【2026-09-18 真机：格式转换 80% 不可用 + 菜单缺一大半选项】
# 所有转换引擎都按 <AppContext.BaseDirectory>\engines\... 定位（shell-convert/Services/ConvertEngine.cs:55
# 与各 Engines\*.cs），而 01-主程序 刻意不含 engines（2.8GB），安装器又只拷 01-主程序\* ——
# 结果"装完没有 engines"：除纯托管图片转换外全部探不到，而 ConvertMenuService 对不可用目标
# 是**整项隐藏**（不是置灰，见其 AddConvertSubmenu）→ 用户看到的就是"选项缺了一大半 + 大部分转换失败"。
# 故本模块改为**自解释布局**：引擎树收进 engines\ 子目录，并随附官方安装脚本，
# 让"脚本与 engines 同目录"这一契约（scripts/install-engines.ps1:40）成立，用户单独运行它也有效。
$enginesSrc = Join-Path $src 'engines'
if (Test-Path $enginesSrc) {
    $modEngines = Join-Path $out '06-格式转换引擎'
    $enginesDst = Join-Path $modEngines 'engines'
    New-Item -ItemType Directory -Path $enginesDst -Force | Out-Null
    Get-ChildItem -Path $enginesSrc -Force | ForEach-Object { Copy-Item -Path $_.FullName -Destination $enginesDst -Recurse -Force }

    $engineInstaller = Join-Path $root 'scripts\install-engines.ps1'
    if (Test-Path $engineInstaller) { Copy-Item $engineInstaller -Destination $modEngines -Force }

    Write-Host "06-格式转换引擎: copied (engines\ + install-engines.ps1)"
}
else {
    Write-Host "06-格式转换引擎: skipped (no engines/ in source)" -ForegroundColor Yellow
}

# ---- 07-右键菜单扩展: native COM DLL ----
$nativeSrc = Join-Path $src 'native\BetterDesktopShellMenu.dll'
if (Test-Path $nativeSrc) {
    $modNative = Join-Path $out '07-右键菜单扩展'
    New-Item -ItemType Directory -Path $modNative -Force | Out-Null
    Copy-Item $nativeSrc (Join-Path $modNative 'BetterDesktopShellMenu.dll') -Force
    Write-Host "07-右键菜单扩展: copied"
}

# ---- summary ----
Write-Host ''
Write-Host '=== MODULES OK ===' -ForegroundColor Green
Get-ChildItem $out -Directory | ForEach-Object {
    $size = [int]((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    $count = (Get-ChildItem $_.FullName -Recurse -File).Count
    Write-Host ("  {0,-16} {1,5} files  {2,5} MB" -f $_.Name, $count, $size)
}
Write-Host "out: $out"
