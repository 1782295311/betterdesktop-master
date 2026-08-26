// BetterDesktop.Kernel.Hmr — PluginModule 已构造插件模块
// 包装 IPlugin 实例与其释放动作（卸载 ALC 等），幂等释放

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>一个已构造的插件模块：包装 IPlugin 实例与其释放动作（如卸载 ALC），幂等释放。</summary>
public sealed class PluginModule : IAsyncDisposable
{
    private readonly Func<Task>? _disposeAsync;
    private int _disposed;

    /// <summary>构造：disposeAsync 为 null 时释放为无操作。</summary>
    public PluginModule(IPlugin plugin, Func<Task>? disposeAsync = null)
    {
        Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _disposeAsync = disposeAsync;
    }

    /// <summary>插件实例。</summary>
    public IPlugin Plugin { get; }

    /// <summary>若插件实现 IPluginStateProvider 则返回该视图，否则为 null。</summary>
    public IPluginStateProvider? StateProvider => Plugin as IPluginStateProvider;

    /// <summary>释放模块（卸载 ALC 等）；幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_disposeAsync is not null)
        {
            await _disposeAsync().ConfigureAwait(false);
        }
    }
}
