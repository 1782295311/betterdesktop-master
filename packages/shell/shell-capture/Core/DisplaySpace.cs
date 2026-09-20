using System;
using System.Collections.Generic;
using System.Windows;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 物理像素 ↔ WPF 逻辑像素（DIP）换算纯函数（T2 对象）。
/// 核心规则：每台显示器按自己的有效 DPI 换算；跨屏矩形按显示器切片换算后并集——
/// 不同缩放下斜跨两屏的选区，逐边用一个缩放值换算必然失真，切片换算才正确。
/// </summary>
public static class DisplaySpace
{
    /// <summary>显示器在 DIP 空间的边界（物理像素 ÷ 该显示器缩放）。</summary>
    public static Rect MonitorDipBounds(MonitorEntry m)
    {
        double sx = m.DpiX / 96.0;
        double sy = m.DpiY / 96.0;
        return new Rect(m.Bounds.X / sx, m.Bounds.Y / sy, m.Bounds.Width / sx, m.Bounds.Height / sy);
    }

    /// <summary>坐标点在 DIP 空间归属的显示器（用于悬浮读数/放大镜的显示缩放）。</summary>
    public static MonitorEntry? MonitorAtDip(Point dip, IReadOnlyList<MonitorEntry> monitors)
    {
        foreach (var m in monitors)
        {
            var b = MonitorDipBounds(m);
            if (dip.X >= b.Left && dip.X < b.Right && dip.Y >= b.Top && dip.Y < b.Bottom)
            {
                return m;
            }
        }
        return monitors.Count > 0 ? monitors[0] : null;
    }

    /// <summary>物理像素坐标点归属的显示器。</summary>
    public static MonitorEntry? MonitorAtPhysical(int x, int y, IReadOnlyList<MonitorEntry> monitors)
    {
        foreach (var m in monitors)
        {
            if (m.Bounds.Contains(x, y))
            {
                return m;
            }
        }
        return monitors.Count > 0 ? monitors[0] : null;
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
