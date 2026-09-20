# 门禁 security 单测（Pester）
# 契约（scripts/AGENTS.md 第 5 条）：每条门禁必须覆盖「非法输入 → 返回违规」。
# 本文件逐条给出**反例**（应报违规）与**正例**（应放过），因为安全门禁最大的失败模式不是漏检，
# 而是**假红太多被绕过** —— 假红只能靠正例来钉住。
$sut = Join-Path $PSScriptRoot 'verify-security.ps1'
. $sut

Describe 'Test-PipeBoundedRead（规则1 管道有界读）' {
    It '服务端管道无有界读 → 违规' {
        $code = 'using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);'
        (Test-PipeBoundedRead $code).Count | Should Be 1
    }

    It '服务端管道走 BoundedPipeLine → 通过' {
        $code = @'
using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
var outcome = BoundedPipeLine.TryRead(server, 1024, 2000, out var line);
'@
        (Test-PipeBoundedRead $code).Count | Should Be 0
    }

    It '只有客户端管道 → 不适用本规则' {
        (Test-PipeBoundedRead 'var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);').Count | Should Be 0
    }
}

Describe 'Test-ProcessStartArgs（规则2 参数不拼接）' {
    It '非壳语义下 Arguments 用插值 → 违规' {
        $code = @'
var psi = new ProcessStartInfo(cliExe)
{
    UseShellExecute = false,
    Arguments = $"--output {userPath}",
};
'@
        (Test-ProcessStartArgs $code).Count | Should Be 1
    }

    It '非壳语义下 Arguments 用字符串加法 → 违规' {
        $code = @'
var psi = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    Arguments = "/c " + command
};
'@
        (Test-ProcessStartArgs $code).Count | Should Be 1
    }

    It '两参字符串重载拼接 → 违规' {
        (Test-ProcessStartArgs 'Process.Start("cmd.exe", "/c " + cmd);').Count | Should Be 1
    }

    It '壳语义（UseShellExecute=true）用插值 → 通过（参数本就交给 shell 解析）' {
        $code = @'
var psi = new ProcessStartInfo("explorer.exe")
{
    Arguments = $"\"{path}\"",
    UseShellExecute = true
};
'@
        (Test-ProcessStartArgs $code).Count | Should Be 0
    }

    It '改用 ArgumentList → 通过' {
        $code = @'
var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
psi.ArgumentList.Add("--output");
psi.ArgumentList.Add(userPath);
'@
        (Test-ProcessStartArgs $code).Count | Should Be 0
    }

    It '显式豁免注释 → 通过' {
        $code = @'
var psi = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    Arguments = $"--flag {value}" // SECURITY-EXEMPT: 值来自编译期常量
};
'@
        (Test-ProcessStartArgs $code).Count | Should Be 0
    }
}

Describe 'Test-JsonMaxDepth（规则3 反序列化限深）' {
    It '外部输入点无 MaxDepth → 硬红线' {
        $r = @(Test-JsonMaxDepth 'packages/shell/shell-index-ipc/IndexIpcClient.cs' 'JsonSerializer.Deserialize<X>(json);')
        $r.Count | Should Be 1
        ($r -join ' ') | Should Match '外部输入'
    }

    It '外部输入点有 MaxDepth → 通过' {
        $r = Test-JsonMaxDepth 'packages/shell/shell-index-ipc/IndexIpcClient.cs' 'new JsonSerializerOptions { MaxDepth = 32 }; JsonSerializer.Deserialize<X>(json);'
        $r.Count | Should Be 0
    }

    It '本地配置文件无 MaxDepth → 棘轮（非硬红线）' {
        $r = @(Test-JsonMaxDepth 'packages/shell/shell-dock/Services/AppGroupStore.cs' 'JsonSerializer.Deserialize<X>(json);')
        $r.Count | Should Be 1
        ($r -join ' ') | Should Not Match '外部输入'
    }

    It '不涉及 JSON 解析的文件 → 不适用' {
        (Test-JsonMaxDepth 'packages/entry/host/X.cs' 'var x = 1;').Count | Should Be 0
    }
}

