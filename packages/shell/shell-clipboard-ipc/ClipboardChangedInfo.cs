using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 灵动岛/搜索框用的轻量复制摘要（引擎 clipboard_changed 事件载荷，引擎侧已截断 textPreview ≤200 字符）。
/// </summary>
public sealed record ClipboardChangedInfo(
    string Id,
    ClipboardItemKind ContentType,
    string TextPreview,
    string ThumbPath,
    string SourceApp,
    DateTime CopiedAt);
