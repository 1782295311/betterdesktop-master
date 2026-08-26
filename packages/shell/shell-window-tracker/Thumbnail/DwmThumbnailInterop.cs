using System.Runtime.InteropServices;

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
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int x;
    public int y;
}

/// <summary>
/// DWM 缩略图属性结构（与 DWM_THUMBNAIL_PROPERTIES 内存布局一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DwmThumbnailProperties
{
    public uint dwFlags;
    public NativeRect rcDestination;
    public NativeRect rcSource;
    public byte opacity;

    [MarshalAs(UnmanagedType.Bool)]
    public bool fVisible;

    [MarshalAs(UnmanagedType.Bool)]
    public bool fSourceClientAreaOnly;
}

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
        hr = NativeDwmRegisterThumbnail(hwndDestination, hwndSource, out phThumbnailId);
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
    internal static bool DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DwmThumbnailProperties ptnProperties)
    {
        return NativeDwmUpdateThumbnailProperties(hThumbnailId, ref ptnProperties) == 0;
    }

    /// <summary>
    /// 取得源窗口的原始尺寸（逻辑像素）。
    /// </summary>
    internal static bool DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out NativeSize size)
    {
        size = default;
        return NativeDwmQueryThumbnailSourceSize(hThumbnailId, out size) == 0;
    }

    /// <summary>
    /// 取消注册缩略图。
    /// </summary>
    internal static void DwmUnregisterThumbnail(IntPtr hThumbnailId)
    {
        if (hThumbnailId != IntPtr.Zero)
        {
            _ = NativeDwmUnregisterThumbnail(hThumbnailId);
        }
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmRegisterThumbnail")]
    private static extern int NativeDwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmUnregisterThumbnail")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeDwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmUpdateThumbnailProperties")]
    private static extern int NativeDwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DwmThumbnailProperties ptnProperties);

    [DllImport("dwmapi.dll", EntryPoint = "DwmQueryThumbnailSourceSize")]
    private static extern int NativeDwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out NativeSize psize);

    /// <summary>
    /// DWM 合成是否启用。未启用（如基础主题/某些远程会话）时不能注册实时缩略图，
    /// 调用方应优雅降级为窗口列表，而非尝试注册导致失败。
    /// 声明对齐真源 DwmApi.DwmIsCompositionEnabled：PreserveSig=false 直接返回 BOOL（无 out 参数）。
    /// </summary>
    internal static bool DwmIsCompositionEnabled()
    {
        try
        {
            return NativeDwmIsCompositionEnabled();
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmIsCompositionEnabled", PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeDwmIsCompositionEnabled();
}
