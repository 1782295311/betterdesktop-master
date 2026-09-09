// BetterDesktop.Kernel — DiagnosticLog 调试追踪门面（M10 单一管道的薄包装）
// 改造说明（2026-09-07 违规2修复）：
//   原实现直写桌面 BetterDesktop_debug.log + 裸 catch{}，属"第二日志管道"违规。
//   现改为：宿主启动时通过 SetSink 注入内核日志 sink（FileLogSink.Invoke），
//   所有 Trace 调用经此 sink 写入 %LocalAppData%\BetterDesktop\logs\，不再写桌面。
//   全仓 270+ 调用点（静态类/WPF 窗口/无 DI 服务）无需修改，统一走单管道。
//   sink 未设置时静默丢弃（与 KernelLogger null sink 行为一致）。

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

    /// <summary>设置日志 sink（宿主启动时调用一次）。传入 null 恢复丢弃模式。</summary>
    public static void SetSink(Action<LogLevel, string>? sink)
    {
        _sink = sink;
    }

    /// <summary>写入一条诊断追踪日志（Info 级）。tag 作为消息前缀。</summary>
    public static void Trace(string tag, string msg)
    {
        _sink?.Invoke(LogLevel.Info, $"[{tag}] {msg}");
    }
}
