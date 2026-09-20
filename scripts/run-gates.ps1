# Better Desktop Cordis — 统一门禁入口
# 用法:
#   .\scripts\run-gates.ps1              全部门禁
#   .\scripts\run-gates.ps1 -Fast        快速通道：只跑秒级门禁（编辑/保存后的即时反馈）
#   .\scripts\run-gates.ps1 -Filter md   只跑 id 含 "md" 的门禁
#   .\scripts\run-gates.ps1 -List        列出注册表
# 退出码: 0 = 全绿; 1 = 任一门禁失败或依赖失败; 2 = 参数无匹配
#
# 【为什么要有 -Fast（2026-09-20）】门禁最大的成本不是"慢"，是"**慢 + 静默**"：
# 一个几十秒却一行不输出的门禁，你无法区分"在跑"与"卡死"，于是不敢跑、绕开跑 —— 而"被绕开的门禁
# 等于不存在"。所以本轮做了两件事，缺一不可：
#   ① 每个门禁都打"开始 / 阶段 / 完成+总耗时"（见 scripts/lib/common.ps1）；
#   ② 把"秒级"的那批单独抽成本通道，让**每次编辑**都能负担得起一次检查。
# 慢的那几道不是被豁免，是被**放到它该在的层**（提交前 / CI）。

param(
    [string]$Filter = '',
    [switch]$List,
    # 快速通道：只跑标了 Fast = $true 的门禁（秒级）。
    [switch]$Fast
)

# Fast 的判据**只有一条：实测秒级**。标 Fast 前先量，不要凭感觉 ——
# 一个"以为很快其实 30 秒"的门禁混进来，整条快通道就没人用了。
$gates = @(
    [pscustomobject]@{ Id = 'package-readme';    Script = 'verify-package-readme.ps1';    Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'agent-note';        Script = 'verify-agent-note.ps1';        Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'archived-notes';    Script = 'verify-archived-notes.ps1';    Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'md-links';          Script = 'verify-md-links.ps1';          Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'md-wrap';           Script = 'verify-md-wrap.ps1';           Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'doc-budgets';       Script = 'verify-doc-budgets.ps1';       Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'gate-registry';     Script = 'verify-gate-registry.ps1';     Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'architecture-guard'; Script = 'verify-architecture-guard.ps1'; Needs = @(); Fast = $false },
    # 2026-09-20 边界与红线：依赖拓扑（无环 / 单向）+ 电源（不得持有唤醒请求）+ 术语禁词。
    # 它是**面**上的规则（不显形于任何单个文件，只显形于全仓形态），与 architecture-guard 的**点**规则互补。
    # 实测 ~6s，进 Fast 通道（便宜到可以每次编辑都跑）。
    [pscustomobject]@{ Id = 'boundaries';           Script = 'verify-boundaries.ps1';           Needs = @(); Fast = $true },
    [pscustomobject]@{ Id = 'host-log-sink';     Script = 'verify-host-log-sink.ps1';     Needs = @(); Fast = $false },
    [pscustomobject]@{ Id = 'cross-asm-event';   Script = 'verify-no-cross-assembly-event.ps1'; Needs = @(); Fast = $false },
    [pscustomobject]@{ Id = 'native-convergence'; Script = 'verify-native-convergence.ps1';     Needs = @(); Fast = $false },
    [pscustomobject]@{ Id = 'dotnet-format';     Script = 'verify-dotnet-format.ps1';     Needs = @(); Fast = $false },
    # test-coverage：**不进 Fast**（Fast 的定义是实测 <5s，它是 11s），但它**已经换了层**：
    # 2026-09-20 之前它跑整个解决方案（24 个测试工程）、输出还被吞掉，事实上从没在本地跑完过；
    # 收窄到"基线要求的 3 个程序集所对应的 3 个工程"后是 11s ⇒ 它现在**能待在"提交前全量"层**里了，
    # 不需要再当成 CI-only 的欠账。位置是重新评估的结果，不是"改完了"的附带结论。
    [pscustomobject]@{ Id = 'test-coverage';     Script = 'verify-test-coverage.ps1';     Needs = @(); Fast = $false },
    [pscustomobject]@{ Id = 'smoke-test';        Script = 'verify-smoke-test.ps1';        Needs = @(); Fast = $false },
    # 2026-09-17 安装器级：安装/卸载脚本的纯 ASCII、跨进程字面量与必检清单一致性
    [pscustomobject]@{ Id = 'system-integration'; Script = 'verify-system-integration.ps1'; Needs = @(); Fast = $false },
    # 2026-09-19 BDMC1 跨语言协议契约：结构 + 三方常量一致 + 两侧跑同一批共享向量（用户补充的 CI 契约测试）
    [pscustomobject]@{ Id = 'protocol-contract'; Script = 'verify-protocol-contract.ps1'; Needs = @(); Fast = $false },
    # 2026-09-20 C1 五层防线机检：管道有界读 / 参数不拼接 / 反序列化限深 / 原生加载路径 / 明文密钥 /
    # 空 catch / 原生编译加固。实测 ~2s（只扫源码目录），故进 Fast 通道 —— 它便宜到可以每次编辑都跑。
    [pscustomobject]@{ Id = 'security'; Script = 'verify-security.ps1'; Needs = @(); Fast = $true }
)

if ($List) {
    Write-Output "门禁注册表（$($gates.Count) 道）:"
    $gates | ForEach-Object { Write-Output ("  {0,-18} {1,-38} {2}" -f $_.Id, $_.Script, $(if ($_.Fast) { '[Fast]' } else { '' })) }
    Write-Output ''
    Write-Output "Fast 通道：$(($gates | Where-Object { $_.Fast }).Count) 道；全量：$($gates.Count) 道。"
    exit 0
}

$selected = @($gates | Where-Object { $_.Id -like "*$Filter*" -and (-not $Fast -or $_.Fast) })
if ($selected.Count -eq 0) {
    Write-Output "没有匹配 '$Filter' 的门禁。"
    exit 2
}

if ($Fast) {
    # 跳过哪些要**说出来**：不然"快通道绿了"会被误读成"全绿"。
    $skipped = @($gates | Where-Object { -not $_.Fast } | ForEach-Object { $_.Id })
    Write-Output "[-Fast] 跳过（$(($skipped).Count) 道，属提交前 / CI 层）：$($skipped -join ', ')"
    Write-Output ''
}

$results = @{}
$failed = @()
foreach ($g in $selected) {
    $needFail = @($g.Needs | Where-Object { $results[$_] -ne $true })
    if ($needFail.Count -gt 0) {
        Write-Output "[SKIP] $($g.Id) — 依赖门禁未通过: $($needFail -join ', ')"
        $results[$g.Id] = $false
        continue
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $gatePath = Join-Path $PSScriptRoot $g.Script
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $gatePath
    $code = $LASTEXITCODE
    $sw.Stop()
    $ok = ($code -eq 0)
    $results[$g.Id] = $ok
    if ($ok) {
        Write-Output "[PASS] $($g.Id) — exit 0（$($sw.ElapsedMilliseconds) ms）"
    } else {
        Write-Output "[FAIL] $($g.Id) — exit $code（$($sw.ElapsedMilliseconds) ms）"
        $failed += $g.Id
    }
}

Write-Output ''
if ($failed.Count -gt 0) {
    Write-Output "门禁失败: $($failed -join ', ')"
    exit 1
}
Write-Output "全部门禁通过（$($selected.Count) 道）。"
exit 0
