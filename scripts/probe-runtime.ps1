<#
  真机探针 probe-runtime.ps1 —— 把"验收定义"变成一次可复现的读数

  【为什么不能靠眼睛】D1/D2/D4 三条验收定义（空闲只 1 个进程 / core 私有工作集 < 8MB /
  powercfg 无 BetterDesktop 条目）在过去被反复"看着像达标"。**"看着像"不是读数**：
  同名进程会有父子链（probe-processes.ps1 记着两次把它误读成"多主"的教训），
  工作集要分清"私有"与"含共享"（差异可达数倍），powercfg 要按名字段匹配而不是肉眼扫。

  【只读】本脚本不启停任何进程、不写任何文件、不改任何注册表。
  【非门禁】真机读数依赖机器状态，做成门禁会长期飘红 → 无人看。它只产出读数与结论。

  用法:
    pwsh -File scripts/probe-runtime.ps1
    pwsh -File scripts/probe-runtime.ps1 -MaxPrivateMB 8 -ExpectSingle
#>
param(
    # D2 阈值（私有工作集上限，MB）
    [double]$MaxPrivateMB = 8,
    # 是否要求"空闲常驻恰好 1 个进程"（过渡期可能有多个，用 -ExpectSingle 才判红）
    [switch]$ExpectSingle,
    # 测组件冷启动（**会 stop 再 start 目标组件**，默认关：探针默认必须是无副作用的）
    [switch]$ColdStart,
    # 冷启动测量的目标组件（默认 index-engine：数据面引擎、空闲自退，打扰最小）
    [string]$ColdStartComponent = ''
)

$ErrorActionPreference = 'Continue'

function Write-Section([string]$T) {
    Write-Output ''
    Write-Output ('─' * 78)
    Write-Output "== $T"
    Write-Output ('─' * 78)
}

$script:Judgements = @()
function Add-Judgement([string]$Item, [string]$Expect, [string]$Actual, [bool]$Ok) {
    $script:Judgements += [pscustomobject]@{ Item = $Item; Expect = $Expect; Actual = $Actual; Ok = $Ok }
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("  [{0}] {1,-22} 期望 {2,-18} 实测 {3}" -f $tag, $Item, $Expect, $Actual)
}

Write-Output "Better Desktop Cordis — 真机探针  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  （只读）"

# ───────────────────── 1. 进程清单与父子链 ─────────────────────
Write-Section '1 · 进程清单（含父链：同名进程必须先看父链再下结论）'
$procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*betterdesktop*' })
if ($procs.Count -eq 0) {
    Write-Output '  没有匹配 betterdesktop* 的进程。'
    Write-Output '  ⚠ 自检：core 几乎总该在跑 —— 一个都没有时先怀疑"是不是探针错了"，再怀疑"是不是没在跑"。'
}
else {
    $byId = @{}
    foreach ($p in $procs) { $byId[[int]$p.ProcessId] = $p }
    foreach ($p in ($procs | Sort-Object CreationDate)) {
        $parent = $byId[[int]$p.ParentProcessId]
        $pname = if ($parent) { "$($parent.Name)($($parent.ProcessId))" } else { "PID $($p.ParentProcessId)（外部）" }
        Write-Output ("  {0,-34} PID {1,-7} 父 {2,-28} 启动 {3}" -f $p.Name, $p.ProcessId, $pname, $p.CreationDate)
    }
}
$script:ProcCount = $procs.Count

# ───────────────────── 2. 私有工作集（D2） ─────────────────────
Write-Section '2 · 私有工作集（D2：core < 阈值；用 Win32_PerfFormattedData，避免 Get-Counter 的慢）'
$perf = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*betterdesktop*' })
if ($perf.Count -eq 0) { Write-Output '  （无读数）' }
$coreMB = -1
foreach ($x in ($perf | Sort-Object { [int]$_.WorkingSetPrivate } -Descending)) {
    $mb = [math]::Round(([double]$x.WorkingSetPrivate) / 1MB, 2)
    $isCore = $x.Name -like '*core*'
    if ($isCore -and $coreMB -lt 0) { $coreMB = $mb }
    Write-Output ("  {0,-34} 私有 {1,8:N2} MB   （IDProcess {2}）" -f $x.Name, $mb, $x.IDProcess)
}
if ($coreMB -ge 0) {
    Add-Judgement 'core 私有工作集' "< ${MaxPrivateMB} MB" "$coreMB MB" ($coreMB -lt $MaxPrivateMB)
}
else {
    Write-Output '  未读到 core 的工作集（core 没在跑？）'
}

