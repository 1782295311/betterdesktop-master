using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 剪贴板引擎 IPC 代理（IClipboardService 全契约实现）。
/// 与引擎同构的单线程统一读写模型：工作线程 drain 发送队列 → PeekNamedPipe 探测 → 读帧分派 → 空闲 20ms，
/// 规避「读线程阻塞 + 写线程 WriteFile 挂起」（S4 真机教训）。
/// 断线自动重连（500ms 起指数退避至 5s）→ 重新订阅事件 → 触发 <see cref="Reconnected"/>。
/// 事件线程分派：history_changed / pause_changed / monitoring_changed / clipboard_changed（扩展，灵动岛用）。
/// </summary>
public sealed class ClipboardIpcClient : IClipboardService, IDisposable
{
    private readonly IClipboardTransport _transport;
    private readonly int _timeoutMs;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentQueue<string> _sendQueue = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _connectLock = new();
    private readonly object _stateLock = new();
    private readonly object _seqLock = new();
    private long _nextId;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _connected;
    private System.Threading.Timer? _pauseTimer;
    private bool _isTemporarilyPaused;
    private DateTime? _resumeAt;
    private List<SequentialItem>? _seqQueue;
    private int _seqIndex;

    /// <summary>
    /// IPC 诊断痕迹（连接成功/失败/断开/心跳超时）→ %LOCALAPPDATA%\BetterDesktop\logs\ipc-client.log。
    /// 【2026-09-12 真机教训】没有它就只能靠猜：面板能连上而宿主连不上（或反之）时，
    /// 两端行为差异无法观测——"假连接/真死亡"是在日志里数出来的，不是在推理里想出来的。
    /// 多进程追加、失败静默（诊断不得影响业务）。
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
            return Path.Combine(dir, "ipc-client.log");
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

