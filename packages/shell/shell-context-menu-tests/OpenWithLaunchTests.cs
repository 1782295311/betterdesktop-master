// 「打开方式」命令行链单测（2026-09-17 用户实测回归修复）。
//
// 【钉住的两个真实缺陷】
//   ① 只取注册表命令里的 exe、丢掉参数：Edge/Chrome 注册的是
//      `msedge.exe --single-argument %1`，丢掉 --single-argument 后点「Microsoft Edge」没反应。
//      → MatchOpenWith 必须把注册表命令行的**参数尾部原样带回**（SplitCommandLine 同源）。
//   ② 占位符替换时再包一层引号：模板 "%1" → 得到 ""C:\x.pdf""（脏命令行，部分应用自研解析器
//      会多出一个空实参）→ BuildLaunchArgs 必须先去重引号再替换。
//
// 测试用**临时注册表键**（HKCU\Software\Classes，HKCR 合并视图可见）+ 唯一临时扩展名，
// 不依赖本机装了哪些软件、也不污染真实右键菜单；测后清理（keep-clean 纪律）。

using System;
using System.IO;
using Microsoft.Win32;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class OpenWithLaunchTests
{
    // ===== BuildLaunchArgs：占位符替换 =====

    [Fact]
    public void BuildArgs_QuotedPlaceholder_DoesNotDoubleQuote()
    {
        var args = ToolCatalog.BuildLaunchArgs("\"%1\"", @"C:\dir\a.txt");

        Assert.Equal("\"C:\\dir\\a.txt\"", args);
    }

    [Fact]
    public void BuildArgs_KeepsRegisteredFlags_InPlace()
    {
        // Edge / Chrome 的真实形态
        var args = ToolCatalog.BuildLaunchArgs("--single-argument %1", @"C:\dir\a b.pdf");

        Assert.Equal("--single-argument \"C:\\dir\\a b.pdf\"", args);
    }

    [Fact]
    public void BuildArgs_WpsStyle_MultiFlagWithQuotedPlaceholder()
    {
        var args = ToolCatalog.BuildLaunchArgs("/prometheus /pdf \"%1\"", @"C:\dir\a.pdf");

        Assert.Equal("/prometheus /pdf \"C:\\dir\\a.pdf\"", args);
    }

    [Fact]
    public void BuildArgs_LegacyFilePlaceholder_IsCaseInsensitive()
    {
        // 本仓库旧写法（Detect 的工具表 / 类别兜底）仍须可用
        var args = ToolCatalog.BuildLaunchArgs("\"%file%\" --out \"%dir%\"", @"C:\dir sub\a.txt");

        Assert.Equal("\"C:\\dir sub\\a.txt\" --out \"C:\\dir sub\"", args);
    }

    [Fact]
    public void BuildArgs_WorkingDirPlaceholder_Unquoted()
    {
        // %w 是 shell 的工作目录占位符（不带引号、不带尾斜杠）
        var args = ToolCatalog.BuildLaunchArgs("-d %w %1", @"C:\dir sub\a.txt");

        Assert.Equal("-d C:\\dir sub \"C:\\dir sub\\a.txt\"", args);
    }

    // ===== SplitCommandLine：注册表命令行 → (exe, 参数模板) =====

    [Fact]
    public void SplitCommandLine_QuotedExe_KeepsArgumentTail()
    {
        var split = ToolCatalog.SplitCommandLine(
            "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", split!.Value.Exe);
        Assert.Equal("--single-argument %1", split.Value.Args);
    }

    [Fact]
    public void SplitCommandLine_NoPlaceholder_AppendsFileArgument()
    {
        // shell 语义：命令里没有占位符时，explorer 会把文件追加为末位实参
        var split = ToolCatalog.SplitCommandLine("\"C:\\App\\tool.exe\" -n");

        Assert.NotNull(split);
        Assert.Equal("-n \"%1\"", split!.Value.Args);
    }

    [Fact]
    public void SplitCommandLine_BareExeWithPlaceholder_NoExtraArg()
    {
        var split = ToolCatalog.SplitCommandLine("C:\\App\\tool.exe \"%1\"");

        Assert.NotNull(split);
        Assert.Equal(@"C:\App\tool.exe", split!.Value.Exe);
        Assert.Equal("\"%1\"", split.Value.Args);
    }

    [Fact]
    public void SplitCommandLine_Empty_ReturnsNull()
    {
        Assert.Null(ToolCatalog.SplitCommandLine("   "));
        Assert.Null(ToolCatalog.SplitCommandLine("\"\""));
    }

    // ===== MatchOpenWith：候选必须带回注册表参数模板 =====

    [Fact]
    public void MatchOpenWith_ReturnsRegisteredCommandTemplate()
    {
        var ext = ".bdtprobe" + Guid.NewGuid().ToString("N")[..8];
        var progId = "BetterDesktop.Probe." + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        try
        {
            using (var cmdKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{progId}\shell\open\command"))
            {
                cmdKey!.SetValue(null, $"\"{exe}\" --bdt-probe-flag \"%1\"");
            }
            using (var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
            {
                // 只枚举**值名**，值类型不参与判定（系统真实写法是 REG_NONE 空值，此处用空串等效且无兼容坑）
                owp!.SetValue(progId, string.Empty);
            }

            var candidates = ToolCatalog.MatchOpenWith("probe" + ext);

            var hit = Assert.Single(candidates, c =>
                string.Equals(c.Path, exe, StringComparison.OrdinalIgnoreCase));
            // 回归点：参数模板必须是注册表里的原样（含自定义开关），不是写死的裸路径
            Assert.Equal("--bdt-probe-flag \"%1\"", hit.Args);
            // 且替换后必须是一个能用的命令行（含开关 + 带引号的文件路径）
            var finalArgs = ToolCatalog.BuildLaunchArgs(hit.Args, @"C:\tmp\probe" + ext);
            Assert.Equal($"--bdt-probe-flag \"C:\\tmp\\probe{ext}\"", finalArgs);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}", throwOnMissingSubKey: false);
        }
    }
}
