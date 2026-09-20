# deploy-local.ps1 — 把「当前源码」完整部署到一个安装根（本地真机验证用）
#
# 【为什么必须脚本化 —— 2026-09-20 两次真机教训】
#   ① 漏：改了 shell-desktop 的菜单代码、也编译了，但手工拷文件时只拷了 DesktopControl.dll
#      和 Shell.Core.dll，**漏了菜单所在的 Shell.Desktop.dll** ⇒ 用户点开菜单"没有变化"。
#   ② 覆盖：第二次"全量拷"时把 **Debug 版**盖到了 Release 版上（Shell.Core.dll 从 14:32 倒退到 13:34）
#      ⇒ 新旧混杂，连"哪个版本在跑"都无法判断。
#   两者同源：**手工挑文件**。所以本脚本只做一件事 —— 全量、单一配置、可校验。
#
# 【纪律】
#   · 只用 Release 产物；目录里没有 Release 产物就**报错**（不静默退回 Debug —— 那正是②的成因）。
#   · 部署前**清掉安装根里的自研产物**（BetterDesktop*），避免旧文件被新目录"继承"下来。
#     第三方依赖（SkiaSharp / ManagedShell / PdfSharp…）不动：它们不随我们改动。
#   · 结束时**校验**：安装根里每个自研 dll 的写入时间都必须 >= 本次构建开始时间。

[CmdletBinding()]
param(
    # 目标版本目录名（默认按当前时间）。传已存在的名字 = 覆盖部署。
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),

    # 跳过构建（只重新拷贝）。用于"构建已成功但想重拷一次"的场合。
    [switch]$SkipBuild,

    # 构建超时（秒）。首次冷构建可能较久。
    [int]$BuildTimeoutSec = 600
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installRoot = Join-Path $env:LOCALAPPDATA "BetterDesktop\app\$Version"
$logDir = Join-Path $env:LOCALAPPDATA 'BetterDesktop\logs'

function Write-Step([string]$text) { Write-Host "== $text" }

# ─────────────────────────────────────────────────────────────────────
# 要部署的项目清单
#
# 判据：**这个项目的产物会被 core 或 explorer 直接拉起**（= 运行期真实要跑的 exe）。
# 与 core/components.json 的组件表对应；改那张表时同步这里。
# 每一项 = @{ Name=显示名; Project=csproj 相对路径 }；Rust 侧单列。
# ─────────────────────────────────────────────────────────────────────
$dotnetProjects = @(
    @{ Name = 'cli';              Project = 'packages\entry\cli\BetterDesktop.Cli.csproj' },
    @{ Name = 'tray';             Project = 'tray\BetterDesktop.Tray.csproj' },
    @{ Name = 'host';             Project = 'packages\entry\host\BetterDesktop.Host.csproj' },
    @{ Name = 'settings';         Project = 'packages\shell\shell-settings-host\BetterDesktop.Settings.csproj' },
    @{ Name = 'desktop-control';  Project = 'packages\shell\shell-desktop-control\BetterDesktop.DesktopControl.csproj' },
    @{ Name = 'recovery';         Project = 'recovery\BetterDesktop.Recovery.csproj' }
)

# Rust 侧：core（本工作流）。
$rustProjects = @(
    @{ Name = 'core'; Manifest = 'core\Cargo.toml'; Exe = 'betterdesktop-core.exe'; Extra = @('BetterDesktop.ico') }
)

# ─────────────────────────────────────────────────────────────────────
# 1) 构建
# ─────────────────────────────────────────────────────────────────────
$buildStart = Get-Date

if (-not $SkipBuild) {
    Write-Step "构建（Release，$($dotnetProjects.Count) 个 .NET 项目 + $($rustProjects.Count) 个 Rust 项目）"
    foreach ($p in $dotnetProjects) {
        $full = Join-Path $repoRoot $p.Project
        if (-not (Test-Path $full)) {
            # 缺项目 = 部署不完整，必须显式失败（缺一个组件的后果见 §13.22.9 的 desktop）
            throw "项目不存在：$($p.Project)（部署清单需要它；若该项目已删除，请同步本脚本）"
        }
        Write-Host "  构建 $($p.Name) ..."
        & dotnet build $full -c Release --nologo -v quiet 2>&1 | Select-Object -Last 2 | ForEach-Object { "    $_" }
        if ($LASTEXITCODE -ne 0) { throw "构建失败：$($p.Project)（退出码 $LASTEXITCODE）" }
    }

    foreach ($r in $rustProjects) {
        $full = Join-Path $repoRoot $r.Manifest
        if (-not (Test-Path $full)) { throw "Rust 项目不存在：$($r.Manifest)" }
        Write-Host "  构建 $($r.Name) ..."
        & cargo build --release --manifest-path $full 2>&1 | Select-Object -Last 2 | ForEach-Object { "    $_" }
        if ($LASTEXITCODE -ne 0) { throw "构建失败：$($r.Name)（退出码 $LASTEXITCODE）" }
    }
}

# ─────────────────────────────────────────────────────────────────────
# 2) 清掉安装根里的**自研产物**（不碰第三方依赖）
#
# 为什么必须清：「新目录 + 旧文件」会让"我部署了吗"变成不可回答的问题（教训②）。
# 为什么不动第三方：它们不随我们改动，清掉会导致组件起不来（且要重新下引擎）。
# ─────────────────────────────────────────────────────────────────────
Write-Step "准备安装根 $installRoot"
if (-not (Test-Path $installRoot)) { New-Item -ItemType Directory -Path $installRoot -Force | Out-Null }
if (-not (Test-Path (Join-Path $installRoot 'native'))) { New-Item -ItemType Directory -Path (Join-Path $installRoot 'native') -Force | Out-Null }

