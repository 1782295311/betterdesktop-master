// BetterDesktop.Shell.ContextMenus — Win32 菜单文本 → WPF 菜单文本解码器
// 来源文本（COM GetMenuStringW / 注册表 MUIVerb）是 Win32 惯例：&X = 助记、&& = 字面 &。
// WPF 惯例是 _X（AccessText），& 会原样显示（实测差评："&7-Zip"）。
// 规则：
//   &&        → 字面 &
//   (&X)      → (_X)   —— 厂商常见 "压缩(&S)" 形态，保留助记交给 MenuAccessKeys
//   其余 &X   → X      —— 剥掉裸前缀（"&7-Zip" → "7-Zip"；自动助记由 MenuAccessKeys 二轮分配）

namespace BetterDesktop.Shell.ContextMenus.Services;

public static class MenuText
{
    public static string FromWin32(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&'))
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                break; // 尾部孤立 &：丢弃
            }

            var next = text[i + 1];
            if (next == '&')
            {
                sb.Append('&');
                i++;
                continue;
            }

            // "(&X)" → "(_X)"：括号形态整体改写助记符
            if (i > 0 && text[i - 1] == '(' && i + 2 < text.Length && text[i + 2] == ')')
            {
                sb.Append('_').Append(next);
                i++;
                continue;
            }

            // 其余 "&X" → "X"（剥前缀；助记交自动分配，避免 & 落到文本中间造成 "7-&Zip"）
            sb.Append(next);
            i++;
        }

        return sb.ToString();
    }
}
