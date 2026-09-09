using System;
using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>内容语义分类结果。</summary>
internal readonly record struct ContentProfile(ContentCategory Category, bool HasImages, bool HasTable, bool IsCode);

/// <summary>
/// 内容语义分类器（纯逻辑，可 headless 单测）。
/// 分类顺序：File &gt; Image &gt; Code &gt; RichText &gt; Text；混合内容用 Flags（HasImages/HasTable）表达。
/// 代码检测 = 行首缩进占比 + 关键字密度 + 注释特征 + 括号配对（不猜语言）。
/// </summary>
internal static class ContentAnalyzer
{
    private static readonly string[] CodeKeywords =
    {
        "if ", "for ", "while ", "return ", "function", "def ", "class ", "import ",
        "using ", "var ", "const ", "let ", "public ", "private ", "protected ", "static ",
        "void ", "int ", "string ", "bool ", "namespace ", "=>", "->", "end", "interface ", "struct ",
    };

    private static readonly string[] CodeCommentMarkers = { "//", "/*", "*/", "#", "--", "<!--" };

    public static ContentProfile Analyze(ClipboardItemKind kind, string html, string text)
    {
        if (kind == ClipboardItemKind.Files)
        {
            return new ContentProfile(ContentCategory.File, HasImages: false, HasTable: false, IsCode: false);
        }

        if (kind == ClipboardItemKind.Image)
        {
            return new ContentProfile(ContentCategory.Image, HasImages: false, HasTable: false, IsCode: false);
        }

        bool hasImages = html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasTable = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase) >= 0
                        || html.IndexOf("<tr", StringComparison.OrdinalIgnoreCase) >= 0;

        string probe = string.IsNullOrWhiteSpace(text) ? html : text;
        bool isCode = IsCodeText(probe);

        if (isCode)
        {
            return new ContentProfile(ContentCategory.Code, hasImages, hasTable, IsCode: true);
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            return new ContentProfile(ContentCategory.RichText, hasImages, hasTable, IsCode: false);
        }

        return new ContentProfile(ContentCategory.Text, HasImages: false, HasTable: false, IsCode: false);
    }

    /// <summary>代码特征判定：缩进占比/关键字密度/注释特征 + 括号配对（行数过少不判）。</summary>
    internal static bool IsCodeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] lines = text.Split('\n');
        if (lines.Length < 3)
        {
            return false;
        }

        int nonEmpty = 0;
        int indented = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            nonEmpty++;
            string trimmed = line.TrimStart();
            if (trimmed.Length < line.Length)
            {
                indented++;
            }
        }

        if (nonEmpty == 0)
        {
            return false;
        }

        double indentRatio = indented / (double)nonEmpty;
        int keywordHits = 0;
        foreach (string kw in CodeKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                keywordHits++;
            }
        }

        bool hasComment = false;
        foreach (string marker in CodeCommentMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                hasComment = true;
                break;
            }
        }

        int openParen = CountChar(text, '{') + CountChar(text, '(') + CountChar(text, '[');
        int closeParen = CountChar(text, '}') + CountChar(text, ')') + CountChar(text, ']');
        bool balanced = Math.Abs(openParen - closeParen) <= Math.Max(2, openParen / 4);

        bool strongSignal = indentRatio >= 0.4 || keywordHits >= 2 || (hasComment && keywordHits >= 1);
        return strongSignal && balanced;
    }

    private static int CountChar(string text, char c)
    {
        int count = 0;
        foreach (char ch in text)
        {
            if (ch == c)
            {
                count++;
            }
        }

        return count;
    }
}
