using System;
using System.IO;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// Kernel 层调试追踪（避免反向依赖 Shell.Dock 的 DebugLog）。
/// 同步追加入桌面 BetterDesktop_debug.log，便于排查插件加载/Pending/Failed 等启动期问题。
/// 落盘失败静默，不参与正常运行逻辑。
/// </summary>
public static class DiagnosticLog
{
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "BetterDesktop_debug.log");

    public static void Trace(string tag, string msg)
    {
        try
        {
            // 仅追加（不覆盖），与 Shell.Dock 的 DebugLog 共用同一文件，避免互相清空。
            File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {msg}\n");
        }
        catch
        {
            // 落盘失败不阻断主流程
        }
    }
}
