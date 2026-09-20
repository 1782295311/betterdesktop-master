using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Ocr.Contracts;

namespace BetterDesktop.Shell.Capture.Ocr;

/// <summary>后处理选项（纯函数参数；缺省即默认）。</summary>
public sealed record OcrPostProcessOptions(
    float ColumnGapRatio = 0.08f,
    float LineOverlapRatio = 0.6f,
    float LowConfidenceThreshold = 0.6f,
    float DenoiseConfidence = 0.5f)
{
    public static OcrPostProcessOptions Default { get; } = new();
}

/// <summary>后处理结果（UI 直接消费：正文 / 块 / 低置信词 / 版式判定）。</summary>
public sealed record OcrPostProcessResult(
    string Text,
    IReadOnlyList<OcrBlock> Blocks,
    IReadOnlyList<OcrWord> LowConfidence,
    bool IsVertical,
    bool IsTable,
    int ColumnCount);

/// <summary>
/// OCR 后处理（纯函数，无 IO 无引擎——合成语料可直接单测）。
/// <para>
/// 对应 OCR 计划 §4 后处理表：版式重建（多栏切分/行聚合/表格→Markdown）、竖排断行、
/// 中英空格归一、标点归一、去噪（屏幕伪影）、低置信标记。
/// 设计原则：全部输入词带像素框与置信度；输出可读正文 + 块 + 低置信词；
/// 低置信词**保留在正文里**（让用户看到原文），另附列表供 UI 底色提示。
/// </para>
/// </summary>
public static class OcrPostProcessor
{
    // CJK 判定字符类：统一表意文字 + CJK 符号标点 + 全角形式（"你好，world" 的中文逗号也参与空格归一）
    private const string CjkClass = @"\p{IsCJKUnifiedIdeographs}\u3000-\u303F\uFF00-\uFFEF";

    /// <summary>
    /// 组合入口：去噪 → 竖排判定 → 表格判定 → 多栏切分 → 行聚合 → 空格/标点归一。
    /// </summary>
    public static OcrPostProcessResult Reconstruct(IReadOnlyList<OcrWord> words, OcrPostProcessOptions? options = null)
    {
        var o = options ?? OcrPostProcessOptions.Default;
        var denoised = Denoise(words, o.DenoiseConfidence);
        var low = LowConfidenceWords(denoised, o.LowConfidenceThreshold);

        var vertical = DetectVerticalText(denoised);
        if (vertical is not null)
        {
            var box = Union(denoised);
            return new OcrPostProcessResult(
                vertical, new[] { new OcrBlock(vertical, box, LineCount: 1) }, low,
                IsVertical: true, IsTable: false, ColumnCount: 1);
        }

        var table = ToMarkdownTable(denoised);
        if (table is not null)
        {
            var box = Union(denoised);
            return new OcrPostProcessResult(
                table, new[] { new OcrBlock(table, box, LineCount: 1) }, low,
                IsVertical: false, IsTable: true, ColumnCount: 0);
        }

        var columns = SplitColumns(denoised, o.ColumnGapRatio);
        var lines = columns.SelectMany(c => GroupLinesDetailed(c)).ToList();
        var text = string.Join("\n", lines.Select(l => l.Text));
        var blocks = lines.Select(l => new OcrBlock(l.Text, l.Box, LineCount: 1)).ToList();
        return new OcrPostProcessResult(text, blocks, low, IsVertical: false, IsTable: false, ColumnCount: columns.Count);
    }

    // ---- 多栏切分 ----

