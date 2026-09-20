// 注意：本工程 UseWPF=true，WPF SDK 的隐式 using 不含 System.IO / System.Linq，
// 必须显式导入（否则 CS0103 "当前上下文中不存在名称 Path/File"）。
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BetterDesktop.Cli;
using Xunit;

namespace BetterDesktop.Cli.Tests;

/// <summary>
/// 批文件入口（原生右键扩展 → CLI）的单测。
///
/// 覆盖：协议解析（正常/空/坏 JSON/非对象/缺 action/版本不符/路径超限）、动作分类、
/// 以及 RunBatch 的失败路径与"读后即删"纪律。
///
/// 【纪律】RunBatch 会走 ShowError（气泡/弹框），测试必须先打开 SuppressUserFeedback，
/// 否则 TryNotifyBalloon 的 Thread.Sleep(6500) 会让测试挂 6.5 秒并污染桌面。
/// </summary>
public sealed class MenuBatchTests
{
    private static string WriteBatchFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bdt-batch-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string ValidBatchJson(string action = "convert-to", string[]? args = null, string[]? paths = null)
    {
        var root = new JsonObject
        {
            ["version"] = HeadlessExecutor.BatchProtocolVersion,
            ["action"] = action,
            ["args"] = new JsonArray([.. (args ?? ["pdf"]).Select(a => (JsonNode)a)]),
            ["paths"] = new JsonArray([.. (paths ?? [@"C:\nonexistent\a.docx"]).Select(p => (JsonNode)p)]),
        };
        return root.ToJsonString();
    }

    // ===================== 动作分类 =====================

    [Theory]
    [InlineData("convert-to", HeadlessExecutor.BatchActionKind.ConvertTo)]
    [InlineData("toggle-key", HeadlessExecutor.BatchActionKind.ToggleKey)]
    // 【2026-09-17 桌面控制独立化】「桌面控制」是叶子命令（无子项），由原生扩展派发到批协议。
    [InlineData("desktop-controls", HeadlessExecutor.BatchActionKind.OpenDesktopControls)]
    // 【2026-09-17 补 Directory 场景】压缩纳入批协议：文件/文件夹右键「压缩为 ZIP」走这条。
    // 此前白名单里没有它 → Directory 场景虽然注册了 B 路/A 路，却没有任何可挂的项（恒空）。
    [InlineData("compress-zip", HeadlessExecutor.BatchActionKind.Compress)]
    [InlineData("compress-7z", HeadlessExecutor.BatchActionKind.Compress)]
    [InlineData("compress-rar", HeadlessExecutor.BatchActionKind.Compress)]
    [InlineData("", HeadlessExecutor.BatchActionKind.Unknown)]
    [InlineData("convert-more", HeadlessExecutor.BatchActionKind.Unknown)]
    // 其余 --menu-cmd 时代的单文件动作名依旧不得在批协议里被接受（它们走宿主链路，不经批文件）。
    [InlineData("unzip-here", HeadlessExecutor.BatchActionKind.Unknown)]
    [InlineData("dock-pin", HeadlessExecutor.BatchActionKind.Unknown)]
    public void ClassifyBatch_MapsOnlyBatchActions(string action, HeadlessExecutor.BatchActionKind expected)
    {
        Assert.Equal(expected, HeadlessExecutor.ClassifyBatch(action));
    }

    // ===================== 协议解析 =====================

