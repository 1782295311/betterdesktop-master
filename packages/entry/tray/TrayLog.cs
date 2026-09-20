using BetterDesktop.Diagnostics;

namespace BetterDesktop.Tray;

/// <summary>
/// 托盘日志：委托给跨进程统一日志器（异步入队 / 有界 / 按天+按大小滚动 / 保留治理）。
/// 托盘有"零包引用"约束，故通过 shared/logging 共享源编译本份实现，而不是引用 kernel。
/// 常驻组件最忌讳"因为日志失败把自己搞崩"——入队路径不抛异常。
/// </summary>
internal static class TrayLog
{
    private static readonly DiagnosticLogger Logger = DiagnosticLogger.Start("tray", DiagnosticLogger.ResolveLevel());

    /// <summary>写一条信息日志。</summary>
    public static void Write(string message) => Logger.Info(message);

    /// <summary>写一条警告日志。</summary>
    public static void Warn(string message) => Logger.Warn(message);

    /// <summary>写一条错误日志（带异常类型/消息/堆栈）。</summary>
    public static void Error(string what, Exception ex) => Logger.Error(what, ex);

    /// <summary>写启动横幅（版本 / 系统 / 目录），实地测试时用于快速定位环境。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Tray 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（退出/崩溃路径）。</summary>
    public static void Flush() => Logger.Flush();
}
