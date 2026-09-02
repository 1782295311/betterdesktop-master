// BetterDesktop.Shell.Dock — DockWindow 的底部 AppBar 接线（partial）
// dock 注册为底部 AppBar 后 explorer 自动上移工作区，最大化窗口/桌面图标不再覆盖 dock——
// dock 无需置顶即可常驻可见（"不要盖在窗口上"的正解：不是被盖住，而是窗口根本不与它重叠）。
// 生命周期：OnSourceInitialized Register → 布局后首次协商 → ABN_POSCHANGED 重申请 → OnClosed Unregister。
// 空闲隐藏（SetDockVisible(false)）时 Unregister 释放条带，唤出时重新 Register（见 SetDockVisible）。

using System;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Shell.Dock.Native;
using BetterDesktop.Shell.WindowTracker;

namespace BetterDesktop.Shell.Dock;

public partial class DockWindow
{
    private const int AppBarCallbackMessage = 0x8101;   // 与菜单栏 0x8100 区分
    private const uint AbnPosChanged = 0x0001;
    private HwndSource? _appBarHwndSource;
    private bool _appBarRegistered;
    private bool _appBarSetPosDone;

    /// <summary>注册前缓存的工作区（物理像素，**不含 dock 自己**）。
    /// ⚠️ dock 注册为底部 AppBar 后，系统会把工作区抬升到 dock 顶（work.Bottom = dock.Top）；
    /// 若定位读实时工作区，会形成"协商→抬升→再定位→再抬升"循环，dock 被一路抬到屏幕顶（实测回归）。
    /// 因此定位/协商一律基于这份注册前缓存。</summary>
    private DockAppBarReservation.NativeRect _appBarWorkArea;

    /// <summary>句柄就绪：挂消息钩子 + 缓存工作区 + 注册底部 AppBar。
    /// ⚠️ 首次协商**不在这里做**：此时窗口尚未布局（SizeToContent 尺寸未定），
    /// 用无效矩形协商会得到负高度，回写 Height 抛异常使插件加载失败（实测回归）。
    /// 首次协商由 Loaded 后的布局定位路径（OnLoadedCore → SyncAppBarPosition）触发。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _appBarHwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _appBarHwndSource?.AddHook(AppBarWndProc);

