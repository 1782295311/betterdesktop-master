<#
  体检脚本 health-check.ps1 —— **一次性所有者普查**（只读 / 只报 / 不阻断，退出码恒 0）

  【它与门禁的分工】
    门禁（verify-*.ps1）回答"**这次改动有没有越界**"，判据窄而硬，且**只覆盖已登记的三条棘轮**。
    本脚本回答"**此刻谁在拥有什么、哪些是收敛欠账、哪些层根本没有门禁**"，判据宽而软。
    所以：**本脚本永远不进 run-gates.ps1**。它的输出是"下一步该收什么"的清单，
    收敛之后由 scripts/verify-boundaries.ps1 接管防回退。

  【为什么不能照抄一份通用模板】
    一份写在别的仓库里的"体检脚本"会四处找 core/ launcher/ extensions/ surface/ 这些目录，
    而本仓的现实是 core/(Rust) + packages/(C# 插件) + host/ + entry 层若干。
    判据抄错 = 全表假红 = 没人看 = 体检本身先烂掉。故本脚本**复用门禁的判据函数本体**
    （dot-source verify-architecture-guard.ps1 只加载函数，其主体被 `InvocationName -ne '.'` 挡住），
    保证"体检报的点"与"门禁判的点"是同一套口径，不会漂移。

  【四层体检里它负责哪层】
    第一层（静态体检）—— A1..A8。
    第三层（跨语言重复）—— A8 给出原料，人工/矩阵补完（docs/cross-language/所有权矩阵.md）。
    第四层（真机）—— 见 scripts/probe-runtime.ps1，由本脚本末尾可选调用。

  用法:
    pwsh -File scripts/health-check.ps1              # 全部静态段
    pwsh -File scripts/health-check.ps1 -WithRuntime # 末尾追加真机探针
    pwsh -File scripts/health-check.ps1 -Only A3,A5  # 只跑指定段
#>
param(
    [switch]$WithRuntime,
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
# 【坑】`pwsh -File x.ps1 -Only A3,A5` 会把 `A3,A5` 当成**一个字符串**传进来（-File 不做参数展开），
# 于是 `-contains 'A3'` 永远为假、脚本静默什么都不跑 —— 故这里自己按逗号拆开。
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot

# 复用门禁判据（只加载函数；门禁主体靠 InvocationName 守卫不会执行）
$guardPath = Join-Path $PSScriptRoot 'verify-architecture-guard.ps1'
if (Test-Path $guardPath) { . $guardPath }

# ───────────────────────────── 枚举与工具 ─────────────────────────────

# 源码根：与 verify-security.ps1 同口径（刻意排除 backups/ 与 native/ 的构建产物）。
$script:SourceRoots = @(
    'core', 'engine', 'engine-index', 'shared', 'native',
    'host', 'packages', 'updater', 'tray', 'launcher', 'recovery',
    'BetterDesktop.Cli', 'BetterDesktop.Cli.Tests', 'launcher-tests',
    'watchdog', 'installer', 'protocols', 'tools'
)
# 【同 verify-boundaries】`engines` 必须锚定到仓库根：不锚定 + `-match` 大小写不敏感，
# 会连带把 `packages/shell/shell-convert/Services/Engines/`（我们自己的源码）也跳过。
$script:SkipRegex = '\\(bin|obj|target|node_modules|\.git|\.vs|backups|dist|Temp|poc|decomp|\.workbuddy)\\|^\\engines\\'

# 一次枚举 + 缓存（同 architecture-guard 的教训：扩展名过滤必须在排除判断**之前**）
$script:FileCache = @{}
function Get-SourceFiles([string[]]$Extensions) {
    $key = $Extensions -join ','
    if ($script:FileCache.ContainsKey($key)) { return $script:FileCache[$key] }
    $out = @()
    foreach ($r in $script:SourceRoots) {
        $p = Join-Path $root $r
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $out += @(Get-ChildItem -LiteralPath $p -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $Extensions -contains $_.Extension } |
            Where-Object {
                # 排除按**相对仓库根**的路径匹配：绝对路径里可能天然含 `\Temp\` 之类被排除的名字
                #（本机路径就含 `...\Desktop\betterdt\...`，临时树更是在 %TEMP% 下）——
                # 用绝对路径匹配会让整棵子树被静默排除，表现为"枚举到 0 个文件"却一切正常。
                $rel = $_.FullName.Substring($root.Length)
                ('\' + $rel.TrimStart('\', '/') + '\') -notmatch $script:SkipRegex
            })
    }
    $script:FileCache[$key] = $out
    return $out
}

# 文件行内容缓存：A8 段要对十几个概念 × 三种扩展名各扫一遍，
# 若每次都重新读盘就是"同一批文件被读几十遍"（架构守卫的 135s 教训同源）。
# 故按绝对路径缓存行数组 —— 源码总量在内存里只有几十 MB，换来的是常数级下降。
$script:LineCache = @{}
function Get-CachedLines([string]$FullName) {
    if (-not $script:LineCache.ContainsKey($FullName)) {
        $script:LineCache[$FullName] = @(Get-Content -LiteralPath $FullName -ErrorAction SilentlyContinue)
    }
    return $script:LineCache[$FullName]
}

# 逐行命中（返回 @{ Path; Line; Text }），跳过整行注释
function Find-Lines([string[]]$Extensions, [string]$Pattern) {
    $hits = @()
    foreach ($f in (Get-SourceFiles $Extensions)) {
        $lines = Get-CachedLines $f.FullName
        if ($null -eq $lines) { continue }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $l = $lines[$i]
            if ($l -match '^\s*(//|#|--|\*)') { continue }
            if ($l -match $Pattern) {
                $hits += [pscustomobject]@{ Path = (Get-RelPath $f.FullName); Line = $i + 1; Text = $l.Trim() }
            }
        }
    }
    return $hits
}

function Write-Section([string]$Title) {
    Write-Output ''
    Write-Output ('─' * 78)
    Write-Output "== $Title"
    Write-Output ('─' * 78)
}

function Write-Finding([string]$Level, [string]$Text) {
    Write-Output ("  [{0}] {1}" -f $Level, $Text)
}

function Test-Section([string]$Id) {
    return ($Only.Count -eq 0 -or $Only -contains $Id)
}

function Get-PathKey([string]$Full) {
    return ($Full -replace '/', '\').ToLowerInvariant()
}

# ───────────────────── A1/A2/A4：三条已有棘轮的"现状 + 收敛目标" ─────────────────────
#
# 【为什么不重写判据】这三个规则已经在 verify-architecture-guard 里，且带棘轮清单。
# 体检的价值不是"再算一遍是否越界"（门禁已经绿了），而是**把清单的 removeBy 摊开**：
# 每一行登记的 why/removeBy 就是一句"这里欠着一次收敛"，聚起来才是"体检报告"。

$script:Allowlist = $null
function Get-Allowlist {
    if ($null -eq $script:Allowlist) {
        $p = Join-Path $root 'scripts\manifests\architecture-allowlist.json'
        $script:Allowlist = (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json)
    }
    return $script:Allowlist
}

function Show-RatchetRule([string]$RuleId, [string]$Title) {
    Write-Section "A · $Title（棘轮 $RuleId）"
    if (-not (Test-Path $guardPath)) { Write-Finding 'SKIP' '未找到 verify-architecture-guard.ps1，无法复用判据'; return }
    $manifest = Get-Allowlist
    $matched = @(Get-RuleMatchedFiles $root $RuleId)
    $res = Get-BoundaryViolations -Root $root -Manifest $manifest -RuleId $RuleId -MatchedFiles $matched
    $entries = @($manifest.rules.$RuleId.allowed)

    Write-Output "命中 $($matched.Count) 个文件；清单登记 $($entries.Count) 条。"
    Write-Output ''
    Write-Output '  ── 收敛欠账（清单条目的 removeBy）──'
    foreach ($e in $entries) {
        $rb = if ($e.removeBy -and $e.removeBy -notmatch '^保留') { $e.removeBy } else { '保留（终态）' }
        Write-Finding $rb $e.path
    }
    if ($res.Violations.Count -gt 0) {
        Write-Output ''
        Write-Output '  ── 未登记（= 新蔓延，门禁会报红）──'
        foreach ($v in $res.Violations) { Write-Finding '新增!' $v }
    }
    if ($res.Stale.Count -gt 0) {
        Write-Output ''
        Write-Output '  ── 清单失效条目（现实中已不再命中 → 门禁会报红，须删条目）──'
        foreach ($s in $res.Stale) { Write-Finding '失效!' $s }
    }
    if ($res.Violations.Count -eq 0 -and $res.Stale.Count -eq 0) {
        Write-Output ''
        Write-Finding 'OK' '无新增违规、无失效条目（棘轮处于自洽状态）'
    }
}

# ───────────────────────── A3：管道服务端所有者（**当前无机检**）─────────────────────────
#
# 判据（终态）：服务端只应由 core（Rust）持有；过渡期允许 host / desktop-control 各持一条
# **legacy 通道**，但必须逐条登记。第二判据（安全）：Rust 侧 CreateNamedPipeW 必须传
# SECURITY_ATTRIBUTES —— 传 None 时走系统默认 DACL，与 core 侧"显式 ACL"不在同一档。

function Show-PipeServers {
    Write-Section 'A3 · 管道服务端所有者（谁在创建服务端，是否带 ACL）'

    Write-Output '  ── C# 侧（new NamedPipeServerStream）──'
    $csHits = @(Find-Lines @('.cs') 'new\s+NamedPipeServerStream\s*\(')
    if ($csHits.Count -eq 0) { Write-Output '    （无）' }
    foreach ($h in $csHits) {
        $isTest = $h.Path -match '(?i)(^|\\|/)[\w\.\-]*tests?(\\|/|$)'
        $hasBounded = (Get-Content -LiteralPath (Join-Path $root $h.Path) -Raw) -match 'BoundedPipeLine'
        $flag = if ($isTest) { '测试桩' } elseif ($hasBounded) { '有界读✓' } else { '**无有界读**' }
        $owner = if ($isTest) { '豁免' } else { '非 core' }
        Write-Finding $owner "$($h.Path):$($h.Line)  [$flag]"
    }

    Write-Output ''
    Write-Output '  ── Rust 侧（CreateNamedPipeW 真调用；含 ACL 判定）──'
    # 【为什么要看"最后一个实参"而不是"文件里有没有 SECURITY_ATTRIBUTES"】
    # core 的 ACL 是在 security.rs 里构造、pipe.rs 里引用的 —— 按**文件**判会漏；
    # 而按**实参**判（第 8 个 = lpSecurityAttributes）能直接区分 `None` 与"传了变量"。
    # 同时必须把 `format!("CreateNamedPipeW(...) failed")` 这类**日志字面量**排除掉，
    # 否则每处报错文案都会被算成一个"服务端"（本脚本首轮就这样多报了 4 处）。
    $rsHits = @(Find-Lines @('.rs') 'CreateNamedPipeW\s*\(') |
        Where-Object { $_.Text -notmatch 'format!|text:|"' }
    foreach ($h in $rsHits) {
        $lines = Get-CachedLines (Join-Path $root $h.Path)
        $lastArg = Get-LastCallArgument $lines $h.Line
        $acl = if ($lastArg -eq 'None') { '**None / 默认 DACL**' } else { '显式 ACL✓' }
        $owner = if ($h.Path -like 'core\*') { 'core' } else { '非 core' }
        Write-Finding $owner "$($h.Path):$($h.Line)  [$acl]  lpSecurityAttributes = $lastArg"
    }
    Write-Output ''
    Write-Output '  判据：终态只允许 core 持服务端；过渡期非 core 的服务端必须逐条登记（阶段 + 删除步骤）。'
    Write-Output '        Rust 侧 ACL 空缺 = 同用户任意进程可连（与 core 的显式 ACL 不在同一档）。'
}

# 取一次调用的**最后一个实参**（用于判 lpSecurityAttributes）：
# 从调用行往下找第一个独立成行的 `)`，回看它上面最后一行非空内容并去掉尾随逗号。
# 【为什么够用】Rust 侧这两处调用都是"每个实参一行 + 右括号独立成行"的 rustfmt 形态；
# 换成解析器是过度工程 —— 而这里判错的代价只是让人多看一眼，不是误判红线。
function Get-LastCallArgument([string[]]$Lines, [int]$CallLine1Based) {
    $start = $CallLine1Based - 1
    $end = -1
    for ($i = $start + 1; $i -lt [Math]::Min($start + 40, $Lines.Count); $i++) {
        if ($Lines[$i] -match '^\s*\)') { $end = $i; break }
    }
    if ($end -lt 0) { return '(未识别)' }
    for ($i = $end - 1; $i -gt $start; $i--) {
        $t = $Lines[$i].Trim()
        if ($t.Length -eq 0) { continue }
        return ($t -replace ',\s*$', '')
    }
    return '(未识别)'
}

# ───────────────────────── A5：电源红线（**当前无机检**）─────────────────────────
#
# 【为什么不能抄"只在 core 里允许 SetThreadExecutionState"】
# 真实的红线是"**不得持有唤醒请求**"，而不是"只能在某个目录里调用" ——
# 后者会把 `PowerManagement.AllowSystemSleep()`（唯一传 ES_CONTINUOUS 的**放行**用法）
# 判成违规，同时放过任何在 core 里持有 ES_SYSTEM_REQUIRED 的写法。方向正好反了。
# 故判据取**标志位**：出现 REQUIRED 类标志或 PowerSetRequest 才是违规。

function Show-PowerRedline {
    Write-Section 'A5 · 电源红线（不得持有唤醒请求）'

    $requiredFlags = 'ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED|ES_AWAYMODE_REQUIRED|PowerRequestSystemRequired|PowerRequestDisplayRequired|PowerRequestAwayModeRequired'
    $all = @(Find-Lines @('.cs', '.rs', '.cpp', '.h') 'SetThreadExecutionState|PowerSetRequest|PowerCreateRequest|SetSuspendState|powrprof\.dll|ES_CONTINUOUS')

    # 排除"标志/函数的**声明**"（枚举成员、P/Invoke）—— 定义标志 ≠ 持有标志。
    # 判据与 scripts/verify-boundaries.ps1 的 Test-IsFlagDeclaration 一致（同一批判据，两处用途）。
    $holders = @($all | Where-Object {
            ($_.Text -match $requiredFlags -or $_.Text -match 'PowerSetRequest\s*\(') -and
            ($_.Text -notmatch '\bextern\b') -and
            ($_.Text -notmatch '^\s*(?:[A-Za-z_][\w\.]*\s+)?(ES_[A-Z_]+|PowerRequest\w+)\s*=')
        })
    $releases = @($all | Where-Object { $_.Text -match 'ES_CONTINUOUS' -and $_.Text -notmatch $requiredFlags })
    $suspend = @($all | Where-Object { $_.Text -match 'SetSuspendState|powrprof\.dll' })

    Write-Output "  ── 持有唤醒请求（**违规候选**）$($holders.Count) 处 ──"
    if ($holders.Count -eq 0) { Write-Output '    （无）' }
    foreach ($h in $holders) { Write-Finding '违规?' "$($h.Path):$($h.Line)  $($h.Text)" }

    Write-Output ''
    Write-Output "  ── 放行睡眠（ES_CONTINUOUS 复位用法，允许）$($releases.Count) 处 ──"
    foreach ($h in $releases) { Write-Finding '允许' "$($h.Path):$($h.Line)  $($h.Text)" }

    Write-Output ''
    Write-Output "  ── 主动睡眠（用户显式触发，允许）$($suspend.Count) 处 ──"
    foreach ($h in $suspend) { Write-Finding '允许' "$($h.Path):$($h.Line)  $($h.Text)" }

    Write-Output ''
    Write-Output '  判据：出现 REQUIRED 类标志 / PowerSetRequest 持有 = 违规；'
    Write-Output '        只传 ES_CONTINUOUS = MSDN 的"清除唤醒请求、放行睡眠"复位用法 = 允许。'
}

# ───────────────────────── A6：依赖方向与环（**当前无机检**）─────────────────────────

# csproj → 项目引用（绝对路径集合）。返回哈希表：绝对路径 → 引用数组
function Get-ProjectGraph([string]$RepoRoot) {
    $map = @{}
    $projects = @(Get-SourceFiles @('.csproj'))
    foreach ($p in $projects) {
        $full = [System.IO.Path]::GetFullPath($p.FullName)
        if (-not $map.ContainsKey((Get-PathKey $full))) { $map[(Get-PathKey $full)] = @() }
        $raw = Get-Content -LiteralPath $full -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($raw)) { continue }
        $refs = @()
        foreach ($m in [regex]::Matches($raw, '<ProjectReference\s+Include\s*=\s*"([^"]+)"')) {
            $inc = $m.Groups[1].Value -replace '/', '\'
            $target = $inc
            if (-not [System.IO.Path]::IsPathRooted($target)) {
                $target = Join-Path (Split-Path -Parent $full) $target
            }
            $refs += (Get-PathKey ([System.IO.Path]::GetFullPath($target)))
        }
        # Reference 的 HintPath 也可指向仓库内的程序集（跨项目硬引用）——同样算一条边
        foreach ($m in [regex]::Matches($raw, '<Reference\s+Include\s*=\s*"([^"]+)"[^>]*>\s*<HintPath>([^<]+)</HintPath>')) {
            $hint = ($m.Groups[2].Value -replace '/', '\').Trim()
            if ($hint -match '\.\.') {
                $refs += (Get-PathKey ([System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $full) $hint))))
            }
        }
        $map[(Get-PathKey $full)] = @($refs | Select-Object -Unique)
    }
    return $map
}

