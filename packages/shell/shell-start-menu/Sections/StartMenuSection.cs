// BetterDesktop.Shell.StartMenu — StartMenuSection 控制界面（自绘）
// 设置左侧导航「开始菜单」分区（方向：聚焦样式与背景，不做功能堆叠）。
// 分组：状态（启用/Win 键）、样式（Win7/Win10/Win11）、背景（跟随主题统一驱动）、外观（宽度）。
// 样式/背景改动经 ISettingsService.Changed 事件由 StartMenuService 热更新（无需重启）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Sections;

/// <summary>
/// 开始菜单控制分区（Win7 / Win10 / Win11 三套样式切换 + 背景跟随主题）。
/// </summary>
public sealed class StartMenuSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "开始菜单";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var svc = StartMenuServiceBridge.TryGet();
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // 页面标题统一由窗口标题栏承载，此处不再重复渲染大标题。
        panel.Children.Add(new TextBlock
        {
            Text = "纯自绘开始菜单，可选 Win7 / Win10 / Win11 三套样式，背景与配色完全跟随「设置 → 外观」主题；入口在 Dock 左端开始图标，也可按 Win 键唤出。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        if (svc is null)
        {
            panel.Children.Add(Desc("开始菜单服务尚未就绪（插件未加载），请稍后重新打开本页。", tokens));
            return panel;
        }

        // ---- 状态 + 启用 ----
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("状态", tokens));
        CardBody(card).Children.Add(Desc(StatusText(svc), tokens));
        var enableToggle = WithStyle(new CheckBox
        {
            Content = "启用开始菜单（接管 Win 键/Dock 左端开始图标）",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13
        }, "MacToggle", tokens);
        enableToggle.IsChecked = svc.IsActive;
        enableToggle.Checked += (_, _) => svc.Enable();
        enableToggle.Unchecked += (_, _) => svc.Disable();
        CardBody(card).Children.Add(enableToggle);
        CardBody(card).Children.Add(ToggleRow(settings, "startmenu.win-key", true, "Windows 键打开开始菜单", tokens));
        panel.Children.Add(card);

        // ---- 样式（核心）：Win7 / Win10 / Win11 ----
        var styleCard = GroupCard(tokens);
        CardBody(styleCard).Children.Add(TitleBlock("样式", tokens));
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        styleRow.Children.Add(new TextBlock
        {
            Text = "开始菜单样式",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 12, 0)
        });
        var styleCombo = WithStyle(new ComboBox
        {
            Width = 180,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            SelectedIndex = settings.Get("startmenu.style", "win11") switch
            {
                "win7" => 0,
                "win10" => 1,
                _ => 2
            }
        }, "MacCombo", tokens);
        styleCombo.Items.Add("Windows 7（经典两栏）");
        styleCombo.Items.Add("Windows 10（磁贴网格）");
        styleCombo.Items.Add("Windows 11（圆角经典）");
        styleCombo.SelectionChanged += (_, e) =>
        {
            var value = styleCombo.SelectedIndex switch
            {
                0 => "win7",
                1 => "win10",
                _ => "win11"
            };
            settings.Set("startmenu.style", value);
        };
        styleRow.Children.Add(styleCombo);
        CardBody(styleCard).Children.Add(styleRow);
        CardBody(styleCard).Children.Add(Desc(
            "· Win7：左侧程序树 + 右侧功能列表（文档/图片/音乐/计算机/控制面板 + 关机）\n" +
            "· Win10：顶部搜索 + 应用磁贴网格 + 右侧最近/位置/电源\n" +
            "· Win11：顶部搜索 + 程序树 + 右侧最近/位置/电源（圆角卡片）",
            tokens));
        panel.Children.Add(styleCard);

        // ---- 背景（跟随主题）----
        var bgCard = GroupCard(tokens);
        CardBody(bgCard).Children.Add(TitleBlock("背景", tokens));
        CardBody(bgCard).Children.Add(Desc(
            "开始菜单背景与全局主题统一驱动（「设置 → 外观」）：\n" +
            "· 透明 / 模糊（Blur）→ 菜单透出桌面毛玻璃\n" +
            "· 亚克力（Acrylic）→ 菜单半透明高模糊\n" +
            "· 不透明 / 清晰 → 菜单使用主题背景色\n" +
            "切换主题风格后开始菜单背景自动跟随，无需单独设置。",
            tokens));
        panel.Children.Add(bgCard);

        // ---- 外观 ----
        var look = GroupCard(tokens);
        CardBody(look).Children.Add(TitleBlock("外观", tokens));
        CardBody(look).Children.Add(SliderRow(
            settings, "startmenu.width", 460, 360, 640, 20,
            "菜单宽度", " px", tokens));
        CardBody(look).Children.Add(Desc("字号缩放跟随「设置 → 外观」页的全局字号。", tokens));
        panel.Children.Add(look);

        // ---- 使用说明 ----
        var note = GroupCard(tokens);
        CardBody(note).Children.Add(TitleBlock("使用", tokens));
        CardBody(note).Children.Add(Desc("· 按 Win 键（或点击 Dock 左端开始图标）：打开/关闭开始菜单（Win+组合键不受影响）", tokens));
        CardBody(note).Children.Add(Desc("· Esc 或点击菜单外区域：关闭", tokens));
        CardBody(note).Children.Add(Desc("· 本页改动即时生效（无需重启），设置持久化到 settings.json", tokens));
        panel.Children.Add(note);

        return panel;
    }

    private static UIElement SliderRow(
        ISettingsService settings,
        string key,
        double defaultValue,
        double min,
        double max,
        double step,
        string label,
        string unit,
        IThemeTokens tokens)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 12, 0)
        });
        var valueText = new TextBlock
        {
            Text = $"{(int)settings.Get(key, defaultValue)}{unit}",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.MutedForeground,
            MinWidth = 64
        };
        var slider = WithStyle(new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            Value = Math.Clamp(settings.Get(key, defaultValue), min, max)
        }, "MacSlider", tokens);
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            valueText.Text = $"{v}{unit}";
            settings.Set(key, (double)v);
        };
        row.Children.Add(slider);
        row.Children.Add(valueText);
        return row;
    }

    private static UIElement ToggleRow(ISettingsService settings, string key, bool defaultValue, string label, IThemeTokens tokens)
    {
        var toggle = WithStyle(new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13
        }, "MacToggle", tokens);
        toggle.IsChecked = settings.Get(key, defaultValue);
        toggle.Checked += (_, _) => settings.Set(key, true);
        toggle.Unchecked += (_, _) => settings.Set(key, false);
        return toggle;
    }

    private static string StatusText(IStartMenuService svc)
    {
        return svc.IsActive
            ? "已启用：Win 键 / Dock 左端开始图标弹出自绘 WPF 开始菜单。"
            : "开始菜单未启用：打开下方开关即接管 Win 键。";
    }

    // ===== 共享 helper（自包含，沿用其他设置分区风格） =====
    private static UIElement Desc(string text, IThemeTokens tokens) => new TextBlock
    {
        Text = text,
        Foreground = tokens.MutedForeground,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Border GroupCard(IThemeTokens tokens) => SettingsUi.CreateCard();

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style style)
        {
            element.Style = style;
        }
        else if (element is Control control)
        {
            control.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
        }

        return element;
    }
}
