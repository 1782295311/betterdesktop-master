// BetterDesktop.Kernel — DiagnosticLog 调试追踪门面（M10 单一管道的薄包装）
// 改造说明（2026-09-07 违规2修复）：
//   原实现直写桌面 BetterDesktop_debug.log + 裸 catch{}，属"第二日志管道"违规。
//   现改为：宿主启动时通过 SetSink 注入内核日志 sink（FileLogSink.Invoke），
//   所有 Trace 调用经此 sink 写入 %LocalAppData%\BetterDesktop\logs\，不再写桌面。
//   全仓 270+ 调用点（静态类/WPF 窗口/无 DI 服务）无需修改，统一走单管道。
//   sink 未设置时静默丢弃（与 KernelLogger null sink 行为一致）。
//
// 2026-09-17（面向分发实地测试）：新增 Flush 与 Crash。
//   异步管道的代价是"进程被终止时队列里的日志会丢"，而崩溃现场最需要的恰恰是最后几条。
//   Crash 会在记录异常后同步刷盘，并把版本/系统/目录等环境快照一并落盘。

using BetterDesktop.Diagnostics;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 调试追踪门面：无 DI 访问的类（静态工具、WPF 窗口、Native 互操作）经此写诊断日志。
/// 宿主启动时调用 <see cref="SetSink"/> 注入文件日志 sink；未注入时日志丢弃。
/// 这是 M10 单一管道的薄包装，不是独立第二管道。
/// </summary>
public static class DiagnosticLog
{
    private static volatile Action<LogLevel, string>? _sink;
    private static volatile Action? _flush;

    /// <summary>设置日志 sink（宿主启动时调用一次）。传入 null 恢复丢弃模式。</summary>
    /// <param name="sink">日志写入回调。</param>
    /// <param name="flush">同步刷盘回调（崩溃/退出路径使用；未提供时为无操作）。</param>
    public static void SetSink(Action<LogLevel, string>? sink, Action? flush = null)
    {
        _sink = sink;
        _flush = flush;
    }

    /// <summary>写入一条诊断追踪日志（Info 级）。tag 作为消息前缀。</summary>
    public static void Trace(string tag, string msg)
    {
        _sink?.Invoke(LogLevel.Info, $"[{tag}] {msg}");
    }

    /// <summary>
    /// 写入一条调试日志（Debug 级）。**高频路径必须用这个**（每秒/每帧/每次轮询都会触发的打点）：
    /// 默认日志级别是 Info，Debug 级不会落盘，需要排障时用
    /// <c>BETTERDESKTOP_LOG_LEVEL=Debug</c> 打开 —— 否则分发给他人实测几天会堆出几十万行噪声，
    /// 反而淹没了真正的关键信息。
    /// </summary>
    public static void Debug(string tag, string msg)
    {
        _sink?.Invoke(LogLevel.Debug, $"[{tag}] {msg}");
    }

    /// <summary>同步刷盘：把异步管道中排队的日志立刻落盘（崩溃 / 退出 / 导出诊断包前调用）。</summary>
    public static void Flush()
    {
        try
        {
            _flush?.Invoke();
        }
        catch
        {
            // 刷盘失败绝不影响调用方（崩溃路径尤其不能二次抛）
        }
    }

    /// <summary>
    /// 记录一次未处理异常（异常链 + 环境快照）并同步刷盘。崩溃路径专用。
    /// 环境快照（版本 / 系统 / 进程 / 日志目录）比堆栈更常决定"下一步怎么修"。
    /// </summary>
    public static void Crash(string tag, string source, Exception? ex, bool terminating)
    {
        try
        {
            _sink?.Invoke(LogLevel.Error, $"[{tag}] [崩溃] 来源={source} 进程终止={terminating}");
            if (ex is not null)
            {
                _sink?.Invoke(LogLevel.Error, $"[{tag}]   类型: {ex.GetType().FullName}");
                _sink?.Invoke(LogLevel.Error, $"[{tag}]   消息: {ex.Message}");
                _sink?.Invoke(LogLevel.Error, $"[{tag}]   堆栈: {ex.StackTrace}");

                var inner = ex.InnerException;
                var depth = 0;
                while (inner is not null && depth++ < 5)
                {
                    _sink?.Invoke(LogLevel.Error, $"[{tag}]   内层[{depth}]: {inner.GetType().FullName}: {inner.Message}");
                    inner = inner.InnerException;
                }
            }

            foreach (var line in DiagnosticLogger.EnvironmentInfo())
            {
                _sink?.Invoke(LogLevel.Info, "  " + line);
            }
        }
        catch
        {
            // 崩溃处理器自身绝不允许再抛
        }
        finally
        {
            Flush();
        }
    }
}
