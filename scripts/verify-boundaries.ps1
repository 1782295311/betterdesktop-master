# 门禁 boundaries（边界与红线：依赖拓扑 / 电源 / 术语）
#
# 【为什么要有它】`verify-architecture-guard` 收的是"**谁在拥有某个具体能力**"（生命周期 /
# 热键 / 配置写入 / Core 命名）。它们都是**点**上的规则：某个文件有没有资格调某个 API。
# 而下面这四条是**面**上的规则 —— 它们不会在任何一个文件里显形，只在整个仓库的形态里显形：
#
#   B1 · 依赖无环      —— 环不会让任何一次编译失败，只会让"改一处要动一圈"，且永远拆不开
#   B2 · 依赖单向      —— 低层引高层（contract ← kernel ← surface ← entry）会让分层名存实亡
#   B3 · 电源红线      —— C11：core 不得**持有**唤醒请求；违反后用户侧是"笔记本睡不下去"
#   B4 · 术语禁词      —— TERMINOLOGY.md §三 自称唯一真相源，此前**没有任何门禁读它**
#   B5 · 组件名一致    —— "从 core 出发能到达什么"的唯一真相源是 core/components.json；
#                         C# 侧的 CoreComponents 常量与它双向一致，才能保证可达性不被名字漂移吃掉
#
# 【为什么这四条都能做成硬规则（而不是棘轮）】
# 2026-09-20 实测基线**四条全绿**：67 个 csproj / 226 条边 → 0 环、0 反向依赖；0 处唤醒请求；
# 0 处术语禁词。没有存量欠账 ⇒ 不需要棘轮。棘轮是给"有历史欠账、只许收缩"的规则用的
#（见 verify-architecture-guard 的 R1/R2/R3）；用错了会得到一份"永远不会收缩的清单"。
#
# 【B3 的判据为什么不是"只允许 core 调 SetThreadExecutionState"】
# 真实的红线是"**不得持有唤醒请求**"，而不是"只能在某个目录里调用"。写成目录白名单会
# 两头都错：把 `PowerManagement.AllowSystemSleep()`（只传 ES_CONTINUOUS 的**放行**用法）
# 判成违规，同时放过任何在 core 里持有 `ES_SYSTEM_REQUIRED` 的写法 —— 方向正好反了。
# 故判据取**标志位**：出现 REQUIRED 类标志或 `PowerSetRequest` 才是违规。
#
# 【与 health-check.ps1 的关系】同一批判据，两处用途：
#   health-check 报"现状 + 收敛欠账"（含棘轮清单的 removeBy，不进 CI）；
#   verify-boundaries 收"防回退"（硬规则，进 run-gates）。判据同源，结论口径一致。
#
# 依据: docs/architecture/2026-09-20-current-architecture.md §二（六层 + 单向依赖）/ §core 职责 G；
#       docs/TERMINOLOGY.md §三；docs/plans/2026-09-18-on-demand-core-architecture.md C11
# 单测: verify-boundaries.Tests.ps1（Pester）
# 豁免: bin / obj / target / node_modules / .git / backups / dist / Temp / decomp 一律不扫描
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# ───────────────────────────── 枚举 ─────────────────────────────
#
# 源码根与 verify-security.ps1 同口径（只扫源码目录，不扫全仓 51 万文件）。
$script:SourceRoots = @(
    'core', 'engine', 'engine-index', 'shared', 'native',
    'host', 'packages', 'updater', 'tray', 'launcher', 'recovery',
    'BetterDesktop.Cli', 'BetterDesktop.Cli.Tests', 'launcher-tests',
    'watchdog', 'installer', 'protocols', 'tools'
)
# 【踩过的坑，别再犯】结尾必须是 `\\`（正则里表示"一个反斜杠"）。写成单个 `\` 时
# PowerShell 不报错（单引号串里反斜杠不是转义符），而是在**运行时**抛
# "Invalid pattern … Illegal \ at end of pattern" —— 异常被 Where-Object 吞掉后
# 枚举返回空集，门禁于是**打印 PASS（0 项目 / 0 边 / 0 文件）**。
# 这正是本门禁单测里那条"空集合让所有断言都成立"的活样本。
$script:SkipRegex = '\\(bin|obj|target|node_modules|\.git|\.vs|backups|dist|Temp|poc|engines|decomp)\\'

