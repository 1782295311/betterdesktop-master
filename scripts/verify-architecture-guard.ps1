# 门禁 architecture-guard（架构守卫）
#
# 现有一条规则（ADR-003 D2）:
#   R0 · 宿主不得携带业务 UI：packages/entry/host/ 下除 App.xaml 外的 .xaml 视为违规
#
# 本批新增三条**边界棘轮**规则（计划 docs/plans/2026-09-18-on-demand-core-architecture.md §5 C21/C22）:
#   R1 · lifecycle-owner  —— 拉起「我们自己的 exe」（BetterDesktop.*.exe）只能由生命周期所有者发起
#   R2 · hotkey-registrar —— RegisterHotKey 的注册点必须逐一声明生命周期所有者（core 独占"必须活过壳"的键）
#   R3 · settings-writer  —— settings.json 的唯一合法写入口是 SettingsService
#
# 【为什么是棘轮而不是硬白名单】
# 这三条规则的**终态**是"唯一位置"，但当前工作树里还有 tray / watchdog / agent / host 等多处历史拉起点
# （按计划 S4/S5/S7 逐步删除）。若一上线就按终态判定，门禁会常年飘红 → 无人看 → 体系先烂。
# 故采用棘轮（ratchet）：清单 scripts/manifests/architecture-allowlist.json **只许收缩**。
#   ① 未登记的新违规 → 报红（挡住新增蔓延）；
#   ② 清单条目在现实中已消失（文件不存在或已不再匹配）→ **同样报红**，强制同步删条目。
#     这一条是棘轮不腐烂的关键：没有它，清单会变成"历史垃圾场"，收缩永远不会发生。
#
# 依据: 计划 §3 依赖方向 / §6.1-B core 禁止清单 / §14 禁区；ADR-003 D2
# 单测: verify-architecture-guard.Tests.ps1（Pester）
# 豁免: bin / obj / target / node_modules / .git 一律不扫描
. (Join-Path $PSScriptRoot 'lib\common.ps1')

#   R4 · core-name-owner —— `BetterDesktop.Core` 这个名只有一个主人：Rust 的常驻进程
#
# ───────────────────────── R0：宿主不得携带业务 UI ─────────────────────────

# 检测逻辑（可单测）：返回 hostDir 下除 App.xaml 之外的业务 .xaml 绝对路径
function Get-BusinessXamlPaths([string]$HostDir) {
    $xaml = @(Get-ChildItem $HostDir -Recurse -Filter '*.xaml' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
    return @($xaml | Where-Object { $_.Name -ne 'App.xaml' } | ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) })
}

# ───────────────── R4：`BetterDesktop.Core` 命名唯一性（可单测） ─────────────────
#
# 判据（2026-09-19 定案）：**裸 `BetterDesktop.Core`** —— 精确匹配 `^BetterDesktop\.Core(\..*)?$`
#（大小写不敏感；"后跟点或字符串结束"），命中 **文件名 / 目录名 / csproj 的 `<AssemblyName>`** 三者之一即违规。
#
# 【为什么不是"凡以 .Core 结尾都不许"】那会误伤 `BetterDesktop.Shell.Core` ——
# 它是**被 88 处 ProjectReference 引用的合法共享 UI 库**，且与 Rust core **不撞名**：
# 后者是**原生 exe**，连 .NET 程序集都不是，assembly resolution 根本不参与。
# 这条门禁收的是**裸名**，不是后缀。
#
# 【为什么 `betterdesktop-core` 不命中】它是 Rust crate 自己的名字（连字符、小写），**唯一合法的主人**；
# 连字符与点不同，故天然不匹配 —— 不需要白名单，也就不会被棘轮的"条目失效"反噬。
#
# 【为什么把 `BetterDesktop.Core.UI` 也算违规】宁可报红让人**显式登记**，
# 也不愿未来某个实现者顺手起个 `BetterDesktop.Core.*` 而无人察觉（本规则的期望收益正是"拦在起名那一刻"）。
function Test-CoreNameViolation([string]$Name) {
    return $Name -match '^BetterDesktop\.Core(\..*)?$'
}

