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
    Write-GateStart 'test-coverage'
    $root = Get-RepoRoot
    $sln = Join-Path $root 'BetterDesktop.slnx'
    $baselinePath = Join-Path $root 'scripts\manifests\coverage-ratchet.baseline.json'
    $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json

    # 【为什么不再跑整个解决方案（2026-09-20 实测）】覆盖棘轮只关心 **3 个程序集**
    # （见 coverage-ratchet.baseline.json），而旧实现跑的是 `dotnet test <sln>` ——
    # **24 个测试工程**全跑一遍、逐个收集覆盖率。其中 21 个与基线毫无关系，纯属白跑；
    # 代价是"这道门禁在本机跑不完"（连续 4 次被切到后台）—— 它事实上**已经不在本地生效了**。
    #
    # 这里显式维护"程序集 → 覆盖它的测试工程"映射。两个刻意的约束：
    #   · 映射缺一个 → 下面立刻**报红**（不会静默漏检）；
    #   · 基线将来新增程序集时，也必须在这里补映射。
    # 漏补的后果是"门禁红并告诉你补哪一条"，而不是"悄悄少测一个程序集"。
    $assemblyTestProjects = [ordered]@{
        'BetterDesktop.Kernel'        = 'packages\kernel\kernel-tests\BetterDesktop.Kernel.Tests.csproj'
        'BetterDesktop.Kernel.Loader' = 'packages\kernel\kernel-loader-tests\BetterDesktop.Kernel.Loader.Tests.csproj'
        'BetterDesktop.Kernel.Timer'  = 'packages\kernel\kernel-timer-tests\BetterDesktop.Kernel.Timer.Tests.csproj'
    }

    $targets = @()
    foreach ($entry in @($baseline.baselines.PSObject.Properties)) {
        if (-not $assemblyTestProjects.Contains($entry.Name)) {
            Write-GateFail 'test-coverage' @(
                "$($entry.Name) — 覆盖棘轮要求它，但脚本里没有对应的测试工程映射（请在 assemblyTestProjects 里补一条）"
            )
        }
        $targets += (Join-Path $root $assemblyTestProjects[$entry.Name])
    }

    Write-GateStage "跑 $($targets.Count) 个测试工程（基线只要求 $($targets.Count) 个程序集，而非全部 24 个）"
    $testExit = 0
    foreach ($proj in $targets) {
        & dotnet test $proj --no-build --collect:"Code Coverage;Format=cobertura" -v minimal
        if ($LASTEXITCODE -ne 0) {
            $testExit = $LASTEXITCODE
            break
        }
    }

    if ($testExit -ne 0) {
        Write-GateFail 'test-coverage' @("dotnet test --collect 失败（退出码 $testExit），覆盖率不可信")
    }

    $xmlFiles = @(Get-ChildItem (Join-Path $root 'packages') -Recurse -Filter '*.cobertura.xml' -File -ErrorAction SilentlyContinue)
    if ($xmlFiles.Count -eq 0) {
        Write-GateFail 'test-coverage' @('未找到 coverage.cobertura.xml')
    }
    # 聚合所有测试工程的 cobertura：同一程序集被多个测试工程覆盖时取最高行覆盖率
    # （单一测试工程只覆盖其调用面，取最新单个文件会误判共享内核程序集）
    $actual = @{}
    foreach ($xf in $xmlFiles) {
        try {
            [xml]$doc = Get-Content $xf.FullName
        } catch { continue }
        foreach ($pkg in @($doc.coverage.packages.package)) {
            $rate = [double]::Parse([string]$pkg.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
            $name = [string]$pkg.name
            if (-not $actual.ContainsKey($name) -or $rate -gt $actual[$name]) {
                $actual[$name] = $rate
            }
        }
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