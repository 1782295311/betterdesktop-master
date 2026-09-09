using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>
/// 圆形矩形结构（与 Win32 RECT 内存布局一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public NativeRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// 尺寸结构（与 Win32 SIZE 内存布局一致）。
/// </summary>
/// <summary>
/// DWM 缩略图属性结构（与 DWM_THUMBNAIL_PROPERTIES 内存布局一致）。
/// </summary>
/// <summary>
/// DWM 缩略图最小声明。
/// 参考 cairoshell 的 DwmInterop（ManagedShell-free 自包含实现），
/// 用于在窗口预览弹出窗中渲染其它窗口的实时缩略图。
/// 自 shell-dock 下沉（步骤5），供 Dock 预览、未来任务栏/开始菜单共用。
/// </summary>
internal static class DwmThumbnailInterop
{
    // DWM_THUMBNAIL_PROPERTIES.dwFlags 取值
    internal const uint DwmTnpRectDestination = 0x1;
    internal const uint DwmTnpRectSource = 0x2;
    internal const uint DwmTnpOpacity = 0x4;
    internal const uint DwmTnpVisible = 0x8;
    internal const uint DwmTnpSourceClientAreaOnly = 0x10;

    /// <summary>
    /// 在目标窗口内部注册对源窗口的缩略图，返回缩略图句柄。
    /// </summary>
    internal static bool DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId, out int hr)
    {
        phThumbnailId = IntPtr.Zero;
        hr = NativeMethods.DwmRegisterThumbnail(hwndDestination, hwndSource, out phThumbnailId);
        return hr == 0;
    }

    /// <summary>
    /// 在目标窗口内部注册对源窗口的缩略图，返回缩略图句柄（兼容旧签名）。
    /// </summary>
    internal static bool DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId)
    {
        return DwmRegisterThumbnail(hwndDestination, hwndSource, out phThumbnailId, out _);
    }

    /// <summary>
    /// 更新缩略图的显示属性（可见性、位置、不透明度）。
    /// </summary>
    internal static bool DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref NativeMethods.DwmThumbnailProperties ptnProperties)
    {
        return NativeMethods.DwmUpdateThumbnailProperties(hThumbnailId, ref ptnProperties) == 0;
    }

    /// <summary>
    /// 取得源窗口的原始尺寸（逻辑像素）。
    /// </summary>
    internal static bool DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out NativeMethods.DwmSize size)
    {
        size = default;
        return NativeMethods.DwmQueryThumbnailSourceSize(hThumbnailId, out size) == 0;
    }

    /// <summary>
    /// 取消注册缩略图。
    /// </summary>
    internal static void DwmUnregisterThumbnail(IntPtr hThumbnailId)
    {
        if (hThumbnailId != IntPtr.Zero)
        {
            _ = NativeMethods.DwmUnregisterThumbnail(hThumbnailId);
        }
    }





    /// <summary>
    /// DWM 合成是否启用。未启用（如基础主题/某些远程会话）时不能注册实时缩略图，
    /// 调用方应优雅降级为窗口列表，而非尝试注册导致失败。
    /// 【F8/V9 修复（7434 声明纪律）】真源签名是 HRESULT DwmIsCompositionEnabled(BOOL* pfEnabled)——
    /// 唯一输出在 out 参数；此前 PreserveSig=false + 无 out 声明把 HRESULT 当返回值，
    /// S_OK(0)→false 恒成立 → 缩略图永久失效。正确写法：out bool 拿状态，int 返回值判调用失败。
    /// </summary>
    internal static bool DwmIsCompositionEnabled()
    {
        try
        {
            return NativeMethods.DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        }
        catch
        {
            return false;
        }
    }

}
