// BetterDesktop.Kernel — IKernelLogger 接口定义（ADR-002 D2 冻结面）
// 内核日志服务（M10 单一管道；v1 sink 注入，P2 宿主接文件管道）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>内核日志服务接口（M10 单一管道）。</summary>
public interface IKernelLogger
{
    /// <summary>按等级记录一条日志。</summary>
    void Log(LogLevel level, string message);

    /// <summary>记录信息级日志。</summary>
    void Info(string message);

    /// <summary>记录警告级日志。</summary>
    void Warn(string message);

    /// <summary>记录错误级日志。</summary>
    void Error(string message);
}
