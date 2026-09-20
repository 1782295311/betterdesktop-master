# verify-no-cross-assembly-event.ps1
# 门禁：扫描跨程序集 C# event 订阅（ADR-002 D4 违规检测）。
# 规则：interface 中声明的 event 若被其他程序集（csproj）以 += 订阅，即违规。
# 白名单：同程序集内订阅、WPF 控件事件（Window/UserControl/Control 派生类）、FileSystemWatcher、
#         状态监控器事件（同程序集内部 INotifyPropertyChanged 模式）、测试代码。
# 用法：pwsh -NoProfile -ExecutionPolicy Bypass scripts/verify-no-cross-assembly-event.ps1
# 退出码：0=通过，1=违规。

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path
)

$ErrorActionPreference = 'Stop'

# 进度反馈（与 lib/common.ps1 同一契约）。本脚本**不 dot-source** 它：它自带 `$RepoRoot` 参数，
# 而 dot-source 会写 `$script:RepoRoot`、把调用方传进来的值覆盖掉（同类坑见 verify-system-integration.ps1 的注释）。
# 所以这里内联三行 —— 契约一致比复用一行代码更要紧。
$gateStart = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "[GATE] no-cross-assembly-event 开始 $((Get-Date).ToString('HH:mm:ss'))"
$violations = New-Object System.Collections.Generic.List[string]

# ---- 1. 枚举所有 .csproj，建立「项目目录 → 项目名」映射 ----
$projects = @{}
Get-ChildItem -Path $RepoRoot -Recurse -Filter *.csproj | ForEach-Object {
    $projDir = $_.DirectoryName
    $projName = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
    $projects[$projDir] = $projName
}
Write-Host "发现 $($projects.Count) 个项目"

function Get-ProjectName([string]$filePath) {
    foreach ($dir in $projects.Keys) {
        if ($filePath.StartsWith($dir, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $projects[$dir]
        }
    }
    return '<unknown>'
}

# ---- 2. 扫描 interface 中声明的 event（跨程序集候选） ----
# 匹配：interface IFoo { ... event EventHandler<Bar>? Changed; ... }
$interfaceEvents = @{}  # eventName -> list of { interface, file, project }
$csFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter *.cs | Where-Object {
    $_.FullName -notmatch '\\(obj|bin|backups|tools\\decomp)\\'
}

foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw -Encoding UTF8
    # 粗匹配 interface 块内的 event 声明
    $ifaceMatches = [regex]::Matches($content, '(?s)interface\s+\w+[^{]*\{(.*?)\}')
    foreach ($m in $ifaceMatches) {
        $ifaceBody = $m.Groups[1].Value
        $eventMatches = [regex]::Matches($ifaceBody, 'event\s+[\w<>,?\s\[\]]+\s+(\w+)\s*;')
        foreach ($em in $eventMatches) {
            $eventName = $em.Groups[1].Value
            $proj = Get-ProjectName $file.FullName
            if (-not $interfaceEvents.ContainsKey($eventName)) {
                $interfaceEvents[$eventName] = New-Object System.Collections.Generic.List[object]
            }
            $interfaceEvents[$eventName].Add([pscustomobject]@{
                EventName = $eventName
                File      = $file.FullName
                Project   = $proj
            })
        }
    }
}
Write-Host "发现 $($interfaceEvents.Count) 个 interface event 声明"

# ---- 3. 扫描所有 .cs 文件中的 += 订阅，判断是否跨程序集 ----
# 白名单事件名（同程序集 WPF/状态监控，不检测）
$whitelistEvents = @(
    'Click', 'Loaded', 'Unloaded', 'Closed', 'Closing',
    'MouseDown', 'MouseUp', 'MouseMove', 'MouseEnter', 'MouseLeave',
    'TextChanged', 'SelectionChanged', 'Checked', 'Unchecked',
    'SizeChanged', 'SourceInitialized', 'ContentRendered',
    'ItemsChanged', 'WinKeyPressed', 'RequestRefresh'
)

