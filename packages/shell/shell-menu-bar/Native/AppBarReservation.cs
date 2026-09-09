// BetterDesktop.Shell.MenuBar — AppBar 空间预留（Win32 SHAppBarMessage）
// 目的：把菜单栏注册为顶部 AppBar，explorer 会自动把工作区下移，
// 桌面图标 / 最大化窗口都会让出菜单栏条带（不再被盖住）。
// 生命周期：Register（ABM_NEW）→ ApplyPos（ABM_QUERYPOS + ABM_SETPOS）→ Unregister（ABM_REMOVE）。
// 系统在工作区变化时向回调窗口发 ABN_POSCHANGED（经 WndProc 转发），届时重新 ApplyPos。
// 注意：APPBARDATA.rc 一律为**物理像素**（与 Window 逻辑坐标不同域），取自 GetWindowRect(hwnd)。

using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.MenuBar.Native;

/// <summary>顶部 AppBar 空间预留（菜单栏桌面避让）。</summary>
internal static class AppBarReservation
{
    private const uint AbmNew = 0x0000;
    private const uint AbmRemove = 0x0001;
    private const uint AbmQueryPos = 0x0002;
    private const uint AbmSetPos = 0x0003;
    private const uint AbeTop = 0x0001;

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


    /// <summary>注册为顶部 AppBar。成功返回 true；失败返回 false（调用方静默降级，不崩溃）。</summary>
    public static bool Register(IntPtr hwnd, uint callbackMessage)
    {
        if (hwnd == IntPtr.Zero) return false;
        var data = new NativeMethods.AppbarData
        {
            cbSize = Marshal.SizeOf<NativeMethods.AppbarData>(),
            hWnd = hwnd,
            uCallbackMessage = callbackMessage,
            uEdge = AbeTop
        };
        var result = NativeMethods.SHAppBarMessage(AbmNew, ref data);
        return result != IntPtr.Zero;
    }

    /// <summary>注销 AppBar（窗口关闭时必须调用，否则顶部空间永久被占）。</summary>
    public static void Unregister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var data = new NativeMethods.AppbarData
        {
            cbSize = Marshal.SizeOf<NativeMethods.AppbarData>(),
            hWnd = hwnd,
            uEdge = AbeTop
        };
        _ = NativeMethods.SHAppBarMessage(AbmRemove, ref data);
    }

    /// <summary>
    /// 按窗口当前物理矩形申请顶部空间，返回**系统协商后**的矩形。
    /// 调用方必须把窗口摆到协商矩形（物理→逻辑换算后 SetWindowPos），保证
    /// "窗口矩形 ≡ AppBar 声明矩形"恒等——这是 cairoshell AppBarWindow 的核心范式：
    /// AppBar 先协商、后定位，绝不读 WorkArea 给自己定位（因果倒置会随坏状态漂移、无法自愈）。
    /// 调用时机：注册后 / ABN_POSCHANGED / 窗口重排后。
    /// </summary>
    public static bool TryApplyPos(IntPtr hwnd, out NativeRect agreed)
    {
        agreed = default;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

        var data = new NativeMethods.AppbarData
        {
            cbSize = Marshal.SizeOf<NativeMethods.AppbarData>(),
            hWnd = hwnd,
            uEdge = AbeTop,
            rc = new NativeMethods.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom }
        };

        var query = NativeMethods.SHAppBarMessage(AbmQueryPos, ref data); // 系统调整 data.rc（贴顶、避开其他 AppBar）
        _ = NativeMethods.SHAppBarMessage(AbmSetPos, ref data);           // 采用调整后的 rc 并触发工作区重算
        agreed = new NativeRect { Left = data.rc.Left, Top = data.rc.Top, Right = data.rc.Right, Bottom = data.rc.Bottom };
        return query != IntPtr.Zero;
    }
}
