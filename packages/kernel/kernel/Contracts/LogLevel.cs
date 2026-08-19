// BetterDesktop.Kernel — 日志等级枚举（ADR-002 D2 冻结面）
// 内核日志服务（M10 单一管道）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>日志等级（M10 单一管道约定）。</summary>
public enum LogLevel
{
    /// <summary>跟踪。</summary>
    Trace,

    /// <summary>调试。</summary>
    Debug,

    /// <summary>信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warn,

    /// <summary>错误。</summary>
    Error
}