# 环检测（DFS + 三色）。返回环列表（每条 = 相对路径数组，首尾同点）
function Get-ProjectCycles([hashtable]$Graph, [string]$RepoRoot) {
    $WHITE = 0; $GRAY = 1; $BLACK = 2
    $color = @{}
    foreach ($k in $Graph.Keys) { $color[$k] = $WHITE }
    $stack = New-Object System.Collections.ArrayList
    $cycles = @()

    $dfs = $null
    $dfs = {
        param($node)
        $color[$node] = $GRAY
        [void]$stack.Add($node)
        foreach ($next in @($Graph[$node])) {
            if (-not $color.ContainsKey($next)) { continue }   # 指向仓外（如 NuGet 解出的路径）→ 无边
            if ($color[$next] -eq $GRAY) {
                $idx = $stack.IndexOf($next)
                $cycles += , @($stack[$idx..($stack.Count - 1)]) + @($next)
            }
            elseif ($color[$next] -eq $WHITE) {
                & $dfs $next
            }
        }
        [void]$stack.RemoveAt($stack.Count - 1)
        $color[$node] = $BLACK
    }

    foreach ($k in @($Graph.Keys)) {
        if ($color[$k] -eq $WHITE) { & $dfs $k }
    }
    return @($cycles)
}

# 层判定（按仓库相对路径首段）
function Get-Layer([string]$RelPath) {
    $norm = $RelPath -replace '\\', '/'
    if ($norm -match '^packages/api/') { return 'contract' }
    if ($norm -match '^protocols/') { return 'contract' }
    if ($norm -match '^shared/') { return 'contract' }
    if ($norm -match '^packages/kernel/') { return 'kernel' }
    if ($norm -match '^packages/shell/') { return 'surface' }
    if ($norm -match '^(core|engine|engine-index|native)/') { return 'engine' }
    if ($norm -match '^(host|BetterDesktop\.Cli|launcher|tray|updater|recovery|installer)/') { return 'entry' }
    if ($norm -match '^tools/') { return 'tool' }
    return 'other'
}

