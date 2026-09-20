<#
  一键体检 full-audit.ps1 —— 四层检查串成一条命令

  【四层各自回答什么】
    ① 静态体检（health-check.ps1）   谁在拥有什么、哪些是收敛欠账、哪些层根本**没有**门禁
    ② 架构+安全门禁（run-gates.ps1）  这次改动有没有越界（已登记判据的机器校验）
    ③ 跨语言所有权矩阵（文档）        同一件事是不是在两种语言里各写了一份
    ④ 真机探针（probe-runtime.ps1）   机器上真实跑出来的读数是否达标

  【为什么②不拆成"架构门禁 + 安全门禁"两条命令】
    本仓的安全门禁 `verify-security.ps1` 已经**登记在 run-gates.ps1 注册表里**，
    单独调用会绕过 `-Fast` 分层与依赖关系。审计要的是"当前基线"，不是"绕开调度器"。
    故②一律走唯一入口 run-gates.ps1。

  【为什么①③不进门禁】它们判据宽、结论需要人判断（"这算不算重复"），
  做成门禁会长期飘红 → 无人看 → 体检体系先烂。门禁只收**窄而硬**的判据，
  这份职责已经由 scripts/verify-boundaries.ps1 承担（依赖方向/环 + 电源红线 + 术语禁词）。

  用法:
    pwsh -File scripts/full-audit.ps1              # 全部四层（真机段可能较慢）
    pwsh -File scripts/full-audit.ps1 -SkipGates   # 跳过门禁（只想看体检清单时）
    pwsh -File scripts/full-audit.ps1 -SkipRuntime # 跳过真机段（CI / 无桌面会话时）
#>
param(
    [switch]$SkipGates,
    [switch]$SkipRuntime,
    [switch]$FastGates
)

$ErrorActionPreference = 'Continue'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$failed = @()

function Write-Stage([string]$N, [string]$Title) {
    Write-Output ''
    Write-Output ('=' * 78)
    Write-Output "阶段 $N — $Title"
    Write-Output ('=' * 78)
}

# ── 阶段 1：静态体检 ──
Write-Stage 1 '静态体检（所有者普查 / 依赖图 / 电源 / 术语）'
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'health-check.ps1')
if ($LASTEXITCODE -ne 0) { $failed += 'health-check' }

# ── 阶段 2：门禁 ──
if (-not $SkipGates) {
    Write-Stage 2 '门禁（唯一入口 run-gates.ps1）'
    $gateArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'run-gates.ps1'))
    if ($FastGates) { $gateArgs += '-Fast' }
    & pwsh @gateArgs
    if ($LASTEXITCODE -ne 0) { $failed += 'gates' }
}
else { Write-Stage 2 '门禁（-SkipGates，已跳过）' }

# ── 阶段 3：跨语言矩阵 ──
Write-Stage 3 '跨语言所有权矩阵（判定表 + 人工三问）'
$matrix = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\cross-language\所有权矩阵.md'
if (Test-Path -LiteralPath $matrix) {
    Write-Output "  判定表：$matrix"
    Write-Output '  原料：见阶段 1 的 A8 段（概念 × 语言 命中文件数）。'
    Write-Output '  人工核对三问（**缺一不可**）：'
    Write-Output '    ① 有没有功能没有所有者？—— 没人决策 = 行为取决于"谁先跑"，必失稳'
    Write-Output '    ② 有没有功能有两个所有者？—— 重复实现，必须收敛到一个'
    Write-Output '    ③ 有没有功能的"所有者"放错了层？—— 例如"必须活过壳退出"的能力放在壳里'
}
else {
    Write-Output "  ⚠ 未找到 $matrix —— 跨语言重复这一层目前是**空白**。"
}

# ── 阶段 4：真机探针 ──
if (-not $SkipRuntime) {
    Write-Stage 4 '真机探针（D1 进程数 / D2 私有工作集 / D4 电源请求 / 管道 E2E）'
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probe-runtime.ps1')
    if ($LASTEXITCODE -ne 0) { $failed += 'probe-runtime' }
}
else { Write-Stage 4 '真机探针（-SkipRuntime，已跳过）' }

# ── 收尾 ──
Write-Output ''
Write-Output ('=' * 78)
$sw.Stop()
if ($failed.Count -eq 0) {
    Write-Output "体检结束（无硬失败）  总耗时 $([int]$sw.Elapsed.TotalSeconds)s"
    Write-Output '注意："无硬失败"只说明**判据**没红；阶段 1/3 的结论需要人读，不会自动报红。'
    exit 0
}
Write-Output "体检结束，硬失败：$($failed -join ', ')  总耗时 $([int]$sw.Elapsed.TotalSeconds)s"
exit 1
