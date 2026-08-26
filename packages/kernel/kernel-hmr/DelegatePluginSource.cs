// BetterDesktop.Kernel.Hmr — DelegatePluginSource 委托插件来源
// 每次 LoadAsync 调用工厂新建实例（内存插件与测试用）

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>委托插件来源：每次 LoadAsync 调用工厂新建实例（内存插件与测试用）。</summary>
public sealed class DelegatePluginSource : IPluginSource
{
    private readonly Func<PluginManifest, Task<IPlugin>> _factory;

    /// <summary>构造。</summary>
    public DelegatePluginSource(Func<PluginManifest, Task<IPlugin>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public async Task<PluginModule> LoadAsync(PluginManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var plugin = await _factory(manifest).ConfigureAwait(false);
        return new PluginModule(plugin);
    }
}
