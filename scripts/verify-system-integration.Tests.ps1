# verify-system-integration.Tests.ps1
# Pester 测试：verify-system-integration.ps1 门禁脚本（兼容 Pester 3.x）
# 用法：pwsh -NoProfile -Command "Invoke-Pester scripts/verify-system-integration.Tests.ps1"
#
# 为什么用临时假树：门禁的职责是"契约字面量一致"，与真实实现内容无关；
# 假树让每个失败分支都能被独立触发（真树里制造不一致 = 破坏产品）。

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'verify-system-integration.ps1'

function New-TestRoot {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) "gate-si-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function Write-TreeFile([string]$root, [string]$rel, [string]$content) {
    $full = Join-Path $root $rel
    New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($full, $content)
}

function New-GoodTree {
    $root = New-TestRoot

    Write-TreeFile $root 'packages/kernel/kernel/Deployment/AutostartRegistrar.cs' 'class A { const string N = "BetterDesktop.Tray"; }'
    Write-TreeFile $root 'packages/kernel/kernel/Deployment/DeploymentInfo.cs' 'class D { const string F = "deployment.json"; const string K = "installRoot"; }'
    Write-TreeFile $root 'packages/entry/tray/AppPaths.cs' 'class P { const string F = "deployment.json"; const string K = "installRoot"; }'
    Write-TreeFile $root 'packages/entry/tray/SettingsBridge.cs' 'class S { const string N = "BetterDesktop.Tray"; }'
    Write-TreeFile $root 'packages/entry/tray/TrayApplicationContext.cs' 'class T { const string V = "--system-integration"; }'
    # 【S4-4（2026-09-20）】这里原有 `watchdog/Program.cs` 夹具：看门狗退役后它不再是"必需件"，
    # 门禁也不再读它（真树里那个文件已经删掉了）。少了它，本用例的其余契约不受影响。
    Write-TreeFile $root 'packages/entry/cli/Program.cs' @'
class C {
  const string V = "--system-integration";
  switch (x) { case "status": case "register": case "repair": case "unregister": }
}
'@
    Write-TreeFile $root 'scripts/install-betterdesktop.ps1' @'
$required = @('a.exe', 'cordis.yml')
# BetterDesktop.Tray deployment.json installRoot
# --system-integration
# Invoke-Tool $cli @('--core', 'task', 'register') -Capture
'@
    Write-TreeFile $root 'scripts/uninstall-betterdesktop.ps1' @'
# BetterDesktop.Tray deployment.json installRoot
# --system-integration
# BetterDesktop Core Ensure
'@
    Write-TreeFile $root 'core/src/task.rs' 'pub const TASK_NAME: &str = "BetterDesktop Core Ensure";'
    Write-TreeFile $root 'packages/entry/recovery/Program.cs' 'class R { const string T = "BetterDesktop Core Ensure"; }'
    Write-TreeFile $root 'scripts/publish.ps1' @'
$required = @('a.exe', 'cordis.yml')
'@
    return $root
}

Describe 'verify-system-integration' {
    Context '契约一致时' {
        It '应返回退出码 0（通过）' {
            $root = New-GoodTree
            try {
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 0
                ($output -join "`n") | Should Match 'PASS'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '托盘自启值名漂移时' {
        It '应返回退出码 1 并指出缺少的字面量' {
            $root = New-GoodTree
            try {
                Write-TreeFile $root 'scripts/install-betterdesktop.ps1' @'
$required = @('a.exe', 'cordis.yml')
# deployment.json installRoot --system-integration
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'BetterDesktop.Tray'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '脚本含非 ASCII 字符时' {
        It '应返回退出码 1（PS5.1 会解析失败）' {
            $root = New-GoodTree
            try {
                Write-TreeFile $root 'scripts/install-betterdesktop.ps1' @'
$required = @('a.exe', 'cordis.yml')
# 中文注释会破坏 PS5.1 解析
# BetterDesktop.Tray deployment.json installRoot --system-integration
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'non-ASCII|ASCII'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '必检清单与 publish 不一致时' {
        It '应返回退出码 1 并指出差异' {
            $root = New-GoodTree
            try {
                Write-TreeFile $root 'scripts/install-betterdesktop.ps1' @'
$required = @('a.exe')
# BetterDesktop.Tray deployment.json installRoot --system-integration
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'cordis.yml'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '卸载器丢掉计划任务名时' {
        It '应返回退出码 1 并指出任务名' {
            $root = New-GoodTree
            try {
                Write-TreeFile $root 'scripts/uninstall-betterdesktop.ps1' @'
# BetterDesktop.Tray deployment.json installRoot
# --system-integration
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'BetterDesktop Core Ensure'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '安装器不再委派任务注册时' {
        It '应返回退出码 1（防第二份任务定义）' {
            $root = New-GoodTree
            try {
                Write-TreeFile $root 'scripts/install-betterdesktop.ps1' @'
$required = @('a.exe', 'cordis.yml')
# BetterDesktop.Tray deployment.json installRoot
# --system-integration
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'task'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '缺少必需文件时' {
        It '应返回退出码 1 并指出文件' {
            $root = New-GoodTree
            try {
                # 夹具必须用**门禁确实会读**的必需件。原先用的是 `watchdog/Program.cs` ——
                # S4-4 删掉那个文件、门禁也随之不再读它之后，这条用例就会"因为夹具消失"而误报失败
                # （测试没坏，是它依附的东西被移走了）。
                Remove-Item (Join-Path $root 'packages/entry/tray/SettingsBridge.cs') -Force
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'SettingsBridge'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }
}