# 收集违规（可单测）：返回相对路径数组；排除目录与 R1-R3 同一套（bin/obj/target/backups…）
function Get-CoreNameViolations([string]$Root) {
    # 【2026-09-20 实测】这一条占 architecture-guard 全程 223s 里的 **135s**（60%）。
    #
    # 分解（实测）：全仓 48874 目录 / 514206 文件；两次 `Get-ChildItem -Recurse` 本身只占 ~25s，
    # 真正的开销是**对每个条目单独跑排除判断** —— 旧写法 `& $isSkipped $f.FullName`
    # 每次都要过 7 条正则（`$skip | Where-Object { $full -match $_ }`），
    # 51 万文件 × 7 ≈ **350 万次正则匹配**。
    #
    # 所以这里只做一件事：把排除判断压成**一条正则**（`$skipRegex` 由 7 个模式 join 而成，
    # 语义与"逐个试"完全等价 —— OR 的结合律）。判据一个字没改，常数掉一个数量级。
    # （试过"手写剪枝跳过 bin/obj"，反而慢 3.5 倍 —— 见 Get-BoundarySourceFiles 的对照数据。）
    $skipRegex = $script:BoundarySkipRegex
    $bad = @()

    $dirs = @(Get-ChildItem $Root -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName + '\' -notmatch $skipRegex })
    foreach ($d in $dirs) {
        if (Test-CoreNameViolation $d.Name) { $bad += (Get-RelPathUnder -FullPath $d.FullName -Root $Root) }
    }

    $files = @(Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $skipRegex })
    foreach ($f in $files) {
        if (Test-CoreNameViolation $f.Name) { $bad += (Get-RelPathUnder -FullPath $f.FullName -Root $Root) }
        # 文件名合规但 AssemblyName 是裸名（例如 Foo.csproj 产出 BetterDesktop.Core.dll）—— 同样违规
        if ($f.Extension -eq '.csproj') {
            $text = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
            if ($text -and ($text -match '<AssemblyName>\s*BetterDesktop\.Core\s*</AssemblyName>')) {
                $bad += (Get-RelPathUnder -FullPath $f.FullName -Root $Root)
            }
        }
    }

    return @($bad | Select-Object -Unique)
}

# ─────────────────────── R1/R2/R3：边界棘轮（可单测） ───────────────────────

# 排除目录：构建产物、依赖、版本控制、**历史快照**
# 豁免理由：backups/ 是刻意保留的历史快照（stage1-pre-clean / stage1-stable / rollback-*），
# 按纪律"快照不改"，对它们做架构判定没有意义（它们的违规无法修复，会让门禁永久飘红）。
$script:BoundarySkipDirs = @('\\bin\\', '\\obj\\', '\\target\\', '\\node_modules\\', '\\.git\\', '\\.vs\\', '\\backups\\')

# 排除目录的正则：一次 join，避免"每个目录都遍历一次模式数组"。
$script:BoundarySkipRegex = ($script:BoundarySkipDirs -join '|')

# 枚举缓存（2026-09-20）：三条边界规则的 `Extensions` **完全相同**（都是 `.cs` / `.rs`），
# 各自枚举一次纯属重复。缓存把"三次全仓枚举"压成一次。
# key 含 Root ⇒ 门禁单测用临时树时不会串味。
$script:SourceFileCache = @{}

