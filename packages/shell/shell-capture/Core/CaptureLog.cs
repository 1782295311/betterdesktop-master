using System.IO;
using BetterDesktop.Diagnostics;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// capture exe 日志：委托给跨进程统一日志器（异步入队 / 有界 / 按天+按大小滚动 / 保留治理）。
/// 写入 %LOCALAPPDATA%\BetterDesktop\logs\capture-yyyyMMdd.log。
/// 降级可见纪律（runtime-health fail-visible）：后端降级、HDR 未映射、受保护内容等必须落日志。
/// </summary>
public static class CaptureLog
{
    private static readonly DiagnosticLogger Logger = DiagnosticLogger.Start("capture", DiagnosticLogger.ResolveLevel());

    public static string LogDir => DiagnosticLogger.DefaultDirectory;

    public static string LogPath => Path.Combine(LogDir, $"capture-{DateTime.Now:yyyyMMdd}.log");

    public static void Trace(string message) => Logger.Trace(message);

    public static void Info(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);

    /// <summary>写启动横幅（版本 / 系统 / 目录），实地测试时用于快速定位环境。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Capture 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（退出/崩溃路径）。</summary>
    public static void Flush() => Logger.Flush();
}
