# 门禁 test-coverage：覆盖棘轮（最低行覆盖率，只能升不能降）
# 流程: dotnet test --collect:"Code Coverage;Format=cobertura" → 解析最新 cobertura → 逐程序集比对基线
# 基线: scripts/manifests/coverage-ratchet.baseline.json（LOCKED-COUNT 与条目数逐值相等）
# 单测: verify-test-coverage.Tests.ps1（Pester，覆盖覆盖率阈值判断）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 判断逻辑（可单测）：实际行覆盖率不得低于基线
function Test-CoverageRate([double]$ActualRate, [double]$MinRate) {
    return $ActualRate -ge $MinRate
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $sln = Join-Path $root 'BetterDesktop.slnx'
    $baselinePath = Join-Path $root 'scripts\manifests\coverage-ratchet.baseline.json'
    $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json

    $null = & dotnet test $sln --no-build --collect:"Code Coverage;Format=cobertura" -v minimal 2>&1
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) {
        Write-GateFail 'test-coverage' @("dotnet test --collect 失败（退出码 $testExit），覆盖率不可信")
    }

    $xmlFiles = @(Get-ChildItem (Join-Path $root 'packages') -Recurse -Filter '*.cobertura.xml' -File -ErrorAction SilentlyContinue)
    if ($xmlFiles.Count -eq 0) {
        Write-GateFail 'test-coverage' @('未找到 coverage.cobertura.xml')
    }
    $latest = $xmlFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    [xml]$doc = Get-Content $latest.FullName
    $packages = @($doc.coverage.packages.package)
    $actual = @{}
    foreach ($pkg in $packages) {
        $rate = [double]::Parse([string]$pkg.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
        $actual[[string]$pkg.name] = $rate
    }

    $fails = @()
    $entries = @($baseline.baselines.PSObject.Properties)
    if ($baseline.'LOCKED-COUNT' -ne $entries.Count) {
        $fails += "coverage-ratchet.baseline.json — LOCKED-COUNT $($baseline.'LOCKED-COUNT') 与条目数 $($entries.Count) 不一致"
    }
    foreach ($entry in $entries) {
        if (-not $actual.ContainsKey($entry.Name)) {
            $fails += "$($entry.Name) — 覆盖率数据中缺少该程序集"
            continue
        }
        $actualRate = $actual[$entry.Name]
        $min = [double]$entry.Value
        if (-not (Test-CoverageRate $actualRate $min)) {
            $fails += ("{0} — 行覆盖率 {1:P2} 低于基线 {2:P2}（棘轮只升不降）" -f $entry.Name, $actualRate, $min)
        }
    }
    if ($fails.Count -gt 0) { Write-GateFail 'test-coverage' $fails }
    $summary = ($entries | ForEach-Object { "{0}={1:P0}" -f $_.Name, $actual[$_.Name] }) -join ' '
    Write-GatePass 'test-coverage' "覆盖棘轮全过：$summary"
}