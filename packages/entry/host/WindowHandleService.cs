using System;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Host;

/// <summary>主桌面窗口句柄服务实现（宿主构造时注入 HWND）。</summary>
public sealed class WindowHandleService : IWindowHandleService
{
    private readonly IntPtr _handle;

    /// <summary>构造。</summary>
    public WindowHandleService(IntPtr handle)
    {
        _handle = handle;
    }

    /// <inheritdoc />
    public IntPtr Handle => _handle;
}
