# 门禁 package-readme 单测（Pester）
# 覆盖：README 合规判断（缺少 Known Limitations / 缺少列表项 / 合规）
$sut = Join-Path $PSScriptRoot 'verify-package-readme.ps1'
. $sut

Describe 'Get-PackageReadmeViolation' {
    It '缺少 README 判为违规' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-readme-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        @(Get-PackageReadmeViolation (Join-Path $tmp 'README.md')) | Should Be '缺少 README.md'
        Remove-Item -Recurse -Force $tmp
    }

    It '缺少 Known Limitations 小节判为违规' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-readme-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        Set-Content -Path (Join-Path $tmp 'README.md') -Value '# 标题' -Encoding UTF8
        Get-PackageReadmeViolation (Join-Path $tmp 'README.md') | Should Be 'README.md 缺少 ``## Known Limitations`` 小节'
        Remove-Item -Recurse -Force $tmp
    }

    It '缺乏列表项判为违规' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-readme-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        Set-Content -Path (Join-Path $tmp 'README.md') -Value "## Known Limitations`n无内容" -Encoding UTF8
        Get-PackageReadmeViolation (Join-Path $tmp 'README.md') | Should Be 'README.md 的 ``## Known Limitations`` 下必须至少一条 ``- `` 列表项'
        Remove-Item -Recurse -Force $tmp
    }

    It '合规 README 判为 null' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-readme-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        Set-Content -Path (Join-Path $tmp 'README.md') -Value "## Known Limitations`n- 已知限制" -Encoding UTF8
        Get-PackageReadmeViolation (Join-Path $tmp 'README.md') | Should Be $null
        Remove-Item -Recurse -Force $tmp
    }
}