    /// <summary>
    /// 按词盒分布把词表切成 N 栏（单栏时返回 1 栏）。
    /// 算法（稳健处理多栏交错行）：①按 Y 带聚行 → ②行内按 X 间隙切段 → ③跨行按 X 范围重叠归并成栏。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<OcrWord>> SplitColumns(
        IReadOnlyList<OcrWord> words, float gapRatio = 0.08f)
    {
        if (words is null || words.Count == 0)
        {
            return Array.Empty<IReadOnlyList<OcrWord>>();
        }

        var lines = GroupRawLines(words);
        var pageWidth = Math.Max(1, words.Max(w => w.Box.Right) - words.Min(w => w.Box.X));

        // ①行内按 X 间隙切段（间隙 > 行高中位数×1.2 且 > 页宽×gapRatio → 栏断）
        var gapThreshold = Math.Max(6f, Math.Max(pageWidth * gapRatio, 12f));
        var segments = new List<(List<OcrWord> Words, double Center, double Width)>();
        foreach (var line in lines)
        {
            var current = new List<OcrWord> { line[0] };
            for (var i = 1; i < line.Count; i++)
            {
                if (line[i].Box.X - line[i - 1].Box.Right > gapThreshold)
                {
                    segments.Add(SegmentOf(current));
                    current = new List<OcrWord>();
                }

                current.Add(line[i]);
            }

            segments.Add(SegmentOf(current));
        }

        // ②跨行按 X 范围重叠归并成栏（中心差 < 两段宽度较大者×1.2 → 同栏）
        var columns = new List<List<OcrWord>>();
        foreach (var seg in segments)
        {
            var col = columns.FirstOrDefault(c =>
                Math.Abs(seg.Center - CenterOf(c)) < Math.Max(seg.Width, WidthOf(c)) * 1.2);
            if (col is null)
            {
                col = new List<OcrWord>();
                columns.Add(col);
            }

            col.AddRange(seg.Words);
        }

        return columns;
    }

    private static (List<OcrWord> Words, double Center, double Width) SegmentOf(List<OcrWord> words)
    {
        var xs = words.Select(w => w.Box.X).Min();
        var right = words.Select(w => w.Box.Right).Max();
        return (words, (xs + right) / 2.0, Math.Max(1, right - xs));
    }

    private static double CenterOf(List<OcrWord> words)
        => (words.Select(w => w.Box.X).Min() + words.Select(w => w.Box.Right).Max()) / 2.0;

    private static double WidthOf(List<OcrWord> words)
        => Math.Max(1, words.Select(w => w.Box.Right).Max() - words.Select(w => w.Box.X).Min());

    // ---- 行聚合 ----

    /// <summary>行聚合为文本行（每行内按 X 排序拼接；拉丁词间保空格，CJK 邻接无空格）。</summary>
    public static IReadOnlyList<string> GroupLines(IReadOnlyList<OcrWord> words)
        => GroupLinesDetailed(words).Select(l => l.Text).ToList();

    private sealed record Line(string Text, PixelRect Box);

    /// <summary>按 Y 带聚行（行内按 X 排序）；GroupLinesDetailed 与 SplitColumns 共用。</summary>
    private static List<List<OcrWord>> GroupRawLines(IReadOnlyList<OcrWord> words, float overlapRatio = 0.6f)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Box.CenterY).ThenBy(w => w.Box.X))
        {
            if (lines.Count == 0)
            {
                lines.Add(new List<OcrWord> { w });
                continue;
            }

            var current = lines[^1];
            var lineHeight = Math.Max(1, Math.Max(current.Max(x => x.Box.Height), w.Box.Height));
            var baseline = current.Average(x => x.Box.CenterY);
            if (Math.Abs(w.Box.CenterY - baseline) <= lineHeight * overlapRatio)
            {
                current.Add(w);
            }
            else
            {
                lines.Add(new List<OcrWord> { w });
            }
        }

        return lines;
    }

    private static IReadOnlyList<Line> GroupLinesDetailed(IReadOnlyList<OcrWord> words, float overlapRatio = 0.6f)
    {
        return GroupRawLines(words, overlapRatio)
            .Select(lineWords => lineWords.OrderBy(w => w.Box.X).ToList())
            .Select(BuildLine)
            .ToList();
    }

    private static Line BuildLine(IReadOnlyList<OcrWord> lineWords)
    {
        var sb = new StringBuilder();
        OcrWord? prev = null;
        foreach (var w in lineWords)
        {
            if (prev is not null)
            {
                var gap = w.Box.X - prev.Box.Right;
                var prevIsCjk = IsCjkChar(prev.Text);
                var curIsCjk = IsCjkChar(w.Text);
                if (!prevIsCjk && !curIsCjk && gap > 0 && !prev.Text.EndsWith(' ') && !w.Text.StartsWith(' '))
                {
                    sb.Append(' '); // 拉丁词之间保留一个空格
                }
            }

            sb.Append(w.Text);
            prev = w;
        }

        var text = NormalizeCjkSpacing(NormalizePunctuation(sb.ToString()));
        return new Line(text, Union(lineWords));
    }

    // ---- 竖排断行 ----

    /// <summary>
    /// 竖排判定（返回竖排文本，非竖排返回 null）。
    /// 判据：≥60% 词为"高瘦单字"（CJK 且高 &gt; 宽×1.5）且 X 中心跨度 ≤ 中位字宽×2.5 →
    /// 按 Y 自上而下连读（中文竖排不加空格）。
    /// </summary>
    public static string? DetectVerticalText(IReadOnlyList<OcrWord> words)
    {
        if (words is null || words.Count < 2)
        {
            return null;
        }

        var tall = words
            .Where(w => w.Box.Height > w.Box.Width * 1.5 && w.Text.All(c => IsCjk(c)))
            .ToList();
        if (tall.Count < words.Count * 0.6)
        {
            return null;
        }

        var xSpan = tall.Max(w => w.Box.CenterX) - tall.Min(w => w.Box.CenterX);
        var medianWidth = Median(tall.Select(w => (double)w.Box.Width));
        if (xSpan > medianWidth * 2.5)
        {
            return null; // X 分布太宽 → 是正常横排的窄字，不是竖排
        }

        var chars = tall
            .OrderBy(w => w.Box.CenterY)
            .Select(w => w.Text.Trim())
            .Where(t => t.Length > 0);
        return string.Concat(chars);
    }

    // ---- 中英空格归一 ----

    /// <summary>
    /// 中英混排空格归一：CJK 与任意字符之间的空白一律消除（"你好 world"→"你好world"；
    /// "Hello 世界"→"Hello世界"；"你好 世界"→"你好世界"）；拉丁词之间的多空格压成单空格。
    /// </summary>
    public static string NormalizeCjkSpacing(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = Regex.Replace(text, $@"(?<=[{CjkClass}])\s+", string.Empty);
        result = Regex.Replace(result, $@"\s+(?=[{CjkClass}])", string.Empty);
        result = Regex.Replace(result, @"[ \t\u3000]+", " ");
        return result.Trim();
    }

    /// <summary>标点归一：全角 ASCII → 半角、全角空格 → 半角空格、剔除零宽字符。</summary>
    public static string NormalizePunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c >= '\uFF01' && c <= '\uFF5E')
            {
                sb.Append((char)(c - 0xFEE0)); // 全角 ASCII → 半角（Ｆ５→F5、，→,）
            }
            else if (c == '\u3000')
            {
                sb.Append(' ');
            }
            else if (c is '\u200B' or '\uFEFF' or '\u200E' or '\u200F')
            {
                // 零宽/方向字符剔除
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // ---- 表格 → Markdown ----

    /// <summary>
    /// 表格识别（行×列网格）→ Markdown 表。行按 Y 带、列按 X 中心聚类；
    /// 至少 2 行 × 2 列才判为表格，否则返回 null（普通文本走行聚合）。
    /// </summary>
    public static string? ToMarkdownTable(IReadOnlyList<OcrWord> words)
    {
        if (words is null || words.Count < 4)
        {
            return null;
        }

        var lines = GroupLinesDetailed(words);
        if (lines.Count < 2)
        {
            return null;
        }

        // 列中心聚类（间隙阈值 = 相邻 X 差中位数×1.2，至少 8px）
        var colCenters = ClusterColumnCenters(words.Select(w => (double)w.Box.CenterX).ToList());
        if (colCenters.Count < 2)
        {
            return null;
        }

        // 网格：按行（Y 带）→ 行内词归到最近列中心；单元格内多词按 X 排序拼接
        var rawLines = GroupRawLines(words);
        if (rawLines.Count < 2)
        {
            return null;
        }

        var table = new string[rawLines.Count, colCenters.Count];
        for (var r = 0; r < rawLines.Count; r++)
        {
            var cellWords = new Dictionary<int, List<OcrWord>>();
            foreach (var w in rawLines[r].OrderBy(w => w.Box.X))
            {
                var c = NearestColumn(w.Box.CenterX, colCenters);
                if (!cellWords.TryGetValue(c, out var list))
                {
                    list = new List<OcrWord>();
                    cellWords[c] = list;
                }

                list.Add(w);
            }

            for (var c = 0; c < colCenters.Count; c++)
            {
                if (!cellWords.TryGetValue(c, out var list))
                {
                    table[r, c] = string.Empty;
                    continue;
                }

                table[r, c] = string.Join(" ", list.Select(w => w.Text));
            }
        }

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", Row(table, 0, colCenters.Count))).Append(" |");
        sb.Append('\n').Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCenters.Count))).Append(" |");
        for (var r = 1; r < rawLines.Count; r++)
        {
            sb.Append('\n').Append("| ").Append(string.Join(" | ", Row(table, r, colCenters.Count))).Append(" |");
        }

        return sb.ToString();
    }

    private static string[] Row(string[,] table, int r, int cols)
    {
        var cells = new string[cols];
        for (var c = 0; c < cols; c++)
        {
            cells[c] = EscapeCell(table[r, c]);
        }

        return cells;
    }

    private static string EscapeCell(string cell)
    {
        var s = NormalizeCjkSpacing(NormalizePunctuation(cell));
        return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    private static List<double> ClusterColumnCenters(IReadOnlyList<double> xs)
    {
        var sorted = xs.OrderBy(x => x).ToList();
        var medianWidth = Median(sorted.Zip(sorted.Skip(1), (a, b) => (double)Math.Abs(b - a)).ToList());
        var gap = Math.Max(8.0, medianWidth * 1.2);
        var clusters = new List<List<double>> { new() };
        foreach (var x in sorted)
        {
            if (clusters[^1].Count > 0 && x - clusters[^1][^1] > gap)
            {
                clusters.Add(new List<double>());
            }

            clusters[^1].Add(x);
        }

        return clusters.Select(c => c.Average()).ToList();
    }

    private static int NearestColumn(double x, IReadOnlyList<double> centers)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < centers.Count; i++)
        {
            var d = Math.Abs(x - centers[i]);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    // ---- 去噪 ----

    /// <summary>
    /// 屏幕伪影过滤（截屏专用，扫描件无此问题）：
    /// ①低置信 + 单字符 + 高瘦（光标/选中高亮残片/抗锯齿边缘）→ 删除；
    /// ②几乎完全重叠的重复词（抗锯齿边缘双份）→ 保留置信度高者。
    /// </summary>
    public static IReadOnlyList<OcrWord> Denoise(IReadOnlyList<OcrWord> words, float minConfidence = 0.5f)
    {
        if (words is null || words.Count == 0)
        {
            return words ?? Array.Empty<OcrWord>();
        }

        var result = new List<OcrWord>(words);
        var drop = new HashSet<OcrWord>();
        foreach (var w in result)
        {
            if (w.Confidence < minConfidence && w.Text.Length <= 1 && w.Box.Height > w.Box.Width * 1.5)
            {
                drop.Add(w);
            }
        }

        for (var i = 0; i < result.Count; i++)
        {
            for (var j = i + 1; j < result.Count; j++)
            {
                var a = result[i];
                var b = result[j];
                if (drop.Contains(a) || drop.Contains(b))
                {
                    continue;
                }

                if (OverlapRatio(a.Box, b.Box) > 0.8)
                {
                    drop.Add(a.Confidence >= b.Confidence ? b : a);
                }
            }
        }

        result.RemoveAll(drop.Contains);
        return result;
    }

    // ---- 低置信标记 ----

    /// <summary>置信度低于阈值的词（正文保留原文，列表供 UI 底色提示）。</summary>
    public static IReadOnlyList<OcrWord> LowConfidenceWords(IReadOnlyList<OcrWord> words, float threshold = 0.6f)
        => (words ?? Array.Empty<OcrWord>()).Where(w => w.Confidence < threshold).ToList();

    // ---- 工具 ----

    private static bool IsCjkChar(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(IsCjk);

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)          // CJK 统一表意文字
           || (c >= 0x3000 && c <= 0x303F)       // CJK 符号标点
           || (c >= 0xFF00 && c <= 0xFFEF);      // 全角形式

    private static float OverlapRatio(PixelRect a, PixelRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var area = Math.Max(1, Math.Min(a.Area, b.Area));
        return (float)inter / area;
    }

    private static PixelRect Union(IEnumerable<OcrWord> words)
    {
        var list = words.ToList();
        if (list.Count == 0)
        {
            return default;
        }

        var x = list.Min(w => w.Box.X);
        var y = list.Min(w => w.Box.Y);
        var right = list.Max(w => w.Box.Right);
        var bottom = list.Max(w => w.Box.Bottom);
        return new PixelRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
