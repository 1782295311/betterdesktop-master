using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 索引引擎 IPC 客户端（M1 契约：ping / status / apply_settings / shutdown）。
///
/// 与引擎同构的**单线程统一读写**模型：工作线程 drain 发送队列 → PeekNamedPipe 探测 → 读帧分派 →
/// 空闲 20ms；规避「读线程阻塞 + 写线程 WriteFile 挂起」（剪贴板 S4 真机教训）。
/// 断线自动重连（500ms 起指数退避至 5s）→ 触发 <see cref="Reconnected"/>。
///
/// 【消费者降级契约】M2/M3 的接入方应优先使用 <see cref="TryGetStatusAsync"/> 这类「失败即 null」
/// 的包装，或在调用处捕获 <see cref="IndexIpcException"/> 后回退本地实现——
/// 绝不让 IPC 故障冒泡到 UI 线程（计划 §5.3）。
/// </summary>
public sealed class IndexIpcClient : IDisposable
{
    private readonly IIndexTransport _transport;
    private readonly int _timeoutMs;
    private readonly ConcurrentQueue<string> _sendQueue = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _connectLock = new();
    private long _nextId;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _connected;
    private DateTime _lastInbound = DateTime.UtcNow;
    private DateTime _lastHeartbeatAt = DateTime.MinValue;

    private const int HeartbeatIntervalMs = 2000;

    /// <summary>心跳响应等待上限：超时即判连接死亡并触发重连。</summary>
    private const int HeartbeatTimeoutMs = 3000;

    /// <summary>
    /// IPC 诊断痕迹 → <c>%LOCALAPPDATA%\BetterDesktop\logs\index-ipc-client.log</c>。
    /// 无它就只能靠猜：多进程（宿主/面板/托盘）行为差异无法观测——「假连接/真死亡」是在日志里
    /// 数出来的，不是在推理里想出来的。多进程追加、失败静默（诊断不得影响业务）。
    /// </summary>
    private static readonly string? TracePath = BuildTracePath();

