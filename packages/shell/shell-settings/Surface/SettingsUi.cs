using System.Windows;
using System.Windows.Controls;

namespace BetterDesktop.Shell.Settings.Surface;

/// <summary>
/// 设置分区的共享 UI 基元：分组卡片。
/// <para>
/// 背景：此前 8 个分区各自复制了一份 <c>GroupCard</c> 实现，且全部为「全透明无描边」——
/// 结果就是设置内容在毛玻璃上"漂浮"，看不出分组边界，且各分区观感随复制时点漂移。
/// 此处收口为单一实现，所有分区共用同一张卡片。
/// </para>
/// <para>
/// 表面与描边一律用 <c>SetResourceReference</c> 绑定（等价于 XAML 的 <c>{DynamicResource}</c>）：
/// 主题令牌一变，所有卡片自动跟随，无需重建视觉树，也不会把颜色固化在分区代码里。
/// </para>
/// </summary>
public static class SettingsUi
{
    /// <summary>卡片表面画刷资源键（在 host/App.xaml 中定义）。</summary>
    public const string CardBackgroundKey = "SettingsCardBackground";

    /// <summary>卡片描边画刷资源键（在 host/App.xaml 中定义）。</summary>
    public const string CardBorderKey = "SettingsCardBorder";

    /// <summary>
    /// 构建一张分组卡片。
    /// 刻意保持「外 Border → 内 Border → StackPanel」三层结构与既有实现一致，
    /// 使各分区既有的 <c>CardBody(card)</c> 取值方式继续成立，无需改动取内容的代码。
    /// </summary>
    public static Border CreateCard()
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

        // 半透明白叠加而非实色：既给出分组边界，又保留毛玻璃的通透感。
        card.SetResourceReference(Border.BackgroundProperty, CardBackgroundKey);
        card.SetResourceReference(Border.BorderBrushProperty, CardBorderKey);
        return card;
    }

    /// <summary>取卡片的内容面板（与既有分区 <c>CardBody</c> 语义一致）。</summary>
    public static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;
}
