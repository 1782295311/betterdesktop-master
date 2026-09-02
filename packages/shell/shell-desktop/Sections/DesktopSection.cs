// BetterDesktop.Shell.Desktop — 自绘桌面设置分区（设置中心侧栏独立导航项）
// 由 DesktopPlugin 在 LoadAsync 里经 ISettingsSectionRegistry.Register 自贡献，
// 不修改 shell-settings 的 SettingsPlugin（插件自贡献，保持解耦）。
//
// 设计约定（与 LeftDockSection 同风格）：
//   - UI 自包含：GroupCard/CardBody/TitleBlock/Labeled/SliderRow/ToggleRow/WithStyle helper 内置。
//   - 直接读写 desktop.* 键，不依赖渲染层程序集；渲染层（DesktopWindow/DesktopIconsControl）
//     读取同一批键，故改动即时生效。
//   - 主题取色一律经 IThemeTokens（Foreground/MutedForeground），禁止硬编码颜色。
//
// 覆盖：桌面开关 / 图标（.lnk 后缀、格尺寸、字号）/ 布局避让（Dock、任务栏、菜单栏）
//       / 交互（右键菜单、拖放）/ 系统快捷入口（显示设置、个性化）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Sections;

/// <summary>自绘桌面设置分区（侧栏「桌面」）。</summary>
public sealed class DesktopSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "桌面";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 桌面开关 ----
        var enableCard = GroupCard();
        var enableBody = CardBody(enableCard);
        enableBody.Children.Add(TitleBlock("自绘桌面", tokens));
        enableBody.Children.Add(ToggleRow("启用自绘桌面（关闭则恢复 explorer 原生桌面）", settings, tokens,
            "components.desktop", true));
        enableBody.Children.Add(NoteBlock("开关即时生效，无需重启。", tokens));
        panel.Children.Add(enableCard);

        // ---- 图标 ----
        var iconCard = GroupCard();
        var iconBody = CardBody(iconCard);
        iconBody.Children.Add(TitleBlock("图标", tokens));
        iconBody.Children.Add(ToggleRow("隐藏快捷方式 .lnk 后缀", settings, tokens,
            "desktop.hideLnkExtension", true));
        iconBody.Children.Add(SliderRow("图标大小", settings, tokens,
            () => Clamp(settings.Get("desktop.iconSize", 44d), 24, 96),
            v => settings.Set("desktop.iconSize", v), 24, 96));
        iconBody.Children.Add(SliderRow("名称字号", settings, tokens,
            () => Clamp(settings.Get("desktop.labelFontSize", 11d), 8, 18),
            v => settings.Set("desktop.labelFontSize", v), 8, 18));
        iconBody.Children.Add(NoteBlock("调整后桌面图标网格会即时重排。", tokens));
        panel.Children.Add(iconCard);

        // ---- 网格间距（布局由「图标大小 + 行/列间距」自动推导，不会留空白） ----
        var gridCard = GroupCard();
        var gridBody = CardBody(gridCard);
        gridBody.Children.Add(TitleBlock("网格间距", tokens));
        gridBody.Children.Add(SliderRow("行间距（垂直）", settings, tokens,
            () => Clamp(settings.Get("desktop.itemSpacingY", 12d), 0, 48),
            v => settings.Set("desktop.itemSpacingY", v), 0, 48));
        gridBody.Children.Add(SliderRow("列间距（水平）", settings, tokens,
            () => Clamp(settings.Get("desktop.itemSpacingX", 12d), 0, 48),
            v => settings.Set("desktop.itemSpacingX", v), 0, 48));
        gridBody.Children.Add(NoteBlock(
            "图标格尺寸由「图标大小 + 间距」自动推导（格宽=图标+列间距+30，格高=图标+行间距+36），" +
            "故调整间距时网格整体适配，不会在桌面下方留下空白。", tokens));
        panel.Children.Add(gridCard);

        // ---- 布局避让 ----
        var layoutCard = GroupCard();
        var layoutBody = CardBody(layoutCard);
        layoutBody.Children.Add(TitleBlock("布局避让", tokens));
        layoutBody.Children.Add(ToggleRow("为 Dock 让出底部空间", settings, tokens,
            "desktop.reserveDock", true));
        layoutBody.Children.Add(ToggleRow("为 Windows 原生任务栏让出底部空间", settings, tokens,
            "desktop.reserveTaskbar", true));
        layoutBody.Children.Add(ToggleRow("为顶部菜单栏让出空间", settings, tokens,
            "desktop.reserveMenuBar", true));
        layoutBody.Children.Add(NoteBlock(
            "Dock 占用高度按 dock.iconSize / dock.showLabel / dock.bottomMargin 估算；" +
            "任务栏高度取主屏工作区差值。", tokens));
        panel.Children.Add(layoutCard);

        // ---- 排列与拖动 ----
        var arrangeCard = GroupCard();
        var arrangeBody = CardBody(arrangeCard);
        arrangeBody.Children.Add(TitleBlock("排列与拖动", tokens));
        arrangeBody.Children.Add(ToggleRow("自动排列（开启后图标按瀑布列排布，不可拖动换位）", settings, tokens,
            "desktop.autoArrange", false));
        arrangeBody.Children.Add(ToggleRow("拖动后对齐网格", settings, tokens,
            "desktop.snapToGrid", true));

        var resetHint = NoteBlock("", tokens);
        var resetBtn = WithStyle(new Button
        {
            Content = "重置图标位置",
            Width = 140,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacButton", tokens);
        resetBtn.Click += (_, _) =>
        {
            try
            {
                // 清空坐标表 → 触发 Changed → 桌面重排并自动分配初始网格位置
                settings.Set("desktop.iconPositions", new Dictionary<string, double[]>());
                resetHint.Text = "已重置图标位置。";
            }
            catch
            {
                resetHint.Text = "重置失败。";
            }
        };
        arrangeBody.Children.Add(resetBtn);
        arrangeBody.Children.Add(resetHint);
        arrangeBody.Children.Add(NoteBlock(
            "默认可自由拖动图标改变布局：按住左键拖动即可（拖动时图标会半透明放大并跟随鼠标），" +
            "松手保存位置；从外部拖入文件会复制/移动到桌面。", tokens));
        panel.Children.Add(arrangeCard);

        // ---- 系统快捷入口 ----
        var sysCard = GroupCard();
        var sysBody = CardBody(sysCard);
        sysBody.Children.Add(TitleBlock("系统显示设置", tokens));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(LinkButton("显示设置", () => OpenUri("ms-settings:display"), tokens));
        row.Children.Add(LinkButton("个性化", () => OpenUri("ms-settings:personalization"), tokens));
        sysBody.Children.Add(row);
        sysBody.Children.Add(NoteBlock("与桌面右键菜单中的同名入口一致。", tokens));
        panel.Children.Add(sysCard);

        return panel;
    }

    // ===== 系统入口 =====

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败静默（M10）
        }
    }

    // ===== 共享 UI helper（与 ThemeSection / LeftDockSection 同风格，Section 自包含） =====

    private static Border GroupCard()
    {
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 16),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = new Border { CornerRadius = new CornerRadius(9), Child = body }
        };
    }

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBlock NoteBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        Foreground = tokens.MutedForeground,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0)
    };

    private static UIElement SliderRow(string label, ISettingsService settings, IThemeTokens tokens,
        Func<double> getVal, Action<double> setVal, double min, double max)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground
        };
        DockPanel.SetDock(text, Dock.Left);

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
            Width = 200,
            Value = getVal(),
            VerticalAlignment = VerticalAlignment.Center,
            AutoToolTipPlacement = AutoToolTipPlacement.None
        }, "MacSlider", tokens);
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue, 0);
            setVal(v);
            valueText.Text = v.ToString("0");
        };

        var right = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(valueText, Dock.Right);
        right.Children.Add(valueText);
        DockPanel.SetDock(slider, Dock.Left);
        right.Children.Add(slider);

        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(text);
        row.Children.Add(right);
        return row;
    }

    private static UIElement ToggleRow(string label, ISettingsService settings, IThemeTokens tokens,
        string key, bool def)
    {
        var box = WithStyle(new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 13,
            IsChecked = settings.Get(key, def)
        }, "MacToggle", tokens);
        box.Checked += (_, _) => settings.Set(key, true);
        box.Unchecked += (_, _) => settings.Set(key, false);
        return box;
    }

    private static UIElement LinkButton(string label, Action onClick, IThemeTokens tokens)
    {
        var btn = WithStyle(new Button
        {
            Content = label,
            Width = 120,
            Margin = new Thickness(0, 0, 10, 0)
        }, "MacButton", tokens);
        btn.Click += (_, _) =>
        {
            try { onClick(); }
            catch { /* 打开失败静默（M10） */ }
        };
        return btn;
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
}
