// BetterDesktop.Kernel.Tests — 事件服务契约测试（ADR-002 D4）
// 契约四：五种分发 + 单监听器异常隔离 + 注销

using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class EventBusTests
{
    [Fact(DisplayName = "emit 并发广播全部监听器")]
    public async Task Emit_BroadcastsAllHandlers()
    {
        using var context = new CordisContext();
        var seen = new List<int>();
        context.Events.On<int>("test/count", (payload, _) => { seen.Add(payload); return Task.CompletedTask; });
        context.Events.On<int>("test/count", (payload, _) => { seen.Add(payload); return Task.CompletedTask; });

        await context.Events.EmitAsync("test/count", 42);

        Assert.Equal(new[] { 42, 42 }, seen);
    }

    [Fact(DisplayName = "单监听器异常被隔离，其余照常执行")]
    public async Task Emit_ThrowingHandler_Isolated()
    {
        using var context = new CordisContext();
        var seen = new List<int>();
        context.Events.On<int>("test/count", (_, _) => throw new InvalidOperationException("boom"));
        context.Events.On<int>("test/count", (payload, _) => { seen.Add(payload); return Task.CompletedTask; });

        await context.Events.EmitAsync("test/count", 7);

        Assert.Equal(new[] { 7 }, seen);
    }

    [Fact(DisplayName = "waterfall 顺序折叠")]
    public async Task Waterfall_FoldsInOrder()
    {
        using var context = new CordisContext();
        context.Events.OnResult<int, int>("test/fold", (payload, _) => Task.FromResult(payload + 1));
        context.Events.OnResult<int, int>("test/fold", (payload, _) => Task.FromResult(payload * 2));

        var result = await context.Events.WaterfallAsync("test/fold", 3);

        Assert.Equal(8, result);
    }

    [Fact(DisplayName = "bail 首个非默认值短路")]
    public async Task Bail_FirstNonNullStops()
    {
        using var context = new CordisContext();
        var calls = 0;
        context.Events.OnResult<int, string?>("test/bail", (payload, _) => { calls++; return Task.FromResult<string?>(null); });
        context.Events.OnResult<int, string?>("test/bail", (payload, _) => { calls++; return Task.FromResult<string?>("hit"); });
        context.Events.OnResult<int, string?>("test/bail", (payload, _) => { calls++; return Task.FromResult<string?>("never"); });

        var result = await context.Events.BailAsync<int, string?>("test/bail", 1);

        Assert.Equal("hit", result);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "注销句柄移除监听器")]
    public async Task On_DisposeHandle_Unsubscribes()
    {
        using var context = new CordisContext();
        var seen = new List<int>();
        var handle = context.Events.On<int>("test/count", (payload, _) => { seen.Add(payload); return Task.CompletedTask; });

        handle.Dispose();
        await context.Events.EmitAsync("test/count", 1);

        Assert.Empty(seen);
    }
}
