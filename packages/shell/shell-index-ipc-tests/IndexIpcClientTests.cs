using System.Diagnostics;
using System.Text.Json;
using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.IndexIpc.Tests;

/// <summary>
/// 索引客户端契约测试（fake transport，不依赖真机引擎进程）。
/// 覆盖：正常路径（ping/status/apply_settings/shutdown）、边界（超时/未连接）、异常（引擎报错）、
/// 以及跨语言线格式纪律（kebab-case 键 / magic 前缀）。
/// </summary>
public sealed class IndexIpcClientTests
{
    private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000, string what = "condition")
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException($"未在 {timeoutMs}ms 内满足：{what}");
    }

    /// <summary>建一个已连通（默认应答器）的客户端；返回后可直接发请求。</summary>
    private static (IndexIpcClient Client, FakeTransport Transport) CreateConnected(int timeoutMs = 5000)
    {
        var transport = new FakeTransport();
        transport.UseDefaultHandler(FakeTransport.SampleStatusJson);
        var client = new IndexIpcClient(transport, timeoutMs);
        client.Connect();
        WaitUntil(() => client.IsConnected, what: "client connected");
        return (client, transport);
    }

    // ---------------- 正常路径 ----------------

    [Fact]
    public async Task PingSucceedsWhenEngineResponds()
    {
        var (client, _) = CreateConnected();
        using (client)
        {
            Assert.True(await client.TryPingAsync());
            await client.PingAsync(); // 不抛即通过
        }
    }

    [Fact]
    public async Task StatusParsesAllContractFields()
    {
        var (client, _) = CreateConnected();
        using (client)
        {
            var status = await client.GetStatusAsync();
            Assert.Equal("0.1.0", status.Version);
            Assert.Equal(4321, status.Pid);
            Assert.Equal(12, status.UptimeSeconds);
            Assert.False(status.Building);
            Assert.Equal(7, status.AppCount);
            Assert.Equal(9, status.FileCount);
            Assert.Equal(34, status.LastBuildMs);
            // M3c：补扫后的「索引有多新」——设置中心状态行据此展示
            Assert.Equal(1700000000123, status.LastBuildAtMs);
            Assert.True(status.Degraded);
            Assert.Equal("索引尚未构建", status.DegradeReason);
            Assert.Equal(7880704, status.RssBytes);
        }
    }

    [Fact]
    public async Task ListAppsParsesCandidates()
    {
        var (client, _) = CreateConnected();
        using (client)
        {
            var result = await client.ListAppsAsync();
            Assert.Equal(2, result.Count);
            Assert.False(result.Building);
            Assert.False(result.Degraded);
            Assert.Null(result.DegradeReason);
            Assert.Equal(2, result.Apps.Count);
            Assert.Equal("Edge", result.Apps[0].NameHint);
            Assert.Equal("start-menu", result.Apps[0].Source);
            // 权威源纪律：引擎不解析 lnk 目标，targetPath == path
            Assert.Equal(result.Apps[0].Path, result.Apps[0].TargetPath);
        }
    }

    [Fact]
    public async Task ApplySettingsSendsKebabCaseKeys()
    {
        var (client, transport) = CreateConnected();
        using (client)
        {
            await client.ApplySettingsAsync(new IndexSettingsPatch
            {
                AppSourceBackend = "local",
                MaxEntries = 1234,
                ScanRoots = new[] { @"D:\迅雷下载" },
                Enabled = false,
            });

            var req = transport.LastRequestOf(IndexIpcProtocol.M_ApplySettings);
            Assert.NotNull(req);
            var prms = req!.Value.GetProperty("params");
            // 【跨语言纪律】键必须是 kebab-case——两侧命名策略漂移会让请求被静默忽略
            Assert.Equal("local", prms.GetProperty("app-source-backend").GetString());
            Assert.Equal(1234, prms.GetProperty("max-entries").GetInt32());
            Assert.Equal(@"D:\迅雷下载", prms.GetProperty("scan-roots")[0].GetString());
            Assert.False(prms.GetProperty("enabled").GetBoolean());
        }
    }

    [Fact]
    public async Task ShutdownSendsShutdownMethod()
    {
        var (client, transport) = CreateConnected();
        using (client)
        {
            await client.ShutdownAsync();
            Assert.NotNull(transport.LastRequestOf(IndexIpcProtocol.M_Shutdown));
        }
    }

    /// <summary>协议红线：每帧（含首帧）都必须携带 magic，否则引擎在首帧处丢弃连接。</summary>
    [Fact]
    public async Task EveryOutgoingFrameCarriesMagic()
    {
        var (client, transport) = CreateConnected();
        using (client)
        {
            await client.PingAsync();
            await client.GetStatusAsync();
            Assert.NotEmpty(transport.RawRequests);
            Assert.All(transport.RawRequests, raw => Assert.StartsWith(IndexIpcProtocol.Magic, raw));
        }
    }

    [Fact]
    public void ReconnectedFiresAfterConnect()
    {
        using var signaled = new ManualResetEventSlim(false);
        // FakeTransport 由 client 持有并释放；此处 using 仅为满足 CA2000（Dispose 幂等）
        using var transport = new FakeTransport();
        transport.UseDefaultHandler(FakeTransport.SampleStatusJson);
        var client = new IndexIpcClient(transport);
        using (client)
        {
            client.Reconnected += () => signaled.Set();
            client.Connect();
            Assert.True(signaled.Wait(2000), "Reconnected 未触发");
        }
    }

    // ---------------- 边界 ----------------

    /// <summary>降级契约：未连接时 TryGetStatusAsync 必须返回 null（调用方据此回退本地实现）。</summary>
    [Fact]
    public async Task TryGetStatusReturnsNullWhenNotConnected()
    {
        using var transport = new FakeTransport();
        using var client = new IndexIpcClient(transport, timeoutMs: 300);
        // 故意不 Connect
        Assert.Null(await client.TryGetStatusAsync());
    }

    /// <summary>引擎不响应 → RPC 超时，抛 IndexIpcException（不得挂死）。</summary>
    [Fact]
    public async Task RpcTimeoutThrowsIndexIpcException()
    {
        using var transport = new FakeTransport { ServerHandler = _ => null };
        using var client = new IndexIpcClient(transport, timeoutMs: 300);
        client.Connect();
        WaitUntil(() => client.IsConnected, what: "client connected");
        var ex = await Assert.ThrowsAsync<IndexIpcException>(() => client.PingAsync());
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectFailureIsRetriedAndClientStaysUsable()
    {
        using var transport = new FakeTransport { FailNextConnect = true };
        transport.UseDefaultHandler(FakeTransport.SampleStatusJson);
        using var client = new IndexIpcClient(transport, timeoutMs: 3000);
        client.Connect();
        // 首次 Connect 失败 → 退避后重试；最终连通并可用
        WaitUntil(() => client.IsConnected, timeoutMs: 4000, what: "retry connect");
        Assert.NotNull(await client.GetStatusAsync());
        Assert.True(transport.ConnectCount >= 2, $"应至少重试一次，实际 {transport.ConnectCount}");
    }

    // ---------------- 异常 ----------------

    [Fact]
    public async Task EngineErrorSurfacesWithErrorCode()
    {
        using var transport = new FakeTransport
        {
            ServerHandler = frame =>
            {
                using var doc = JsonDocument.Parse(frame);
                long id = doc.RootElement.GetProperty("id").GetInt64();
                return FakeTransport.Err(id, IndexIpcProtocol.ErrInvalidParams, "app-source-backend 非法值 'bogus'");
            },
        };
        using var client = new IndexIpcClient(transport, timeoutMs: 3000);
        client.Connect();
        WaitUntil(() => client.IsConnected, what: "client connected");

        var ex = await Assert.ThrowsAsync<IndexIpcException>(() =>
            client.ApplySettingsAsync(new IndexSettingsPatch { AppSourceBackend = "bogus" }));
        Assert.Equal(IndexIpcProtocol.ErrInvalidParams, ex.ErrorCode);
        Assert.Contains("bogus", ex.Message);
    }

    // ---------------- 纯函数 ----------------

    [Fact]
    public void BuildRequestOmitsParamsWhenNull()
    {
        string without = IndexIpcClient.BuildRequest(1, "ping", null);
        Assert.DoesNotContain("params", without, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(without);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("ping", doc.RootElement.GetProperty("method").GetString());

        string with = IndexIpcClient.BuildRequest(2, "apply_settings", new Dictionary<string, object?> { ["enabled"] = true });
        Assert.Contains("params", with, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSettingsPayloadOmitsNullFields()
    {
        var payload = IndexIpcClient.BuildSettingsPayload(new IndexSettingsPatch { MaxEntries = 5 });
        Assert.Single(payload);
        Assert.True(payload.ContainsKey("max-entries"));
        Assert.Empty(IndexIpcClient.BuildSettingsPayload(new IndexSettingsPatch()));
    }
}
