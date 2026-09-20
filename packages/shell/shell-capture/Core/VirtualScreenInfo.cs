using System;
using System.Collections.Generic;
using System.Windows;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 虚拟屏信息（多显示器并集；采集/选区/DPI 换算的坐标基准）。
/// 事实来源：EnumDisplayMonitors + GetMonitorInfoW + GetDpiForMonitor（运行时 Query）。
/// </summary>
public sealed class VirtualScreenInfo
{
    public PixelRect Bounds { get; }

    public IReadOnlyList<MonitorEntry> Monitors { get; }

    public bool IsRemoteSession { get; }

    public bool AnyAdvancedColor { get; }

    private VirtualScreenInfo(PixelRect bounds, IReadOnlyList<MonitorEntry> monitors, bool remote, bool hdr)
    {
        Bounds = bounds;
        Monitors = monitors;
        IsRemoteSession = remote;
        AnyAdvancedColor = hdr;
    }

    /// <summary>查询当前虚拟屏状态（采集/覆盖层共用；每次采集时重新查询，避免分辨率变更后坐标失真）。</summary>
    public static VirtualScreenInfo Query()
    {
        var monitors = MonitorApi.EnumMonitors();
        if (monitors.Count == 0)
        {
            // 枚举失败兜底：以系统虚拟屏指标为准（罕见，但保证有值可算）。
            var fb = MonitorApi.VirtualScreenBounds();
            return new VirtualScreenInfo(fb, Array.Empty<MonitorEntry>(), false, false);
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.Bounds.X);
            top = Math.Min(top, m.Bounds.Y);
            right = Math.Max(right, m.Bounds.Right);
            bottom = Math.Max(bottom, m.Bounds.Bottom);
        }

        bool remote = MonitorApi.GetSystemMetrics(MonitorApi.SM_REMOTESESSION) != 0;
        bool hdr = MonitorApi.AnyAdvancedColorEnabled();
        return new VirtualScreenInfo(PixelRect.FromLTRB(left, top, right, bottom), monitors, remote, hdr);
    }
}
