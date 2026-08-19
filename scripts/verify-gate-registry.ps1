# 门禁 gate-registry：门禁注册一致性（防门禁静默失联）
# 规则:
#   1. 每个 scripts/verify-*.ps1 必须登记进 run-gates.ps1 的 $gates 注册表（漏登记 = 门禁永不运行 = 假绿）
#   2. 注册表中每个 Script 必须真实存在
#   3. 每个已登记门禁必须有变红物证 docs/guard-redproof/<gate-id>-redproof.md
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot
$fails = @()
$verifyScripts = @(Get-ChildItem (Join-Path $root 'scripts') -Filter 'verify-*.ps1' -File | Select-Object -ExpandProperty Name)
$runner = Get-Content (Join-Path $root 'scripts\run-gates.ps1') -Raw
$registered = @([regex]::Matches($runner, "'(verify-[a-zA-Z0-9-]+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
foreach ($s in $verifyScripts) {
    if ($s -notin $registered) { $fails += "$s — 未登记进 run-gates.ps1 注册表（新增门禁必须同一变更登记）" }
}
foreach ($s in $registered) {
    if (-not (Test-Path (Join-Path $root "scripts\$s"))) { $fails += "$s — 已登记但脚本文件不存在" }
    $gateId = ($s -replace '\.ps1$', '') -replace '^verify-', ''
    if (-not (Test-Path (Join-Path $root "docs\guard-redproof\$gateId-redproof.md"))) { $fails += "$gateId — 缺少变红物证 docs/guard-redproof/$gateId-redproof.md" }
}
if ($fails.Count -gt 0) { Write-GateFail 'gate-registry' $fails }
Write-GatePass 'gate-registry' "verify 脚本 $($verifyScripts.Count) 个全部登记，物证齐全"