function Show-DependencyGraph {
    Write-Section 'A6 · 依赖方向与环（csproj 图）'
    $g = Get-ProjectGraph $root
    Write-Output "csproj 节点 $($g.Count) 个，边 $(($g.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum) 条。"

    $cycles = @(Get-ProjectCycles $g $root)
    Write-Output ''
    Write-Output "  ── 依赖环 $($cycles.Count) 个 ──"
    if ($cycles.Count -eq 0) { Write-Output '    （无环）' }
    foreach ($c in $cycles) {
        Write-Finding '环!' (($c | ForEach-Object { Get-RelPath $_ }) -join ' → ')
    }

    # 越界：低层不得引用高层
    $rank = @{ 'contract' = 0; 'engine' = 1; 'kernel' = 2; 'surface' = 3; 'entry' = 4; 'tool' = 4; 'other' = 5 }
    $violations = @()
    foreach ($k in $g.Keys) {
        $fromRel = Get-RelPath $k
        $fromLayer = Get-Layer $fromRel
        foreach ($t in @($g[$k])) {
            $toRel = Get-RelPath $t
            if ($toRel -notmatch '\.csproj$') { continue }   # HintPath 指向 DLL，跳过方向判定
            $toLayer = Get-Layer $toRel
            if ($fromLayer -in @('other', 'tool') -or $toLayer -in @('other', 'tool')) { continue }
            if ($rank[$toLayer] -gt $rank[$fromLayer]) {
                $violations += "[$fromLayer → $toLayer] $fromRel ⇒ $toRel"
            }
        }
    }
    Write-Output ''
    Write-Output "  ── 反向依赖（低层引高层）$($violations.Count) 条 ──"
    if ($violations.Count -eq 0) { Write-Output '    （无）' }
    foreach ($v in ($violations | Sort-Object)) { Write-Finding '越界!' $v }

    Write-Output ''
    Write-Output '  层序：contract(0) < engine(1) < kernel(2) < surface(3) < entry(4)。'
    Write-Output '  判据：只允许"高层 → 低层"；环与反向依赖都是问题。'
}

