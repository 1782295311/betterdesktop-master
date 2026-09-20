# 部署剪贴板引擎 + 面板 + 截图（capture）到 %LOCALAPPDATA%\BetterDesktop
#（宿主/引擎/面板三方约定的定位路径）。
#
# 为什么必须部署到这里：宿主 ClipboardPlugin 的 ClipboardEngineLauncher.LocatePanelExe、
# 引擎 engine.rs open_panel、面板 EngineProber.LocateEngineExe 都以「%LOCALAPPDATA%\BetterDesktop」
# 为约定候选之一，三方能互相找到才不会出现「右键回退宿主命令 / 重复拉起抢管道」。
# capture（截图 exe）同目录：常驻注册全局热键的入口面进程，不依赖宿主，用户双击/开机自启即可用。
#
# 用法：
#   pwsh -File scripts/deploy-clipboard.ps1                     # 构建面板(Debug) + cargo release 引擎
#   pwsh -File scripts/deploy-clipboard.ps1 -Configuration Release
#   pwsh -File scripts/deploy-clipboard.ps1 -SkipEngineBuild    # 只部署面板，复用现有引擎产物
#   pwsh -File scripts/deploy-clipboard.ps1 -StopEngine        # 引擎在跑时先停再部署，完成后自动重新拉起

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipEngineBuild,
    [switch]$StopEngine
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dst = Join-Path $env:LOCALAPPDATA "BetterDesktop"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# ---- 1) 面板 exe + 依赖 ----
$tfm = "net8.0-windows10.0.19041.0"
$panelOut = Join-Path $root "packages\shell\shell-clipboard-panel\bin\x64\$Configuration\$tfm"
if (-not (Test-Path $panelOut)) {
    throw "面板输出目录不存在：$panelOut（先执行 dotnet build BetterDesktop.slnx -c $Configuration）"
}

# 运行中的面板会锁住自身 exe/dll，必须先停（面板无状态，用户下次触发入口会自动重建）。
$panelProcs = Get-Process -Name "BetterDesktop.Clipboard.Panel" -ErrorAction SilentlyContinue
if ($panelProcs) {
    $panelProcs | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

Copy-Item "$panelOut\*" $dst -Recurse -Force
Write-Host "[clipboard] 面板已部署 -> $dst\BetterDesktop.Clipboard.Panel.exe"

# ---- 2) 引擎 exe（Rust） ----
if (-not $SkipEngineBuild) {
    Push-Location (Join-Path $root "engine")
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "[clipboard] cargo build --release 失败（退出码 $LASTEXITCODE），回退使用已有产物"
        }
    }
    catch {
        Write-Warning "[clipboard] cargo 不可用或构建异常：$($_.Exception.Message)；回退使用已有产物"
    }
    finally {
        Pop-Location
    }
}

$engineCandidates = @(
    (Join-Path $root "engine\target\release\betterdesktop-clipboard-engine.exe"),
    (Join-Path $root "engine\target\debug\betterdesktop-clipboard-engine.exe")
)
$engineExe = $engineCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$engineDst = Join-Path $dst "BetterDesktop.Clipboard.Engine.exe"

if ($engineExe) {
    # 运行中的引擎会锁住目标 exe（Copy-Item 失败会中断脚本）——按需先停。
    $engineRunning = @(Get-Process -Name "BetterDesktop.Clipboard.Engine" -ErrorAction SilentlyContinue).Count -gt 0
    $restartEngine = $false
    if ($engineRunning -and $StopEngine) {
        Get-Process -Name "BetterDesktop.Clipboard.Engine" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        $engineRunning = $false
        $restartEngine = $true   # 只有"本次被我们停掉"才需要重新拉起
    }

    try {
        Copy-Item $engineExe $engineDst -Force
        Write-Host "[clipboard] 引擎已部署 <- $engineExe"
    }
    catch {
        Write-Warning "[clipboard] 引擎 exe 被占用（引擎正在运行）：$($_.Exception.Message)"
        Write-Warning "            改用 -StopEngine 重新执行即可先停引擎再部署。"
    }

    # ⚠️ 只有"本次被我们停掉"才重新拉起：引擎没停时再拉会与在跑实例撞单实例，
    # 实测造成短暂双实例竞争 → 面板 IPC 连接抖动（query timeout）。
    # 剪贴板监听也不能因部署而静默中断，故停过就必须拉起。
    if ($restartEngine) {
        try {
            # 与 ClipboardEngineLauncher 同一纪律：console subsystem exe 必须无窗口启动。
            Start-Process -FilePath $engineDst -WorkingDirectory $dst -WindowStyle Hidden
            Write-Host "[clipboard] 引擎已重新拉起（无窗口）"
        }
        catch {
            Write-Warning "[clipboard] 引擎重新拉起失败：$($_.Exception.Message)；宿主/面板下次探活会兜底拉起。"
        }
    }
}
else {
    Write-Warning "[clipboard] 未找到引擎产物（尝试过：$($engineCandidates -join ' / ')）"
}

Write-Host "[clipboard] 部署完成。宿主下次启动会据此改向右键命令（无需手动清注册表）。"

# ---- 3) 截图入口面 exe（capture，TFM 22621：WGC 投影） ----
# 独立常驻进程（全局热键 Win+Shift+S + 托盘）；与面板/引擎同目录，便于三方与用户找到。
$tfmCapture = "net8.0-windows10.0.22621.0"
$captureOut = Join-Path $root "packages\shell\shell-capture\bin\$Configuration\$tfmCapture"
$captureExe = Join-Path $captureOut "BetterDesktop.Capture.exe"
if (Test-Path $captureExe) {
    $captureProcs = Get-Process -Name "BetterDesktop.Capture" -ErrorAction SilentlyContinue
    if ($captureProcs) {
        # 截图进程无持久状态（临时文件按会话清理），直接停掉换新版本。
        $captureProcs | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
    Copy-Item "$captureOut\*" $dst -Recurse -Force
    Write-Host "[clipboard] 截图入口面已部署 -> $dst\BetterDesktop.Capture.exe"
}
else {
    Write-Warning "[clipboard] 未找到截图入口面产物：$captureExe（先执行 dotnet build packages\shell\shell-capture）"
}
