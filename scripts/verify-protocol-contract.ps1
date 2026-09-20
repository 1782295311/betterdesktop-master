# 门禁 protocol-contract：BDMC1 跨语言协议契约
#
# 规则:
#   1. protocols/bdmc1-test-vectors.json 结构合法（schema=1、用例数 >= 10、kind/reason/generate 取值在白名单内、用例名唯一）
#   2. 三方声明的 maxMessageBytes 一致：共享向量 == Rust 常量 == C# 常量
#      （**不编译即可查**：直接读两侧源码里的常量 —— 这条最容易在改协议时漏掉，且漏了就是隐蔽的截断 bug）
#   3. 两侧实现都跑同一批共享向量且全绿：`cargo test protocol` + `dotnet test --filter Bdmc1ProtocolContractTests`
#      —— 这是"每次改都验"，不是"开发时验一次"
#
# 依据: 计划 docs/plans/2026-09-18-on-demand-core-architecture.md §7 S2 + 用户 2026-09-19 补充（共享向量 + CI 契约测试）
# 单测: verify-protocol-contract.Tests.ps1（Pester）
# 豁免: 无（向量文件是契约，必须存在且可解析）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 合法的 kind / reason / generate 取值（与向量文件自身的说明一致）
$script:AllowedKinds = @('legacy', 'control', 'invalid')
$script:AllowedReasons = @('empty', 'too-large', 'invalid-utf8', 'bad-magic', 'empty-head')
$script:AllowedGenerate = @('oversize', 'invalid-utf8')

# 结构校验（可单测）：返回问题列表（空数组 = 通过）
function Test-VectorShape([object]$Vectors) {
    $problems = @()
    if ($Vectors.schema -ne 1) { $problems += "schema 必须为 1（当前 $($Vectors.schema)）" }
    if ($null -eq $Vectors.maxMessageBytes -or $Vectors.maxMessageBytes -le 0) {
        $problems += "maxMessageBytes 必须为正整数"
    }
    $cases = @($Vectors.cases)
    if ($cases.Count -lt 10) { $problems += "用例数 $($cases.Count) 少于 10 条（边界覆盖不足）" }

    $seen = @{}
    foreach ($c in $cases) {
        if ([string]::IsNullOrWhiteSpace($c.name)) { $problems += "存在无名用例"; continue }
        if ($seen.ContainsKey($c.name)) { $problems += "用例名重复：$($c.name)" }
        $seen[$c.name] = $true

        if ($c.expect.kind -notin $script:AllowedKinds) {
            $problems += "$($c.name)：expect.kind '$($c.expect.kind)' 不在白名单"
            continue
        }

        # 【存在性而非值】JSON 里 "field": "" 与字段缺失语义不同（arg 允许空串，但 head/reason 不允许）。
        # 另注：PowerShell 把 [string]$null 强制成 ''，所以判存在**必须**查属性名，不能查 $null。
        $expectProps = @($c.expect.PSObject.Properties.Name)
        $hasHead = $expectProps -contains 'head'
        $hasArg = $expectProps -contains 'arg'
        $hasReason = $expectProps -contains 'reason'

        # input / generate 二选一。
        # ⚠️ 两者的"存在"判据**故意不同**：`input` 允许为空串（`"input": ""` 就是"空输入"这条边界用例），
        #    故按**字段存在性**判；`generate` 必须指向已知生成器，故空/空白视同缺失。
        $caseProps = @($c.PSObject.Properties.Name)
        $hasInput = $caseProps -contains 'input'
        $hasGen = -not [string]::IsNullOrWhiteSpace($c.generate)
        if ($hasInput -eq $hasGen) {
            $problems += "$($c.name)：input 与 generate 必须二选一（input 可为空串；generate 不可为空白）"
        }
        if ($hasGen -and $c.generate -notin $script:AllowedGenerate) {
            $problems += "$($c.name)：generate '$($c.generate)' 不在白名单"
        }

        if ($c.expect.kind -eq 'invalid') {
            if (-not $hasReason -or [string]::IsNullOrWhiteSpace($c.expect.reason)) {
                $problems += "$($c.name)：kind=invalid 必须给非空 reason"
            } elseif ($c.expect.reason -notin $script:AllowedReasons) {
                $problems += "$($c.name)：reason '$($c.expect.reason)' 不在白名单"
            }
        } else {
            if (-not $hasHead -or [string]::IsNullOrWhiteSpace($c.expect.head)) {
                $problems += "$($c.name)：kind=$($c.expect.kind) 必须给非空 head"
            }
            if (-not $hasArg) {
                $problems += "$($c.name)：kind=$($c.expect.kind) 必须给 arg 字段（可为空串，但字段必须存在）"
            }
            if ($hasReason -and -not [string]::IsNullOrWhiteSpace($c.expect.reason)) {
                $problems += "$($c.name)：非 invalid 用例不应给 reason"
            }
        }
    }
    return @($problems)
}

