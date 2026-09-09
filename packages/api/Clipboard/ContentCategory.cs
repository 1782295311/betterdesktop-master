namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 内容语义分类（入库时计算一次并持久化，供分类筛选与「合适粘贴」策略）。
/// 分类顺序：File &gt; Image &gt; Code &gt; RichText &gt; Text。
/// </summary>
public enum ContentCategory
{
    /// <summary>普通文字。</summary>
    Text = 0,

    /// <summary>代码（粘贴时强制纯文本，保留缩进换行）。</summary>
    Code = 1,

    /// <summary>富文本（HTML/RTF 三格式并存）。</summary>
    RichText = 2,

    /// <summary>图片。</summary>
    Image = 3,

    /// <summary>文件列表。</summary>
    File = 4,
}