    [Fact]
    public void TryParseBatch_AcceptsValidPayload()
    {
        var ok = HeadlessExecutor.TryParseBatch(
            ValidBatchJson("convert-to", ["pdf"], [@"C:\a\1.docx", @"C:\a\2.docx"]),
            out var request,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("convert-to", request!.Action);
        Assert.Equal(["pdf"], request.Args);
        Assert.Equal(2, request.Paths.Count);
    }

    [Fact]
    public void TryParseBatch_MissingArgsYieldsEmptyList()
    {
        var ok = HeadlessExecutor.TryParseBatch(
            """{"version":1,"action":"toggle-key","paths":[]}""", out var request, out _);

        Assert.True(ok);
        Assert.NotNull(request);
        Assert.Empty(request!.Args);
        Assert.Empty(request.Paths);
    }

    [Fact]
    public void TryParseBatch_RejectsProtocolVersionMismatch()
    {
        var ok = HeadlessExecutor.TryParseBatch(
            """{"version":99,"action":"convert-to","args":["pdf"],"paths":["a.docx"]}""",
            out var request,
            out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Contains("协议版本", error);
    }

    [Theory]
    [InlineData("", "批文件为空")]
    [InlineData("   ", "批文件为空")]
    [InlineData("{ not json", "解析失败")]
    [InlineData("[]", "根节点不是对象")]
    [InlineData("""{"version":1,"args":[]}""", "缺少 action")]
    public void TryParseBatch_RejectsBadPayload(string json, string expectedFragment)
    {
        var ok = HeadlessExecutor.TryParseBatch(json, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void TryParseBatch_RejectsTooManyPaths()
    {
        var paths = Enumerable.Range(0, HeadlessExecutor.MaxBatchPaths + 1)
            .Select(i => $@"C:\a\{i}.docx")
            .ToArray();

        var ok = HeadlessExecutor.TryParseBatch(ValidBatchJson(paths: paths), out _, out var error);

        Assert.False(ok);
        Assert.Contains("超限", error);
    }

    // ===================== RunBatch（失败路径 + 读后即删） =====================

    [Fact]
    public void RunBatch_MissingFile_ReturnsUsage()
    {
        HeadlessExecutor.SuppressUserFeedback = true;

        var code = HeadlessExecutor.RunBatch(
            Path.Combine(Path.GetTempPath(), $"bdt-missing-{Guid.NewGuid():N}.json"));

        Assert.Equal(ExitCodes.Usage, code);
    }

    [Fact]
    public void RunBatch_BadJson_ReturnsUsage_AndDeletesBatchFile()
    {
        HeadlessExecutor.SuppressUserFeedback = true;
        var path = WriteBatchFile("{ broken");

        var code = HeadlessExecutor.RunBatch(path);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.False(File.Exists(path), "读后即删：批文件必须被清理");
    }

    [Fact]
    public void RunBatch_UnknownAction_ReturnsUsage_AndDeletesBatchFile()
    {
        HeadlessExecutor.SuppressUserFeedback = true;
        var path = WriteBatchFile(ValidBatchJson(action: "no-such-action"));

        var code = HeadlessExecutor.RunBatch(path);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.False(File.Exists(path), "读后即删：批文件必须被清理");
    }

    [Fact]
    public void RunBatch_ConvertToMissingPaths_ReturnsFileMissing_AndDeletesBatchFile()
    {
        HeadlessExecutor.SuppressUserFeedback = true;
        var path = WriteBatchFile(ValidBatchJson(
            action: "convert-to",
            args: ["pdf"],
            paths: [Path.Combine(Path.GetTempPath(), "bdt-definitely-missing.docx")]));

        var code = HeadlessExecutor.RunBatch(path);

        Assert.Equal(ExitCodes.FileMissing, code);
        Assert.False(File.Exists(path), "读后即删：批文件必须被清理");
    }

    [Fact]
    public void RunBatch_ConvertToWithoutTarget_ReturnsUsage()
    {
        HeadlessExecutor.SuppressUserFeedback = true;
        var path = WriteBatchFile(ValidBatchJson(action: "convert-to", args: [], paths: [@"C:\a.docx"]));

        var code = HeadlessExecutor.RunBatch(path);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RunBatch_ConvertToWithoutPaths_ReturnsUsage()
    {
        HeadlessExecutor.SuppressUserFeedback = true;
        var path = WriteBatchFile(ValidBatchJson(action: "convert-to", args: ["pdf"], paths: []));

        var code = HeadlessExecutor.RunBatch(path);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RunBatch_EmptyPath_ReturnsUsage()
    {
        HeadlessExecutor.SuppressUserFeedback = true;

        var code = HeadlessExecutor.RunBatch(string.Empty);

        Assert.Equal(ExitCodes.Usage, code);
    }
}
