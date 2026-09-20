using System;
using System.Runtime.InteropServices;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Native;

/// <summary>窗口句柄 / 前台 / 点击穿透原生接口。</summary>
internal static class WindowApi
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out MonitorApi.RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    /// <summary>窗口在虚拟屏上的像素矩形（GetWindowRect，含负坐标）。</summary>
    public static PixelRect? GetWindowPixelRect(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var r))
        {
            return null;
        }
        return PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>返回光标下最上层可见窗口（排除我们自己的贴图/覆盖层由调用方过滤）。</summary>
    public static IntPtr WindowAt(int x, int y) => WindowFromPoint(new POINT { X = x, Y = y });

    /// <summary>窗口是否可见。</summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>前台窗口（用于判断是否被我们抢了前台 / 点击穿透基准）。</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static bool SetForeground(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>窗口是否已开启点击穿透（WS_EX_TRANSPARENT）。</summary>
    public static bool IsClickThrough(IntPtr hwnd) => (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;

    /// <summary>
    /// 设置点击穿透（贴图开关）：WS_EX_TRANSPARENT + WS_EX_LAYERED + WS_EX_TOOLWINDOW（不进任务栏）。
    /// 注意：WPF 窗口应通过 hwnd 源设置，避免 WPF 内部样式覆盖；返回是否成功。
    /// </summary>
    public static bool SetClickThrough(IntPtr hwnd, bool enabled)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (enabled)
        {
            style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
        }
        return SetWindowLong(hwnd, GWL_EXSTYLE, style) != 0;
    }
}
