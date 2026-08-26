# 门禁 test-coverage 单测（Pester）
# 覆盖：覆盖率阈值判断
$sut = Join-Path $PSScriptRoot 'verify-test-coverage.ps1'
. $sut

Describe 'Test-CoverageRate' {
    It '实际覆盖率高于基线判为通过' {
        Test-CoverageRate 0.85 0.80 | Should Be $true
    }

    It '实际覆盖率等于基线判为通过' {
        Test-CoverageRate 0.80 0.80 | Should Be $true
    }

    It '实际覆盖率低于基线判为失败' {
        Test-CoverageRate 0.75 0.80 | Should Be $false
    }
}