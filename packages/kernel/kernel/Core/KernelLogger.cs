// BetterDesktop.Kernel — KernelLogger 实现（ADR-002 D2）
// v1 sink 注入；P2 宿主接文件管道（M10 单一管道）

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>内核日志服务实现：sink 注入，默认无持久化（ADR-002 D2）。</summary>
public sealed class KernelLogger : IKernelLogger
{
    private readonly Action<LogLevel, string>? _sink;

    /// <summary>构造：sink 为 null 时日志被丢弃（v1 已知限制，见包 README）。</summary>
    public KernelLogger(Action<LogLevel, string>? sink = null)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message)
    {
        _sink?.Invoke(level, message);
    }

    /// <inheritdoc />
    public void Info(string message) => Log(LogLevel.Info, message);

    /// <inheritdoc />
    public void Warn(string message) => Log(LogLevel.Warn, message);

    /// <inheritdoc />
    public void Error(string message) => Log(LogLevel.Error, message);
}
