using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.WindowTracker.Native;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>
/// 运行项悬停预览窗：紧贴图标上方的小预览浮层（原 DockThumbWindow，自 shell-dock 下沉并通用化）。
///
/// 外壳**逐字对齐 cairoshell 的 TaskThumbWindow.xaml**（裸 Window，不继承 ShellWindow 基类）：
///   AllowsTransparency="True" + Background="Transparent"（分层透明窗，cairoshell 实测可显示 DWM 缩略图）
///   SizeToContent="WidthAndHeight" / ShowActivated="False" / ShowInTaskbar="False" /
///   WindowStyle="None" / ResizeMode="NoResize" / Topmost="True" / UseLayoutRounding="True"
///
/// 之前多次"全黑"的根因不在分层与否，而在：①缩略图注册时机（构造期 destination 句柄为 Zero）；
/// ②DPI 取值错位矩形算出控件框外。这两点已在 DwmThumbnail / 本窗 Loaded 时序里修正。
/// 本窗脱离 ShellWindow 基类，是因为 ShellWindow 构造函数强制 AllowsTransparency=true +
/// 透明背景并在此之上叠加材质逻辑，而 cairoshell 的可用范本就是裸 Window，逐字照搬最稳。
///
/// 定位用调用方传入的屏幕级锚点坐标（宿主基于自身窗口 Top/Left 换算）。
/// 缩略图质量由调用方从设置读取后以 <see cref="ThumbnailQuality"/> 传入（本窗不依赖设置服务）。
/// </summary>
public sealed class ThumbnailWindow : Window
{
    private readonly List<DwmThumbnail> _thumbnails = new();
    private Point _anchorScreen;

    public ThumbnailWindow(Point anchorScreen, IReadOnlyList<RunningWindow> windows, ThumbnailQuality quality)
    {
        _anchorScreen = anchorScreen;

        var (thumbW, thumbH) = quality switch
        {
            ThumbnailQuality.Low => (160, 100),
            ThumbnailQuality.High => (240, 150),
            _ => (200, 124)
        };

        // 逐字对齐 cairoshell TaskThumbWindow.xaml（裸 Window，非基类）
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8),
            MaxWidth = 920
        };

        foreach (var window in windows)
        {
            var thumb = new DwmThumbnail(quality)
            {
                Width = thumbW,
                Height = thumbH,
                Margin = new Thickness(4)
            };

            // 关键时序（对齐 cairoshell TaskThumbnail.UserControl_Loaded）：
            // 在控件自身 Loaded（已挂载到已显示窗口）后才设置源句柄，
            // 此时 PresentationSource.FromVisual(this) 必然有效，注册一次成功。
            // DPI 由 DwmThumbnail 内部实时取自 CompositionTarget.TransformToDevice，
            // 不再用 VisualTreeHelper.GetDpi（SizeToContent 分层窗下取值时机不可靠→矩形错位→空白）。
            // 注册延迟到 ApplicationIdle：多个缩略图错峰注册，避免打开瞬间同步挤占 UI 线程导致卡顿。
            var captured = window;
            thumb.Loaded += (_, _) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (thumb.IsLoaded && IsVisible)
                    {
                        thumb.SourceWindowHandle = captured.Hwnd;
                    }
                }));
            };

            _thumbnails.Add(thumb);

            var cell = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Cursor = Cursors.Hand,
                Child = thumb
            };

            cell.MouseLeftButtonUp += (_, _) =>
            {
                RunningAppDetector.ActivateWindow(captured.Hwnd);
                Close();
            };

            wrap.Children.Add(cell);
        }

        var outer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 26, 26, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = wrap
        };
        Content = outer;

        SourceInitialized += OnSourceInitialized;

        // 窗口显示后（Loaded）再定位：此时 ActualWidth/Height 已由 SizeToContent 结算完成。
        Loaded += (_, _) => PositionAbove();
        SizeChanged += (_, _) => PositionAbove();

        // 鼠标离开预览窗且未回到图标时由宿主触发关闭（见宿主的 closePreviewTimer）。
        MouseLeave += (_, _) => PreviewMouseLeft?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 宿主订阅：鼠标离开预览层时请求关闭（由宿主延迟判定是否真的离开图标）。
    /// </summary>
    public event EventHandler? PreviewMouseLeft;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 隐藏 alt-tab：预览浮层不应出现在任务切换列表（对齐 cairoshell HideWindowFromTasks）。
        try
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                _ = NativeMethods.SetWindowLong(
                    helper.Handle,
                    NativeMethods.GwlExStyle,
                    NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GwlExStyle) | NativeMethods.WsExToolWindow);
            }
        }
        catch
        {
            // 隐藏失败不阻断
        }
    }

    /// <summary>
    /// 紧贴锚点（图标屏幕坐标）上方居中（对齐 cairoshell GetThumbnailAnchor + SetPosition）。
    /// 锚点为图标左上角在屏幕上的坐标；预览窗宽度自身已知（ActualWidth）。
    /// 边界夹取基于"锚点所在显示器"的工作区（多屏下不能只用主屏，否则跨屏预览会被夹错）。
    /// </summary>
    private void PositionAbove()
    {
        try
        {
            var anchorWidth = 64.0; // 运行项容器宽度（与宿主构建时一致）

            // 根据锚点坐标解析其所在显示器，作为边界夹取的参考屏。
            var screen = ScreenFromPoint(_anchorScreen);

            // 预览窗默认显示在图标正上方；贴近屏幕顶部时改为显示在图标下方。
            var screenHeight = screen.Height;
            if (_anchorScreen.Y <= screen.Top + screenHeight * 0.15)
            {
                Top = _anchorScreen.Y + DockItemHeight + 8;
            }
            else
            {
                Top = _anchorScreen.Y - ActualHeight - 8;
            }

            // 水平居中对齐图标，并夹在所在屏幕边界内（对齐 cairoshell SetPosition 的边界处理）。
            var desiredLeft = _anchorScreen.X + (anchorWidth - ActualWidth) / 2;
            var screenWidth = screen.Width;
            var screenLeft = screen.Left;
            if (desiredLeft < screenLeft + 4)
            {
                Left = screenLeft + 4;
            }
            else if (desiredLeft + ActualWidth > screenLeft + screenWidth - 4)
            {
                Left = screenLeft + screenWidth - ActualWidth - 4;
            }
            else
            {
                Left = desiredLeft;
            }
        }
        catch
        {
            // 定位失败不阻断
        }
    }

    /// <summary>
    /// 根据一个点解析其所在显示器矩形（多屏边界夹取用）。
    /// 直接用 user32 MonitorFromPoint 对齐宿主布局服务的显示器枚举口径。
    /// </summary>
    private static Rect ScreenFromPoint(Point p)
    {
        try
        {
            var hMonitor = MonitorFromPoint((int)p.X, (int)p.Y, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
            {
                var m = info.rcMonitor;
                return new Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
            }
        }
        catch
        {
            // 回退主屏
        }

        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // 运行项图标区域高度（与宿主构建容器时一致）。
    private const double DockItemHeight = 48.0;

    private static class NativeMethods
    {
        public const int GwlExStyle = -20;
        public const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
