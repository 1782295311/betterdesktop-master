// BetterDesktop.Shell.Clipboard — 剪贴板设置分区（Phase B 6.5 G6/K5 配置化）
// 键：extensions.clipboard-history.{capacity,pinned-limit,max-image-mb,max-total-image-mb,retention-days,hotkey-*}
// 热键为只读展示（组合键由 Manager 常量锁定，避免热键冲突面失控）。

using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.Clipboard.Sections;

/// <summary>剪贴板历史设置分区（容量/收藏/图片预算/保留天数；热键只读说明）。</summary>
internal sealed class ClipboardSection : ISettingsSection
{
    private const string CapacityKey = "extensions.clipboard-history.capacity";
    private const string PinnedLimitKey = "extensions.clipboard-history.pinned-limit";
    private const string MaxImageMbKey = "extensions.clipboard-history.max-image-mb";
    private const string MaxTotalImageMbKey = "extensions.clipboard-history.max-total-image-mb";
    private const string RetentionDaysKey = "extensions.clipboard-history.retention-days";

    public string Title => "剪贴板";

    public string? IconKey => null;

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 存储与容量 ----
        var card = SettingsUi.CreateCard();
        var body = (StackPanel)((Border)card.Child!).Child!;
        body.Children.Add(TitleBlock("存储与容量", tokens));

        body.Children.Add(SliderRow(
            "历史容量上限", 100, 10000, settings.Get(CapacityKey, 10000),
            v => $"{v} 条", v => settings.Set(CapacityKey, v), tokens));
        body.Children.Add(SliderRow(
            "收藏上限", 10, 500, settings.Get(PinnedLimitKey, 200),
            v => $"{v} 条", v => settings.Set(PinnedLimitKey, v), tokens));
        body.Children.Add(SliderRow(
            "保留天数", 7, 365, settings.Get(RetentionDaysKey, 90),
            v => $"{v} 天", v => settings.Set(RetentionDaysKey, v), tokens));
        body.Children.Add(SliderRow(
            "单张图片上限", 1, 20, settings.Get(MaxImageMbKey, 5),
            v => $"{v} MB", v => settings.Set(MaxImageMbKey, v), tokens));
        body.Children.Add(SliderRow(
            "图片总量上限", 20, 500, settings.Get(MaxTotalImageMbKey, 200),
            v => $"{v} MB", v => settings.Set(MaxTotalImageMbKey, v), tokens));

        panel.Children.Add(card);

        // ---- 快捷键 ----
        var hotkeyCard = SettingsUi.CreateCard();
        var hotkeyBody = (StackPanel)((Border)hotkeyCard.Child!).Child!;
        hotkeyBody.Children.Add(TitleBlock("快捷键", tokens));
        hotkeyBody.Children.Add(HintBlock(
            "Ctrl+Shift+V 打开历史面板（按序粘贴激活时改为粘贴下一条）\n" +
            "Ctrl+Shift+P 收藏视图\n" +
            "Ctrl+Shift+Backspace 暂停/恢复捕获", tokens));
        panel.Children.Add(hotkeyCard);

        // ---- 隐私 ----
        var privacyCard = SettingsUi.CreateCard();
        var privacyBody = (StackPanel)((Border)privacyCard.Child!).Child!;
        privacyBody.Children.Add(TitleBlock("隐私", tokens));
        privacyBody.Children.Add(HintBlock(
            "密码管理器/网银/验证码等敏感窗口中的复制内容默认不记录；\n" +
            "暂停期间不捕获，历史可正常查询与粘贴。", tokens));
        panel.Children.Add(privacyCard);

        return panel;
    }

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        Margin = new Thickness(0, 4, 0, 2),
        TextWrapping = TextWrapping.Wrap,
    };

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        element.Style = (Style)element.FindResource(key);
        return element;
    }

    private static UIElement SliderRow(
        string label,
        int min,
        int max,
        int value,
        Func<int, string> format,
        Action<int> onChange,
        IThemeTokens tokens)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var valueText = new TextBlock
        {
            Text = format(value),
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            Width = 80,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(valueText, System.Windows.Controls.Dock.Right);
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = tokens.Foreground,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = WithStyle(new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            IsSnapToTickEnabled = true,
            TickFrequency = Math.Max(1, (max - min) / 50),
            VerticalAlignment = VerticalAlignment.Center,
        }, "MacSlider", tokens);
        slider.ValueChanged += (_, _) =>
        {
            int v = (int)slider.Value;
            valueText.Text = format(v);
            onChange(v);
        };

        dock.Children.Add(valueText);
        dock.Children.Add(slider);
        dock.Children.Add(labelText);
        return dock;
    }
}
