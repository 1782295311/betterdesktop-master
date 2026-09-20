// BetterDesktop.Host — 文件日志 sink（M10 单一管道的宿主端实现）。
// 写入 %LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log。
// 实现委托给跨进程共享的 DiagnosticLogger（异步 / 有界 / 按天+按大小滚动 / 保留治理 / 崩溃可刷盘），
// 本类只负责把内核的 LogLevel 映射成 DiagnosticLevel 并保持原有构造/Dispose 形状。

using BetterDesktop.Diagnostics;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Host;

/// <summary>
/// 文件日志 sink：实现 <see cref="Action{LogLevel, string}"/> 签名，供 CordisContext logSink 注入。
/// 调用方不阻塞（入队即返回），后台线程批量写盘。
/// </summary>
public sealed class FileLogSink : IDisposable
{
    private readonly DiagnosticLogger _logger;

    /// <summary>构造：建日志目录、治理历史日志（清理过期/超额）、写启动横幅。</summary>
    public FileLogSink()
    {
        _logger = DiagnosticLogger.Start("host", DiagnosticLogger.ResolveLevel());
        _logger.Banner("BetterDesktop.Host 启动", DiagnosticLogger.EnvironmentInfo());
    }

    /// <summary>日志入队（非阻塞）。供 CordisContext logSink: logSink.Invoke 调用。</summary>
    public void Invoke(LogLevel level, string message) => _logger.Write(Map(level), message);

    /// <summary>同步刷盘（崩溃 / 退出路径使用）。</summary>
    public void Flush() => _logger.Flush();

    /// <summary>停止后台写入，排空队列剩余日志，释放文件句柄。</summary>
    public void Dispose()
    {
        _logger.Info("[host] 日志正常关闭");
        _logger.Dispose();
    }

    private static DiagnosticLevel Map(LogLevel level) => level switch
    {
        LogLevel.Trace => DiagnosticLevel.Trace,
        LogLevel.Debug => DiagnosticLevel.Debug,
        LogLevel.Info => DiagnosticLevel.Info,
        LogLevel.Warn => DiagnosticLevel.Warn,
        LogLevel.Error => DiagnosticLevel.Error,
        _ => DiagnosticLevel.Info,
    };
}
