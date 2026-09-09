namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板历史变更类型（HistoryChanged 载荷）。
/// </summary>
public enum ClipboardChangeKind
{
    /// <summary>新增条目。</summary>
    Added = 0,

    /// <summary>条目更新（去重置顶/收藏/标签/复制次数等）。</summary>
    Updated = 1,

    /// <summary>删除单条/多条。</summary>
    Removed = 2,

    /// <summary>清空（清除非收藏）。</summary>
    Cleared = 3,
}
