# 门禁 boundaries 单测（Pester）
#
# 覆盖四组「非法输入 → 返回违规」：
#   B1 依赖环：A→B→A 必须被检出；DAG 必须静默
#   B2 依赖方向：kernel → surface 违规；surface → kernel 放行；层判定按路径首段
#   B3 电源红线：ES_SYSTEM_REQUIRED / PowerSetRequest 违规；ES_CONTINUOUS 放行；注释不计
#   B4 术语禁词：namespace Cairo / IExtensionService 违规；行尾溯源注释不计；decomp 豁免
#
# 【为什么每条都要有"非法输入"用例】门禁最大的失效模式不是"判错"，是"**静默什么都不判**"
#（正则写歪、目录枚举返回空、异常被吞）。只测"合法输入通过"永远发现不了这一类 ——
# 而它恰恰是最常见的（空集合让所有断言都成立）。
$sut = Join-Path $PSScriptRoot 'verify-boundaries.ps1'
. $sut

function New-TempTree {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-bnd-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    return $tmp
}

function Add-File([string]$Root, [string]$Rel, [string]$Content) {
    $full = Join-Path $Root $Rel
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $full -Value $Content -Encoding UTF8
}

Describe 'Get-BoundaryFiles（枚举与豁免）' {
    It '按扩展名过滤，且排除 bin/obj/target/backups/dist/Temp' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\a\keep.cs' 'x'
        Add-File $tmp 'packages\a\skip.txt' 'x'
        Add-File $tmp 'packages\a\bin\no.cs' 'x'
        Add-File $tmp 'packages\a\obj\no.cs' 'x'
        Add-File $tmp 'packages\a\target\no.rs' 'x'
        Add-File $tmp 'backups\snap\no.cs' 'x'
        Add-File $tmp 'dist\no.cs' 'x'
        Add-File $tmp 'Temp\no.cs' 'x'

        $got = @(Get-BoundaryFiles $tmp @('.cs'))
        $got.Count | Should Be 1
        (Split-Path -Leaf $got[0]) | Should Be 'keep.cs'

        Remove-Item -Recurse -Force $tmp
    }

    It '不存在的源码根不报错（返回空集）' {
        $tmp = New-TempTree
        @(Get-BoundaryFiles $tmp @('.cs')).Count | Should Be 0
        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Remove-LineComments（剥注释但不动行号）' {
    It '剥掉行尾注释，行数保持不变' {
        $src = "var a = 1; // Cairo`nvar b = 2;`n// Cairo`nvar c = 3;"
        $out = Remove-LineComments $src
        ($out -split "`n").Count | Should Be 4
        $out | Should Not Match 'Cairo'
        $out | Should Match 'var a = 1;'
    }
}

Describe 'Get-ProjectGraph（建图：ProjectReference + HintPath）' {
    It '相对 ProjectReference 被解析成绝对路径；含 .. 的 HintPath 也算一条边' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\a\A.csproj' @'
<Project>
  <ItemGroup>
    <ProjectReference Include="..\b\B.csproj" />
    <Reference Include="Lib"><HintPath>..\..\packages\c\bin\Lib.dll</HintPath></Reference>
  </ItemGroup>
</Project>
'@
        Add-File $tmp 'packages\b\B.csproj' '<Project />'
        Add-File $tmp 'packages\c\C.csproj' '<Project />'

        $g = Get-ProjectGraph $tmp
        $g.Count | Should Be 3
        $aKey = (Join-Path $tmp 'packages\a\A.csproj').ToLowerInvariant()
        $refs = @($g[$aKey])
        $refs.Count | Should Be 2
        @($refs | Where-Object { $_ -like '*b\b.csproj' }).Count | Should Be 1
        @($refs | Where-Object { $_ -like '*lib.dll' }).Count | Should Be 1

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Get-ProjectCycles（B1：环检测）' {
    It '两节点互引 → 检出 1 个环' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\a\A.csproj' '<Project><ItemGroup><ProjectReference Include="..\b\B.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\b\B.csproj' '<Project><ItemGroup><ProjectReference Include="..\a\A.csproj" /></ItemGroup></Project>'

        @(Get-ProjectCycles (Get-ProjectGraph $tmp)).Count | Should Be 1

        Remove-Item -Recurse -Force $tmp
    }

    It '三节点环路 → 检出；同时存在的 DAG 分支不被误报' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\a\A.csproj' '<Project><ItemGroup><ProjectReference Include="..\b\B.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\b\B.csproj' '<Project><ItemGroup><ProjectReference Include="..\c\C.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\c\C.csproj' '<Project><ItemGroup><ProjectReference Include="..\a\A.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\d\D.csproj' '<Project><ItemGroup><ProjectReference Include="..\a\A.csproj" /></ItemGroup></Project>'

        @(Get-ProjectCycles (Get-ProjectGraph $tmp)).Count | Should Be 1

        Remove-Item -Recurse -Force $tmp
    }

    It '无环图 → 0 个环（防"永远报环"的假阳性）' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\a\A.csproj' '<Project><ItemGroup><ProjectReference Include="..\b\B.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\b\B.csproj' '<Project />'

        @(Get-ProjectCycles (Get-ProjectGraph $tmp)).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Get-ProjectLayer（层判定按路径首段）' {
    It '六层映射' {
        (Get-ProjectLayer 'packages\api\a\A.csproj') | Should Be 'contract'
        (Get-ProjectLayer 'protocols\x.csproj') | Should Be 'contract'
        (Get-ProjectLayer 'packages\kernel\k\K.csproj') | Should Be 'kernel'
        (Get-ProjectLayer 'packages\shell\s\S.csproj') | Should Be 'surface'
        (Get-ProjectLayer 'core\src\main.rs') | Should Be 'engine'
        (Get-ProjectLayer 'packages\entry\host\BetterDesktop.Host.csproj') | Should Be 'entry'
        (Get-ProjectLayer 'packages\entry\cli\BetterDesktop.Cli.csproj') | Should Be 'entry'
        (Get-ProjectLayer 'tools\X\X.csproj') | Should Be 'unclassified'
    }
}

