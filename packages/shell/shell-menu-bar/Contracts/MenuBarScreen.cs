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
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>屏幕几何与 DPI 换算工具（全部以 WPF 逻辑单位为准）。</summary>
internal static class MenuBarScreen
{
    /// <summary>
    /// 把 <c>Visual.PointToScreen</c> 得到的**物理像素**坐标换算为**逻辑单位**。
    /// 换算失败（未挂 PresentationSource / headless）时原样返回，保证不抛。
    /// 实现已上提 shell-core/Windows/PopupPositioningService.ToScreenDipFromPhysical（P0-2/B1 收口）。
    /// </summary>
    public static Point ToLogical(Visual visual, Point physicalPoint)
    {
        return PopupPositioningService.ToScreenDipFromPhysical(physicalPoint, visual);
    }

    /// <summary>
    /// 取包含指定**物理点**的显示器的工作区，返回**逻辑单位**的 Rect。
    /// 找不到显示器时回落到 <see cref="SystemParameters.WorkArea"/>。
    /// </summary>
    public static Rect GetWorkArea(Point physicalPoint)
    {
        try
        {
            var pt = new NativeMethods.POINT { X = (int)Math.Round(physicalPoint.X), Y = (int)Math.Round(physicalPoint.Y) };
            var hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var mi = new NativeMethods.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
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
        return GetScaleForPoint(new NativeMethods.POINT
        {
            X = (int)Math.Round(physicalPoint.X),
            Y = (int)Math.Round(physicalPoint.Y)
        });
    }

    /// <summary>取指定物理点所在显示器的 DPI 缩放系数（1.0 = 100%）。</summary>
    private static double GetScaleForPoint(NativeMethods.POINT pt)
    {
        // ShCore.GetDpiForMonitor 在 Win8.1+ 可用；失败一律按 1.0 处理（与原行为一致）。
        try
        {
            var hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
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
}
