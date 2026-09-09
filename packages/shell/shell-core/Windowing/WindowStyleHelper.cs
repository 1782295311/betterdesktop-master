using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Windowing;

/// <summary>窗口样式辅助（Win32）。</summary>
public static class WindowStyleHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>将窗口设为浮动、不抢焦点（WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW）。</summary>
    public static void MakeFloatingNoActivate(IntPtr hWnd)
    {
        int style = NativeMethods.GetWindowLong(hWnd, GWL_EXSTYLE);
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hWnd, GWL_EXSTYLE, style);
    }
}
