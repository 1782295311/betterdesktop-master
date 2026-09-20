using System.Text.Json;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

/// <summary>
/// 内存仿真引擎传输（单测用）：ServerHandler 对请求帧返回响应帧（null=不响应）；
/// ServerPush 注入引擎通知；可模拟断开/连接失败。
/// </summary>
internal sealed class FakeTransport : IClipboardTransport
{
    private readonly object _lock = new();
    private readonly Queue<string> _incoming = new();
    private readonly Queue<string> _serverPush = new();
    private bool _connected;
    private bool _failConnect;

    /// <summary>请求帧（已剥 magic）→ 响应帧；null=不响应（调用方超时）。</summary>
    public Func<string, string?>? ServerHandler { get; set; }

    /// <summary>引擎侧事件通知注入（工作线程轮询时送达客户端）。</summary>
    public Queue<string> ServerPush => _serverPush;

    /// <summary>记录客户端发出的请求帧（剥 magic 后的 JSON），供断言调用序列。</summary>
    public List<string> Requests { get; } = new();

    public bool FailNextConnect { get => _failConnect; set => _failConnect = value; }

    public bool IsConnected => _connected;

    public void Connect()
    {
        lock (_lock)
        {
            if (_failConnect)
            {
                _failConnect = false;
                throw new IOException("simulated connect failure");
            }
            _connected = true;
            // 仿真引擎对 subscribe_events 的响应（无 id 通知不返回；订阅本身无响应——保持静默）
        }
    }

    public void WriteFrame(string json)
    {
        var frame = json.StartsWith(IpcProtocol.Magic, StringComparison.Ordinal)
            ? json[IpcProtocol.Magic.Length..]
            : json;
        lock (_lock)
        {
            Requests.Add(frame);
            if (!_connected)
            {
                // 断开期间不响应（心跳 ping 无应答 → 客户端判定断开并重连）
                return;
            }
            var resp = ServerHandler?.Invoke(frame);
            if (resp is not null)
            {
                _incoming.Enqueue(resp);
                Monitor.PulseAll(_lock);
            }
        }
    }

    public bool IsDataAvailable()
    {
        lock (_lock)
        {
            return _incoming.Count > 0 || _serverPush.Count > 0;
        }
    }

    public string? ReadFrame()
    {
        lock (_lock)
        {
            while (_incoming.Count == 0 && _serverPush.Count == 0 && _connected)
            {
                Monitor.Wait(_lock, 200);
            }
            if (_incoming.Count > 0)
            {
                return _incoming.Dequeue();
            }
            if (_serverPush.Count > 0)
            {
                return _serverPush.Dequeue();
            }
            return null; // 断开
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _connected = false;
            _incoming.Clear();
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose() => Disconnect();

    // ---------------- 测试辅助 ----------------

    /// <summary>断言：客户端发出过指定 method 的请求。</summary>
    public JsonElement? LastRequestOf(string method)
    {
        lock (_lock)
        {
            for (int i = Requests.Count - 1; i >= 0; i--)
            {
                using var doc = JsonDocument.Parse(Requests[i]);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var m) && m.GetString() == method)
                {
                    return root.Clone();
                }
            }
            return null;
        }
    }
}
