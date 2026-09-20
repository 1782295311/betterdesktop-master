// BetterDesktop.Shell.MenuBar — 设置分区「菜单栏」（问题 7：系统功能归设置管）
//
// 【职责边界】2026-08-30 用户明确：
//   菜单栏的「+」（扩展中心）只负责**外部扩展功能插件**的启用/停用；
//   **菜单栏系统功能**（系统托盘/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
//   统一搬到「设置 → 菜单栏」管理。
// 因此本分区遍历 ExtensionCatalog.SystemFeatures，每项一个开关：写 extensions.<id>.enabled
// 并即时把显隐应用到运行状态条（MenuBarStatusStrip.SetComponentVisible）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.MenuBar.Sections;

/// <summary>
/// 设置左侧导航栏的「菜单栏」分区：管理系统功能的显隐（即时生效）。
/// </summary>
internal sealed class MenuBarSection : ISettingsSection
{
    /// <summary>把开关结果应用到运行中的状态条；为 null 时（菜单栏未加载）仅持久化。</summary>
    private readonly Action<MenuBarStatusButtonId, bool>? _applyVisibility;

    /// <summary>切换「系统托盘隐藏重复系统图标」；为 null 时仅持久化。</summary>
    private readonly Action<bool>? _setTrayHideSystemIcons;

    public MenuBarSection(
        Action<MenuBarStatusButtonId, bool>? applyVisibility = null,
        Action<bool>? setTrayHideSystemIcons = null)
    {
        _applyVisibility = applyVisibility;
        _setTrayHideSystemIcons = setTrayHideSystemIcons;
    }

    public string Title => "菜单栏";

    public string? IconKey => null;

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 空闲自动隐藏 ----
        var idleCard = GroupCard(tokens);
        var idleBody = CardBody(idleCard);
        idleBody.Children.Add(TitleBlock("空闲自动隐藏", tokens));
        idleBody.Children.Add(HintBlock(
            "无鼠标/键盘操作达到该分钟后，自动隐藏 Dock 与顶部菜单栏；期间任何操作立即恢复显示。阈值同时作用于两者。",
            tokens));
        idleBody.Children.Add(SliderRow("空闲阈值（分钟）", settings, tokens,
            () => Clamp(settings.Get("shell.idleHideMinutes", 20d), 1, 240),
            v => settings.Set("shell.idleHideMinutes", v), 1, 240));
        panel.Children.Add(idleCard);

        // ---- 系统功能显隐 ----
        var featCard = GroupCard(tokens);
        var featBody = CardBody(featCard);
        featBody.Children.Add(TitleBlock("系统功能", tokens));
        featBody.Children.Add(HintBlock(
            "控制顶部菜单栏右侧各项的显示与隐藏，切换后即时生效。", tokens));

        // 系统托盘去重：菜单栏已有专用的音量/网络/电池/通知按钮，托盘区再显示一遍是冗余。
        featBody.Children.Add(ToggleRow("系统托盘隐藏重复的系统图标（音量/网络/电源/操作中心）",
            settings, tokens, "menubar.tray.hideSystemIcons", true,
            on => _setTrayHideSystemIcons?.Invoke(on)));

        // 两列网格：15 项单列会拉得很长，两列对齐更接近系统设置观感。
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var index = 0;
        foreach (var feature in ExtensionCatalog.SystemFeatures)
        {
            // 默认值取自目录的 DefaultEnabled（此前写死 true）：写死会让设置页把"帧率"显示成开启，
            // 而实际默认是关（帧率组件是唯一订阅每帧渲染的地方，默认关是电源红线），两边必须同一真相源。
            var row = ToggleRow(feature.Name, settings, tokens, feature.SettingsKey, feature.DefaultEnabled,
                on =>
                {
                    // 先落盘，再应用到运行中的状态条（顺序不能反：应用失败也不该丢用户意图）
                    if (feature.MenuBarButton is { } id)
                    {
                        _applyVisibility?.Invoke(id, on);
                    }
                });

            var column = index % 2;
            // 行定义只在第一列时新增，否则每项都加一行会多出 7 个空行。
            if (column == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetColumn(row, column);
            Grid.SetRow(row, index / 2);
            grid.Children.Add(row);
            index++;
        }

        featBody.Children.Add(grid);
        panel.Children.Add(featCard);

        // ---- 外部扩展 ----
        var extCard = GroupCard(tokens);
        var extBody = CardBody(extCard);
        extBody.Children.Add(TitleBlock("外部扩展", tokens));
        extBody.Children.Add(HintBlock(
            "外部扩展功能插件（快速笔记、天气、台前调度、截屏、动态桌面等）请在菜单栏最右侧的「+」扩展中心中启用或停用。",
            tokens));
        panel.Children.Add(extCard);

        return panel;
    }

    // ===== UI helper（与 shell-settings 的 LeftDockSection 同风格，Section 自包含） =====

    private static Border GroupCard(IThemeTokens tokens) => SettingsUi.CreateCard();

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };

    /// <summary>开关行：写 settings 的 bool 键，并可选回调（用于即时显隐）。</summary>
    private static UIElement ToggleRow(string label, ISettingsService settings, IThemeTokens tokens,
        string key, bool def, Action<bool>? onChanged = null)
    {
        var box = WithStyle(new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 6, 12, 6),
            FontSize = 13,
            // Get<T>(key, default) 的 T? 只是可空注解，T=bool 时返回 bool（不是 bool?）。
            IsChecked = settings.Get(key, def)
        }, "MacToggle", tokens);

        box.Checked += (_, _) => { settings.Set(key, true); onChanged?.Invoke(true); };
        box.Unchecked += (_, _) => { settings.Set(key, false); onChanged?.Invoke(false); };
        return box;
    }

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style s)
        {
            element.Style = s;
        }
        else if (element is Control c)
        {
            c.Foreground = tokens.Foreground;
        }
        return element;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    /// <summary>数值滑块行（120ms 落盘防抖，避免拖动时高频 Set 轰炸订阅方）。</summary>
    private static UIElement SliderRow(string label, ISettingsService settings, IThemeTokens tokens,
        Func<double> getVal, Action<double> setVal, double min, double max)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground
        };
        DockPanel.SetDock(text, System.Windows.Controls.Dock.Left);

        var valueText = new TextBlock
        {
            Text = getVal().ToString("0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Foreground = tokens.MutedForeground,
            MinWidth = 30
        };

        var slider = WithStyle(new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = getVal(),
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center
        }, "MacSlider", tokens);

        double pending = double.NaN;
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            if (!double.IsNaN(pending))
            {
                setVal(pending);
                pending = double.NaN;
            }
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue, 0);
            valueText.Text = v.ToString("0");
            pending = v;
            debounce.Stop();
            debounce.Start();
        };

        var right = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(valueText, System.Windows.Controls.Dock.Right);
        right.Children.Add(valueText);
        DockPanel.SetDock(slider, System.Windows.Controls.Dock.Left);
        right.Children.Add(slider);

        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
        row.Children.Add(text);
        row.Children.Add(right);
        return row;
    }
}
