namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// OCR/外部来源批量录入项（M1 契约：复用去重/分类/落盘管线）。
/// </summary>
public sealed record ClipboardImportItem(
    ClipboardItemKind Kind,
    string? Content,
    string? HtmlContent = null,
    string? RtfContent = null,
    string? ImagePath = null,
    string[]? FilePaths = null,
    string? SourceApp = null);
