using System.Text.Json;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

/// <summary>
/// 【按格粘 · 2026-09-13】表格单元格级按序粘贴的回归：
/// 拆分规则（尾部空清理 / 行优先）、会话入队、粘后自动按键、取消复位。
/// </summary>
public class ClipboardCellPasteTests
{
    private static string Resp(string reqJson, string resultJson)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}";
    }

    private static string EntryResponse(string reqJson, string content) =>
        Resp(reqJson, JsonSerializer.Serialize(new { content }));

    private static ClipboardEntry TableEntry(string content) => new()
    {
        Id = "table-1",
        ContentType = ClipboardItemKind.Text,
        Content = content,
    };

    // ---------------- 拆分（T1/T2）----------------

    [Fact]
    public void Split_IsRowMajor_ExcelVisualOrder()
    {
        var cells = ClipboardTableCells.Split("a\tb\r\nc\td");

        Assert.Equal(new[] { "a", "b", "c", "d" }, cells);
    }

    [Fact]
    public void Split_TrailingTabsAndBlankLinesAreDiscarded()
    {
        // 行尾多余 Tab（Excel 常带）+ 末尾空行 → 都要清掉，否则用户要多按几次"空粘贴"
        var cells = ClipboardTableCells.Split("a\tb\t\t\r\nc\td\t\r\n\r\n");

        Assert.Equal(new[] { "a", "b", "c", "d" }, cells);
    }

    [Fact]
    public void Split_KeepsInteriorEmptyCell()
    {
        // 行内空格子保留（用户可据此清空目标格）；只有**行尾**空才丢
        var cells = ClipboardTableCells.Split("a\t\tc");

        Assert.Equal(new[] { "a", "", "c" }, cells);
    }

    [Fact]
    public void Split_MixedLineEndings()
    {
        Assert.Equal(new[] { "a", "b", "c", "d" }, ClipboardTableCells.Split("a\tb\nc\td"));
        Assert.Equal(new[] { "a", "b", "c", "d" }, ClipboardTableCells.Split("a\tb\rc\td"));
    }

    [Fact]
    public void IsTabular_RequiresMultipleColumns()
    {
        Assert.True(ClipboardTableCells.IsTabular(TableEntry("a\tb\r\nc\td"), out var rows, out var cols, out var count));
        Assert.Equal(2, rows);
        Assert.Equal(2, cols);
        Assert.Equal(4, count);

        // 单列（无 Tab）不算表格 —— 普通粘贴即可
        Assert.False(ClipboardTableCells.IsTabular(TableEntry("a\r\nb")));
        // 纯文本不算
        Assert.False(ClipboardTableCells.IsTabular(TableEntry("hello world")));
        // 图片条目不算（即便 content 里凑巧有 Tab）
        Assert.False(ClipboardTableCells.IsTabular(new ClipboardEntry
        {
            Id = "img",
            ContentType = ClipboardItemKind.Image,
            Content = "a\tb",
        }));
    }

    // ---------------- 会话（T3/T4/T5）----------------

    [Fact]
    public void BeginCellSequentialPaste_SplitsAllCells_WithoutEngineWriteback()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "a\tb\r\nc\td") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        c.BeginCellSequentialPaste(TableEntry("a\tb\r\nc\td"), CellPasteAutoKey.None);

        Assert.True(c.IsSequentialPasteActive);
        Assert.Equal(4, c.SequentialRemaining);
    }

    [Fact]
    public void BeginCellSequentialPaste_RejectsOversizedTable()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "x") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        // 2001 格（1001 行 × 2 列）超过上限
        var big = string.Join("\r\n", Enumerable.Range(0, 1001).Select(i => $"{i}\tv"));
        var ex = Assert.Throws<ClipboardIpcException>(
            () => c.BeginCellSequentialPaste(TableEntry(big), CellPasteAutoKey.None));
        Assert.Contains("超过上限", ex.Message);
        Assert.False(c.IsSequentialPasteActive);
    }

    [Fact]
    public void PasteNextSequential_CellMode_WritesSnapshotAndInjectsAutoKey()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "a\tb\r\nc\td") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        var written = new List<string>();
        c.SnapshotWriterHook = payload =>
        {
            written.Add(payload.Text);
            return true; // 不碰真实剪贴板
        };
        var pastes = 0;
        c.PasteInjectorHook = () => pastes++;
        var autoKeys = new List<CellPasteAutoKey>();
        c.AutoKeyInjectorHook = key => autoKeys.Add(key);

        c.BeginCellSequentialPaste(TableEntry("a\tb\r\nc\td"), CellPasteAutoKey.Tab);

        c.PasteNextSequential();
        Assert.Equal(new[] { "a" }, written);
        Assert.Equal(1, pastes);
        Assert.Equal(new[] { CellPasteAutoKey.Tab }, autoKeys);
        Assert.Equal(3, c.SequentialRemaining);

        c.PasteNextSequential();
        Assert.Equal(new[] { "a", "b" }, written);
        Assert.Equal(2, autoKeys.Count);

        // 单元格模式**不得**向引擎发 copy_to_clipboard（每格一次注定失败的 IPC + 噪声日志）
        Assert.Null(t.LastRequestOf("copy_to_clipboard"));
    }

    [Fact]
    public void PasteNextSequential_CellMode_NoneAutoKey_DoesNotInjectKey()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "a\tb") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        c.SnapshotWriterHook = _ => true;
        c.PasteInjectorHook = () => { };
        var autoKeys = 0;
        c.AutoKeyInjectorHook = _ => autoKeys++;

        c.BeginCellSequentialPaste(TableEntry("a\tb"), CellPasteAutoKey.None);
        c.PasteNextSequential();

        Assert.Equal(0, autoKeys);
    }

    /// <summary>写回失败时**绝不注入**：剪贴板里还留着上一格，注入会把上一格重复粘一次。</summary>
    [Fact]
    public void PasteNextSequential_CellMode_WriteFail_SkipsPasteButStillAdvances()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "a\tb") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        c.SnapshotWriterHook = _ => false; // 写不进去
        var pastes = 0;
        c.PasteInjectorHook = () => pastes++;
        var autoKeys = 0;
        c.AutoKeyInjectorHook = _ => autoKeys++;

        c.BeginCellSequentialPaste(TableEntry("a\tb"), CellPasteAutoKey.Tab);
        c.PasteNextSequential();

        Assert.Equal(0, pastes);      // 不注入（避免重复粘上一格）
        Assert.Equal(1, autoKeys);    // 仍跳格（保持格位对齐）
        Assert.Equal(1, c.SequentialRemaining);
    }

    [Fact]
    public void Cancel_ResetsSession()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? EntryResponse(req, "a\tb") : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        c.SnapshotWriterHook = _ => true;
        c.PasteInjectorHook = () => { };
        var autoKeys = 0;
        c.AutoKeyInjectorHook = _ => autoKeys++;

        c.BeginCellSequentialPaste(TableEntry("a\tb"), CellPasteAutoKey.Tab);
        c.CancelSequentialPaste();

        Assert.False(c.IsSequentialPasteActive);
        Assert.Equal(0, c.SequentialRemaining);

        c.PasteNextSequential(); // 会话已取消 → 无操作
        Assert.Equal(0, autoKeys);
    }
}