# ───────────────────── A7：术语禁词（TERMINOLOGY.md §三 的机检缺口）─────────────────────
#
# 【为什么这条值得机检】TERMINOLOGY.md 自称"命名的唯一真相源"，但**没有任何门禁读它** ——
# 于是"禁用术语不得混入新仓"只写在纸上。禁词表是有限、稳定、无歧义的，正适合机检。

function Show-TerminologyDrift {
    Write-Section 'A7 · 术语禁词（TERMINOLOGY.md §三 ↔ 实际标识符）'
    # 【判据必须收窄到"声明语境"，否则整段会被注释淹没】
    # 首版直接 grep `\bCairo\w*` 得到 5 处命中，**全部是行尾注释里的出处说明**
    #（如"// cairoshell DesktopIcons.setPosition 同值"）—— 那是**正当的溯源**，不是命名漂移。
    # 故：① 先剥掉注释（复用 architecture-guard 的 Remove-SourceComments，保证口径一致）；
    #     ② 只匹配**会进入程序集元数据**的位置：namespace / 类型声明 / using / 接口实现。
    # 这样剩下的命中才是真的"旧仓语言混进新仓"。
    $terms = @(
        @{ Pat = '\bnamespace\s+[A-Za-z0-9_.]*Cairo'; Why = '旧仓命名空间，ADR-001 D1 明确切割' },
        @{ Pat = '\b(class|interface|struct|enum|record)\s+\w*Cairo\w*'; Why = '旧仓类型名' },
        @{ Pat = '\busing\s+[A-Za-z0-9_.]*Cairo[A-Za-z0-9_.]*\s*;'; Why = '旧仓 using' },
        @{ Pat = '\bIExtensionService\b|\bICairoPlugin\b'; Why = '旧仓外挂式扩展宿主模型（已否决）' },
        @{ Pat = '\b(class|interface)\s+\w*ExtensionService\b'; Why = '旧仓外挂式扩展模型' }
    )
    $anyHit = $false
    foreach ($f in (Get-SourceFiles @('.cs', '.rs'))) {
        $rel = Get-RelPath $f.FullName
        if ($rel -match '(?i)(backups|decomp|原生重写|评估)') { continue }
        $code = Remove-SourceComments ((Get-CachedLines $f.FullName) -join "`n")
        $lines = $code -split "`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($t in $terms) {
                if ($lines[$i] -match $t.Pat) {
                    $anyHit = $true
                    Write-Finding '禁词!' "$($rel):$($i + 1)  $($lines[$i].Trim())  ← $($t.Why)"
                }
            }
        }
    }
    if (-not $anyHit) { Write-Finding 'OK' '未发现术语禁词（已剥注释，只查 namespace / 类型声明 / using 语境）' }
    Write-Output ''
    Write-Output '  判据：TERMINOLOGY.md §三 禁词表。此段当前**没有任何门禁**——属机检缺口。'
}

