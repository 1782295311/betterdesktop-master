using System;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板历史变更事件载荷。
/// </summary>
public sealed record ClipboardHistoryChangedEventArgs(ClipboardChangeKind Change, string EntryId);
