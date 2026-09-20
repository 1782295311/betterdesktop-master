using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.WindowTracker;

/// <summary>
/// 轻量调试追踪：转投内核单管道（<see cref="DiagnosticLog"/> → 宿主异步文件 sink）。
/// <para>
/// 历史实现是"每条同步追加到桌面 BetterDesktop_debug.log"，理由是"崩溃前最后一帧必须即时落盘"。
/// 但该 API 被 Dock 的悬停/召唤与缩略图链路调用（<c>DockWindow.xaml.cs</c> 36 处、<c>DwmThumbnail</c> 等），
/// 属 UI 热路径——同步写盘会直接拖慢鼠标响应，且把日志写到用户桌面属"第二管道"违规。
/// </para>
/// <para>
/// 现改为异步入队：性能问题消除；崩溃场景的"最后一帧"由 <c>CrashGuard</c> 在未处理异常时
/// 同步 Flush（见 host/FileLogSink.Flush）保证，可靠性不降级。
/// </para>
/// </summary>
public static class DebugLog
{
    /// <summary>写一条追踪（tag 作为消息前缀）。异步入队，不阻塞调用线程。</summary>
    public static void Trace(string tag, string msg) => DiagnosticLog.Trace(tag, msg);

    /// <summary>
    /// 写一条调试级日志：**高频打点（每秒/每次轮询/每次刷新）必须用这个**。
    /// 默认级别 Info 下不落盘，避免实测几天堆出几十万行把关键信息淹没；
    /// 排障时设 <c>BETTERDESKTOP_LOG_LEVEL=Debug</c> 打开。
    /// </summary>
    public static void Debug(string tag, string msg) => DiagnosticLog.Debug(tag, msg);

    /// <summary>遗留兼容：异步管道无需显式 flush（保留方法以免外部引用报错）。</summary>
    public static void FlushNow()
    {
    }
}
