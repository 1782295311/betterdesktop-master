using System;
using System.Runtime.InteropServices;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Taskbar.Contracts;

namespace BetterDesktop.Shell.Taskbar.Native;

/// <summary>
/// DWM 窗口合成属性封装（Win10 任务栏外观控制主路径）。
/// 原样搬运 TranslucentTB：<c>SetWindowCompositionAttribute</c> + <c>WCA_ACCENT_POLICY</c>
/// + <c>ACCENT_POLICY</c>。枚举值与 Windows 未文档化 ABI 严格对齐，不得随意改值。
/// </summary>
public static class DwmapiHelper
{
    // ACCENT_STATE 枚举值（与 Windows 一致，TTB 实证可用）
    private const uint ACCENT_NORMAL = 0;
    private const uint ACCENT_ENABLE_GRADIENT = 1;
    private const uint ACCENT_ENABLE_TRANSPARENT = 2; // 未直接用于任务栏，保留对齐
    private const uint ACCENT_ENABLE_BLURBEHIND = 3;
    private const uint ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const uint ACCENT_ENABLE_HOSTBACKDROP = 5; // 保留

    private const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public uint AccentState;
        public uint AccentFlags;
        public uint GradientColor; // 0xAABBGGRR（ABGR，Windows 未文档化 ABI；非 ARGB！）
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }


    // WM_DWMCOMPOSITIONCHANGED = 0x031A：还原到 NORMAL 后必须通知 explorer/DWM 刷新任务栏外观，
    // 否则残留策略（TTB taskbarattributeworker.cpp:611：send_message(WM_DWMCOMPOSITIONCHANGED, 1, 0)，
    // 拆析-TranslucentTB L50 生死线 2"恢复默认 = ACCENT_NORMAL + 发该消息"）。
    private const uint WmDwmCompositionChanged = 0x031A;


    /// <summary>
    /// ARGB → ABGR（R/B 通道互换）。
    /// 本模型（设置面板）以 ARGB 存储颜色；而 Windows 原生 ABI 均要求 ABGR：
    /// ACCENT_POLICY.GradientColor 与 ITaskbarAppearanceService 的 color 参数都是 0xAABBGGRR
    /// （对照 TTB 原版 color.ToABGR()）。传参前必须转换，否则颜色红蓝互换。
    /// </summary>
    public static uint ToAbgr(uint argb) =>
        (argb & 0xFF00FF00u) | ((argb & 0x00FF0000u) >> 16) | ((argb & 0x000000FFu) << 16);

    /// <summary>
    /// 将任务栏外观（ACCENT 策略）套用到指定窗口。
    /// 这是 Win10 路径的核心；Win11 由 ExplorerTapBridge 走 ITaskbarService。
    /// </summary>
    public static bool SetAccent(IntPtr hWnd, TaskbarAppearance appearance)
    {
        if (hWnd == IntPtr.Zero) return false;

        uint state;
        uint flags;
        uint color = appearance.Color;

        switch (appearance.Accent)
        {
            case TaskbarAccent.Normal:
                state = ACCENT_NORMAL;
                flags = 0;
                break;
            case TaskbarAccent.Opaque:
                state = ACCENT_ENABLE_GRADIENT;
                flags = 2;
                color |= 0xFF000000; // alpha=0xFF 不透明
                break;
            case TaskbarAccent.Clear:
                state = ACCENT_ENABLE_GRADIENT;
                flags = 2;
                color = (color & 0x00FFFFFF); // alpha=0 完全透明
                break;
            case TaskbarAccent.Blur:
                state = ACCENT_ENABLE_BLURBEHIND;
                flags = 2;
                break;
            case TaskbarAccent.Acrylic:
                state = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                flags = 0;
                // 亚克力不完全接受 alpha=0（TTB 实证：置 1 避免全黑）
                if ((color & 0xFF000000) == 0) color |= 0x01000000;
                break;
            default:
                state = ACCENT_NORMAL;
                flags = 0;
                break;
        }

        var policy = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = flags,
            GradientColor = ToAbgr(color), // 原生 ABI 要求 ABGR（0xAABBGGRR）
            AnimationId = 0
        };

        var data = new NativeMethods.WINCOMPATTRDATA
        {
            nAttribute = WCA_ACCENT_POLICY,
            pData = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
            ulSize = Marshal.SizeOf<ACCENT_POLICY>()
        };

        try
        {
            Marshal.StructureToPtr(policy, data.pData, false);
            // 【2026-09-14 语义修正】该 API 返回 BOOL（非零 = 成功）；旧写法 `hr == 0` 判成功是反的。
            bool ok = NativeMethods.SetWindowCompositionAttribute(hWnd, ref data);
            // F4/V5：SetLastError 已开但从不读等于没诊断（7437 纪律 2）——失败必须带错误码。
            if (!ok)
            {
                DiagnosticLog.Trace("TaskbarAccent",
                    $"SetWindowCompositionAttribute 失败 hwnd={hWnd:X} state={state} err={Marshal.GetLastWin32Error()}");
            }
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(data.pData);
        }
    }

    /// <summary>还原窗口到系统默认 ACCENT（Normal）。</summary>
    public static bool ClearAccent(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        var policy = new ACCENT_POLICY { AccentState = ACCENT_NORMAL, AccentFlags = 0, GradientColor = 0, AnimationId = 0 };
        var data = new NativeMethods.WINCOMPATTRDATA
        {
            nAttribute = WCA_ACCENT_POLICY,
            pData = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>()),
            ulSize = Marshal.SizeOf<ACCENT_POLICY>()
        };
        try
        {
            Marshal.StructureToPtr(policy, data.pData, false);
            // 【2026-09-14 语义修正】BOOL 语义（同 SetAccent）：非零 = 成功。
            bool ok = NativeMethods.SetWindowCompositionAttribute(hWnd, ref data);
            // F4/V5：同 SetAccent——失败带错误码，还原失败也要可诊断。
            if (!ok)
            {
                DiagnosticLog.Trace("TaskbarAccent",
                    $"SetWindowCompositionAttribute(还原) 失败 hwnd={hWnd:X} err={Marshal.GetLastWin32Error()}");
            }
            // 还原链补全（审计 §4.5 / TTB cpp:611）：还原成功后通知 explorer 刷新任务栏外观状态。
            if (ok)
            {
                _ = NativeMethods.SendMessage(hWnd, WmDwmCompositionChanged, (IntPtr)1, IntPtr.Zero);
            }
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(data.pData);
        }
    }
}
