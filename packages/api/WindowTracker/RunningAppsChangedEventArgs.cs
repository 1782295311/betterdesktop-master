using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// <see cref="IWindowTrackerService.RunningAppsChanged"/> 的事件参数。
/// </summary>
public sealed class RunningAppsChangedEventArgs : EventArgs
{
    /// <summary>变化后的运行中应用集合快照。</summary>
    public required IReadOnlyList<RunningAppInfo> Apps { get; init; }
}
