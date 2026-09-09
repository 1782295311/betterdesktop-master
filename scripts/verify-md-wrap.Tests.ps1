# 门禁 md-wrap 单测（Pester）
# 覆盖：块分类（散文 = 违规，列表/标题/引用 = 豁免）与硬换行检测
$sut = Join-Path $PSScriptRoot 'verify-md-wrap.ps1'
. $sut

Describe 'Test-HardWrapBlock' {
    It '多行散文段落判为硬换行违规' {
        Test-HardWrapBlock @('第一行散文', '第二行散文') | Should Be $true
    }

    It '列表块豁免' {
        Test-HardWrapBlock @('- 项一', '- 项二') | Should Be $false
    }

    It '标题块豁免' {
        Test-HardWrapBlock @('# 标题一', '## 标题二') | Should Be $false
    }

    It '单行块不违规' {
        Test-HardWrapBlock @('一行散文') | Should Be $false
    }
}

Describe 'Get-HardWrappedParagraphCount' {
    It '围栏代码块内部硬换行不判违规' {
        $bt = [char]96
        $content = "$bt$bt$bt`n第一行`n第二行`n$bt$bt$bt"
        Get-HardWrappedParagraphCount $content | Should Be 0
    }

    It '代码块内空行不截断围栏' {
        $bt = [char]96
        $content = "$bt$bt$bt`n第一行`n`n第三行`n$bt$bt$bt"
        Get-HardWrappedParagraphCount $content | Should Be 0
    }

    It '围栏闭合后的散文仍正常检测' {
        $bt = [char]96
        $content = "$bt$bt$bt`n代码`n$bt$bt$bt`n`n第一段第一行`n第一段第二行"
        Get-HardWrappedParagraphCount $content | Should BeGreaterThan 0
    }

    It '含硬换行段落的内容判为 >= 1' {
        $content = "第一段第一行`n第一段第二行`n`n- 列表项"
        Get-HardWrappedParagraphCount $content | Should BeGreaterThan 0
    }

    It '合规内容判为 0' {
        $content = "一段一行`n`n- 列表项"
        Get-HardWrappedParagraphCount $content | Should Be 0
    }
}