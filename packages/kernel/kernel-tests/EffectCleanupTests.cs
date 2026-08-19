// BetterDesktop.Kernel.Tests — effect 托管清理契约测试（ADR-002 D1）
// 契约三：卸载逆序执行；单条异常隔离

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class EffectCleanupTests
{
    [Fact(DisplayName = "卸载时清理器逆序执行")]
    public async Task Dispose_EffectsRunInReverseOrder()
    {
        var order = new List<int>();
        var plugin = new TestPlugin(
            "effects",
            effects: new Func<IDisposable>[]
            {
                () => Track(1, order),
                () => Track(2, order),
                () => Track(3, order)
            });
        using var context = new CordisContext();
        var handle = context.Plugin(plugin);
        await handle.AwaitAsync();
        Assert.Equal(PluginState.Active, handle.State);

        await handle.DisposeAsync();
        Assert.Equal(new[] { 3, 2, 1 }, order);
        Assert.Equal(PluginState.Disposed, handle.State);
    }

    [Fact(DisplayName = "单条清理异常不阻断其余清理")]
    public async Task Dispose_ThrowingEffect_DoesNotBlockOthers()
    {
        var ran = new List<int>();
        var plugin = new TestPlugin(
            "effects",
            effects: new Func<IDisposable>[]
            {
                () => Track(1, ran),
                () => new ThrowingDisposable(),
                () => Track(3, ran)
            });
        using var context = new CordisContext();
        var handle = context.Plugin(plugin);
        await handle.AwaitAsync();

        await handle.DisposeAsync();
        Assert.Equal(new[] { 3, 1 }, ran);
        Assert.Equal(PluginState.Disposed, handle.State);
    }

    [Fact(DisplayName = "显式注销句柄立即执行清理，卸载时不重复")]
    public async Task EffectHandle_DisposeEarly_RunsOnceImmediately()
    {
        var ran = new List<int>();
        IDisposable? earlyHandle = null;
        var plugin = new TestPlugin(
            "effects",
            onLoad: context =>
            {
                earlyHandle = context.Effect(() => Track(1, ran));
                context.Effect(() => Track(2, ran));
            });
        using var context = new CordisContext();
        var handle = context.Plugin(plugin);
        await handle.AwaitAsync();

        earlyHandle!.Dispose();
        Assert.Equal(new[] { 1 }, ran);

        await handle.DisposeAsync();
        Assert.Equal(new[] { 1, 2 }, ran);
    }

    private static IDisposable Track(int id, List<int> order)
    {
        return new TrackDisposable(() => order.Add(id));
    }

    private sealed class TrackDisposable : IDisposable
    {
        private readonly Action _action;

        public TrackDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            _action();
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("boom");
        }
    }
}
