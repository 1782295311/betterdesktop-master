# 门禁 host-log-sink 单测（Pester）
# 覆盖：CordisContext 构造是否挂 logSink
$sut = Join-Path $PSScriptRoot 'verify-host-log-sink.ps1'
. $sut

Describe 'Test-ContentHasLogSink' {
    It '挂 logSink 判为 true' {
        Test-ContentHasLogSink 'var context = new CordisContext(logSink: (level, msg) => {});' | Should Be $true
    }

    It '未挂 logSink 判为 false' {
        Test-ContentHasLogSink 'var context = new CordisContext();' | Should Be $false
    }

    It '其它地方出现 logSink 关键词不误判' {
        Test-ContentHasLogSink '// TODO: 接 logSink 管道' | Should Be $false
    }
}