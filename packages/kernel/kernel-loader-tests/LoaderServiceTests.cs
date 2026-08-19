// BetterDesktop.Kernel.Loader.Tests — loader 契约测试
// 装配启用条目 / 跳过禁用 / 未知工厂 fail-closed / 解析失败抛错

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Loader;
using Xunit;

namespace BetterDesktop.Kernel.Loader.Tests;

public sealed class LoaderServiceTests : IDisposable
{
    private readonly string _configPath;

    public LoaderServiceTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"cordis-{Guid.NewGuid():N}.yml");
    }

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }
    }

    private sealed class CountingPlugin : IPlugin
    {
        public static int LoadedInstances;

        public string Name => "test.counting";

        public IReadOnlyList<Type> Inject => Array.Empty<Type>();

        public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
        {
            LoadedInstances++;
            return Task.CompletedTask;
        }

        public Task UnloadAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    [Fact(DisplayName = "装配启用条目，禁用条目跳过")]
    public async Task Load_EnabledLoaded_DisabledSkipped()
    {
        File.WriteAllText(_configPath, "plugins:\n  - id: a\n    name: count\n    enabled: true\n  - id: b\n    name: count\n    enabled: false\n");
        using var context = new CordisContext();
        var loader = new LoaderService(new LoaderOptions
        {
            ConfigPath = _configPath,
            Factories = { ["count"] = () => new CountingPlugin() }
        });
        var handle = context.Plugin(loader);

        await handle.AwaitAsync();
        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(2, loader.Reports.Count);
        Assert.Equal(LoaderEntryStatus.Loaded, loader.Reports[0].Status);
        Assert.Equal(LoaderEntryStatus.Skipped, loader.Reports[1].Status);
    }

    [Fact(DisplayName = "enabled 缺省为 true")]
    public async Task Load_EnabledDefaultTrue()
    {
        File.WriteAllText(_configPath, "plugins:\n  - id: a\n    name: count\n");
        using var context = new CordisContext();
        var loader = new LoaderService(new LoaderOptions
        {
            ConfigPath = _configPath,
            Factories = { ["count"] = () => new CountingPlugin() }
        });
        var handle = context.Plugin(loader);

        await handle.AwaitAsync();
        Assert.Equal(LoaderEntryStatus.Loaded, loader.Reports[0].Status);
    }

    [Fact(DisplayName = "未知工厂 fail-closed：条目失败，loader 仍活跃")]
    public async Task Load_UnknownFactory_FailClosedNotCrash()
    {
        File.WriteAllText(_configPath, "plugins:\n  - id: a\n    name: ghost\n");
        using var context = new CordisContext();
        var loader = new LoaderService(new LoaderOptions { ConfigPath = _configPath });
        var handle = context.Plugin(loader);

        await handle.AwaitAsync();
        Assert.Equal(PluginState.Active, handle.State);
        Assert.Equal(LoaderEntryStatus.UnknownFactory, loader.Reports[0].Status);
    }

    [Fact(DisplayName = "配置缺失/非法：loader 进入 Failed 并带明确错误")]
    public async Task Load_MissingConfig_FailsLoudly()
    {
        using var context = new CordisContext();
        var loader = new LoaderService(new LoaderOptions { ConfigPath = _configPath });
        var handle = context.Plugin(loader);

        await handle.AwaitAsync();
        Assert.Equal(PluginState.Failed, handle.State);
    }
}
