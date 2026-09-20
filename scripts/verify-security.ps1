# 门禁 security（五层防线机检）
#
# 【为什么要有它】功能能跑不代表安全。安全项的共同特点是"**写的时候想不到，出事时补不上**"：
# 管道少一个长度上限、参数用了字符串拼接、JSON 不限深 —— 这些在功能测试里**全是绿的**，
# 因为功能测试永远用"善意输入"。机器门禁是唯一能在"写的那一刻"挡住它们的层。
#
# 【规则与五层防线的映射】
#   第 1 条 · 管道有界读   → 第一层 IPC（无界读 = 一条连接打死命令通道）
#   第 2 条 · 参数不拼接   → 第二层 进程与路径（参数注入 = 命令注入的经典形态）
#   第 3 条 · 反序列化限深 → 第三层 反序列化（深层嵌套 = 栈深炸弹）
#   第 4 条 · 原生加载路径 → 第二层 进程与路径（相对名 = CWD/DLL 劫持面）
#   第 5 条 · 密钥不落明文 → 第四层 模型与数据（密钥入源码即入仓库）
#   第 6 条 · 不吞异常     → 横向（吞掉的安全异常 = 静默失效的防线）
#   第 7 条 · 原生编译加固 → 第五层 DLL 与原生（CFG/ASLR/DEP 是对内存破坏的最后一道）
#
# 【豁免写法】在**同一行或上方 3 行内**写 `SECURITY-EXEMPT: <理由>` 即跳过该处。
#   豁免必须带理由：门禁读它、人也读它 —— 没有理由的豁免等于把红线变成装饰。
#
# 【棘轮（ratchet）】第 3 条的"本地配置文件"与第 6 条存在历史存量，
# 清单 scripts/manifests/security-baseline.json **只许收缩**：
#   ① 未登记的新违规 → 报红（挡住新增蔓延）；
#   ② 已登记的条目在现实中已消失 → **同样报红**（否则清单会变成历史垃圾场）。
#   与 verify-architecture-guard 的棘轮同构（那边的注释记着这条纪律的由来）。
#
# 依据: docs/security.md 五层防线；core/src/security.rs（Rust 侧同判据）
# 单测: verify-security.Tests.ps1（Pester）
# 豁免: bin / obj / target / node_modules / .git 一律不扫描；tools/decomp 是反编译参考副本（无 csproj，不参与编译）
# 耗时: 只扫源码目录（不是全仓）——全仓 51 万文件要 25s+，而构建产物里没有我们要找的东西。
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 源码根：只在这些目录下递归。刻意**不含** core/engine/native（Rust 工程，本门禁的规则是 C#/CMake 向的）
# 与 backups/参考/ 等非源码目录。
$script:SourceRoots = @(
    'packages', 'installer', 'protocols', 'tools'
)

# 外部输入点：这些文件的 JSON 来自管道 / HTTP / 跨进程 stdio，**硬红线**（不在棘轮范围内）。
# 判定依据是数据来源而不是文件位置 —— 新增一处外部输入点时必须同时加进这张表（否则棘轮会放过它）。
$script:ExternalJsonInputs = @(
    'packages/entry/updater/UpdateSource.cs',
    'packages/shell/shell-index-ipc/IndexIpcClient.cs',
    'packages/shell/shell-clipboard-ipc/ClipboardIpcClient.cs',
    'packages/shell/shell-context-menu/Services/MenuBrokerClient.cs',
    'packages/shell/shell-music/Services/KugouMusicApi.cs'
)

# ───────────────────────── 规则 1：服务端管道必须有界读 ─────────────────────────

# 检测逻辑（可单测）：含服务端管道构造的文件必须引用 BoundedPipeLine。
function Test-PipeBoundedRead([string]$Content) {
    if ($Content -notmatch 'new\s+NamedPipeServerStream\s*\(') { return @() }
    if ($Content -match 'BoundedPipeLine') { return @() }
    return @('服务端命名管道缺少有界读（BoundedPipeLine.TryRead）——裸 ReadLine 可被"只写不换行"吃满内存并钉死实例槽')
}

# ───────────────────────── 规则 2：进程启动参数不得拼接 ─────────────────────────