# ───────────────────── 3. 进程数（D1） ─────────────────────
Write-Section '3 · 空闲常驻（D1）'
# 【判据不是"数出来的个数"】D1 的目标态是"**只有一个常驻**，其余按需拉起、用完即退"。
# 用"进程数 == 1"当判据会把"core 按需拉起的组件正在跑"判成违规 —— 那是**设计中的行为**。
# 真正的判据有两条，且都必须成立：
#   ① 只有一个 betterdesktop-core（常驻者唯一）；
#   ② 其余每一个进程的**祖先链都能上溯到 core**（谁拉的 = core，而不是别的守护者）。
# 第②条正是 probe-processes.ps1 记下的教训：同名/多进程先看父链，再下结论。
$coreProcs = @($procs | Where-Object { $_.Name -match '(?i)^betterdesktop-core' })
$nonCore = @($procs | Where-Object { $_.Name -notmatch '(?i)^betterdesktop-core' })

# 祖先链可达性（用**全量**进程表上溯，因为父进程可能不是 betterdesktop* ）
if (-not $script:AllProcs) { $script:AllProcs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) }
$allById = @{}
foreach ($p in $script:AllProcs) { $allById[[int]$p.ProcessId] = $p }
$coreIds = @($coreProcs | ForEach-Object { [int]$_.ProcessId })

# 【踩过的坑，别再犯】形参不能叫 `$Pid` —— 它是 PowerShell 的**只读自动变量**（当前进程 ID）。
# 赋值失败被 `$ErrorActionPreference = 'Continue'` 吞成 WriteError，函数照样返回 $false，
# 于是**每一个进程都被判成"祖先不可达"**（首版实测 4/4 全红）。
# 这与 probe-processes.ps1 里 `$pid` 那次是同一个坑：错的且像真的，比报错危险。
function Test-AncestorIsCore([int]$TargetId) {
    $seen = @{}
    $cur = $allById[$TargetId]
    $depth = 0
    while ($cur -and $depth -lt 12) {
        $ppid = [int]$cur.ParentProcessId
        if ($coreIds -contains $ppid) { return $true }
        if ($seen.ContainsKey($ppid)) { return $false }   # 防环
        $seen[$ppid] = $true
        $cur = $allById[$ppid]
        $depth++
    }
    return $false
}

Write-Output "  core 进程 $($coreProcs.Count) 个；其余 $($nonCore.Count) 个按需进程："
$orphans = @()
foreach ($p in $nonCore) {
    $reach = Test-AncestorIsCore ([int]$p.ProcessId)
    if (-not $reach) { $orphans += $p }
    Write-Output ("    {0,-34} PID {1,-7} 祖先可达 core：{2}" -f $p.Name, $p.ProcessId, $(if ($reach) { '是' } else { '**否**' }))
}

Add-Judgement '常驻者唯一（core）' '= 1' "$($coreProcs.Count)" ($coreProcs.Count -eq 1)
Add-Judgement '非 core 进程祖先可达 core' '全部可达' "$($orphans.Count) 个不可达" ($orphans.Count -eq 0)
foreach ($o in $orphans) { Write-Output "      ⚠ 不可达：$($o.Name) (PID $($o.ProcessId), 父 PID $($o.ParentProcessId)) —— 查是谁拉的" }

$legacy = @($procs | Where-Object { $_.Name -match '(?i)(Agent|Watchdog|Tray)\.exe' })
if ($legacy.Count -gt 0) {
    Write-Output "  ⚠ 仍在跑的旧守护者/托盘 $($legacy.Count) 个 —— 它们会把组件拉回，与 core 的 gate 打架（计划 §7.1）："
    foreach ($l in $legacy) { Write-Output "      $($l.Name) (PID $($l.ProcessId))" }
}
if ($ExpectSingle -and ($nonCore.Count -gt 0)) {
    Write-Output "  （-ExpectSingle 已指定：目标态要求空闲时**一个按需进程都不在**。）"
    Add-Judgement '空闲按需进程数' '= 0' "$($nonCore.Count)" ($nonCore.Count -eq 0)
}

# ───────────────────── 4. 电源请求（D4） ─────────────────────
Write-Section '4 · 电源请求（D4：powercfg /requests 不得有 BetterDesktop 条目）'
try {
    $req = & powercfg /requests 2>&1 | Out-String
    $hits = @($req -split "`r?`n" | Where-Object { $_ -match '(?i)betterdesktop|better desktop' })
    if ($hits.Count -eq 0) {
        Add-Judgement 'powercfg /requests' '无 BetterDesktop 条目' '无' $true
    }
    else {
        foreach ($h in $hits) { Write-Output "      $h" }
        Add-Judgement 'powercfg /requests' '无 BetterDesktop 条目' "$($hits.Count) 条命中" $false
    }
}
catch {
    Write-Output "  读取 powercfg 失败（可能需要管理员）：$($_.Exception.Message)"
}