# 枚举（可单测）：root 下指定扩展名、且不在排除目录内的文件
#
# 【排除必须按"相对 root 的路径"匹配，不能用绝对路径】本门禁的单测在 %TEMP% 下造临时树，
# 而绝对路径里天然含 `...\AppData\Local\Temp\...` —— 用绝对路径匹配 `\Temp\` 会把**整棵临时树**
# 排除掉，于是枚举返回空集、门禁打印 PASS（0 项目 / 0 边）。首版就是这么"绿"的。
# 按相对路径匹配后，排除只针对仓库内部的目录名，与仓库坐在哪无关。
function Get-BoundaryFiles([string]$Root, [string[]]$Extensions) {
    $out = @()
    foreach ($r in $script:SourceRoots) {
        $p = Join-Path $Root $r
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $out += @(Get-ChildItem -LiteralPath $p -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $Extensions -contains $_.Extension } |
            Where-Object {
                $rel = $_.FullName.Substring($Root.Length)
                ('\' + $rel.TrimStart('\', '/') + '\') -notmatch $script:SkipRegex
            })
    }
    return $out
}

# 剥掉行注释（可单测）。行数保持不变 —— 报出的行号必须与真实文件对得上，
# 否则"行号对不上"的报错对使用者等于没有报错（verify-security 的规则 6 记着这条）。
function Remove-LineComments([string]$Text) {
    $lines = $Text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $lines[$i] = ($lines[$i] -split '//', 2)[0]
    }
    return ($lines -join "`n")
}

# ─────────────────── B1/B2：依赖拓扑（可单测） ───────────────────

# csproj → 引用（绝对路径，小写归一）。返回 [hashtable]：key = 绝对路径，value = string[]
function Get-ProjectGraph([string]$Root) {
    $map = @{}
    foreach ($p in (Get-BoundaryFiles $Root @('.csproj'))) {
        $full = [System.IO.Path]::GetFullPath($p.FullName)
        $key = $full.ToLowerInvariant()
        if (-not $map.ContainsKey($key)) { $map[$key] = @() }
        $raw = Get-Content -LiteralPath $full -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($raw)) { continue }
        $refs = @()
        foreach ($m in [regex]::Matches($raw, '<ProjectReference\s+Include\s*=\s*"([^"]+)"')) {
            $inc = $m.Groups[1].Value -replace '/', '\'
            $target = $inc
            if (-not [System.IO.Path]::IsPathRooted($target)) {
                $target = Join-Path (Split-Path -Parent $full) $target
            }
            $refs += ([System.IO.Path]::GetFullPath($target)).ToLowerInvariant()
        }
        # HintPath 指向仓库内的程序集（跨项目硬引用）同样算一条边 —— 它和 ProjectReference
        # 一样会把两个包钉在一起，只是少了编译期检查。漏掉它 = 规则的盲区。
        foreach ($m in [regex]::Matches($raw, '<Reference\s+Include\s*=\s*"[^"]+"[^>]*>\s*<HintPath>([^<]+)</HintPath>')) {
            $hint = ($m.Groups[1].Value -replace '/', '\').Trim()
            if ($hint -match '\.\.') {
                $refs += ([System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $full) $hint))).ToLowerInvariant()
            }
        }
        $map[$key] = @($refs | Select-Object -Unique)
    }
    return $map
}

# 环检测（DFS + 三色）。返回环列表，每条是绝对路径数组（首尾同点），已排序去重。
# 指向仓外的边（NuGet 解出的裸名/HintPath）在 $Graph 里没有节点 → 直接跳过，不构成环。
function Get-ProjectCycles([hashtable]$Graph) {
    $color = @{}
    foreach ($k in $Graph.Keys) { $color[$k] = 0 }   # 0=白 1=灰 2=黑
    $path = New-Object System.Collections.ArrayList
    $cycles = New-Object System.Collections.ArrayList

    $dfs = {
        param($node)
        $color[$node] = 1
        [void]$path.Add($node)
        foreach ($next in @($Graph[$node])) {
            if (-not $color.ContainsKey($next)) { continue }
            if ($color[$next] -eq 1) {
                $idx = $path.IndexOf($next)
                if ($idx -ge 0) {
                    $chain = @($path[$idx..($path.Count - 1)]) + @($next)
                    [void]$cycles.Add(($chain | Sort-Object) -join ' -> ')
                }
            }
            elseif ($color[$next] -eq 0) { & $dfs $next }
        }
        [void]$path.RemoveAt($path.Count - 1)
        $color[$node] = 2
    }
    foreach ($k in @($Graph.Keys)) { if ($color[$k] -eq 0) { & $dfs $k } }
    return @($cycles | Select-Object -Unique)
}

