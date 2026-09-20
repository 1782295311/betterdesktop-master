# 门禁 architecture-guard 单测（Pester）
# 覆盖：
#   R0 拒绝业务 UI / 豁免 App.xaml / 排除 obj、bin
#   R1-R3 边界棘轮：命中判定、未登记→违规、已登记→通过、条目失效→报红、排除目录豁免
$sut = Join-Path $PSScriptRoot 'verify-architecture-guard.ps1'
. $sut

# 构造临时仓库树（返回根路径）
function New-TempTree {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    return $tmp
}

function Add-File([string]$Root, [string]$Rel, [string]$Content) {
    $full = Join-Path $Root $Rel
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $full -Value $Content -Encoding UTF8
}

Describe 'Get-BusinessXamlPaths' {
    It '返回业务 UI，豁免 App.xaml' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $tmp 'Views') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'App.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'Views\DesktopWindow.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'Views\MainWindow.xaml') -Force | Out-Null

        $result = @(Get-BusinessXamlPaths $tmp)
        @($result | Where-Object { $_ -like '*DesktopWindow.xaml' }).Count | Should Be 1
        @($result | Where-Object { $_ -like '*MainWindow.xaml' }).Count | Should Be 1
        @($result | Where-Object { $_ -like '*App.xaml' }).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It '排除 obj、bin 下的 .xaml' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $tmp 'obj') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $tmp 'bin') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'App.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'obj\Foo.g.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'bin\Bar.xaml') -Force | Out-Null

        @(Get-BusinessXamlPaths $tmp).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It '空目录返回空数组' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null

        @(Get-BusinessXamlPaths $tmp).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Get-RuleMatchedFiles（边界棘轮的命中判定）' {
    It '两个标记同时出现才算命中（只有一个不命中）' {
        $tmp = New-TempTree
        Add-File $tmp 'both.cs'  'var p = Process.Start("BetterDesktop.Host.exe");'
        Add-File $tmp 'onlylaunch.cs' 'var p = Process.Start("notepad.exe");'
        Add-File $tmp 'onlyname.cs' 'const string n = "BetterDesktop.Host.exe";'

        $hit = @(Get-RuleMatchedFiles $tmp 'lifecycle-owner')
        $hit.Count | Should Be 1
        (Split-Path -Leaf $hit[0]) | Should Be 'both.cs'

        Remove-Item -Recurse -Force $tmp
    }

    It 'RegisterHotKey 命中；settings 写入需同时含 settings.json 与写标记' {
        $tmp = New-TempTree
        Add-File $tmp 'hot.cs' 'RegisterHotKey(h, 1, 0, 0x41);'
        # 两个标记都必须在**代码**里（字符串字面量算代码）
        Add-File $tmp 'write.cs' 'File.WriteAllText(Path.Combine(dir, "settings.json"), x);'
        Add-File $tmp 'readonlymention.cs' 'var p = Path.Combine(dir, "settings.json");'

        @(Get-RuleMatchedFiles $tmp 'hotkey-registrar').Count | Should Be 1
        $sw = @(Get-RuleMatchedFiles $tmp 'settings-writer')
        $sw.Count | Should Be 1
        (Split-Path -Leaf $sw[0]) | Should Be 'write.cs'

        Remove-Item -Recurse -Force $tmp
    }

    It '注释里提到标识符**不算**命中（注释往往是在解释"为什么不该这么写"）' {
        $tmp = New-TempTree
        Add-File $tmp 'onlycomment.cs' '// 注意：core 不得写 settings.json，也不要 Process.Start("BetterDesktop.Host.exe")'
        Add-File $tmp 'mixed.cs' @'
// 上面这行注释提到 Process.Start("BetterDesktop.Host.exe")
const string name = "BetterDesktop.Host.exe";
'@

        @(Get-RuleMatchedFiles $tmp 'lifecycle-owner').Count | Should Be 0
        @(Get-RuleMatchedFiles $tmp 'settings-writer').Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It '豁免目录（bin/obj/target/backups）不参与判定' {
        $tmp = New-TempTree
        Add-File $tmp 'bin\x.cs' 'Process.Start("BetterDesktop.Host.exe");'
        Add-File $tmp 'obj\x.cs' 'Process.Start("BetterDesktop.Host.exe");'
        Add-File $tmp 'backups\snapshot\x.cs' 'Process.Start("BetterDesktop.Host.exe");'

        @(Get-RuleMatchedFiles $tmp 'lifecycle-owner').Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It 'Rust 的 spawn 原语也算命中（原先只认 C# 口径，core 拉起 CLI 会整条绕过棘轮）' {
        $tmp = New-TempTree
        Add-File $tmp 'x.rs' 'let ok = crate::process::spawn_detached(&exe, Some("--shellmenu-register")); const N: &str = "BetterDesktop.Cli.exe";'

        $hit = @(Get-RuleMatchedFiles $tmp 'lifecycle-owner')
        $hit.Count | Should Be 1

        Remove-Item -Recurse -Force $tmp
    }

    It '【回归钉子】三段 exe 名同样命中（旧口径只认两段，三段名对规则全隐形）' {
        $tmp = New-TempTree
        # 2026-09-20：`BetterDesktop\.[A-Za-z][A-Za-z0-9]*\.exe` 匹配不了 BetterDesktop.Index.Engine.exe，
        # 于是 shell-index-ipc/IndexEngineLauncher.cs 这个**真实的第二生命周期所有者**从未被本规则看到
        #（真机探针先抓到：该引擎实例的父进程不是 core）。这条用例把"必须允许多段"钉住。
        Add-File $tmp 'multi.cs' 'IndexEngineLauncher.StartDetached(@"C:\x\BetterDesktop.Index.Engine.exe", log);'
        Add-File $tmp 'multi2.cs' 'var p = Process.Start("BetterDesktop.Clipboard.Panel.exe");'
        Add-File $tmp 'two.cs' 'var p = Process.Start("BetterDesktop.Host.exe");'

        $hit = @(Get-RuleMatchedFiles $tmp 'lifecycle-owner')
        $hit.Count | Should Be 3

        Remove-Item -Recurse -Force $tmp
    }

    It '只扫 .cs 与 .rs，不扫其它扩展名' {
        $tmp = New-TempTree
        Add-File $tmp 'x.md' 'Process.Start("BetterDesktop.Host.exe");'
        Add-File $tmp 'x.ps1' 'Process.Start("BetterDesktop.Host.exe");'
        # 两个标记都必须在代码里（注释会被剥掉）
        Add-File $tmp 'x.rs' 'unsafe { CreateProcessW(...) } let n = "BetterDesktop.Host.exe";'

        $hit = @(Get-RuleMatchedFiles $tmp 'lifecycle-owner')
        $hit.Count | Should Be 1
        (Split-Path -Leaf $hit[0]) | Should Be 'x.rs'

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Test-AllowEntryMatch（清单条目匹配）' {
    It '以 / 或 \ 结尾 = 目录前缀匹配' {
        (Test-AllowEntryMatch 'packages/entry/launcher/Services/CliRunner.cs' 'packages/entry/launcher/Services/') | Should Be $true
        (Test-AllowEntryMatch 'packages/entry/launcher\Services\CliRunner.cs' 'packages/entry/launcher/Services/') | Should Be $true
        (Test-AllowEntryMatch 'launcher\Services\CliRunner.cs' 'launcher\Services\') | Should Be $true
        (Test-AllowEntryMatch 'packages/entry/launcher/Other.cs' 'packages/entry/launcher/Services/') | Should Be $false
    }

    It '其余 = 精确匹配文件路径，且分隔符不敏感' {
        (Test-AllowEntryMatch 'packages\entry\host\Bootstrap.cs' 'packages\entry\host\Bootstrap.cs') | Should Be $true
        (Test-AllowEntryMatch 'packages\entry\host\Bootstrap.cs' 'packages/entry/host/Bootstrap.cs') | Should Be $true
        (Test-AllowEntryMatch 'packages\entry\host\Bootstrap.cs' 'packages\entry\host\BootstrapX.cs') | Should Be $false
    }

    It '前缀不误伤同名前缀目录（launcher/Services/ 不匹配 launcher/ServicesExtra/）' {
        (Test-AllowEntryMatch 'packages/entry/launcher/ServicesExtra/x.cs' 'packages/entry/launcher/Services/') | Should Be $false
    }
}

Describe 'Test-CoreNameViolation（R4：只有裸 BetterDesktop.Core 才算违规）' {
    It '裸名命中（含大小写不同与 .X 后缀）' {
        (Test-CoreNameViolation 'BetterDesktop.Core') | Should Be $true
        (Test-CoreNameViolation 'BetterDesktop.Core.dll') | Should Be $true
        (Test-CoreNameViolation 'betterdesktop.core') | Should Be $true
        (Test-CoreNameViolation 'BetterDesktop.Core.UI') | Should Be $true   # 宁可报红让人显式登记
    }

    It '【反例】带前缀的合法名与 Rust 名必须通过 —— 防止未来有人把规则"加强"成后缀匹配' {
        (Test-CoreNameViolation 'BetterDesktop.Shell.Core') | Should Be $false
        (Test-CoreNameViolation 'BetterDesktop.Shell.Core.dll') | Should Be $false
        (Test-CoreNameViolation 'BetterDesktop.Kernel.Core') | Should Be $false
        (Test-CoreNameViolation 'betterdesktop-core') | Should Be $false
        (Test-CoreNameViolation 'betterdesktop-core.exe') | Should Be $false
        (Test-CoreNameViolation 'BetterDesktop.CoreX') | Should Be $false    # 无分隔点 ⇒ 不是同一个名
    }
}

Describe 'Get-CoreNameViolations（R4：目录名 / 文件名 / AssemblyName 三处都查）' {
    It '三处命中；Shell.Core、Rust 名、构建产物与历史快照放行' {
        $tmp = New-TempTree
        New-Item -ItemType Directory -Path (Join-Path $tmp 'BetterDesktop.Core') -Force | Out-Null
        Add-File $tmp 'a\BetterDesktop.Core.dll' 'x'
        Add-File $tmp 'b\BetterDesktop.Shell.Core.dll' 'x'
        Add-File $tmp 'c\betterdesktop-core.exe' 'x'
        Add-File $tmp 'd\Odd.csproj' '<AssemblyName>BetterDesktop.Core</AssemblyName>'
        Add-File $tmp 'e\Fine.csproj' '<AssemblyName>BetterDesktop.Shell.Core</AssemblyName>'
        Add-File $tmp 'backups\snap\BetterDesktop.Core.dll' 'x'
        Add-File $tmp 'bin\BetterDesktop.Core.dll' 'x'

        $v = @(Get-CoreNameViolations $tmp)
        $v.Count | Should Be 3
        @($v | Where-Object { $_ -like '*Shell.Core*' }).Count | Should Be 0
        @($v | Where-Object { $_ -like '*betterdesktop-core*' }).Count | Should Be 0
        @($v | Where-Object { $_ -like '*backups*' }).Count | Should Be 0
        @($v | Where-Object { $_ -like '*\bin\*' -or $_ -like 'bin\*' }).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Get-BoundaryViolations（棘轮：违规 + 失效条目）' {
    BeforeEach {
        $script:Manifest = [pscustomobject]@{
            rules = [pscustomobject]@{
                'lifecycle-owner' = [pscustomobject]@{
                    allowed = @(
                        [pscustomobject]@{ path = 'packages/entry/launcher/Services/'; why = 'x'; removeBy = 'n/a' },
                        [pscustomobject]@{ path = 'packages\entry\host\Bootstrap.cs'; why = 'x'; removeBy = 'S6' }
                    )
                }
            }
        }
    }

    It '未登记的命中文件 → 违规；同时未被覆盖的条目 → 失效' {
        $tmp = New-TempTree
        # packages/entry/launcher/Services/ 被覆盖 → 不算失效；host\Bootstrap.cs 未被命中 → 失效；tray/… 未登记 → 违规
        $matched = @(
            (Join-Path $tmp 'packages\entry\launcher\Services\CliRunner.cs'),
            (Join-Path $tmp 'tray\ProcessBridge.cs')
        )
        $res = Get-BoundaryViolations -Root $tmp -Manifest $script:Manifest -RuleId 'lifecycle-owner' -MatchedFiles $matched
        $res.Violations.Count | Should Be 1
        $res.Violations[0] | Should Be 'tray\ProcessBridge.cs'
        $res.Stale.Count | Should Be 1
        $res.Stale[0] | Should Be 'packages\entry\host\Bootstrap.cs'
        Remove-Item -Recurse -Force $tmp
    }

    It '已登记的命中文件 → 无违规' {
        $tmp = New-TempTree
        $matched = @((Join-Path $tmp 'packages\entry\launcher\Services\CliRunner.cs'), (Join-Path $tmp 'packages\entry\host\Bootstrap.cs'))
        $res = Get-BoundaryViolations -Root $tmp -Manifest $script:Manifest -RuleId 'lifecycle-owner' -MatchedFiles $matched
        $res.Violations.Count | Should Be 0
        $res.Stale.Count | Should Be 0
        Remove-Item -Recurse -Force $tmp
    }

    It '已登记但现实中不再命中 → 失效条目（强制收缩清单）' {
        $tmp = New-TempTree
        $matched = @((Join-Path $tmp 'packages\entry\launcher\Services\CliRunner.cs'))
        $res = Get-BoundaryViolations -Root $tmp -Manifest $script:Manifest -RuleId 'lifecycle-owner' -MatchedFiles $matched
        $res.Violations.Count | Should Be 0
        $res.Stale.Count | Should Be 1
        $res.Stale[0] | Should Be 'packages\entry\host\Bootstrap.cs'
        Remove-Item -Recurse -Force $tmp
    }

    It '规则节点缺失 → 全部命中都算违规（清单没写就视为未登记）' {
        $tmp = New-TempTree
        $matched = @((Join-Path $tmp 'anything.cs'))
        $res = Get-BoundaryViolations -Root $tmp -Manifest $script:Manifest -RuleId 'settings-writer' -MatchedFiles $matched
        $res.Violations.Count | Should Be 1
        Remove-Item -Recurse -Force $tmp
    }
}
