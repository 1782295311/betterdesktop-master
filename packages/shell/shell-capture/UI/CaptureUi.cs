using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// 截图入口面（覆盖层 / 标注编辑器 / OCR 面板 / 贴图）的**共用视觉原语**。
/// <para>
/// 【为什么集中】与剪贴板面板同一套语言：尺寸/字号/圆角/间距/动效一律取自
/// <see cref="EntryTheme.Scale"/>（shell-core，两个 exe 共用同一份数字），
/// 颜色一律取主题令牌（<see cref="ThemeBrushes"/> / App.Resources），
/// 界面代码里**再出现野生色值或野生字号即视为回归**。
/// </para>
/// <para>
/// 两套表面（2026-09-15 定）：
/// ① <b>窗口表面</b>（编辑器 / OCR 面板 —— 普通窗口）：走主题令牌，跟随宿主深浅色；
/// ② <b>HUD 表面</b>（选区信息条 / 放大镜标签 / 覆盖层功能栏 / 贴图穿透面板 —— 浮在截图或桌面之上）：
///   **恒为深色**。它叠在任意内容（可能是白底文档、也可能是深色 IDE）之上，
///   跟随浅色主题反而会在亮图上失去对比度 —— 系统截图工具的 HUD 也一律是深色。
///   只有强调色（按钮/选区）跟随主题，保证"是我们家的工具"。
/// </para>
/// </summary>
internal static class CaptureUi
{
    /// <summary>现代化图标字体（Win10/11 内置）。禁止用 emoji 当图标：彩色字形在字体回退下样子不可控。</summary>
    public static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    /// <summary>等宽字体（像素尺寸/坐标/颜色值等"精确数据"用它，读起来更稳）。</summary>
    public static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");

    /// <summary>统一过渡缓动（快起慢收：界面立刻响应、随后安定）。</summary>
    public static IEasingFunction MotionEase()
        => new CubicEase { EasingMode = EasingMode.EaseOut };

    // ---- 窗口表面（跟随主题） ----
    public static Color SurfaceRest
        => ThemeBrushes.TryGetColor("ControlBackground") ?? Color.FromRgb(0x2C, 0x2C, 0x2E);

    public static Color SurfaceHover
        => ThemeBrushes.TryGetColor("ControlBackgroundHover") ?? Color.FromRgb(0x48, 0x48, 0x4A);

    // ---- HUD 表面（恒深色，见类注释 ②） ----
    // 【为什么**不透明** · 2026-09-15 用户实测】原为 0xEB（92%）：胶囊与其中的按钮同色，
    // 但按钮再叠一层 92% 后，把"已被胶囊挡掉 8% 的桌面"又挡掉 8% —— 结果是每个按钮在胶囊上
    // 显出一块略深的圆角矩形（叠加假象，不是设计意图）。改成不透明后按钮静止态与胶囊**完全同色**，
    // 只在 hover/选中时才浮现底色（这也是系统级 HUD 工具条的观感）。
    public static readonly Color HudSurface = Color.FromRgb(0x1C, 0x1C, 0x1E);
    public static readonly Color HudSurfaceHover = Color.FromRgb(0x35, 0x35, 0x38);
    public static readonly Color HudForeground = Color.FromRgb(0xF2, 0xF2, 0xF2);
    public static readonly Color HudMuted = Color.FromRgb(0x9E, 0x9E, 0xA4);

    /// <summary>
    /// 一排按钮之间的标准间距。
    /// <para>
    /// 【口径 · 2026-09-15 用户实测两轮】8px 仍显挤 —— 定稿 12px（SpaceM）。
    /// 用法：相邻按钮各带 <c>ButtonGap/2</c> 外边距（=> 两两之间 12），容器再给 <c>ButtonGap/2</c> 内缩
    /// （=> 外缘到第一个按钮也是 12，左右对称）。**不要再就地写 4/6/8 这类零散间距**。
    /// </para>
    /// </summary>
    public static double ButtonGap => EntryTheme.Scale.SpaceM;

    /// <summary>按钮表面风格。</summary>
    public enum Face
    {
        /// <summary>普通按钮（跟随主题的表面色）。</summary>
        Surface,