Describe 'Get-DirectionViolations（B2：只允许高层引低层）' {
    It 'kernel → surface 违规；surface → kernel 放行' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\kernel\kbad\KBad.csproj' '<Project><ItemGroup><ProjectReference Include="..\..\shell\sb\SB.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\shell\sb\SB.csproj' '<Project />'
        Add-File $tmp 'packages\shell\sgood\SGood.csproj' '<Project><ItemGroup><ProjectReference Include="..\..\kernel\kgood\KGood.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\kernel\kgood\KGood.csproj' '<Project />'

        $v = @(Get-DirectionViolations (Get-ProjectGraph $tmp) $tmp)
        $v.Count | Should Be 1
        $v[0] | Should Match 'KBad'

        Remove-Item -Recurse -Force $tmp
    }

    It 'contract → kernel 违规（契约层必须是最底的叶子）' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\api\a\A.csproj' '<Project><ItemGroup><ProjectReference Include="..\..\kernel\k\K.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\kernel\k\K.csproj' '<Project />'

        @(Get-DirectionViolations (Get-ProjectGraph $tmp) $tmp).Count | Should Be 1

        Remove-Item -Recurse -Force $tmp
    }

    It '合法的单向链（entry → surface → kernel → contract）→ 0 违规' {
        $tmp = New-TempTree
        Add-File $tmp 'packages\api\a\A.csproj' '<Project />'
        Add-File $tmp 'packages\kernel\k\K.csproj' '<Project><ItemGroup><ProjectReference Include="..\..\api\a\A.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\shell\s\S.csproj' '<Project><ItemGroup><ProjectReference Include="..\..\kernel\k\K.csproj" /></ItemGroup></Project>'
        Add-File $tmp 'packages\entry\host\H.csproj' '<Project><ItemGroup><ProjectReference Include="..\packages\shell\s\S.csproj" /></ItemGroup></Project>'

        @(Get-DirectionViolations (Get-ProjectGraph $tmp) $tmp).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }
}

Describe 'Test-PowerRedline（B3：不得持有唤醒请求）' {
    It 'ES_SYSTEM_REQUIRED / ES_DISPLAY_REQUIRED / PowerSetRequest → 违规' {
        @(Test-PowerRedline 'SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);').Count | Should Be 1
        @(Test-PowerRedline 'SetThreadExecutionState(ES_DISPLAY_REQUIRED);').Count | Should Be 1
        @(Test-PowerRedline 'var r = PowerSetRequest(h, PowerRequestSystemRequired);').Count | Should Be 1
    }

    It '【反例】只传 ES_CONTINUOUS 的"放行睡眠"复位用法必须通过' {
        @(Test-PowerRedline 'SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);').Count | Should Be 0
    }

    It '【反例】注释里讨论唤醒标志不算违规（本仓注释大量讨论这件事）' {
        @(Test-PowerRedline '// 不要调用 SetThreadExecutionState(ES_SYSTEM_REQUIRED)，那会阻止睡眠').Count | Should Be 0
    }

    It '【反例】枚举成员声明与 P/Invoke 声明不是"持有"（定义标志 ≠ 使用标志）' {
        @(Test-PowerRedline 'ES_SYSTEM_REQUIRED = 0x00000001,').Count | Should Be 0
        @(Test-PowerRedline '        ES_DISPLAY_REQUIRED = 0x00000002,').Count | Should Be 0
        @(Test-PowerRedline 'private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);').Count | Should Be 0
        @(Test-PowerRedline 'private static extern IntPtr PowerSetRequest(IntPtr h, POWER_REQUEST_TYPE t);').Count | Should Be 0
    }
}

