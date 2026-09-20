// BetterDesktop.DesktopControl — 独立进程日志
//
// 为什么必须有：本进程是**短命**进程（弹完菜单即退），出问题现场就没了，user 只能看到"点了没反应"。
// 日志落 %LOCALAPPDATA%\BetterDesktop\logs\desktop-control-<date>.log（统一日志根）。
// 同时接 DiagnosticLog.SetSink：shell-desktop 渲染层（如"菜单命令 X 失败"）的 Trace 也进同一份日志。

using System;
using System.IO;
using BetterDesktop.Diagnostics;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>
/// 独立进程日志：委托给跨进程统一日志器（异步入队 / 有界 / 滚动 / 保留治理）。
/// 短命进程尤其依赖它——进程退出前 Dispose 会排空队列。
/// </summary>
internal static class DesktopControlLog
{
    private static readonly DiagnosticLogger Logger = DiagnosticLogger.Start("desktop-control", DiagnosticLogger.ResolveLevel());

    private static bool _installed;

    /// <summary>当前日志文件路径（供"打开日志目录"类功能使用）。</summary>
    public static string LogPath => System.IO.Path.Combine(
        DiagnosticLogger.DefaultDirectory,
        $"desktop-control-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>接上内核诊断日志并写启动横幅（一次即可）。</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        Banner();
        DiagnosticLog.SetSink((_, message) => Logger.Info(message));
    }

    public static void Trace(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);

    /// <summary>写启动横幅（版本 / 系统 / 参数），短命进程排障的第一现场。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.DesktopControl 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（进程退出前必须调用，否则短命进程最后几条日志会丢）。</summary>
    public static void Flush() => Logger.Flush();
}