$removed = @(Get-ChildItem $installRoot -File |
    Where-Object { $_.Name -like 'BetterDesktop*' -or $_.Name -like 'betterdesktop*' })
foreach ($f in $removed) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
Write-Host "  清掉自研产物 $($removed.Count) 个（第三方依赖保留）"

# ─────────────────────────────────────────────────────────────────────
# 3) 全量拷贝（只用 Release 产物；找不到就报错，绝不退回 Debug）
# ─────────────────────────────────────────────────────────────────────
Write-Step "拷贝产物"

function Get-ReleaseOutputDir([string]$projectRel) {
    $projDir = Split-Path -Parent (Join-Path $repoRoot $projectRel)
    $binRelease = Join-Path $projDir 'bin\Release'
    if (-not (Test-Path $binRelease)) { return $null }
    # net8.0-windows10.0.19041.0 之类，可能有多个 TFM：取最新的一个
    $tfm = Get-ChildItem $binRelease -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $tfm) { return $null }
    return $tfm.FullName
}

$copied = 0
$deployed = New-Object System.Collections.Generic.List[object]   # (Source, Target) 供校验用

foreach ($p in $dotnetProjects) {
    $out = Get-ReleaseOutputDir $p.Project
    if ($null -eq $out) {
        throw "$($p.Name)：找不到 Release 产物目录（bin\Release\<tfm>）。请先构建 —— 不退回 Debug（那会让版本来源不可判断）"
    }
    $files = @(Get-ChildItem $out -File | Where-Object {
        # 只拷运行期需要的：自研产物 + 配置文件 + 依赖清单；不拷 pdb / 编译中间物
        $_.Name -like 'BetterDesktop*' -or $_.Name -like '*.json' -or $_.Name -like '*.dll'
    })
    foreach ($f in $files) {
        $dest = Join-Path $installRoot $f.Name
        Copy-Item $f.FullName $dest -Force
        $deployed.Add([pscustomobject]@{ Source = $f.FullName; Target = $dest; From = $p.Name })
    }
    $copied += $files.Count
    Write-Host ("  {0,-16} <- {1}  ({2} 个文件)" -f $p.Name, (Split-Path $out -Parent | Split-Path -Leaf), $files.Count)
}

foreach ($r in $rustProjects) {
    $targetDir = Join-Path (Split-Path -Parent (Join-Path $repoRoot $r.Manifest)) 'target\release'
    $exe = Join-Path $targetDir $r.Exe
    if (-not (Test-Path $exe)) { throw "$($r.Name)：找不到 $($r.Exe)（$targetDir）" }
    $dest = Join-Path $installRoot $r.Exe
    Copy-Item $exe $dest -Force
    $deployed.Add([pscustomobject]@{ Source = $exe; Target = $dest; From = $r.Name })
    $copied++
    foreach ($extra in $r.Extra) {
        $e = Join-Path $targetDir $extra
        if (Test-Path $e) {
            $destExtra = Join-Path $installRoot $extra
            Copy-Item $e $destExtra -Force
            $deployed.Add([pscustomobject]@{ Source = $e; Target = $destExtra; From = $r.Name })
            $copied++
        }
    }
    Write-Host ("  {0,-16} <- target\release" -f $r.Name)
}

# ─────────────────────────────────────────────────────────────────────
# 4) 校验：安装根里每个产物都必须与**源产物内容一致**（比对哈希，不是时间戳）
#
# 【为什么用哈希而不是时间戳 —— 2026-09-20 实测】时间戳判据会被增量构建骗过：
#   · `cargo build` 在源码未变时不重编译 ⇒ exe 时间戳停在旧日期（正常，不是部署失败）；
#   · MSBuild 对**内容未变**的文件不重写 ⇒ `*.deps.json` / `*.runtimeconfig.json` / `.ico`
#     时间戳同样是旧的（正常）。
# 第一版脚本用时间戳，于是报了 6 个"假 stale" —— 判据错会让人去修根本没坏的东西。
# 哈希才是"部署正确"的定义：**目标 == 源**，且**源里有的一个都不能缺**。
# ─────────────────────────────────────────────────────────────────────
Write-Step "校验（哈希比对，$($deployed.Count) 个产物）"
$bad = @()
foreach ($d in $deployed) {
    if (-not (Test-Path $d.Target)) {
        $bad += "缺失: $($d.Target)（来自 $($d.From)）"
        continue
    }
    $hs = (Get-FileHash $d.Source -Algorithm SHA256).Hash
    $ht = (Get-FileHash $d.Target -Algorithm SHA256).Hash
    if ($hs -ne $ht) { $bad += "内容不一致: $(Split-Path $d.Target -Leaf)（来自 $($d.From)）" }
}

if ($bad.Count -gt 0) {
    Write-Host "[FAIL] 部署校验失败：" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" }
    throw "部署校验失败：$($bad.Count) 个产物与源不一致"
}

Write-Host "  $($deployed.Count) 个产物的内容与源产物逐一一致" -ForegroundColor Green
Write-Host ""
Write-Host "安装根：$installRoot"
Write-Host "部署文件数：$copied"
Write-Host ""
Write-Host "下一步：重启 core 使其加载新产物"
Write-Host "  Stop-Process -Name betterdesktop-core,BetterDesktop.DesktopControl -Force -ErrorAction SilentlyContinue"
Write-Host "  Start-Process `"$installRoot\betterdesktop-core.exe`""
