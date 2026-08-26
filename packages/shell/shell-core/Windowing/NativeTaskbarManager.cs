using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Core.Windowing;

/// <summary>
/// 原生 Windows 部件管理（参考 Cairo Shell 的 <c>ExplorerHelper.HideExplorerTaskbar</c> 思路）：
/// 本桌面环境**不实现** Windows 原生任务栏，而是直接管理 Explorer 原生任务栏的显示/隐藏。
/// 启用我方 Dock 时隐藏原生任务栏（让其独占底部区域）；关闭或退出时恢复，避免桌面环境退出后原生任务栏消失。
/// 覆盖主屏 <c>Shell_TrayWnd</c> 与多显示器的 <c>Shell_SecondaryTrayWnd</c>（用 EnumWindows 遍历匹配，比硬编码更稳）。
/// </summary>
public static class NativeTaskbarManager
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>设置 Explorer 原生任务栏的可见性（true=显示，false=隐藏）。失败时静默忽略。</summary>
    public static void SetTaskbarVisible(bool visible)
    {
        var cmd = visible ? SW_SHOW : SW_HIDE;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                var sb = new StringBuilder(256);
                if (GetClassName(hWnd, sb, sb.Capacity) > 0)
                {
                    var className = sb.ToString();
                    if (className == PrimaryTrayClass || className == SecondaryTrayClass)
                    {
                        ShowWindow(hWnd, cmd);
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
}
