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

    /// <summary>
    /// 表情包（2026-09-12 新增）：用户**主动填入**的动图（GIF/WebP/APNG…），原文件珍藏、
    /// 不参与过期/容量驱逐，也不被「清理未收藏」删除。
    /// <para>
    /// 只追加不改既有值：旧历史 JSON 里的分类仍是 0-4，反序列化不受影响（契约可加性纪律）。
    /// </para>
    /// </summary>
    Sticker = 5,
}
