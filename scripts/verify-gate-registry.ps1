# 门禁 gate-registry：门禁注册一致性 + 门禁单测强制（防门禁静默失联 / 无证明）
# 规则:
#   1. 每个 scripts/verify-*.ps1 必须登记进 run-gates.ps1 的 $gates 注册表（漏登记 = 门禁永不运行 = 假绿）
#   2. 注册表中每个 Script 必须真实存在
#   3. 每个已登记门禁必须有门禁单测 scripts/verify-<gate>.Tests.ps1（Pester，证明「非法输入 → 拒绝」）
#   单测: verify-gate-registry.Tests.ps1（覆盖单测路径推导）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 路径推导（可单测）：verify-x.ps1 → verify-x.Tests.ps1
function Get-GateTestPath([string]$ScriptName) {
    return $ScriptName -replace '\.ps1$', '.Tests.ps1'
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $fails = @()
    $verifyScripts = @(Get-ChildItem (Join-Path $root 'scripts') -Filter 'verify-*.ps1' -File | Where-Object { $_.Name -notmatch '\.Tests\.ps1$' } | Select-Object -ExpandProperty Name)
    $runner = Get-Content (Join-Path $root 'scripts\run-gates.ps1') -Raw
    $registered = @([regex]::Matches($runner, "'(verify-[a-zA-Z0-9-]+\.ps1)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)

    foreach ($s in $verifyScripts) {
        if ($s -notin $registered) { $fails += "$s — 未登记进 run-gates.ps1 注册表（新增门禁必须同一变更登记）" }
    }
    foreach ($s in $registered) {
        if (-not (Test-Path (Join-Path $root "scripts\$s"))) { $fails += "$s — 已登记但脚本文件不存在" }
        $gateId = ($s -replace '\.ps1$', '') -replace '^verify-', ''
        $test = Join-Path $root "scripts\$(Get-GateTestPath $s)"
        if (-not (Test-Path $test)) { $fails += "$gateId — 缺少门禁单测 scripts/$(Get-GateTestPath $s)" }
    }

    if ($fails.Count -gt 0) { Write-GateFail 'gate-registry' $fails }
    Write-GatePass 'gate-registry' "verify 脚本 $($verifyScripts.Count) 个全部登记，单测齐全"
}