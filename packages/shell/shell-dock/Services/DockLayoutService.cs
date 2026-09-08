using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Native;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 布局服务实现。
/// 负责 Dock 度量计算、底部边缘热区判定、前台全屏窗口检测和显示器枚举。
///
/// 多显示器策略（system.multiMonitorStrategy）：
/// - primary：仅主显示器（当前默认行为）；
/// - all：所有显示器各放一个 Dock（按屏工作区底部居中）；
/// - independent：各显示器独立布局（行为与 all 一致，但语义上允许未来差异化配置，
///   当前阶段与 all 共用"每屏一个 Dock"的实现，仅保留策略可区分性）。
/// </summary>
public sealed class DockLayoutService : IDockLayoutService
{
    private readonly double _edgeHotZoneHeight;
    private double _bottomMargin;
    private readonly ISettingsService? _settings;

    public DockLayoutService(double edgeHotZoneHeight = 4d, double bottomMargin = 10d, ISettingsService? settings = null)
    {
        _edgeHotZoneHeight = edgeHotZoneHeight;
        _bottomMargin = bottomMargin;
        _settings = settings;
    }

    /// <summary>Dock 距屏幕底部的高度（px）。可在运行时由 dock 视觉配置更新（用户调滑块后重新定位）。</summary>
    public double BottomMargin
    {
        get => _bottomMargin;
        set => _bottomMargin = value;
    }

    /// <summary>
    /// 当前多显示器策略；缺省 primary。
    /// </summary>
    private string MultiMonitorStrategy
        => _settings?.Get("system.multiMonitorStrategy", "primary") ?? "primary";

    public DockLayoutMetrics Measure(int screenWidth, int screenHeight, int iconCount)
    {
        var iconSize = 48d;
        var itemSpacing = 12d;
        var labelHeight = 24d;
        var horizontalPadding = 16d;

        var contentWidth = iconCount * iconSize + Math.Max(0, iconCount - 1) * itemSpacing + horizontalPadding * 2;
        var contentHeight = iconSize + labelHeight + _bottomMargin * 2;

        var x = Math.Max(0, (screenWidth - contentWidth) / 2d);
        var y = Math.Max(0, screenHeight - contentHeight - _bottomMargin);

        return new DockLayoutMetrics
        {
            Bounds = new Rect(x, y, contentWidth, contentHeight),
            IconSize = iconSize,
            ItemSpacing = itemSpacing,
            LabelHeight = labelHeight,
            EdgeHoverRect = new Rect(0, screenHeight - _edgeHotZoneHeight, screenWidth, _edgeHotZoneHeight)
        };
    }

    public bool ShouldShowOnEdgeHover(Point cursorScreenPoint)
    {
        // 底部热区触发。
        return cursorScreenPoint.Y >= SystemParameters.PrimaryScreenHeight - _edgeHotZoneHeight;
    }

    public bool ShouldHideOnFullscreen()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            // ⚠️ 前台是"桌面宿主"（Progman/WorkerW）或本进程窗口（自绘桌面/菜单栏/dock）时
            //    绝不算全屏——它们是无边框全屏窗口，会被下方覆盖判定误判，
            //    导致"一点桌面 dock 就消失、且永远不再出现"（实测回归）。
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId)
            {
                return false;
            }

            if (GetClassName(hwnd) is "Progman" or "WorkerW")
            {
                return false;
            }

            // P2c（7404）：多任务视图（AltTab/任务视图/SnapAssist）可见时隐藏 dock——
            // 此类瞬态覆盖层不满足"前台窗口覆盖整屏且无标题栏"的常规全屏判定。
            // 只轮询查询不注册回调；服务不可用（低版本/QueryService 失败）降级 false 不误隐藏。
            if (MultitaskingViewVisibilityService.IsAnyViewVisible())
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            // 全屏判定基于"前台窗口所在显示器"的工作区，而非主屏——
            // 多显示器下前台窗口可能落在非主屏，必须按其实际所在屏判断覆盖。
            var screen = MonitorFromWindowRect(rect);
            var screenW = screen.Width;
            var screenH = screen.Height;

            // 全屏条件：前台窗口覆盖整个所在屏工作区且无标题栏。
            var coversScreen = rect.Left <= screen.Left && rect.Top <= screen.Top &&
                               rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
            if (!coversScreen)
            {
                return false;
            }

            var style = (uint)NativeMethods.GetWindowLong(hwnd, GWL_STYLE);
            var hasCaption = (style & WS_CAPTION) != 0;

            return !hasCaption;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 枚举所有显示器的物理矩形（对齐 cairoshell 用 MonitorFromWindow + GetMonitorInfo）。
    /// 返回的每个 Rect 为显示器整屏（含任务栏区域），调用方需自行减去工作区偏移时可用
    /// <see cref="GetScreenWorkingArea"/>。
    /// </summary>
    public IReadOnlyList<Rect> GetAllScreens()
    {
        var screens = new List<Rect>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (hMonitor, hdc, rectPtr, data) =>
                {
                    // lprcMonitor 是指向 RECT 的非托管指针，需 Marshal 取出。
                    var r = Marshal.PtrToStructure<RECT>(rectPtr);
                    screens.Add(new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    return true;
                }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败回退到主屏
            screens.Clear();
            screens.Add(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        }

        if (screens.Count == 0)
        {
            screens.Add(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        }

        return screens;
    }

    /// <summary>
    /// 根据多显示器策略返回 Dock 应出现的"目标屏幕"列表。
    /// - primary：仅主屏；
    /// - all / independent：所有显示器（每屏一个 Dock）。
    /// 返回的是显示器整屏矩形，定位时再用底部居中换算工作区坐标。
    /// </summary>
    public IReadOnlyList<Rect> GetDockTargetScreens()
    {
        var strategy = MultiMonitorStrategy;
        if (strategy is "all" or "independent")
        {
            return GetAllScreens();
        }

        // primary：仅主屏
        return new List<Rect>
        {
            new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)
        };
    }

    /// <summary>
    /// 把给定屏幕矩形底部居中定位（Dock 默认贴在屏幕底部、水平居中）。
    /// 返回 Dock 窗口应有的 Left / Top（基于屏幕整屏矩形换算，已含底部留白）。
    /// </summary>
    public (double Left, double Top) BottomCenterForScreen(Rect screen, double dockWidth, double dockHeight)
    {
        var left = screen.Left + (screen.Width - dockWidth) / 2;
        var top = screen.Top + screen.Height - dockHeight - _bottomMargin;
        return (left, top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private const int GWL_STYLE = -16;
    private const uint WS_CAPTION = 0x00C00000;

    /// <summary>取窗口类名（失败返回空串，M10）。</summary>
    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        _ = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// 返回给定窗口矩形所在显示器的工作区矩形（用于全屏判定对齐到正确屏幕）。
    /// </summary>
    private Rect MonitorFromWindowRect(NativeMethods.RECT windowRect)
    {
        // 以窗口中心定位所在显示器
        var cx = (windowRect.Left + windowRect.Right) / 2;
        var cy = (windowRect.Top + windowRect.Bottom) / 2;
        var hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = cx, Y = cy }, MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            var w = info.rcWork;
            return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

}
