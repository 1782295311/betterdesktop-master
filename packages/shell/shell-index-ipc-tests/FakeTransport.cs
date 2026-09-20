using System.Text.Json;
using BetterDesktop.Shell.IndexIpc;

namespace BetterDesktop.Shell.IndexIpc.Tests;

/// <summary>
/// 内存仿真引擎传输（单测用）：<see cref="ServerHandler"/> 对请求帧返回响应帧（null=不响应 → 调用方超时）；
/// 可模拟连接失败/断开。<see cref="RawRequests"/> 保留**带 magic 的原始帧**，供协议前缀纪律断言。
/// </summary>
internal sealed class FakeTransport : IIndexTransport
{
    private readonly object _lock = new();
    private readonly Queue<string> _incoming = new();
    private bool _connected;
    private bool _failConnect;

    /// <summary>请求帧（已剥 magic）→ 响应帧；null = 不响应（调用方超时）。</summary>
    public Func<string, string?>? ServerHandler { get; set; }

    /// <summary>客户端发出的**原始**帧（含 magic 前缀）。</summary>
    public List<string> RawRequests { get; } = new();

    /// <summary>客户端发出的请求帧（剥 magic 后），供方法/参数断言。</summary>
    public List<string> Requests { get; } = new();

    public bool FailNextConnect { get => _failConnect; set => _failConnect = value; }

    /// <summary>累计 Connect 次数（重连行为断言）。</summary>
    public int ConnectCount { get; private set; }

    public bool IsConnected => _connected;

    public void Connect()
    {
        lock (_lock)
        {
            // 先计数再判失败：ConnectCount 语义是「尝试次数」，失败尝试也要计入（重连行为断言用）
            ConnectCount++;
            if (_failConnect)
            {
                _failConnect = false;
                throw new IOException("simulated connect failure");
            }
            _connected = true;
        }
    }

    public void WriteFrame(string json)
    {
        string frame = json.StartsWith(IndexIpcProtocol.Magic, StringComparison.Ordinal)
            ? json[IndexIpcProtocol.Magic.Length..]
            : json;
        lock (_lock)
        {
            RawRequests.Add(json);
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
            }
        }
    }

    public bool IsDataAvailable()
    {
        lock (_lock)
        {
            return _incoming.Count > 0;
        }
    }

    public string? ReadFrame()
    {
        lock (_lock)
        {
            return _incoming.Count > 0 ? _incoming.Dequeue() : null;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _connected = false;
            _incoming.Clear();
        }
    }

    public void Dispose() => Disconnect();

    // ---------------- 测试辅助 ----------------

    /// <summary>最近一次指定 method 的请求根元素（剥 magic 后解析）。</summary>
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

    /// <summary>构造 JSON-RPC 成功响应（result 原样嵌入）。</summary>
    public static string Ok(long id, string resultJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";

    /// <summary>构造 JSON-RPC 错误响应（message 经 JSON 转义，避免手拼引号出错）。</summary>
    public static string Err(long id, int code, string message) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id +
        ",\"error\":{\"code\":" + code +
        ",\"message\":" + JsonSerializer.Serialize(message) + "}}";

    /// <summary>
    /// 默认应答器：按 method 回响应，并按 id 回填（id 取自请求体）。
    /// </summary>
    public void UseDefaultHandler(string statusJson)
    {
        ServerHandler = frame =>
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            long id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt64()
                : 0;
            string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            return method switch
            {
                "ping" => Ok(id, """{"pong":true,"version":"0.1.0"}"""),
                "status" => Ok(id, statusJson),
                "list_apps" => Ok(id, SampleListAppsJson),
                "search_files" => Ok(id, SampleSearchFilesJson),
                "apply_settings" => Ok(id, """{"ok":true}"""),
                "shutdown" => Ok(id, """{"ok":true}"""),
                _ => Err(id, IndexIpcProtocol.ErrMethodNotFound, "Method not found"),
            };
        };
    }

    /// <summary>引擎侧 status 响应体样例（字段名与 engine-index/src/model.rs 一致）。</summary>
    public const string SampleStatusJson = """
        {"version":"0.1.0","pid":4321,"uptimeSeconds":12,"building":false,"appCount":7,
         "fileCount":9,"lastBuildMs":34,"lastBuildAtMs":1700000000123,
         "degraded":true,"degradeReason":"索引尚未构建","rssBytes":7880704}
        """;

    /// <summary>引擎侧 search_files 响应体样例（字段名与 engine-index/src/engine.rs search_files 一致）。</summary>
    public const string SampleSearchFilesJson = """
        {"files":[
            {"path":"D:/迅雷下载/MaaEnd-x.zip","name":"MaaEnd-x.zip","sizeBytes":1024,"modifiedMs":1700000000000},
            {"path":"D:/Tools/maa.py","name":"maa.py","sizeBytes":64,"modifiedMs":0}
         ],"count":2,"building":false,"degraded":false,"degradeReason":null}
        """;

    /// <summary>引擎侧 list_apps 响应体样例（字段名与 engine-index/src/engine.rs list_apps 一致）。</summary>
    public const string SampleListAppsJson = """
        {"apps":[
            {"path":"C:/ProgramData/Microsoft/Windows/Start Menu/Programs/Edge.lnk",
             "targetPath":"C:/ProgramData/Microsoft/Windows/Start Menu/Programs/Edge.lnk",
             "nameHint":"Edge","source":"start-menu","iconKey":""},
            {"path":"C:/Program Files/App/a.exe","targetPath":"C:/Program Files/App/a.exe",
             "nameHint":"a","source":"program-files","iconKey":""}
         ],"count":2,"building":false,"degraded":false,"degradeReason":null}
        """;
}
