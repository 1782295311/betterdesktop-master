# verify-native-convergence.Tests.ps1
# Pester 测试：Native 互操作收敛门禁（先跑脚本再断言行为）。

Describe 'verify-native-convergence 门禁' {
    BeforeAll {
        $script:Gate = Join-Path $PSScriptRoot 'verify-native-convergence.ps1'
    }

    It '脚本存在且可执行' {
        $script:Gate | Should Exist
        { & $script:Gate 2>&1 } | Should Not Throw
    }

    It '返回 PASS（退出码 0）' {
        $output = & $script:Gate 2>&1
        $code = $global:LASTEXITCODE
        $code | Should Be 0
        ($output -join "`n") | Should Match 'PASS'
    }

    It '断言 DllImport 基线（≤281）' {
        $output = & $script:Gate 2>&1
        ($output -join "`n") | Should Match '总数: \d+ / 上限 281'
    }

    It '检测到超基线时退出 1（负例）' {
        $out = & $script:Gate -MaxDllImport 10 2>&1
        $code = $global:LASTEXITCODE
        $code | Should Be 1
        ($out -join "`n") | Should Match '超基线'
    }
}
