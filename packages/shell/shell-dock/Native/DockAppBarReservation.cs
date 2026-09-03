// BetterDesktop.Shell.Dock — AppBar 空间预留（Win32 SHAppBarMessage，底部 dock 专属）
// 与 shell-menu-bar/Native/AppBarReservation 同范式（ABM_NEW → QUERYPOS+SETPOS → ABM_REMOVE），
// 差异仅 edge=ABE_BOTTOM：dock 注册为底部 AppBar 后，explorer 自动把工作区上移，
// **最大化窗口/桌面图标不再覆盖 dock 条带**——dock 不需要置顶就能始终可见（cairoshell 同款）。
// 注意：APPBARDATA.rc 一律为物理像素（与 Window 逻辑坐标不同域），取自 GetWindowRect(hwnd)。

using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.WindowTracker;

namespace BetterDesktop.Shell.Dock.Native;

/// <summary>底部 AppBar 空间预留（dock 桌面避让）。</summary>
internal static class DockAppBarReservation
{
    private const uint AbmNew = 0x0000;
    private const uint AbmRemove = 0x0001;
    private const uint AbmQueryPos = 0x0002;
    private const uint AbmSetPos = 0x0003;
    private const uint AbeBottom = 0x0003;

    /// <summary>系统广播：工作区位置变化，AppBar 应重新申请位置。</summary>
    public const uint AbnPosChanged = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct AppbarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public int lParam;
    }

    /// <summary>AppBar 协商矩形（物理像素，与 GetWindowRect 同域）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppbarData pData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>窗口所在屏的物理工作区（GetMonitorInfo，物理像素，与协商矩形同域）。失败返回 false。</summary>
    public static bool GetMonitorWorkArea(IntPtr hwnd, out NativeRect work)
    {
        work = default;
        if (hwnd == IntPtr.Zero) return false;
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
        work = mi.rcWork;
        return true;
    }

    /// <summary>窗口所在屏的物理**整屏**矩形（rcMonitor，GetMonitorInfo，物理像素）。失败返回 false。
    /// dock 定位/协商以此为纵向基准（2026-09-02 定稿：dock 底边贴屏幕底边 − bottomMargin）——
    /// 整屏矩形不受 AppBar 自身抬升影响，天然免疫"协商→抬升→再定位"循环。</summary>
    public static bool GetMonitorBounds(IntPtr hwnd, out NativeRect monitor)
    {
        monitor = default;
        if (hwnd == IntPtr.Zero) return false;
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
        monitor = mi.rcMonitor;
        return true;
    }

    /// <summary>把窗口移动到协商矩形（物理像素，与 GetWindowRect 同域）。不动 Z 序、不激活。</summary>
    public static void MoveWindowTo(IntPtr hwnd, NativeRect rect)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>注册为底部 AppBar。成功返回 true；失败返回 false（调用方静默降级，不崩溃）。</summary>
    public static bool Register(IntPtr hwnd, uint callbackMessage)
    {
        if (hwnd == IntPtr.Zero) return false;
        var data = new AppbarData
        {
            cbSize = Marshal.SizeOf<AppbarData>(),
            hWnd = hwnd,
            uCallbackMessage = callbackMessage,
            uEdge = AbeBottom
        };
        var result = SHAppBarMessage(AbmNew, ref data);
        return result != IntPtr.Zero;
    }

    /// <summary>注销 AppBar（窗口关闭/空闲隐藏时必须调用，否则底部空间永久被占）。</summary>
    public static void Unregister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var data = new AppbarData
        {
            cbSize = Marshal.SizeOf<AppbarData>(),
            hWnd = hwnd,
            uEdge = AbeBottom
        };
        _ = SHAppBarMessage(AbmRemove, ref data);
    }

    /// <summary>
    /// 按调用方提供的**期望物理矩形**申请底部空间，返回系统协商后的矩形。
    /// ⚠️ 输入必须是物理像素，且 Top 必须已位于工作区之内：实测 Windows 的 QUERYPOS 只把
    /// rc.Bottom 拉回工作区底、**不回写 rc.Top**——若窗口被定位到工作区下方（历史 bug：定位用
    /// SystemParameters 域与窗口 PerMonitorV2 域不一致），协商会返回负高度（实测 W=769 H=-299），
    /// 条带永不生效。期望矩形由调用方基于 GetMonitorWorkArea 计算。
    /// 调用方必须把窗口摆到协商矩形（物理域 SetWindowPos），保证"窗口矩形 ≡ AppBar 声明矩形"恒等。
    /// 调用时机：注册后 / ABN_POSCHANGED / 窗口重排后。
    /// </summary>
    public static bool TryApplyPos(IntPtr hwnd, NativeRect desired, bool forceSetPos, out NativeRect agreed)
    {
        agreed = default;
        if (hwnd == IntPtr.Zero) return false;
        if (desired.Right - desired.Left <= 0 || desired.Bottom - desired.Top <= 0) return false;

        var data = new AppbarData
        {
            cbSize = Marshal.SizeOf<AppbarData>(),
            hWnd = hwnd,
            uEdge = AbeBottom,
            rc = desired
        };

        var query = SHAppBarMessage(AbmQueryPos, ref data);
        agreed = data.rc;

        // 防 ABN_POSCHANGED 自触发循环：SETPOS 会让系统重算工作区并广播 ABN_POSCHANGED，
        // 若每次协商都 SETPOS 会无限循环（实测 dock 被一路抬到屏幕顶）。仅在
        // (a) 从未 SETPOS（首次声明矩形）或 (b) 协商结果与窗口当前矩形不同 时才 SETPOS。
        var moved = true;
        if (!forceSetPos && GetWindowRect(hwnd, out var cur))
        {
            moved = Math.Abs(cur.Left - agreed.Left) > 1 || Math.Abs(cur.Top - agreed.Top) > 1 ||
                    Math.Abs(cur.Right - agreed.Right) > 1 || Math.Abs(cur.Bottom - agreed.Bottom) > 1;
        }
        if (moved)
        {
            _ = SHAppBarMessage(AbmSetPos, ref data);
        }

        // 诊断（负高度排查）：期望矩形 vs 协商后 rc vs 所在屏工作区
        try
        {
            var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            var hasMon = GetMonitorInfo(mon, ref mi);
            DebugLog.Trace("Dock",
                $"AppBar 协商诊断: query={(query != IntPtr.Zero)} " +
                $"desired=({desired.Left},{desired.Top},{desired.Right},{desired.Bottom}) W={desired.Right - desired.Left} H={desired.Bottom - desired.Top} " +
                $"agreed=({agreed.Left},{agreed.Top},{agreed.Right},{agreed.Bottom}) W={agreed.Right - agreed.Left} H={agreed.Bottom - agreed.Top} " +
                (hasMon ? $"work=({mi.rcWork.Left},{mi.rcWork.Top},{mi.rcWork.Right},{mi.rcWork.Bottom})" : "work=?"));
        }
        catch
        {
            // 诊断落盘失败不阻断协商
        }

        return query != IntPtr.Zero;
    }
}
