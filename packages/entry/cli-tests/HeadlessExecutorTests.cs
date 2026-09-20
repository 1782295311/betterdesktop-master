using System.IO;
using BetterDesktop.Cli;
using BetterDesktop.Shell.Convert.Contracts;
using Xunit;

namespace BetterDesktop.Cli.Tests;

/// <summary>
/// CLI headless 执行器单测（计划 §13 单测 7/8）：
/// 动作分类 / 退出码映射为纯逻辑（不弹框）；headless 执行走成功路径（纯托管引擎，无 UI 依赖）。
/// </summary>
public class HeadlessExecutorTests
{
    // —— 7. 动作分类（参数解析/路由的核心判定） ——

    [Theory]
    [InlineData("convert-to-pdf", HeadlessActionKind.ConvertTo)]
    [InlineData("convert-to-docx", HeadlessActionKind.ConvertTo)]
    [InlineData("compress-zip", HeadlessActionKind.Compress)]
    [InlineData("compress-7z", HeadlessActionKind.Compress)]
    [InlineData("compress-rar", HeadlessActionKind.Compress)]
    [InlineData("unzip-here", HeadlessActionKind.Unzip)]
    [InlineData("unzip-to", HeadlessActionKind.Unzip)]
    [InlineData("clipboard-history", HeadlessActionKind.NeedsHost)]
    [InlineData("open-settings", HeadlessActionKind.NeedsHost)]
    [InlineData("dock-pin", HeadlessActionKind.NeedsHost)]
    [InlineData("convert", HeadlessActionKind.NeedsHost)]
    [InlineData("convert-more", HeadlessActionKind.NeedsHost)]
    [InlineData("unknown-xyz", HeadlessActionKind.Unknown)]
    [InlineData("", HeadlessActionKind.Unknown)]
    public void Classify_ReturnsExpected(string action, HeadlessActionKind expected)
    {
        Assert.Equal(expected, HeadlessExecutor.Classify(action));
    }

    // —— 7. 退出码映射（错误分层：引擎缺失与业务失败分开） ——

    [Theory]
    [InlineData(ConvertError.EngineMissing, ExitCodes.EngineMissing)]
    [InlineData(ConvertError.InputInvalid, ExitCodes.Failed)]
    [InlineData(ConvertError.ConversionFailed, ExitCodes.Failed)]
    [InlineData(ConvertError.EngineCrashed, ExitCodes.Failed)]
    [InlineData(ConvertError.Timeout, ExitCodes.Failed)]
    [InlineData(ConvertError.OutputFailed, ExitCodes.Failed)]
    [InlineData(ConvertError.None, ExitCodes.Failed)]
    public void MapConvertError_ReturnsExpected(ConvertError error, int expected)
    {
        Assert.Equal(expected, HeadlessExecutor.MapConvertError(error));
    }

    // —— 8. headless 转换执行（md→html 纯托管引擎；成功路径不弹框） ——

    [Fact]
    public void Run_ConvertToHtml_Success()
    {
        // 【2026-09-17 补纪律】本类此前没开 SuppressUserFeedback：一旦转换失败（典型：Rust 转换引擎未部署
        // → EngineMissing），ShowError 会走原生 MessageBox **弹在用户桌面上**并卡住运行者——
        // 用户实测就是这么看到那个"Rust 转换引擎未部署"弹框的。MenuBatchTests 一直有这条纪律，此处补齐。
        HeadlessExecutor.SuppressUserFeedback = true;

        var dir = CreateTempDir();
        try
        {
            var input = Path.Combine(dir, "sample.md");
            File.WriteAllText(input, "# 标题\n\n正文内容");

            var exit = HeadlessExecutor.Run("convert-to-html", input);

            Assert.Equal(ExitCodes.Ok, exit);
            var output = Path.Combine(dir, "sample.html");
            Assert.True(File.Exists(output), "转换产物应生成于同目录");
            Assert.Contains("标题", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // —— 8. headless 压缩/解压执行（zip 内置引擎） ——

    [Fact]
    public void Run_CompressZip_Then_UnzipTo_Success()
    {
        HeadlessExecutor.SuppressUserFeedback = true; // 同上：任何失败路径都不该弹在用户桌面上

        var dir = CreateTempDir();
        try
        {
            var input = Path.Combine(dir, "doc.txt");
            File.WriteAllText(input, "hello betterdt");

            var exitCompress = HeadlessExecutor.Run("compress-zip", input);
            Assert.Equal(ExitCodes.Ok, exitCompress);
            var zip = Path.Combine(dir, "doc.zip");
            Assert.True(File.Exists(zip), "zip 产物应生成于同目录");

            var exitUnzip = HeadlessExecutor.Run("unzip-to", zip);
            Assert.Equal(ExitCodes.Ok, exitUnzip);
            Assert.True(File.Exists(Path.Combine(dir, "doc", "doc.txt")), "解压到以文件名命名的子文件夹");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // —— 8. 输入缺失 → FileMissing（不弹框，纯判定在 Run 首步） ——

    [Fact]
    public void Run_ConvertTo_MissingInput_ReturnsFileMissing()
    {
        var exit = HeadlessExecutor.Run("convert-to-pdf", @"Z:\definitely\missing\file.docx");
        Assert.Equal(ExitCodes.FileMissing, exit);
    }

    // —— 3. 设置直写：未知 toggle-key 拒绝（无副作用、不写文件） ——

    [Fact]
    public void ToggleKey_UnknownKey_ReturnsUsage()
    {
        Assert.Equal(ExitCodes.Usage, HeadlessExecutor.ToggleKey("bogus-key"));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bdt-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
