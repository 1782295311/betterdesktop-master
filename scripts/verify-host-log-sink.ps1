# 门禁 host-log-sink：宿主必须为 CordisContext 挂日志 sink，杜绝静默吞异常
# 规则:
#   - 检查 host/Bootstrap.cs 中 CordisContext 构造是否传入 logSink
#   - 未挂 sink 时内核日志（含插件加载失败）会被静默丢弃，运行期问题无法诊断，即红
# 依据: KernelLogger 默认无 sink（v1 已知限制）；启动期失败必须可观测
# 单测: verify-host-log-sink.Tests.ps1（Pester）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：CordisContext 构造文本是否携带 logSink
function Test-ContentHasLogSink([string]$Content) {
    return [bool]($Content -match 'new\s+CordisContext\s*\([^)]*logSink')
}

# 门禁主体（dot-source 时跳过，供单测仅加载函数）
if ($MyInvocation.InvocationName -ne '.') {
    $bootstrap = Join-Path (Get-RepoRoot) 'host\Bootstrap.cs'
    if (-not (Test-Path $bootstrap)) {
        Write-GateFail 'host-log-sink' @('host/Bootstrap.cs — 引导文件不存在')
    }
    $content = Get-Content $bootstrap -Raw
    if (-not (Test-ContentHasLogSink $content)) {
        Write-GateFail 'host-log-sink' @('host/Bootstrap.cs — CordisContext 未挂日志 sink，插件加载失败会被静默吞掉，无法定位运行期故障')
    }
    Write-GatePass 'host-log-sink' '宿主已为 CordisContext 挂日志 sink'
}