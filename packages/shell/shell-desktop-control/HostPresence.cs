// BetterDesktop.DesktopControl — 主程序在线探测（只探测，不实现协议）
//
// 探测手段选"进程名"而不是"管道存在性"：本进程只想知道"宿主在不在"来决定菜单项是否置灰，
// 托盘 PipeProbe 那套管道枚举是 internal（跨程序集不可用），而真正的送达判定由
// MenuCommandPipeClient.TrySend 的返回值兜住（发不出去 → 退化为免宿主直写路径）。
// 探测失败一律按"不在"处理（宁可多置灰一项，也不假装能点）。

using System;
using System.Diagnostics;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>宿主（BetterDesktop.Host.exe）是否在运行。</summary>
internal static class HostPresence
{
    /// <summary>与 tray/ProcessBridge.HostProcessName、watchdog 目标名一致（改名需三处同步）。</summary>
    private const string HostProcessName = "BetterDesktop.Host";

    public static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(HostProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"宿主探测失败（按未运行处理）: {ex.Message}");
            return false;
        }
    }
}