# ───────────────── A7b：命名漂移（同一动作多个名字）─────────────────
#
# 用户口径：同一动作出现三个以上名字 → 标记（如 EnsureHost / LaunchHost / StartHostIfNeeded 并存）。
# 实现：抽 `public ... (Ensure|Launch|Start|Spawn|Boot|Create)\w+\(` 的方法名，
# 按**动词后的尾巴**归组；同一尾巴出现 ≥2 个不同动词即"候选漂移"。
#
# 【为什么是"候选"而不是"违规"】`StartTimer` 与 `CreateTimer` 并存是正常的（不同语义）；
# 漂移的是**同一件事**的三个名字。机器分不出"同一件事"，所以本段只给线索，
# 判定走 TERMINOLOGY.md 与代码评审 —— 与其猜错，不如把判断权交回给人。

function Show-NamingDrift {
    Write-Section 'A7b · 命名漂移（同一动作的多个动词）'
    $verbs = 'Ensure|Launch|Start|Spawn|Boot|Create'
    $map = @{}
    foreach ($f in (Get-SourceFiles @('.cs'))) {
        $rel = Get-RelPath $f.FullName
        if ($rel -match '(?i)(tests?\\|Tests\.cs$)') { continue }
        $code = Remove-SourceComments ((Get-CachedLines $f.FullName) -join "`n")
        foreach ($m in [regex]::Matches($code, "(?m)^(?![^\n]*\bextern\b)\s*(?:public|internal|private|protected)[\w\s<>,\[\]\?\.]*\s+($verbs)([A-Z]\w*)\s*\(")) {
            $verb = $m.Groups[1].Value
            $tail = $m.Groups[2].Value
            if (-not $map.ContainsKey($tail)) { $map[$tail] = @{} }
            $map[$tail][$verb] = $true
        }
    }
    $drift = @($map.Keys | Where-Object { $map[$_].Keys.Count -ge 2 } | Sort-Object)
    Write-Output "  扫描到「动词+尾巴」命名 $($map.Count) 组；同一尾巴 ≥2 个动词的 $($drift.Count) 组："
    if ($drift.Count -eq 0) { Write-Output '    （无）' }
    foreach ($d in $drift) {
        $names = @($map[$d].Keys | Sort-Object | ForEach-Object { "$_$d" })
        $lvl = if ($names.Count -ge 3) { '漂移!' } else { '候选' }
        Write-Finding $lvl ($names -join ' / ')
    }
    Write-Output ''
    Write-Output '  判据：同一动作 ≥3 个名字 = 漂移；2 个 = 候选（可能是正当的语义区分，交人工判）。'
}

