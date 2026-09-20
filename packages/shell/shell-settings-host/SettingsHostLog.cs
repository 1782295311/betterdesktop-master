// BetterDesktop.Settings — 独立设置进程日志
//
// 为什么必须有：本进程可能"打不开"而不是"崩掉"（分区目录构建失败、外观字典缺失、单实例转发失败…），
// 没有日志就只能靠猜。落 %LOCALAPPDATA%\BetterDesktop\logs\settings-host-<date>.log，并与 DiagnosticLog 接通。

using BetterDesktop.Diagnostics;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.SettingsHost;

/// <summary>独立设置进程日志：委托给跨进程统一日志器（异步入队 / 有界 / 按天+按大小滚动 / 保留治理）。</summary>
internal static class SettingsHostLog
{
    private static readonly DiagnosticLogger Logger =
        DiagnosticLogger.Start("settings-host", DiagnosticLogger.ResolveLevel());

    private static bool _installed;

    /// <summary>当前日志文件路径（供"打开日志目录"类功能使用）。</summary>
    public static string Path => System.IO.Path.Combine(
        DiagnosticLogger.DefaultDirectory,
        $"settings-host-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>接上内核诊断日志并写启动横幅（一次即可）。</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        Banner();
        DiagnosticLog.SetSink((_, message) => Trace(message), Flush);
    }

    public static void Trace(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);

    /// <summary>写启动横幅（版本 / 系统 / 参数）。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Settings 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（退出/崩溃路径）。</summary>
    public static void Flush() => Logger.Flush();
}
