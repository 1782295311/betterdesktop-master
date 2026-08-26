// BetterDesktop.Kernel.Hmr — HmrEvents 生命周期事件名常量
// 事件名约定「域/动作」，见 ADR-002 D4

namespace BetterDesktop.Kernel.Hmr;

/// <summary>HMR 生命周期事件名常量（事件名约定「域/动作」，见 ADR-002 D4）。</summary>
public static class HmrEvents
{
    /// <summary>插件已加载。</summary>
    public const string Loaded = "kernel.plugin/loaded";

    /// <summary>插件已卸载。</summary>
    public const string Unloaded = "kernel.plugin/unloaded";

    /// <summary>插件重载成功。</summary>
    public const string ReloadSucceeded = "kernel.plugin/reload-succeeded";

    /// <summary>插件重载失败并回滚。</summary>
    public const string RolledBack = "kernel.plugin/rolled-back";

    /// <summary>插件被拒绝加载（版本不兼容或依赖缺失）。</summary>
    public const string Rejected = "kernel.plugin/rejected";
}
