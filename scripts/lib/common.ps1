# Better Desktop Cordis — 门禁公共库
# 每个 verify-*.ps1 都以本文件为唯一入口确定仓库根与输出格式。

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-RepoRoot {
    return $script:RepoRoot
}

# 输出契约（见 scripts/AGENTS.md）：
#   [PASS] <gate-id> — 摘要       （通过）
#   [FAIL] <gate-id> — <位置> <原因> （失败，逐条输出后 exit 1）
function Write-GatePass([string]$GateId, [string]$Summary) {
    Write-Output "[PASS] $GateId — $Summary"
}

function Write-GateFail([string]$GateId, [string[]]$Reasons) {
    foreach ($r in $Reasons) { Write-Output "[FAIL] $GateId — $r" }
    exit 1
}

function Get-RelPath([string]$FullPath) {
    $root = $script:RepoRoot
    if ($FullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($root.Length + 1)
    }
    return $FullPath
}
