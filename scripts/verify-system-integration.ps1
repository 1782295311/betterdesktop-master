# 门禁 system-integration：安装/卸载链路（2026-09-17 安装器级）的四类不变量
# 规则:
#   1. install/uninstall 脚本存在、能被 PowerShell 解析、且是纯 ASCII（PS5.1 以 ANSI 读 .ps1，非 ASCII 会解析失败）
#   2. 跨进程字面量一致（一个点写错就整套失效，且现场表现为"注册了但不生效"）：
#        BetterDesktop.Tray / deployment.json / installRoot / --system-integration
#   3. Cli 必须实现四个子命令（status/register/repair/unregister）
#   4. install 脚本的必检文件清单必须与 publish.ps1 的 $required 一致（半成品不得安装）
#   5. core 兜底计划任务名跨进程一致（core/src/task.rs / uninstall / recovery），且安装器
#      必须走 `--core task register` 委派（不得自带第二份任务定义）
#   单测: verify-system-integration.Tests.ps1
[CmdletBinding()]
param(
    # 仓库根（门禁单测用临时树覆盖）
    [string]$RepoRoot = ''
)

# 必须在 dot-source 之前把覆盖值抄走：lib/common.ps1 会写 `$script:RepoRoot` —— dot-source 进本文件时
# 那个变量就是本文件的 `$RepoRoot`，会把 -RepoRoot 覆盖成真实仓库根。
# 症状是"门禁单测全绿、其实一次都没检查临时树"（本轮踩过，别再犯）。
$rootOverride = $RepoRoot
. (Join-Path $PSScriptRoot 'lib\common.ps1')

Write-GateStart 'system-integration'

$root = if ([string]::IsNullOrWhiteSpace($rootOverride)) { Get-RepoRoot } else { $rootOverride }

$fails = @()

function Read-RepoFile([string]$rel) {
    $full = Join-Path $root $rel
    if (-not (Test-Path $full)) {
        $script:fails += "$rel - 文件不存在（安装器级改动的必需件）"
        return $null
    }
    return (Get-Content $full -Raw)
}

function Assert-Contains([string]$rel, [string]$content, [string]$needle) {
    if ($null -eq $content) { return }
    if ($content.IndexOf($needle, [System.StringComparison]::Ordinal) -lt 0) {
        $script:fails += "$rel - 缺少必需字面量 '$needle'（跨进程契约必须逐字一致）"
    }
}

function Assert-PlainAscii([string]$rel, [string]$content) {
    if ($null -eq $content) { return }
    foreach ($ch in $content.ToCharArray()) {
        if ([int]$ch -gt 127) {
            $script:fails += "$rel - 含非 ASCII 字符（PowerShell 5.1 按 ANSI 读取会解析失败；保持纯 ASCII）"
            return
        }
    }
}

function Assert-Parses([string]$rel) {
    $full = Join-Path $root $rel
    if (-not (Test-Path $full)) { return }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        $script:fails += "$rel - 语法错误: $($errors[0].Message)"
    }
}

# ---- 1. 脚本存在 / 可解析 / 纯 ASCII ----
$installRel = 'scripts/install-betterdesktop.ps1'
$uninstallRel = 'scripts/uninstall-betterdesktop.ps1'
$install = Read-RepoFile $installRel
$uninstall = Read-RepoFile $uninstallRel
Assert-Parses $installRel
Assert-Parses $uninstallRel
Assert-PlainAscii $installRel $install
Assert-PlainAscii $uninstallRel $uninstall

# ---- 2. 跨进程字面量一致 ----
$kernelAutostart = Read-RepoFile 'packages/kernel/kernel/Deployment/AutostartRegistrar.cs'
$kernelDeployment = Read-RepoFile 'packages/kernel/kernel/Deployment/DeploymentInfo.cs'
$trayAppPaths = Read-RepoFile 'tray/AppPaths.cs'
$traySettings = Read-RepoFile 'tray/SettingsBridge.cs'
$trayMenu = Read-RepoFile 'tray/TrayApplicationContext.cs'
$cli = Read-RepoFile 'BetterDesktop.Cli/Program.cs'

