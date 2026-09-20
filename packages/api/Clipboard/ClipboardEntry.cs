using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板历史条目（公共模型，字段稳定可序列化——供历史面板/搜索框/便签等任意板块消费）。
/// 图片字节不在此模型内（落盘后仅存 <see cref="ImagePath"/> 元数据，见技术库 1301 红线 5）。
/// </summary>
public sealed class ClipboardEntry
{
    /// <summary>
    /// 列表预览截断长度（字符）。
    /// 【2026-09-12 用户要求】长文本要多显示文字以区分"开头相同"的条目：200 → 500 字；
    /// 列表行最多显示 6 行（RecentStrip.PreviewMaxHeight ≈ 170 字），500 留足余量。
    /// </summary>
    private const int PreviewMaxChars = 500;

    /// <summary>稳定标识（GUID N 格式）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>捕获内容类型。</summary>
    public ClipboardItemKind ContentType { get; set; }

    /// <summary>语义分类（入库时计算一次）。</summary>
    public ContentCategory Category { get; set; }

    /// <summary>纯文本内容（HTML 条目为剥离后的文本）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>完整 HTML（HTML/富文本条目保留，含 img/table 标签）。</summary>
    public string HtmlContent { get; set; } = string.Empty;

    /// <summary>RTF 内容（三格式并存时保留）。</summary>
    public string RtfContent { get; set; } = string.Empty;

    /// <summary>复制时间（本地时间）。</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>是否收藏（收藏条目不参与驱逐/过期）。</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 是否表情包（**与收藏同级的独立标记**，2026-09-13）。
    /// <para>
    /// 用户口径："跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" —— 因此本标记可打在
    /// **任何类型**的条目上（文字颜文字 / 静态图 / 动图 / 文件），被打标记的条目：
    /// ① 出现在「表情包」筛选里；② 与收藏一样**豁免驱逐与「清理未收藏」**。
    /// </para>
    /// <para>
    /// 历史沿革：此前表情包是 <see cref="ContentCategory.Sticker"/> **分类**，且只能靠"导入本地文件"产生 ——
    /// 文字颜文字永远无法成为表情包。旧数据由引擎启动迁移转换为本标记。
    /// </para>
    /// </summary>
    public bool IsSticker { get; set; }

    /// <summary>图片落盘相对路径（相对存储根，如 clipboard\images\{id}.png）。</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 内容哈希（引擎写入：图片=像素级指纹、表情包=文件字节 sha256 前 16 hex；旧数据为空字符串）。
    /// <para>
    /// 【2026-09-12 补齐】引擎早已在 JSON 里写该字段，而 C# 模型缺这一项 → 反序列化时被静默丢弃；
    /// 补齐后 legacy 后端可据此做表情包内容去重，图片条目也能暴露指纹供诊断。
    /// </para>
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>图片宽度（px，0=非图片）。</summary>
    public int ImageWidth { get; set; }

    /// <summary>图片高度（px，0=非图片）。</summary>
    public int ImageHeight { get; set; }

    /// <summary>内容字节数（文本=UTF8 字节数；图片=落盘字节数）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>复制次数（「常用」排序依据）。</summary>
    public int CopyCount { get; set; }

    /// <summary>来源进程名（去 .exe）。</summary>
    public string SourceProcessName { get; set; } = string.Empty;

    /// <summary>来源窗口标题。</summary>
    public string SourceWindowTitle { get; set; } = string.Empty;

    /// <summary>文件条目路径列表。</summary>
    public string[] FilePaths { get; set; } = Array.Empty<string>();

    /// <summary>标签（可搜索，空格分隔）。</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// OCR 识别文本（2026-09-14 截图+OCR 接入；引擎摘要返回 ocrText，参与 keyword 搜索命中）。
    /// 仅图片条目可能非空；面板/宿主可直接展示。
    /// </summary>
    public string OcrText { get; set; } = string.Empty;

    /// <summary>混合内容标记：含图片（&lt;img / data URI）。</summary>
    public bool HasImages { get; set; }

    /// <summary>混合内容标记：含表格（&lt;table / &lt;tr）。</summary>
    public bool HasTable { get; set; }

    /// <summary>代码标记（粘贴强制纯文本）。</summary>
    public bool IsCode { get; set; }

