// BetterDesktop.Shell.MenuBar — 屏幕几何与 DPI 换算
//
// 【为什么需要这个文件】
// 原实现有两处单位混用，是当前弹窗错位的根因：
//   1) Visual.PointToScreen() 返回**物理像素**；Window.Left/Top 与 SystemParameters.WorkArea 是
//      **WPF 逻辑单位（DIP, 96dpi 基准）**。原 PopupAnchor 把两者直接相加，
//      在非 100% 缩放的显示器上弹窗会整体偏移。
//   2) SystemParameters.WorkArea 只描述**主屏**。多显示器下，副屏弹窗会被回钳到主屏边界，
//      表现为"在副屏点菜单栏，面板却弹到主屏边上"。
//
// 本文件提供两个能力，且统一以**逻辑单位**为准（与 Window.Left/Top 同域，可直接运算）：
//   - ToLogical()：把 PointToScreen 得到的物理点换算成逻辑点
//   - GetWorkArea()：取**指定点所在显示器**的工作区（而非固定主屏）
// 不引入 System.Windows.Forms / Microsoft.Win32.SystemEvents 包依赖，直接 P/Invoke。

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>屏幕几何与 DPI 换算工具（全部以 WPF 逻辑单位为准）。</summary>
internal static class MenuBarScreen
{
    private const uint MonDefaultToNearest = 0x00000002;

    /// <summary>
    /// 把 <c>Visual.PointToScreen</c> 得到的**物理像素**坐标换算为**逻辑单位**。
    /// 换算失败（未挂 PresentationSource / headless）时原样返回，保证不抛。
    /// </summary>
    public static Point ToLogical(Visual visual, Point physicalPoint)
    {
        try
        {
            var source = PresentationSource.FromVisual(visual);
            var transform = source?.CompositionTarget?.TransformFromDevice;
            // TransformFromDevice 是 Matrix?（可空 struct），必须取值后再 Transform
            return transform is { } m ? m.Transform(physicalPoint) : physicalPoint;
        }
        catch
        {
            // headless / 预览工厂场景：无 PresentationSource，原样返回（不改变既有行为）
            return physicalPoint;
        }
    }

    /// <summary>
    /// 取包含指定**物理点**的显示器的工作区，返回**逻辑单位**的 Rect。
    /// 找不到显示器时回落到 <see cref="SystemParameters.WorkArea"/>。
    /// </summary>
    public static Rect GetWorkArea(Point physicalPoint)
    {
        try
        {
            var pt = new NativePoint { X = (int)Math.Round(physicalPoint.X), Y = (int)Math.Round(physicalPoint.Y) };
            var hMonitor = MonitorFromPoint(pt, MonDefaultToNearest);
            if (hMonitor != IntPtr.Zero)
            {
                var mi = new MonitorInfo();
                mi.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    // rcWork 是物理像素；用该显示器自身的 DPI 换算到逻辑单位
                    var scale = GetScaleForPoint(pt);
                    return new Rect(
                        mi.rcWork.Left / scale,
                        mi.rcWork.Top / scale,
                        (mi.rcWork.Right - mi.rcWork.Left) / scale,
                        (mi.rcWork.Bottom - mi.rcWork.Top) / scale);
                }
            }
        }
        catch
        {
            // P/Invoke 失败不致命，走主屏回落
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>主屏工作区（逻辑单位）。菜单栏默认停驻于此。</summary>
    public static Rect PrimaryWorkArea => SystemParameters.WorkArea;

    /// <summary>取指定**物理点**所在显示器的 DPI 缩放系数（1.0 = 100%）。取不到时回落 1.0。</summary>
    public static double GetScale(Point physicalPoint)
    {
        return GetScaleForPoint(new NativePoint
        {
            X = (int)Math.Round(physicalPoint.X),
            Y = (int)Math.Round(physicalPoint.Y)
        });
    }

    /// <summary>取指定物理点所在显示器的 DPI 缩放系数（1.0 = 100%）。</summary>
    private static double GetScaleForPoint(NativePoint pt)
    {
        // ShCore.GetDpiForMonitor 在 Win8.1+ 可用；失败一律按 1.0 处理（与原行为一致）。
        try
        {
            var hMonitor = MonitorFromPoint(pt, MonDefaultToNearest);
            if (hMonitor != IntPtr.Zero && GetDpiForMonitor(hMonitor, DpiType.Effective, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // 忽略：老系统无 ShCore
        }

        return 1.0;
    }

    private const int S_OK = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("Shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, DpiType dpiType, out uint dpiX, out uint dpiY);

    private enum DpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }
}