    public ClipboardIpcClient(IClipboardTransport transport, int timeoutMs = IpcProtocol.DefaultTimeoutMs)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // C1：应答来自**命名管道**（外部输入）→ 显式限深，不依赖库默认值。
            MaxDepth = 32,
        };
    }

    /// <summary>连接引擎并启动事件订阅线程（可重复调用；幂等）。</summary>
    public void Connect()
    {
        lock (_connectLock)
        {
            if (_worker is { IsAlive: true })
            {
                return;
            }
            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ClipboardIpcWorker" };
            _worker.Start();
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        // 【2026-09-18】Join 由 2s 放宽到 5s：工作线程最长可能正卡在一次"有上限的管道写"里
        //（写超时 3s，见 NamedPipeTransport.WriteFrame）。等待不够就 Dispose _wake/_transport，
        // 会让线程在释放后的对象上继续跑（旧代码只能靠 catch ObjectDisposedException 兜底）。
        _worker?.Join(TimeSpan.FromSeconds(5));
        _pauseTimer?.Dispose();
        _transport.Dispose();
        _wake.Dispose();
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new ClipboardIpcException("client disposed"));
        }
        _pending.Clear();
    }

    // ---------------- 事件（契约 + 扩展） ----------------

    public event Action<ClipboardHistoryChangedEventArgs>? HistoryChanged;
    public event Action<bool>? PauseStateChanged;

    /// <summary>扩展：灵动岛/搜索框的轻量复制摘要（引擎 clipboard_changed）。</summary>
    public event Action<ClipboardChangedInfo>? ClipboardChanged;

    /// <summary>扩展：连接建立（含断线重连后）；面板据此全量刷新。</summary>
    public event Action? Reconnected;

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

                // 心跳与断线判定（2026-09-12 重写 · 真机血案）：
                //   · 每 HeartbeatIntervalMs **至多**发一次 ping —— 必须节流：早期实现里 SendHeartbeat
                //     自己 `_wake.Set()` 唤醒循环，而触发条件（_lastInbound 未刷新）仍成立 →
                //     "发心跳 → 唤醒 → 再发"正反馈风暴（实测 7ms 内 12 个心跳），响应处理被拖垮 →
                //     3s 内等不到响应 → 误判超时 → 断开重连 → 无限循环
                //（这就是宿主/面板 IPC "时好时坏、engine not connected" 的真身）。
                //   · 健康信号只看 _lastInbound（任何入站帧都会刷新）；HeartbeatTimeoutMs 无入站即判死。
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
                // 【生死线 · 2026-09-12 真机实测】工作线程必须永生：任何未预期异常
                //（ObjectDisposedException / 管道竞态 / 心跳回调并发 OnDisconnected）
                // 一旦逃出循环，线程即死亡 → 之后**永不重连**，而 UI 仍显示"引擎未连接，正在重试…"
                //（假重试真死亡）——症状即"引擎明明在跑却一直提示未连接"。
                Trace($"worker loop exception: {ex.GetType().Name}: {ex.Message}");
                OnDisconnected(ex.Message);
                Thread.Sleep(100); // 防忙等（异常持续时不让循环空转）
            }

            // 【2026-09-12 修复】把等待纳入 try 保护：此前它在 try/catch **之外** ——
            // Dispose() 与工作线程存在竞态（Dispose 先 Join(2s)，若线程正阻塞在慢 WriteFrame 则
            // Join 超时，随后 _wake.Dispose()，线程回来执行到此处抛 ObjectDisposedException，
            // 异常逃出循环 → .NET 中任意线程的未处理异常会**终止整个进程**）。
            try
            {
                // 【2026-09-18】空闲降频：本包是**零 kernel 依赖**的底层 IPC 包，拿不到
                // SystemPowerMonitor 的挂起状态（不能像其它组件那样接电源门控），
                // 但 20ms/50Hz 常驻唤醒在"整天没人复制东西"时纯属浪费电。
                // 现在按活跃度自适应：有在途请求或刚有流量 → 20ms（低延迟）；
                // 完全空闲（无在途请求且 1 秒内无入站）→ 50ms（对剪贴板事件的观感延迟仍不可感知）。
                var idle = (DateTime.UtcNow - _lastInbound).TotalMilliseconds;
                _wake.Wait(_pending.IsEmpty && idle > 1000 ? IdleWaitMs : ActiveWaitMs);
                _wake.Reset();
            }
            catch (ObjectDisposedException)
            {
                break; // _wake 已释放 = Dispose 路径，正常退出循环
            }
        }
    }

    private const int HeartbeatIntervalMs = 2000;

    /// <summary>
    /// 等待"连接就绪"的上限。
    /// 【2026-09-18】原来写死 2 秒：引擎没起来时，**每一次**调用（含 UI 线程上的）都要先白等 2 秒才失败。
    /// 收敛到 1 秒仍是"够启动"的余量（工作线程 500ms 起退避重连，正常 1s 内必连上）。
    /// </summary>
    private const int ConnectTimeoutMs = 1000;

    /// <summary>连续 RPC 超时多少次即判定连接已死并触发重连（见 <see cref="CallAsync"/>）。</summary>
    private const int TimeoutStrikesBeforeReconnect = 2;

    /// <summary>空闲轮询周期：无在途请求且 1 秒内无入站流量时使用（有活时用 <see cref="ActiveWaitMs"/>）。</summary>
    private const int IdleWaitMs = 50;

    /// <summary>活跃轮询周期：有在途请求或刚有流量（保证事件低延迟）。</summary>
    private const int ActiveWaitMs = 20;

    /// <summary>「打开历史面板」的超时：必须**快失败**，好让降级路径（直接拉起面板 exe）立刻接手。</summary>
    private const int OpenPanelTimeoutMs = 800;

    /// <summary>
    /// UI 线程上同步 RPC 的超时上限（比默认值短）：界面最多冻 3 秒，而不是 5 秒。
    /// 判定用 <see cref="SynchronizationContext.Current"/>（WPF 的 UI 线程必有；本包无 WPF 依赖，
    /// 这是纯 BCL 能拿到的最接近的判据）。
    /// </summary>
    private const int UiThreadTimeoutMs = 3000;

    /// <summary>已提示过的"UI 线程同步 RPC"方法名（每个只提示一次，避免刷屏；作为迁移 CallAsync 的清单）。</summary>
    private static readonly ConcurrentDictionary<string, bool> UiThreadCallWarned = new();

    /// <summary>心跳响应等待上限：超时即判连接死亡并触发重连（见 SendHeartbeat 注释）。</summary>
    private const int HeartbeatTimeoutMs = 3000;

    private DateTime _lastInbound = DateTime.UtcNow;

    /// <summary>上次发出心跳的时刻（心跳节流用；见 WorkerLoop 内的正反馈注释）。</summary>
    private DateTime _lastHeartbeatAt = DateTime.MinValue;

    /// <summary>连续 RPC 超时计数（任何入站响应都会清零）。见 <see cref="CallAsync"/> 的超时策略。</summary>
    private int _consecutiveTimeouts;

    /// <summary>
    /// 发一次心跳 ping。**节流由调用方保证**，且**不唤醒发送循环**（<c>_wake.Set()</c> 会让循环
    /// 立即空转重发 → 心跳风暴，见 WorkerLoop 注释）；写出交由下一轮 DrainSendQueue。
    /// 健康判定不依赖本方法的返回值——只看 <see cref="_lastInbound"/> 是否按时刷新。
    /// </summary>
    private void SendHeartbeat()
    {
        long id = Interlocked.Increment(ref _nextId);
        _pending[id] = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendQueue.Enqueue(IpcProtocol.Magic + BuildRequest(id, IpcProtocol.M_Ping, null));
    }

    private void TryConnectWithRetry()
    {
        int delay = 500;
        while (_running && !_connected)
        {
            try
            {
                _transport.Connect();
                // 首帧 magic + 订阅事件（引擎首帧强制校验 magic）
                _transport.WriteFrame(IpcProtocol.Magic + BuildRequest(IpcProtocol.M_Subscribe, null));
                _connected = true;
                Debug.WriteLine($"[ClipboardIpc] connected to {IpcProtocol.PipeName}");
                Trace("connected");

                // 【纪律 · 2026-09-12】Reconnected 必须**异步派发**：订阅者的典型用法是
                // "重连后全量刷新"（会发同步 RPC），若在读线程同步回调，该 RPC 的响应需要同一线程
                // 读取 → 自死锁（实测 5s 超时）。故统一交由线程池执行。
                if (Reconnected is { } handler)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            handler();
                        }
                        catch (Exception handlerEx)
                        {
                            Debug.WriteLine($"[ClipboardIpc] reconnected handler failed: {handlerEx.Message}");
                        }
                    });
                }
                return;
            }
            catch (Exception ex)
            {
                // 【2026-09-12 收口】不再限定异常类型：旧版只 catch
                // IOException/TimeoutException/UnauthorizedAccessException，其它异常
                //（ObjectDisposedException 等）会直接杀死重连线程 → 永不重连。
                Debug.WriteLine($"[ClipboardIpc] connect failed: {ex.GetType().Name}: {ex.Message}; retry in {delay}ms");
                Trace($"connect failed: {ex.GetType().Name}: {ex.Message}; retry in {delay}ms");
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 5000);
            }
        }
    }

    private void OnDisconnected(string reason)
    {
        if (!_connected)
        {
            return;
        }
        _connected = false;
        Debug.WriteLine($"[ClipboardIpc] disconnected: {reason}");
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
            tcs.TrySetException(new ClipboardIpcException("engine disconnected"));
        }
        _pending.Clear();

        // 【2026-09-12 修复】同时清空发送队列：已入队但未写出的帧若留到重连后重放，
        // 会让**非幂等命令**（delete_many / import / clear_unpinned）被重复执行一次 ——
        // 而调用方其实已收到"engine disconnected"异常。丢弃更安全（调用方可重试）。
        _sendQueue.Clear();
    }

    /// <summary>等待连接就绪（首次 Connect 后工作线程异步建立；真实场景面板启动即调用需此门）。</summary>
    /// <summary>等待连接就绪；**用 Task.Delay 而非 Thread.Sleep**（后者会把调用线程钉住 2 秒）。</summary>
    private async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_connected)
        {
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (!_connected && sw.Elapsed < timeout)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return _connected;
    }

    private void DrainSendQueue()
    {
        while (_sendQueue.TryDequeue(out var frame))
        {
            _transport.WriteFrame(frame);
        }
    }

    private void DispatchFrame(string frame)
    {
        _lastInbound = DateTime.UtcNow;
        var json = frame.StartsWith(IpcProtocol.Magic, StringComparison.Ordinal)
            ? frame[IpcProtocol.Magic.Length..]
            : frame;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            long id = idEl.GetInt64();
            // 有响应 = 链路活着（哪怕响应本身是错误）：清零"连续超时"计数。
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
            if (_pending.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var err))
                {
                    long code = err.TryGetProperty("code", out var c) ? c.GetInt64() : 0;
                    string message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "rpc error" : "rpc error";
                    tcs.TrySetException(ClipboardIpcException.Rpc(code, message));
                }
                else if (root.TryGetProperty("result", out var res))
                {
                    tcs.TrySetResult(res.Clone());
                }
                else
                {
                    tcs.TrySetException(new ClipboardIpcException("malformed response (no result/error)"));
                }
            }
        }
        else
        {
            DispatchNotification(root);
        }
    }

    private void DispatchNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var m) || !root.TryGetProperty("params", out var p))
        {
            return;
        }
        var method = m.GetString();
        switch (method)
        {
            case IpcProtocol.N_HistoryChanged:
                DispatchHistoryChanged(p);
                break;
            case IpcProtocol.N_PauseChanged:
                if (p.TryGetProperty("paused", out var paused))
                {
                    lock (_stateLock)
                    {
                        _isTemporarilyPaused = paused.GetBoolean();
                        if (!_isTemporarilyPaused)
                        {
                            _resumeAt = null;
                        }
                    }
                    PauseStateChanged?.Invoke(_isTemporarilyPaused);
                }
                break;
            case IpcProtocol.N_ClipboardChanged:
                ClipboardChanged?.Invoke(ParseClipboardChanged(p));
                break;
        }
    }

    private void DispatchHistoryChanged(JsonElement p)
    {
        var kind = p.TryGetProperty("kind", out var k) ? k.GetString() : IpcProtocol.K_Added;
        var change = kind switch
        {
            IpcProtocol.K_Updated => ClipboardChangeKind.Updated,
            IpcProtocol.K_Deleted => ClipboardChangeKind.Removed,
            IpcProtocol.K_ClearedUnpinned => ClipboardChangeKind.Cleared,
            _ => ClipboardChangeKind.Added,
        };
        string id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        HistoryChanged?.Invoke(new ClipboardHistoryChangedEventArgs(change, id));
    }

    private static ClipboardChangedInfo ParseClipboardChanged(JsonElement p)
    {
        int type = p.TryGetProperty("contentType", out var ct) ? ct.GetInt32() : 0;
        DateTime copiedAt = p.TryGetProperty("copiedAt", out var ts) && DateTime.TryParse(ts.GetString(), out var d)
            ? d
            : DateTime.Now;
        return new ClipboardChangedInfo(
            p.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            (ClipboardItemKind)type,
            p.TryGetProperty("textPreview", out var tp) ? tp.GetString() ?? string.Empty : string.Empty,
            p.TryGetProperty("thumbPath", out var th) ? th.GetString() ?? string.Empty : string.Empty,
            p.TryGetProperty("sourceApp", out var sa) ? sa.GetString() ?? string.Empty : string.Empty,
            copiedAt);
    }

    // ---------------- RPC ----------------

    /// <summary>
    /// 请求序列化选项：**必须 camelCase**。
    /// <para>
    /// 【2026-09-12 修复】此前用默认 `JsonSerializer.Serialize`（PascalCase）—— 匿名 payload 的属性
    /// 恰好都是小写开头（`new { id = … }`）所以一直没暴露；但 `import` 传的是 `List&lt;ClipboardEntry&gt;`，
    /// 会被序列化成 `"Id"`/`"ContentType"`，而引擎 `model.rs` 是 `#[serde(rename_all = "camelCase")]`
    /// 且必填字段无 default → 解析失败 `invalid entries`，**ImportEntries 恒失败**。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string BuildRequest(long id, string method, object? payload)
    {
        var obj = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = payload,
        };
        return JsonSerializer.Serialize(obj, RequestOptions);
    }

    private static string BuildRequest(string method, object? payload) => BuildRequest(0, method, payload);

    /// <summary>同步 RPC（兼容既有调用方）。**在 UI 线程上调用前请先读 CallAsync 的注释。**</summary>
    private JsonElement Call(string method, object? payload = null) => Call(method, payload, _timeoutMs);

    /// <summary>
    /// 带显式超时的同步 RPC —— 给"必须快失败"的 UI 入口用（例如菜单栏点击打开剪贴板面板）。
    /// <para>
    /// 【2026-09-18 审计：同步 RPC 会冻结界面】单次调用最坏耗时 = 等连接就绪 + RPC 超时，
    /// 旧实现是 <b>2s + 5s = 7 秒</b>且发生在**调用线程**上；面板/宿主在 UI 线程上大量调用本类，
    /// 于是引擎半死时界面直接"卡住不动"（用户观感：点了没反应、窗口像死了）。
    /// 现在：① 等连接就绪收敛到 <see cref="ConnectTimeoutMs"/>；② 超时会累计并触发重连（见 CallAsync）；
    /// ③ 新增 <see cref="CallAsync"/>，UI 路径应优先用；④ 关键入口可把超时压到亚秒级。
    /// </para>
    /// </summary>
    private JsonElement Call(string method, object? payload, int timeoutMs)
    {
        // 【2026-09-18】UI 线程上的同步 RPC 有两处收紧：
        //   ① 超时压到 UiThreadTimeoutMs（界面最多冻 3 秒，而不是 5 秒）；
        //   ② 每个方法名留一条**只记一次**的痕迹 → 这就是"还有哪些调用锁着 UI 线程"的迁移清单。
        // 为什么用痕迹而不是直接改所有调用方：本包零 kernel 依赖、也无法在这里判断业务上下文，
        // 先保证"不再长时间冻界面 + 有据可查"，再把具体调用点迁到 CallAsync。
        if (SynchronizationContext.Current is not null && timeoutMs > UiThreadTimeoutMs)
        {
            if (UiThreadCallWarned.TryAdd(method, true))
            {
                Trace($"UI 线程同步 RPC（建议改 CallAsync）：{method}");
            }

            timeoutMs = UiThreadTimeoutMs;
        }

        return CallAsync(method, payload, timeoutMs, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 异步 RPC（推荐路径）：**不占用调用线程**，适合 UI 线程调用。
    /// <para>
    /// 超时策略：单次超时只算本次失败；**连续 <see cref="TimeoutStrikesBeforeReconnect"/> 次**超时即判定
    /// 连接已死并主动断开重连 —— 这是"引擎不读管道时连接永不恢复"的修复：旧实现超时后连接状态仍为
    /// "已连接"，之后每一次调用都必然再超时，用户只能重启面板/宿主才能恢复。
    /// </para>
    /// </summary>
    /// <param name="method">RPC 方法名（见 IpcProtocol）。</param>
    /// <param name="payload">请求参数（匿名对象即可，序列化恒 camelCase）。</param>
    /// <param name="timeoutMs">本次超时；null = 构造时给的默认值。</param>
    /// <param name="cancellationToken">取消令牌（取消即立刻抛出，不等待超时）。</param>
    public async Task<JsonElement> CallAsync(
        string method,
        object? payload = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = timeoutMs ?? _timeoutMs;

        if (!await WaitConnectedAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ClipboardIpcException("engine not connected");
        }

        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _sendQueue.Enqueue(IpcProtocol.Magic + BuildRequest(id, method, payload));
        _wake.Set();
        try
        {
            return await tcs.Task
                .WaitAsync(TimeSpan.FromMilliseconds(timeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var strikes = Interlocked.Increment(ref _consecutiveTimeouts);
            if (strikes >= TimeoutStrikesBeforeReconnect)
            {
                // 主动判死：让工作线程走 OnDisconnected → 重连（否则"假连接"会一直接着超时）。
                OnDisconnected($"rpc timeout x{strikes}: {method}");
            }

            throw new ClipboardIpcException($"ipc timeout ({timeout}ms): {method}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // ---------------- IClipboardService：查询 ----------------

    /// <summary>
    /// 扩展（S6 §6.3 分页契约成员）：面板按页拉取。
    /// 【2026-09-12 搜索/筛选下沉引擎】全部条件直通引擎 <c>query</c>：引擎侧先过滤再分页，
    /// <c>total</c> = **过滤后**命中总数 → UI 永远只取一页。
    /// 早前 keyword/kind/sourceApp 走「FetchAll 全库 + 本地 LINQ」，打一个字就把整库（含 HTML 正文）
    /// 序列化到 UI 线程，分页形同虚设（万条规模必卡），该路径已删除。
    /// </summary>
    public (IReadOnlyList<ClipboardEntry> Items, int Total) GetFilteredEntriesPage(
        ClipboardItemKind? kind,
        ContentCategory? category,
        string? keyword,
        string? sourceApp,
        int offset,
        int limit,
        bool? pinned = null,
        bool? sticker = null)
    {
        limit = Math.Clamp(limit, 1, IpcProtocol.MaxPageSize);
        var page = QueryPage(kind, category, keyword, sourceApp, pinned, sticker, offset, limit);
        return (page.Items, page.Total);
    }

    public LastCopiedContent? GetLastCopiedContent(TimeSpan? timeLimit = null)
    {
        var e = GetLastEntry(timeLimit);
        if (e is null)
        {
            return null;
        }
        string? contentOrPath = e.ContentType switch
        {
            ClipboardItemKind.Image => e.ImagePath,
            ClipboardItemKind.Files => e.FilePaths.FirstOrDefault(),
            _ => e.Content,
        };
        return new LastCopiedContent(e.ContentType, contentOrPath, e.Timestamp);
    }

    /// <summary>取最近一条完整条目（含 Id/尺寸/哈希）；可选时间窗过滤（超出返回 null）。</summary>
    public ClipboardEntry? GetLastEntry(TimeSpan? timeLimit = null)
    {
        var res = Call(IpcProtocol.M_GetLast);
        if (res.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var e = DeserializeEntry(res);
        if (timeLimit.HasValue && DateTime.Now - e.Timestamp > timeLimit.Value)
        {
            return null;
        }
        return e;
    }

    /// <summary>按 ID 取完整条目（不存在返回 null）。</summary>
    public ClipboardEntry? GetEntryById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        var res = Call(IpcProtocol.M_GetEntry, new { id });
        return res.ValueKind == JsonValueKind.Null ? null : DeserializeEntry(res);
    }

    /// <summary>
    /// 引擎 <c>query</c> 直通：过滤与分页都在引擎侧完成（null 条件不参与过滤）；
    /// keyword/sourceApp 由引擎 trim + 忽略大小写子串匹配，<c>total</c> 为过滤后命中总数。
    /// </summary>
    private (List<ClipboardEntry> Items, int Total) QueryPage(
        ClipboardItemKind? kind,
        ContentCategory? category,
        string? keyword,
        string? sourceApp,
        bool? pinned,
        bool? sticker,
        int offset,
        int limit)
    {
        var res = Call(IpcProtocol.M_Query, new
        {
            offset,
            limit,
            category = category.HasValue ? (int)category.Value : (int?)null,
            kind = kind.HasValue ? (int)kind.Value : (int?)null,
            keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            sourceApp = string.IsNullOrWhiteSpace(sourceApp) ? null : sourceApp.Trim(),
            pinned,
            // 【2026-09-13】表情包已由"分类"改为与收藏同级的**标记** → 筛选走独立参数
            //（这样文字颜文字、静态图、动图、文件都能被筛出来）。
            sticker
        });
        int total = res.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        var items = new List<ClipboardEntry>();
        if (res.TryGetProperty("items", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                items.Add(DeserializeEntry(el));
            }
        }
        return (items, total);
    }

    private ClipboardEntry DeserializeEntry(JsonElement el)
    {
        var raw = el.GetRawText();
        return JsonSerializer.Deserialize<ClipboardEntry>(raw, _jsonOptions) ?? new ClipboardEntry();
    }

    // ---------------- IClipboardService：变更 ----------------

    public void PinEntry(ClipboardEntry entry) => Call(IpcProtocol.M_Pin, new { id = entry.Id });

    public void UnpinEntry(ClipboardEntry entry) => Call(IpcProtocol.M_Unpin, new { id = entry.Id });

    /// <summary>
    /// 设置/取消「表情包」标记（与收藏同级的独立标记 · 2026-09-13）。
    /// 任何条目都能标记 —— 文字颜文字、静态图、动图、文件一视同仁（用户口径："跟收藏一样的机制"）。
    /// </summary>
    public void SetSticker(ClipboardEntry entry, bool value)
        => Call(IpcProtocol.M_SetSticker, new { id = entry.Id, value });

    public void DeleteEntry(ClipboardEntry entry) => Call(IpcProtocol.M_Delete, new { id = entry.Id });

    public void DeleteEntries(IEnumerable<ClipboardEntry> entries)
    {
        var ids = entries.Select(e => e.Id).Distinct().ToArray();
        if (ids.Length > 0)
        {
            Call(IpcProtocol.M_DeleteMany, new { ids });
        }
    }

    public void ClearAllUnpinned() => Call(IpcProtocol.M_ClearUnpinned);

    /// <summary>
    /// 存储状态快照（面板据此提醒用户清理）。
    /// 【软限制】总存储预算超限时引擎**不驱逐条目**，只把 <see cref="StorageStatus.OverBudget"/> 置真，
    /// 由 UI 提示用户自行清理（用户 2026-09-12 口径："达到存储上面也可以存储，但要发消息提醒用户及时清理"）。
    /// </summary>
    public readonly record struct StorageStatus(
        long UsedMb,
        int BudgetMb,
        bool OverBudget,
        int Entries,
        int Unpinned,
        int Capacity);

    /// <summary>查询存储占用与预算（引擎不可达时抛 <see cref="ClipboardIpcException"/>）。</summary>
    public StorageStatus GetStorageStatus()
    {
        var res = Call(IpcProtocol.M_StorageStatus);
        long usedMb = res.TryGetProperty("usedMb", out var u) ? u.GetInt64() : 0;
        int budgetMb = res.TryGetProperty("budgetMb", out var b) ? b.GetInt32() : 0;
        bool over = res.TryGetProperty("overBudget", out var o) && o.GetBoolean();
        int entries = res.TryGetProperty("entries", out var e) ? e.GetInt32() : 0;
        int unpinned = res.TryGetProperty("unpinned", out var up) ? up.GetInt32() : 0;
        int capacity = res.TryGetProperty("capacity", out var c) ? c.GetInt32() : 0;
        return new StorageStatus(usedMb, budgetMb, over, entries, unpinned, capacity);
    }

    public void SetEntryTags(ClipboardEntry entry, string tags) =>
        Call(IpcProtocol.M_SetTags, new { id = entry.Id, tags = tags ?? string.Empty });

    /// <summary>【截图 OCR · 2026-09-14】写回 OCR 识别文本（空文本 = 清空）。调用方经 IOcrService 间接使用。</summary>
    public void SetEntryOcrText(string entryId, string ocrText) =>
        Call(IpcProtocol.M_SetOcrText, new { id = entryId, ocr_text = ocrText ?? string.Empty });

    // ---------------- IClipboardService：粘贴 ----------------

    public void CopyEntryToClipboard(ClipboardEntry entry) =>
        Call(IpcProtocol.M_Copy, new { id = entry.Id });

    public void CopyEntryAsPlainText(ClipboardEntry entry) =>
        Call(IpcProtocol.M_Copy, new { id = entry.Id, plain = true });

    public void PasteEntryToActiveWindow(ClipboardEntry entry)
    {
        CopyEntryToClipboard(entry);
        InjectPaste();
    }

    /// <summary>
    /// 【P2-3 临时粘贴 · 2026-09-13】写回条目并**暂存当前剪贴板**（供粘贴后还原）。
    /// 返回是否捕获到可还原的原内容（剪贴板为空/不可读时为 false）。
    /// </summary>
    public bool CopyEntryTemporarilyToClipboard(ClipboardEntry entry)
    {
        var res = Call(IpcProtocol.M_CopyTemp, new { id = entry.Id });
        return res.TryGetProperty("hadPrevious", out var h) && h.GetBoolean();
    }

    /// <summary>【P2-3】还原临时粘贴前的剪贴板内容；返回是否确实执行了还原。</summary>
    public bool RestoreTemporaryClipboard()
    {
        var res = Call(IpcProtocol.M_RestoreTemp);
        return res.TryGetProperty("restored", out var r) && r.GetBoolean();
    }

    /// <summary>
    /// 【P2-3 临时粘贴】写回条目 → 注入粘贴 → 短暂延迟后**还原原剪贴板**。
    /// <para>
    /// 还原走后台线程（不阻塞调用方 UI）；250ms 是"够注入生效、又赶在用户下次复制之前"的折中
    ///（对标 TieZ 的还原时机）。还原失败不影响已完成的粘贴，只记诊断日志。
    /// </para>
    /// </summary>
    public void PasteEntryTemporarilyToActiveWindow(ClipboardEntry entry)
    {
        CopyEntryTemporarilyToClipboard(entry);
        InjectPaste();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(250).ConfigureAwait(false);
                RestoreTemporaryClipboard();
            }
            catch (Exception ex)
            {
                Trace($"临时粘贴：还原剪贴板失败（{ex.Message}）");
            }
        });
    }

    /// <summary>
    /// 单测缝：粘贴按键注入实现（默认 keybd_event 发 Ctrl+V）。
    /// headless 单测替换掉它，避免真的往当前桌面注入按键（会污染用户正在用的窗口）。
    /// </summary>
    internal Action? PasteInjectorHook { get; set; }

    /// <summary>单测缝：本地写纯文本剪贴板实现（默认 Win32；headless 单测替换掉，避免污染真实剪贴板）。</summary>
    internal Action<string>? ClipboardTextWriterHook { get; set; }

    /// <summary>
    /// 粘贴按键注入方式（面板从设置读入）。
    /// 默认 <see cref="PasteInjectMode.CtrlV"/> = **旧行为、零变化**：现代 Windows 终端
    ///（conhost / cmd / PowerShell / Windows Terminal）与常规应用都支持 `Ctrl+V`，
    /// 只有 mintty / PuTTY 这类少数终端才需要 `Shift+Insert`（由用户按需切换）。
    /// </summary>
    public PasteInjectMode InjectMode { get; set; } = PasteInjectMode.CtrlV;

    private void InjectPaste()
    {
        var injector = PasteInjectorHook;
        if (injector is not null)
        {
            injector();
            return;
        }
        SendPaste(InjectMode);
    }

    private void WriteClipboardText(string text)
    {
        var writer = ClipboardTextWriterHook;
        if (writer is not null)
        {
            writer(text);
            return;
        }
        SetClipboardText(text);
    }

    /// <summary>
    /// 多选合并粘贴：纯文本拼接（默认 "\n\n"）后本地写剪贴板 + SendPaste。
    /// 注意：本地写剪贴板不经引擎抑制令牌，引擎轮询可能将合并文本捕获为新条目（去重置顶，语义可接受）。
    /// </summary>
    public void MergePasteToActiveWindow(IEnumerable<ClipboardEntry> entries, string? separator = null)
    {
        // 【2026-09-12 修复】此前直接用本地 `e.PlainText` —— 而列表页返回的是**摘要载荷**
        //（htmlContent/rtfContent 已剥离、content 可能被截断甚至为空），HTML 条目会拼出空串。
        // 现对空文本逐条向引擎取全文；取不到就跳过该条，不影响其余。
        var parts = new List<string>();
        foreach (var e in entries)
        {
            // 【2026-09-12 修复】面板传来的条目来自**列表摘要载荷**（content 可能被截断到 1024 字符、
            // htmlContent/rtfContent 已剥离）——"非空"绝不代表是全文。旧实现只在 PlainText 为空时才回引擎取，
            // 于是长文本合并粘贴被静默截断到摘要长度。现在一律回引擎取全文，取不到再退回本地载荷。
            string text;
            try
            {
                text = FetchEntryPlainText(e);
            }
            catch (Exception ex)
            {
                Trace($"合并粘贴：取全文失败 id={e.Id}（{ex.Message}），退回本地载荷");
                text = string.Empty;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                text = e.PlainText;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }
        if (parts.Count == 0)
        {
            // 【2026-09-12 审计】旧实现静默 return —— 用户以为已粘贴，实际什么都没发生。
            throw new ClipboardIpcException("合并粘贴：所选条目均无可粘贴文本");
        }
        WriteClipboardText(string.Join(separator ?? "\n\n", parts));
        InjectPaste();
    }

    /// <summary>向引擎取条目纯文本全文（列表摘要载荷可能为空或被截断；大文本走 get_content 取 bin）。</summary>
    private string FetchEntryPlainText(ClipboardEntry entry)
    {
        var res = Call(IpcProtocol.M_GetEntry, new { id = entry.Id });
        var text = ReadString(res, "content");
        if (text.Length > 0)
        {
            return text;
        }

        // >100KB 的大文本落在 content\{id}.bin（懒加载）→ get_entry 的 content 为空，改取全文
        var content = Call(IpcProtocol.M_GetContent, new { id = entry.Id });
        return ReadString(content, "content");
    }

    // ---------------- 按序粘贴状态机（v1.3；本地实现 + 内容快照自包含） ----------------

    /// <summary>
    /// 按序粘贴过程提示（条目失效 / 降级 / 失败）。
    /// <para>
    /// 【为什么需要它 · 2026-09-12 用户实测"选了按序粘贴后它会自己释放"】旧实现里条目一旦失效，
    /// <see cref="PasteNextSequential"/> 直接静默失败并前进序号 —— 用户看到的就是"勾好的列表自己少了、
    /// 内容凭空没了"。过程必须可见：面板订阅本事件在状态条上显示。
    /// </para>
    /// <para>**不在 IClipboardService 契约内**（契约只加不改），面板按具体类型订阅。</para>
    /// </summary>
    public event Action<string>? SequentialIssue;

    /// <summary>最近一次 <see cref="BeginSequentialPaste"/> 中内容快照不可用的条数（0 = 全部健康）。</summary>
    public int LastSequentialInvalidCount { get; private set; }

    public bool IsSequentialPasteActive
    {
        get
        {
            lock (_seqLock)
            {
                return _seqQueue is { Count: > 0 } && _seqIndex < _seqQueue.Count;
            }
        }
    }

    public int SequentialRemaining
    {
        get
        {
            lock (_seqLock)
            {
                return _seqQueue is null ? 0 : Math.Max(0, _seqQueue.Count - _seqIndex);
            }
        }
    }

    /// <summary>
    /// 开始按序粘贴：**逐条抓取内容全文作为快照**，之后粘贴不再依赖条目在引擎侧继续存在。
    /// <para>
    /// 【为什么是快照 · 2026-09-12】队列此前只存 id，每条都回引擎 <c>copy_to_clipboard(id)</c> 取全文；
    /// 条目一旦在引擎侧消失（用户删掉该条 /「清理未收藏」/ 启动迁移去重合并 / 引擎重启快照回退），
    /// 该条就 <c>entry not found</c>。现在即使查不到条目，也能用快照本地写回剪贴板。
    /// </para>
    /// <para>抓取失败（引擎不可达/条目已删）不阻塞会话：条目被计为"快照不可用"并经 <see cref="SequentialIssue"/> 报出。</para>
    /// </summary>
    public void BeginSequentialPaste(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            throw new ArgumentException("entries must be non-empty", nameof(entries));
        }

        var items = new List<SequentialItem>(entries.Count);
        var invalid = 0;
        var degraded = 0;

        // 【2026-09-12 审计修复】快照抓取是在调用线程（面板 UI 线程）上的**同步 IPC 往返**：
        // 引擎正常时每条 <5ms，但引擎假死/半死时单条要等满 5s 超时 —— 勾 N 条就会冻结 N×5s。
        // 给整体一个预算，超预算后剩余条目直接用勾选时携带的摘要载荷（不再发 IPC），
        // 保证"点得动、有反馈"（摘要可能被截断，但至少能粘，且下面会明确告知用户）。
        var deadline = Environment.TickCount64 + SnapshotBudgetMs;
        foreach (var entry in entries)
        {
            SequentialClipboardPayload payload;
            if (Environment.TickCount64 > deadline)
            {
                payload = SnapshotFallback(entry);
                degraded++;
            }
            else
            {
                payload = CaptureSnapshot(entry);
            }

            if (!payload.HasContent)
            {
                invalid++;
            }
            items.Add(new SequentialItem(entry, payload));
        }

        lock (_seqLock)
        {
            _seqQueue = items;
            _seqIndex = 0;
        }

        LastSequentialInvalidCount = invalid;
        if (invalid > 0)
        {
            RaiseIssue($"按序粘贴：{entries.Count} 条中有 {invalid} 条内容快照不可用（引擎侧已失效）");
        }
        if (degraded > 0)
        {
            RaiseIssue($"按序粘贴：引擎响应超时，{degraded} 条改用列表摘要兜底（长文本可能不完整）");
        }
    }

    // ---------------- 【按格粘 · 2026-09-13】表格单元格级按序粘贴 ----------------

    /// <summary>按格粘：粘完一格后自动按的键（<see cref="CellPasteAutoKey.None"/> = 不按）。会话级，启动时设定。</summary>
    private CellPasteAutoKey _cellAutoKey = CellPasteAutoKey.None;

    /// <summary>粘后自动按键前的等待（毫秒）：粘贴是**异步**的，立刻按键会被目标应用吞掉。</summary>
    private const int CellAutoKeyDelayMs = 80;

    /// <summary>
    /// 单测缝：粘后自动按键的注入实现（默认 keybd_event 发 Tab / Enter）。
    /// headless 单测替换掉它，避免真的往当前桌面注入按键。
    /// </summary>
    internal Action<CellPasteAutoKey>? AutoKeyInjectorHook { get; set; }

    /// <summary>
    /// 【按格粘 · 2026-09-13】以**表格条目的单元格**为元素开始按序粘贴：每次 Ctrl+V 粘一个格子。
    /// <para>
    /// 与 <see cref="BeginSequentialPaste"/> 的区别：那条是"多个**条目**逐条粘"；本条是
    /// "一个**表格条目**拆成多个格子逐格粘" —— 用于把 Excel 数据填进业务系统表单。
    /// 单元格是纯文本且自包含，因此**不依赖引擎侧条目存活**，也不走引擎写回。
    /// </para>
    /// <para>顺序＝**行优先**（Excel 视觉序），由 <see cref="ClipboardTableCells.Split"/> 保证。</para>
    /// </summary>
    /// <param name="autoKey">粘完一格后自动按键 —— 由用户在启动时选择（不同表格/目标系统差异极大，不写死）。</param>
    public void BeginCellSequentialPaste(ClipboardEntry entry, CellPasteAutoKey autoKey)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (!ClipboardTableCells.IsTabular(entry, out var rows, out var cols, out var count))
        {
            throw new ClipboardIpcException("该条目不是可逐格粘贴的表格（需要多列的 Tab 结构）");
        }

        if (count > ClipboardTableCells.MaxCells)
        {
            throw new ClipboardIpcException(
                $"表格过大：{rows} 行 × {cols} 列 = {count} 格，超过上限 {ClipboardTableCells.MaxCells} 格");
        }

        // 取全文：列表摘要的 content 可能被截断；>100KB 落 bin 时走 get_content（见 FetchEntryPlainText）。
        string text;
        try
        {
            text = FetchEntryPlainText(entry);
        }
        catch (Exception e)
        {
            Trace($"按格粘：取全文失败（{e.Message}），改用列表载荷");
            RaiseIssue("按格粘：引擎响应超时，表格改用列表载荷（内容可能不完整）");
            text = entry.Content ?? string.Empty;
        }

        var cells = ClipboardTableCells.Split(text);
        if (cells.Count == 0)
        {
            throw new ClipboardIpcException("表格拆分后没有可粘贴的单元格");
        }

        var items = new List<SequentialItem>(cells.Count);
        foreach (var cell in cells)
        {
            items.Add(new SequentialItem(
                entry,
                new SequentialClipboardPayload
                {
                    Id = entry.Id,
                    Kind = ClipboardItemKind.Text,
                    Text = cell,
                },
                skipEngineWriteback: true));
        }

        lock (_seqLock)
        {
            _seqQueue = items;
            _seqIndex = 0;
            _cellAutoKey = autoKey;
        }

        LastSequentialInvalidCount = 0;
        RaiseIssue($"按格粘就绪：{rows} 行 × {cols} 列 = {items.Count} 格（顺序＝行优先）");
    }

    /// <summary>粘后按会话设定注入 Tab / Enter（<see cref="CellPasteAutoKey.None"/> 不注入）。</summary>
    private void ApplyCellAutoKey()
    {
        var key = _cellAutoKey;
        if (key == CellPasteAutoKey.None)
        {
            return;
        }

        try
        {
            Thread.Sleep(CellAutoKeyDelayMs); // 粘贴异步生效，立刻按键会被吞
            var injector = AutoKeyInjectorHook;
            if (injector is not null)
            {
                injector(key);
                return;
            }

            if (key == CellPasteAutoKey.Tab)
            {
                SendVirtualKey(0x09); // VK_TAB
            }
            else
            {
                SendVirtualKey(0x0D); // VK_RETURN
            }
        }
        catch (Exception e)
        {
            Trace($"按格粘：自动按键注入失败（{e.Message}）");
        }
    }

    private static void SendVirtualKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyEventFKeyUp, UIntPtr.Zero);
    }

    public void PasteNextSequential()
    {
        SequentialItem item;
        int seqNo;
        lock (_seqLock)
        {
            if (_seqQueue is null || _seqIndex >= _seqQueue.Count)
            {
                return;
            }
            item = _seqQueue[_seqIndex];
            _seqIndex++;
            seqNo = _seqIndex;
        }

        // 【按格粘 · 2026-09-13】单元格不是引擎侧条目 → 直接用自包含文本写回（不跑注定失败的引擎正路）。
        if (item.SkipEngineWriteback)
        {
            if (WriteSnapshotToClipboard(item.Payload, allowEmpty: true))
            {
                InjectPaste();
            }
            else
            {
                // 写不进去就**绝不注入**：剪贴板里还留着上一格，注入会把上一格重复粘一次 ——
                // 错位比跳过更糟，故宁可只跳格。
                RaiseIssue($"按格粘：第 {seqNo} 格无法写入剪贴板，已跳过粘贴（仍按设定跳格）");
            }

            ApplyCellAutoKey();
            return;
        }

        // ① 首选引擎写回：完整格式层级 + 抑制回环 + HTML 内嵌图还原都由引擎负责（条目仍在时的正路）
        try
        {
            CopyEntryToClipboard(item.Entry);
            InjectPaste();
            return;
        }
        catch (Exception e)
        {
            Trace($"按序粘贴：引擎写回失败（{e.Message}），改用内容快照 id={item.Entry.Id}");
            RaiseIssue($"按序粘贴：第 {seqNo} 条已被引擎释放，已用本机内容快照粘贴");
        }

        // ② 条目已失效 → 用**本进程抓好的快照**写剪贴板（自包含，不依赖条目存活）
        if (WriteSnapshotToClipboard(item.Payload))
        {
            InjectPaste();
            return;
        }

        // ③ 两条路都不通 → 明确失败。旧实现在这里静默吞掉，正是"内容自己消失"的观感来源
        var message = $"按序粘贴：第 {seqNo} 条内容不可用（id={item.Entry.Id}）";
        RaiseIssue(message);
        throw new ClipboardIpcException(message);
    }

    public void CancelSequentialPaste()
    {
        lock (_seqLock)
        {
            _seqQueue = null;
            _seqIndex = 0;
            // 【按格粘】会话结束即复位自动按键 —— 否则取消后残留的 Tab/Enter 会打到用户后续的普通粘贴上。
            _cellAutoKey = CellPasteAutoKey.None;
        }
    }

    /// <summary>报出过程提示（写诊断痕迹 + 通知订阅方；订阅方异常不影响状态机）。</summary>
    private void RaiseIssue(string message)
    {
        Trace(message);
        try
        {
            SequentialIssue?.Invoke(message);
        }
        catch (Exception)
        {
            // 订阅方（UI）异常不得影响按序粘贴
        }
    }

    /// <summary>按序粘贴快照抓取的总时间预算（毫秒）；超预算后剩余条目直接用摘要载荷，不再发 IPC。</summary>
    private const long SnapshotBudgetMs = 2500;

    /// <summary>
    /// 抓取条目的内容快照（全文字段）。先铺勾选时携带的摘要载荷，再用引擎字段覆盖 ——
    /// 引擎取不到（条目已删/引擎不可达）时至少还有摘要可用（摘要 content 可能被截断到 1024 字符）。
    /// </summary>
    private SequentialClipboardPayload CaptureSnapshot(ClipboardEntry entry)
    {
        var payload = SnapshotFallback(entry);
        try
        {
            var res = Call(IpcProtocol.M_GetEntry, new { id = entry.Id });
            var text = ReadString(res, "content");
            var html = ReadString(res, "htmlContent");
            var rtf = ReadString(res, "rtfContent");
            var imagePath = ReadString(res, "imagePath");
            var files = ReadStrings(res, "filePaths");

            // >100KB 的大文本落在 content\{id}.bin（懒加载）→ get_entry 的 content 为空，需 get_content 取全文
            var inBin = res.TryGetProperty("contentInBin", out var b) && b.ValueKind == JsonValueKind.True;
            if (inBin && text.Length == 0)
            {
                var content = Call(IpcProtocol.M_GetContent, new { id = entry.Id });
                text = ReadString(content, "content");
            }

            // 引擎字段非空才覆盖（空值不覆盖摘要兜底 —— 摘要虽可能截断，仍优于空）
            if (text.Length > 0)
            {
                payload.Text = text;
            }
            if (html.Length > 0)
            {
                payload.Html = html;
            }
            if (rtf.Length > 0)
            {
                payload.Rtf = rtf;
            }
            if (imagePath.Length > 0)
            {
                payload.ImagePath = imagePath;
            }
            if (files.Length > 0)
            {
                payload.FilePaths = files;
            }
        }
        catch (Exception e)
        {
            Trace($"按序粘贴：快照抓取失败 id={entry.Id}（{e.Message}）");
        }

        payload.HasContent = HasAnyContent(payload);
        return payload;
    }

    /// <summary>用勾选时携带的摘要载荷构造快照（引擎不可达 / 超预算时的兜底）。</summary>
    private static SequentialClipboardPayload SnapshotFallback(ClipboardEntry entry)
    {
        var payload = new SequentialClipboardPayload
        {
            Id = entry.Id,
            Kind = entry.ContentType,
            Text = entry.Content,
            Html = entry.HtmlContent,
            Rtf = entry.RtfContent,
            ImagePath = entry.ImagePath,
            FilePaths = entry.FilePaths,
        };
        payload.HasContent = HasAnyContent(payload);
        return payload;
    }

    private static bool HasAnyContent(SequentialClipboardPayload payload) =>
        payload.Text.Length > 0 || payload.Html.Length > 0 || payload.Rtf.Length > 0
        || payload.ImagePath.Length > 0 || payload.FilePaths.Length > 0;

    private static string ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string[] ReadStrings(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// 单测缝：快照降级写剪贴板的实现（默认走 Win32）。
    /// headless 单测注入内存实现，避免触碰真实剪贴板（OpenClipboard 在无桌面会话下会失败）。
    /// </summary>
    internal Func<SequentialClipboardPayload, bool>? SnapshotWriterHook { get; set; }

    /// <param name="allowEmpty">
    /// 【按格粘 · 2026-09-13】允许写入**空文本**（表格里的空格子）：那表示"清空当前格"。
    /// 若按普通规则拒绝，该格会被整段跳过而**后续格位全部错位**（比粘空更糟）。
    /// </param>
    private bool WriteSnapshotToClipboard(SequentialClipboardPayload payload, bool allowEmpty = false)
    {
        if (!payload.HasContent && !allowEmpty)
        {
            return false;
        }

        var writer = SnapshotWriterHook ?? WriteSnapshotNative;
        try
        {
            return writer(payload);
        }
        catch (Exception e)
        {
            Trace($"按序粘贴：快照写剪贴板失败（{e.Message}）");
            return false;
        }
    }

    /// <summary>
    /// 用快照写回剪贴板（多格式并存，目标应用自选）—— 格式与引擎 <c>capture::write_back</c> 对齐：
    /// 文件 → CF_HDROP；文本 → CF_UNICODETEXT；HTML → 标准 CF_HTML（同模板包装）；RTF → "Rich Text Format"。
    /// 图片快照不含位图数据（原图在引擎侧磁盘）→ 降级为路径文本，至少不让内容凭空消失。
    /// </summary>
    private static bool WriteSnapshotNative(SequentialClipboardPayload payload)
    {
        var blocks = BuildSnapshotBlocks(payload);
        if (blocks.Count == 0)
        {
            return false;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        var pending = new List<IntPtr>(); // 已分配但尚未把所有权交给剪贴板的句柄（异常路径必须释放）
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            foreach (var (format, data) in blocks)
            {
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
                if (h == IntPtr.Zero)
                {
                    return false;
                }
                pending.Add(h);

                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    Marshal.Copy(data, 0, p, data.Length);
                }
                finally
                {
                    GlobalUnlock(h);
                }

                if (SetClipboardData(format, h) == IntPtr.Zero)
                {
                    return false;
                }
                pending.Remove(h); // 所有权已转移给剪贴板，不能再 GlobalFree
            }
            return true;
        }
        finally
        {
            CloseClipboard();
            foreach (var h in pending)
            {
                GlobalFree(h);
            }
        }
    }

    private static List<(uint Format, byte[] Data)> BuildSnapshotBlocks(SequentialClipboardPayload payload)
    {
        var blocks = new List<(uint Format, byte[] Data)>();
        if (payload.FilePaths.Length > 0)
        {
            blocks.Add((CF_HDROP, BuildDropFiles(payload.FilePaths)));
            // 【2026-09-13】与引擎 `set_files_entry` 对齐：文件/目录场景必须**同时**给路径文本，
            // 否则粘贴到网页聊天框会被展开成子项图标或字符标记（用户实测"粘贴文件夹出来一堆图标"）。
        }

        // 文本兜底：优先条目文本；文件条目本身没有文本 → 用路径（按行分隔）
        var clipboardText = !string.IsNullOrEmpty(payload.Text)
            ? payload.Text
            : string.Join("\r\n", payload.FilePaths);
        if (!string.IsNullOrEmpty(clipboardText))
        {
            blocks.Add((CF_UNICODETEXT, Encoding.Unicode.GetBytes(clipboardText + "\0")));
        }
        if (!string.IsNullOrEmpty(payload.Html))
        {
            uint htmlFormat = RegisterClipboardFormat("HTML Format");
            if (htmlFormat != 0)
            {
                blocks.Add((htmlFormat, Encoding.UTF8.GetBytes(WrapHtmlForClipboard(payload.Html))));
            }
        }
        if (!string.IsNullOrEmpty(payload.Rtf))
        {
            uint rtfFormat = RegisterClipboardFormat("Rich Text Format");
            if (rtfFormat != 0)
            {
                blocks.Add((rtfFormat, Encoding.UTF8.GetBytes(payload.Rtf + "\0")));
            }
        }
        if (blocks.Count == 0 && !string.IsNullOrEmpty(payload.ImagePath))
        {
            blocks.Add((CF_UNICODETEXT, Encoding.Unicode.GetBytes(payload.ImagePath + "\0")));
        }
        return blocks;
    }

    /// <summary>
    /// CF_HTML 标准包装（与引擎 <c>capture::wrap_html_for_clipboard</c> 同模板，偏移按 UTF-8 字节计）。
    /// 引擎捕获时已剥头，故此处输入是裸 HTML 片段，包一次即标准 CF_HTML。
    /// </summary>
    private static string WrapHtmlForClipboard(string htmlFragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";

        var placeholder =
            $"Version:0.9\r\nStartHTML:{99999999:D8}\r\nEndHTML:{99999999:D8}\r\nStartFragment:{99999999:D8}\r\nEndFragment:{99999999:D8}\r\n";
        int startHtml = Encoding.UTF8.GetByteCount(placeholder);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(htmlFragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);

        return
            $"Version:0.9\r\nStartHTML:{startHtml:D8}\r\nEndHTML:{endHtml:D8}\r\nStartFragment:{startFragment:D8}\r\nEndFragment:{endFragment:D8}\r\n{prefix}{htmlFragment}{suffix}";
    }

    /// <summary>CF_HDROP 载荷：DROPFILES 头（20 字节，fWide=TRUE）+ 双 \0 结尾的 UTF-16 路径表。</summary>
    private static byte[] BuildDropFiles(string[] paths)
    {
        const int HeaderSize = 20;
        var sb = new StringBuilder();
        foreach (var path in paths)
        {
            sb.Append(path).Append('\0');
        }
        sb.Append('\0');

        var pathBytes = Encoding.Unicode.GetBytes(sb.ToString());
        var buffer = new byte[HeaderSize + pathBytes.Length];
        BitConverter.GetBytes(HeaderSize).CopyTo(buffer, 0); // pFiles：路径表相对结构头的偏移
        BitConverter.GetBytes(1).CopyTo(buffer, 16); // fWide = TRUE
        pathBytes.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    /// <summary>按序粘贴队列项：条目元数据 + **自包含内容快照**。</summary>
    private sealed class SequentialItem
    {
        public SequentialItem(ClipboardEntry entry, SequentialClipboardPayload payload, bool skipEngineWriteback = false)
        {
            Entry = entry;
            Payload = payload;
            SkipEngineWriteback = skipEngineWriteback;
        }

        public ClipboardEntry Entry { get; }

        public SequentialClipboardPayload Payload { get; }

        /// <summary>
        /// 【按格粘 · 2026-09-13】跳过"先向引擎取该条目"的正路。
        /// 单元格**不是引擎侧条目**（只是表格文本的一段），走正路只会每格一次注定失败的 IPC ——
        /// 既慢又刷噪声日志；直接用自包含文本写回即可。
        /// </summary>
        public bool SkipEngineWriteback { get; }
    }

    // ---------------- 录入（M1 契约预留） ----------------

    // ---------------- 表情包（用户主动填入的动图；2026-09-12） ----------------

    /// <summary>
    /// 导入表情包：请求引擎做「原文件字节级复制 + 内容去重 + Sticker 分类入库」。
    /// 结果逐条回报（新增 / **已在历史里→升级** / 重复跳过 / 失败原因）—— 失败与升级都必须可见，不静默。
    /// </summary>
    public StickerImportResult AddStickers(IEnumerable<string> filePaths)
    {
        var paths = filePaths?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        if (paths.Length == 0)
        {
            return new StickerImportResult(0, 0, 0, Array.Empty<string>());
        }

        var res = Call(IpcProtocol.M_AddSticker, new { paths });
        int added = res.TryGetProperty("added", out var a) ? a.GetInt32() : 0;
        int upgraded = res.TryGetProperty("upgraded", out var u) ? u.GetInt32() : 0;
        int skipped = res.TryGetProperty("skipped", out var s) ? s.GetInt32() : 0;

        var errors = new List<string>();
        if (res.TryGetProperty("errors", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.GetString() is { Length: > 0 } msg)
                {
                    errors.Add(msg);
                }
            }
        }
        return new StickerImportResult(added, upgraded, skipped, errors);
    }

    // ---------------- 状态与控制 ----------------

    /// <summary>
    /// 管道当前是否已连接（工作线程维护的 <c>_connected</c> 的只读视图）。
    /// <para>
    /// 【2026-09-14】存在的意义：调用方需要区分「引擎真的连不上」与「引擎活着但这次请求失败」
    /// （RPC 报错 / 单次超时 / 解析失败）。此前面板只能按异常文案猜，结果把**所有**失败都
    /// 渲染成"引擎未连接，正在重试…"（用户实测：暂停记录时看到"与引擎断开连接"，实际连接正常）。
    /// </para>
    /// </summary>
    public bool IsConnected => _connected;

    public bool IsTemporarilyPaused
    {
        get { lock (_stateLock) { return _isTemporarilyPaused; } }
    }

    public int PauseRemainingSeconds
    {
        get
        {
            lock (_stateLock)
            {
                if (!_isTemporarilyPaused || _resumeAt is null)
                {
                    return 0;
                }
                return Math.Max(0, (int)(_resumeAt.Value - DateTime.Now).TotalSeconds);
            }
        }
    }

    public void PauseTemporarily(int seconds = 60)
    {
        // 【2026-09-12 审计修复】秒数**随请求发给引擎**：引擎侧到点自动恢复。
        // 旧实现只靠本进程 Timer 补发 resume —— 面板崩溃/被杀后引擎会**永久暂停**，
        // 剪贴板从此静默不再记录（用户只会觉得"它坏了"）。本进程 Timer 保留为双保险（幂等）。
        Call(IpcProtocol.M_Pause, new { seconds });
        lock (_stateLock)
        {
            _isTemporarilyPaused = true;
            _resumeAt = DateTime.Now.AddSeconds(seconds);
        }
        _pauseTimer?.Dispose();
        _pauseTimer = new System.Threading.Timer(_ => Resume(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    public void Resume()
    {
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        lock (_stateLock)
        {
            _isTemporarilyPaused = false;
            _resumeAt = null;
        }
        Call(IpcProtocol.M_Resume);
    }

    /// <summary>
    /// 打开历史面板。
    /// 【2026-09-12 真机】IPC 不可用（引擎未连接 / 僵尸连接 / 请求超时）时**降级为直接拉起面板 exe --open**：
    /// 面板自身会探活并拉起引擎，因此"打开面板"不应依赖"调用方↔引擎"的长连接健康度。
    /// 用户点右键/菜单栏必须出界面——这里吞掉异常并走降级（失败只留 Debug 痕迹）。
    /// </summary>
    public void OpenHistoryWindow()
    {
        try
        {
            // 【2026-09-18】用亚秒级超时：这是**用户点击**路径（菜单栏/托盘/右键），
            // 引擎半死时不能让菜单卡 5 秒 —— 快速失败后立刻走下面的降级（直接拉起面板 exe）。
            Call(IpcProtocol.M_OpenPanel, null, OpenPanelTimeoutMs);
        }
        catch (ClipboardIpcException ex)
        {
            Debug.WriteLine($"[ClipboardIpc] open_panel via IPC failed ({ex.Message}); fallback to direct panel launch");
            ClipboardEngineLauncher.OpenPanel();
        }
    }

    /// <summary>
    /// 扩展（供宿主接线）：推送设置增量到引擎。
    /// 引擎侧 <c>apply_settings</c> 为「部分字段合并」——只传变化的键，缺失键保持引擎现值；
    /// 键名与引擎 <c>Settings</c> 的 kebab-case 序列化一致（enabled/capacity/pinned-limit/storage-mode/...）。
    /// </summary>
    public void ApplySettings(IReadOnlyDictionary<string, object?> patch)
    {
        if (patch.Count == 0)
        {
            return;
        }
        Call(IpcProtocol.M_ApplySettings, patch);
    }

    // ---------------- 本地 Win32 辅助（SendPaste / 纯文本写剪贴板） ----------------

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const uint KeyEventFKeyUp = 0x02;

    /// <summary>
    /// 粘贴按键注入（2026-09-13 · 对标 TieZ"三档注入"，先落两档）。
    /// <para>
    /// 旧实现**恒发 `Ctrl+V`** —— 在终端里那是"字面输入/控制字符"，不是粘贴：
    /// 用户在 Windows Terminal / conhost / mintty / PuTTY 里执行"粘贴回原窗口"会**毫无反应**。
    /// 现按 <paramref name="mode"/> 选择；<see cref="PasteInjectMode.Auto"/> 时按**目标窗口进程**判定。
    /// </para>
    /// </summary>
    private void SendPaste(PasteInjectMode mode = PasteInjectMode.Auto)
    {
        var viaShiftInsert = mode switch
        {
            PasteInjectMode.ShiftInsert => true,
            PasteInjectMode.CtrlV => false,
            _ => IsTerminalLikeForegroundWindow(),
        };

        LastInjectKey = viaShiftInsert ? "Shift+Insert" : "Ctrl+V";
        if (viaShiftInsert)
        {
            SendShiftInsert();
        }
        else
        {
            SendCtrlV();
        }
    }

    /// <summary>
    /// 最近一次粘贴注入**实际按下的键**（`Ctrl+V` 或 `Shift+Insert`）；null = 未注入过。
    /// <para>
    /// 【为什么留这个观测点】"在终端里粘不上"这类问题的第一个分水岭就是
    /// **注入键是否选错**（终端里 `Ctrl+V` 是字面输入）—— 没有它只能靠猜。
    /// 2026-09-13 排障时正是卡在这里：日志只说"已注入"，却看不出按的是哪个键。
    /// </para>
    /// </summary>
    public string? LastInjectKey { get; private set; }

    private static void SendCtrlV()
    {
        const byte VK_CONTROL = 0x11;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event((byte)'V', 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        keybd_event((byte)'V', 0, KeyEventFKeyUp, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KeyEventFKeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// `Shift+Insert`：终端类窗口的通用粘贴键（Windows 终端 / Linux 终端 / SSH 客户端一致支持）。
    /// </summary>
    private static void SendShiftInsert()
    {
        const byte VK_SHIFT = 0x10;
        const byte VK_INSERT = 0x2D;
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_INSERT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        keybd_event(VK_INSERT, 0, KeyEventFKeyUp, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KeyEventFKeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// 前台窗口是不是**终端类**（按进程名白名单判定）。
    /// <para>
    /// 为什么用进程名而不是窗口类名：终端实现太多（WT / conhost / mintty / alacritty / wezterm /
    /// PuTTY…），窗口类名各成一派且不稳定；进程名直观、可扩展，新增终端只需往
    /// <see cref="TerminalProcessNames"/> 加一行。
    /// </para>
    /// </summary>
    private static bool IsTerminalLikeForegroundWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return false;
            }
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return IsTerminalProcessName(proc.ProcessName);
        }
        catch (Exception e)
        {
            // 取不到进程（权限不足 / 进程刚退出）→ 按"非终端"处理：
            // 保持 Ctrl+V 的原行为，绝不因判定失败引入新的失败面。
            System.Diagnostics.Debug.WriteLine($"[ClipboardIpc] 终端判定失败，按常规窗口处理: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// **明确不认 `Ctrl+V`** 的终端进程名白名单（不含 `.exe`；比较不区分大小写）。
    /// <para>
    /// 【⚠️ 这份名单收得极窄，是刻意的 · 2026-09-13 用户实测纠正】
    /// 最初把 `WindowsTerminal` / `conhost` / `cmd` / `powershell` / `pwsh` 也列了进来，
    /// 前提是"终端里 Ctrl+V 是字面输入" —— **该前提是过时印象**：
    /// conhost 自 Win10 1809 起支持 Ctrl+V，`cmd` / PowerShell / Windows Terminal 全部支持。
    /// 用户实测原话："**我使用 Ctrl+V 可以粘贴到 powershell 和 cmd 里面啊**"。
    /// 拿过时印象去改默认路径，就是**用回归换修复** —— 已全部移除。
    /// </para>
    /// <para>
    /// 判定原则：**宁可漏判，不可误判**。漏判的代价 = 用户在这个终端里粘不上（可手动切模式）；
    /// 误判的代价 = 一个本来能用的终端被换掉粘贴键（**用户不知道发生了什么**）——后者严重得多。
    /// </para>
    /// <para>
    /// 仍然**不收 VS Code（Code）**：编辑器与集成终端同进程名，且它支持 Ctrl+V。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Git Bash / MSYS2 / Cygwin：Ctrl+V 是字面输入，粘贴靠 Shift+Insert（实测确认）
        "mintty",
        // SSH / 串口客户端：Ctrl+V 通常发给远端而非本地粘贴
        "putty", "Xshell", "SecureCRT", "MobaXterm",
    };

    /// <summary>
    /// 进程名是否属于终端类（**单测入口**；白名单见 <see cref="TerminalProcessNames"/>）。
    /// 从判定逻辑里单独抽出来，是为了让它可被单测直接覆盖 —— 前台窗口在单测里造不出来。
    /// </summary>
    internal static bool IsTerminalProcessName(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && TerminalProcessNames.Contains(processName.Trim());

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static void SetClipboardText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        IntPtr p = GlobalLock(h);
        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
        }
        finally
        {
            GlobalUnlock(h);
        }
        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(h);
            throw new ClipboardIpcException("OpenClipboard failed");
        }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
            {
                throw new ClipboardIpcException("SetClipboardData failed");
            }
            h = IntPtr.Zero; // 所有权已转移给剪贴板
        }
        finally
        {
            CloseClipboard();
            if (h != IntPtr.Zero)
            {
                GlobalFree(h);
            }
        }
    }
}

/// <summary>
/// 按序粘贴的内容快照载荷：**自包含** —— 粘贴时不再依赖引擎侧条目存活（条目被删/被清理/引擎重启都能照常粘出）。
/// internal：单测经 InternalsVisibleTo 构造/断言（见 ClipboardIpcClientTests）。
/// </summary>
internal sealed class SequentialClipboardPayload
{
    public string Id { get; set; } = string.Empty;

    public ClipboardItemKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    public string Rtf { get; set; } = string.Empty;

    public string ImagePath { get; set; } = string.Empty;

    public string[] FilePaths { get; set; } = Array.Empty<string>();

    /// <summary>是否取到了可用于写回的内容（false = 该条完全不可用）。</summary>
    public bool HasContent { get; set; }
}
