using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Pinning.Contracts;

namespace BetterDesktop.Shell.Notification.Windows;

/// <summary>
/// 新装应用提醒弹窗（右下角）。
/// 检测到新安装的应用后，弹窗列出并支持一键固定到 Dock / 全部忽略。
/// 退出时自动把当前列表全部标记为已见，避免重复打扰。
/// 自 shell-dock 迁入：依赖改为 IAppSourceService / IAppIconService / IPinningService（与 Dock 解耦），模型为 AppItem。
/// </summary>
internal sealed class NewAppsNotificationWindow : ShellWindow
{
    // 右下角紧凑弹窗，贴边定位，关闭外边缘阴影留白（避免内缩与定位偏移）。
    protected override Thickness ChromeMargin => new(0);
    // 右下角通知卡：宽度/高度按内容固定计算（360 宽、高度随 app 数），钉死不可缩放。
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    private readonly IAppSourceService _appSource;
    private readonly IAppIconService? _iconService;
    private readonly IPinningService? _pinning;
    private readonly StackPanel _listPanel;
    private readonly List<AppItem> _apps;

    public NewAppsNotificationWindow(
        IVibrancyService vibrancy,
        IAppSourceService appSource,
        IAppIconService? iconService,
        IPinningService? pinning,
        IAppearanceService? appearance,
        IReadOnlyList<AppItem> apps)
    {
        VibrancyService = vibrancy;
        // 全局外观服务（主题圆角/描边/字号）：交给基类统一驱动，与设置窗口观感一致。
        AppearanceService = appearance;
        _appSource = appSource;
        _iconService = iconService;
        _pinning = pinning;
        _apps = apps.ToList();

        Width = 360;
        // 依据应用数量确定高度，避免自动尺寸在定位时产生 NaN
        var contentHeight = 70 + _apps.Count * 56;
        Height = Math.Min(Math.Max(contentHeight, 150), 520);
        WindowStartupLocation = WindowStartupLocation.Manual;
        PositionBottomRight();

        // 内容
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "检测到新装应用",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12)
        };
        SetThemeBinding(title, TextBlock.ForegroundProperty, "ThemeForeground");
        Grid.SetRow(title, 0);

        _listPanel = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 330
        };
        Grid.SetRow(scroll, 1);

        var dismiss = new Button
        {
            Content = "全部忽略",
            Width = 90,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 12, 12)
        };
        dismiss.Click += (_, _) => { MarkAllSeen(); Close(); };
        Grid.SetRow(dismiss, 2);

        root.Children.Add(title);
        root.Children.Add(scroll);
        root.Children.Add(dismiss);

        // 毛玻璃容器背景：交给统一基类（ApplyAppearance 会把色调托盘/皮肤同步到 ChromeBorder），
        // 与其他 Shell 窗口一致透出毛玻璃，不在此自行铺托盘（否则观感偏离统一基类）。
        var outer = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12)
        };
        outer.Child = root;
        Content = outer;

        // 根 Border 接入基类外观令牌：对齐 FrostedGlassDemo/统一基类——圆角 8、1.5px 描边（受光面对角渐变、
        // 随主题刷新），由基类统一驱动（取代上方写死的 CornerRadius=12/BorderThickness=1）。
        SetThemeBinding(outer, Border.BorderBrushProperty, "CardBorderBrush");
        // 根背景跟随皮肤（与描边同一 DynamicResource 机制，一处改全局跟随），无皮肤时为半透明托盘透毛玻璃。
        SetThemeBinding(outer, Border.BackgroundProperty, "SkinBackgroundBrush");
        ChromeBorder = outer;

        foreach (var app in _apps)
        {
            _listPanel.Children.Add(CreateRow(app));
        }
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    private FrameworkElement CreateRow(AppItem app)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Margin = new Thickness(4, 6, 4, 6);

        var icon = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Source = CreatePlaceholderIcon(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 0);

        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        SetThemeBinding(label, TextBlock.ForegroundProperty, "ThemeForeground");
        Grid.SetColumn(label, 1);

        var pinButton = new Button
        {
            Content = "固定",
            Width = 54,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        pinButton.Click += (_, _) => PinAndRemove(app, row);
        Grid.SetColumn(pinButton, 2);

        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(pinButton);

        _ = LoadIconAsync(app, icon);

        return row;
    }

    private void PinAndRemove(AppItem app, FrameworkElement row)
    {
        try
        {
            // 一键固定到 Dock（zone="dock"）；去重由通用固定服务负责。
            _pinning?.Pin("dock", app);
        }
        catch
        {
            // 固定失败不阻断
        }

        // 无论成功与否都标记已见并移除该行
        _appSource.MarkAppsSeen(new[] { app });
        _apps.Remove(app);
        _listPanel.Children.Remove(row);

        if (_apps.Count == 0)
        {
            Close();
        }
    }

    private void MarkAllSeen()
    {
        if (_apps.Count == 0)
        {
            return;
        }

        _appSource.MarkAppsSeen(_apps);
        _apps.Clear();
    }

    protected override void OnLoadedCore()
    {
    }

    protected override void OnClosed(EventArgs e)
    {
        // 用户直接关闭（例如点击 X / 失去焦点退出）时也保证不再重复提醒
        MarkAllSeen();
        base.OnClosed(e);
    }

    private async Task LoadIconAsync(AppItem item, Image target)
    {
        try
        {
            if (_iconService is null)
            {
                return;
            }

            var icon = await _iconService.GetIconAsync(item);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标加载失败不阻断
        }
    }

    private static ImageSource CreatePlaceholderIcon()
    {
        var visual = new DrawingVisual();
        using var ctx = visual.RenderOpen();
        ctx.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 40, 40));
        var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