# ───────────────────── A8：跨语言重复线索（第三层体检的原料）─────────────────────
#
# 【为什么静态脚本查不出"重复"】"谁拉起进程"可以 grep；"同一件事在 Rust 与 C# 各写一份"
# 不能 —— 两侧的符号名、文件布局、调用形态都不同。故这里只做**线索汇总**：
# 对每个"概念"列出各语言的命中文件数，真正的判定交给 docs/cross-language/所有权矩阵.md 的
# 单所有者规则（每个功能只能有一个所有者，其他语言只能请求）。

function Show-CrossLanguageClues {
    Write-Section 'A8 · 跨语言重复线索（概念 × 语言 命中文件数）'
    # 模式必须够窄 —— 首版用了 `tier|desired` 这类泛词，Rust 侧一次命中 166 处，
    # 表格变成噪声（"线索"一旦飘忽就没人顺着它去看）。宁可少列几行，也要每行都指向真东西。
    $concepts = @(
        @{ Name = '生命周期决策（我们自己的 exe）'; Pat = 'BetterDesktop\.[A-Za-z][A-Za-z0-9]*\.exe' },
        @{ Name = '进程拉起原语（含第三方）'; Pat = 'Process\.Start|spawn_detached|StartDetached|CreateProcessW|ShellExecuteW' },
        @{ Name = '管道服务端'; Pat = 'CreateNamedPipeW\s*\(|new\s+NamedPipeServerStream\s*\(' },
        @{ Name = '管道客户端'; Pat = 'CreateFileW.*PIPE|NamedPipeClientStream' },
        @{ Name = '热键注册'; Pat = 'RegisterHotKey' },
        @{ Name = '托盘图标'; Pat = 'Shell_NotifyIconW|NotifyIcon' },
        @{ Name = '设置写入'; Pat = 'settings\.json' },
        @{ Name = '电源状态'; Pat = 'WM_POWERBROADCAST|PBT_APMRESUM|PBT_APMSUSPEND|SetThreadExecutionState|PowerSetRequest|SetSuspendState' },
        @{ Name = '组件表/期望态'; Pat = 'components\.json|ComponentSpec' },
        @{ Name = '监护/看门狗'; Pat = 'Watchdog|Supervisor|supervisor::' },
        @{ Name = '窗口缩略图/停靠（DWM）'; Pat = 'DwmRegisterThumbnail|DwmThumbnail|DwmSetWindowAttribute' },
        @{ Name = '右键菜单快照'; Pat = 'shellmenu\.json|ShellMenuSnapshot' }
    )
    # 【坑】.NET 复合格式串里"右对齐"就是正宽度 `{1,8}`；写成 `{1,>8}` 会抛
    # "Input string was not in a correct format"（`>` 不是合法对齐说明符）—— 与 C# 字符串插值语法不同。
    Write-Output ("  {0,-34}{1,8}{2,8}{3,8}" -f '概念', 'Rust', 'C#', 'C++')
    Write-Output ('  ' + ('-' * 58))
    foreach ($c in $concepts) {
        $rs = @(Find-Lines @('.rs') $c.Pat).Count
        $cs = @(Find-Lines @('.cs') $c.Pat).Count
        $cpp = @(Find-Lines @('.cpp', '.h') $c.Pat).Count
        $mark = if (($rs -gt 0) -and ($cs -gt 0)) { '  ← 双语言都有' } else { '' }
        Write-Output ("  {0,-34}{1,8}{2,8}{3,8}{4}" -f $c.Name, $rs, $cs, $cpp, $mark)
    }
    Write-Output ''
    Write-Output '  判据：同一概念在多语言各有实现 ≠ 违规；但**两个语言都标"所有者"** = 重复，必须收敛。'
    Write-Output '  判定表在 docs/cross-language/所有权矩阵.md；本段只提供"往哪看"的线索。'
}

