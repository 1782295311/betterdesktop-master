using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>基于主屏的桌面表面实现（v1 仅主屏，WPF 逻辑坐标）。</summary>
public sealed class DesktopSurface : IDesktopSurface
{
    private readonly IntPtr _handle;
    private readonly Rect _screenBounds;
    private readonly Rect _workArea;
    private readonly double _dpiScaleX;
    private readonly double _dpiScaleY;

    /// <summary>构造，基于主屏几何计算边界与 DPI。</summary>
    public DesktopSurface(IntPtr handle)
    {
        _handle = handle;
        _screenBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        _workArea = SystemParameters.WorkArea;

        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is { } target)
        {
            _dpiScaleX = target.TransformToDevice.M11;
            _dpiScaleY = target.TransformToDevice.M22;
        }
        else
        {
            _dpiScaleX = 1.0;
            _dpiScaleY = 1.0;
        }
    }

    /// <inheritdoc />
    public IntPtr Handle => _handle;

    /// <inheritdoc />
    public Rect ScreenBounds => _screenBounds;

    /// <inheritdoc />
    public Rect WorkArea => _workArea;

    /// <inheritdoc />
    public double DpiScaleX => _dpiScaleX;

    /// <inheritdoc />
    public double DpiScaleY => _dpiScaleY;
}