# 扫描源码文件（可单测）：返回 root 下扩展名匹配且不在排除目录内的文件绝对路径
#
# 【2026-09-20 测量与两次修正 —— 别再把这段改回"手写剪枝"】
#
# 事实：全仓 **51 万文件 / 4.9 万目录**，其中绝大多数在 bin/ obj/ node_modules/ 之类被排除的目录里。
# 于是有两种优化方向，我两种都量过：
#
#   ① 手写剪枝（自己递归 + `[IO.Directory]::Exists` 逐条目判断，下降时跳过排除目录）：
#      **比原生慢 3.5 倍** —— 65.9s vs 18.6s，返回的文件数一模一样（875 个）。
#      原因很朴素：PowerShell 的逐条目循环 + 每条目一次系统调用，常数远大于
#      `Get-ChildItem` 背后那个原生的 C# 枚举器。**剪枝的算法优势抵不过实现语言的常数差**。
#   ② 保留 `Get-ChildItem`，只压缩**排除判断**的代价（把"7 条正则逐个试"合成 1 条）：
#      这一步才是决定性的 —— R4 之所以要 135s，是因为它对每个文件都跑一次
#      `$skip | Where-Object { $full -match $_ }`（7 条正则 × 51 万文件 ≈ 350 万次匹配）。
#
# 所以最终形态是 ②（外加缓存）：判据逐字不变，只把常数压下来。
# **扩展名过滤必须在排除判断之前** —— 这样排除正则只需跑在几百个候选文件上，而不是 51 万个。
function Get-BoundarySourceFiles([string]$Root, [string[]]$Extensions) {
    $key = "$Root|" + ($Extensions -join ',')
    if ($script:SourceFileCache.ContainsKey($key)) {
        return $script:SourceFileCache[$key]
    }

    $skipRegex = $script:BoundarySkipRegex
    $files = @(Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $Extensions -contains $_.Extension } |
        Where-Object { $_.FullName -notmatch $skipRegex })

    $result = @($files | ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) })
    $script:SourceFileCache[$key] = $result
    return $result
}

# 三条规则的匹配定义（可单测：每个 rule 给 "两个标记同时出现才算违规" 的语义）
$script:BoundaryRules = @{
    'lifecycle-owner' = @{
        Desc      = '拉起我们自己的 exe 只能由生命周期所有者（core / launcher）发起'
        Extensions = @('.cs', '.rs')
        # 模式表必须覆盖**两种语言各自的拉起原语**。原先只有 C# 口径（Process.Start 等），
        # 于是 core 用 `process::spawn_detached` 拉起 CLI 时**两个标记只命中一个 → 不算命中**，
        # 整条 R1 对 Rust 侧是瞎的（2026-09-19 实测：清单里登记了 core\src\shellmenu.rs，
        # 门禁却报"条目已失效"，因为规则根本没匹配到）。这不是清单的问题，是规则的盲区。
        NeedAll   = @(
            'Process\.Start|ProcessStartInfo|CreateProcessW|StartDetached|ShellExecuteW|spawn_detached|run_and_wait',
            # 【2026-09-20 修】exe 名必须**允许多段**：旧口径 `BetterDesktop\.[A-Za-z][A-Za-z0-9]*\.exe`
            # 只匹配"两段名"（`BetterDesktop.Host.exe`、`BetterDesktop.DesktopControl.exe`），
            # 于是**三段名全部对本规则隐形**：`BetterDesktop.Index.Engine.exe`、
            # `BetterDesktop.Clipboard.Engine.exe`、`BetterDesktop.Clipboard.Panel.exe`。
            # 实测后果：`packages/shell/shell-index-ipc/IndexEngineLauncher.cs`（`EnsureEngine` +
            # `StartDetached`，被 shell-search / shell-app-source 直接调用）从未命中本规则 ——
            # 真机探针反而抓到了它拉起的、**父进程不是 core** 的 `BetterDesktop.Index.Engine.exe`。
            # 规则写窄一点是"误报更少"，但规则**该管的人不管**时，误报为零也不值得。
            'BetterDesktop(?:\.[A-Za-z][A-Za-z0-9]*)+\.exe'
        )
    }
    'hotkey-registrar' = @{
        Desc      = 'RegisterHotKey 注册点必须声明生命周期所有者'
        Extensions = @('.cs', '.rs')
        NeedAll   = @('RegisterHotKey')
    }
    'settings-writer' = @{
        Desc      = 'settings.json 的唯一合法写入口是 SettingsService'
        Extensions = @('.cs', '.rs')
        NeedAll   = @(
            'settings\.json',
            'WriteAllText|WriteAllBytes|WriteAllLines|OpenWrite|StreamWriter|File\.Delete|File\.Move'
        )
    }
}

