using System.Text.Json;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

/// <summary>
/// 【P2-3 临时粘贴 · 2026-09-13】IPC 命令路径回归：写回并暂存 → 注入 → **还原**。
/// 这些用例锁死三条命令串（与引擎 <c>dispatch</c> 对齐）与 <c>hadPrevious</c>/<c>restored</c> 解析，
/// 防止单侧改动导致"临时粘贴静默变成普通粘贴"。
/// </summary>
public class ClipboardTempPasteTests
{
    private static string Resp(string reqJson, string resultJson)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}";
    }

    private static FakeTransport Server(string method, string resultJson)
    {
        var t = new FakeTransport();
        t.ServerHandler = req => req.Contains($"\"{method}\"") ? Resp(req, resultJson) : null;
        return t;
    }

    [Fact]
    public void CopyEntryTemporarilyToClipboard_SendsCommandAndReadsHadPrevious()
    {
        using var t = Server("copy_temp_to_clipboard", "{\"ok\":true,\"hadPrevious\":true}");
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        var hadPrevious = c.CopyEntryTemporarilyToClipboard(new ClipboardEntry { Id = "abc123" });

        Assert.True(hadPrevious);
        var p = t.LastRequestOf("copy_temp_to_clipboard")!.Value.GetProperty("params");
        Assert.Equal("abc123", p.GetProperty("id").GetString());
    }

    [Fact]
    public void CopyEntryTemporarilyToClipboard_NoPrevious_ReturnsFalse()
    {
        using var t = Server("copy_temp_to_clipboard", "{\"ok\":true,\"hadPrevious\":false}");
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        Assert.False(c.CopyEntryTemporarilyToClipboard(new ClipboardEntry { Id = "e2" }));
    }

    [Fact]
    public void RestoreTemporaryClipboard_ReadsRestoredFlag()
    {
        using var t = Server("restore_temp_clipboard", "{\"ok\":true,\"restored\":true}");
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        Assert.True(c.RestoreTemporaryClipboard());
        Assert.NotNull(t.LastRequestOf("restore_temp_clipboard"));
    }

    /// <summary>引擎过期（>10s）时返回 restored=false —— 客户端必须如实回报，不得当成成功。</summary>
    [Fact]
    public void RestoreTemporaryClipboard_Expired_ReturnsFalse()
    {
        using var t = Server("restore_temp_clipboard", "{\"ok\":true,\"restored\":false}");
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        Assert.False(c.RestoreTemporaryClipboard());
    }

    /// <summary>端到端顺序：写回 → 注入 → 后台延迟后还原（还原不能发生在注入之前）。</summary>
    [Fact]
    public void PasteEntryTemporarilyToActiveWindow_InjectsThenRestores()
    {
        using var t = new FakeTransport();
        t.ServerHandler = req => req.Contains("\"copy_temp_to_clipboard\"")
            ? Resp(req, "{\"ok\":true,\"hadPrevious\":true}")
            : req.Contains("\"restore_temp_clipboard\"")
                ? Resp(req, "{\"ok\":true,\"restored\":true}")
                : null;
        using var c = new ClipboardIpcClient(t);
        c.Connect();

        var injected = 0;
        c.PasteInjectorHook = () => Interlocked.Increment(ref injected);

        c.PasteEntryTemporarilyToActiveWindow(new ClipboardEntry { Id = "abc123" });

        Assert.Equal(1, injected); // 同步完成写回 + 注入
        Assert.NotNull(t.LastRequestOf("copy_temp_to_clipboard"));
        Assert.Null(t.LastRequestOf("restore_temp_clipboard")); // 还原必须是延迟的（否则粘贴拿不到内容）

        Assert.True(
            SpinWait.SpinUntil(() => t.LastRequestOf("restore_temp_clipboard") is not null, 3000),
            "临时粘贴应在注入后（约 250ms）还原剪贴板");
    }
}