# 白名单类型（这些类型的 event 订阅不算跨程序集违规）
$whitelistTypes = @(
    'FileSystemWatcher', 'INotifyPropertyChanged', 'INotifyCollectionChanged',
    'DispatcherTimer', 'Timer', 'Window', 'UserControl', 'Control',
    'MenuItem', 'Button', 'TextBox', 'ComboBox', 'Slider', 'ToggleSwitch',
    'StatusStrip', 'BlankArea', 'IStartMenuSearchService'
)

foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw -Encoding UTF8
    $subscriberProj = Get-ProjectName $file.FullName

    # 匹配 xxx.EventName += handler
    $subMatches = [regex]::Matches($content, '(\w+(?:\.\w+)*)\.(\w+)\s*\+=\s*')
    foreach ($sm in $subMatches) {
        $sourceExpr = $sm.Groups[1].Value
        $eventName = $sm.Groups[2].Value

        # 跳过白名单事件名
        if ($whitelistEvents -contains $eventName) { continue }

        # 跳过非 interface event（只检测在 interface 中声明的 event）
        if (-not $interfaceEvents.ContainsKey($eventName)) { continue }

        # 检查是否有白名单类型在 sourceExpr 中
        $isWhitelisted = $false
        foreach ($wt in $whitelistTypes) {
            if ($sourceExpr -match "(^|\.)$wt`$" -or $sourceExpr -match "\b$wt\b") {
                $isWhitelisted = $true
                break
            }
        }
        if ($isWhitelisted) { continue }

        # 检查定义该 event 的 interface 是否在不同项目
        foreach ($def in $interfaceEvents[$eventName]) {
            if ($def.Project -ne $subscriberProj -and $def.Project -ne '<unknown>') {
                $lineNum = ($content.Substring(0, $sm.Index) -split "`n").Count
                $relPath = $file.FullName.Substring($RepoRoot.Length).TrimStart('\')
                $violations.Add(
                    "跨程序集 event 订阅: $relPath`:$lineNum" +
                    " - $sourceExpr.$eventName += " +
                    "（event 定义于 $($def.Project)，订阅于 $subscriberProj）"
                )
                break
            }
        }
    }
}

# ---- 已知违规基线（计划违规3-8，不在本次修复范围；修复后从本表移除） ----
# eventName -> 说明（对应计划违规编号）
$knownViolations = @{
    'AppSourceChanged'        = '违规3：IAppSourceService 跨程序集 event（待迁移 IEventBus）'
    'PinnedChanged'           = '违规4：IPinningService 跨程序集 event（待迁移 IEventBus）'
    'ForegroundWindowChanged' = '违规5：IWindowTrackerService 跨程序集 event（待迁移 IEventBus）'
}

# ---- 4. 输出结果 ----
$newViolations = New-Object System.Collections.Generic.List[string]
$knownCount = 0
foreach ($v in $violations) {
    $isKnown = $false
    foreach ($evtName in $knownViolations.Keys) {
        if ($v -match "\.$evtName \+=") {
            $isKnown = $true
            $knownCount++
            break
        }
    }
    if (-not $isKnown) {
        $newViolations.Add($v)
    }
}

if ($knownCount -gt 0) {
    Write-Host ""
    Write-Host "已知违规（基线内，待计划违规3-8修复）: $knownCount 项" -ForegroundColor Yellow
}

if ($newViolations.Count -gt 0) {
    Write-Host ""
    Write-Host "=== 新增跨程序集 event 违规 ($($newViolations.Count) 项) ===" -ForegroundColor Red
    foreach ($v in $newViolations) {
        Write-Host "  FAIL: $v" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "修复指引：将 interface event 替换为 IEventBus.EmitAsync/On，见 ADR-002 D4。"
    Write-Host "若属计划内已知违规，请加入脚本 `$knownViolations 基线表。"
    Write-Host "[GATE] no-cross-assembly-event 失败 总耗时 $([int]$gateStart.Elapsed.TotalSeconds)s" -ForegroundColor Red
    exit 1
} else {
    Write-Host ""
    Write-Host "PASS: 未发现新增跨程序集 event 订阅。" -ForegroundColor Green
    Write-Host "[GATE] no-cross-assembly-event 完成 总耗时 $([int]$gateStart.Elapsed.TotalSeconds)s"
    exit 0
}