# 检测逻辑（可单测）：返回违规描述。
# 只判"**非壳语义**的进程启动"：UseShellExecute=true 时参数本就交给 shell 解析（如 explorer 打开路径），
# 那是调用方**有意**选择的语义；而 UseShellExecute=false 是我们自己构造命令行给 CreateProcess，必须用 ArgumentList。
function Test-ProcessStartArgs([string]$Content) {
    $bad = @()
    $lines = $Content -split "`n"

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*//') { continue }
        # 2a：两参字符串重载 Process.Start("exe", "args" + var)
        if ($line -match 'Process\.Start\s*\([^)]*\+') {
            $bad += "第 $($i+1) 行：Process.Start 用字符串拼接命令行（应用 ProcessStartInfo.ArgumentList）"
        }
    }

    # 2b：ProcessStartInfo 初始化块内 Arguments 为动态（插值/拼接）且整块没有显式 UseShellExecute=true
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch 'new\s+[\w\.]*ProcessStartInfo') { continue }
        if ($lines[$i] -match '^\s*//') { continue }

        $block = @()
        for ($j = $i; $j -lt [Math]::Min($i + 25, $lines.Count); $j++) {
            $block += $lines[$j]
            if ($lines[$j] -match '\}\s*;') { break }
        }
        $text = $block -join "`n"
        if ($text -match 'SECURITY-EXEMPT') { continue }

        $dynamic = $false
        foreach ($b in $block) {
            if ($b -match 'Arguments\s*=\s*\$@?"') { $dynamic = $true }
            elseif ($b -match 'Arguments\s*=' -and $b -match '\+') { $dynamic = $true }
        }
        if ($dynamic -and ($text -notmatch 'UseShellExecute\s*=\s*true')) {
            $bad += "第 $($i+1) 行：ProcessStartInfo.Arguments 用插值/拼接且非壳语义（应用 ArgumentList 逐项传参）"
        }
    }
    return $bad
}

# ───────────────────────── 规则 3：JSON 解析必须显式限深 ─────────────────────────

# 检测逻辑（可单测）：外部输入点 → 硬红线；其余 → 棘轮。
function Test-JsonMaxDepth([string]$RelPath, [string]$Content) {
    $usesJson = ($Content -match 'JsonSerializer\.Deserialize' -or $Content -match 'JsonDocument\.Parse')
    if (-not $usesJson) { return @() }
    if ($Content -match 'MaxDepth') { return @() }

    foreach ($e in $script:ExternalJsonInputs) {
        if ($RelPath -eq $e) {
            return @('外部输入（管道/HTTP/跨进程 stdio）的 JSON 解析缺少显式 MaxDepth（禁止依赖库默认值）')
        }
    }
    return @('JSON 解析缺少显式 MaxDepth（棘轮：存量在基线内，新增即红）')
}

# ───────────────────────── 规则 4：原生/程序集加载不得用相对名字面量 ─────────────────────────

# 检测逻辑（可单测）：LoadLibrary("x.dll") 这类**不含路径分隔符**的字面量会被当前目录劫持。
# 允许的形态：变量（且已绝对化）/ 含路径分隔符的字面量。系统库名（如 "user32.dll"）也命中规则 ——
# 刻意如此：走 KnownDLLs 的系统库应当用 DllImport 特性，而不是运行时 LoadLibrary 裸名。
# **逐行**扫描：注释里出现 LoadLibrary("x.dll")（如解释某个失败现象）不是违规。
function Test-RelativeNativeLoad([string]$Content) {
    $bad = @()
    $pattern = '(LoadLibrary|NativeLibrary\.TryLoad|Assembly\.LoadFrom)\s*\(\s*"(?<name>[^"\\/:]+)"'
    $lines = $Content -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*//') { continue }
        $m = [regex]::Match($lines[$i], $pattern)
        if ($m.Success) {
            $bad += "第 $($i+1) 行：$($m.Groups[1].Value) 使用相对名字面量 '$($m.Groups['name'].Value)'（可被当前目录劫持，应用绝对路径变量）"
        }
    }
    return $bad
}

# ───────────────────────── 规则 5：禁止明文密钥字面量 ─────────────────────────

# 检测逻辑（可单测）：api key / token / secret 被赋值成长字面量。
# 阈值 12 字符：够短的值几乎不可能是真密钥，避免把 "token" = "" 之类的占位判红。
function Test-PlaintextSecret([string]$Content) {
    $bad = @()
    $lines = $Content -split "`n"
    $pattern = '(?i)\b(api[_-]?key|auth[_-]?token|access[_-]?token|client[_-]?secret|secret[_-]?key)\b\s*=\s*"[^"]{12,}"'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*//') { continue }
        if ($lines[$i] -match 'SECURITY-EXEMPT') { continue }
        if ($lines[$i] -match $pattern) {
            $bad += "第 $($i+1) 行：疑似明文密钥字面量（密钥应走 DPAPI / 配置注入，不得入源码）"
        }
    }
    return $bad
}

