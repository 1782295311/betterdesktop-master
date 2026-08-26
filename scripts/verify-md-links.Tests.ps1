# 门禁 md-links 单测（Pester）
# 覆盖：标题到 slug 的归一化
$sut = Join-Path $PSScriptRoot 'verify-md-links.ps1'
. $sut

Describe 'Get-Slug' {
    It '空格转连字符并转小写' {
        Get-Slug 'My Heading' | Should Be 'my-heading'
    }

    It '去除特殊字符' {
        Get-Slug 'What is *this*?' | Should Be 'what-is-this'
    }

    It '保留中文字符' {
        Get-Slug '标题 测试' | Should Be '标题-测试'
    }
}