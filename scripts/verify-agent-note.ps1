# 门禁 agent-note：决策记录的结构与格式
# 规则（详见 .agents/notes/README.md）:
#   - 路径 {lifecycle}/{class}/yyyy-mm-dd-topic.md；生命周期与类别为封闭集合
#   - 禁止 INDEX.md（整个 notes 树）
#   - 文件头: 第1行 "# Agent Note: <标题>"，第2行空，第3行 "Status: <生命周期>"，第4行空
#   - 各生命周期必需小节; implemented 禁止残留 proposed 期小节
# 单测: verify-agent-note.Tests.ps1（Pester，覆盖文件内容合规判断）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：检查单个决策记录内容，返回违规数组（空数组 = 合规）
function Get-AgentNoteContentViolations([string[]]$Lines, [string]$Lifecycle) {
    $required = @{
        'proposed'    = @('## Problem', '## Proposal', '## Alternatives considered', '## Acceptance criteria', '## Risks')
        'implemented' = @('## Problem', '## Decision', '## Alternatives considered', '## Consequences')
        'rejected'    = @('## Problem', '## Proposal', '## Alternatives considered')
    }
    $forbidden = @{ 'implemented' = @('## Proposal', '## Plan') }
    $fails = @()
    if ($Lines.Count -lt 4) { return @('至少 4 行（标题/空行/Status/空行）') }
    if ($Lines[0] -notmatch '^# Agent Note: .+') { $fails += '首行须为 ``# Agent Note: <标题>``' }
    if ($Lines[1] -ne '') { $fails += '第二行须为空行' }
    if ($Lines[2] -notmatch '^Status: (proposed|implemented|rejected)( — .*)?$') {
        $fails += '第三行须为 ``Status: <proposed|implemented|rejected>``'
    }
    elseif ($Lines[2] -notmatch "^Status: $Lifecycle(\s| —|$)") {
        $fails += "Status 必须与其目录生命周期一致（$Lifecycle）"
    }
    if ($Lines[3] -ne '') { $fails += '第四行须为空行' }
    $body = ($Lines | Select-Object -Skip 4) -join "`n"
    foreach ($sec in $required[$Lifecycle]) {
        if ($body -notmatch [regex]::Escape($sec)) { $fails += "缺少必需小节 ``$sec``" }
    }
    if ($forbidden.ContainsKey($Lifecycle)) {
        foreach ($sec in $forbidden[$Lifecycle]) {
            if ($body -match [regex]::Escape($sec)) { $fails += "implemented 记录不得残留提议期小节 ``$sec``" }
        }
    }
    return @($fails)
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $notes = Join-Path $root '.agents\notes'
    $classes = @('architecture', 'feature', 'bug-fix', 'process', 'testing', 'simplification')
    $lifecycles = @('proposed', 'implemented', 'rejected')

    $fails = @()
    foreach ($lc in $lifecycles) {
        foreach ($cls in $classes) {
            if (-not (Test-Path (Join-Path $notes "$lc\$cls"))) { $fails += ".agents/notes/$lc/$cls — 缺少类别目录" }
        }
    }
    $idx = @(Get-ChildItem $notes -Recurse -Filter 'INDEX.md' -File -ErrorAction SilentlyContinue)
    foreach ($i in $idx) { $fails += "$(Get-RelPath $i.FullName) — 决策记录树禁止 INDEX.md（以目录树浏览与仓库搜索替代）" }

    foreach ($lc in $lifecycles) {
        foreach ($cls in $classes) {
            $dir = Join-Path $notes "$lc\$cls"
            foreach ($f in Get-ChildItem $dir -Filter '*.md' -File -ErrorAction SilentlyContinue) {
                $rel = Get-RelPath $f.FullName
                if ($f.Name -notmatch '^\d{4}-\d{2}-\d{2}-.+\.md$') {
                    $fails += "$rel — 文件名须为 yyyy-mm-dd-主题.md"
                    continue
                }
                $lines = @(Get-Content $f.FullName)
                foreach ($v in (Get-AgentNoteContentViolations $lines $lc)) {
                    $fails += "$rel — $v"
                }
            }
        }
    }

    if ($fails.Count -gt 0) { Write-GateFail 'agent-note' $fails }
    $count = @(Get-ChildItem $notes -Recurse -Filter '*.md' -File | Where-Object { $_.FullName -notmatch '\\archived\\' }).Count
    Write-GatePass 'agent-note' "活跃决策记录 $count 篇，格式全部合规"
}