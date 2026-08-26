# 门禁 smoke-test 单测（Pester）
# 覆盖：进程存活判断
$sut = Join-Path $PSScriptRoot 'verify-smoke-test.ps1'
. $sut

Describe 'Test-ProcessAlive' {
    It '进程已退出判为失败' {
        Test-ProcessAlive $true | Should Be $false
    }

    It '进程仍存活判为通过' {
        Test-ProcessAlive $false | Should Be $true
    }
}
