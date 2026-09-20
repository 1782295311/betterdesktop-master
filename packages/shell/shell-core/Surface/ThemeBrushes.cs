using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 主题令牌取色帮助器（代码构建 UI / 事件处理器内取色用）。
/// <para>
/// 统一风格基准（设置中心）规定：界面颜色一律走 Application.Resources 令牌，禁止硬编码。
/// 静态元素属性优先用 <see cref="FrameworkElement.SetResourceReference"/>（模式切换自动跟随）；
/// 本帮助器服务于无法绑定的场景：事件处理器内的指令式赋值、OnRender 自绘取色、
/// 需要「令牌色 × 透明度」派生的叠层。
/// </para>
/// <para>
/// 令牌全集见 host/App.xaml（Theme* / Control* / Popup* / SettingsCard* / BorderStroke* /
/// SkinAccentFromSkin / Status*）。调用方只写键名，不写色值。
/// </para>
/// </summary>
public static class ThemeBrushes
{
    /// <summary>按令牌键取画刷；缺失时回退（fallback 为 null 则回退 ThemeForeground）。</summary>
    public static Brush Get(string key, Brush? fallback = null)
    {
        var app = Application.Current;
        if (app?.Resources.Contains(key) == true && app.Resources[key] is Brush b)
        {
            return b;
        }
        return fallback ?? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    }

    /// <summary>按令牌键取纯色；缺失时返回 null（调用方自行决定回退语义）。</summary>
    public static Color? TryGetColor(string key)
    {
        var app = Application.Current;
        if (app?.Resources.Contains(key) == true && app.Resources[key] is SolidColorBrush b)
        {
            return b.Color;
        }
        return null;
    }

    /// <summary>
    /// 令牌色 × 透明度派生叠层（frozen，供指令式赋值/自绘）。
    /// 例：选中行背景 = Tint("SkinAccentFromSkin", 0.23)。
    /// </summary>
    public static Brush Tint(string key, double opacity)
    {
        var color = TryGetColor(key) ?? Color.FromRgb(0xF2, 0xF2, 0xF2);
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>强调色（SkinAccentFromSkin）× 透明度派生。等价 Tint("SkinAccentFromSkin", opacity)。</summary>
    public static Brush AccentTint(double opacity) => Tint("SkinAccentFromSkin", opacity);

    // ===== 语义状态色（Color 形态，供动画/自绘；回退值与 host/App.xaml 令牌初值保持一致） =====

    /// <summary>成功绿（令牌 StatusSuccess；macOS 系统绿回退）。</summary>
    public static Color SuccessColor => TryGetColor("StatusSuccess") ?? Color.FromRgb(0x34, 0xC7, 0x59);

    /// <summary>警告橙（令牌 StatusWarning；macOS 系统橙回退）。</summary>
    public static Color WarningColor => TryGetColor("StatusWarning") ?? Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>危险红（令牌 StatusDanger；macOS 系统红回退）。</summary>
    public static Color DangerColor => TryGetColor("StatusDanger") ?? Color.FromRgb(0xFF, 0x5F, 0x57);
}
