// BetterDesktop.Kernel.Hmr — IPluginStateProvider 可选状态迁移能力
// 热重载「旧配置迁移」的插件侧契约

namespace BetterDesktop.Kernel.Hmr;

/// <summary>可选插件能力：实现本接口的插件在热重载时先 Capture 再 Restore，实现旧状态迁移。</summary>
public interface IPluginStateProvider
{
    /// <summary>捕获当前运行状态（卸载前调用；返回 null 表示无状态可迁移）。</summary>
    Task<PluginStateSnapshot?> CaptureStateAsync(CancellationToken cancellationToken = default);

    /// <summary>恢复运行状态（新实例激活后调用；异常被隔离并记录，不阻断切换）。</summary>
    Task RestoreStateAsync(PluginStateSnapshot state, CancellationToken cancellationToken = default);
}
