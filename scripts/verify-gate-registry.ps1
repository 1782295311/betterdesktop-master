# 门禁 gate-registry：门禁注册一致性（防门禁静默失联）
# 规则:
#   1. 每个 scripts/verify-*.ps1 必须登记进 run-gates.ps1 的 $gates 注册表（漏登记 = 门禁永不运行 = 假绿）
#   2. 注册表中每个 Script 必须真实存在
#   3. 每个已登记门禁必须有变红物证 docs/guard-redproof/<gate-id>-redproof.md
#   4. 物证必须完整：含 ①②③ 三要素小节，且含完成结论「红/绿双向验证通过」。
#      （用正向完成标记而非扫描「待填写」：红跑原样输出里可能合法地出现该词，反向扫描会误伤）
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
    $proof = Join-Path $root "docs\guard-redproof\$gateId-redproof.md"
    if (-not (Test-Path $proof)) { $fails += "$gateId — 缺少变红物证 docs/guard-redproof/$gateId-redproof.md" }
    else {
        $c = Get-Content $proof -Raw
        if ($c -notmatch '## ①' -or $c -notmatch '## ②' -or $c -notmatch '## ③') { $fails += "$gateId — 物证缺少 ①②③ 三要素小节" }
        if ($c -notmatch '红/绿双向验证通过') { $fails += "$gateId — 物证缺少完成结论「红/绿双向验证通过」（未完成的物证视为无物证）" }
    }
}
if ($fails.Count -gt 0) { Write-GateFail 'gate-registry' $fails }
Write-GatePass 'gate-registry' "verify 脚本 $($verifyScripts.Count) 个全部登记，物证齐全"
