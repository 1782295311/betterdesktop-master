// BetterDesktop.Shell.MenuBar — 通知中心独立弹出面板（自绘，走统一基类/主题）
// 不调起系统原生通知中心：UI 全部自绘，数据来自既有状态监控的真实快照
//   （电池 / 网络 / CPU / 内存），打开时实时生成"当前系统状态通知"列表；
//   一切正常时显示空态"暂无通知"。底部提供"系统通知设置"入口（真实可用）。
// 与其它状态条面板一致：继承 MenuBarPopupWindow，失焦/点击窗口外自动收起。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 通知中心独立弹出面板。内容 100% 来自监控器实时快照，零硬编码状态数据；
/// 空态为真实"无通知"状态，非占位。
/// </summary>
internal sealed class NotificationCenterPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 340;

    private readonly IBatteryMonitor? _battery;
    private readonly INetworkMonitor? _network;
    private readonly ICpuMonitor? _cpu;
    private readonly IMemoryMonitor? _memory;

    public NotificationCenterPopupWindow(
        IVibrancyService vibrancy,
        IBatteryMonitor? battery,
        INetworkMonitor? network,
        ICpuMonitor? cpu,
        IMemoryMonitor? memory,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        _battery = battery;
        _network = network;
        _cpu = cpu;
        _memory = memory;
    }

    /// <summary>Playground/大容器预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载。
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 标题行：通知中心
        var title = new TextBlock
        {
            Text = "通知中心",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        column.Children.Add(title);

        // 通知列表（真实快照生成）
        var listPanel = new StackPanel { Orientation = Orientation.Vertical };
        var items = CollectNotifications();
        if (items.Count == 0)
        {
            listPanel.Children.Add(BuildEmptyState());
        }
        else
        {
            foreach (var item in items)
            {
                listPanel.Children.Add(BuildItemCard(item));
            }
        }
        var scroll = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        column.Children.Add(scroll);

        // 底部：系统通知设置入口（真实可用，非占位）
        var settingsBtn = new Button
        {
            Content = "系统通知设置",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = 11,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        SetThemeBinding(settingsBtn, Button.ForegroundProperty, "ThemeMutedForeground");
        settingsBtn.Click += (_, _) => OpenNotificationSettings();
        column.Children.Add(settingsBtn);

        root.Child = column;
        return root;
    }

    // ---- 通知数据：来自监控器实时快照（真实数据，非模拟） ----

    private sealed record NotificationEntry(string Title, string Body, string IconData);

    private List<NotificationEntry> CollectNotifications()
    {
        var list = new List<NotificationEntry>();
        AddIf(() => _battery?.GetSnapshot(), s => s.Progress >= 0 && s.Progress < 20,
            () => new NotificationEntry("电量不足", $"{Math.Round(_battery!.GetSnapshot().Progress)}% 电量，请连接电源。", WarningIconData),
            list);
        AddIf(() => _network?.GetSnapshot(), s => s.Severity != StatusSeverity.Normal,
            () => new NotificationEntry("网络连接中断", "当前无法访问网络，请检查连接。", WarningIconData),
            list);
        AddIf(() => _cpu?.GetSnapshot(), s => s.Progress > 90,
            () => new NotificationEntry("CPU 负载过高", $"当前 CPU 利用率约 {Math.Round(_cpu!.GetSnapshot().Progress)}%。", WarningIconData),
            list);
        AddIf(() => _memory?.GetSnapshot(), s => s.Progress > 90,
            () => new NotificationEntry("内存占用过高", $"当前内存占用约 {Math.Round(_memory!.GetSnapshot().Progress)}%。", WarningIconData),
            list);
        return list;
    }

    /// <summary>条件成立时追加通知；读取/判断全程 try-catch，监控器不可用时静默跳过。</summary>
    private static void AddIf(
        Func<StatusSnapshot?> snapshots,
        Func<StatusSnapshot, bool> predicate,
        Func<NotificationEntry> factory,
        List<NotificationEntry> list)
    {
        try
        {
            var snap = snapshots();
            if (snap is not null && predicate(snap))
            {
                list.Add(factory());
            }
        }
        catch
        {
            // 监控器不可用：跳过该条
        }
    }

    private FrameworkElement BuildEmptyState()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 4) };
        var icon = new Path
        {
            Data = Geometry.Parse(InfoIconData),
            Stretch = Stretch.Uniform,
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(icon, Path.FillProperty, "ThemeMutedForeground");
        panel.Children.Add(icon);
        var text = new TextBlock
        {
            Text = "暂无通知",
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetThemeBinding(text, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        panel.Children.Add(text);
        return panel;
    }

    private FrameworkElement BuildItemCard(NotificationEntry entry)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            SnapsToDevicePixels = true
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Path
        {
            Data = Geometry.Parse(entry.IconData),
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0)
        };
        SetThemeBinding(icon, Path.FillProperty, "ThemeMutedForeground");
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var textColumn = new StackPanel { Orientation = Orientation.Vertical };
        var titleText = new TextBlock
        {
            Text = entry.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        textColumn.Children.Add(titleText);
        var bodyText = new TextBlock
        {
            Text = entry.Body,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        SetThemeBinding(bodyText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        textColumn.Children.Add(bodyText);
        Grid.SetColumn(textColumn, 1);
        row.Children.Add(textColumn);

        card.Child = row;
        return card;
    }

    private static void OpenNotificationSettings()
    {
        try
        {
            ShellExecute(IntPtr.Zero, "open", "ms-settings:notifications", null, null, 0);
        }
        catch
        {
            // 设置页 URI 不可用时静默（面板其余功能不受影响）
        }
    }

    // ---- 矢量图标（自绘，Path） ----
    private const string WarningIconData = "M12 2L1 21h22L12 2zm1 14h-2v2h2v-2zm0-6h-2v4h2v-4z";
    private const string InfoIconData = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);
}
