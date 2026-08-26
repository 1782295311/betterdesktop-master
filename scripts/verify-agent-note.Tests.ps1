# 门禁 agent-note 单测（Pester）
# 覆盖：决策记录内容合规判断（header/必需小节/forbidden 小节）
$sut = Join-Path $PSScriptRoot 'verify-agent-note.ps1'
. $sut

Describe 'Get-AgentNoteContentViolations' {
    It '合规 implemented 记录返回空数组' {
        $lines = @(
            '# Agent Note: 测试',
            '',
            'Status: implemented',
            '',
            '## Problem',
            '问题',
            '## Decision',
            '决策',
            '## Alternatives considered',
            '- 备选',
            '## Consequences',
            '后果'
        )
        @(Get-AgentNoteContentViolations $lines 'implemented').Count | Should Be 0
    }

    It 'implemented 残留 ## Proposal 判为违规' {
        $lines = @(
            '# Agent Note: 测试',
            '',
            'Status: implemented',
            '',
            '## Problem',
            '问题',
            '## Proposal',
            '应该是计划'
        )
        $v = @(Get-AgentNoteContentViolations $lines 'implemented')
        ($v | Where-Object { $_ -like '*Proposal*' }).Count | Should Be 1
    }

    It '缺少必需小节判为违规' {
        $lines = @(
            '# Agent Note: 测试',
            '',
            'Status: proposed',
            '',
            '## Problem',
            '问题'
        )
        $v = @(Get-AgentNoteContentViolations $lines 'proposed')
        ($v | Where-Object { $_ -like '*缺少必需小节*' }).Count | Should BeGreaterThan 0
    }

    It 'Status 与生命周期不符判为违规' {
        $lines = @(
            '# Agent Note: 测试',
            '',
            'Status: implemented',
            ''
        )
        @(Get-AgentNoteContentViolations $lines 'proposed') | Where-Object { $_ -like '*生命周期*' } | ForEach-Object { $_ } | Out-Null
        @(Get-AgentNoteContentViolations $lines 'proposed' | Where-Object { $_ -like '*生命周期*' }).Count | Should Be 1
    }
}