    private static string? BuildTracePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "index-ipc-client.log");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Trace(string message)
    {
        if (TracePath is null)
        {
            return;
        }
        try
        {
            File.AppendAllText(
                TracePath,
                $"[{DateTime.Now:HH:mm:ss.fff}] [pid {Environment.ProcessId}] {message}\r\n");
        }
        catch (Exception)
        {
            // 诊断写入失败不阻断
        }
    }

    public IndexIpcClient(IIndexTransport transport, int timeoutMs = IndexIpcProtocol.DefaultTimeoutMs)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs;
    }

    /// <summary>当前是否连通（供消费者在发起调用前做廉价判定）。</summary>
    public bool IsConnected => _connected;

    /// <summary>连接建立（含断线重连后）——消费方可据此重推全量配置。</summary>
    public event Action? Reconnected;

    /// <summary>连接引擎并启动工作线程（可重复调用；幂等）。</summary>
    public void Connect()
    {
        lock (_connectLock)
        {
            if (_worker is { IsAlive: true })
            {
                return;
            }
            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "IndexIpcWorker" };
            _worker.Start();
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _transport.Dispose();
        _wake.Dispose();
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new IndexIpcException("client disposed"));
        }
        _pending.Clear();
    }

    // ---------------- 公开 RPC ----------------

    /// <summary>探活。引擎应答 → true（异常/超时 → false，不抛）。</summary>
    public async Task<bool> TryPingAsync(CancellationToken ct = default)
    {
        try
        {
            await PingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (IndexIpcException)
        {
            return false;
        }
    }

    /// <summary>探活（失败抛 <see cref="IndexIpcException"/>）。</summary>
    public async Task PingAsync(CancellationToken ct = default)
    {
        await Task.Run(() => CallCore(IndexIpcProtocol.M_Ping, null), ct).ConfigureAwait(false);
    }

    /// <summary>读取引擎状态。</summary>
    public async Task<IndexStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var element = await Task.Run(() => CallCore(IndexIpcProtocol.M_Status, null), ct)
            .ConfigureAwait(false);
        return DeserializeStatus(element);
    }

    /// <summary>
    /// 降级友好包装：任何 IPC 故障（未连接/超时/引擎报错/解析失败）→ null。
    /// **M2/M3 的消费者应优先用它**，据 null 决定回退本地实现。
    /// </summary>
    public async Task<IndexStatus?> TryGetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (IndexIpcException)
        {
            return null;
        }
    }

    /// <summary>读取应用索引快照（raw candidate）。</summary>
    public async Task<ListAppsResult> ListAppsAsync(CancellationToken ct = default)
    {
        var element = await Task.Run(() => CallCore(IndexIpcProtocol.M_ListApps, null), ct)
            .ConfigureAwait(false);
        return element.Deserialize<ListAppsResult>(JsonOptions)
            ?? throw new IndexIpcException("list_apps 响应为空");
    }

    /// <summary>
    /// 查询文件索引（常驻内存子串匹配）。返回的是**候选**（引擎只做「前缀命中优先」两轮收集），
    /// 排名与分类仍由调用方（`FileSearchProvider`）负责。
    /// </summary>
    /// <param name="query">文件名子串；空白查询引擎返回空集（不返全量）。</param>
    /// <param name="limit">返回上限；≤0 用引擎默认（200）。</param>
    public async Task<SearchFilesResult> SearchFilesAsync(
        string query,
        int limit = 0,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (limit > 0)
        {
            payload["limit"] = limit;
        }

        var element = await Task.Run(() => CallCore(IndexIpcProtocol.M_SearchFiles, payload), ct)
            .ConfigureAwait(false);
        return element.Deserialize<SearchFilesResult>(JsonOptions)
            ?? throw new IndexIpcException("search_files 响应为空");
    }

    /// <summary>
    /// 降级友好包装：IPC 故障 **或**「构建中 / 降级」→ <c>null</c>，调用方据此回退本地实现。
    /// <para>为什么「构建中」也返回 null：空集会被用户读成「没有这个文件」，
    /// 而真相是「索引还没建好」——回退本地扫描才是正确语义（计划 §5.3 降级必须可见）。</para>
    /// </summary>
    public async Task<SearchFilesResult?> TrySearchFilesAsync(
        string query,
        int limit = 0,
        CancellationToken ct = default)
    {
        try
        {
            var result = await SearchFilesAsync(query, limit, ct).ConfigureAwait(false);
            return result.Building || result.Degraded ? null : result;
        }
        catch (IndexIpcException)
        {
            return null;
        }
    }

    /// <summary>
    /// 批量取图标：引擎一律返回 **256×256 PNG**（用户硬约束：不做多档），显示端自己倍缩。
    /// </summary>
    /// <param name="keys">
    /// 缓存键 = 图标来源路径（**.lnk 先解析成目标 exe**；UWP 传 AUMID）。
    /// 空白键会被过滤；过滤后为空则直接返回空结果（不发 RPC）。
    /// </param>
    /// <param name="refresh">
    /// <c>true</c> = 先清引擎侧缓存再取。**应用更新后路径不变而图标已变**时必需，
    /// 否则旧图标会永久驻留（引擎缓存键就是路径）。
    /// <para>仅**首个分片**携带 refresh：每个分片都清会让刚取回的图标立刻被下一片挤掉。</para>
    /// </param>
    /// <remarks>
    /// 超过 <see cref="IndexIpcProtocol.MaxIconBatch"/> 时**自动分片**（引擎对超限批次返回 -32602）——
    /// 让调用方不可能踩到「批次超限 → 图标静默消失」这个坑。
    /// </remarks>
    public async Task<GetIconsResult> GetIconsAsync(
        IEnumerable<string> keys,
        bool refresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var list = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (list.Length == 0)
        {
            return new GetIconsResult();
        }

        var icons = new List<IconHit>(list.Length);
        var missing = 0;
        var first = true;
        foreach (var chunk in ChunkKeys(list, IndexIpcProtocol.MaxIconBatch))
        {
            var payload = BuildIconKeysPayload(chunk, refresh && first);
            first = false;

            var element = await Task.Run(() => CallCore(IndexIpcProtocol.M_GetIcons, payload), ct)
                .ConfigureAwait(false);
            var part = element.Deserialize<GetIconsResult>(JsonOptions)
                ?? throw new IndexIpcException("get_icons 响应为空");

            icons.AddRange(part.Icons);
            missing += part.Missing;
        }

        return new GetIconsResult { Icons = icons, Count = icons.Count, Missing = missing };
    }

    /// <summary>
    /// 降级友好包装：任何 IPC 故障 → <c>null</c>（调用方回退本地 Shell 提取 + glyph 兜底）。
    /// <para>与 <c>TrySearchFilesAsync</c> 的差别：图标**没有** building/degraded 语义 ——
    /// 图标是按需提取的，不依赖索引快照，构建中照样能取。故只有 IPC 故障才降级。</para>
    /// </summary>
    public async Task<GetIconsResult?> TryGetIconsAsync(
        IEnumerable<string> keys,
        bool refresh = false,
        CancellationToken ct = default)
    {
        try
        {
            return await GetIconsAsync(keys, refresh, ct).ConfigureAwait(false);
        }
        catch (IndexIpcException)
        {
            return null;
        }
    }

    /// <summary>下发增量设置（引擎侧非法值会返回 -32602，此处抛 <see cref="IndexIpcException"/>）。</summary>
    public async Task ApplySettingsAsync(IndexSettingsPatch patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var payload = BuildSettingsPayload(patch);
        await Task.Run(() => CallCore(IndexIpcProtocol.M_ApplySettings, payload), ct)
            .ConfigureAwait(false);
    }

    /// <summary>请求引擎优雅退出。</summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await Task.Run(() => CallCore(IndexIpcProtocol.M_Shutdown, null), ct).ConfigureAwait(false);
    }

    // ---------------- 协议构造（可单测） ----------------

    /// <summary>
    /// 构造 apply_settings 的下发载荷：**显式 kebab-case 键**（与引擎 settings.rs 对齐），
    /// 仅包含非 null 字段。不使用命名策略——两侧策略漂移会导致请求被静默忽略。
    /// </summary>
    internal static Dictionary<string, object?> BuildSettingsPayload(IndexSettingsPatch patch)
    {
        var payload = new Dictionary<string, object?>();
        if (patch.AppSourceBackend is not null)
        {
            payload["app-source-backend"] = patch.AppSourceBackend;
        }
        if (patch.MaxEntries is not null)
        {
            payload["max-entries"] = patch.MaxEntries;
        }
        if (patch.ScanRoots is not null)
        {
            payload["scan-roots"] = patch.ScanRoots;
        }
        if (patch.Enabled is not null)
        {
            payload["enabled"] = patch.Enabled;
        }
        return payload;
    }

    /// <summary>
    /// 构造 get_icons 的下发载荷：键名 <c>keys</c> / <c>refresh</c>（引擎按 camelCase 解析）。
    /// <c>refresh=false</c> 时**不下发该键**（引擎默认 false）——避免两侧各写一份默认值。
    /// </summary>
    internal static Dictionary<string, object?> BuildIconKeysPayload(
        IReadOnlyList<string> keys,
        bool refresh)
    {
        var payload = new Dictionary<string, object?> { ["keys"] = keys };
        if (refresh)
        {
            payload["refresh"] = true;
        }

        return payload;
    }

    /// <summary>按上限切分键集合（引擎对超限批次返回 -32602，故此处保证永不超限）。</summary>
    internal static IEnumerable<IReadOnlyList<string>> ChunkKeys(
        IReadOnlyList<string> keys,
        int size)
    {
        for (int i = 0; i < keys.Count; i += size)
        {
            var chunk = new List<string>(Math.Min(size, keys.Count - i));
            for (int j = i; j < Math.Min(i + size, keys.Count); j++)
            {
                chunk.Add(keys[j]);
            }

            yield return chunk;
        }
    }

    /// <summary>构造一条 JSON-RPC 请求体（不含 magic 前缀）。</summary>
    internal static string BuildRequest(long id, string method, object? parameters)
    {
        var body = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            body["params"] = parameters;
        }
        return JsonSerializer.Serialize(body);
    }

    /// <summary>反序列化 status 响应（camelCase，大小写不敏感）。</summary>
    internal static IndexStatus DeserializeStatus(JsonElement element) =>
        element.Deserialize<IndexStatus>(JsonOptions)
        ?? throw new IndexIpcException("status 响应为空");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // C1：应答来自**命名管道**（外部输入）→ 显式限深，不依赖库默认值。
        MaxDepth = 32,
    };

    /// <summary>入站帧的解析选项：同样来自管道，故同样限深。</summary>
    private static readonly JsonDocumentOptions FrameOptions = new() { MaxDepth = 32 };

    // ---------------- 工作线程 ----------------

    private void WorkerLoop()
    {
        while (_running)
        {
            try
            {
                if (!_connected)
                {
                    TryConnectWithRetry();
                    continue;
                }

                DrainSendQueue();
                while (_transport.IsDataAvailable())
                {
                    var frame = _transport.ReadFrame();
                    if (frame is null)
                    {
                        OnDisconnected("pipe closed");
                        break;
                    }
                    DispatchFrame(frame);
                }

                // 心跳与断线判定（照抄剪贴板引擎 2026-09-12 真机重写结论）：
                //   · 每 HeartbeatIntervalMs **至多**一次 ping（必须节流，否则「发心跳 → 唤醒 → 再发」
                //     正反馈风暴会把响应处理拖垮 → 误判超时 → 断开重连 → 无限循环）；
                //   · 健康信号只看 _lastInbound（任何入站帧都刷新），超时即判死。
                if (_connected)
                {
                    var now = DateTime.UtcNow;
                    var idle = now - _lastInbound;
                    if (idle.TotalMilliseconds > HeartbeatIntervalMs
                        && (now - _lastHeartbeatAt).TotalMilliseconds > HeartbeatIntervalMs)
                    {
                        _lastHeartbeatAt = now;
                        SendHeartbeat();
                    }
                    if (idle.TotalMilliseconds > HeartbeatTimeoutMs)
                    {
                        OnDisconnected($"heartbeat timeout (no inbound for {idle.TotalMilliseconds:0}ms)");
                    }
                }
            }
            catch (Exception ex)
            {
                // 【生死线】工作线程必须永生：任何未预期异常一旦逃出循环，线程即死亡 →
                // 之后永不重连，而 UI 仍显示「正在重试…」（假重试真死亡）。
                Trace($"worker loop exception: {ex.GetType().Name}: {ex.Message}");
                OnDisconnected(ex.Message);
                Thread.Sleep(100); // 防忙等
            }

            // 等待必须纳入 try 保护：Dispose 与工作线程存在竞态（_wake 已释放时抛
            // ObjectDisposedException，异常逃出循环会终止整个进程）。
            try
            {
                _wake.Wait(20);
                _wake.Reset();
            }
            catch (ObjectDisposedException)
            {
                break; // _wake 已释放 = Dispose 路径，正常退出
            }
        }
    }

    private void TryConnectWithRetry()
    {
        int delay = 500;
        while (_running && !_connected)
        {
            try
            {
                _transport.Connect();
                // 首帧即真实请求（引擎首帧强制校验 magic；客户端每帧恒携带 magic）
                _connected = true;
                _lastInbound = DateTime.UtcNow;
                _lastHeartbeatAt = DateTime.MinValue;
                Trace("connected");
                PublishReconnected();
                return;
            }
            catch (Exception ex)
            {
                // 不再限定异常类型：只 catch 部分类型会让其它异常杀死重连线程 → 永不重连
                Trace($"connect failed: {ex.GetType().Name}: {ex.Message}; retry in {delay}ms");
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 5000);
            }
        }
    }

    /// <summary>
    /// Reconnected 必须**异步派发**：订阅者的典型用法是「重连后全量刷新」（会发同步 RPC），
    /// 若在读线程同步回调，该 RPC 的响应需要同一线程读取 → 自死锁。
    /// </summary>
    private void PublishReconnected()
    {
        if (Reconnected is not { } handler)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Trace($"reconnected handler failed: {ex.Message}");
            }
        });
    }

    private void SendHeartbeat()
    {
        long id = Interlocked.Increment(ref _nextId);
        _pending[id] = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendQueue.Enqueue(IndexIpcProtocol.Magic + BuildRequest(id, IndexIpcProtocol.M_Ping, null));
    }

    private void DrainSendQueue()
    {
        while (_sendQueue.TryDequeue(out var frame))
        {
            _transport.WriteFrame(frame);
        }
    }

    private void OnDisconnected(string reason)
    {
        if (!_connected)
        {
            return;
        }
        _connected = false;
        Trace($"disconnected: {reason}");
        try
        {
            _transport.Disconnect();
        }
        catch
        {
            // 忽略清理异常
        }
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new IndexIpcException("index engine disconnected"));
        }
        _pending.Clear();

        // 同时清空发送队列：已入队未写出的帧若留到重连后重放，会让非幂等命令被重复执行
        _sendQueue.Clear();
    }

    private void DispatchFrame(string frame)
    {
        _lastInbound = DateTime.UtcNow;
        try
        {
            using var doc = JsonDocument.Parse(frame, FrameOptions);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
            {
                // 无 id：M1 无事件推送，忽略（M2 引入订阅时在此分派 notify）
                Debug.WriteLine($"[IndexIpc] unhandled frame: {frame}");
                return;
            }
            long id = idProp.GetInt64();
            if (!_pending.TryRemove(id, out var tcs))
            {
                return;
            }
            if (root.TryGetProperty("error", out var err))
            {
                int code = err.TryGetProperty("code", out var c) ? c.GetInt32() : IndexIpcProtocol.ErrInternal;
                string message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown" : "unknown";
                tcs.TrySetException(new IndexIpcException(message, code));
                return;
            }
            if (root.TryGetProperty("result", out var result))
            {
                tcs.TrySetResult(result.Clone());
                return;
            }
            tcs.TrySetException(new IndexIpcException("malformed response: missing result"));
        }
        catch (JsonException ex)
        {
            Trace($"frame parse failed: {ex.Message}");
        }
    }

    /// <summary>同步 RPC 调用（由公开方法经 Task.Run 包装，避免阻塞调用线程）。</summary>
    private JsonElement CallCore(string method, object? parameters)
    {
        if (!_connected)
        {
            throw new IndexIpcException("index engine not connected");
        }

        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _sendQueue.Enqueue(IndexIpcProtocol.Magic + BuildRequest(id, method, parameters));
        _wake.Set();

        bool completed;
        try
        {
            completed = tcs.Task.Wait(_timeoutMs);
        }
        catch (AggregateException ex) when (ex.InnerException is IndexIpcException inner)
        {
            // 【生死线】Task.Wait 在任务已故障时抛 AggregateException —— 若不在此解包，
            // 引擎返回的 JSON-RPC 错误（如 -32602）会以 AggregateException 形式冒给调用方，
            // 既丢错误码、又让「按错误码分流」的回退逻辑失效。
            _pending.TryRemove(id, out _);
            throw inner;
        }

        if (!completed)
        {
            _pending.TryRemove(id, out _);
            throw new IndexIpcException($"RPC timeout: {method}");
        }

        try
        {
            return tcs.Task.GetAwaiter().GetResult();
        }
        catch (IndexIpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IndexIpcException($"RPC failed: {method}", ex);
        }
    }
}