# 层判定（按仓库相对路径首段，可单测）。
# 【为什么用"首段"而不是"看依赖关系"】层是**人的约定**，必须从路径读出来；若由依赖图反推，
# 规则就退化成"当前图一定合法"（自证），失去拦住新增越界的能力。
function Get-ProjectLayer([string]$RelPath) {
    $n = $RelPath -replace '\\', '/'
    if ($n -match '^packages/api/') { return 'contract' }
    if ($n -match '^protocols/') { return 'contract' }
    if ($n -match '^shared/') { return 'contract' }
    if ($n -match '^packages/kernel/') { return 'kernel' }
    if ($n -match '^packages/shell/') { return 'surface' }
    if ($n -match '^(core|engine|engine-index|native)/') { return 'engine' }
    if ($n -match '^(packages/entry|installer)/') { return 'entry' }
    return 'unclassified'
}

$script:LayerRank = @{ 'contract' = 0; 'engine' = 1; 'kernel' = 2; 'surface' = 3; 'entry' = 4 }

# 反向依赖（可单测）：只允许"高层 → 低层"。返回描述字符串数组（已排序）。
# unclassified 与 tools/ 不参与方向判定（工具可以依赖任何层；未分类说明分类表需要补，但不该由本规则判红）。
function Get-DirectionViolations([hashtable]$Graph, [string]$Root) {
    $bad = @()
    foreach ($k in $Graph.Keys) {
        $fromRel = $k.Substring($Root.Length + 1)
        $from = Get-ProjectLayer $fromRel
        if (-not $script:LayerRank.ContainsKey($from)) { continue }
        foreach ($t in @($Graph[$k])) {
            if (-not $t.EndsWith('.csproj')) { continue }
            if (-not $t.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $toRel = $t.Substring($Root.Length + 1)
            $to = Get-ProjectLayer $toRel
            if (-not $script:LayerRank.ContainsKey($to)) { continue }
            if ($script:LayerRank[$to] -gt $script:LayerRank[$from]) {
                $bad += "[$from → $to] $fromRel ⇒ $toRel"
            }
        }
    }
    return @($bad | Sort-Object -Unique)
}

# ─────────────────── B3：电源红线（可单测） ───────────────────

# 声明 vs 使用（可单测）：`ES_SYSTEM_REQUIRED = 0x1,` 是**标志的定义**，不是"持有唤醒请求"。
# 【为什么必须分开】首版不区分，于是 `PowerManagement.cs` 的 `[Flags] enum EXECUTION_STATE`
# 三个成员声明被报成 3 处红线 —— 而那是**枚举类型定义本身**，删掉它连"放行睡眠"都写不出来。
# 另一类同形误报是 P/Invoke 声明（`private static extern ... SetThreadExecutionState(...)`），
# 它是"这个函数存在"，不是"调用了它"。
function Test-IsFlagDeclaration([string]$Line) {
    if ($Line -match '\bextern\b') { return $true }
    if ($Line -match '^\s*(?:[A-Za-z_][\w\.]*(?:<[^>]*>)?(?:\[\])?\s+)?(ES_[A-Z_]+|PowerRequest\w+)\s*=') { return $true }
    return $false
}

# 检测逻辑（可单测）：返回违规描述数组。
# 违规 = 出现 REQUIRED 类标志（维持系统/显示/离开模式唤醒）或 PowerSetRequest 调用。
# 放行 = 只传 ES_CONTINUOUS（MSDN 的标准"清除唤醒请求、放行睡眠"复位用法）。
function Test-PowerRedline([string]$Content) {
    $bad = @()
    $code = Remove-LineComments $Content
    $lines = $code -split "`n"
    $required = 'ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED|ES_AWAYMODE_REQUIRED|PowerRequestSystemRequired|PowerRequestDisplayRequired|PowerRequestAwayModeRequired'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if (Test-IsFlagDeclaration $l) { continue }
        if ($l -match $required) {
            $bad += "第 $($i + 1) 行：持有唤醒请求（$($l.Trim())）—— 除用户显式触发的长任务（须带超时）外不得持有"
        }
        elseif ($l -match '\bPowerSetRequest\s*\(') {
            $bad += "第 $($i + 1) 行：PowerSetRequest 持有唤醒请求（$($l.Trim())）"
        }
    }
    return $bad
}

