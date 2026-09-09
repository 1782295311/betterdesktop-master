using System;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 最近复制内容（融合技术库 1301 getLastCopiedContent 想法，供搜索框/启动器取上下文）。
/// </summary>
public sealed record LastCopiedContent(ClipboardItemKind Type, string? ContentOrPath, DateTime Timestamp);
