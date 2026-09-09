using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.PluginSdk;

/// <summary>
/// 插件窗口契约（插件 SDK 唯一依赖面）。
/// 插件只提供业务内容与窗口基础配置，外壳（ChromeBorder/描边/圆角/毛玻璃/字号）完全由内核
/// <c>PluginHostWindow</c> 托管——插件看不到 <c>ShellWindow</c>，也拿不到任何内核服务实例。
/// </summary>
public interface IShellPluginWindow
{
    /// <summary>插件内容 UI（相当于内置窗口的内部内容）。内核把它注入宿主外壳的内容区。</summary>
    UIElement Content { get; }

    /// <summary>窗口基础配置声明（白名单项，由内核校验后生效）。</summary>
    PluginWindowConfig Config { get; }

    /// <summary>
    /// 主题变化通知（可选）：仅当插件有自定义绘制（非 DynamicResource 绑定的颜色/字号）时才需实现。
    /// 载荷为只读 <see cref="ThemeSnapshot"/>，不暴露 <c>IAppearanceService</c>，插件无法修改全局主题。
    /// </summary>
    void OnThemeChanged(ThemeSnapshot theme);
}

/// <summary>
/// 插件窗口基础配置（白名单）。仅声明，最终由内核 <c>PluginHostWindow.ApplyPluginConfig</c>
/// 经权限校验后生效——插件不能越权改变外壳行为。
/// </summary>
public sealed class PluginWindowConfig
{
    /// <summary>窗口唯一标识（插件内建议与窗口用途一致，用于权限校验与日志）。</summary>
    public string WindowKey { get; set; } = "";

    /// <summary>是否允许置顶。默认 false；最终是否置顶由权限服务裁决（仅官方白名单插件可置顶）。</summary>
    public bool AllowTopmost { get; set; }

    /// <summary>是否在任务栏显示。壳面窗口默认 false。</summary>
    public bool ShowInTaskbar { get; set; }

    /// <summary>允许的缩放模式。默认 CanResize（与内置窗口一致——除 dock 悬浮胶囊外均默认可自由拉伸，
    /// 让插件画布尺寸去适应皮肤而非反之）。插件仍可在 Config 声明 NoResize 拒绝缩放。</summary>
    public ResizeMode AllowResize { get; set; } = ResizeMode.CanResize;

    /// <summary>默认宽度（DIP）。</summary>
    public double DefaultWidth { get; set; } = 400;

    /// <summary>默认高度（DIP）。</summary>
    public double DefaultHeight { get; set; } = 300;
}

/// <summary>皮肤种类：无 / 图片皮肤 / 配色预设皮肤。供 UI 与插件据以分流渲染策略（跨层共享契约）。</summary>
public enum SkinKind
{
    /// <summary>无皮肤（仅半透明色调托盘）。</summary>
    None = 0,
    /// <summary>图片皮肤（用户本地图片作窗口根背景）。</summary>
    Image = 1,
    /// <summary>配色预设皮肤（内置主题包，驱动 WindowTint/Accent/描边，无图）。</summary>
    Preset = 2
}

/// <summary>
/// 主题快照（只读）。由内核在 <c>IAppearanceService.Changed</c> 时构造，仅给插件查看，
/// 不暴露服务实例与修改入口。
/// </summary>
public sealed class ThemeSnapshot
{
    /// <summary>是否暗色系（亮色模式为 false；暗色/无色为 true）。</summary>
    public bool IsDarkMode { get; init; }

    /// <summary>全局字号缩放（1=基准）。</summary>
    public double FontScale { get; init; }

    /// <summary>当前前景文字色（对应令牌 ThemeForeground）。</summary>
    public Color ForegroundColor { get; init; }

    /// <summary>当前卡片描边色（对应令牌 CardBorderBrush 的主色）。</summary>
    public Color CardBorderColor { get; init; }

    /// <summary>当前强调色（Accent，如选中/链接）。</summary>
    public Color AccentColor { get; init; }

    /// <summary>当前是否处于皮肤模式（图片或配色预设）。插件据以决定"透出皮肤"还是"中性托盘"。</summary>
    public bool SkinIsActive { get; init; }

    /// <summary>皮肤背景 Brush（与窗口根背景一致：皮肤图或色调托盘）。无皮肤时为 null，
    /// 插件自定义绘制可引用此 Brush 做协调（如面板透出同一张皮肤图）。</summary>
    public System.Windows.Media.Brush? SkinBackgroundBrush { get; init; }

    /// <summary>皮肤主色（配色皮肤=预设 Accent；否则=全局 Accent）。插件自定义绘制取色用。</summary>
    public Color SkinAccentColor { get; init; }
}
