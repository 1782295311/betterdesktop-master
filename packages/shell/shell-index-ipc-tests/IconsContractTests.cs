using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.IndexIpc.Tests;

/// <summary>
/// 【M4c · 2026-09-14】<c>get_icons</c> 的跨语言契约测试（fake transport，不依赖真机引擎）。
///
/// 重点锁四件事：
/// ① **参数线格式**——引擎按 camelCase 解析 <c>{ keys, refresh }</c>，键名漂移 → 请求被静默忽略；
/// ② **分批**——引擎对超限批次返回 -32602，客户端必须**自动分片**，否则图标会整批静默消失；
/// ③ **refresh 只随首片下发**——每片都清缓存会让刚取回的图标被下一片挤掉；
/// ④ **缺失不静默**——提取失败的键由 <c>missing</c> 计数暴露，调用方据此走 glyph 兜底。
/// </summary>
public sealed class IconsContractTests
{
    /// <summary>1×1 PNG（真 PNG magic，用于验证 base64 往返与字节还原）。</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G' };

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000)
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

        throw new TimeoutException("client not connected in time");
    }

    private static (IndexIpcClient Client, FakeTransport Transport) Connected(string resultJson)
        => ConnectedWith(Reply(resultJson));

    private static (IndexIpcClient Client, FakeTransport Transport) ConnectedWith(Func<string, string?> handler)
    {
        var transport = new FakeTransport { ServerHandler = handler };
        var client = new IndexIpcClient(transport, 5000);
        client.Connect();
        WaitUntil(() => client.IsConnected);
        return (client, transport);
    }

    private static IndexIpcClient NotConnectedClient()
    {
#pragma warning disable CA2000 // transport 所有权移交给 IndexIpcClient（其 Dispose 会释放）
        var transport = new FakeTransport();
        return new IndexIpcClient(transport, 200);
#pragma warning restore CA2000
    }

    private static Func<string, string?> Reply(string resultJson) => frame =>
    {
        using var doc = JsonDocument.Parse(frame);
        long id = doc.RootElement.GetProperty("id").GetInt64();
        return FakeTransport.Ok(id, resultJson);
    };

    private static Func<string, string?> FailWith(int code, string message) => frame =>
    {
        using var doc = JsonDocument.Parse(frame);
        long id = doc.RootElement.GetProperty("id").GetInt64();
        return FakeTransport.Err(id, code, message);
    };

    // ---------------- 正常路径 ----------------

    [Fact]
    public async Task GetIcons_ParsesPngBytesAndSendsCamelCaseParams()
    {
        var (client, transport) = Connected(
            $$"""{"icons":[{"key":"C:\\a.exe","pngBase64":"{{OnePixelPngBase64}}"}],"count":1,"missing":1}""");

        using (client)
        {
            var result = await client.GetIconsAsync(new[] { @"C:\a.exe", @"C:\b.exe" });

            Assert.Equal(1, result.Count);
            Assert.Equal(1, result.Missing);

            var hit = result.Icons[0];
            Assert.Equal(@"C:\a.exe", hit.Key);

            // 字节还原：base64 → PNG（magic 校验）
            var bytes = hit.TryDecodePng();
            Assert.NotNull(bytes);
            Assert.Equal(PngMagic, bytes!.Take(4).ToArray());

            // 线格式：params.keys 必须存在（引擎按 camelCase 解析）
            var parameters = transport.LastRequestOf("get_icons")!.Value.GetProperty("params");
            Assert.Equal(2, parameters.GetProperty("keys").GetArrayLength());
        }
    }

    /// <summary>refresh=false（默认）时不下发该键——避免两侧各写一份默认值。</summary>
    [Fact]
    public async Task GetIcons_OmitsRefreshWhenNotRequested()
    {
        var (client, transport) = Connected("""{"icons":[],"count":0,"missing":0}""");
        using (client)
        {
            await client.GetIconsAsync(new[] { @"C:\a.exe" });

            var parameters = transport.LastRequestOf("get_icons")!.Value.GetProperty("params");
            Assert.False(parameters.TryGetProperty("refresh", out _), "未请求刷新时不应下发 refresh");
        }
    }

    [Fact]
    public async Task GetIcons_SendsRefreshWhenRequested()
    {
        var (client, transport) = Connected("""{"icons":[],"count":0,"missing":0}""");
        using (client)
        {
            await client.GetIconsAsync(new[] { @"C:\a.exe" }, refresh: true);

            var parameters = transport.LastRequestOf("get_icons")!.Value.GetProperty("params");
            Assert.True(parameters.GetProperty("refresh").GetBoolean());
        }
    }

    /// <summary>键集合过滤后为空 → 不发 RPC（空 keys 会让引擎返回 -32602，没必要求一个错误）。</summary>
    [Fact]
    public async Task GetIcons_ReturnsEmptyWithoutRpc_WhenKeysBlank()
    {
        var (client, transport) = Connected("""{"icons":[],"count":0,"missing":0}""");
        using (client)
        {
            var result = await client.GetIconsAsync(new[] { " ", "" });

            Assert.Equal(0, result.Count);
            Assert.Null(transport.LastRequestOf("get_icons"));
        }
    }

    // ---------------- 分批（本次最重要的一组） ----------------

    /// <summary>
    /// 129 个键（= 上限 64 × 2 + 1）必须切成 3 片；**refresh 只随首片**下发
    /// （每片都清缓存会把刚取回的图标立刻挤掉）；分片结果必须合并成一个结果集。
    /// </summary>
    [Fact]
    public async Task GetIcons_AutoChunksAndSendsRefreshOnlyOnFirstChunk()
    {
        var chunkSizes = new List<int>();
        var refreshFlags = new List<bool>();

        var (client, _) = ConnectedWith(frame =>
        {
            using var doc = JsonDocument.Parse(frame);
            long id = doc.RootElement.GetProperty("id").GetInt64();
            var parameters = doc.RootElement.GetProperty("params");
            var keys = parameters.GetProperty("keys");

            chunkSizes.Add(keys.GetArrayLength());
            refreshFlags.Add(
                parameters.TryGetProperty("refresh", out var r) && r.GetBoolean());

            var sb = new StringBuilder("{\"icons\":[");
            int n = keys.GetArrayLength();
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"key\":\"").Append(keys[i].GetString()).Append("\",\"pngBase64\":\"\"}");
            }

            sb.Append("],\"count\":").Append(n).Append(",\"missing\":0}");
            return FakeTransport.Ok(id, sb.ToString());
        });

        using (client)
        {
            var keys = new string[IndexIpcProtocol.MaxIconBatch * 2 + 1];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = "k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var result = await client.GetIconsAsync(keys, refresh: true);

            Assert.Equal(3, chunkSizes.Count);
            Assert.Equal(IndexIpcProtocol.MaxIconBatch, chunkSizes[0]);
            Assert.Equal(IndexIpcProtocol.MaxIconBatch, chunkSizes[1]);
            Assert.Equal(1, chunkSizes[2]);

            Assert.Equal(new[] { true, false, false }, refreshFlags);

            Assert.Equal(keys.Length, result.Count);
            Assert.Equal(0, result.Missing);
        }
    }

    // ---------------- 降级路径 ----------------

    /// <summary>引擎报错 → Try 包装返回 null，调用方回退本地 Shell 提取。</summary>
    [Fact]
    public async Task TryGetIcons_ReturnsNull_WhenEngineErrors()
    {
        var (client, _) = ConnectedWith(FailWith(IndexIpcProtocol.ErrInvalidParams, "keys 不能为空"));
        using (client)
        {
            Assert.Null(await client.TryGetIconsAsync(new[] { @"C:\a.exe" }));
        }
    }

    [Fact]
    public async Task TryGetIcons_ReturnsNull_WhenNotConnected()
    {
        var client = NotConnectedClient();
        using (client)
        {
            Assert.Null(await client.TryGetIconsAsync(new[] { @"C:\a.exe" }));
        }
    }

    /// <summary>非 Try 版本必须抛且保留错误码（供「按错误码分流」的场景）。</summary>
    [Fact]
    public async Task GetIcons_ThrowsWithErrorCode_WhenEngineErrors()
    {
        var (client, _) = ConnectedWith(FailWith(IndexIpcProtocol.ErrInvalidParams, "keys 不能为空"));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<IndexIpcException>(
                () => client.GetIconsAsync(new[] { @"C:\a.exe" }));
            Assert.Equal(IndexIpcProtocol.ErrInvalidParams, ex.ErrorCode);
        }
    }

    // ---------------- 坏数据不得抛（图标是非关键资源） ----------------

    [Fact]
    public void TryDecodePng_ReturnsNullOnInvalidBase64()
    {
        Assert.Null(new IconHit { PngBase64 = "!!!not-base64!!!" }.TryDecodePng());
        Assert.Null(new IconHit { PngBase64 = string.Empty }.TryDecodePng());
    }
}