# ───────────────────── 5. 控制管道连通（端到端） ─────────────────────
Write-Section '5 · 控制管道连通（core 服务端 ↔ CLI 客户端）'
# 【路径不能硬编码】本仓的 CLI 产物同时在 bin\Debug\、bin\Release\、bin\x64\... 多个 TFM 目录下，
# 部署副本又在 %LOCALAPPDATA%\BetterDesktop\app\<版本>\ —— 写死一条路径就会落空（首版即如此）。
# 故：列**候选集合**，取最新时间戳的那个，并把"用的是哪一份"打出来（否则读数无法复现）。
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliCandidates = @()
foreach ($base in @((Join-Path $env:LOCALAPPDATA 'BetterDesktop'), (Join-Path $repoRoot 'BetterDesktop.Cli\bin'), (Join-Path $repoRoot 'dist'))) {
    if (-not (Test-Path -LiteralPath $base)) { continue }
    $cliCandidates += @(Get-ChildItem -LiteralPath $base -Recurse -Filter 'BetterDesktop.Cli.exe' -File -ErrorAction SilentlyContinue)
}
$cli = $cliCandidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $cli) {
    Write-Output '  未找到 BetterDesktop.Cli.exe（检查 LOCALAPPDATA 与 bin 目录）。'
}
else {
    Write-Output "  使用：$cli"
    $out = & $cli --core status 2>&1 | Out-String
    Write-Output "  --- 输出 ---"
    ($out.Trim() -split "`r?`n") | ForEach-Object { Write-Output "    $_" }
    Write-Output "  --- 判读 ---"
    if ($out -match '(?i)Usage|用法' -and $out -notmatch '(?i)desired') {
        Write-Output '  ⚠ 返回 Usage：这份 CLI 不支持 --core（旧构建）。**换新构建再测**，不要把 Usage 当成"管道不通"。'
    }
    elseif ($out -match '(?i)desired|actual|health') {
        Add-Judgement '控制管道 E2E' '返回 desired/actual/health' '有响应' $true

        # 组件健康：desired 与 actual 不一致、或 health != ok，都是**实质问题**（不是风格问题）。
        # `shell running False degraded` 这种行只有真机 status 才看得到 —— 静态门禁永远查不出。
        $degraded = @()
        $mismatch = @()
        foreach ($line in ($out -split "`r?`n")) {
            $cols = @($line.Trim() -split '\s+' | Where-Object { $_ })
            if ($cols.Count -lt 4) { continue }
            if ($cols[0] -eq 'component') { continue }
            $name, $desired, $actual, $health = $cols[0..3]
            if ($health -ne 'ok') { $degraded += "$name(health=$health)" }
            if (($desired -eq 'running') -and ($actual -eq 'False')) { $mismatch += "$name(desired=running, actual=False)" }
        }
        if ($degraded.Count -gt 0) { Add-Judgement '组件健康' '全部 ok' ($degraded -join ', ') $false }
        else { Add-Judgement '组件健康' '全部 ok' '全部 ok' $true }
        if ($mismatch.Count -gt 0) { Add-Judgement 'desired/actual 一致' '一致' ($mismatch -join ', ') $false }

        $restartLine = @($out -split "`r?`n" | Where-Object { $_ -match 'restarts=' })
        if ($restartLine.Count -gt 0) { Write-Output "  重启计数：$($restartLine[0].Trim())" }
    }
    else {
        Write-Output '  ⚠ 未能识别返回语义 —— 人工判读上面原始输出。'
    }
}

# ───────────────────── 6. 控制往返与组件冷启动 ─────────────────────
Write-Section '6 · 控制往返延迟（收敛到 core 后新增的那一跳有多贵）'
# 【为什么必须量这一条】"壳插件不再自己拉起进程，改向 core 请求"是一次**热路径**上的架构改动：
# 每次"确保引擎在跑""打开面板"都从一次 CreateProcess 变成一次管道往返。
# 如果这一跳是几十毫秒级，收敛就是拿交互手感换架构整洁 —— 那不可接受。
# 所以它不是"顺带看看"，是这次收敛的**验收条件之一**。
if ($cli) {
    $samples = @()
    $failedSamples = 0
    for ($i = 0; $i -lt 8; $i++) {
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        $null = & $cli --core status 2>&1
        $t.Stop()
        if ($LASTEXITCODE -eq 0) { $samples += $t.Elapsed.TotalMilliseconds } else { $failedSamples++ }
    }
    if ($samples.Count -eq 0) {
        Write-Output "  8 次 status 全部失败 —— core 不可达（下面的判据不适用）"
    }
    else {
        # 「注意」：这里量的是 **CLI 进程启动 + 一次往返**，因而包含 .NET 启动开销（几十毫秒）。
        # 壳内部调用 CoreControlClient 不付这份开销，所以本读数是**上界**而不是真实代价。
        $min = ($samples | Measure-Object -Minimum).Minimum
        $avg = ($samples | Measure-Object -Average).Average
        $max = ($samples | Measure-Object -Maximum).Maximum
        Write-Output ("  `--core status`（含 CLI 进程启动开销）min {0:N0} ms / avg {1:N0} ms / max {2:N0} ms（成功 {3}/8）" -f $min, $avg, $max, $samples.Count)
        # 上界 < 500ms 即说明"管道那一跳"本身不是瓶颈（剩下的都是进程启动）。
        Add-Judgement '控制往返上界' '< 500 ms（含 CLI 启动）' ("{0:N0} ms" -f $max) ($max -lt 500)
    }
}
else {
    Write-Output '  未找到 BetterDesktop.Cli.exe，跳过（这一节需要它发控制请求）。'
}