    /// <summary>
    /// 命名格式载荷（2026-09-13 P1-4 命名格式透传）。
    /// <para>
    /// Excel / WPS 表格的"可编辑表格"数据只存在于应用自定义的命名格式里 —— 引擎捕获后放在本字段，
    /// 复制时按同名还原，粘回表格仍是可编辑表格。**列表摘要载荷由引擎剥离该字段**（base64 较大），
    /// 因此列表条目拿到的通常是空集合，写回由引擎侧完成。
    /// </para>
    /// </summary>
    public List<ClipboardNamedFormat> NamedFormats { get; set; } = new();

    /// <summary>
    /// 内容是否命中敏感信息（2026-09-13 P2-2：手机号 / 身份证 / 邮箱 / 银行卡 / 密钥）。
    /// <para>
    /// **只用于预览遮罩，不改变内容** —— 用户粘贴时仍是全文（见 <see cref="BuildMaskedPreview"/>）。
    /// </para>
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>列表预览（[图片]/[文件]/[HTML] 前缀 + 截断 <see cref="PreviewMaxChars"/> 字）。</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            switch (ContentType)
            {
                case ClipboardItemKind.Image:
                    return "[图片]";
                case ClipboardItemKind.Files:
                    return FilePaths.Length > 0 ? $"[文件] {System.IO.Path.GetFileName(FilePaths[0])}" : "[文件]";
                case ClipboardItemKind.Html:
                case ClipboardItemKind.RichText:
                    return string.IsNullOrWhiteSpace(PlainText) ? "[HTML]" : Truncate(PlainText, PreviewMaxChars);
                default:
                    return Truncate(Content, PreviewMaxChars);
            }
        }
    }

    /// <summary>剥离 HTML 标签后的纯文本。</summary>
    [JsonIgnore]
    public string PlainText
    {
        get
        {
            if (!string.IsNullOrEmpty(Content))
            {
                return Content;
            }

            return string.IsNullOrEmpty(HtmlContent) ? string.Empty : StripHtml(HtmlContent);
        }
    }

    /// <summary>统计文本（字数/行数）。</summary>
    [JsonIgnore]
    public string StatsText
    {
        get
        {
            string text = PlainText;
            if (ContentType == ClipboardItemKind.Image)
            {
                return $"{ImageWidth}×{ImageHeight}";
            }

            // 【表情包 · 2026-09-12】显示"格式 · 尺寸"（如 "GIF · 300×300"）——
            // 比通用的"N 个文件"有用得多：用户挑表情包时会看动图格式与画面尺寸。
            // 【2026-09-13】判据改为**标记** —— 表情包已不是分类，旧判据永不成立，会让本属性退化成"1 个文件"；
            // 保留旧分类值作遗留数据兜底。
            if (IsSticker || Category == ContentCategory.Sticker)
            {
                string ext = FilePaths.Length > 0
                    ? System.IO.Path.GetExtension(FilePaths[0]).TrimStart('.').ToUpperInvariant()
                    : string.Empty;
                string size = ImageWidth > 0 && ImageHeight > 0
                    ? $"{ImageWidth}×{ImageHeight}"
                    : "尺寸未知";
                return ext.Length > 0 ? $"{ext} · {size}" : size;
            }

            if (ContentType == ClipboardItemKind.Files)
            {
                return $"{FilePaths.Length} 个文件";
            }

            int lines = 0;
            if (text.Length > 0)
            {
                lines = text.Split('\n').Length;
            }

            return $"{text.Length} 字 · {lines} 行";
        }
    }

    /// <summary>分类显示名（文字/代码/富文本/图片/文件；混合追加「·图」「·表」）。</summary>
    [JsonIgnore]
    public string CategoryLabel
    {
        get
        {
            string baseName = Category switch
            {
                ContentCategory.Code => "代码",
                ContentCategory.RichText => "富文本",
                ContentCategory.Image => "图片",
                ContentCategory.File => "文件",
                ContentCategory.Sticker => "表情包",
                _ => "文字",
            };

            if (HasImages)
            {
                baseName += "·图";
            }

            if (HasTable)
            {
                baseName += "·表";
            }

            return baseName;
        }
    }

    /// <summary>来源应用显示名（进程名去 .exe，空则「未知」）。</summary>
    [JsonIgnore]
    public string SourceAppDisplayName
    {
        get
        {
            string name = SourceProcessName;
            if (string.IsNullOrEmpty(name))
            {
                return "未知";
            }

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

    /// <summary>内容类型显示名（文本/图片/文件/HTML/RTF）。</summary>
    [JsonIgnore]
    public string ContentTypeLabel => ContentType switch
    {
        ClipboardItemKind.Image => "图片",
        ClipboardItemKind.Files => "文件",
        ClipboardItemKind.Html => "HTML",
        ClipboardItemKind.RichText => "RTF",
        _ => "文本",
    };

    /// <summary>
    /// 敏感条目的**遮罩预览**（2026-09-13 P2-2）：保留前 <paramref name="leading"/> / 后
    /// <paramref name="trailing"/> 个字符，中间以 <c>••••</c> 替代；非敏感或过短则原样返回预览。
    /// <para>
    /// 仅作用于**展示**：拿本属性渲染列表，实际复制/粘贴仍走完整内容（用户要能原样粘出全文）。
    /// </para>
    /// </summary>
    public string BuildMaskedPreview(int leading, int trailing)
    {
        var text = Preview;
        if (!IsSensitive || text.Length == 0)
        {
            return text;
        }

        leading = Math.Max(0, leading);
        trailing = Math.Max(0, trailing);
        // 太短则不遮（否则整条只剩点，反而无法辨识是什么内容）
        if (text.Length <= leading + trailing + 2)
        {
            return text;
        }

        return $"{CutHead(text, leading)}••••{CutTail(text, trailing)}";
    }

    /// <summary>取前 <paramref name="count"/> 个字符（不切断 UTF-16 代理对）。</summary>
    private static string CutHead(string text, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        if (count >= text.Length)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[count - 1]) ? count - 1 : count;
        return text[..cut];
    }

    /// <summary>取后 <paramref name="count"/> 个字符（不切断 UTF-16 代理对）。</summary>
    private static string CutTail(string text, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        if (count >= text.Length)
        {
            return text;
        }

        var start = text.Length - count;
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>内部生成：捕获完成后补齐元数据（分类已计算时保留）。</summary>
    public void FinalizeMetadata()
    {
        if (ContentType == ClipboardItemKind.Text && Content != null)
        {
            SizeBytes = Encoding.UTF8.GetByteCount(Content);
        }
    }

    /// <summary>复制次数 +1 并刷新时间。</summary>
    public void Touch()
    {
        CopyCount++;
        Timestamp = DateTime.Now;
    }

    /// <summary>判断是否为同内容（去重依据：类型 + 内容指纹）。</summary>
    public bool IsSameContent(ClipboardEntry other)
    {
        if (other is null)
        {
            return false;
        }

        if (ContentType != other.ContentType)
        {
            return false;
        }

        return ContentType switch
        {
            ClipboardItemKind.Image => string.Equals(ImagePath, other.ImagePath, StringComparison.Ordinal),
            ClipboardItemKind.Files => string.Join('\u001f', FilePaths) == string.Join('\u001f', other.FilePaths),
            ClipboardItemKind.Html => string.Equals(HtmlContent, other.HtmlContent, StringComparison.Ordinal),
            ClipboardItemKind.RichText => string.Equals(RtfContent, other.RtfContent, StringComparison.Ordinal),
            _ => string.Equals(Content, other.Content, StringComparison.Ordinal),
        };
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        // 【2026-09-12 修复】不切在 UTF-16 代理对中间：切在中间会产生孤立代理项
        //（emoji / 扩展汉字显示为乱码方块）。若第 max 位是高代理，则少切一位。
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }
        return text[..cut] + "…";
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        // 先剥离 script/style 块，再剥离标签，最后解码常见实体。
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<(script|style)[^>]*>.*?</\1>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(withoutBlocks, "<[^>]+>", string.Empty);
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return decoded.Trim();
    }

    /// <summary>支持剪贴板条目去重（ContentType + 内容指纹的稳定哈希，非加密用途）。</summary>
    [JsonIgnore]
    public string ContentFingerprint
    {
        get
        {
            string payload = ContentType switch
            {
                ClipboardItemKind.Image => $"image:{ImagePath}",
                ClipboardItemKind.Files => $"files:{string.Join('\u001f', FilePaths)}",
                ClipboardItemKind.Html => $"html:{HtmlContent}",
                ClipboardItemKind.RichText => $"rtf:{RtfContent}",
                _ => $"text:{Content}",
            };
            var bytes = Encoding.UTF8.GetBytes(payload);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return System.Convert.ToHexString(hash)[..16];
        }
    }
}
