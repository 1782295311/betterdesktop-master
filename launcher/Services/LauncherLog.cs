// BetterDesktop 启动器 —— 自带日志（%LOCALAPPDATA%\BetterDesktop\logs\launcher.log）。
//
// 【为什么不复用 Kernel 的 DiagnosticLog】启动器的第一职责恰恰是"在环境可能不完整时仍能自证"：
// 复用共享日志会引入"日志本身依赖某组件已正确部署"的耦合。这里用最小实现写独立文件，
// 与托盘 TrayLog / 恢复 RecoveryLog 同一模式（都是短生命周期进程，自证优先）。

using System;
using System.IO;
using System.Text;

namespace BetterDesktop.Launcher.Services;

/// <summary>启动器日志（多进程追加、失败静默：诊断永不影响业务）。</summary>
internal static class LauncherLog
{
    private static readonly object Gate = new();
    private static readonly string? LogFile = BuildLogFile();

    /// <summary>日志文件绝对路径（用于界面提示"日志在哪"）；不可用时为 null。</summary>
    public static string? FilePath => LogFile;

    private static string? BuildLogFile()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop",
                "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "launcher.log");
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Write(string message)
    {
        if (LogFile is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                System.IO.File.AppendAllText(
                    LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 写日志失败不得影响启动
        }
    }

    public static void Error(string what, Exception ex)
        => Write($"[错误] {what}: {ex.GetType().Name}: {ex.Message}");
}
