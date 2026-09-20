# 门禁 protocol-contract 单测（Pester）
# 覆盖：向量结构校验能拒绝各类非法输入（非法 kind / reason / generate / 重名 / 用例过少 / 二选一违规 / 缺字段）
#       maxMessageBytes 常量抽取（Rust 下划线分隔 / C# / 缺失时返回 null）
#       仓库内真实向量本身合法
#
# 【夹具为何走 JSON 往返】`"input": ""`（空输入用例）与"字段缺失"在 JSON 里语义不同，
# 而 PowerShell 的 [pscustomobject]@{ input = $null } 会把 $null 强制成 ''，
# 无法表达"字段缺失" → 用 ConvertTo-Json/ConvertFrom-Json 往返得到与门禁看到的一致的对象。
$sut = Join-Path $PSScriptRoot 'verify-protocol-contract.ps1'
. $sut

function New-Vectors([object[]]$Cases) {
    $doc = @{ schema = 1; maxMessageBytes = 1048576; cases = $Cases }
    return ($doc | ConvertTo-Json -Depth 8) | ConvertFrom-Json
}

# 一条合法 legacy 用例（hashtable 只放需要的键，缺省即"字段缺失"）
function Ok-Case([string]$Name) {
    return @{ name = $Name; input = 'BDMC1|convert|x'; expect = @{ kind = 'legacy'; head = 'convert'; arg = 'x' } }
}

function Ok-Cases([int]$Count, [int]$StartAt = 0) {
    $list = @()
    for ($i = $StartAt; $i -lt ($StartAt + $Count); $i++) { $list += (Ok-Case "ok$i") }
    return $list
}

Describe 'Get-DeclaredMaxBytes' {
    It '抽取 Rust 常量（下划线分隔的数字）' {
        $text = 'pub const MAX_MESSAGE_BYTES: usize = 1_048_576;'
        Get-DeclaredMaxBytes $text 'rust' | Should Be 1048576
    }

    It '抽取 C# 常量' {
        $text = 'public const int MaxMessageBytes = 1_048_576;'
        Get-DeclaredMaxBytes $text 'csharp' | Should Be 1048576
    }

    It '缺失或改名时返回 null（而不是静默当 0）' {
        Get-DeclaredMaxBytes 'nothing here' 'rust' | Should Be $null
        Get-DeclaredMaxBytes 'nothing here' 'csharp' | Should Be $null
    }
}

Describe 'Test-VectorShape' {
    It '10 条合法用例 → 无问题' {
        $problems = @(Test-VectorShape (New-Vectors (Ok-Cases 10)))
        ($problems -join '; ') | Should Be ''
    }

    It '用例不足 10 条 → 报问题' {
        $problems = @(Test-VectorShape (New-Vectors (Ok-Cases 1)))
        ($problems -join ' ') | Should Match '少于 10'
    }

    It '非法 kind → 报问题' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'badkind'; input = 'x'; expect = @{ kind = 'weird'; head = 'h'; arg = 'a' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match 'badkind'
    }

    It 'invalid 用例的非法/缺失 reason → 报问题' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'badreason'; input = 'x'; expect = @{ kind = 'invalid'; reason = 'nope' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match 'reason.*不在白名单'

        $cases2 = Ok-Cases 9
        $cases2 += @{ name = 'noreason'; input = 'x'; expect = @{ kind = 'invalid' } }
        (Test-VectorShape (New-Vectors $cases2)) -join ' ' | Should Match '必须给非空 reason'
    }

    It '非法 generate → 报问题' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'badgen'; generate = 'explode'; expect = @{ kind = 'invalid'; reason = 'empty' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match 'badgen'
    }

    It '用例名重复 → 报问题' {
        $cases = Ok-Cases 9
        $cases += (Ok-Case 'ok0')
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match '重复'
    }

    It 'input 与 generate 同时存在 / 同时缺失 → 报问题' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'both'; input = 'x'; generate = 'oversize'; expect = @{ kind = 'invalid'; reason = 'empty' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match '二选一'

        $cases2 = Ok-Cases 9
        $cases2 += @{ name = 'neither'; expect = @{ kind = 'invalid'; reason = 'empty' } }
        (Test-VectorShape (New-Vectors $cases2)) -join ' ' | Should Match '二选一'
    }

    It '非 invalid 用例给了 reason → 报问题' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'redundant'; input = 'x'; expect = @{ kind = 'legacy'; head = 'h'; arg = 'a'; reason = 'empty' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match '不应给 reason'
    }

    It '合法用例缺 head / 缺 arg 字段 → 报问题；arg 为空串合法' {
        $cases = Ok-Cases 8
        $cases += @{ name = 'nohead'; input = 'x'; expect = @{ kind = 'legacy'; arg = 'a' } }
        (Test-VectorShape (New-Vectors $cases)) -join ' ' | Should Match '必须给非空 head'

        $cases2 = Ok-Cases 8
        $cases2 += @{ name = 'noarg'; input = 'x'; expect = @{ kind = 'legacy'; head = 'h' } }
        (Test-VectorShape (New-Vectors $cases2)) -join ' ' | Should Match '必须给 arg 字段'

        $cases3 = Ok-Cases 9
        $cases3 += @{ name = 'emptyarg'; input = 'x'; expect = @{ kind = 'legacy'; head = 'h'; arg = '' } }
        (Test-VectorShape (New-Vectors $cases3)) -join '; ' | Should Be ''
    }

    It '空串 input 是合法边界用例（不得被当成字段缺失）' {
        $cases = Ok-Cases 9
        $cases += @{ name = 'emptyinput'; input = ''; expect = @{ kind = 'invalid'; reason = 'empty' } }
        (Test-VectorShape (New-Vectors $cases)) -join '; ' | Should Be ''
    }

    It 'schema 与 maxMessageBytes 非法 → 报问题' {
        $bad = New-Vectors (Ok-Cases 9)
        $bad.schema = 2
        $bad.maxMessageBytes = 0
        (Test-VectorShape $bad) -join ' ' | Should Match 'schema'
        (Test-VectorShape $bad) -join ' ' | Should Match 'maxMessageBytes'
    }
}

Describe '仓库内共享向量本身合法' {
    It 'protocols/bdmc1-test-vectors.json 通过结构校验' {
        $path = Join-Path (Get-RepoRoot) 'protocols\bdmc1-test-vectors.json'
        Test-Path $path | Should Be $true
        $vectors = Get-Content $path -Raw | ConvertFrom-Json
        $problems = @(Test-VectorShape $vectors)
        ($problems -join '; ') | Should Be ''
    }
}