# 剥掉行注释（可单测）：`//` 到行尾。
#
# 【为什么必须剥】规则的意图是"这段代码里出现了某个调用/字面量"，而注释里**解释**这些词
# 恰恰说明作者知道它们存在（例如 pipe.rs 注释里写"`set` 刻意不实现：core 写 settings.json
# 会制造第二个写者"）—— 那是**反证**代码没违规，却被粗筛判成了违规。
#
# 已知局限：字符串字面量里的 `//`（如 "https://"）也会被剥掉，那只可能导致**漏报**，不会误报。
function Remove-SourceComments([string]$Text) {
    $lines = $Text -split "`n"
    $stripped = foreach ($line in $lines) { ($line -split '//', 2)[0] }
    return ($stripped -join "`n")
}

# 单规则命中文件（可单测）：文件中**同时**出现 NeedAll 里的每个模式（注释不计），才算命中该规则
function Get-RuleMatchedFiles([string]$Root, [string]$RuleId) {
    $rule = $script:BoundaryRules[$RuleId]
    if ($null -eq $rule) { throw "unknown rule id: $RuleId" }
    $hit = @()
    foreach ($f in (Get-BoundarySourceFiles $Root $rule.Extensions)) {
        $text = Get-Content $f -Raw -ErrorAction SilentlyContinue
        if ($null -eq $text) { continue }
        $code = Remove-SourceComments $text
        $all = $true
        foreach ($pat in $rule.NeedAll) {
            if ($code -notmatch $pat) { $all = $false; break }
        }
        if ($all) { $hit += $f }
    }
    return @($hit)
}

