# 门禁 doc-budgets：关键文档字数预算
# 计数口径: 每个 CJK 汉字计 1 词 + 每个拉丁字母/数字连续串计 1 词（避免中文被低估）
# 规则: 清单 scripts/manifests/doc-budgets.manifest.json 中列出的文件必须存在且不超过上限;
#       超限先精简; 确实要放宽上限，必须在决策记录中说明理由（见 AGENTS.md）
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot
$manifestPath = Join-Path $root 'scripts\manifests\doc-budgets.manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1) { Write-GateFail 'doc-budgets' @("$manifestPath — schema 必须为 1") }
$budgets = $manifest.'doc-budgets'

$fails = @()
foreach ($rel in @($budgets.PSObject.Properties.Name)) {
    $path = Join-Path $root $rel
    if (-not (Test-Path $path -PathType Leaf)) {
        $fails += "$rel — 预算清单中的文件不存在"
        continue
    }
    $text = Get-Content $path -Raw
    $count = 0
    $count += ([regex]::Matches($text, '[\u4e00-\u9fff]')).Count
    $count += ([regex]::Matches($text, '[A-Za-z0-9]+')).Count
    $ceiling = $budgets.$rel
    if ($count -gt $ceiling) {
        $fails += "$rel — $count 词超过 $ceiling 词上限 — 请精简，或按 AGENTS.md 在决策记录中说明理由后调整上限"
    }
}
if ($fails.Count -gt 0) { Write-GateFail 'doc-budgets' $fails }
Write-GatePass 'doc-budgets' "预算清单 $(@($budgets.PSObject.Properties.Name).Count) 个文档全部在限内"