# ───────────────────────────── 主体 ─────────────────────────────

Write-Output "Better Desktop Cordis — 体检（一次性 / 只读）  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Output "仓库根：$root"
Write-Output '本脚本**不进 run-gates.ps1**：它的输出是收敛清单，不是通过/失败判据。'

if (Test-Section 'A1') { Show-RatchetRule 'lifecycle-owner' '生命周期所有者（谁在拉起我们自己的 exe）' }
if (Test-Section 'A2') { Show-RatchetRule 'settings-writer' '配置写入（谁在写 settings.json）' }
if (Test-Section 'A3') { Show-PipeServers }
if (Test-Section 'A4') { Show-RatchetRule 'hotkey-registrar' '热键注册（谁在注册全局热键）' }
if (Test-Section 'A5') { Show-PowerRedline }
if (Test-Section 'A6') { Show-DependencyGraph }
if (Test-Section 'A7') { Show-TerminologyDrift }
if (Test-Section 'A7b') { Show-NamingDrift }
if (Test-Section 'A8') { Show-CrossLanguageClues }

if ($WithRuntime) {
    $probe = Join-Path $PSScriptRoot 'probe-runtime.ps1'
    if (Test-Path $probe) { & pwsh -NoProfile -ExecutionPolicy Bypass -File $probe }
    else { Write-Output ''; Write-Output '  未找到 scripts/probe-runtime.ps1，跳过运行时探针。' }
}

Write-Output ''
Write-Output '=== 体检结束（退出码 0：本脚本只报不判）==='
