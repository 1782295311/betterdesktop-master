using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
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

    // 2026-09-12：悬停缩略图 → 实时大图预览层（PreviewWindow，替代 DWM 透明化 Aero Peek）。
    // DwmActivateLivePreview 会透明化 dock 与缩略图浮层（EXCLUDED_FROM_PEEK 实测无效），
    // 用户"只能预览无法选择进入"；改 DwmRegisterThumbnail 大图预览（不透明化任何窗口）。
    private PreviewWindow? _previewWindow;

    /// <summary>
    /// 【2026-09-14 用户决定：改回 v2】悬停格改为**原生 peek**
    /// （<c>DwmActivateLivePreview</c> → DWM 合成器层「只亮出目标窗口」，免疫 UIPI，管理员窗口同样有效）。
    /// <para>代价（已知且用户接受）：peek 会一并变暗 dock 与浮层本身——这正是当年否决 v2 的原因，
    /// 现由「dock 挂桌面层」尝试规避（<c>BETTERDESKTOP_DOCK_DESKTOP_LAYER=1</c>）。</para>
    /// <para>回退：<c>BETTERDESKTOP_DOCK_PEEK=v3</c> → 走原自绘大图预览层（不透明化任何窗口）。
    /// 决策与验收见 docs/plans/2026-09-14-dock-desktop-layer-peek-v2.md。</para>
    /// </summary>
    private static readonly bool UseNativePeek = !string.Equals(
        Environment.GetEnvironmentVariable("BETTERDESKTOP_DOCK_PEEK"),
        "v3",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>当前 peek 的目标窗口句柄（<see cref="IntPtr.Zero"/> = 未在 peek）。</summary>
    private IntPtr _peekTarget;

    private readonly WindowPeek _peek = new();

    /// <summary>
    /// peek 的 callingHwnd：**预览期间不被 DWM 透明化的那个窗口**（调用方传入，通常 = DockWindow）。
    /// IntPtr.Zero 时退回本浮层自身句柄（旧行为）。
    /// </summary>
    private readonly IntPtr _callingHwnd;

    private readonly ThumbnailQuality _quality;
    // 缩略图单元 → 源窗口句柄（供关闭按钮/点击进入定位）。
    private readonly List<(Border Cell, IntPtr Hwnd)> _cells = new();

    // 单元描边：peek 生效时高亮，让用户一眼看出"哪个窗口被临时置顶了"。
    // 颜色走主题令牌（前景白/强调色）实时取值，随亮暗模式与皮肤跟随。
    private static Brush CellIdleBrush => ThemeBrushes.Tint("ThemeForeground", 0.63);
    private static Brush CellActiveBrush => ThemeBrushes.Get("SkinAccentFromSkin");
    private static Brush CellIdleBackBrush => ThemeBrushes.Tint("ThemeForeground", 0.11);
    private static Brush CellActiveBackBrush => ThemeBrushes.AccentTint(0.25);

    /// <param name="anchorScreen">屏幕锚点。</param>
    /// <param name="windows">要展示缩略图的窗口集合。</param>
    /// <param name="quality">缩略图质量档。</param>
    /// <param name="callingHwnd">
    /// peek 的 callingHwnd = **预览期间不被透明化的那个窗口**，必须传 **DockWindow 句柄**：
    /// 传浮层自身会让 peek 把 dock（含运行区）一并变暗，用户观感即"运行区抽搐/闪"
    /// （见 docs/plans/2026-09-11-host-elevation-dock-peek.md §7 Q1；该结论此前未落地到代码）。
    /// </param>
    public ThumbnailWindow(
        Point anchorScreen,
        IReadOnlyList<RunningWindow> windows,
        ThumbnailQuality quality,
        IntPtr callingHwnd)
    {
        _anchorScreen = anchorScreen;
        _quality = quality;
        _callingHwnd = callingHwnd;

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

            // 卡片结构：**顶栏（关闭按钮）与缩略图区域严格互斥**。
            // ⚠️ 关闭按钮绝不能盖在 DwmThumbnail 的矩形上——DWM 缩略图由 DWM 合成画在窗口内容**之上**，
            // 任何 WPF 绘制的控件只要落进缩略图矩形就会被整个盖住（stage-manager 同款约束）。
            // 因此顶栏单独占一行，按钮右对齐 = 卡片右上角，与缩略图区不重叠。
            // cell 先声明（关闭按钮的回调要捕获它），内容随后组装。
            var cell = new Border
            {
                Background = CellIdleBackBrush,
                BorderBrush = CellIdleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Cursor = Cursors.Hand
            };

            var card = new Grid();
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bar = new Grid { Height = CloseBarHeight, Margin = new Thickness(0, 0, 0, 2) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var closeButton = BuildCloseButton(() => ClosePreviewedWindow(captured, cell));
            Grid.SetColumn(closeButton, 1);
            bar.Children.Add(closeButton);
            Grid.SetRow(bar, 0);
            card.Children.Add(bar);

            Grid.SetRow(thumb, 1);
            card.Children.Add(thumb);

            cell.Child = card;

            cell.MouseEnter += (_, _) => OpenPreview(captured);
            cell.MouseLeftButtonUp += (_, _) =>
            {
                // 点选 = 真正激活进入。
                // 【v2 契约】激活前必须 Cancel()：否则 End() 会先把被预览窗口收回最小化，
                // ActivateWindow 再把它还原 → 视觉上"闪一下"（WindowPeek 显式记录了这条）。
                _peek.Cancel();
                _peekTarget = IntPtr.Zero;
                ClosePreview();
                // 同 dock 运行区：MouseUp 处理中鼠标仍被本线程捕获，SetForegroundWindow 会被拒——延迟激活。
                // 带 UIPI 回退（任务管理器这类高完整性窗口直连唤不动，交给应用自己唤醒）。
                Dispatcher.BeginInvoke(
                    () => RunningAppDetector.ActivateWindowOrRelaunch(captured.Hwnd, captured.ExePath),
                    System.Windows.Threading.DispatcherPriority.Background);
                Close();
            };

            _cells.Add((cell, captured.Hwnd));
            wrap.Children.Add(cell);
        }

        var outer = new Border
        {
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.92 },
            BorderBrush = ThemeBrushes.Tint("ThemeForeground", 0.7),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = wrap
        };
        Content = outer;

        SourceInitialized += OnSourceInitialized;

        // 窗口显示后（Loaded）再定位：此时 ActualWidth/Height 已由 SizeToContent 结算完成。
        Loaded += (_, _) => PositionAbove();
        SizeChanged += (_, _) => PositionAbove();

        // 大图预览由 cell.MouseEnter 打开、PreviewWindow 自身 MouseLeave / 本浮层 MouseLeave 关闭
        // （不用全局命中测试：PreviewWindow 是独立顶层窗，生命周期由自身鼠标事件管理更稳）。
        MouseLeave += (_, _) => ClosePreview();

        // 任何退出路径（点选/宿主关闭/退出）都关闭大图预览层。
        Closed += (_, _) => ClosePreview();
    }

    // 关闭按钮所在顶栏高度：撑开一条与缩略图互斥的区域（按钮不可与缩略图矩形重叠）。
    private const double CloseBarHeight = 20;

    /// <summary>
    /// 卡片右上角的关闭按钮。
    /// 用 Border + TextBlock 手搓而非 Button：只需鼠标交互，且能精确控制
    /// `MouseLeftButtonUp + e.Handled = true`，阻止事件冒泡到 cell 的"点击激活"处理器
    /// （否则关窗口的同时还会去激活它，语义打架）。
    /// </summary>
    private static Border BuildCloseButton(Action onClick)
    {
        var idle = ThemeBrushes.Tint("ThemeForeground", 0.25);
        var hover = ThemeBrushes.Tint("StatusDanger", 0.9);

        var mark = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = idle,
            Child = mark,
            Cursor = Cursors.Hand,
            ToolTip = "关闭此窗口",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };

        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = idle;
        button.MouseLeftButtonUp += (_, e) =>
        {
            // 必须截断冒泡：cell 上挂的是"点击激活该窗口"，与关闭语义冲突。
            e.Handled = true;
            onClick();
        };

        return button;
    }

    /// <summary>
    /// 关闭缩略图对应的窗口：发 WM_CLOSE 优雅关闭（应用可提示保存/拒绝），并立刻把卡片从浮层移除。
    /// 视觉即时移除而非等窗口真的销毁——WM_CLOSE 是异步的，等它会有"点了没反应"的观感。
    /// </summary>
    private void ClosePreviewedWindow(RunningWindow window, Border cell)
    {
        try
        {
            // 窗口即将消失：关闭大图预览（预览一个不存在的窗口没有意义）。
            ClosePreview();

            DebugLog.Trace("Preview", $"close request hwnd=0x{(long)window.Hwnd:X} title={window.Title}");
            RunningAppDetector.CloseWindow(window.Hwnd);

            if (cell.Parent is Panel host)
            {
                host.Children.Remove(cell);
            }

            _cells.RemoveAll(c => c.Hwnd == window.Hwnd);

            // 最后一张也关掉了 → 浮层没有存在意义，直接收掉（dock 的看门狗由 Closed 事件收尾）。
            if (_cells.Count == 0)
            {
                Close();
            }
        }
        catch
        {
            // 关闭失败不阻断浮层。
        }
    }

    /// <summary>
    /// 悬停缩略图 → 打开（或切换到）该窗口的实时大图预览层。
    /// 目标相同则不重复打开；目标不同先关旧的再开新的。
    /// </summary>
    private void OpenPreview(RunningWindow window)
    {
        try
        {
            // 【v2 默认路径】原生 peek：让系统亮出**真窗口**，不再是我们的自绘层。
            if (UseNativePeek)
            {
                if (_peekTarget == window.Hwnd)
                {
                    return;
                }

                ClosePreview();
                _peekTarget = window.Hwnd;
                _peek.Begin(
                    window.Hwnd,
                    _callingHwnd != IntPtr.Zero
                        ? _callingHwnd
                        : new System.Windows.Interop.WindowInteropHelper(this).Handle);
                DebugLog.Trace("Peek", $"native peek begin hwnd=0x{(long)window.Hwnd:X} title={window.Title}");
                return;
            }

            if (_previewWindow is not null)
            {
                if (_previewWindow.Target == window.Hwnd)
                {
                    return;
                }
                ClosePreview();
            }

            _previewWindow = new PreviewWindow(window, _quality);
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            _previewWindow.Show();
            DebugLog.Trace("Preview", $"large preview open hwnd=0x{(long)window.Hwnd:X} title={window.Title}");
        }
        catch
        {
            // 预览层打开失败不阻断浮层（缩略图浮层仍可用）。
        }
    }

    /// <summary>关闭大图预览层（幂等）。</summary>
    private void ClosePreview()
    {
        try
        {
            // v2 路径：结束原生 peek（幂等；未在 peek 时无副作用）。
            // 用 End() 而非 Cancel()：正常关闭要**原样收回**被预览窗口的最小化状态。
            if (_peekTarget != IntPtr.Zero)
            {
                _peekTarget = IntPtr.Zero;
                _peek.End();
            }

            if (_previewWindow is null)
            {
                return;
            }

            var p = _previewWindow;
            _previewWindow = null;
            if (p.IsVisible)
            {
                p.Close();
            }
        }
        catch
        {
            // 关闭失败不阻断浮层。
        }
    }

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
                    NativeMethods.GWL_EXSTYLE,
                    NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE) | NativeMethods.WS_EX_TOOLWINDOW);
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
            var hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMonitor, ref info))
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

    // 运行项图标区域高度（与宿主构建容器时一致）。
    private const double DockItemHeight = 48.0;

}
