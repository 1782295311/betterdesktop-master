using BetterDesktop.Diagnostics;

namespace BetterDesktop.Recovery;

/// <summary>
/// 恢复工具日志：通过 shared/logging 共享源编译进本程序集（本工程有"零依赖"约束，
/// 不引用任何 BetterDesktop 工程），写入统一日志根 recovery-yyyyMMdd.log。
/// </summary>
internal static class RecoveryLog
{
    private static readonly DiagnosticLogger Logger =
        DiagnosticLogger.Start("recovery", DiagnosticLogger.ResolveLevel());

    public static void Info(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    /// <summary>写启动横幅（版本 / 系统 / 命令行）。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Recovery 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（短命进程结束前必须调用）。</summary>
    public static void Flush() => Logger.Flush();
}