# 从源码文本抽取声明的上限（可单测）：$Language = rust | csharp
function Get-DeclaredMaxBytes([string]$Text, [string]$Language) {
    $pattern = if ($Language -eq 'rust') {
        'MAX_MESSAGE_BYTES\s*:\s*usize\s*=\s*([0-9_]+)'
    } else {
        'MaxMessageBytes\s*=\s*([0-9_]+)'
    }
    $m = [regex]::Match($Text, $pattern)
    if (-not $m.Success) { return $null }
    return [int]($m.Groups[1].Value -replace '_', '')
}

# ───────────────────────────── 门禁主体 ─────────────────────────────
if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'protocol-contract'
    $root = Get-RepoRoot
    $fails = @()

    # 1. 向量结构
    $vectorsPath = Join-Path $root 'protocols\bdmc1-test-vectors.json'
    if (-not (Test-Path $vectorsPath -PathType Leaf)) {
        Write-GateFail 'protocol-contract' @("$vectorsPath — 共享向量缺失（协议契约的唯一权威，不得删除）")
    }
    $vectors = Get-Content $vectorsPath -Raw | ConvertFrom-Json
    $shapeProblems = @(Test-VectorShape $vectors)
    if ($shapeProblems.Count -gt 0) {
        $fails += @($shapeProblems | ForEach-Object { "protocols/bdmc1-test-vectors.json — $_" })
    }

    # 2. 三方 maxMessageBytes 一致（不编译即可查）
    $rustPath = Join-Path $root 'core\src\protocol.rs'
    $csPath = Join-Path $root 'packages\kernel\kernel\MenuCommandPipeCodec.cs'
    $declared = @()
    foreach ($pair in @(@($rustPath, 'rust', 'core/src/protocol.rs'), @($csPath, 'csharp', 'packages/kernel/kernel/MenuCommandPipeCodec.cs'))) {
        $path, $lang, $rel = $pair
        if (-not (Test-Path $path -PathType Leaf)) {
            $fails += "$rel — 实现文件缺失"
            continue
        }
        $declaredValue = Get-DeclaredMaxBytes (Get-Content $path -Raw) $lang
        if ($null -eq $declaredValue) {
            $fails += "$rel — 未找到 maxMessageBytes 常量声明（改名后请同步本门禁）"
            continue
        }
        if ($declaredValue -ne $vectors.maxMessageBytes) {
            $fails += "$rel — 声明 $declaredValue 与共享向量 $($vectors.maxMessageBytes) 不一致"
        }
        $declared += $declaredValue
    }

    if ($fails.Count -gt 0) { Write-GateFail 'protocol-contract' $fails }

    # 3. 两侧实现跑同一批共享向量
    $env:Path = "$env:Path;$env:USERPROFILE\.cargo\bin"

    Push-Location (Join-Path $root 'core')
    try {
        $rustOut = & cargo test --release protocol 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $tail = ($rustOut -split "`n" | Select-Object -Last 12) -join ' | '
            $fails += "core（Rust）共享向量测试未通过（exit $LASTEXITCODE）：$tail"
        }
    } finally { Pop-Location }

    $csProject = Join-Path $root 'packages\kernel\kernel-tests\BetterDesktop.Kernel.Tests.csproj'
    $csOut = & dotnet test $csProject -c Debug --nologo --filter 'FullyQualifiedName~Bdmc1ProtocolContractTests' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $tail = ($csOut -split "`n" | Where-Object { $_ -match '失败|Failed|Assert' } | Select-Object -Last 8) -join ' | '
        $fails += "kernel-tests（C#）共享向量测试未通过（exit $LASTEXITCODE）：$tail"
    }

    if ($fails.Count -gt 0) { Write-GateFail 'protocol-contract' $fails }
    Write-GatePass 'protocol-contract' "共享向量 $(@($vectors.cases).Count) 条；三方 maxMessageBytes=$($vectors.maxMessageBytes) 一致；Rust 与 C# 两侧向量测试均绿"
}
