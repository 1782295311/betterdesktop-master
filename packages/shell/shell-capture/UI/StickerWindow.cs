using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// 贴图窗口：图片置顶显示、拖拽、角部缩放、透明度、右键菜单（复制/置顶/关闭）。
/// 点击穿透：整窗 WS_EX_TRANSPARENT，同时弹出小型「退出穿透」面板（穿透窗口本身收不到鼠标）。
/// 重启不恢复（计划 §7 默认：不恢复贴图）。
/// </summary>
public sealed class StickerWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    private readonly string _pngPath;
    private Point _dragOffset;
    private bool _dragging;
    private bool _clickThrough;
    private Window? _escapePanel;

    /// <param name="pngPath">贴图图片路径。</param>
    /// <param name="topmostDefault">默认是否置顶（2026-09-15 起由设置 stickerTopmost 决定，用户可在 settings.json 改）。</param>
    /// <param name="onClosed">窗口关闭回调（会话清理本功能文件）。</param>
    public StickerWindow(string pngPath, bool topmostDefault = true, Action? onClosed = null)
    {
        _pngPath = pngPath;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = topmostDefault;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        // 任何关闭路径（右键菜单/Alt+F4/进程内）触发回调 → 会话清理本功能文件
        Closed += (_, _) => onClosed?.Invoke();

        // 四边留 4px 透明边用于阴影/手柄
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = ThemeBrushes.Get("BorderStroke"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4),
            // 贴图是"浮在桌面上的照片"：一圈柔和投影比硬描边更能表达层级
            //（Effect 只作用于这一层，不参与图像内容，不影响导出/复制）
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
        var grid = new Grid();
        var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(1) };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(pngPath);
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"贴图载入失败：{ex.Message}");
            Close();
            return;
        }
        grid.Children.Add(img);
        border.Child = grid;
        // 注意：不要在此处设 Content=border —— 下方 Content=outer（含 border）会因
        // "元素已是另一个元素的逻辑子级" 抛异常 → 贴图窗口构造失败、旧版直接崩进程
        // （2026-09-15 真机根因，见 capture.log 17:09:57）。

        // 初始尺寸（屏幕 40% 宽上限），位置屏幕中心
        double sw = SystemParameters.WorkArea.Width;
        double sh = SystemParameters.WorkArea.Height;
        double w = Math.Min(img.Source.Width, sw * 0.4);
        double h = w * img.Source.Height / img.Source.Width;
        Width = w + 10;
        Height = h + 10;
        Left = (sw - Width) / 2 + SystemParameters.WorkArea.Left;
        Top = (sh - Height) / 2 + SystemParameters.WorkArea.Top;

        // 拖拽：窗口任意位置按下可拖（含透明边/空白，2026-09-15 用户要求"随意移动"）。
        // 屏幕坐标直接定位（Left=鼠标-按下偏移），无增量累积误差。
        // 缩放手柄 thumb 自身处理鼠标（handled）→ 不会误触发窗口拖拽。
        MouseLeftButtonDown += (_, e) =>
        {
            if (_clickThrough)
            {
                SetClickThrough(false); // 穿透开着收不到鼠标事件；能进来说明已退出，确保可拖
            }
            _dragging = true;
            _dragOffset = e.GetPosition(null); // 屏幕坐标
            CaptureMouse();
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(null);
                Left = p.X - _dragOffset.X;
                Top = p.Y - _dragOffset.Y;
            }
        };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制到剪贴板" };
        copy.Click += (_, _) => CopyToClipboard();
        var pin = new MenuItem { Header = "置顶", IsCheckable = true, IsChecked = Topmost };
        pin.Checked += (_, _) => Topmost = true;
        pin.Unchecked += (_, _) => Topmost = false;
        var through = new MenuItem { Header = "点击穿透" };
        through.Click += (_, _) => SetClickThrough(true);
        var close = new MenuItem { Header = "关闭贴图" };
        close.Click += (_, _) => Close();
        menu.Items.Add(copy);
        menu.Items.Add(pin);
        menu.Items.Add(through);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        // 主题化弹层（完整重模板代价大；这里统一底色/前景/字号/内边距，弹层本体仍走系统模板）
        menu.Background = ThemeBrushes.Get("ThemeContentBackground");
        menu.Foreground = ThemeBrushes.Get("ThemeForeground");
        menu.BorderBrush = ThemeBrushes.Get("BorderStroke");
        menu.Padding = new Thickness(EntryTheme.Scale.SpaceXS);
        menu.FontSize = EntryTheme.Scale.FontBody;
        ContextMenu = menu;
        MouseRightButtonUp += (_, e) =>
        {
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        };

        // 角部缩放手柄
        var thumb = new Thumb
        {
            Width = 12,
            Height = 12,
            Cursor = Cursors.SizeNWSE,
            Template = CreateThumbTemplate(),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        thumb.DragDelta += (_, e) =>
        {
            double nw = Math.Max(80, Width + e.HorizontalChange);
            double nh = Math.Max(60, Height + e.VerticalChange);
            Width = nw;
            Height = nh;
        };
        var outer = new Grid();
        outer.Children.Add(border);
        outer.Children.Add(thumb);
        Content = outer;
    }

    /// <summary>
    /// 角部缩放柄模板：**可见**的斜向三粒点（原实现是整块透明矩形 —— 用户根本看不出右下角能拖拽缩放）。
    /// 命中区仍按 Thumb 的 14×14 计算，血点只是视觉指示。
    /// </summary>
    private static ControlTemplate CreateThumbTemplate()
    {
        // 深色圆角底 + 白色斜向三粒点：白点直接压在亮图上会看不见，故先垫一层暗底
        var backer = new FrameworkElementFactory(typeof(Border));
        backer.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusS));
        backer.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)));

        var grid = new FrameworkElementFactory(typeof(Grid));
        foreach (var (dx, dy) in new[] { (8.5, 8.5), (4.5, 8.5), (8.5, 4.5) })
        {
            var dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.SetValue(FrameworkElement.WidthProperty, 2.5);
            dot.SetValue(FrameworkElement.HeightProperty, 2.5);
            dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(dx, dy, 0, 0));
            dot.SetValue(Shape.FillProperty, new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
            grid.AppendChild(dot);
        }
        backer.AppendChild(grid);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = backer };
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, enabled ? style | WsExTransparent : style & ~WsExTransparent);

        if (enabled)
        {
            ShowEscapePanel();
        }
        else
        {
            _escapePanel?.Close();
            _escapePanel = null;
        }
    }

    /// <summary>穿透时贴图窗口收不到鼠标，用小面板提供退出通道（+关闭）。</summary>
    private void ShowEscapePanel()
    {
        if (_escapePanel is not null)
        {
            _escapePanel.Activate();
            return;
        }
        var panel = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            // 尺寸交给内容决定（固定 120×34 在换字号/DPI 下会裁字）
            SizeToContent = SizeToContent.WidthAndHeight,
        };
        // HUD 表面（浮在桌面之上，恒深色）+ 与覆盖层同款胶囊按钮
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(CaptureUi.IconTextButton(
            "\uE7A7", "退出穿透", () => SetClickThrough(false), CaptureUi.Face.Hud));
        bar.Children.Add(CaptureUi.IconButton(
            "\uE711", "关闭贴图", Close, CaptureUi.Face.Hud));
        panel.Content = CaptureUi.Capsule(bar);
        panel.Left = Math.Max(0, Left);
        panel.Top = Math.Max(0, Top - 40);
        panel.Show();
        _escapePanel = panel;
        panel.Closed += (_, _) => _escapePanel = null;
    }

    private void CopyToClipboard()
    {
        try
        {
            byte[] png = File.ReadAllBytes(_pngPath);
            // Win32 整块写：失败即真没写（OpenClipboard 竞争）→ 重试 3 次（与截图保存路径一致）
            string? lastError = null;
            for (int i = 0; i < 3; i++)
            {
                if (ClipboardImageWriter.WritePng(png, out string error))
                {
                    CaptureLog.Info("贴图已复制到剪贴板");
                    return;
                }
                lastError = error;
                Thread.Sleep(150);
            }
            MessageBox.Show($"复制失败：{lastError}", "贴图", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "贴图", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _escapePanel?.Close();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
