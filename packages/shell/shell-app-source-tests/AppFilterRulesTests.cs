using BetterDesktop.Shell.AppSource.Services;
using Xunit;

namespace BetterDesktop.Shell.AppSource.Tests;

/// <summary>
/// 【S5 · 2026-09-14】全程序模式过滤规则回归。
/// <para>
/// 语义基线：<c>docs/analysis/2026-09-13-app-filter-derivation.md</c> §7.1（保留项里 536/1487 = 36%
/// 是「程序员在环境里用、没人双击」的 CLI / 工具链）。
/// </para>
/// <para>
/// <b>本文件最重要的一组断言是「不该滤的」</b>——过滤层一旦误杀用户真用的程序，
/// 代价远高于多显示几个 CLI 工具（§9 风险表把它列为「高」）：套件主程序（<c>Ssms.exe</c> /
/// <c>devenv.exe</c>）、常规应用、以及**恰好叫 bin.exe / Jreport 这类名字相近**的用例。
/// </para>
/// </summary>
public class AppFilterRulesTests
{
    // ------------------------------------------------------------- CLI / 工具链：该滤

    [Theory]
    // \bin\ 形迹（含 \usr\bin\ —— Git / MSYS2）
    [InlineData(@"C:\Program Files\Git\usr\bin\grep.exe")]
    [InlineData(@"C:\Program Files\Git\mingw64\bin\git.exe")]
    // Java 工具链
    [InlineData(@"C:\Program Files\Java\jdk-17\bin\javac.exe")]
    [InlineData(@"C:\Program Files\Java\jdk\bin\jar.exe")]
    [InlineData(@"C:\Program Files\Java\jre1.8.0_291\bin\java.exe")]
    // pip 控制台脚本
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Python\Python312\Scripts\uvicorn.exe")]
    // Node.js
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    // Windows Kits（签名 / 打包工具）
    [InlineData(@"C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe")]
    // Tesseract OCR 命令行
    [InlineData(@"C:\Program Files\Tesseract-OCR\tesseract.exe")]
    // Docker CLI 插件
    [InlineData(@"C:\Program Files\Docker\cli-plugins\docker-buildx.exe")]
    // MSVC 编译器
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.38.33130\bin\Hostx64\x64\cl.exe")]
    // Roslyn 编译器
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe")]
    // VS 附属工具
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe")]
    public void ShouldFilter_ToolchainPath_ReturnsTrue(string path)
    {
        Assert.True(AppFilterRules.ShouldFilter(path));
    }

    [Theory]
    // 后台服务（*Service.exe）
    [InlineData(@"C:\Program Files\ASUS\ASUSUpdateService.exe")]
    [InlineData(@"C:\Program Files\App\foobarService.exe")]
    // 崩溃遥测
    [InlineData(@"C:\Program Files\App\crashpad_handler.exe")]
    [InlineData(@"C:\Program Files\App\bugreport.exe")]
    [InlineData(@"C:\Program Files\App\telemetry_host.exe")]
    // 运行库分发
    [InlineData(@"C:\Program Files\App\vc_redist.x64.exe")]
    [InlineData(@"C:\Program Files\App\vcruntime140_1.exe")]
    public void ShouldFilter_ServiceOrRedistributable_ReturnsTrue(string path)
    {
        Assert.True(AppFilterRules.ShouldFilter(path));
    }

    // ------------------------------------------------------------- 不该滤（本文件最重要的一组）

    [Theory]
    // 套件主程序：混装目录里但用户会双击（整块按供应商滤会误杀它们）
    [InlineData(@"C:\Program Files\Microsoft SQL Server Management Studio 19\Common7\IDE\Ssms.exe")]
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe")]
    [InlineData(@"C:\Program Files\VMware\VMware Workstation\vmware.exe")]
    // 常规应用
    [InlineData(@"C:\Program Files\7-Zip\7zFM.exe")]
    [InlineData(@"C:\Program Files\Notepad++\notepad++.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"D:\Program Files\SomeApp\SomeApp.exe")]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\Code.exe")]
    public void ShouldFilter_RealApplication_ReturnsFalse(string path)
    {
        Assert.False(AppFilterRules.ShouldFilter(path));
    }

    [Theory]
    // 名字相近但语义不同：文件在应用根目录（叫 bin.exe）≠ 位于 \bin\ 目录
    [InlineData(@"C:\Program Files\SomeApp\bin.exe")]
    // 目录段必须**整段相等**：Binder ≠ bin
    [InlineData(@"C:\Program Files\Binder\Binder.exe")]
    // 版本化前缀外的同首字母目录：Jreport 不是 jre*
    [InlineData(@"C:\Program Files\Jreport\jr.exe")]
    [InlineData(@"C:\Program Files\Jdkeeper\app.exe")]
    // 裸 runtime 刻意不滤（会误伤用户真用的运行时类应用）
    [InlineData(@"C:\Program Files\SomeRuntime\runtime.exe")]
    public void ShouldFilter_LookAlikeName_ReturnsFalse(string path)
    {
        Assert.False(AppFilterRules.ShouldFilter(path));
    }

    // ------------------------------------------------------------- 边界

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldFilter_EmptyPath_ReturnsFalse(string? path)
    {
        // 路径不明时宁可多显示，不可误杀
        Assert.False(AppFilterRules.ShouldFilter(path));
    }

    [Fact]
    public void ShouldFilter_PathWithoutFileName_ReturnsFalse()
    {
        // 纯目录（无文件名）没有任何可滤依据
        Assert.False(AppFilterRules.ShouldFilter(@"C:\Program Files\Git\usr\bin\"));
    }
}
