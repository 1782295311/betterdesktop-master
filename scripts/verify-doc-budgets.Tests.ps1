# 门禁 doc-budgets 单测（Pester）
# 覆盖：词数统计口径 + 预算判断
$sut = Join-Path $PSScriptRoot 'verify-doc-budgets.ps1'
. $sut

Describe 'Get-DocWordCount' {
    It 'CJK 汉字各计 1 词' {
        Get-DocWordCount '测试文档' | Should Be 4
    }

    It '拉丁字母/数字连续串各计 1 词' {
        Get-DocWordCount 'abc 123 test' | Should Be 3
    }

    It '混合中英文按口径计数' {
        Get-DocWordCount '文 abc 档 123' | Should Be 4
    }
}

Describe 'Test-DocBudget' {
    It '在预算内判为通过' {
        Test-DocBudget '短文本' 10 | Should Be $true
    }

    It '超出预算判为失败' {
        Test-DocBudget '这是一段非常非常长的文本内容，远超预算上限' 5 | Should Be $false
    }
}