# 门禁 dotnet-format 单测（Pester）
# 覆盖：退出码判断 + 违规提取 + 新增违规判定（棘轮）
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

Describe 'Get-FormatViolations' {
    It '从输出提取 相对路径:行号 并去重排序' {
        $out = @"
C:\repo\packages\a\A.cs(10,2): error ENDOFLINE: 修复行尾标记。
C:\repo\packages\b\B.cs(3,1): error WHITESPACE: 修复空白字符。
C:\repo\packages\a\A.cs(10,5): error ENDOFLINE: 修复行尾标记。
"@
        $got = @(Get-FormatViolations $out 'C:\repo')
        $got.Count | Should Be 2
        $got[0] | Should Be 'packages/a/A.cs:10'
        $got[1] | Should Be 'packages/b/B.cs:3'
    }
}

Describe 'Get-NewViolations' {
    It '返回不在基线中的新增违规' {
        $cur = @('a.cs:1', 'b.cs:2')
        $base = @('b.cs:2')
        $got = @(Get-NewViolations $cur $base)
        $got.Count | Should Be 1
        $got[0] | Should Be 'a.cs:1'
    }

    It '全部在基线内时返回空' {
        $cur = @('a.cs:1')
        $base = @('a.cs:1', 'b.cs:2')
        $got = @(Get-NewViolations $cur $base)
        $got.Count | Should Be 0
    }
}