Describe 'Test-TerminologyViolation（B4：术语禁词）' {
    It 'namespace / 类型声明 / using 里的旧仓名 → 违规' {
        @(Test-TerminologyViolation 'x\A.cs' 'namespace CairoDesktop.Shell { }').Count | Should Be 1
        @(Test-TerminologyViolation 'x\A.cs' 'public class CairoMenuService { }').Count | Should Be 1
        @(Test-TerminologyViolation 'x\A.cs' 'using CairoDesktop.Infrastructure;').Count | Should Be 1
        @(Test-TerminologyViolation 'x\A.cs' 'public interface IExtensionService { }').Count | Should Be 1
    }

    It '【反例】行尾溯源注释不算违规（"// cairoshell 同值"是正当出处说明）' {
        @(Test-TerminologyViolation 'x\A.cs' 'Margin = new Thickness(7, 13, 0, 0); // cairoshell DesktopIcons 同值').Count | Should Be 0
        @(Test-TerminologyViolation 'x\A.cs' '// 参考 Cairo.xaml 的 TaskbarDockBackground').Count | Should Be 0
    }

    It '【反例】合法名必须放行（BetterDesktop.Shell.Core 不是禁词）' {
        @(Test-TerminologyViolation 'x\A.cs' 'namespace BetterDesktop.Shell.Core;').Count | Should Be 0
        @(Test-TerminologyViolation 'x\A.cs' 'using BetterDesktop.Kernel.Core;').Count | Should Be 0
    }

    It '反编译参考副本豁免（不是我们的命名空间）' {
        @(Test-TerminologyViolation 'tools\decomp\A.cs' 'namespace CairoDesktop.Shell { }').Count | Should Be 0
    }
}

Describe 'Get-DeclaredComponentNames（B5：从 C# 抽组件名）' {
    It '抽字符串常量；剥掉注释里的示例（注释不是声明）' {
        $src = @'
public static class CoreComponents
{
    /// <summary>注释里举例 public const string X = "fake";</summary>
    public const string Shell = "shell";
    public const string IndexEngine = "index-engine"; // 行尾注释 public const string Y = "fake2";
    private const string NotPublic = "ignored";
}
'@
        $names = @(Get-DeclaredComponentNames $src)
        $names.Count | Should Be 2
        ($names -contains 'shell') | Should Be $true
        ($names -contains 'index-engine') | Should Be $true
    }

    It '空源码 → 空集（不抛）' {
        @(Get-DeclaredComponentNames '').Count | Should Be 0
    }
}

Describe 'Get-ComponentNameMismatch（B5：组件名双向对账）' {
    It 'C# 多出的名字（表里没有）→ 违规；措辞指向 unknown-component' {
        $bad = @(Get-ComponentNameMismatch @('shell') @('shell', 'typo-name'))
        $bad.Count | Should Be 1
        $bad[0] | Should Match 'typo-name'
        $bad[0] | Should Match 'unknown-component'
    }

    It '表里多出的名字（C# 没有）→ 违规；措辞指向"到达不了"' {
        $bad = @(Get-ComponentNameMismatch @('shell', 'orphan') @('shell'))
        $bad.Count | Should Be 1
        $bad[0] | Should Match 'orphan'
        $bad[0] | Should Match '到达'
    }

    It '一致 → 0 违规（防"永远报不一致"的假阳性）' {
        @(Get-ComponentNameMismatch @('a', 'b') @('b', 'a')).Count | Should Be 0
    }

    It '两侧都空 → 0 违规（由门禁主体的空集合自检负责拦，本函数只管对账）' {
        @(Get-ComponentNameMismatch @() @()).Count | Should Be 0
    }
}