Describe 'Test-RelativeNativeLoad（规则4 原生加载路径）' {
    It 'LoadLibrary 用相对名字面量 → 违规' {
        (Test-RelativeNativeLoad 'var m = LoadLibrary("helper.dll");').Count | Should Be 1
    }

    It 'NativeLibrary.TryLoad 相对名 → 违规' {
        (Test-RelativeNativeLoad 'NativeLibrary.TryLoad("core.dll", out var h);').Count | Should Be 1
    }

    It 'LoadLibrary 用绝对路径变量 → 通过' {
        (Test-RelativeNativeLoad 'var m = LoadLibrary(Path.Combine(AppContext.BaseDirectory, "helper.dll"));').Count | Should Be 0
    }

    It '注释里提到相对名 → 通过' {
        (Test-RelativeNativeLoad '// 否则子进程 LoadLibrary("helper.dll") 会失败').Count | Should Be 0
    }
}

Describe 'Test-PlaintextSecret（规则5 明文密钥）' {
    It 'apiKey 为长字面量 → 违规' {
        (Test-PlaintextSecret 'private const string ApiKey = "0123456789abcdef";').Count | Should Be 1
    }

    It 'token 为长字面量 → 违规' {
        (Test-PlaintextSecret 'var authToken = "abcdefghijklmnopqrst";').Count | Should Be 1
    }

    It '密钥来自读取而非字面量 → 通过' {
        (Test-PlaintextSecret 'var apiKey = SecretStore.Load("music");').Count | Should Be 0
    }
}

Describe 'Test-EmptyCatch（规则6 空 catch）' {
    It '真正空的 catch → 违规' {
        (Test-EmptyCatch 'try { A(); } catch { }').Count | Should Be 1
    }

    It '跨行空 catch → 违规' {
        $code = @'
try
{
    A();
}
catch (Exception)
{
}
'@
        (Test-EmptyCatch $code).Count | Should Be 1
    }

    It 'catch 块内只有注释 → 通过（作者已说明为何忽略）' {
        $code = @'
try
{
    A();
}
catch (Exception)
{
    // 尽力而为：失败不影响主流程
}
'@
        (Test-EmptyCatch $code).Count | Should Be 0
    }

    It '注释里讨论 catch { } → 通过（不得因注释假红）' {
        (Test-EmptyCatch '// 不要写 catch { } 吞异常').Count | Should Be 0
    }

    It 'catch 块内有语句 → 通过' {
        (Test-EmptyCatch 'try { A(); } catch (Exception ex) { Log(ex); }').Count | Should Be 0
    }
}

Describe 'Test-NativeHardening（规则7 原生编译加固）' {
    It '三项齐全 → 通过' {
        $c = 'set(X /guard:cf) target_link_options(y PRIVATE /DYNAMICBASE /NXCOMPAT)'
        (Test-NativeHardening $c).Count | Should Be 0
    }

    It '缺 /guard:cf → 违规' {
        $r = @(Test-NativeHardening 'target_link_options(y PRIVATE /DYNAMICBASE /NXCOMPAT)')
        $r.Count | Should Be 1
        ($r -join ' ') | Should Match 'guard:cf'
    }

    It '缺 /NXCOMPAT → 违规' {
        (Test-NativeHardening '/guard:cf /DYNAMICBASE').Count | Should Be 1
    }
}

Describe '棘轮基线辅助函数' {
    It '键缺失时返回空集合（而非含 $null 的数组 —— 那会造出幽灵条目）' {
        $map = @{}
        (Get-BaselineSet $map 'json-maxdepth').Count | Should Be 0
    }

    It '键存在时返回条目' {
        $map = @{ 'json-maxdepth' = @('a.cs', 'b.cs') }
        (Get-BaselineSet $map 'json-maxdepth').Count | Should Be 2
    }
}

Describe 'Test-IsTestPath（测试路径判定）' {
    It '测试工程目录 → true' {
        (Test-IsTestPath 'packages/kernel/kernel-tests/Foo.cs') | Should Be $true
    }

    It 'shell-*-tests 目录 → true' {
        (Test-IsTestPath 'packages/shell/shell-clipboard-ipc-tests/FakeTransport.cs') | Should Be $true
    }

    It '业务目录 → false' {
        (Test-IsTestPath 'packages/shell/shell-clipboard/ClipboardPlugin.cs') | Should Be $false
    }
}
