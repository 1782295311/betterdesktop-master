using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Pinning.Contracts;

/// <summary>固定列表变更事件参数。</summary>
public sealed class PinnedChangedEventArgs : EventArgs
{
    public PinnedChangedEventArgs(string zone, IReadOnlyList<PinnedItem> items)
    {
        Zone = zone;
        Items = items;
    }

    /// <summary>发生变更的 zone。</summary>
    public string Zone { get; }

    /// <summary>该 zone 变更后的完整固定项列表（只读快照）。</summary>
    public IReadOnlyList<PinnedItem> Items { get; }
}