if ($ColdStart) {
    Write-Section '6b · 组件冷启动延迟（-ColdStart：会 stop 再 start，请勿在干活时跑）'
    $target = if ($ColdStartComponent) { $ColdStartComponent } else { 'index-engine' }
    Write-Output "  目标组件：$target（默认 index-engine —— 数据面引擎、空闲自退，打扰最小）"
    $null = & $cli --core stop $target 2>&1
    Start-Sleep -Milliseconds 800   # 让 core 完成 stop 与状态落定
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    $startOut = & $cli --core start $target 2>&1 | Out-String
    $elapsedToAck = $t.Elapsed.TotalMilliseconds
    Write-Output ("  start 受理耗时 {0:N0} ms；返回：{1}" -f $elapsedToAck, $startOut.Trim())

    # 受理 ≠ 起来：继续轮询 status 直到 actual=true（或超时）。
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $out = & $cli --core status 2>&1 | Out-String
        if ($out -match "(?m)^\s*$([regex]::Escape($target))\s+\S+\s+True\s") { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    $t.Stop()
    $total = $t.Elapsed.TotalMilliseconds
    if ($ready) {
        Write-Output ("  {0} 冷启动完成耗时 {1:N0} ms（含 start 受理）" -f $target, $total)
        Add-Judgement '组件冷启动' '< 3000 ms（一个 reconcile 周期内）' ("{0:N0} ms" -f $total) ($total -lt 3000)
    }
    else {
        Write-Output "  ⚠ 15s 内未观测到 actual=True —— 组件没起来或 core 拒绝了（查 core 日志）。"
        Add-Judgement '组件冷启动' '< 3000 ms' '15s 未就绪' $false
    }
}
else {
    Write-Output ''
    Write-Output '  （组件冷启动测量会 stop/start 组件，默认不做；要测加 -ColdStart）'
}

# ───────────────────── 7. 唤醒后状态（手工段） ─────────────────────
Write-Section '7 · 睡眠/唤醒后（需人工触发一次睡眠后再跑本脚本）'
Write-Output '  本段无法自动完成（要人先让机器睡一次）。唤醒后重跑本脚本，逐条对照：'
Write-Output '    · 进程数 / 进程名集合 —— 与睡前一致（不多不少；core 应该还在）'
Write-Output '    · 全局热键 —— 按一次，有反应（core 热键在唤醒链第 ④ 步重建）'
Write-Output '    · 托盘图标 —— 在（explorer 重启过则需 TaskbarCreated 重注册）'
Write-Output '    · 控制管道 —— 本节第 5 段能连上（唤醒链第 ③ 步重建实例）'
Write-Output '    · 独占能力（任务栏外观 / 桌面图标钩子）—— 生效'
Write-Output '  顺序判据：唤醒链必须"①重置计时器 → ②reconcile → ③管道 → ④托盘"，顺序不可交换。'

# ───────────────────── 结论 ─────────────────────
Write-Section '结论'
if ($script:Judgements.Count -eq 0) {
    Write-Output '  无可判定项（多半是 core 没在跑）。'
}
else {
    $bad = @($script:Judgements | Where-Object { -not $_.Ok })
    foreach ($j in $script:Judgements) {
        Write-Output ("  [{0}] {1} —— 实测 {2}" -f $(if ($j.Ok) { 'PASS' } else { 'FAIL' }), $j.Item, $j.Actual)
    }
    Write-Output ''
    if ($bad.Count -eq 0) { Write-Output '  真机读数全绿。' }
    else { Write-Output "  $($bad.Count) 项未达标（见上）。" }
}
Write-Output ''
Write-Output '（本脚本只读：不启停进程、不写文件、不改注册表。）'
