// BetterDesktop.Shell.ContextMenus — 同层唯一助记（Alt 访问键）分配器
// Win10 菜单惯例：每项一个字母助记（带下划线），Alt+字母直达。
// 规则（保守、可预期）：
//   1. 文本里已显式标注 (_X) / (&X) → 采用它，并占用该键；
//   2. 否则取文本中首个未被占用的 ASCII 字母/数字（中文无拉丁字符 → 不分配）；
//   3. 冲突顺延到下一个可用字符；都不可用 → 该项不分配（不强行制造错误助记）。
// 中文项第一版不引拼音库（避免依赖膨胀），由调用方显式标注 (_X)。

using System.Text;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>菜单项助记分配器（同层唯一）。</summary>
public static class MenuAccessKeys
{
    /// <summary>
    /// 为同层文本分配助记，返回与输入等长的列表：已分配的文本插入 &amp;（如 "打开(_O)"），未分配为 null。
    /// </summary>
    public static List<string?> Assign(IReadOnlyList<string> texts)
    {
        var result = new List<string?>(texts.Count);
        // 归一化到大写后占用（StringComparer 不适配 HashSet<char>）
        var used = new HashSet<char>();
        bool Occupy(char ch) => used.Add(char.ToUpperInvariant(ch));

        // 第一轮：显式标注优先（(_X) 或 (&X)）
        for (var i = 0; i < texts.Count; i++)
        {
            result.Add(null);
            var marker = FindExplicitMarker(texts[i]);
            if (marker is not { } m)
            {
                continue;
            }

            if (!Occupy(m.Char))
            {
                continue; // 显式标注冲突：让位给先出现者，该项交第二轮自动分配
            }

            result[i] = Build(texts[i], m);
        }

        // 第二轮：自动分配（首个未被占用的 ASCII 字母/数字）
        for (var i = 0; i < texts.Count; i++)
        {
            if (result[i] is not null)
            {
                continue;
            }

            var text = texts[i];
            for (var p = 0; p < text.Length; p++)
            {
                var ch = text[p];
                if (!char.IsAsciiLetterOrDigit(ch) || !Occupy(ch))
                {
                    continue;
                }

                result[i] = string.Concat(text.AsSpan(0, p), "_", text.AsSpan(p));
                break;
            }
        }

        return result;
    }

    /// <summary>查找显式标注 (_X) / (&amp;X)，返回其在文本中的位置与字符。</summary>
    private static (int Index, char Char)? FindExplicitMarker(string text)
    {
        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (text[i] != '(')
            {
                continue;
            }

            if (text[i + 1] is '_' or '&')
            {
                var ch = text[i + 2];
                if (text[i + 3] == ')')
                {
                    return (i, ch);
                }
            }
        }
        return null;
    }

    private static string Build(string text, (int Index, char Char) marker)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append(text.AsSpan(0, marker.Index));
        sb.Append('_').Append(marker.Char);
        sb.Append(text.AsSpan(marker.Index + 4)); // 跳过 "(_X)"/"(&X)" 四个字符
        return sb.ToString();
    }
}
