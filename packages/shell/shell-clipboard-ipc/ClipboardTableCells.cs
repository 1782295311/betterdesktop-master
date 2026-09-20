using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 【按格粘 · 2026-09-13】把"一块表格"的剪贴板文本拆成**单元格序列**（判定与拆分的**单一真相源**）。
/// <para>
/// 依据：Excel / WPS 复制区域时，剪贴板纯文本本身就是 <c>\t</c> 分列、<c>\r\n</c> 分行 ——
/// 该结构已完整落在 <see cref="ClipboardEntry.Content"/> 里，因此**不需要 Excel 私有格式**，
/// 也不需要引擎参与。
/// </para>
/// <para>
/// <b>顺序 = 行优先</b>（Excel 视觉顺序）：先第一行所有格子，再第二行 …… 正好对应"表单逐格往下填"。
/// </para>
/// </summary>
public static class ClipboardTableCells
{
    /// <summary>
    /// 单元格数上限：超过视为"整张工作表"，拒绝入队（防误操作把几万格排成队列）。
    /// </summary>
    public const int MaxCells = 2000;

    /// <summary>是否可作为"按格粘"来源（至少 2 列、至少 2 格）。</summary>
    public static bool IsTabular(ClipboardEntry entry) => IsTabular(entry, out _, out _, out _);

    /// <summary>
    /// 判定并给出规模。<paramref name="rows"/> = 有效行数（已丢弃纯空行）、
    /// <paramref name="cols"/> = 最宽行的列数、<paramref name="count"/> = 展平后的单元格总数。
    /// </summary>
    public static bool IsTabular(ClipboardEntry entry, out int rows, out int cols, out int count)
    {
        rows = 0;
        cols = 0;
        count = 0;

        if (entry is null)
        {
            return false;
        }

        // 只有文本类才有 Tab 结构（图片/文件条目无意义）
        if (entry.ContentType is not (ClipboardItemKind.Text or ClipboardItemKind.Html or ClipboardItemKind.RichText))
        {
            return false;
        }

        var text = entry.Content;
        if (string.IsNullOrEmpty(text) || text.IndexOf('\t') < 0)
        {
            return false;
        }

        var parsed = ParseRows(text);
        rows = parsed.Count;
        foreach (var row in parsed)
        {
            count += row.Count;
            if (row.Count > cols)
            {
                cols = row.Count;
            }
        }

        // 至少两列才算"可逐格粘的表格"；单列数据用普通粘贴即可，不必进队列
        return cols >= 2 && count >= 2;
    }

    /// <summary>展平为单元格序列（行优先）；纯空行丢弃、行尾连续空单元格丢弃。</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (var row in ParseRows(text))
        {
            result.AddRange(row);
        }

        return result;
    }

    /// <summary>
    /// 解析为行 × 列。
    /// <para>
    /// <b>清理规则（红线）</b>：丢弃**行尾的连续空单元格**（Excel 复制常带多余 Tab）与**纯空行**
    ///（末尾换行会产出一个空行）—— 否则用户要多按若干次"空粘贴"。
    /// **行内的空单元格保留**（如 3 列中第 2 列为空）：行为可预测，用户据此可清空目标格。
    /// </para>
    /// </summary>
    private static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();

        foreach (var line in EnumerateLines(text))
        {
            var cells = line.Split('\t');

            // 行尾连续空单元格丢弃（至少保留 1 个，供下面的"纯空行"判定）
            var end = cells.Length;
            while (end > 1 && cells[end - 1].Length == 0)
            {
                end--;
            }

            // 整行都是空的（只有一个空单元格）→ 丢弃该行
            if (end == 1 && cells[0].Length == 0)
            {
                continue;
            }

            var row = new List<string>(end);
            for (var i = 0; i < end; i++)
            {
                row.Add(cells[i]);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>按 <c>\r\n</c> / <c>\n</c> / <c>\r</c> 分行（不产出末尾空行）。</summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++; // 吃掉 \n
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