# ───────────────────────── 规则 6：禁止空 catch 吞异常 ─────────────────────────

# 剥离注释噪声（**必须先做**，否则注释里的 `catch { }` 会被当成违规 —— 而本仓库的注释
# 恰恰大量讨论"不要 catch { } 吞异常"，不剥离会得到 143 个假红的荒唐结果）。
# 行数保持不变（整行注释替换成空串而非删除）——否则报出的行号会与真实文件对不上，
# 而"行号对不上"的报错对使用者来说等于没有报错。
function Remove-CsCommentNoise([string]$Content) {
    $noBlock = [regex]::Replace(
        $Content, '/\*.*?\*/', '',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $lines = $noBlock -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*//') { $lines[$i] = '' }
    }
    return ($lines -join "`n")
}

# 检测逻辑（可单测）：catch 块内既无语句也无注释即违规。
# 【为什么"只有注释"算通过】至少留一句"为什么可以忽略"，下一个人才能判断它还成不成立；
# 而真正的空块 `catch { }` 会让防线**静默失效** —— 代码看起来有 try/catch，实则什么都没做。
# 【实现要点：在"剥离注释后的文本"上找位置，在"原文"上判豁免】—— 两者缺一都会假红：
#   · 不剥离 → 注释里讨论 `catch { }` 会被当成违规（本仓库注释大量讨论这件事）；
#   · 只在剥离文本上判 → "仅有一句注释的 catch" 会被剥成空块，同样被误判。
function Test-EmptyCatch([string]$Content) {
    $bad = @()
    $srcLines = $Content -split "`n"
    $clean = Remove-CsCommentNoise $Content
    $pattern = 'catch\s*(\([^)]*\))?\s*\{\s*\}'

    foreach ($m in [regex]::Matches($clean, $pattern)) {
        $startLine = (($clean.Substring(0, $m.Index)) -split "`n").Count
        $endPos = [Math]::Min($m.Index + $m.Length, $clean.Length)
        $endLine = (($clean.Substring(0, $endPos)) -split "`n").Count

        if ($m.Value -match 'SECURITY-EXEMPT') { continue }

        # 原文区间内出现任何注释 → 视为"作者已说明为什么可以忽略"，放行。
        $hasComment = $false
        for ($k = $startLine; $k -le $endLine -and $k -le $srcLines.Count; $k++) {
            if ($srcLines[$k - 1] -match '//' -or $srcLines[$k - 1] -match '/\*') { $hasComment = $true; break }
        }
        if ($hasComment) { continue }

        $bad += "第 ${startLine} 行：空 catch 吞异常（至少记日志或注明为什么可以忽略）"
    }
    return $bad
}

# ───────────────────────── 规则 7：原生工程必须开安全编译项 ─────────────────────────

# 检测逻辑（可单测）：CMake 工程必须显式声明 CFG / ASLR / DEP。
# 【为什么强制显式】这三项里只有 /DYNAMICBASE /NXCOMPAT 是链接器默认；/guard:cf 默认**关**。
# 依赖默认值 = 换一个工具链版本就把防线丢了，所以要求写出来（与 core 侧"显式 ACL"同一思路）。
function Test-NativeHardening([string]$Content) {
    $bad = @()
    if ($Content -notmatch '/guard:cf') { $bad += '缺少 /guard:cf（控制流保护，MSVC 默认关）' }
    if ($Content -notmatch '/DYNAMICBASE') { $bad += '缺少 /DYNAMICBASE（ASLR）' }
    if ($Content -notmatch '/NXCOMPAT') { $bad += '缺少 /NXCOMPAT（DEP）' }
    return $bad
}

# ───────────────────────── 文件枚举 ─────────────────────────

# 只扫源码根下的 .cs 与 CMakeLists.txt；跳过构建产物、反编译参考副本与阶段快照（backups）。
function Get-SecurityScannableFiles([string]$RepoRoot) {
    $skip = '\\(bin|obj|target|node_modules|\.git|\.vs|decomp|backups)\\'
    $out = @()
    foreach ($r in $script:SourceRoots) {
        $p = Join-Path $RepoRoot $r
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $out += @(Get-ChildItem -LiteralPath $p -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.Extension -eq '.cs' -or $_.Name -eq 'CMakeLists.txt') -and ($_.FullName + '\') -notmatch $skip
            })
    }
    return $out
}

