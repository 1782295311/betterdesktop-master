// BetterDesktop.Kernel.Hmr.Tests — HmrManager 契约测试
// 动态加载 / 卸载 / 重载 / 版本兼容 / 依赖校验 / 状态迁移 / 失败回滚 / 监控事件

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using Xunit;

namespace BetterDesktop.Kernel.Hmr.Tests;

public sealed class HmrManagerTests
{
    private static HmrManager CreateManager(IContext context, Func<PluginManifest, IPlugin> factory) =>
        new(context, sourceFactory: _ => new DelegatePluginSource(manifest => Task.FromResult(factory(manifest))));

    [Fact(DisplayName = "加载成功并进入 Loaded 状态")]
    public async Task Load_Succeeds()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, _ => new TestHmrPlugin("p"));
        var result = await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(PluginReloadStatus.Loaded, result.Status);
        Assert.Equal(PluginReloadStatus.Loaded, manager.GetPluginStatus("p"));
    }

    [Fact(DisplayName = "内核 ABI 不兼容被拒绝")]
    public async Task Load_IncompatibleAbi_Rejected()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, _ => new TestHmrPlugin("p"));
        var result = await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(2, 0, 0)));

        Assert.False(result.Success);
        Assert.Equal(PluginReloadStatus.Failed, result.Status);
        Assert.Contains("ABI 不兼容", result.Error);
    }

    [Fact(DisplayName = "必需依赖缺失被拒绝，可选依赖缺失放行")]
    public async Task Load_DependencyValidation()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, m => new TestHmrPlugin(m.Name));

        var manifest = new PluginManifest("dep", "dep", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        manifest.Dependencies.Add(new PluginDependency("missing.required"));
        manifest.Dependencies.Add(new PluginDependency("missing.optional", optional: true));

        var result = await manager.LoadPluginAsync(manifest);
        Assert.False(result.Success);
        Assert.Contains("missing.required", result.Error);
        Assert.DoesNotContain("missing.optional", result.Error);
    }

    [Fact(DisplayName = "依赖已加载则放行")]
    public async Task Load_DependencySatisfied_Succeeds()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, m => new TestHmrPlugin(m.Name));
        await manager.LoadPluginAsync(new PluginManifest("base", "base", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));

        var manifest = new PluginManifest("dep", "dep", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        manifest.Dependencies.Add(new PluginDependency("base", new SemanticVersion(1, 0, 0)));

        var result = await manager.LoadPluginAsync(manifest);
        Assert.True(result.Success, result.Error);
    }

    [Fact(DisplayName = "卸载后状态为 Unloaded")]
    public async Task Unload_SetsStatus()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, _ => new TestHmrPlugin("p"));
        await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));

        Assert.True(await manager.UnloadPluginAsync("p"));
        Assert.Equal(PluginReloadStatus.Unloaded, manager.GetPluginStatus("p"));
        Assert.False(await manager.UnloadPluginAsync("p"));
    }

    [Fact(DisplayName = "重载成功：旧实例卸载、新实例激活并迁移状态")]
    public async Task Reload_Succeeds_MigratesState()
    {
        using var context = new CordisContext();
        var created = new List<TestHmrPlugin>();
        using var manager = new HmrManager(context, sourceFactory: _ => new DelegatePluginSource(_ =>
        {
            var plugin = new TestHmrPlugin("p");
            created.Add(plugin);
            return Task.FromResult<IPlugin>(plugin);
        }));

        var manifest = new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        Assert.True((await manager.LoadPluginAsync(manifest)).Success);
        Assert.Single(created);

        var reload = await manager.ReloadPluginAsync("p");
        Assert.True(reload.Success, reload.Error);
        Assert.False(reload.RolledBack);
        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].UnloadCount);
        Assert.Equal(1, created[1].LoadCount);
        Assert.Equal(1, created[0].CaptureCount);
        Assert.Equal(1, created[1].RestoreCount);
        Assert.Equal(created[0].CapturedPayload, created[1].RestoredPayload);
        Assert.Equal(PluginReloadStatus.Loaded, manager.GetPluginStatus("p"));
    }

    [Fact(DisplayName = "重载失败：回滚到旧版本并记录监控计数")]
    public async Task Reload_Fails_RollsBack()
    {
        using var context = new CordisContext();
        var attempt = 0;
        using var manager = new HmrManager(context, sourceFactory: _ => new DelegatePluginSource(_ =>
        {
            attempt++;
            IPlugin plugin = attempt == 1 ? new TestHmrPlugin("p") : new FailingPlugin();
            return Task.FromResult(plugin);
        }));

        Assert.True((await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)))).Success);

        var reload = await manager.ReloadPluginAsync("p");
        Assert.False(reload.Success);
        Assert.True(reload.RolledBack);
        Assert.Equal(PluginReloadStatus.Loaded, manager.GetPluginStatus("p"));

        var info = Assert.Single(manager.GetPluginRuntimeInfos());
        Assert.Equal(1, info.LoadCount);
        Assert.Equal(1, info.FailureCount);
        Assert.Equal(1, info.RollbackCount);
    }

    [Fact(DisplayName = "ReloadAll 返回成功重载数量")]
    public async Task ReloadAll_ReturnsCount()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, m => new TestHmrPlugin(m.Name));
        await manager.LoadPluginAsync(new PluginManifest("a", "a", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));
        await manager.LoadPluginAsync(new PluginManifest("b", "b", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));

        Assert.Equal(2, await manager.ReloadAllPluginsAsync());
    }

    [Fact(DisplayName = "禁用后加载被拒绝")]
    public async Task Disabled_RejectsLoad()
    {
        using var context = new CordisContext();
        using var manager = CreateManager(context, _ => new TestHmrPlugin("p"));
        manager.Disable();
        var result = await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));

        Assert.False(result.Success);
        Assert.Contains("已禁用", result.Error);
        manager.Enable();
        Assert.True(manager.IsEnabled);
    }

    [Fact(DisplayName = "生命周期事件经内核事件总线分发")]
    public async Task LifecycleEvents_Emitted()
    {
        using var context = new CordisContext();
        var events = new List<PluginLifecycleEvent>();
        context.Events.On<PluginLifecycleEvent>(HmrEvents.Loaded, (e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });
        context.Events.On<PluginLifecycleEvent>(HmrEvents.RolledBack, (e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });

        var attempt = 0;
        using var manager = new HmrManager(context, sourceFactory: _ => new DelegatePluginSource(_ =>
        {
            attempt++;
            IPlugin plugin = attempt == 1 ? new TestHmrPlugin("p") : new FailingPlugin();
            return Task.FromResult(plugin);
        }));

        await manager.LoadPluginAsync(new PluginManifest("p", "p", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0)));
        await manager.ReloadPluginAsync("p");

        Assert.Contains(events, e => e.Kind == PluginLifecycleKind.Loaded);
        Assert.Contains(events, e => e.Kind == PluginLifecycleKind.RolledBack);
    }
}
