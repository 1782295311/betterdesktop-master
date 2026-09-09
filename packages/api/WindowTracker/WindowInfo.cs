namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// 单个窗口的轻量快照。
/// </summary>
/// <param name="Hwnd">窗口句柄。</param>
/// <param name="ProcessId">所属进程 Id。</param>
/// <param name="Title">窗口标题。</param>
/// <param name="IsMinimized">是否最小化。</param>
public sealed record WindowInfo(
    IntPtr Hwnd,
    uint ProcessId,
    string Title,
    bool IsMinimized);
