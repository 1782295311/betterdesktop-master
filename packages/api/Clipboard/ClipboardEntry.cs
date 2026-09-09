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

    /// <summary>图片落盘相对路径（相对存储根，如 clipboard\images\{id}.png）。</summary>
    public string ImagePath { get; set; } = string.Empty;

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

    /// <summary>混合内容标记：含图片（&lt;img / data URI）。</summary>
    public bool HasImages { get; set; }

    /// <summary>混合内容标记：含表格（&lt;table / &lt;tr）。</summary>
    public bool HasTable { get; set; }

    /// <summary>代码标记（粘贴强制纯文本）。</summary>
    public bool IsCode { get; set; }

    /// <summary>列表预览（[图片]/[文件]/[HTML] 前缀 + 截断 200 字）。</summary>
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
                    return string.IsNullOrWhiteSpace(PlainText) ? "[HTML]" : Truncate(PlainText, 200);
                default:
                    return Truncate(Content, 200);
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

        return text.Length <= max ? text : text[..max] + "…";
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
