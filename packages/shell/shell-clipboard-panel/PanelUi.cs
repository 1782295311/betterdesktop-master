// BetterDesktop.Clipboard.Panel — UI 原语工厂
//
// 【为什么需要它 · 2026-09-12 UI 收敛】面板经历多轮功能追加，每一轮都"就地加一块 UI"：
//   · 5 个状态横幅逐字复制同一段视觉代码（ControlBackground + BorderStrokeAccent + CornerRadius 6 + Padding 10,4）；
//   · 其中"中性多选操作条"被套上了警告色 —— 复制的代价不是多打几行字，而是**语义会在复制中漂移**；
//   · 图标按钮的尺寸/可访问名各写各的，出现 28×28 与 26×26 两种、且全部没有可读名称。
// 本类把这些原语收敛到唯一实现：状态卡片（图标+文字+颜色三通道）、动作按钮、图标按钮。
// 所有尺寸走 PanelTheme.Scale，禁止就地写数值。

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 状态卡片的语义类型。
/// 【纪律】颜色只是**第三通道** —— 每张卡片都必须同时有图标与文字（技能规则 "Color Only"：不得仅用颜色传达信息）。
/// </summary>
internal enum StatusKind
{
    /// <summary>中性信息（例如多选操作）。**不得使用警告色** —— 这正是收敛前多选条被误套警告皮的地方。</summary>
    Info,

    /// <summary>需要注意（存储将满、引擎重连中）。</summary>
    Warning,

    /// <summary>已处于异常态（引擎断开、操作失败）。</summary>
    Danger,

    /// <summary>承载会话与动作（按序粘贴进行中）。</summary>
    Action,
}

/// <summary>面板 UI 原语工厂（唯一实现；调用方只给语义，不给视觉数值）。</summary>
internal static class PanelUi
{
    /// <summary>现代化图标字体（Win10/11 内置）；面板图标统一用它，不再用 emoji 形状。</summary>
    internal static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");

    /// <summary>
    /// 等宽字体（代码条目预览）。
    /// 【为什么集中】此前 RecentStrip 就地写 <c>new FontFamily("Consolas")</c> —— 字体是视觉决策，
    /// 与字号/圆角一样必须只有一个来源（技能规则：禁止就地写视觉数值）。
    /// </summary>
    internal static readonly FontFamily MonoFont = new FontFamily("Consolas, Cascadia Mono, monospace");

    /// <summary>
    /// 统一的过渡缓动（技能规则：动效须有 150-300ms 过渡，且运动要"传达意义"而非装饰）。
    /// 用 EaseOut = 快起慢收，符合"界面立刻响应、随后安定"的感知。
    /// </summary>
    internal static IEasingFunction MotionEase()
        => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>表面底色（令牌 ControlBackground 的色值；按钮/输入框静止态）。</summary>
    internal static Color SurfaceRest
        => ThemeBrushes.TryGetColor("ControlBackground") ?? Color.FromRgb(0x2C, 0x2C, 0x2E);

    /// <summary>表面悬停色（令牌 ControlBackgroundHover）。</summary>
    internal static Color SurfaceHover
        => ThemeBrushes.TryGetColor("ControlBackgroundHover") ?? Color.FromRgb(0x48, 0x48, 0x4A);

    /// <summary>
    /// 状态卡片：`[图标] 文案 … [动作按钮…]`。
    /// 同一时刻只应有一张处于可见（由 PanelMainWindow 的状态槽统一裁决优先级）。
    /// </summary>
    internal static Border CreateStatusCard(
        StatusKind kind,
        string icon,
        out TextBlock text,
        params (string Label, RoutedEventHandler Click)[] actions)
    {
        var card = new Border
        {
            Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS, EntryTheme.Scale.GutterX, 0),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusL),
            Padding = new Thickness(EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderStrokeAccent");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(CreateStatusIcon(icon, kind));

        text = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextElement.ForegroundProperty, StatusForegroundKey(kind));
        row.Children.Add(text);

        foreach (var (label, click) in actions)
        {
            row.Children.Add(CreateActionButton(label, click));
        }

