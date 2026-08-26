namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// 单个窗口的轻量快照。
/// </summary>
public sealed record WindowInfo(
    /// <summary>窗口句柄。</summary>
    IntPtr Hwnd,
    /// <summary>所属进程 Id。</summary>
    uint ProcessId,
    /// <summary>窗口标题。</summary>
    string Title,
    /// <summary>是否最小化。</summary>
    bool IsMinimized);
