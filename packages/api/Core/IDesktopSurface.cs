using System.Windows;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>桌面表面几何信息（WPF 逻辑坐标，v1 仅主屏）。</summary>
public interface IDesktopSurface
{
    /// <summary>主窗口句柄。</summary>
    IntPtr Handle { get; }

    /// <summary>主屏逻辑边界（WPF 单位，原点左上）。</summary>
    Rect ScreenBounds { get; }

    /// <summary>主屏工作区（不含任务栏，WPF 单位）。</summary>
    Rect WorkArea { get; }

    /// <summary>水平 DPI 缩放（device / logical）。</summary>
    double DpiScaleX { get; }

    /// <summary>垂直 DPI 缩放（device / logical）。</summary>
    double DpiScaleY { get; }
}