        card.Child = row;
        return card;
    }

    /// <summary>状态图标（与文案同色；颜色只是补充通道，不承担唯一语义）。</summary>
    internal static TextBlock CreateStatusIcon(string icon, StatusKind kind)
    {
        var glyph = new TextBlock
        {
            Text = icon,
            FontSize = EntryTheme.Scale.FontBody,
            FontFamily = IconFont,
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextElement.ForegroundProperty, StatusForegroundKey(kind));
        return glyph;
    }

    /// <summary>状态色令牌：只有 Warning / Danger 用警示色，Info / Action 用常规前景（中性操作不该看起来像报警）。</summary>
    private static string StatusForegroundKey(StatusKind kind) => kind switch
    {
        StatusKind.Warning => "StatusWarning",
        StatusKind.Danger => "StatusDanger",
        _ => "ThemeForeground",
    };

    /// <summary>
    /// 动作按钮（状态卡片内 / 底部栏 / 多选条）：≥32px 点击目标 + 胶囊外形 + 过渡反馈 + 可访问名。
    /// <para>
    /// 【2026-09-15 视觉收敛】此前它用 WPF 默认 Button 模板（方角 + 系统灰底 + 直角描边），
    /// 而图标按钮是无边框圆形淡底 —— 同一个页脚里"＋ 表情包"是方盒子、旁边五个图标是圆底，
    /// 看起来像两套产品。现在两者共用同一套表面语言：胶囊外形、同一底色、同一过渡时长。
    /// </para>
    /// </summary>
    internal static Button CreateActionButton(string label, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = EntryTheme.Scale.FontSmall,
            Padding = new Thickness(EntryTheme.Scale.SpaceM, 0, EntryTheme.Scale.SpaceM, 0),
            MinHeight = EntryTheme.Scale.TapTarget,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(SurfaceRest),
            Template = BuildSurfaceButtonTemplate(EntryTheme.Scale.RadiusPill, SurfaceRest, SurfaceHover, true),
        };
        btn.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        AutomationProperties.SetName(btn, label); // 图标/短标签也必须可读（技能 High 规则）
        btn.Click += click;
        return btn;
    }

    /// <summary>
    /// 通用「表面按钮」模板：胶囊/圆角 + **逐实例**背景画刷（可动画）+ hover 颜色过渡 + 焦点环。
    /// <para>
    /// 【为什么必须逐实例画刷】过渡靠给 <c>Bd.Background.Color</c> 打 ColorAnimation —— 若背景是
    /// 应用级共享令牌画刷（SetResourceReference），动画会改到**所有**按钮、且令牌被永久改写。
    /// 故按钮自带一份 <see cref="SolidColorBrush"/>（面板 v1 只在启动时读一次主题，静态色安全）。
    /// </para>
    /// </summary>
    private static ControlTemplate BuildSurfaceButtonTemplate(
        double radius, Color rest, Color hover, bool showBorder)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(showBorder ? 1 : 0));
        border.SetValue(Border.BorderBrushProperty, ThemeBrushes.Get("BorderStrokeSubtle"));
        // 【必须显式转发 Padding · 2026-09-15】自定义 ControlTemplate 不会自动应用 Button.Padding
        //（WPF 自带模板靠 `Margin="{TemplateBinding Padding}"` 手动转发）——不转发则 Padding 被静默丢弃，
        // 按钮文字左右零留白。截图入口面先踩到这个坑，此处同步修正。
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        // hover：颜色淡入（150ms EaseOut），而非瞬间切换
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.EnterActions.Add(ColorFade("Bd", hover));
        hoverTrigger.ExitActions.Add(ColorFade("Bd", rest));
        template.Triggers.Add(hoverTrigger);

        // 焦点环：键盘用户必须看得见焦点（技能 Critical 规则；此前按钮完全没有焦点视觉）
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty,
            ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong))
        { TargetName = "Bd" });
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)) { TargetName = "Bd" });
        template.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>给模板内元素的 <c>Background.Color</c> 生成一段颜色过渡（供 Enter/ExitActions 复用）。</summary>
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

    /// <summary>
    /// 图标按钮：32×32（不小于标准点击目标）。
    /// **可访问名取 ToolTip 文案** —— 收敛前全目录 0 处可访问名，屏幕阅读器只会念出 emoji glyph。
    /// </summary>
    internal static Button CreateIconButton(string glyph, string tooltip, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = EntryTheme.Scale.TapTarget,
            Height = EntryTheme.Scale.TapTarget,
            FontSize = EntryTheme.Scale.FontBody,
            FontFamily = IconFont,
            // 自带画刷（可动画）：hover 由**透明**淡入强调色淡底，与动作按钮同一套过渡时长
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template = BuildIconButtonTemplate(),
        };
        btn.SetResourceReference(Control.ForegroundProperty, "ThemeMutedForeground");
        AutomationProperties.SetName(btn, tooltip);
        btn.Click += click;
        return btn;
    }

    /// <summary>
    /// 图标按钮模板：圆形淡底 hover（颜色淡入 150ms）/ 按下加深 / 焦点环 / 禁用降透明度。
    /// 圆角改为 <see cref="EntryTheme.Scale.RadiusPill"/> —— 与动作按钮同为胶囊，页脚不再"方一行圆一行"。
    /// </summary>
    private static ControlTemplate BuildIconButtonTemplate()
    {
        var accent = ThemeBrushes.TryGetColor("SkinAccentFromSkin") ?? SurfaceRest;
        var hoverFill = EntryTheme.Blend(SurfaceRest, accent, EntryTheme.Scale.AccentSubtle);

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusPill));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.EnterActions.Add(ColorFade("Bd", hoverFill));
        hover.ExitActions.Add(ColorFade("Bd", Colors.Transparent));
        template.Triggers.Add(hover);

        // 焦点环：Tab 到按钮时可见（此前只有 hover 反馈，纯键盘用户看不到自己在哪）
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty,
            ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong))
        { TargetName = "Bd" });
        template.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>给已存在的控件补可访问名（列表项等非按钮控件用）。</summary>
    internal static void Name(FrameworkElement element, string name) => AutomationProperties.SetName(element, name);

    /// <summary>语义强调色底（禁止在 UI 代码里再写 AccentTint 的裸小数）。</summary>
    internal static Brush AccentFill(bool selected)
        => selected ? ThemeBrushes.AccentTint(EntryTheme.Scale.AccentMedium) : Brushes.Transparent;

    /// <summary>
    /// 人类可读的文件大小（B / KB / MB / GB）。
    /// <para>
    /// 【为什么放在这里 · 2026-09-13】条目行与图片预览都要显示大小 —— 若无单一实现，
    /// 两处会各自写一套格式（小数点位数/单位阈值不一致，"看起来不像一个产品"）。
    /// </para>
    /// <para>≤0 返回空串：**不显示**比显示"0 B"更诚实（旧数据 size_bytes 可能为 0 = 未统计）。</para>
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:0.#} KB";
        }

        double mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:0.#} MB" : $"{mb / 1024.0:0.##} GB";
    }
}
