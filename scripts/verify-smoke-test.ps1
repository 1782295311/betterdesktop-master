# 门禁 smoke-test：启动主程序并验证进程存活（不崩溃）
# 规则:
#   - 主程序路径: host\bin\x64\Debug\net8.0-windows10.0.19041.0\BetterDesktop.Host.exe
#   - 启动前清理遗留进程（幂等），结束后强制回收
#   - 等待 4 秒后检查进程是否仍在运行（未崩溃即绿）
# 注: 主程序为 shell 宿主（Dock/菜单栏/开始菜单/设置），smoke-test 仅验证进程存活
# 单测: verify-smoke-test.Tests.ps1（Pester，覆盖阈值判断）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 判断逻辑（可单测）：进程是否在指定时间后仍存活
function Test-ProcessAlive([bool]$HasExited) {
    return -not $HasExited
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'smoke-test'
    $exe = Join-Path (Get-RepoRoot) 'packages\entry\host\bin\x64\Debug\net8.0-windows10.0.19041.0\BetterDesktop.Host.exe'
    if (-not (Test-Path $exe)) {
        Write-GateFail 'smoke-test' @("$exe — 主程序不存在，请先 ``dotnet build BetterDesktop.slnx``（Debug/x64）")
    }

    # 清理遗留进程
    Get-Process -Name 'BetterDesktop.Host' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    Start-Sleep -Seconds 4

    # 在回收前记录进程状态
    $exited = $proc.HasExited

    # 回收
    if (-not $exited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-ProcessAlive $exited)) {
        Write-GateFail 'smoke-test' @("主程序在 4 秒内崩溃（ExitCode=$($proc.ExitCode)），内核初始化可能失败")
    }
    Write-GatePass 'smoke-test' "主程序启动正常，进程存活 4 秒未崩溃"
}
