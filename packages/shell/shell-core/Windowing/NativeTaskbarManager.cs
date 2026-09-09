using System;
using System.Runtime.InteropServices;
using System.Text;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Windowing;

/// <summary>
/// 原生 Windows 部件管理（参考 Cairo Shell 的 <c>ExplorerHelper.HideExplorerTaskbar</c> 思路）：
/// 本桌面环境**不实现** Windows 原生任务栏，而是直接管理 Explorer 原生任务栏的显示/隐藏。
/// 启用我方 Dock 时隐藏原生任务栏（让其独占底部区域）；关闭或退出时恢复，避免桌面环境退出后原生任务栏消失。
/// 覆盖主屏 <c>Shell_TrayWnd</c> 与多显示器的 <c>Shell_SecondaryTrayWnd</c>（用 EnumWindows 遍历匹配，比硬编码更稳）。
/// 
/// Win11 兼容（2026-09-07 实测 25H2）：任务栏为 XAML 托管窗口，仅 NativeMethods.ShowWindow(SW_SHOW) 无法恢复
/// （IsWindowVisible 仍为 false）；显示必须 恢复 WS_VISIBLE 样式 + NativeMethods.SetWindowPos(SWP_FRAMECHANGED|SWP_SHOWWINDOW)
/// + ShowWindow/ShowWindowAsync 组合。隐藏同样走清样式 + SWP_HIDEWINDOW，确保两态对称可靠。
/// </summary>
public static class NativeTaskbarManager
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int GWL_STYLE = -16;
    private const int WS_VISIBLE = 0x10000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    /// <summary>设置 Explorer 原生任务栏的可见性（true=显示，false=隐藏）。失败时静默忽略。</summary>
    public static void SetTaskbarVisible(bool visible)
    {
        try
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                var sb = new StringBuilder(256);
                if (NativeMethods.GetClassName(hWnd, sb, sb.Capacity) > 0)
                {
                    var className = sb.ToString();
                    if (className == PrimaryTrayClass || className == SecondaryTrayClass)
                    {
                        if (visible)
                        {
                            ShowTaskbar(hWnd);
                        }
                        else
                        {
                            HideTaskbar(hWnd);
                        }
                    }
                }
                return true; // 继续枚举其余窗口
            }, IntPtr.Zero);
        }
        catch
        {
            // Win32 失败（如无 Explorer 任务栏）不阻断桌面环境主流程
        }
    }

    private static void ShowTaskbar(IntPtr hWnd)
    {
        // Win11 任务栏 XAML 托管：仅 ShowWindow 不可靠，必须显式恢复 WS_VISIBLE 并刷新样式。
        var style = NativeMethods.GetWindowLong(hWnd, GWL_STYLE);
        NativeMethods.SetWindowLong(hWnd, GWL_STYLE, style | WS_VISIBLE);
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(hWnd, SW_SHOW);
        NativeMethods.ShowWindowAsync(hWnd, SW_SHOW);
    }

    private static void HideTaskbar(IntPtr hWnd)
    {
        var style = NativeMethods.GetWindowLong(hWnd, GWL_STYLE);
        NativeMethods.SetWindowLong(hWnd, GWL_STYLE, style & ~WS_VISIBLE);
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_HIDEWINDOW);
        NativeMethods.ShowWindow(hWnd, SW_HIDE);
        NativeMethods.ShowWindowAsync(hWnd, SW_HIDE);
    }
}
