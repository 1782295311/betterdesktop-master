using System;
using System.Windows;
using BetterDesktop.Shell.WindowTracker.Contracts;
using BetterDesktop.Shell.WindowTracker.Thumbnail;

namespace BetterDesktop.Shell.WindowTracker.Services;

/// <summary>
/// DWM 缩略图服务实现：薄封装 <see cref="DwmThumbnailInterop"/> 的原生注册/更新/注销。
/// 全部方法静默容错：句柄无效 / DWM 未合成时降级（M10），不向调用方抛异常。
/// </summary>
public sealed class WindowThumbnailService : IWindowThumbnailService
{
    /// <inheritdoc />
    public IntPtr RegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource)
    {
        if (hwndDestination == IntPtr.Zero || hwndSource == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (!DwmThumbnailInterop.DwmIsCompositionEnabled())
        {
            return IntPtr.Zero;
        }

        return DwmThumbnailInterop.DwmRegisterThumbnail(hwndDestination, hwndSource, out var thumb)
            ? thumb
            : IntPtr.Zero;
    }

    /// <inheritdoc />
    public void UpdateThumbnail(IntPtr thumbnail, Rect destinationRect, byte opacity)
    {
        if (thumbnail == IntPtr.Zero)
        {
            return;
        }

        var props = new DwmThumbnailProperties
        {
            fVisible = true,
            dwFlags = DwmThumbnailInterop.DwmTnpVisible |
                      DwmThumbnailInterop.DwmTnpRectDestination |
                      DwmThumbnailInterop.DwmTnpOpacity,
            opacity = opacity,
            rcDestination = new NativeRect(
                (int)destinationRect.X,
                (int)destinationRect.Y,
                (int)(destinationRect.X + destinationRect.Width),
                (int)(destinationRect.Y + destinationRect.Height))
        };

        DwmThumbnailInterop.DwmUpdateThumbnailProperties(thumbnail, ref props);
    }

    /// <inheritdoc />
    public void UnregisterThumbnail(IntPtr thumbnail)
    {
        DwmThumbnailInterop.DwmUnregisterThumbnail(thumbnail);
    }
}
