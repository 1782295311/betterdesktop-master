// BetterDesktop.Shell.MenuBar — 通知中心独立弹出面板（C11 通知图标双用入口的「右键」目标）
//
// 角色：通知图标（MenuBarStatusButtonId.Notification）同一图标空间的低频入口。
//   左键（高频）= 控制中心（ControlCenterWindow）；右键（低频）= 本通知中心。
//   左右键分发见 Services/StatusBarMenuBarExtension.cs OnButtonClicked 的 Notification 分支。
//
// 【当前状态 · 如实标注】通知中心尚未接入系统通知源（WinRT UserNotificationListener 待接入）：
//   面板显示空态占位（「暂无通知」+ 接入说明），不写死任何假通知。
//   接入系统通知后，把 BuildEmptyState 替换为通知列表即可，面板骨架与弹窗互斥管线不变。
// 与其它菜单栏面板一致：继承 MenuBarPopupWindow，外观走主题令牌（基类统一挂载）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（通知中心面板，白话 → 方法）──
//   "面板整体布局" → BuildContent（标题行 + 空态区）
//   "空态占位"     → BuildEmptyState（大铃铛 + 暂无通知 + 接入说明）
//   面板基类 MenuBarPopupWindow；弹窗互斥/定位在 Services/StatusBarMenuBarExtension.cs。
// ────────────────────────────────────

/// <summary>
/// 通知中心独立面板（C11 双用图标 · 右键入口）。
/// 当前未接系统通知源 → 空态占位；接入后替换 <see cref="BuildEmptyState"/> 为通知列表。
/// </summary>
internal sealed class NotificationCenterWindow : MenuBarPopupWindow
{
    private const double PanelWidth = 320;

    public NotificationCenterWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
    }

    /// <inheritdoc />
    protected override FrameworkElement BuildContent()
    {
        var root = new Border { Padding = new Thickness(14) };
        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 标题行：标题 + 接入状态副标。
        var title = new TextBlock
        {
            Text = "通知中心",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        SetThemeBinding(title, TextBlock.ForegroundProperty, "ThemeForeground");
        column.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "系统通知待接入",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(hint);

        column.Children.Add(BuildEmptyState());

        root.Child = column;
        return root;
    }

    /// <summary>空态占位：大铃铛 + 「暂无通知」+ 接入说明（未接系统通知源，不写死假数据）。</summary>
    private FrameworkElement BuildEmptyState()
    {
        var wrap = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 24, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 铃铛轮廓（24×24 视口，Stroke 风格：钟体 + 铃舌）。
        var bell = new Path
        {
            Width = 46,
            Height = 46,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            Data = Geometry.Parse(
                "M 12 3 C 8.8 3 6 5.8 6 9 C 6 13.2 4.4 14.6 4.4 16.4 L 19.6 16.4 C 19.6 14.6 18 13.2 18 9 C 18 5.8 15.2 3 12 3 Z M 10 20 C 10.4 21 11.1 21.5 12 21.5 C 12.9 21.5 13.6 21 14 20")
        };
        SetThemeBinding(bell, Shape.StrokeProperty, "ThemeMutedForeground");
        wrap.Children.Add(bell);

        var empty = new TextBlock
        {
            Text = "暂无通知",
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(empty, TextBlock.ForegroundProperty, "ThemeForeground");
        wrap.Children.Add(empty);

        var note = new TextBlock
        {
            Text = "系统通知接入后，将在此汇总显示",
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(note, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        wrap.Children.Add(note);

        return wrap;
    }
}
