namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板条目内容类型（捕获优先级 HTML > Text &gt; Image &gt; Files）。
/// </summary>
public enum ClipboardItemKind
{
    /// <summary>纯文本。</summary>
    Text = 0,

    /// <summary>图片（字节落盘，模型存路径元数据）。</summary>
    Image = 1,

    /// <summary>文件列表（资源管理器复制）。</summary>
    Files = 2,

    /// <summary>HTML 富文本。</summary>
    Html = 3,

    /// <summary>RTF 富文本（三格式并存时使用）。</summary>
    RichText = 4,
}