# 基线：allowed.<rule> = 相对路径数组（**正斜杠**）。
# 文件不存在 = 空基线（此时任何棘轮命中都是"新违规"）。
function Get-SecurityBaseline([string]$RepoRoot) {
    $path = Join-Path $RepoRoot 'scripts\manifests\security-baseline.json'
    if (-not (Test-Path -LiteralPath $path)) { return @{} }
    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $map = @{}
    foreach ($p in $json.allowed.PSObject.Properties) {
        $map[$p.Name] = @($p.Value | Where-Object { -not [string]::IsNullOrEmpty($_) })
    }
    return $map
}

# 取某条规则的基线集合。**必须处理"键不存在"**：`@($map[$k])` 在键缺失时会得到
# 「含一个 $null 的数组」，于是棘轮结算里冒出幽灵条目 'u（报"必须删除"却删不掉）。
function Get-BaselineSet([hashtable]$Map, [string]$Rule) {
    if (-not $Map.ContainsKey($Rule)) { return @() }
    $v = $Map[$Rule]
    if ($null -eq $v) { return @() }
    return @($v)
}

# 测试工程判定：测试里的管道服务端是**测试桩**（本地进程内，不面对不可信输入），不适用规则 1。
# 判据用相对路径的目录段，避免误伤名字里带 test 的业务文件。
function Test-IsTestPath([string]$RelPath) {
    return [bool]($RelPath -match '(?i)(^|/)[\w\.\-]*tests?(/|$)')
}

# ───────────────────────── 门禁主体 ─────────────────────────

if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'security'
    $root = Get-RepoRoot
    Write-GateStage '枚举源码（packages + installer + protocols + tools）'
    $files = Get-SecurityScannableFiles $root
    Write-GateStage "扫描 $($files.Count) 个文件"

    $baseline = Get-SecurityBaseline $root
    $failures = @()
    $ratchetHits = @{ 'json-maxdepth' = @(); 'empty-catch' = @() }

    foreach ($f in $files) {
        $rel = (Get-RelPath $f.FullName) -replace '\\', '/'
        $content = Get-Content -LiteralPath $f.FullName -Raw
        if ([string]::IsNullOrEmpty($content)) { continue }

        if ($f.Name -eq 'CMakeLists.txt') {
            foreach ($r in Test-NativeHardening $content) {
                $failures += "$rel — 规则7 原生加固：$r"
            }
            continue
        }

        foreach ($r in Test-ProcessStartArgs $content) { $failures += "$rel — 规则2 参数不拼接：$r" }
        foreach ($r in Test-RelativeNativeLoad $content) { $failures += "$rel — 规则4 原生加载路径：$r" }
        foreach ($r in Test-PlaintextSecret $content) { $failures += "$rel — 规则5 明文密钥：$r" }

        if (-not (Test-IsTestPath $rel)) {
            foreach ($r in Test-PipeBoundedRead $content) { $failures += "$rel — 规则1 管道有界读：$r" }
        }

        foreach ($r in Test-JsonMaxDepth $rel $content) {
            if ($r -like '外部输入*') { $failures += "$rel — 规则3 反序列化限深：$r" }
            elseif (-not (Test-IsTestPath $rel)) { $ratchetHits['json-maxdepth'] += $rel }
        }
        foreach ($r in Test-EmptyCatch $content) {
            $ratchetHits['empty-catch'] += $rel
        }
    }

    # 棘轮结算：未被基线登记的命中 = 新违规（报红）；基线里已不存在的条目 = 必须收缩（同样报红）
    foreach ($rule in @('json-maxdepth', 'empty-catch')) {
        $hits = @($ratchetHits[$rule] | Sort-Object -Unique)
        $allowed = Get-BaselineSet $baseline $rule
        foreach ($h in $hits) {
            if ($allowed -notcontains $h) {
                $failures += "$h — 棘轮（$rule）：新引入的存量违规（要么修，要么在 security-baseline.json 显式登记并写明理由）"
            }
        }
        foreach ($a in $allowed) {
            if ($hits -notcontains $a) {
                $failures += "scripts/manifests/security-baseline.json — 棘轮条目 '$a'（$rule）已不再命中，必须删除（清单只许收缩）"
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-GateFail 'security' $failures
    }
    Write-GatePass 'security' "五层防线 7 条规则通过（扫描 $($files.Count) 个文件）"
}
