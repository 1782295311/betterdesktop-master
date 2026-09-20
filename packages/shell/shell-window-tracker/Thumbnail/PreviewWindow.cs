using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.WindowTracker.Native;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>
/// 悬停缩略图的「实时大图预览层」——替代 DWM 透明化 Aero Peek。
///
/// 【2026-09-12 决策】DwmActivateLivePreview（cairoshell/ManagedShell 方案）会透明化除目标外
/// 所有顶层窗口（含 dock 与缩略图浮层；EXCLUDED_FROM_PEEK=12 实测无效，系统任务栏可见是
/// 系统窗口特例）。用户实测"只能预览无法选择进入" + 浮层视觉缩小/消失。改本方案：
/// DwmRegisterThumbnail 把目标窗口**实时内容**渲染成大图（不透明化任何窗口），
/// dock / 缩略图浮层在预览期间保持可见可点；点击预览层即激活进入目标窗口。
/// 效果近似 Aero Peek（实时内容 + 大尺寸），且无"透明化屏蔽"副作用。
/// </summary>
internal sealed class PreviewWindow : Window
{
    private readonly IntPtr _hwnd;
    private readonly string _exePath;
    private readonly DwmThumbnail _thumb;

    /// <summary>被预览的目标窗口句柄。</summary>
    public IntPtr Target => _hwnd;

    public PreviewWindow(RunningWindow window, ThumbnailQuality quality)
    {
        _hwnd = window.Hwnd;
        _exePath = window.ExePath ?? string.Empty;

        ShowActivated = false;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 大图尺寸：按源窗口宽高比等比缩放到工作区 ~65%（宽）/ ~75%（高）上限；
        // 拿不到源矩形（极端时序）时退固定比例。
        var work = SystemParameters.WorkArea;
        var maxW = work.Width * 0.65;
        var maxH = work.Height * 0.75;
        double pw, ph;
        if (NativeMethods.GetWindowRect(_hwnd, out var rc) && rc.Right - rc.Left > 0 && rc.Bottom - rc.Top > 0)
        {
            var sw = rc.Right - rc.Left;
            var sh = rc.Bottom - rc.Top;
            var scale = Math.Min(maxW / sw, maxH / sh);
            pw = Math.Max(1.0, sw * scale);
            ph = Math.Max(1.0, sh * scale);
        }
        else
        {
            pw = maxW;
            ph = maxH;
        }

        _thumb = new DwmThumbnail(quality)
        {
            Width = pw,
            Height = ph
        };

        // 注册时序对齐 ThumbnailWindow：控件挂载到已显示窗口后、ApplicationIdle 错峰注册，
        // 此时 PresentationSource 必然有效（DwmRegisterThumbnail 的 destination 句柄有效）。
        _thumb.Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (_thumb.IsLoaded && IsVisible)
                {
                    _thumb.SourceWindowHandle = _hwnd;
                }
            }));
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.9 },
            BorderBrush = ThemeBrushes.Tint("ThemeForeground", 0.7),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(3),
            Child = _thumb
        };
        Content = border;

        // 点击预览层 = 真正激活进入目标窗口（延迟激活，对齐浮层点选逻辑）。
        MouseLeftButtonUp += (_, _) => ActivateAndClose();
        // 鼠标离开预览层 → 关闭（宿主 cell.MouseLeave 不负责关闭，避免移入预览层的时序竞争）。
        MouseLeave += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 大图预览层同样不可出现在 Alt-Tab / 任务栏。
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
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
            // 隐藏失败不阻断预览。
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 屏幕中央定位（工作区中心 - 内容尺寸/2）。
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + (work.Height - ActualHeight) / 2;
    }

    private void ActivateAndClose()
    {
        // 与浮层点选同构：MouseUp 处理中鼠标被捕获，SetForegroundWindow 会被拒 → 延迟激活。
        // 带 UIPI 回退（高完整性窗口直连唤不动，交给应用自己唤醒）。
        Dispatcher.BeginInvoke(
            () => RunningAppDetector.ActivateWindowOrRelaunch(_hwnd, _exePath),
            DispatcherPriority.Background);
        Close();
    }
}
