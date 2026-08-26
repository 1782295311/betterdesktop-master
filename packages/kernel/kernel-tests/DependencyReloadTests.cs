// BetterDesktop.Kernel.Tests — 依赖驱动重载契约测试（ADR-002 D1）
// 契约二：依赖未满足保持 PENDING；提供后加载；实例变化自动重载；同实例重复提供不重载

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class DependencyReloadTests
{
    private interface IService
    {
    }

    private sealed class Service : IService
    {
    }

    [Fact(DisplayName = "依赖未满足保持 Pending，提供后加载，实例变化自动重载")]
    public async Task Inject_WaitThenReload()
    {
        using var context = new CordisContext();
        var plugin = new TestPlugin("dependent") { Inject = new[] { typeof(IService) } };
        var handle = context.Plugin(plugin);

        await handle.AwaitAsync();
        Assert.Equal(PluginState.Pending, handle.State);
        Assert.Equal(0, plugin.LoadCount);

        context.Provide<IService>(new Service());
        await handle.AwaitAsync();
        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(1, plugin.LoadCount);

        context.Provide<IService>(new Service());
        await handle.AwaitAsync();
        Assert.Equal(2, plugin.LoadCount);
        Assert.Equal(1, plugin.UnloadCount);
    }

    [Fact(DisplayName = "同一实例重复提供，不触发重载")]
    public async Task Provide_SameInstance_NoReload()
    {
        using var context = new CordisContext();
        var service = new Service();
        context.Provide<IService>(service);
        var plugin = new TestPlugin("dependent") { Inject = new[] { typeof(IService) } };
        var handle = context.Plugin(plugin);
        await handle.AwaitAsync();
        Assert.Equal(1, plugin.LoadCount);

        context.Provide<IService>(service);
        await handle.AwaitAsync();
        Assert.Equal(1, plugin.LoadCount);
    }

    [Fact(DisplayName = "加载抛异常进入 Failed，不拖垮上下文")]
    public async Task Load_Throws_FailedAndIsolated()
    {
        using var context = new CordisContext();
        var plugin = new TestPlugin("broken", _ => throw new InvalidOperationException("boom"));
        var handle = context.Plugin(plugin);

        await handle.AwaitAsync();
        Assert.Equal(PluginState.Failed, handle.State);

        // 上下文仍可用：其它插件照常加载
        var healthy = new TestPlugin("healthy");
        var healthyHandle = context.Plugin(healthy);
        await healthyHandle.AwaitAsync();
        Assert.Equal(PluginState.Active, healthyHandle.State);
    }

    [Fact(DisplayName = "已释放插件忽略服务变化，不幽灵重启")]
    public async Task DisposedPlugin_IgnoresServiceChanges()
    {
        using var context = new CordisContext();
        context.Provide<IService>(new Service());
        var plugin = new TestPlugin("dependent") { Inject = new[] { typeof(IService) } };
        var handle = context.Plugin(plugin);
        await handle.AwaitAsync();
        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(1, plugin.LoadCount);

        await handle.DisposeAsync();
        Assert.Equal(PluginState.Disposed, handle.State);

        context.Provide<IService>(new Service());
        await Task.Delay(100);
        Assert.Equal(PluginState.Disposed, handle.State);
        Assert.Equal(1, plugin.LoadCount);
        Assert.Equal(1, plugin.UnloadCount);
    }
}
