using System.Diagnostics;
using System.Text.Json;
using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.IndexIpc.Tests;

/// <summary>
/// 【M3b · 2026-09-14】<c>search_files</c> 的跨语言契约测试（fake transport，不依赖真机引擎）。
///
/// 重点锁两件事：
/// ① **参数线格式**——引擎按 camelCase 解析 <c>{ query, limit }</c>，键名漂移会导致「查询被静默忽略」；
/// ② **降级语义**——「构建中 / 降级」必须返回 <c>null</c>（调用方据此回退本地实现），
///    绝不能返回空集：空集会被用户读成「没有这个文件」，而真相是「索引还没建好」。
/// </summary>
public sealed class SearchFilesContractTests
{
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

    /// <summary>用自定义应答体建一个已连通的客户端。</summary>
    private static (IndexIpcClient Client, FakeTransport Transport) Connected(string resultJson)
        => ConnectedWith(Reply(resultJson));

    /// <summary>
    /// 用自定义应答器建一个已连通的客户端。
    /// <para>transport 随返回值**逃逸出本方法**——所有权移交给 <see cref="IndexIpcClient"/>
    ///（其 Dispose 会释放 transport），分析器据此不再报 CA2000。</para>
    /// </summary>
    private static (IndexIpcClient Client, FakeTransport Transport) ConnectedWith(Func<string, string?> handler)
    {
        var transport = new FakeTransport { ServerHandler = handler };
        var client = new IndexIpcClient(transport, 5000);
        client.Connect();
        WaitUntil(() => client.IsConnected);
        return (client, transport);
    }

    /// <summary>未连接的客户端（transport 所有权同样移交给 client）。</summary>
    private static IndexIpcClient NotConnectedClient()
    {
        // CA2000：transport 的所有权**移交**给 IndexIpcClient（其 Dispose 会 Dispose transport）——
        // 分析器识别不了这种「构造期所有权转移」，故局部豁免（与生产代码同写法）。
#pragma warning disable CA2000
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

    private const string HealthyBody = """
        {"files":[{"path":"D:/迅雷下载/MaaEnd-x.zip","name":"MaaEnd-x.zip","sizeBytes":1024,"modifiedMs":1700000000000}],
         "count":1,"building":false,"degraded":false,"degradeReason":null}
        """;

    // ---------------- 正常路径 ----------------

    [Fact]
    public async Task SearchFiles_ParsesHitsAndSendsCamelCaseParams()
    {
        var (client, transport) = Connected(HealthyBody);
        using (client)
        {
            var result = await client.SearchFilesAsync("maa", limit: 100);

            Assert.Equal(1, result.Count);
            Assert.False(result.Building);
            Assert.False(result.Degraded);
            var hit = result.Files[0];
            Assert.Equal("MaaEnd-x.zip", hit.Name);
            Assert.Equal("D:/迅雷下载/MaaEnd-x.zip", hit.Path);
            Assert.Equal(1024, hit.SizeBytes);
            Assert.Equal(1700000000000, hit.ModifiedMs);

            // 线格式：query / limit 必须为 camelCase 键（引擎按此解析）
            var request = transport.LastRequestOf("search_files");
            Assert.NotNull(request);
            var parameters = request!.Value.GetProperty("params");
            Assert.Equal("maa", parameters.GetProperty("query").GetString());
            Assert.Equal(100, parameters.GetProperty("limit").GetInt32());
        }
    }

    /// <summary>limit ≤ 0 时不下发 limit 字段（用引擎默认，避免两侧默认值各写一份）。</summary>
    [Fact]
    public async Task SearchFiles_OmitsLimitWhenNotSpecified()
    {
        var (client, transport) = Connected(HealthyBody);
        using (client)
        {
            await client.SearchFilesAsync("maa");

            var parameters = transport.LastRequestOf("search_files")!.Value.GetProperty("params");
            Assert.False(parameters.TryGetProperty("limit", out _), "未指定 limit 时不应下发该键");
        }
    }

    // ---------------- 降级路径（本文件最重要的一组） ----------------

    [Fact]
    public async Task TrySearchFiles_ReturnsNull_WhenBuilding()
    {
        var (client, _) = Connected(
            """{"files":[],"count":0,"building":true,"degraded":false,"degradeReason":null}""");
        using (client)
        {
            Assert.Null(await client.TrySearchFilesAsync("maa"));
        }
    }

    [Fact]
    public async Task TrySearchFiles_ReturnsNull_WhenDegraded()
    {
        var (client, _) = Connected(
            """{"files":[],"count":0,"building":false,"degraded":true,"degradeReason":"达到上限"}""");
        using (client)
        {
            Assert.Null(await client.TrySearchFilesAsync("maa"));
        }
    }

    /// <summary>引擎报错（如 -32602）→ Try 包装返回 null，调用方回退本地实现。</summary>
    [Fact]
    public async Task TrySearchFiles_ReturnsNull_WhenEngineErrors()
    {
        var (client, _) = ConnectedWith(FailWith(IndexIpcProtocol.ErrInvalidParams, "缺 query"));
        using (client)
        {
            Assert.Null(await client.TrySearchFilesAsync("maa"));
        }
    }

    /// <summary>非 Try 版本必须抛（且保留引擎错误码）——供需要区分失败原因的场景。</summary>
    [Fact]
    public async Task SearchFiles_ThrowsWithErrorCode_WhenEngineErrors()
    {
        var (client, _) = ConnectedWith(FailWith(IndexIpcProtocol.ErrInvalidParams, "缺 query"));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<IndexIpcException>(() => client.SearchFilesAsync("maa"));
            Assert.Equal(IndexIpcProtocol.ErrInvalidParams, ex.ErrorCode);
        }
    }

    /// <summary>未连接时 Try 包装返回 null（不得抛到 UI 线程）。</summary>
    [Fact]
    public async Task TrySearchFiles_ReturnsNull_WhenNotConnected()
    {
        var client = NotConnectedClient();
        using (client)
        {
            Assert.Null(await client.TrySearchFilesAsync("maa"));
        }
    }
}
