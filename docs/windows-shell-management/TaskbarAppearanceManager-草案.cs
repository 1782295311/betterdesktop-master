// 草案：L2 任务栏外观管理（C#，外部进程，无需注入 Explorer）
// 灵感：TranslucentTB 的 SetWindowCompositionAttribute + ACCENT_POLICY 手法
// 位置建议：packages/shell/shell-core/Windowing/TaskbarAppearanceManager.cs
// 注意：仅主屏任务栏可外部修改；副屏需注入（见 README Phase D）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Core.Windowing;

/// <summary>
/// 原生任务栏外观管理（透明/模糊/亚克力/渐变）。参考 TranslucentTB 实现。
/// 仅作用于主屏任务栏（Shell_TrayWnd）；副屏需注入 Explorer，见 cross-language/Phase D。
/// </summary>
public static class TaskbarAppearanceManager
{
    // ---- 数据结构（对应 Windows undocumented ACCENT_POLICY）----
    private enum ACCENT_STATE : int
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public ACCENT_STATE AccentState;
        public int AccentFlags;
        public int GradientColor;   // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;       // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    // ---- P/Invoke ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---- 对外 API ----
    public enum TaskbarStyle { Normal, Transparent, Blur, Acrylic, Gradient }

    /// <summary>设置主屏任务栏外观。color 为 ARGB；opacity 0~1（仅 Gradient/Acrylic 生效）。</summary>
    public static bool SetPrimaryTaskbarStyle(TaskbarStyle style, uint argbColor = 0x00000000, double opacity = 1.0)
    {
        var hwnd = FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return false;
        return Apply(hwnd, style, argbColor, opacity);
    }

    /// <summary>枚举所有任务栏（主+副屏），返回 HWND 与是否主屏。</summary>
    public static IEnumerable<(IntPtr Hwnd, bool IsPrimary)> EnumTaskbars()
    {
        var primary = MonitorFromPoint(0, 0, MONITOR_DEFAULTTOPRIMARY);
        var result = new List<(IntPtr, bool)>();
        EnumWindows((hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            if (GetClassName(hWnd, sb, sb.Capacity) > 0)
            {
                var cn = sb.ToString();
                if (cn is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    result.Add((hWnd, MonitorFromWindow(hWnd, 0) == primary));
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---- 内部 ----
    private static bool Apply(IntPtr hwnd, TaskbarStyle style, uint argb, double opacity)
    {
        var state = style switch
        {
            TaskbarStyle.Normal => ACCENT_STATE.ACCENT_DISABLED,
            TaskbarStyle.Transparent => ACCENT_STATE.ACCENT_ENABLE_TRANSPARENT,
            TaskbarStyle.Blur => ACCENT_STATE.ACCENT_ENABLE_BLURBEHIND,
            TaskbarStyle.Acrylic => ACCENT_STATE.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            TaskbarStyle.Gradient => ACCENT_STATE.ACCENT_ENABLE_GRADIENT,
            _ => ACCENT_STATE.ACCENT_DISABLED,
        };

        // ARGB -> ABGR（Windows 颜色序）
        int abgr = (int)((argb & 0xFF00FF00) | ((argb & 0x00FF0000) >> 16) | ((argb & 0x000000FF) << 16));
        int alpha = (int)(opacity * 255) << 24; // 注意：实际用 GradientColor 的 Alpha 通道
        abgr = (abgr & 0x00FFFFFF) | alpha;

        var policy = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = 0,
            GradientColor = abgr,
            AnimationId = 0,
        };

        var data = new WINCOMPATTRDATA
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(policy)),
            SizeOfData = Marshal.SizeOf(policy),
        };

        try
        {
            Marshal.StructureToPtr(policy, data.Data, false);
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data.Data);
        }
    }
}
