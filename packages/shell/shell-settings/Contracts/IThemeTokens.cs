using System.Windows.Media;

namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 轻量主题令牌：把设置界面（及未来壳表面）的硬编码颜色收口到一处。
/// 本环境不实现深/浅色模式，所有颜色均为跟随统一窗口基类毛玻璃材质的中性协调色，
/// 文字一律为亮色（禁止黑色字体）；ThemeCenter 落地后可由其实现替换，调用方无需改动。
/// </summary>
public interface IThemeTokens
{
    /// <summary>窗口整体背景（如设置窗口底色）。</summary>
    Brush WindowBackground { get; }

    /// <summary>侧栏/面板背景（略深于内容区）。</summary>
    Brush PanelBackground { get; }

    /// <summary>内容区背景。</summary>
    Brush ContentBackground { get; }

    /// <summary>主前景色（文字）。亮色模式转深，其余模式恒亮。</summary>
    Brush Foreground { get; }

    /// <summary>次要前景（描述文字）。</summary>
    Brush MutedForeground { get; }

    /// <summary>分隔线（半透明灰）。</summary>
    Brush Separator { get; }

    /// <summary>强调/选中色（导航选中项背景）。</summary>
    Brush Accent { get; }

    /// <summary>输入控件背景（如 TextBox/搜索框底色），随模式统一。</summary>
    Brush InputBackground { get; }

    /// <summary>输入控件描边。</summary>
    Brush InputBorder { get; }

    /// <summary>卡片描边画刷（由 BorderStrength + BorderStyle 实时推导：Solid=纯色；TopGlow/Diagonal=渐变；Inner=弱纯色，靠内层 Border 实现内描边）。</summary>
    Brush CardBorder { get; }

    /// <summary>当前描边样式（0=Solid，1=TopGlow，2=Diagonal，3=Inner），供卡片据以决定是否内嵌亮线 Border。</summary>
    int CardBorderStyle { get; }

    /// <summary>卡片阴影效果（由 ShadowSize 实时推导；无阴影时返回 null）。套用到 Border.Effect。</summary>
    System.Windows.Media.Effects.Effect? CardShadow { get; }

    // ---- 几何与字号（把壳表面硬编码的圆角/字号收口到令牌，便于全局统一） ----

    /// <summary>面板/菜单圆角（默认 8，与 DWM 系统默认圆角重合）。布局根 Border 用此值，不得再硬编码。</summary>
    double CornerRadius { get; }

    /// <summary>输入类控件字号（搜索框，默认 14）。</summary>
    double FontSizeInput { get; }

    /// <summary>正文/主列表项字号（程序项/结果标题，默认 13）。</summary>
    double FontSizeBody { get; }

    /// <summary>次要/说明文字字号（"无匹配结果"/功能列表，默认 12）。</summary>
    double FontSizeCaption { get; }

    /// <summary>小字号（磁贴名称等密集图文，默认 11）。</summary>
    double FontSizeSmall { get; }
}
