using System.IO;
using BetterDesktop.Diagnostics;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 面板进程日志：委托给跨进程统一日志器（异步入队 / 有界 / 按天+按大小滚动 / 保留治理）。
/// 写入 %LocalAppData%\BetterDesktop\logs\panel-yyyyMMdd.log。
/// </summary>
internal static class PanelLog
{
    private static readonly DiagnosticLogger Logger = DiagnosticLogger.Start("panel", DiagnosticLogger.ResolveLevel());

    /// <summary>当前日志文件路径（供"打开日志目录"类功能使用）。</summary>
    public static string LogPath => Path.Combine(
        DiagnosticLogger.DefaultDirectory,
        $"panel-{DateTime.Now:yyyyMMdd}.log");

    public static void Trace(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);

    /// <summary>写启动横幅（版本 / 系统 / 目录），实地测试时用于快速定位环境。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Clipboard.Panel 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（退出/崩溃路径）。</summary>
    public static void Flush() => Logger.Flush();
}