# ─────────────────── B4：术语禁词（可单测） ───────────────────
#
# 【判据必须收窄到"声明语境"】直接 grep `Cairo` 会命中大量**行尾溯源注释**
#（如"// cairoshell DesktopIcons.setPosition 同值"）—— 那是正当的出处说明，不是命名漂移。
# 故：① 先剥行注释；② 只匹配会进入程序集元数据的位置（namespace / 类型声明 / using）。
$script:TerminologyPatterns = @(
    @{ Pat = '\bnamespace\s+[A-Za-z0-9_.]*Cairo'; Why = '旧仓命名空间（ADR-001 D1 明确切割）' },
    @{ Pat = '\b(class|interface|struct|enum|record)\s+\w*Cairo\w*'; Why = '旧仓类型名' },
    @{ Pat = '\busing\s+[A-Za-z0-9_.]*Cairo[A-Za-z0-9_.]*\s*;'; Why = '旧仓 using' },
    @{ Pat = '\bIExtensionService\b|\bICairoPlugin\b'; Why = '旧仓外挂式扩展宿主模型（已否决）' },
    @{ Pat = '\b(class|interface)\s+\w*ExtensionService\b'; Why = '旧仓外挂式扩展模型' }
)

# 检测逻辑（可单测）：返回违规描述数组。
function Test-TerminologyViolation([string]$RelPath, [string]$Content) {
    $bad = @()
    # 反编译器产物/历史参考副本不是"我们的命名空间"，豁免（与 verify-security 对 decomp 的口径一致）
    if ($RelPath -match '(?i)(decomp|原生重写|评估)') { return @() }
    $code = Remove-LineComments $Content
    $lines = $code -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($t in $script:TerminologyPatterns) {
            if ($lines[$i] -match $t.Pat) {
                $bad += "第 $($i + 1) 行：术语禁词「$($t.Pat)」—— $($t.Why)（TERMINOLOGY.md §三）"
                # 一行只报一条：模式之间有重叠（`interface IExtensionService` 同时命中
                # "旧仓类型名"与"旧仓扩展模型"），重复输出会让同一行出现两次，
                # 而修复动作只有一个。报一次就够。
                break
            }
        }
    }
    return $bad
}

# ─────────────────── B5：组件名双向一致（可达性，可单测） ───────────────────
#
# 【为什么这条规则存在】"从 core 出发，可以达到任何功能"这件事的**唯一真相源**是
# `core/components.json` —— core 只认识表里的名字。而壳侧要请求 core 启动某个组件时，
# 必须写这个名字（`CoreComponents` 常量）。两份名字一旦漂移，症状分别是：
#   · C# 里有、表里没有 → 运行时 `unknown-component`，用户看到"点了没反应"；
#   · 表里有、C# 里没有 → 那个组件**没有任何壳侧入口能到达**（存在但摸不到）。
# 两者都不会让编译失败，所以必须在门禁里单向对账。

