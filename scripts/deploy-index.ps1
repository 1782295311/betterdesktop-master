# 部署索引引擎（Rust）到 %LOCALAPPDATA%\BetterDesktop（宿主/客户端约定的定位路径）。
#
# 为什么必须部署到这里：IndexEngineLauncher.LocateEngineExe 的候选顺序是
# 「环境变量 BetterDesktopIndexEnginePath → AppContext.BaseDirectory → %LOCALAPPDATA%\BetterDesktop」，
# 只有落到约定路径，宿主的 shell-app-source 才能自动找到并拉起引擎。
#
# 用法：
#   pwsh -File scripts/deploy-index.ps1                 # cargo release 构建 + 部署（引擎在跑时先停再拉起）
#   pwsh -File scripts/deploy-index.ps1 -SkipEngineBuild # 复用已有产物，只部署
#
# 模板 = scripts/deploy-clipboard.ps1（同一套"停 → 拷 → 按需重启"纪律）。

param(
    [switch]$SkipEngineBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dst = Join-Path $env:LOCALAPPDATA "BetterDesktop"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# ---- 1) 构建（cargo 不在 PATH：用绝对路径，见计划 §12 Q2） ----
if (-not $SkipEngineBuild) {
    $cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
    if (-not (Test-Path $cargo)) {
        Write-Warning "[index] 未找到 cargo（$cargo），回退使用已有产物"
    }
    else {
        Push-Location (Join-Path $root "engine-index")
        try {
            & $cargo build --release
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "[index] cargo build --release 失败（退出码 $LASTEXITCODE），回退使用已有产物"
            }
        }
        catch {
            Write-Warning "[index] 构建异常：$($_.Exception.Message)；回退使用已有产物"
        }
        finally {
            Pop-Location
        }
    }
}

# ---- 2) 部署 exe ----
$engineExe = Join-Path $root "engine-index\target\release\betterdesktop-index-engine.exe"
$engineDst = Join-Path $dst "BetterDesktop.Index.Engine.exe"

if (-not (Test-Path $engineExe)) {
    Write-Warning "[index] 未找到引擎产物：$engineExe（先执行 cargo build --release）"
    exit 1
}

# 运行中的引擎会锁住目标 exe（Copy-Item 会失败）→ 先停，拷完再拉起。
# ⚠️ 只有"本次被我们停掉"才重新拉起：否则会与在跑实例撞单实例、造成双实例竞争。
$wasRunning = @(Get-Process -Name "BetterDesktop.Index.Engine" -ErrorAction SilentlyContinue).Count -gt 0
if ($wasRunning) {
    Get-Process -Name "BetterDesktop.Index.Engine" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

try {
    Copy-Item $engineExe $engineDst -Force
    Write-Host "[index] 引擎已部署 <- $engineExe"
}
catch {
    Write-Warning "[index] 引擎 exe 被占用：$($_.Exception.Message)"
    exit 1
}

if ($wasRunning) {
    try {
        # 与 IndexEngineLauncher 同一纪律：console subsystem exe 必须无窗口启动。
        Start-Process -FilePath $engineDst -WorkingDirectory $dst -WindowStyle Hidden
        Write-Host "[index] 引擎已重新拉起（无窗口）"
    }
    catch {
        Write-Warning "[index] 引擎重新拉起失败：$($_.Exception.Message)；宿主下次探活会兜底拉起。"
    }
}

Write-Host "[index] 部署完成。宿主 shell-app-source 下次启动会经 IPC 查询引擎（不可用时自动回退本地扫描）。"
