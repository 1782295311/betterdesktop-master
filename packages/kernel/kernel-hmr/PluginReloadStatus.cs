// BetterDesktop.Kernel.Hmr — PluginReloadStatus 插件生命周期状态
// 区别于内核 PluginState 的 fiber 状态机

namespace BetterDesktop.Kernel.Hmr;

/// <summary>HMR 管理的插件生命周期状态（区别于内核 PluginState 的 fiber 状态机）。</summary>
public enum PluginReloadStatus
{
    /// <summary>从未被本管理器管理。</summary>
    Unknown,

    /// <summary>首次加载中。</summary>
    Loading,

    /// <summary>已加载并活跃。</summary>
    Loaded,

    /// <summary>热重载中（旧实例仍活跃，先立后破）。</summary>
    Reloading,

    /// <summary>加载或重载失败，且当前无活跃实例。</summary>
    Failed,

    /// <summary>已卸载。</summary>
    Unloaded
}
