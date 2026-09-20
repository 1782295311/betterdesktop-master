# Better Desktop Cordis — 门禁公共库
# 每个 verify-*.ps1 都以本文件为唯一入口确定仓库根与输出格式。

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-RepoRoot {
    return $script:RepoRoot
}

# ─────────────────────── 进度反馈（2026-09-20） ───────────────────────
#
# 【为什么要它】门禁最大的成本不是"慢"，是"**慢 + 静默**"：一个 146 秒却一行不输出的门禁，
# 你无法区分"在跑"与"卡死" —— 于是不敢跑、绕开跑，门禁就形同不存在。
# 一个 5 分钟但每几秒有输出的门禁是可接受的；一个 2 分钟完全静默的会被绕开。
#
# 三行契约（所有门禁一致，便于肉眼扫、也便于机器抓）：
#   [GATE] <id> 开始 <HH:mm:ss>              ← 由 Write-GateStart 打印
#   [GATE] <id> 阶段 <描述> … <已用>s         ← 可选，脚本按需调 Write-GateStage
#   [GATE] <id> 完成 总耗时 <N>s              ← 由 Write-GatePass / Write-GateFail 自动追加
#
# 【为什么不改进既有的 [PASS]/[FAIL] 行】那两行是对外契约（见下），
# 各 verify-*.Tests.ps1 与 run-gates.ps1 都可能依赖其文本。所以进度一律**另起一行**，
# 只增不改 —— 加反馈不该顺带改契约。
$script:GateId = ''
$script:GateStart = [System.Diagnostics.Stopwatch]::StartNew()

# 门禁开始（每个 verify-*.ps1 在 dot-source 本文件后立刻调用一次）。
function Write-GateStart([string]$GateId) {
    $script:GateId = $GateId
    $script:GateStart.Restart()
    Write-Output "[GATE] $GateId 开始 $((Get-Date).ToString('HH:mm:ss'))"
}

# 阶段打点。
#
# 【用途明确：先测量，后优化】它的存在是为了回答"这 146 秒花在哪一步"，
# 而不是为了好看。看不到分步耗时就去优化，通常会把力气花在不是瓶颈的那一步上。
function Write-GateStage([string]$Description) {
    $elapsed = [int]$script:GateStart.Elapsed.TotalSeconds
    Write-Output "[GATE] $($script:GateId) 阶段 $Description … 已用 ${elapsed}s"
}

# 输出契约（见 scripts/AGENTS.md）：
#   [PASS] <gate-id> — 摘要       （通过）
#   [FAIL] <gate-id> — <位置> <原因> （失败，逐条输出后 exit 1）
function Write-GatePass([string]$GateId, [string]$Summary) {
    Write-Output "[PASS] $GateId — $Summary"
    Write-Output "[GATE] $GateId 完成 总耗时 $([int]$script:GateStart.Elapsed.TotalSeconds)s"
}

function Write-GateFail([string]$GateId, [string[]]$Reasons) {
    foreach ($r in $Reasons) { Write-Output "[FAIL] $GateId — $r" }
    # 失败也要报耗时：否则"它挂在哪里"同样无从判断。
    Write-Output "[GATE] $GateId 失败 总耗时 $([int]$script:GateStart.Elapsed.TotalSeconds)s"
    exit 1
}

function Get-RelPath([string]$FullPath) {
    $root = $script:RepoRoot
    if ($FullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($root.Length + 1)
    }
    return $FullPath
}
