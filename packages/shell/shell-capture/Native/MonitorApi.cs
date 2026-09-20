using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Native;

/// <summary>显示器枚举 / DPI / HDR 状态原生接口（虚拟屏坐标与 per-monitor DPI 的事实来源）。</summary>
internal static class MonitorApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    public const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("shcore.dll", PreserveSig = true)]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MONITOR_DPI_TYPE_EFFECTIVE_DPI。</summary>
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int Type;
        public int Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint AdvancedColorSupported;
        public uint AdvancedColorEnabled;
        public uint WideColorEncodeEnabled;
        public uint HdrModeEnabled;
    }

    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = -4;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [DllImport("user32.dll", PreserveSig = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll", PreserveSig = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", PreserveSig = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    /// <summary>枚举当前所有显示器（含负坐标副屏）。</summary>
    public static List<MonitorEntry> EnumMonitors()
    {
        var list = new List<MonitorEntry>();
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr _, ref RECT lprcMonitor, IntPtr _) =>
        {
            var info = new MONITORINFO { CbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
                var ex = new MONITORINFOEX { CbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                string name = GetMonitorInfoEx(hMonitor, ref ex) ? ex.DeviceName ?? string.Empty : string.Empty;
                list.Add(new MonitorEntry
                {
                    Handle = hMonitor,
                    DeviceName = name,
                    Bounds = new PixelRect(info.RcMonitor.Left, info.RcMonitor.Top, info.RcMonitor.Width, info.RcMonitor.Height),
                    WorkArea = new PixelRect(info.RcWork.Left, info.RcWork.Top, info.RcWork.Width, info.RcWork.Height),
                    DpiX = (int)(dpiX == 0 ? 96 : dpiX),
                    DpiY = (int)(dpiY == 0 ? 96 : dpiY),
                    IsPrimary = (info.DwFlags & MONITORINFOF_PRIMARY) != 0,
                });
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    }

    /// <summary>是否远程会话（SM_REMOTESESSION）；远程桌面/虚拟机下 DXGI 与 WGC 可能失效，需降级 BitBlt。</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_REMOTESESSION = 0x1000;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>整个虚拟屏像素矩形（可为负坐标）。</summary>
    public static PixelRect VirtualScreenBounds()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>是否有任一显示器处于高级色彩（HDR）模式（DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO）。
    /// 读取失败返回 false（fail-safe）。逐显示器精确匹配需对 TARGET_NAME，此处用「任一开启」门控 + 帧格式实测。</summary>
    public static bool AnyAdvancedColorEnabled()
    {
        foreach (var path in GetActivePaths())
        {
            var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    Type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    Size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref req) == 0 && req.AdvancedColorEnabled != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static DISPLAYCONFIG_PATH_INFO[] GetActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out _) != 0 || pathCount == 0)
        {
            return Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref pathCount, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            return Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        }
        return paths;
    }
}

/// <summary>显示器条目（采集/选区/DPI 换算的输入；供纯函数与单测消费）。</summary>
public sealed class MonitorEntry
{
    public IntPtr Handle { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>物理像素边界（虚拟屏坐标，可为负）。</summary>
    public PixelRect Bounds { get; set; }

    public PixelRect WorkArea { get; set; }

    public int DpiX { get; set; }

    public int DpiY { get; set; }

    public bool IsPrimary { get; set; }

    /// <summary>有效 DPI 缩放（96 = 100%）。</summary>
    public double ScaleX => DpiX / 96.0;

    public double ScaleY => DpiY / 96.0;

    public override string ToString() => $"{DeviceName} {Bounds} DPI={DpiX}";
}