        /// <summary>主按钮（强调色实底）。</summary>
        Primary,

        /// <summary>HUD 普通按钮（恒深色）。</summary>
        Hud,

        /// <summary>HUD 主按钮（强调色实底，用于"完成"）。</summary>
        HudPrimary,

        /// <summary>
        /// 选中态按钮（强调色淡底 + 正文前景）。
        /// 用于"当前生效的工具/颜色/状态"这类**单选组** —— 与 <see cref="Primary"/> 的区别是
        /// 实底主按钮表示"执行"，淡底表示"当前选中"，两者语义不能混（一排工具按钮全用实底会喧宾夺主）。
        /// </summary>
        Selected,
    }

    /// <summary>
    /// 强调色实底上的文字色：按亮度选黑/白，而不是一律写死白 ——
    /// 用户把强调色改成亮黄/亮绿时，白字会糊在底上（对比度不足）。
    /// </summary>
    private static Color OnAccent()
    {
        var a = EntryTheme.AccentColor;
        // 相对亮度（sRGB 近似）：> 0.6 视为亮底 → 用近黑色文字
        double l = (0.2126 * a.R + 0.7152 * a.G + 0.0722 * a.B) / 255.0;
        return l > 0.6 ? Color.FromRgb(0x14, 0x14, 0x16) : Colors.White;
    }

