using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>HTML 片段（自动分段产物）。<see cref="IsFallback"/> 为 true 表示该片段无法安全切分，已降级为纯文本语义（调用方应纯文本写回）。</summary>
internal sealed record HtmlSegment(string Content, bool IsFallback);

/// <summary>
/// 大段内容自动分段器（Phase B D3，内部机制、用户无感）。
/// HTML 按块级边界标签切分并保证片段自包含；纯文本按空行拆段。RTF 条目不分段（由调用方判定）。
/// 片段不携带 CF_HTML 头（统一由 <see cref="ClipboardNative.SetHtmlText"/> 包装）。
/// </summary>
internal static class ClipboardSegmenter
{
    // 块级结束标签：分段边界（边界标签本身是分隔符，不进入任何片段）。
    private static readonly Regex BlockEndRegex = new(
        @"</(p|div|li|tr|h1|h2|h3|h4|h5|h6|ul|ol|table|blockquote|section|article|header|footer|figure|figcaption)\s*>|<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 块级开始标签（片段内残留的开放边界，剥离之）。
    private static readonly Regex BlockOpenAtStartRegex = new(
        @"^\s*<(p|div|li|tr|h1|h2|h3|h4|h5|h6|ul|ol|table|blockquote|section|article|header|footer|figure|figcaption)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockOpenAtEndRegex = new(
        @"<(p|div|li|tr|h1|h2|h3|h4|h5|h6|ul|ol|table|blockquote|section|article|header|footer|figure|figcaption)\b[^>]*>\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 任意标签（配对校验用）。
    private static readonly Regex AnyOpenTagRegex = new(@"<([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex CloseTagRegex = new(@"</([a-zA-Z][a-zA-Z0-9]*)\s*>", RegexOptions.Compiled);
    private static readonly Regex AnyTagRegex = new("<[^>]*>", RegexOptions.Compiled);

    // void/自闭合元素：不参与配对。
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "img", "hr", "input", "meta", "link",
    };

    /// <summary>拆分 HTML：返回自包含片段列表。无法安全切分时整体降级为单个 IsFallback 片段。</summary>
    public static IReadOnlyList<HtmlSegment> SplitHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<HtmlSegment>();
        }

        try
        {
            // 1) 按块级边界切分；边界标签本身丢弃（分隔符）。
            var raw = new List<string>();
            int start = 0;
            foreach (Match m in BlockEndRegex.Matches(html))
            {
                if (m.Index > start)
                {
                    raw.Add(html.Substring(start, m.Index - start));
                }

                start = m.Index + m.Length;
            }

            if (start < html.Length)
            {
                raw.Add(html.Substring(start));
            }

            // 2) 清洗每段：剥离残留的块级开放标签 → 校验配对 → 建片段。
            var segments = new List<HtmlSegment>();
            foreach (string part in raw)
            {
                string cleaned = TrimOpenBlockTags(part.Trim());
                if (string.IsNullOrWhiteSpace(StripTags(cleaned)))
                {
                    continue; // 纯结构段（仅标签无内容），丢弃
                }

                if (IsBalanced(cleaned))
                {
                    segments.Add(new HtmlSegment(cleaned, false));
                }
                else
                {
                    string fallback = StripToPlainText(cleaned);
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        segments.Add(new HtmlSegment(fallback, true));
                    }
                }
            }

            if (segments.Count == 0)
            {
                // 全部为空/降级为空：整体单段（校验或纯文本降级）。
                return IsBalanced(html)
                    ? new List<HtmlSegment> { new(html, false) }
                    : new List<HtmlSegment> { new(StripToPlainText(html), true) };
            }

            return segments;
        }
        catch (Exception)
        {
            // 任何解析异常：整体降级纯文本，绝不产出损坏 HTML。
            return new List<HtmlSegment> { new(StripToPlainText(html), true) };
        }
    }

    /// <summary>拆分纯文本：按空行拆段，去首尾空行；无空行单段不拆（保持单次粘贴）。</summary>
    public static IReadOnlyList<string> SplitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        string[] parts = Regex.Split(text, @"\n\s*\n");
        var result = new List<string>();
        foreach (string part in parts)
        {
            string trimmed = part.Trim('\r', '\n', ' ', '\t');
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        // 无空行 → 单段不拆（保持原样，含原换行）。
        return result.Count <= 1 ? new List<string> { text } : result;
    }

    /// <summary>剥离片段首/尾残留的开放块级开始标签（属于相邻段的边界），循环直至无残留。</summary>
    private static string TrimOpenBlockTags(string segment)
    {
        string result = segment;
        bool changed = true;
        while (changed)
        {
            changed = false;
            string trimmedStart = BlockOpenAtStartRegex.Replace(result, string.Empty, 1);
            if (trimmedStart.Length != result.Length)
            {
                result = trimmedStart;
                changed = true;
            }

            string trimmedEnd = BlockOpenAtEndRegex.Replace(result, string.Empty, 1);
            if (trimmedEnd.Length != result.Length)
            {
                result = trimmedEnd;
                changed = true;
            }
        }

        return result;
    }

    /// <summary>简单标签配对校验：栈式匹配开始/结束标签；void 元素跳过。只校验配对，不校验属性。</summary>
    private static bool IsBalanced(string html)
    {
        var stack = new Stack<string>();
        foreach (Match m in AnyOpenTagRegex.Matches(html))
        {
            string tag = m.Groups[1].Value;
            if (VoidTags.Contains(tag))
            {
                continue;
            }

            stack.Push(tag.ToLowerInvariant());
        }

        foreach (Match m in CloseTagRegex.Matches(html))
        {
            string tag = m.Groups[1].Value.ToLowerInvariant();
            if (stack.Count == 0 || !string.Equals(stack.Peek(), tag, StringComparison.Ordinal))
            {
                return false;
            }

            stack.Pop();
        }

        return stack.Count == 0;
    }

    private static string StripTags(string html) => AnyTagRegex.Replace(html, string.Empty);

    private static string StripToPlainText(string html) => WebUtility.HtmlDecode(StripTags(html)).Trim();

    /// <summary>片段写回时的纯文本伴生（HTML 段同时提供 Text 格式，供目标应用降级）。</summary>
    internal static string StripToPlainTextForWrite(string html) => WebUtility.HtmlDecode(StripTags(html)).Trim();
}
