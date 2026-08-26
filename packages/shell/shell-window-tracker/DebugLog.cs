using System;
using System.IO;

namespace BetterDesktop.Shell.WindowTracker;

/// <summary>
/// 轻量调试追踪：用于真机排查（如缩略图预览链路、启动崩溃定位）。
/// 自 shell-dock 下沉（步骤5）：缩略图链路随 DwmThumbnail 迁入 window-tracker，
/// Dock 侧（DockWindow）仍经本类打点，故为 public 供跨程序集使用。
/// 设计要点：每条 Trace 同步追加落盘到桌面 BetterDesktop_debug.log。
/// 之所以不用后台缓冲刷盘——崩溃/进程异常退出时内存缓冲会丢失，
/// 排查崩溃最需要的恰恰是「崩溃前的最后一帧」，必须即时落盘。
/// 落盘失败静默，不参与正常运行逻辑。
/// </summary>
public static class DebugLog
{
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "BetterDesktop_debug.log");

    public static void Trace(string tag, string msg)
    {
        try
        {
            // 仅追加（不覆盖），避免与内核层 DiagnosticLog 共用同一文件时互相清空旧日志。
            File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {msg}\n");
        }
        catch
        {
            // 落盘失败不阻断主流程
        }
    }

    /// <summary>遗留兼容：显式 flush 接口（同步落盘本就是即时，这里为空操作）。</summary>
    public static void FlushNow()
    {
        // 同步追加模式下无需额外 flush；保留方法以免外部引用报错。
    }
}