foreach ($pair in @(
        @{ Rel = 'packages/kernel/kernel/Deployment/AutostartRegistrar.cs'; Text = $kernelAutostart; Needle = 'BetterDesktop.Tray' },
        @{ Rel = 'tray/SettingsBridge.cs'; Text = $traySettings; Needle = 'BetterDesktop.Tray' },
        @{ Rel = $installRel; Text = $install; Needle = 'BetterDesktop.Tray' },
        @{ Rel = $uninstallRel; Text = $uninstall; Needle = 'BetterDesktop.Tray' },
        @{ Rel = 'packages/kernel/kernel/Deployment/DeploymentInfo.cs'; Text = $kernelDeployment; Needle = 'deployment.json' },
        @{ Rel = 'tray/AppPaths.cs'; Text = $trayAppPaths; Needle = 'deployment.json' },
        @{ Rel = $installRel; Text = $install; Needle = 'deployment.json' },
        @{ Rel = $uninstallRel; Text = $uninstall; Needle = 'deployment.json' },
        @{ Rel = 'packages/kernel/kernel/Deployment/DeploymentInfo.cs'; Text = $kernelDeployment; Needle = 'installRoot' },
        @{ Rel = 'tray/AppPaths.cs'; Text = $trayAppPaths; Needle = 'installRoot' },
        @{ Rel = $installRel; Text = $install; Needle = 'installRoot' },
        @{ Rel = $uninstallRel; Text = $uninstall; Needle = 'installRoot' },
        @{ Rel = 'BetterDesktop.Cli/Program.cs'; Text = $cli; Needle = '--system-integration' },
        @{ Rel = 'tray/TrayApplicationContext.cs'; Text = $trayMenu; Needle = '--system-integration' },
        @{ Rel = $installRel; Text = $install; Needle = '--system-integration' },
        @{ Rel = $uninstallRel; Text = $uninstall; Needle = '--system-integration' }
    )) {
    Assert-Contains $pair.Rel $pair.Text $pair.Needle
}

# ---- 3. Cli 四个子命令 ----
foreach ($verb in @('status', 'register', 'repair', 'unregister')) {
    Assert-Contains 'BetterDesktop.Cli/Program.cs' $cli "case `"$verb`""
}

# ---- 4b. core 兜底计划任务：名字跨进程一致 + 安装器必须走委派 ----
# 任务名是**跨进程字面量**：core 生成 XML 用它、卸载器按它删、recovery 按它删。
# 写错的后果不在安装路径上，而在**卸载路径**上 —— 任务残留后每 5 分钟触发一次，
# 每次都以"文件不存在"失败，而那时已经没有任何地方会报这条错（core 已被删）。
# 这正是"最该被门禁钉住"的一类字面量：错了没人会立刻发现。
$coreTaskRs = Read-RepoFile 'core/src/task.rs'
$recoveryProgram = Read-RepoFile 'recovery/Program.cs'
foreach ($pair in @(
        @{ Rel = 'core/src/task.rs'; Text = $coreTaskRs; Needle = 'BetterDesktop Core Ensure' },
        @{ Rel = $uninstallRel; Text = $uninstall; Needle = 'BetterDesktop Core Ensure' },
        @{ Rel = 'recovery/Program.cs'; Text = $recoveryProgram; Needle = 'BetterDesktop Core Ensure' }
    )) {
    Assert-Contains $pair.Rel $pair.Text $pair.Needle
}

# 安装器**必须委派**（`--core task register`），不得自带第二份任务定义。
# 它自己没有那个字面量是**正确**的：任务 XML 只有 core/src/task.rs 一份实现。
# 这条断言防的是"有人在 PowerShell 里又拼了一遍 XML/名字"——那正是本仓库吃过的事故形态。
Assert-Contains $installRel $install "'--core', 'task', 'register'"

# ---- 4. 必检清单一致性（install vs publish）----
$publish = Read-RepoFile 'scripts/publish.ps1'
function Get-RequiredFiles([string]$text, [string]$startMarker, [string]$endMarker) {
    if ($null -eq $text) { return @() }
    $start = $text.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) { return @() }
    $end = $text.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0) { return @() }
    $block = $text.Substring($start, $end - $start)
    return @([regex]::Matches($block, "'([A-Za-z0-9_\.\\-]+\.(?:exe|yml|dll))'") |
        ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
}

$installRequired = Get-RequiredFiles $install '$required = @(' ')'
$publishRequired = Get-RequiredFiles $publish '$required = @(' ')'
if ($publishRequired.Count -eq 0) {
    $fails += 'scripts/publish.ps1 - 未解析到 $required 清单（门禁解析器需同步）'
}
else {
    $onlyInstall = @($installRequired | Where-Object { $_ -notin $publishRequired })
    $onlyPublish = @($publishRequired | Where-Object { $_ -notin $installRequired })
    foreach ($f in $onlyInstall) { $fails += "scripts/install-betterdesktop.ps1 - 必检清单多了 '$f'（与 publish.ps1 不一致）" }
    foreach ($f in $onlyPublish) { $fails += "scripts/install-betterdesktop.ps1 - 必检清单少了 '$f'（publish.ps1 要求，半成品不得安装）" }
}

if ($fails.Count -gt 0) { Write-GateFail 'system-integration' $fails }
Write-GatePass 'system-integration' '安装/卸载链路：脚本纯 ASCII 可解析、4 类跨进程字面量一致、Cli 四子命令齐全、必检清单与 publish 一致'
