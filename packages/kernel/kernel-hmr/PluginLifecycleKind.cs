// BetterDesktop.Kernel.Hmr — PluginLifecycleKind 生命周期事件种类

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件生命周期事件种类。</summary>
public enum PluginLifecycleKind
{
    /// <summary>已加载。</summary>
    Loaded,

    /// <summary>已卸载。</summary>
    Unloaded,

    /// <summary>重载成功（新版本激活）。</summary>
    ReloadSucceeded,

    /// <summary>重载失败并回滚（旧版本继续服务）。</summary>
    RolledBack,

    /// <summary>被拒绝（版本/ABI 不兼容或依赖缺失）。</summary>
    Rejected
}