# 从 C# 源码里抽组件名常量（可单测）：只取**字符串常量赋值**形态。
function Get-DeclaredComponentNames([string]$Source) {
    if ([string]::IsNullOrEmpty($Source)) { return @() }
    $code = Remove-LineComments $Source
    return @([regex]::Matches($code, 'public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

# 双向对账（可单测）：返回违规描述数组。
function Get-ComponentNameMismatch([string[]]$TableNames, [string[]]$CodeNames) {
    $bad = @()
    foreach ($n in $CodeNames) {
        if ($TableNames -notcontains $n) {
            $bad += "CoreComponents 声明了 '$n'，但 core/components.json 里没有这个 name —— 运行时表现为 unknown-component（点了没反应）"
        }
    }
    foreach ($n in $TableNames) {
        if ($CodeNames -notcontains $n) {
            $bad += "core/components.json 有 '$n'，但 CoreComponents 里没有对应常量 —— 该组件**没有壳侧入口能到达**（存在但摸不到）"
        }
    }
    return @($bad | Sort-Object)
}

# ───────────────────────────── 门禁主体 ─────────────────────────────
# （dot-source 时跳过，供单测仅加载函数）

if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'boundaries'
    $root = Get-RepoRoot
    $fails = @()

    # ── B1/B2：依赖拓扑 ──
    Write-GateStage '枚举 csproj 并建图'
    $graph = Get-ProjectGraph $root
    $edgeCount = @($graph.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
    Write-GateStage "图已建（$($graph.Count) 节点 / $edgeCount 边）"
    # 【空集合自检】"0 项目 / 0 边"读起来像"全绿"，实际含义是**枚举坏了**（正则写歪、根路径错）。
    # 判据是"全仓至少有 N 个项目"而不是"有没有违规"—— 因为它防的正是**静默通过**这一类失效。
    if ($graph.Count -lt 10) {
        Write-GateFail 'boundaries' @("枚举到的 csproj 只有 $($graph.Count) 个（全仓应有数十个）—— 枚举逻辑本身失效，本次判定无效")
    }

    foreach ($c in (Get-ProjectCycles $graph)) { $fails += "B1 依赖环：$c" }
    foreach ($v in (Get-DirectionViolations $graph $root)) {
        $fails += "B2 反向依赖：$v —— 依赖只允许「高层 → 低层」（contract < engine < kernel < surface < entry）"
    }
    Write-GateStage 'B1/B2 完成（环 + 方向）'

    # ── B3/B4：逐文件扫红线 ──
    $files = @(Get-BoundaryFiles $root @('.cs', '.rs', '.cpp', '.h'))
    Write-GateStage "扫描 $($files.Count) 个源文件"
    $powerCount = 0
    $termCount = 0
    foreach ($f in $files) {
        $rel = Get-RelPath $f.FullName
        $content = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($content)) { continue }

        if ($f.Extension -in @('.cs', '.rs', '.cpp', '.h')) {
            foreach ($r in (Test-PowerRedline $content)) {
                $powerCount++
                $fails += "B3 电源红线：$rel — $r"
            }
        }
        if ($f.Extension -in @('.cs', '.rs')) {
            foreach ($r in (Test-TerminologyViolation $rel $content)) {
                $termCount++
                $fails += "B4 术语禁词：$rel — $r"
            }
        }
    }
    Write-GateStage "B3/B4 完成（电源 $powerCount 处、术语 $termCount 处）"

    # ── B5：组件名双向一致（可达性）──
    $tablePath = Join-Path $root 'core\components.json'
    $constPath = Join-Path $root 'packages\kernel\kernel\Core\CoreComponents.cs'
    if (-not (Test-Path -LiteralPath $tablePath)) {
        $fails += "B5 可达性：core/components.json 缺失 —— 组件表是 core 唯一的业务数据，缺它则一切名字对账都无从谈起"
    }
    elseif (-not (Test-Path -LiteralPath $constPath)) {
        $fails += "B5 可达性：CoreComponents.cs 缺失 —— 壳侧将没有组件名的唯一定义处，名字会重新散回各包"
    }
    else {
        # 解析失败必须精确报"表坏了"：否则下面的"表 0 个 / C# 9 个"会指向错误方向
        #（同 verify-architecture-guard 那次 29 条假红：真正的原因是 JSON 里一个直引号）。
        try {
            $tableJson = Get-Content -LiteralPath $tablePath -Raw | ConvertFrom-Json
        }
        catch {
            Write-GateFail 'boundaries' @("B5 可达性：core/components.json 无法解析（JSON 语法错误）：$($_.Exception.Message)。**本次判定无效**，请先修组件表。")
        }
        $tableNames = @($tableJson.components | ForEach-Object { $_.name } | Sort-Object -Unique)
        $codeNames = @(Get-DeclaredComponentNames (Get-Content -LiteralPath $constPath -Raw))
        # 空集合自检：两侧任一为空都说明**解析坏了**，而不是"对账通过"
        if ($tableNames.Count -lt 5 -or $codeNames.Count -lt 5) {
            $fails += "B5 可达性：解析到组件名 表=$($tableNames.Count) / C#=$($codeNames.Count) —— 解析逻辑失效，本次对账无效"
        }
        foreach ($m in (Get-ComponentNameMismatch $tableNames $codeNames)) { $fails += "B5 可达性：$m" }
        Write-GateStage "B5 完成（表 $($tableNames.Count) 个 / C# $($codeNames.Count) 个）"
    }

    if ($fails.Count -gt 0) { Write-GateFail 'boundaries' $fails }
    Write-GatePass 'boundaries' "依赖无环且单向（$($graph.Count) 项目 / $edgeCount 边）；无唤醒请求持有；无术语禁词；组件名表 ↔ C# 双向一致"
}
