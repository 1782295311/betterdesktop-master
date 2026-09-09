// BetterDesktop.Shell.StartMenu — MonitorInterop
// 监视器 P/Invoke 收口：获取鼠标光标所在监视器的可用工作区（物理像素）。
// 供开始菜单定位（多显示器 / 跨 DPI）使用；P/Invoke 声明已统一收口到 shell-core/Native（NativeMethods）。

using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.StartMenu.Native;

/// <summary>
/// 监视器互操作：取光标所在监视器的工作区（不含任务栏等保留区域）。
/// 物理像素返回；调用方需按目标 DPI 缩放到 WPF 设备无关单位（DIP）。
/// </summary>
internal static class MonitorInterop
{
    /// <summary>监视器工作区矩形（物理像素，相对虚拟屏幕原点，可为负）。</summary>
    public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// 光标所在监视器的完整信息：物理工作区 + 该监视器的有效 DPI（物理像素 / DIP 换算基准）。
    /// DpiX/DpiY 为该监视器真实、与窗口无关的缩放基准；DPI 换算必须用它，不能复用窗口滞后 DPI，
    /// 否则多显示器 / 跨 DPI 窗口会被压得过低、底部探出屏幕。
    /// </summary>
    public readonly record struct MonitorInfo(WorkArea Area, int DpiX, int DpiY);

    /// <summary>
    /// 取鼠标光标所在监视器的可用工作区及其有效 DPI；失败/异常时返回 null（由调用方兜底）。
    /// </summary>
    public static MonitorInfo? GetMonitorInfoUnderCursor()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                return null;
            }

            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.MONITORINFO();
            info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>();
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            var wa = info.rcWork;
            var (dpiX, dpiY) = GetEffectiveDpi(monitor);
            return new MonitorInfo(new WorkArea(wa.Left, wa.Top, wa.Right, wa.Bottom), dpiX, dpiY);
        }
        catch
        {
            // P/Invoke 失败不冒泡（M10），由调用方回退主工作区。
            return null;
        }
    }

    /// <summary>
    /// 取监视器有效 DPI（shcore GetDpiForMonitor，Win8.1+ 每显示器 DPI）。失败时返回 0，
    /// 由调用方回退窗口自身 DPI（旧系统兜底，M10）。
    /// </summary>
    private static (int X, int Y) GetEffectiveDpi(IntPtr hMonitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0)
            {
                return ((int)dpiX, (int)dpiY);
            }
        }
        catch
        {
            // shcore 不可用（极老系统）→ 返回 0，调用方用窗口 DPI 兜底（M10）。
        }

        return (0, 0);
    }
}
