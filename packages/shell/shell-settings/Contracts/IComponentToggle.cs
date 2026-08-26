namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 组件启停预留契约：每个可独立开关的桌面组件（菜单栏 / Dock / 扩展中心 / 右键菜单 等）
/// 实现此接口，由系统管理分区的"组件管理"开关驱动。
/// 当前实现约定：组件在 LoadAsync 开头读取对应的 <c>components.*</c> 设置键，
/// 为 false 则跳过自身 UI 创建（不启该组件）。预留 Toggle 方法供运行时动态启停。
/// </summary>
public interface IComponentToggle
{
    /// <summary>组件标识（对应设置键 components.&lt;id&gt;，如 "dock" / "menubar"）。</summary>
    string ComponentId { get; }

    /// <summary>当前是否处于启用状态。</summary>
    bool IsEnabled { get; }

    /// <summary>运行时启停（true=启用并创建 UI；false=停用并销毁 UI）。</summary>
    void Toggle(bool enabled);
}
