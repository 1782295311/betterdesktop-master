# 门禁 gate-registry 单测（Pester）
# 覆盖：门禁单测路径推导
$sut = Join-Path $PSScriptRoot 'verify-gate-registry.ps1'
. $sut

Describe 'Get-GateTestPath' {
    It 'verify-x.ps1 推导出 verify-x.Tests.ps1' {
        Get-GateTestPath 'verify-smoke-test.ps1' | Should Be 'verify-smoke-test.Tests.ps1'
    }

    It '只替换结尾的 .ps1' {
        Get-GateTestPath 'verify-architecture-guard.ps1' | Should Be 'verify-architecture-guard.Tests.ps1'
    }
}