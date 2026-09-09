# 门禁 architecture-guard：宿主不得携带业务 UI（一切 UI 由 shell 插件提供）
# 规则:
#   - 扫描范围: host/ 下所有 .xaml（排除 obj/bin）
#   - 豁免: 任意层级的 App.xaml（应用入口资源定义）
#   - 其余 .xaml（如 Views/DesktopWindow.xaml、MainWindow.xaml）视为宿主夹带业务 UI，即红
# 依据: ADR-003 D2（桌面主窗口与背景归 shell.desktop 插件）；宿主只装配插件、不写业务 UI
# 单测: verify-architecture-guard.Tests.ps1（Pester）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：返回 hostDir 下除 App.xaml 之外的业务 .xaml 绝对路径
function Get-BusinessXamlPaths([string]$HostDir) {
    $xaml = @(Get-ChildItem $HostDir -Recurse -Filter '*.xaml' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
    return @($xaml | Where-Object { $_.Name -ne 'App.xaml' } | ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) })
}

# 门禁主体（dot-source 时跳过，供单测仅加载函数）
if ($MyInvocation.InvocationName -ne '.') {
    $hostDir = Join-Path (Get-RepoRoot) 'host'
    # 豁免清单（ADR-003 D2）：SplashWindow 为宿主启动画面（插件加载前须由宿主展示），只减不增
    $allowed = @('host\Views\SplashWindow.xaml')
    $businessXaml = @(Get-BusinessXamlPaths $hostDir | Where-Object { (Get-RelPath $_) -notin $allowed })
    if ($businessXaml.Count -gt 0) {
        $fails = @($businessXaml | ForEach-Object { "$(Get-RelPath $_) — 宿主不得携带业务 UI，该窗口应由 shell 插件提供" })
        Write-GateFail 'architecture-guard' $fails
    }
    Write-GatePass 'architecture-guard' 'host/ 下无业务 UI（App.xaml 与 ADR-003 D2 豁免项除外）'
}