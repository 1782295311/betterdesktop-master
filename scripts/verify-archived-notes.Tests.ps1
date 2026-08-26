# 门禁 archived-notes 单测（Pester）
# 覆盖：哈希比对
$sut = Join-Path $PSScriptRoot 'verify-archived-notes.ps1'
. $sut

Describe 'Test-SealMatches' {
    It '哈希一致判为匹配' {
        Test-SealMatches 'abc123' 'abc123' | Should Be $true
    }

    It '哈希不一致判为不匹配' {
        Test-SealMatches 'abc123' 'def456' | Should Be $false
    }
}