    /// <summary>
    /// 胶囊按钮（≥32px 点击目标 + hover 颜色过渡 + 键盘焦点环 + 可访问名）。
    /// <para>
    /// 过渡靠给 <c>Bd.Background.Color</c> 打 ColorAnimation —— 故画刷必须**逐按钮自带**：
    /// 用共享令牌画刷做动画会改到所有按钮、并永久改写令牌。
    /// </para>
    /// </summary>
    public static Button PillButton(
        string label,
        Action onClick,
        Face face = Face.Surface,
        double fontSize = EntryTheme.Scale.FontSmall,
        bool compact = false)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = fontSize,
            // 内部左右留白：16px（12px 时 2 字标签像被"框住"，用户实测反馈"胶囊内留白不够"）
            Padding = new Thickness(
                compact ? EntryTheme.Scale.SpaceM : EntryTheme.Scale.SpaceL, 0,
                compact ? EntryTheme.Scale.SpaceM : EntryTheme.Scale.SpaceL, 0),
            MinHeight = EntryTheme.Scale.TapTarget,
            Margin = new Thickness(ButtonGap / 2, 0, ButtonGap / 2, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyFace(btn, face);
        AutomationProperties.SetName(btn, label);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// 给已存在的胶囊按钮**重挂表面风格**（运行时切换选中态用）。
    /// <para>
    /// 【为什么必须走它】单选组（工具/颜色）切换选中态时，若"重建按钮"会丢焦点、丢无障碍名、
    /// 并让已按下按钮的键盘焦点跳走 —— 只换表面（背景/前景/模板）才是对的。
    /// </para>
    /// </summary>
    public static void ApplyFace(Button btn, Face face)
    {
        var p = Palette(face);
        btn.Background = new SolidColorBrush(p.Rest);
        btn.Foreground = new SolidColorBrush(p.Fore);
        btn.Template = BuildPillTemplate(p.Rest, p.Hover, p.Pressed, p.Fore, p.ShowBorder);
    }

    /// <summary>图标 + 文字胶囊按钮（覆盖层功能栏 / 编辑器工具条用）。</summary>
    public static Button IconTextButton(
        string glyph,
        string label,
        Action onClick,
        Face face = Face.Hud,
        double fontSize = EntryTheme.Scale.FontSmall)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = fontSize + 1,
            VerticalAlignment = VerticalAlignment.Center,
            // 图标与文字之间：留 8px（原 6px 在 12px 字号下两个字形几乎贴在一起）
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = new Button
        {
            Content = content,
            // 按钮内部左右留白：16px（原 12px 让"图标+文字"这个整体贴着胶囊边，看着发挤）
            Padding = new Thickness(EntryTheme.Scale.SpaceL, 0, EntryTheme.Scale.SpaceL, 0),
            MinHeight = EntryTheme.Scale.TapTarget,
            // 相邻按钮之间留 ButtonGap（见 Capsule 的配套外内缩）
            Margin = new Thickness(ButtonGap / 2, 0, ButtonGap / 2, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyFace(btn, face);
        AutomationProperties.SetName(btn, label);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>纯图标胶囊按钮（32×32，撤销/重做/复制这类高频小动作）。</summary>
    public static Button IconButton(
        string glyph,
        string tooltip,
        Action onClick,
        Face face = Face.Surface)
    {
        var btn = new Button
        {
            Content = glyph,
            FontFamily = IconFont,
            FontSize = EntryTheme.Scale.FontBody,
            Width = EntryTheme.Scale.TapTarget,
            Height = EntryTheme.Scale.TapTarget,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            // 相邻图标按钮之间留 ButtonGap（原实现完全贴合，一排图标像一整块）
            Margin = new Thickness(ButtonGap / 2, 0, ButtonGap / 2, 0),
        };
        ApplyFace(btn, face);
        AutomationProperties.SetName(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// HUD 胶囊容器：把一组按钮/文字放进同一块圆角深色底（浮层上的"工具条"）。
    /// <para>
    /// 【留白口径 · 2026-09-15 用户两轮实测】按钮自带左右各 <c>ButtonGap/2</c> 外边距，
    /// 容器再给 <c>ButtonGap/2</c> 内缩 → <b>外缘 = ButtonGap(12)、相邻按钮之间 = ButtonGap(12)</b>，
    /// 左右完全对称（首版容器 4px + 按钮单侧 4px → 外缘左 4 右 8，既挤又不对称）。
    /// 纵向同样给 ButtonGap：胶囊高 = 32 + 24 = 56px，横向纵向一致才不"扁"。
    /// </para>
    /// <para>
    /// 圆角 = <see cref="EntryTheme.Scale.RadiusPill"/> + 纵向内缩 → 两端仍是**半圆**（真胶囊）：
    /// 内缩变大后若圆角不变，56px 高的容器配 16px 圆角就成了"圆角矩形"，形状语言不统一。
    /// </para>
    /// </summary>
    public static Border Capsule(UIElement child)
    {
        var padY = ButtonGap;              // 纵向与横向同取 ButtonGap：56px 高的胶囊才不显扁
        var padX = ButtonGap / 2;          // 与按钮各半的外边距相加 = ButtonGap
        return new Border
        {
            Background = new SolidColorBrush(HudSurface),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusPill + padY),
            Padding = new Thickness(padX, padY, padX, padY),
            Child = child,
        };
    }

    /// <summary>表面文字（HUD 或主题前景；两者语义不同，绝不混用）。</summary>
    public static TextBlock Label(string text, bool hud, double fontSize = EntryTheme.Scale.FontSmall)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.Foreground = hud ? new SolidColorBrush(HudForeground) : ThemeBrushes.Get("ThemeForeground");
        return tb;
    }

    /// <summary>次要文字（HUD 或主题的弱化前景 —— 用于"识别中…"、提示行等）。</summary>
    public static TextBlock Muted(string text, bool hud, double fontSize = EntryTheme.Scale.FontSmall)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.Foreground = hud ? new SolidColorBrush(HudMuted) : ThemeBrushes.Get("ThemeMutedForeground");
        return tb;
    }

    /// <summary>HUD 上的微型分隔线（浅色低透明，不抢视觉）。</summary>
    public static Border HudSeparator() => new()
    {
        Width = 1,
        // 两侧各 ButtonGap/2 + 按钮自身 ButtonGap/2 = 分隔线左右各 ButtonGap，分组边界一眼可辨
        Margin = new Thickness(ButtonGap / 2, EntryTheme.Scale.SpaceS, ButtonGap / 2, EntryTheme.Scale.SpaceS),
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
    };

    /// <summary>
    /// 通用胶囊模板：圆角 + 逐实例背景画刷（可动画）+ hover 150ms 淡入 + 焦点环 + 禁用降透明。
    /// </summary>
    private static ControlTemplate BuildPillTemplate(
        Color rest, Color hover, Color pressed, Color fore, bool showBorder)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusPill));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(showBorder ? 1 : 0));
        border.SetValue(Border.BorderBrushProperty,
            showBorder ? ThemeBrushes.Get("BorderStrokeSubtle") : Brushes.Transparent);
        // 【必须显式转发 Padding · 2026-09-15 用户实测"文字贴边、胶囊长度不够"真根因】
        // 自定义 ControlTemplate **不会**自动应用 Button.Padding —— WPF 自带模板是靠
        // `Margin="{TemplateBinding Padding}"` 手动转发才生效的。此前所有胶囊按钮挂的
        // Padding(16,0,16,0) 全部被静默丢弃 → 文字左右各 0px，看着又挤又怪。
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        // hover：颜色淡入（150ms EaseOut）而非瞬间切换
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.EnterActions.Add(ColorFade("Bd", hover));
        hoverTrigger.ExitActions.Add(ColorFade("Bd", rest));
        template.Triggers.Add(hoverTrigger);

        // 按下：即时加深（瞬时状态，不做过渡 —— 过渡会让"按下"感觉发飘）
        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(pressed)) { TargetName = "Bd" });
        template.Triggers.Add(pressedTrigger);

        // 键盘焦点：必须看得见（纯键盘用户否则不知道自己在哪）
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(fore)) { TargetName = "Bd" });
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)) { TargetName = "Bd" });
        template.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);

        return template;
    }

    private static BeginStoryboard ColorFade(string targetName, Color to)
    {
        var animation = new ColorAnimation(to, TimeSpan.FromMilliseconds(EntryTheme.Scale.MotionFastMs))
        {
            EasingFunction = MotionEase(),
        };
        Storyboard.SetTargetName(animation, targetName);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Background.Color"));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return new BeginStoryboard { Storyboard = storyboard };
    }

    /// <summary>一种表面风格的全部取色（rest / hover / pressed / 文字 / 是否描边）。</summary>
    private readonly record struct FacePalette(
        Color Rest, Color Hover, Color Pressed, Color Fore, bool ShowBorder);

    /// <summary>选中态取色（与剪贴板面板的"选中筛选 chip"同一口径：强调色淡底 + 正文前景）。</summary>
    private static FacePalette SelectedPalette(Color accent)
    {
        var rest = EntryTheme.Blend(SurfaceRest, accent, EntryTheme.Scale.AccentMedium);
        return new FacePalette(
            rest,
            EntryTheme.Blend(rest, Colors.White, 0.10),
            EntryTheme.Blend(rest, Colors.Black, 0.10),
            ThemeBrushes.TryGetColor("ThemeForeground") ?? Color.FromRgb(0xF2, 0xF2, 0xF2),
            false);
    }

    /// <summary>四种表面风格的取色（唯一来源；按钮构造一律经此，禁止就地写色值）。</summary>
    private static FacePalette Palette(Face face)
    {
        var accent = EntryTheme.AccentColor;
        var onAccent = OnAccent();
        return face switch
        {
            Face.Primary => new FacePalette(
                accent,
                EntryTheme.Blend(accent, Colors.White, 0.14),
                EntryTheme.Blend(accent, Colors.Black, 0.14),
                onAccent,
                false),
            Face.Hud => new FacePalette(
                HudSurface,
                HudSurfaceHover,
                EntryTheme.Blend(HudSurfaceHover, Colors.White, 0.08),
                HudForeground,
                false),
            Face.HudPrimary => new FacePalette(
                accent,
                EntryTheme.Blend(accent, Colors.White, 0.14),
                EntryTheme.Blend(accent, Colors.Black, 0.14),
                onAccent,
                false),
            Face.Selected => SelectedPalette(accent),
            _ => new FacePalette(
                SurfaceRest,
                SurfaceHover,
                ThemeBrushes.TryGetColor("ControlBackgroundPressed") ?? Color.FromRgb(0x34, 0x34, 0x36),
                ThemeBrushes.TryGetColor("ControlForeground") ?? Color.FromRgb(0xF2, 0xF2, 0xF2),
                true),
        };
    }
}
