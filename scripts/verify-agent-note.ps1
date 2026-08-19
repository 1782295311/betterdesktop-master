# 门禁 agent-note：决策记录的结构与格式
# 规则（详见 .agents/notes/README.md）:
#   - 路径 {lifecycle}/{class}/yyyy-mm-dd-topic.md；生命周期与类别为封闭集合
#   - 禁止 INDEX.md（整个 notes 树）
#   - 文件头: 第1行 "# Agent Note: <标题>"，第2行空，第3行 "Status: <生命周期>"，第4行空
#   - 各生命周期必需小节由 $required 定义；implemented 禁止残留 proposed 期小节
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot
$notes = Join-Path $root '.agents\notes'
$classes = @('architecture', 'feature', 'bug-fix', 'process', 'testing', 'simplification')
$lifecycles = @('proposed', 'implemented', 'rejected')
$required = @{
    'proposed'    = @('## Problem', '## Proposal', '## Alternatives considered', '## Acceptance criteria', '## Risks')
    'implemented' = @('## Problem', '## Decision', '## Alternatives considered', '## Consequences')
    'rejected'    = @('## Problem', '## Proposal', '## Alternatives considered')
}
$forbidden = @{ 'implemented' = @('## Proposal', '## Plan') }

$fails = @()

# 类别目录必须存在（git 不跟踪空目录，缺失即红——与归档门禁口径一致）
foreach ($lc in $lifecycles) {
    foreach ($cls in $classes) {
        if (-not (Test-Path (Join-Path $notes "$lc\$cls"))) {
            $fails += ".agents/notes/$lc/$cls — 缺少类别目录"
        }
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
            $lines = Get-Content $f.FullName
            if ($lines.Count -lt 4) {
                $fails += "$rel — 至少 4 行（标题/空行/Status/空行）"
                continue
            }
            if ($lines[0] -notmatch '^# Agent Note: .+') { $fails += "$rel :1 — 首行须为 ``# Agent Note: <标题>``" }
            if ($lines[1] -ne '') { $fails += "$rel :2 — 第二行须为空行" }
            if ($lines[2] -notmatch '^Status: (proposed|implemented|rejected)( — .*)?$') {
                $fails += "$rel :3 — 第三行须为 ``Status: <proposed|implemented|rejected>``"
            } elseif ($lines[2] -notmatch "^Status: $lc(\s| —|$)") {
                $fails += "$rel :3 — Status 必须与其目录生命周期一致（$lc）"
            }
            if ($lines[3] -ne '') { $fails += "$rel :4 — 第四行须为空行" }
            $body = ($lines | Select-Object -Skip 4) -join "`n"
            foreach ($sec in $required[$lc]) {
                if ($body -notmatch [regex]::Escape($sec)) { $fails += "$rel — 缺少必需小节 ``$sec``" }
            }
            if ($forbidden.ContainsKey($lc)) {
                foreach ($sec in $forbidden[$lc]) {
                    if ($body -match [regex]::Escape($sec)) { $fails += "$rel — implemented 记录不得残留提议期小节 ``$sec``" }
                }
            }
        }
    }
}

if ($fails.Count -gt 0) { Write-GateFail 'agent-note' $fails }
$count = @(Get-ChildItem $notes -Recurse -Filter '*.md' -File | Where-Object { $_.FullName -notmatch '\\archived\\' }).Count
Write-GatePass 'agent-note' "活跃决策记录 $count 篇，格式全部合规"
