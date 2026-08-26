# 门禁 dotnet-format 单测（Pester）
# 覆盖：退出码判断
$sut = Join-Path $PSScriptRoot 'verify-dotnet-format.ps1'
. $sut

Describe 'Test-FormatResult' {
    It '退出码 0 判为格式一致' {
        Test-FormatResult 0 | Should Be $true
    }

    It '退出码非 0 判为有差异' {
        Test-FormatResult 1 | Should Be $false
    }
}