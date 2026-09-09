# verify-native-convergence.ps1
# 门禁：Native 互操作声明收敛（2026-09-07-common-capability-extraction.md Phase A 产物）。
# 规则：
#   1) packages/ 下（排除 bin/obj/backups 与 shell-core/Native）的 [DllImport 声明总数
#      ≤ 基线 281（Phase A 完成后实测计数；迁移前为 311，已消除 30 处重复声明）。
#      新增互操作声明必须先收口到 shell-core/Native/NativeMethods.cs，否则总数会超基线。
#   2) 已收口函数名不得再出现在 shell-core/Native 之外（防旧式重复声明复活）。
#   3) 不允许在 shell-core/Native 之外新建 WinEventPump/MouseHook 同构实现（防多泵/多钩子封装蔓延）。
# 用法：pwsh -NoProfile -ExecutionPolicy Bypass scripts/verify-native-convergence.ps1
# 退出码：0=通过，1=违规。

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path,
    [int]$MaxDllImport = 281
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\common.ps1')

# ---- 已收口到 shell-core/Native 的函数名（出现即视为重复声明复活）----
$convergedFunctions = @(
    'SetWinEventHook', 'UnhookWinEvent',
    'SetWindowsHookEx', 'UnhookWindowsHookEx', 'CallNextHookEx',
    'MonitorFromPoint', 'GetMonitorInfo', 'GetDpiForMonitor',
    'GetLastInputInfo',
    'PostThreadMessage', 'GetMessage', 'TranslateMessage', 'DispatchMessage'
)

# ---- 1. 枚举源码文件（排除 bin/obj/backups 与 shell-core/Native）----
$csFiles = Get-ChildItem -Path (Join-Path $RepoRoot 'packages') -Recurse -Filter *.cs | Where-Object {
    $_.FullName -notmatch '\\(bin|obj|backups)\\' -and
    $_.FullName -notmatch '\\packages\\backups\\' -and
    $_.FullName -notmatch '\\shell-core\\Native\\'
}

# ---- 2. 统计 DllImport 总数 ----
$total = 0
foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw -Encoding UTF8
    $total += [regex]::Matches($content, 'DllImport\(').Count
}
Write-Output "非 shell-core/Native 的 DllImport 声明总数: $total / 上限 $MaxDllImport"

$violations = New-Object System.Collections.Generic.List[string]

if ($total -gt $MaxDllImport) {
    $violations.Add("DllImport 总数超基线：$total > $MaxDllImport（新增互操作声明应收口到 shell-core/Native/NativeMethods.cs）")
}

# ---- 3. 已收口函数名残留检查 ----
foreach ($file in $csFiles) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -notmatch 'DllImport\(') { continue }
        foreach ($fn in $convergedFunctions) {
            if ($line -match "\b$fn\b") {
                $rel = $file.FullName.Substring($RepoRoot.Length).TrimStart('\')
                $violations.Add("重复声明复活: $rel`:$($i+1) - $fn（已收口到 shell-core/Native，禁止在 NativeMethods 之外重复声明）")
            }
        }
    }
}

# ---- 4. 多泵/多钩子封装蔓延检查（WinEventPump/MouseHook 只允许在 shell-core/Native）----
foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw -Encoding UTF8
    if ($content -match 'class\s+(?:WinEventPump|MouseHook)(?:\s|:|\{)' -or $content -match 'class\s+MouseHook(?:\s|:|\{)') {
        $rel = $file.FullName.Substring($RepoRoot.Length).TrimStart('\')
        $violations.Add("泵/钩子封装重复: $rel（WinEventPump/MouseHook 唯一实现位于 shell-core/Native）")
    }
}

# ---- 5. 输出 ----
if ($violations.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Native 收敛违规 ($($violations.Count) 项) ==="
    foreach ($v in $violations) {
        Write-Output "  FAIL: $v"
    }
    Write-Output ""
    Write-Output "修复指引：新增互操作声明收口到 packages/shell/shell-core/Native/NativeMethods.cs（分区静态类）；"
    Write-Output "泵/钩子统一用 WinEventPump/MouseHook（7435 纪律：专用 STA 泵线程 + delegate 字段强引用 + 成对 Unhook）。"
    Write-GateFail 'native-convergence' $violations
} else {
    Write-Output ""
    Write-GatePass 'native-convergence' "Native 互操作声明收敛合规（$total ≤ $MaxDllImport，无重复声明复活）"
    exit 0
}
