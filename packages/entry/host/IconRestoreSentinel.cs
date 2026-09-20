// BetterDesktop.Host — 图标恢复哨兵（--icon-restore-sentinel <pid>）
//
// 背景（2026-09-06 真机实证）：宿主会话冻结后被任务管理器结束（TerminateProcess），
// Application.Exit / AppDomain.ProcessExit 均不触发 → explorer 桌面图标层（SysListView32）
// 残留隐藏，表现为"退出程序后桌面图标不恢复、空白右键唤不出菜单"。
// 托管退出事件只能覆盖优雅退出；本哨兵以独立进程等宿主死亡后恢复，覆盖全部死亡路径。
//
// 生命周期：DesktopPlugin 隐藏图标后拉起本模式实例 → 等待宿主 pid 退出 → 恢复图标 → 退出。
// 幂等：与 Exit/ProcessExit 的恢复重复调用无副作用（无条件 SW_SHOW）。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Host;

/// <summary>图标恢复哨兵：等待宿主进程退出后恢复 explorer 桌面图标层可见。</summary>
public static class IconRestoreSentinel
{
    private const int SwShow = 5;

    /// <summary>最长等待 24h：正常情况下宿主退出即结束；超时自愈退出，防开发期哨兵堆积。</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    /// <summary>阻塞等待宿主退出并恢复图标（在 OnStartup 内同步执行，无窗口无消息循环）。</summary>
    public static void Run(int hostPid)
    {
        DiagnosticLog.Trace("icon-sentinel", $"启动：等待宿主 pid={hostPid} 退出后恢复桌面图标");
        try
        {
            using var host = Process.GetProcessById(hostPid);
            var deadline = DateTime.UtcNow + MaxWait;
            while (!host.WaitForExit(2000) && DateTime.UtcNow < deadline)
            {
                host.Refresh();
                // pid 复用防御：进程名不再是宿主 → 视同宿主已退出
                if (!host.ProcessName.StartsWith("BetterDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Trace("icon-sentinel", $"pid={hostPid} 已被复用（{host.ProcessName}），视同宿主退出");
                    break;
                }
            }
        }
        catch (ArgumentException)
        {
            // 宿主已退出（GetProcessById 找不到进程）→ 直接恢复
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("icon-sentinel", $"等待异常（仍执行恢复）：{ex.Message}");
        }

        RestoreIcons();
        DiagnosticLog.Trace("icon-sentinel", "宿主已退出：桌面图标层恢复完成");
    }
    /// <summary>崩溃兜底：仅处理 explorer 未运行/任务栏不可见（2026-09-16 起不再恢复图标层，见方法体注释）。</summary>
    private static void RestoreIcons()
    {
        try
        {
            // 【2026-09-16 L1 常驻化】不再恢复图标层：桌面图标隐藏是用户意图（随卸载才恢复），
            // 壳退出后由 Agent（DesktopIconsCapability.ApplyIntent）按设置意图保持/重新落地；
            // explorer 崩溃导致的图标层残留隐藏，也由 Agent 接管时按意图恢复。
            DiagnosticLog.Trace("icon-sentinel", "图标层不恢复（L1 常驻：隐藏意图保持，由 Agent 接管落地）");


            // 【回归修复 2026-09-06】任务栏恢复：explorer 崩溃/宿主异常退出后任务栏可能残留隐藏或消失。
            // 哨兵进程覆盖全部死亡路径（含 TerminateProcess/冻结被结束任务），必须同时恢复任务栏。
            try
            {
                if (Process.GetProcessesByName("explorer").Length == 0)
                {
                    DiagnosticLog.Trace("icon-sentinel", "任务栏恢复：explorer 未运行 → 启动 explorer.exe");
                    Process.Start("explorer.exe");
                }
                else
                {
                    // 确保 Shell_TrayWnd / Shell_SecondaryTrayWnd 可见（EnumWindows 遍历匹配）
                    NativeMethods.EnumWindows((hWnd, lParam) =>
                    {
                        var sb = new System.Text.StringBuilder(256);
                        if (NativeMethods.GetClassName(hWnd, sb, sb.Capacity) > 0)
                        {
                            var cn = sb.ToString();
                            if (cn == "Shell_TrayWnd" || cn == "Shell_SecondaryTrayWnd")
                            {
                                NativeMethods.ShowWindow(hWnd, SwShow);
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                    DiagnosticLog.Trace("icon-sentinel", "任务栏恢复：Shell_TrayWnd SW_SHOW 已执行");
                }
            }
            catch (Exception ex2)
            {
                DiagnosticLog.Trace("icon-sentinel", $"任务栏恢复异常：{ex2.Message}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("icon-sentinel", $"恢复异常：{ex.Message}");
        }
    }

}