# 清单条目匹配（可单测）：以 '/' 或 '\' 结尾 = 目录前缀匹配，其余 = 精确匹配文件相对路径。
# 两种分隔符都接受（Windows 路径手写时必然混用；只认其中一种会让清单静默失效 —— 本门禁首轮就被这个坑绊过）。
function Test-AllowEntryMatch([string]$RelPath, [string]$EntryPattern) {
    $normalizedEntry = $EntryPattern.Replace('\', '/')
    $normalizedRel = $RelPath.Replace('\', '/')
    if ($normalizedEntry.EndsWith('/')) {
        return $normalizedRel.StartsWith($normalizedEntry, [System.StringComparison]::OrdinalIgnoreCase)
    }
    return $normalizedRel.Equals($normalizedEntry, [System.StringComparison]::OrdinalIgnoreCase)
}

# 棘轮判定（可单测）：返回 @{ Violations; Stale }
#   Violations = 命中规则但未登记的相对路径
#   Stale      = 已登记但现实中不再命中的条目（强制收缩清单）
function Get-BoundaryViolations(
    [string]$Root,
    [object]$Manifest,
    [string]$RuleId,
    [string[]]$MatchedFiles
) {
    $entries = @()
    $ruleNode = $Manifest.rules.$RuleId
    if ($null -ne $ruleNode) { $entries = @($ruleNode.allowed) }

    $violations = @()
    $hitRels = @($MatchedFiles | ForEach-Object { (Get-RelPathUnder -FullPath $_ -Root $Root) })
    foreach ($rel in $hitRels) {
        $ok = $false
        foreach ($e in $entries) {
            if (Test-AllowEntryMatch $rel $e.path) { $ok = $true; break }
        }
        if (-not $ok) { $violations += $rel }
    }

    $stale = @()
    foreach ($e in $entries) {
        $used = @($hitRels | Where-Object { Test-AllowEntryMatch $_ $e.path })
        if ($used.Count -eq 0) { $stale += $e.path }
    }

    return @{ Violations = @($violations); Stale = @($stale) }
}

# 相对路径（可注入根，避免与 lib/common.ps1 的同名函数冲突 —— 那个只认仓库根单例）
function Get-RelPathUnder([string]$FullPath, [string]$Root) {
    if ($FullPath.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($Root.Length + 1)
    }
    return $FullPath
}

# ───────────────────────────── 门禁主体 ─────────────────────────────
# （dot-source 时跳过，供单测仅加载函数）

if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'architecture-guard'
    $root = Get-RepoRoot
    $discover = $args -contains '-Discover'

    # 发现模式：只打印各规则当前命中的文件，供播种/复核清单用；不代表通过或失败。
    if ($discover) {
        foreach ($ruleId in @('lifecycle-owner', 'hotkey-registrar', 'settings-writer')) {
            Write-Output "== $ruleId =="
            foreach ($f in (Get-RuleMatchedFiles $root $ruleId)) {
                Write-Output (Get-RelPathUnder -FullPath $f -Root $root)
            }
        }
        exit 0
    }

    $allFails = @()

    # R0：宿主不得携带业务 UI
    $hostDir = Join-Path $root 'packages\entry\host'
    # 豁免清单（ADR-003 D2）：SplashWindow 为宿主启动画面（插件加载前须由宿主展示），只减不增
    $allowedXaml = @('packages\entry\host\Views\SplashWindow.xaml')
    $businessXaml = @(Get-BusinessXamlPaths $hostDir | Where-Object { (Get-RelPath $_) -notin $allowedXaml })
    if ($businessXaml.Count -gt 0) {
        $allFails += @($businessXaml | ForEach-Object { "$(Get-RelPath $_) — 宿主不得携带业务 UI，该窗口应由 shell 插件提供" })
    }
    Write-GateStage 'R0 完成（packages/entry/host/*.xaml）'

    # R1/R2/R3：边界棘轮
    $manifestPath = Join-Path $root 'scripts\manifests\architecture-allowlist.json'
    if (-not (Test-Path $manifestPath -PathType Leaf)) {
        Write-GateFail 'architecture-guard' @("$manifestPath — 边界棘轮清单缺失（R1/R2/R3 无法判定）")
    }
    # 【2026-09-20 加固】清单解析失败必须**精确报"清单坏了"**，不能让它退化成"所有条目都不存在"。
    # 实测代价：一次手改把直引号写进 JSON 字符串（合法 XML/PS 习惯，非法 JSON），
    # 门禁于是报出 **29 条**"未登记"（每条都是假的）—— 真正的原因一行都没说。
    # 规则本身没错（它确实什么都读不到），错的是**报错指向了错误的方向**。
    try {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        Write-GateFail 'architecture-guard' @("$manifestPath — 清单无法解析（JSON 语法错误）：$($_.Exception.Message)。**本次判定无效**，请先修清单；不要把下面的『未登记』当真。")
    }

    foreach ($ruleId in @('lifecycle-owner', 'hotkey-registrar', 'settings-writer')) {
        $matched = @(Get-RuleMatchedFiles $root $ruleId)
        $res = Get-BoundaryViolations -Root $root -Manifest $manifest -RuleId $ruleId -MatchedFiles $matched
        foreach ($v in $res.Violations) {
            $allFails += "[$ruleId] $v — 未登记；$($script:BoundaryRules[$ruleId].Desc)。若确属合法，请登记进 architecture-allowlist.json 并注明理由与 removeBy"
        }
        foreach ($s in $res.Stale) {
            $allFails += "[$ruleId] $s — 清单条目已失效（现实中不再命中）；棘轮只许收缩，请删除该条目"
        }
        # 每条规则都**独立全仓扫描一次**（规则各有自己的扩展名与口径）——
        # 所以逐条打点：不然"三条规则一起慢"与"某一条特别慢"分不开。
        Write-GateStage "$ruleId 完成（命中 $($matched.Count) 个文件）"
    }

    # R4：`BetterDesktop.Core` 命名唯一性（不是棘轮 —— 该名**本就该零命中**，无需清单）
    $coreNameViolations = @(Get-CoreNameViolations $root)
    Write-GateStage 'R4 完成（Core 命名唯一性，全仓目录 + 文件枚举）'
    if ($coreNameViolations.Count -gt 0) {
        $allFails += @($coreNameViolations | ForEach-Object {
                "[core-name-owner] $_ — `BetterDesktop.Core` 这个名只有一个主人：**Rust 的常驻进程**。C# 侧不得起裸名（合法的共享 UI 库是带前缀的 BetterDesktop.Shell.Core；Rust crate 是 betterdesktop-core）。若确需该名，请说明理由并在此规则上显式登记"
            })
    }

    if ($allFails.Count -gt 0) { Write-GateFail 'architecture-guard' $allFails }
    Write-GatePass 'architecture-guard' '宿主无业务 UI；生命周期 / 热键 / 配置写入三条边界棘轮无新增违规且清单无失效条目；BetterDesktop.Core 命名唯一'
}
