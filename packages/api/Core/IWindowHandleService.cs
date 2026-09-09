using System;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>主桌面窗口句柄服务。由宿主实现并 Provide，供 shell-core 取主窗口 HWND。</summary>
public interface IWindowHandleService
{
    /// <summary>主桌面窗口句柄。</summary>
    IntPtr Handle { get; }
}
