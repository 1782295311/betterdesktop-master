// BetterDesktop.Shell.Island — 设置分区「灵动岛」
//
// 只做三件事：总开关 / 动效强度 / 各消息来源开关 + 位置微调。全部写 settings（前缀 island.），
// 由插件订阅 shell.settings/changed 即时落到运行中的表面（一处改，立即生效，不需要重启）。

using System;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Island.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Island.Sections;

/// <summary>设置中心「灵动岛」分区。</summary>
internal sealed class IslandSection : ISettingsSection
{
    /// <summary>设置变更回调（插件据此重读选项并即时应用）。</summary>
    private readonly Action? _onChanged;

    public IslandSection(Action? onChanged = null) => _onChanged = onChanged;

    /// <inheritdoc />
    public string Title => "灵动岛";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 显示与动效 ----
        var lookCard = SettingsCard();
        var lookBody = CardBody(lookCard);
        lookBody.Children.Add(TitleBlock("显示与动效", tokens));
        lookBody.Children.Add(HintBlock(
            "灵动岛贴在菜单栏中置，显示程序所有活动（复制、转换进度、媒体播放）。收出成液体形态：从菜单栏长出来、脱离时颈部变细。",
            tokens));
        lookBody.Children.Add(ToggleRow("启用灵动岛", settings, tokens, IslandOptions.EnabledKey, true));
        lookBody.Children.Add(TierRow(settings, tokens));
        lookBody.Children.Add(ToggleRow("鼠标悬停展开详情", settings, tokens, IslandOptions.HoverExpandKey, true));
        lookBody.Children.Add(ToggleRow("空闲时保留一枚小凸起（默认关）", settings, tokens, IslandOptions.IdleVisibleKey, false));
        lookBody.Children.Add(HintBlock(
            "关掉（默认）：不用时岛完全藏进菜单栏，只有活动出现才长出来。打开则空闲时留一枚小凸起做位置提示（只画形状、不显示内容、也不接管点击）。",
            tokens));
        panel.Children.Add(lookCard);

        // ---- 消息来源 ----
        var sourceCard = SettingsCard();
        var sourceBody = CardBody(sourceCard);
        sourceBody.Children.Add(TitleBlock("消息来源", tokens));
        sourceBody.Children.Add(HintBlock(
            "关闭某一来源只会让它的活动不进岛；功能本身不受影响（例如关掉会话来源后按序粘贴照旧，只是不再上岛提示）。"
            + "岛**不会**因为普通复制而弹出——那会把剪贴板内容暴露在屏幕上；它只呈现你主动发起的按序粘贴/按格粘会话进度。",
            tokens));
        sourceBody.Children.Add(ToggleRow("剪贴板按序粘贴 / 按格粘会话", settings, tokens, IslandOptions.PasteSessionSourceKey, true));
        sourceBody.Children.Add(ToggleRow("格式转换进度与完成提示", settings, tokens, IslandOptions.ConvertSourceKey, true));
        sourceBody.Children.Add(ToggleRow("媒体播放控制", settings, tokens, IslandOptions.MediaSourceKey, true));
        panel.Children.Add(sourceCard);

        // ---- 位置微调 ----
        var posCard = SettingsCard();
        var posBody = CardBody(posCard);
        posBody.Children.Add(TitleBlock("位置微调", tokens));
        posBody.Children.Add(HintBlock(
            "岛默认水平居中于菜单栏、紧贴其下沿。多显示器或特殊分辨率下若不居中，可用这两项微调（单位：像素）。",
            tokens));
        posBody.Children.Add(SliderRow("水平偏移", settings, tokens, IslandOptions.OffsetXKey, 0d, -400d, 400d));
        posBody.Children.Add(SliderRow("垂直偏移", settings, tokens, IslandOptions.OffsetYKey, 0d, -12d, 60d));
        panel.Children.Add(posCard);

        return panel;
    }

    // ===== UI helper（与 MenuBarSection 同风格：分区自包含，颜色全走令牌） =====

    private static Border SettingsCard()
    {
        var body = new StackPanel { Margin = new Thickness(18, 15, 18, 16) };
        var inner = new Border { CornerRadius = new CornerRadius(9), Child = body };
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = inner,
            SnapsToDevicePixels = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCardBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorder");
        return card;
    }

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 4),
    };

    private UIElement ToggleRow(string label, ISettingsService settings, IThemeTokens tokens, string key, bool fallback)
    {
        var box = WithStyle(new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 6, 12, 6),
            FontSize = 13,
            IsChecked = settings.Get(key, fallback),
        }, tokens);

        box.Checked += (_, _) =>
        {
            settings.Set(key, true);
            _onChanged?.Invoke();
        };
        box.Unchecked += (_, _) =>
        {
            settings.Set(key, false);
            _onChanged?.Invoke();
        };
        return box;
    }

    private UIElement TierRow(ISettingsService settings, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 2), LastChildFill = false };
        var label = new TextBlock
        {
            Text = "动效强度",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.Foreground,
        };
        DockPanel.SetDock(label, System.Windows.Controls.Dock.Left);
        row.Children.Add(label);

        var current = IslandOptions.ParseTier(settings.Get(IslandOptions.TierKey, string.Empty), IslandMotionTier.Full);
        var combo = new ComboBox
        {
            Width = 160,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = tokens.Foreground,
            ItemsSource = new[] { "完整（液体形变 + 脉冲）", "精简（快一点，无脉冲）", "关闭（直接切换）" },
            SelectedIndex = current switch
            {
                IslandMotionTier.Off => 2,
                IslandMotionTier.Lite => 1,
                _ => 0,
            },
        };
        combo.SelectionChanged += (_, _) =>
        {
            var tier = combo.SelectedIndex switch
            {
                2 => IslandMotionTier.Off,
                1 => IslandMotionTier.Lite,
                _ => IslandMotionTier.Full,
            };
            settings.Set(IslandOptions.TierKey, IslandOptions.TierText(tier));
            _onChanged?.Invoke();
        };
        DockPanel.SetDock(combo, System.Windows.Controls.Dock.Left);
        row.Children.Add(combo);
        return row;
    }

    private UIElement SliderRow(string label, ISettingsService settings, IThemeTokens tokens, string key, double fallback, double min, double max)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground,
        };
        DockPanel.SetDock(text, System.Windows.Controls.Dock.Left);

        var valueText = new TextBlock
        {
            Text = settings.Get(key, fallback).ToString("0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Foreground = tokens.MutedForeground,
            MinWidth = 36,
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = settings.Get(key, fallback),
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            var value = Math.Round(e.NewValue, 0);
            valueText.Text = value.ToString("0");
            settings.Set(key, value);
            _onChanged?.Invoke();
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

    private static T WithStyle<T>(T element, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources["MacToggle"] is Style style)
        {
            element.Style = style;
        }
        else if (element is Control control)
        {
            control.Foreground = tokens.Foreground;
        }

        return element;
    }
}