        if (_appBarHwndSource is not null)
        {
            var hwnd = _appBarHwndSource.Handle;
            // 注册前缓存"不含 dock 的工作区"——注册后 GetMonitorWorkArea 返回的已含 dock 的抬升
            _ = DockAppBarReservation.GetMonitorWorkArea(hwnd, out _appBarWorkArea);
            _appBarRegistered = DockAppBarReservation.Register(hwnd, AppBarCallbackMessage);
            _appBarSetPosDone = false;
            DebugLog.Trace("Dock", $"AppBar 注册: {_appBarRegistered} (work={_appBarWorkArea.Left},{_appBarWorkArea.Top},{_appBarWorkArea.Right},{_appBarWorkArea.Bottom})");
        }
    }

    /// <summary>AppBar 协商定位：先按底部居中落位，再以"期望物理矩形"（基于所在屏物理工作区）向系统申请，
    /// 协商结果用 SetWindowPos 物理应用，WPF 侧只回写 Left/Top（Width/Height 由 SizeToContent 布局决定）。
    /// 未注册（AppBar 失败降级）时退化为纯定位。
    /// ⚠️ 回写协商矩形前必须校验宽高有效（协商可能给出异常矩形，负 Height 会抛异常）。
    /// ⚠️ 协商输入必须用期望矩形而非 GetWindowRect 当前位置：窗口被 SystemParameters 域误导定位到
    /// 工作区下方时，QUERYPOS 只把 Bottom 拉回工作区底而 Top 不动 → 负高度（实测 W=769 H=-299），条带永不生效。</summary>
    private void SyncAppBarPosition()
    {
        PositionToBottomCenter();

        if (!_appBarRegistered || _appBarHwndSource is null)
        {
            return;
        }

        // 窗口尺寸未就绪（SizeToContent 尚未算出）时跳过协商，等 OnSizeChanged 再来
        if (ActualWidth <= 0 || ActualHeight <= 0 || !IsVisible)
        {
            return;
        }

        var hwnd = _appBarHwndSource.Handle;
        var transform = _appBarHwndSource.CompositionTarget?.TransformToDevice ?? default;
        var scale = transform.M11 > 0 ? transform.M11 : 1.0; // 物理像素 / WPF 逻辑

        // 期望矩形（物理域）：底部居中 + 距"注册前缓存工作区"底留白 BottomMargin。
        // ⚠️ 用缓存而非实时 GetMonitorWorkArea——实时值已含 dock 自己的抬升，会导致协商循环。
        var work = _appBarWorkArea;
        if (work.Right - work.Left <= 0 || work.Bottom - work.Top <= 0)
        {
            // 缓存无效（极端时序：未注册/窗口尚未映射屏）→ 实时查，仅本次兜底
            if (!DockAppBarReservation.GetMonitorWorkArea(hwnd, out work))
            {
                return;
            }
        }

        var pw = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        var ph = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        var margin = (int)Math.Round(_layout.BottomMargin * scale);
        var bottom = Math.Max(work.Top + ph, work.Bottom - margin);
        var left = work.Left + Math.Max(0, (work.Right - work.Left - pw) / 2);
        var desired = new DockAppBarReservation.NativeRect
        {
            Left = left,
            Top = bottom - ph,
            Right = left + pw,
            Bottom = bottom
        };

        if (DockAppBarReservation.TryApplyPos(hwnd, desired, !_appBarSetPosDone, out var agreed))
        {
            var w = agreed.Right - agreed.Left;
            var h = agreed.Bottom - agreed.Top;
            if (w <= 0 || h <= 0)
            {
                DebugLog.Trace("Dock", $"AppBar 协商矩形无效 (W={w} H={h})，跳过回写");
                return;
            }

            _appBarSetPosDone = true;

            // 物理定位：不经 WPF Left/Top 属性（属性赋值会按窗口 DPI 再换算一次，域混乱时二次错位）
            DockAppBarReservation.MoveWindowTo(hwnd, agreed);

            // WPF 逻辑同步：只回写 Left/Top，写 Width/Height 会破坏 SizeToContent 自适应
            var leftLog = agreed.Left / scale;
            var topLog = agreed.Top / scale;
            if (Math.Abs(Left - leftLog) > 0.5 || Math.Abs(Top - topLog) > 0.5)
            {
                Left = leftLog;
                Top = topLog;
                DebugLog.Trace("Dock", $"AppBar 协商回写: L={leftLog:F0} T={topLog:F0} (物理 {agreed.Left},{agreed.Top},{agreed.Right},{agreed.Bottom})");
            }
        }
    }

    private IntPtr AppBarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBarCallbackMessage && (uint)wParam == AbnPosChanged)
        {
            // 工作区变化（其他 AppBar/任务栏移动）：重新申请位置
            SyncAppBarPosition();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>确保 AppBar 已注册（空闲隐藏时被释放，唤出时重注册）。</summary>
    private void EnsureAppBar()
    {
        if (_appBarRegistered || _appBarHwndSource is null)
        {
            if (_appBarRegistered) SyncAppBarPosition();
            return;
        }

        _appBarRegistered = DockAppBarReservation.Register(_appBarHwndSource.Handle, AppBarCallbackMessage);
        _appBarSetPosDone = false;
        DebugLog.Trace("Dock", $"AppBar 重注册: {_appBarRegistered}");
    }

    /// <summary>释放 AppBar 条带（空闲隐藏时调用）：底部空间归还系统，桌面/窗口可用全屏。</summary>
    private void ReleaseAppBar()
    {
        if (!_appBarRegistered || _appBarHwndSource is null)
        {
            return;
        }

        DockAppBarReservation.Unregister(_appBarHwndSource.Handle);
        _appBarRegistered = false;
        _appBarSetPosDone = false;
        DebugLog.Trace("Dock", "AppBar 已释放（空闲隐藏）");
    }

    /// <summary>窗口关闭必须注销 AppBar，否则底部空间永久被占。</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_appBarRegistered && _appBarHwndSource is not null)
        {
            DockAppBarReservation.Unregister(_appBarHwndSource.Handle);
            _appBarRegistered = false;
        }
        base.OnClosed(e);
    }
}
