using System.Text.Json;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

public class ClipboardIpcClientTests
{
    private const string SampleEntry =
        "{\"id\":\"abc123\",\"contentType\":0,\"category\":0,\"content\":\"hello 中文\",\"htmlContent\":\"\",\"rtfContent\":\"\","
        + "\"timestamp\":\"2026-09-12T12:15:25\",\"isPinned\":false,\"imagePath\":\"\",\"imageWidth\":0,\"imageHeight\":0,"
        + "\"sizeBytes\":10,\"copyCount\":1,\"sourceProcessName\":\"notepad\",\"sourceWindowTitle\":\"\",\"filePaths\":[],"
        + "\"tags\":\"\",\"hasImages\":false,\"hasTable\":false,\"isCode\":false,\"contentInBin\":false}";

    private static string Resp(string reqJson, string resultJson)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}";
    }

    private static string Err(string reqJson, long code, string message)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}";
    }

    private static FakeTransport MakeTransport()
    {
        var t = new FakeTransport();
        t.ServerHandler = req =>
        {
            if (req.Contains("\"get_last\""))
            {
                return Resp(req, SampleEntry);
            }
            // 按序粘贴开始时会逐条 get_entry 抓内容快照 —— 默认给一条可用的，
            // 否则每条都要等 3s 超时（测试会慢且噪声大）。
            if (req.Contains("\"get_entry\""))
            {
                return Resp(req, SampleEntry);
            }
            return null;
        };
        return t;
    }

    private static ClipboardIpcClient MakeClient(FakeTransport t, int timeoutMs = 3000)
    {
        var c = new ClipboardIpcClient(t, timeoutMs);
        c.Connect();
        return c;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>
    /// 【2026-09-14 回归】连接态真源 <see cref="ClipboardIpcClient.IsConnected"/>：
    /// 面板据此区分「引擎真的连不上」与「引擎活着但这次请求失败」（RPC 报错 / 单次超时）。
    /// <para>
    /// 此前没有这个属性，面板只能把**所有**分页异常都渲染成"引擎未连接，正在重试…"，
    /// 于是引擎明明活着也报断线（用户实测：暂停记录时看到"与引擎断开连接"，实际连接正常），
    /// 而该横幅优先级最高，会把"已暂停捕获"折叠成"+N 条提醒"。
    /// </para>
    /// </summary>
    [Fact]
    public void IsConnected_TracksPipeLifecycle()
    {
        using var t = MakeTransport();
        using var c = new ClipboardIpcClient(t, timeoutMs: 500);

        Assert.False(c.IsConnected, "Connect() 之前必须是未连接态");

        c.Connect();
        Assert.True(WaitFor(() => c.IsConnected), "连接建立后应回报已连接");

        // 对端断开：客户端会立刻自动重连（本仿真默认立即成功）—— 先让下一次连接失败，
        // 才能稳定观测到「断开 → false」这一步（否则重连太快，断言会闪）。
        //
        // 超时给 6s：**本仿真传输**没有 PeekNamedPipe 语义（`IsDataAvailable` 只看队列长度），
        // 断线只能靠心跳无应答发现（HeartbeatTimeoutMs = 3s）；真实命名管道是立即发现的。
        t.FailNextConnect = true;
        t.Disconnect();
        Assert.True(
            WaitFor(() => !c.IsConnected, timeoutMs: 6000),
            "对端断开后应回报未连接（面板据此才允许报断线）");
    }

    // ---------------- 查询 ----------------

    [Fact]
    public void GetLastCopiedContent_Roundtrip()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        var last = c.GetLastCopiedContent();
        Assert.NotNull(last);
        Assert.Equal(ClipboardItemKind.Text, last!.Type);
        Assert.Equal("hello 中文", last.ContentOrPath);
        Assert.Equal(new DateTime(2026, 9, 12, 12, 15, 25), last.Timestamp);
    }

    [Fact]
    public void GetLastCopiedContent_RespectsTimeLimit()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        var last = c.GetLastCopiedContent(TimeSpan.FromSeconds(1));
        Assert.Null(last); // 条目时间 2026-09-12，远超 1 秒
    }

    [Fact]
    public void GetFilteredEntriesPage_ForwardsOffsetLimitCategory()
    {
        using var t = MakeTransport();
        t.ServerHandler = req =>
        {
            if (!req.Contains("\"query\""))
            {
                return null;
            }
            return Resp(req, "{\"total\":1,\"items\":[" + SampleEntry + "]}");
        };
        using var c = MakeClient(t);
        var (items, total) = c.GetFilteredEntriesPage(null, ContentCategory.Code, null, null, 10, 100);
        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("abc123", items[0].Id);
        Assert.Equal(ContentCategory.Text, items[0].Category); // 样例 category=0→Text
        var req = t.LastRequestOf("query")!.Value;
        Assert.Equal(10, req.GetProperty("params").GetProperty("offset").GetInt32());
        Assert.Equal(100, req.GetProperty("params").GetProperty("limit").GetInt32());
        Assert.Equal(1, req.GetProperty("params").GetProperty("category").GetInt32());
    }

    [Fact]
    public void GetFilteredEntriesPage_ForwardsAllFiltersToEngine()
    {
        using var t = MakeTransport();
        // 假引擎：过滤已在服务端完成，只回它算出的命中集（客户端不得再自行过滤/重算 total）
        t.ServerHandler = req => req.Contains("\"query\"")
            ? Resp(req, "{\"total\":1,\"items\":[" + SampleEntry + "]}")
            : null;
        using var c = MakeClient(t);

        var (items, total) = c.GetFilteredEntriesPage(
            ClipboardItemKind.Text, ContentCategory.Code, "  hello  ", "  Notepad ", offset: 0, limit: 100, pinned: true);

        Assert.Equal(1, total); // 引擎返回的「过滤后命中总数」直接透传
        Assert.Equal("abc123", items[0].Id);

        var p = t.LastRequestOf("query")!.Value.GetProperty("params");
        Assert.Equal("hello", p.GetProperty("keyword").GetString());     // 客户端只负责 trim
        Assert.Equal(0, p.GetProperty("kind").GetInt32());              // Text=0
        Assert.Equal(1, p.GetProperty("category").GetInt32());           // Code=1
        Assert.Equal("Notepad", p.GetProperty("sourceApp").GetString());
        Assert.True(p.GetProperty("pinned").GetBoolean());
        Assert.Equal(0, p.GetProperty("offset").GetInt32());
        Assert.Equal(100, p.GetProperty("limit").GetInt32());
    }

    // ---------------- 错误 / 超时 ----------------

    [Fact]
    public void RpcError_ThrowsClipboardIpcException()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("\"get_last\"") ? Err(req, -32602, "bad id") : null;
        using var c = MakeClient(t);
        var ex = Assert.Throws<ClipboardIpcException>(() => c.GetLastCopiedContent());
        Assert.Equal(-32602, ex.RpcCode);
        Assert.Equal("bad id", ex.Message);
    }

    [Fact]
    public void Timeout_Throws()
    {
        using var t = MakeTransport();
        t.ServerHandler = _ => null; // 永不响应
        using var c = MakeClient(t, timeoutMs: 300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ClipboardIpcException>(() => c.GetLastCopiedContent());
        sw.Stop();
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.ElapsedMilliseconds < 5000, "timeout should be fast");
    }

    // ---------------- 事件 ----------------

    [Fact]
    public void HistoryChanged_Event_Raised()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        ClipboardHistoryChangedEventArgs? got = null;
        using var done = new ManualResetEventSlim();
        c.HistoryChanged += e => { got = e; done.Set(); };
        t.ServerPush.Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"history_changed\",\"params\":{\"kind\":\"added\",\"id\":\"e1\"}}");
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(ClipboardChangeKind.Added, got!.Change);
        Assert.Equal("e1", got.EntryId);
    }

    [Fact]
    public void PauseEvent_UpdatesState()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        using var pauseDone = new ManualResetEventSlim();
        c.PauseStateChanged += _ => pauseDone.Set();
        t.ServerPush.Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"pause_changed\",\"params\":{\"paused\":true}}");
        Assert.True(pauseDone.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(c.IsTemporarilyPaused);
    }

    [Fact]
    public void ClipboardChanged_Event_Raised()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        ClipboardChangedInfo? got = null;
        using var done = new ManualResetEventSlim();
        c.ClipboardChanged += info => { got = info; done.Set(); };
        t.ServerPush.Enqueue(
            "{\"jsonrpc\":\"2.0\",\"method\":\"clipboard_changed\",\"params\":{\"id\":\"e9\",\"contentType\":3,"
            + "\"textPreview\":\"<p>hi</p>\",\"thumbPath\":\"C:\\\\t.png\",\"sourceApp\":\"word\",\"copiedAt\":\"2026-09-12T13:00:00\"}}");
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal("e9", got!.Id);
        Assert.Equal(ClipboardItemKind.Html, got.ContentType);
        Assert.Equal("word", got.SourceApp);
    }

    // ---------------- 变更调用序列 ----------------

    [Fact]
    public void PinDeleteClear_SendCorrectMethods()
    {
        using var t = MakeTransport();
        t.ServerHandler = req =>
        {
            if (req.Contains("\"get_entry\""))
            {
                return Resp(req, SampleEntry);
            }
            return Resp(req, "{\"ok\":true}");
        };
        using var c = MakeClient(t);
        var e = new ClipboardEntry { Id = "e1" };
        c.PinEntry(e);
        Assert.NotNull(t.LastRequestOf("pin"));
        c.DeleteEntry(e);
        Assert.NotNull(t.LastRequestOf("delete"));
        c.ClearAllUnpinned();
        Assert.NotNull(t.LastRequestOf("clear_unpinned"));
    }

    [Fact]
    public void CopyPlainText_SendsPlainFlag()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => Resp(req, "{\"ok\":true}");
        using var c = MakeClient(t);
        c.CopyEntryAsPlainText(new ClipboardEntry { Id = "e1" });
        var req = t.LastRequestOf("copy_to_clipboard")!.Value;
        Assert.True(req.GetProperty("params").GetProperty("plain").GetBoolean());
    }

    // ---------------- 表情包导入（用户主动填入） ----------------

    /// <summary>
    /// 导入请求：paths 需去重 + trim，字段名必须 camelCase（引擎 serde rename_all="camelCase"）；
    /// 结果三态（新增/重复/失败原因）需完整解析出来 —— UI 要逐条反馈失败，不能静默。
    /// </summary>
    [Fact]
    public void AddStickers_ForwardsPathsAndParsesResult()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("\"add_sticker\"")
            ? Resp(req, "{\"ok\":true,\"added\":2,\"skipped\":1,\"ids\":[\"a\",\"b\"],\"errors\":[\"不支持的格式\"]}")
            : null;
        using var c = MakeClient(t);

        var result = c.AddStickers(new[] { "C:\\a.gif", " C:\\b.gif ", "C:\\a.gif" });

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Errors);
        Assert.Contains("不支持的格式", result.Errors[0], StringComparison.Ordinal);

        var paths = t.LastRequestOf("add_sticker")!.Value.GetProperty("params").GetProperty("paths");
        Assert.Equal(2, paths.GetArrayLength()); // 重复项已去掉
        Assert.Equal("C:\\a.gif", paths[0].GetString());
        Assert.Equal("C:\\b.gif", paths[1].GetString()); // 已 trim
    }

    /// <summary>空输入不得发请求（避免引擎侧无谓报错），返回全零结果。</summary>
    [Fact]
    public void AddStickers_EmptyInput_SendsNoRequest()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);

        var result = c.AddStickers(Array.Empty<string>());

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Null(t.LastRequestOf("add_sticker"));
    }

    // ---------------- 合并粘贴（必须取引擎全文，不得用列表摘要） ----------------

    /// <summary>
    /// 【2026-09-12 修复】面板传来的条目是**列表摘要载荷**（content 截断到 1024 字符、htmlContent 剥离）。
    /// 旧实现只在 PlainText 为空时才回引擎取全文 → 长文本合并粘贴被静默截断。现在一律取全文。
    /// </summary>
    [Fact]
    public void MergePaste_FetchesEngineFullText_AndInjectsOnce()
    {
        using var t = MakeTransport();
        var full = "FULL-TEXT-" + new string('x', 2000); // 远超摘要截断长度
        t.ServerHandler = req => req.Contains("\"get_entry\"")
            ? Resp(req, SampleEntry.Replace("hello 中文", full))
            : null;

        using var c = MakeClient(t);
        var written = string.Empty;
        c.ClipboardTextWriterHook = s => written = s; // 不碰真实剪贴板
        var injected = 0;
        c.PasteInjectorHook = () => injected++;

        // 摘要载荷：content 只有截断版（模拟列表页返回）
        var digest = new ClipboardEntry { Id = "abc123", Content = "hello 中文（截断版）" };
        c.MergePasteToActiveWindow(new[] { digest });

        Assert.Equal(1, injected);
        Assert.Contains("FULL-TEXT-", written, StringComparison.Ordinal);   // 取自引擎全文
        Assert.DoesNotContain("截断版", written, StringComparison.Ordinal); // 绝不用摘要凑数
    }

    /// <summary>所选条目都取不到可粘文本时必须显式失败（旧实现静默 return，用户以为已粘贴）。</summary>
    [Fact]
    public void MergePaste_ThrowsWhenNoPasteableText()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") ? Err(req, -32602, "entry not found") : null;
        using var c = MakeClient(t);
        c.ClipboardTextWriterHook = _ => { };

        var ex = Assert.Throws<ClipboardIpcException>(
            () => c.MergePasteToActiveWindow(new[] { new ClipboardEntry { Id = "x" } }));
        Assert.Contains("无可粘贴文本", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- 表情包标记（与收藏同级的独立标记 · 2026-09-13） ----------------

    /// <summary>
    /// 标记走**独立 IPC**（`set_sticker`）而不是"导入文件"的一部分 ——
    /// 因为任何条目都能被标记（含**文字颜文字**），必须有"对任意 id 打标记"的通道。
    /// </summary>
    [Fact]
    public void SetSticker_ForwardsIdAndValue()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("set_sticker") ? Resp(req, "{\"ok\":true,\"isSticker\":true}") : null;
        using var c = MakeClient(t);

        c.SetSticker(new ClipboardEntry { Id = "abc123" }, true);

        var p = t.LastRequestOf("set_sticker")!.Value.GetProperty("params");
        Assert.Equal("abc123", p.GetProperty("id").GetString());
        Assert.True(p.GetProperty("value").GetBoolean());
    }

    /// <summary>取消标记必须原样转发 `value=false`（否则用户永远取消不掉）。</summary>
    [Fact]
    public void SetSticker_ForwardsFalseToUnmark()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("set_sticker") ? Resp(req, "{\"ok\":true,\"isSticker\":false}") : null;
        using var c = MakeClient(t);

        c.SetSticker(new ClipboardEntry { Id = "abc123" }, false);

        var p = t.LastRequestOf("set_sticker")!.Value.GetProperty("params");
        Assert.False(p.GetProperty("value").GetBoolean());
    }

    /// <summary>导入结果里的 `upgraded`（命中历史已有条目 → 只打标记、不新增）必须被解析出来。</summary>
    [Fact]
    public void AddStickers_ParsesUpgradedCount()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("add_sticker")
            ? Resp(req, "{\"ok\":true,\"added\":1,\"upgraded\":2,\"skipped\":3,\"ids\":[],\"errors\":[]}")
            : null;
        using var c = MakeClient(t);

        var r = c.AddStickers(new[] { "a.png" });

        Assert.Equal(1, r.Added);
        Assert.Equal(2, r.Upgraded);
        Assert.Equal(3, r.Skipped);
    }

    /// <summary>筛选走 `sticker` 参数（表情包是标记，不再是分类值）。</summary>
    [Fact]
    public void GetFilteredEntriesPage_ForwardsStickerFlag()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("\"query\"")
            ? Resp(req, "{\"total\":0,\"items\":[]}")
            : null;
        using var c = MakeClient(t);

        _ = c.GetFilteredEntriesPage(null, null, null, null, 0, 100, sticker: true);

        var p = t.LastRequestOf("query")!.Value.GetProperty("params");
        Assert.True(p.GetProperty("sticker").GetBoolean());
    }

    // ---------------- 按序粘贴状态机（纯逻辑 + 内容快照自包含） ----------------

    [Fact]
    public void SequentialPaste_StateMachine()
    {
        using var t = MakeTransport();
        using var c = MakeClient(t);
        Assert.False(c.IsSequentialPasteActive);
        c.BeginSequentialPaste(new[] { new ClipboardEntry { Id = "a" }, new ClipboardEntry { Id = "b" } });
        Assert.True(c.IsSequentialPasteActive);
        Assert.Equal(2, c.SequentialRemaining);
        c.CancelSequentialPaste();
        Assert.False(c.IsSequentialPasteActive);
        Assert.Equal(0, c.SequentialRemaining);
    }

    /// <summary>
    /// 【2026-09-12 用户实测"选了按序粘贴它会自己释放"】条目在引擎侧已失效（copy 报 not found）时，
    /// 必须用 BeginSequentialPaste 时抓好的**内容快照**把内容粘出去，绝不能静默跳过。
    /// </summary>
    [Fact]
    public void SequentialPaste_FallsBackToSnapshot_WhenEngineDroppedEntry()
    {
        using var t = MakeTransport();
        t.ServerHandler = req =>
        {
            if (req.Contains("\"get_entry\""))
            {
                return Resp(req, SampleEntry); // 开始时条目还在 → 快照抓到 "hello 中文"
            }
            if (req.Contains("\"copy_to_clipboard\""))
            {
                return Err(req, -32602, "entry not found: abc123"); // 粘贴时条目已被释放
            }
            return null;
        };
        using var c = MakeClient(t);
        c.PasteInjectorHook = () => { }; // 不往真实桌面注入按键

        SequentialClipboardPayload? written = null;
        c.SnapshotWriterHook = payload =>
        {
            written = payload;
            return true;
        };

        var issues = new List<string>();
        c.SequentialIssue += issues.Add;

        c.BeginSequentialPaste(new[] { new ClipboardEntry { Id = "abc123", ContentType = ClipboardItemKind.Text } });
        Assert.Equal(0, c.LastSequentialInvalidCount);

        c.PasteNextSequential();

        Assert.NotNull(written);
        Assert.Equal("hello 中文", written!.Text); // 来自引擎全文（勾选条目本身是空摘要载荷）
        Assert.Contains(issues, s => s.Contains("已被引擎释放", StringComparison.Ordinal));
        Assert.False(c.IsSequentialPasteActive);
    }

    /// <summary>
    /// 引擎写回失败且快照也不可用 → **显式失败**并报出提示。
    /// 旧实现在这里静默吞掉、序号照样前进 —— 那正是用户看到的"内容自己消失"。
    /// </summary>
    [Fact]
    public void SequentialPaste_ThrowsWhenNeitherEngineNorSnapshotAvailable()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => req.Contains("\"get_entry\"") || req.Contains("\"copy_to_clipboard\"")
            ? Err(req, -32602, "entry not found")
            : null;
        using var c = MakeClient(t);
        c.PasteInjectorHook = () => { };

        var issues = new List<string>();
        c.SequentialIssue += issues.Add;

        c.BeginSequentialPaste(new[] { new ClipboardEntry { Id = "gone", ContentType = ClipboardItemKind.Text } });
        Assert.Equal(1, c.LastSequentialInvalidCount);

        var ex = Assert.Throws<ClipboardIpcException>(() => c.PasteNextSequential());
        Assert.Contains("gone", ex.Message, StringComparison.Ordinal);
        Assert.Contains(issues, s => s.Contains("内容不可用", StringComparison.Ordinal));
    }

    /// <summary>条目仍在时走引擎写回（快照仅作失效兜底），且只注入一次按键。</summary>
    [Fact]
    public void SequentialPaste_UsesEnginePath_WhenEntryAlive()
    {
        using var t = MakeTransport();
        t.ServerHandler = req =>
        {
            if (req.Contains("\"get_entry\""))
            {
                return Resp(req, SampleEntry);
            }
            if (req.Contains("\"copy_to_clipboard\""))
            {
                return Resp(req, "{\"ok\":true}");
            }
            return null;
        };
        using var c = MakeClient(t);

        var injected = 0;
        c.PasteInjectorHook = () => injected++;
        var snapshotUsed = false;
        c.SnapshotWriterHook = _ =>
        {
            snapshotUsed = true;
            return true;
        };

        c.BeginSequentialPaste(new[] { new ClipboardEntry { Id = "abc123", ContentType = ClipboardItemKind.Text } });
        c.PasteNextSequential();

        Assert.Equal(1, injected);
        Assert.False(snapshotUsed); // 引擎路径成功 → 不得触发快照降级
        Assert.False(c.IsSequentialPasteActive);
    }

    // ---------------- 重连 ----------------

    [Fact]
    public void Reconnect_AfterDisconnect_RaisesReconnectedAndResubscribes()
    {
        using var t = MakeTransport();
        t.ServerHandler = req => Resp(req, "{\"ok\":true}");
        using var c = MakeClient(t);
        using var reconnected = new ManualResetEventSlim();
        c.Reconnected += reconnected.Set;
        reconnected.Reset();
        t.Disconnect();
        // 心跳间隔 2s + 重连退避 → 8s 内应完成
        Assert.True(reconnected.Wait(TimeSpan.FromSeconds(8)), "should reconnect");
        // 重连后重新发送 subscribe_events
        var last = t.Requests.Last();
        Assert.Contains("subscribe_events", last, StringComparison.Ordinal);
    }
}
