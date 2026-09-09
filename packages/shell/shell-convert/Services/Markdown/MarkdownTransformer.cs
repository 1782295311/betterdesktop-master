using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using BetterDesktop.Kernel.Core;
using Markdig;

namespace BetterDesktop.Shell.Convert.Services.Markdown;

/// <summary>front matter 处理模式（红线 10：默认剥离不进正文；可设为转标题区，规则单一）。</summary>
public enum FrontMatterMode
{
    /// <summary>剥离（默认）。</summary>
    Strip,

    /// <summary>转标题区（key: value → 加粗列表块）。</summary>
    Heading,
}

/// <summary>
/// Markdown 纯托管转换器（Markdig/ReverseMarkdown；零外部进程依赖——md 链必须独立可用，计划 §6.3）。
/// </summary>
public static partial class MarkdownTransformer
{
    /// <summary>front matter 模式设置键（convert.front-matter = strip | heading）。</summary>
    public const string FrontMatterModeKey = "convert.front-matter";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // 表格/任务列表/删除线等（红线 9：标准 md 语法支持）
        .UseYamlFrontMatter()
        .Build();

    /// <summary>YAML front matter 识别（文件开头 --- ... --- 块）。</summary>
    [GeneratedRegex(@"\A---\s*\r?\n(.*?)\r?\n---\s*(\r?\n|$)", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();

    /// <summary>img src 提取。</summary>
    [GeneratedRegex(@"<img\s[^>]*src=""([^""]+)""")]
    private static partial Regex ImgSrcRegex();

    /// <summary>剥离 front matter，返回正文与是否存在标记。</summary>
    public static (string Body, bool HadFrontMatter) StripFrontMatter(string markdown)
    {
        var match = FrontMatterRegex().Match(markdown);
        return match.Success ? (markdown[match.Length..], true) : (markdown, false);
    }

    /// <summary>front matter 文本（不存在返回 null）。</summary>
    public static string? ExtractFrontMatter(string markdown) =>
        FrontMatterRegex().Match(markdown) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>front matter → 标题区块（按 key: value 逐行；无冒号行忽略）。</summary>
    public static string FrontMatterToHeadingBlock(string frontMatter)
    {
        var lines = frontMatter.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l.Contains(':'))
            .Select(l =>
            {
                var idx = l.IndexOf(':');
                return $"- **{l[..idx].Trim()}**: {l[(idx + 1)..].Trim()}";
            });
        return string.Join("\n", lines) + "\n\n";
    }

    /// <summary>按设置模式应用 front matter 处理并产出正文（红线 10 单一规则入口）。</summary>
    public static string ApplyFrontMatter(string markdown, FrontMatterMode mode)
    {
        var (body, had) = StripFrontMatter(markdown);
        if (!had || mode == FrontMatterMode.Strip)
        {
            return body;
        }
        var front = ExtractFrontMatter(markdown)!;
        return FrontMatterToHeadingBlock(front) + body;
    }

    /// <summary>md → html 片段（表格/任务列表齐备；Markdig.Markdown 全限定避免本域 Markdown 命名空间遮蔽）。</summary>
    public static string ToHtmlBody(string markdown) => Markdig.Markdown.ToHtml(markdown, Pipeline);

    /// <summary>
    /// 独立 HTML 页（红线 8：内嵌 CSS 脱离程序可开；CJK 字体栈；代码块/表格/任务列表样式齐备）。
    /// </summary>
    public static string BuildStandaloneHtml(string title, string bodyHtml) =>
        """
        <!DOCTYPE html>
        <html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>TITLE_PLACEHOLDER</title>
<style>
body { font-family: "Segoe UI", "Microsoft YaHei", "PingFang SC", sans-serif; line-height: 1.65; color: #1f2328; max-width: 860px; margin: 2em auto; padding: 0 1.2em; }
h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin-top: 1.4em; }
code, pre { font-family: Consolas, "Courier New", monospace; background: #f6f8fa; border-radius: 6px; }
code { padding: .15em .4em; }
pre { padding: .8em 1em; overflow: auto; }
pre code { background: none; padding: 0; }
table { border-collapse: collapse; margin: 1em 0; }
th, td { border: 1px solid #d0d7de; padding: .4em .8em; }
th { background: #f6f8fa; }
blockquote { border-left: 4px solid #d0d7de; margin: 1em 0; padding: .2em 1em; color: #59636e; }
img { max-width: 100%; }
ul.task-list { list-style: none; padding-left: 1.2em; }
input[type="checkbox"] { margin-right: .4em; }
hr { border: none; border-top: 1px solid #d0d7de; margin: 2em 0; }
</style>
</head>
<body>
BODY_PLACEHOLDER
</body>
</html>
""".Replace("TITLE_PLACEHOLDER", WebUtility.HtmlEncode(title)).Replace("BODY_PLACEHOLDER", bodyHtml);

    /// <summary>html → 纯文本（script/style 剔除；块级标签转行；实体解码）。</summary>
    public static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|h[1-6]|li|tr|table|blockquote|pre)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        // 压缩 3+ 连续空行为 2
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim() + "\n";
    }

    /// <summary>纯文本 → 简单 html 段落（转义后按空行分段——诚实能力：无 md 语法解释）。</summary>
    public static string PlainTextToHtml(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + WebUtility.HtmlEncode(p).Replace("\n", "<br>\n") + "</p>");
        return string.Join("\n", paragraphs);
    }

    /// <summary>
    /// 解析 html 中相对图片路径（红线 7）：相对 src → 绝对 file URI（两跳中转阶段使用）；
    /// 缺失图片逐张告警，不产出静默残缺文档。
    /// </summary>
    public static string ResolveRelativeImageSrcs(string html, string baseDirectory)
    {
        return ImgSrcRegex().Replace(html, match =>
        {
            var src = match.Groups[1].Value;
            if (Uri.TryCreate(src, UriKind.Absolute, out _)
                || src.StartsWith('#') || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value; // 绝对 URL/data/锚点不动
            }
            var absolute = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(src)));
            if (!File.Exists(absolute))
            {
                DiagnosticLog.Trace("shell-convert", $"图片缺失(告警不阻断): {src}");
                return match.Value;
            }
            return match.Value.Replace(src, new Uri(absolute).AbsoluteUri);
        });
    }

    /// <summary>文件名清理（红线 13 / unified-multiformat-export）：Windows 非法字符 → _；清完为空/纯下划线回落 output。</summary>
    public static string SanitizeFileName(string name)
    {
        var cleaned = Regex.Replace(name, """[\\/:*?"<>|]""", "_").Trim();
        return cleaned.Length > 0 && cleaned.Trim('_').Length > 0 ? cleaned : "output";
    }
}
