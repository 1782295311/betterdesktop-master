namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 用户自定义热键的**文本规范** ⇄ Win32 注册参数（**单一真相源** · 2026-09-13）。
/// <para>
/// 【为什么必须共用一份】规范串由**设置界面写入**（录制用户按键），由**面板读取并注册**
/// （`RegisterHotKey`）。两边各写一遍解析必然漂移 —— 例如设置里录成 `Ctrl+Shift+Enter`
/// 而面板只认 `Ctrl+Shift+Return`，症状就是"设置里配了热键、按下去却没反应"。
/// 本项目在"指纹键构造"上已经踩过一次同类坑（见 TECH-KNOWLEDGE 1301），故此处收敛：
/// 主键一律用 WPF `Key` 枚举名（`Enter` / `D1` / `F5`）作为**唯一书写形式**，
/// 两侧都经 `Enum.Parse&lt;Key&gt;` / `ToString()` 处理，天然一致。
/// </para>
/// <para>
/// 规范串：修饰键在前、主键在后，`+` 分隔，顺序固定 Ctrl → Shift → Alt → Win → 主键。
/// 例：`Ctrl+Shift+Enter`、`Alt+D1`（显示为 `Alt+1`）、`Ctrl+Alt+F5`。
/// </para>
/// <para>
/// 【为什么要求至少一个修饰键】裸键（如单独的 `F5`）极易与其它程序/系统抢占，用户自己也容易误触。
/// </para>
/// </summary>
public static class HotkeySpec
{
    // ---- Win32 RegisterHotKey 修饰键位（winuser.h）----

    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>MOD_NOREPEAT（0x4000）：按住不连发 —— 与引擎内置热键同口径（7413 纪律）。</summary>
    public const int ModNoRepeat = 0x4000;

    /// <summary>
    /// 引擎内置的全局热键。面板/设置都不该让用户抢这些组合：抢了要么注册失败，
    /// 要么把引擎的"打开面板 / 暂停 / 删除最近一条"顶掉（用户以为热键坏了）。
    /// </summary>
    public static readonly string[] BuiltInHotkeys =
    {
        "Ctrl+Shift+V", "Ctrl+Shift+P", "Ctrl+Shift+Backspace",
    };

    /// <summary>与内置热键冲突？（大小写/别名不敏感，如 `ctrl+return` 也会被判为冲突）</summary>
    public static bool ConflictsWithBuiltIn(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var normalized = NormalizeSpelling(spec);
        foreach (var builtin in BuiltInHotkeys)
        {
            if (string.Equals(NormalizeSpelling(builtin), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 解析规范串 → 修饰键位 + 主键名。
    /// 面板侧再用 `KeyInterop.VirtualKeyFromKey(Enum.Parse&lt;Key&gt;(mainKey))` 换成虚拟键码。
    /// </summary>
    /// <param name="modifiers">Win32 fsModifiers（已含 MOD_NOREPEAT），失败为 0。</param>
    /// <param name="mainKey">主键名（WPF `Key` 枚举名，如 `Enter` / `D1`），失败为空。</param>
    /// <param name="canonical">规范化后的规范串（统一大小写与顺序），失败为空。</param>
    public static bool TryParse(string? spec, out int modifiers, out string mainKey, out string canonical)
    {
        modifiers = 0;
        mainKey = string.Empty;
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var ctrl = false;
        var shift = false;
        var alt = false;
        var win = false;
        var main = string.Empty;

        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "win" or "windows" or "meta":
                    win = true;
                    continue;
            }

            if (main.Length > 0)
            {
                return false; // 出现两个主键 → 非法（`Ctrl+A+B`）
            }
            main = raw;
        }

        if (main.Length == 0)
        {
            return false;
        }

        // 必须至少一个修饰键（见类注释）
        if (!ctrl && !shift && !alt && !win)
        {
            return false;
        }

        modifiers = (ctrl ? ModControl : 0)
            | (shift ? ModShift : 0)
            | (alt ? ModAlt : 0)
            | (win ? ModWin : 0)
            | ModNoRepeat;
        // 主键名**归一**到 WPF `Key` 枚举名：用户手工编辑 settings.json 时多半写 Win32 风格
        //（`Backspace` / `Esc`），而面板要用 `Enum.Parse<Key>` 换虚拟键码 —— 不归一会静默失效，
        // 表现为"设置里明明配了热键、按下去没反应"（最难查的一类问题）。
        mainKey = NormalizeMainKeyName(main);
        canonical = BuildCanonical(ctrl, shift, alt, win, mainKey);
        return true;
    }

    /// <summary>
    /// 主键名归一：Win32 风格名 → WPF <c>Key</c> 枚举名（`Backspace`→`Back`、`Esc`→`Escape`、`Return`→`Enter`…）。
    /// <para>
    /// 字母 / 数字 / 功能键本就同名（`A` / `D1` / `F5`），一律原样返回；大小写不敏感。
    /// </para>
    /// </summary>
    public static string NormalizeMainKeyName(string mainKey)
    {
        var t = mainKey.Trim();
        return t.ToLowerInvariant() switch
        {
            "backspace" or "back" => "Back",
            "escape" or "esc" => "Escape",
            "return" or "enter" => "Enter",
            "del" or "delete" => "Delete",
            "ins" or "insert" => "Insert",
            "space" => "Space",
            "tab" => "Tab",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "home" => "Home",
            "end" => "End",
            "prior" or "pageup" or "pgup" => "PageUp",
            "next" or "pagedown" or "pgdn" => "PageDown",
            _ => t,
        };
    }

    /// <summary>构造规范串（设置界面录制按键后调用；主键名取 WPF `Key` 枚举名）。</summary>
    public static string Build(bool ctrl, bool shift, bool alt, bool win, string mainKey)
        => BuildCanonical(ctrl, shift, alt, win, mainKey);

    /// <summary>
    /// 主键**显示名**（`Key` 枚举名 → 用户看得懂的名字）：`D1`→`1`、`NumPad1`→`Num1`、
    /// `Back`→`Backspace`、`Escape`→`Esc`。
    /// <para>
    /// 只影响显示 —— **存储恒用 Key 枚举名**（那是两侧约定的书写形式，不能因显示而改写）。
    /// </para>
    /// </summary>
    public static string PrettyMainKey(string mainKey)
    {
        if (mainKey.Length == 2 && mainKey[0] == 'D' && char.IsDigit(mainKey[1]))
        {
            return mainKey[1].ToString();
        }
        if (mainKey.Length == 7 && mainKey.StartsWith("NumPad", StringComparison.Ordinal) && char.IsDigit(mainKey[6]))
        {
            return "Num" + mainKey[6];
        }

        // WPF 枚举名 → 用户习惯叫法（反向映射，与 NormalizeMainKeyName 配对）
        return mainKey switch
        {
            "Back" => "Backspace",
            "Escape" => "Esc",
            "Return" or "Enter" => "Enter",
            "PageUp" => "PgUp",
            "PageDown" => "PgDn",
            _ => mainKey,
        };
    }

    /// <summary>规范串的显示形式（`Alt+D1` → `Alt+1`）。</summary>
    public static string Pretty(string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return string.Empty;
        }

        var parts = canonical.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return canonical;
        }
        parts[^1] = PrettyMainKey(parts[^1]);
        return string.Join("+", parts);
    }

    /// <summary>
    /// 拼写规范化（**不做主键合法性校验**，故不需要 WPF 依赖）：修饰键统一大小写、常见别名归一
    ///（`return`→`Enter`、`back`→`Backspace`、`escape`→`Esc`），并**重建为固定顺序**
    ///（Ctrl → Shift → Alt → Win → 主键）。
    /// <para>
    /// 【为什么必须重排顺序】`Shift+Ctrl+V` 与 `Ctrl+Shift+V` 是同一个键，用户/文档两种写法都常见。
    /// 若只做大小写归一、保留原顺序，两者会被判为不同串 → 设置里明明填的是内置热键却"检测不到冲突"
    ///（测试 `Conflicts_detects_builtin_global_hotkeys` 正是这么抓到的）。
    /// </para>
    /// </summary>
    private static string NormalizeSpelling(string spec)
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ctrl = false;
        var shift = false;
        var alt = false;
        var win = false;
        var main = string.Empty;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "win" or "windows" or "meta":
                    win = true;
                    break;
                // 别名只在"主键"位置归一（`Esc` 必须是主键，不会是修饰键）
                case "return":
                    main = "Enter";
                    break;
                case "back":
                    main = "Backspace";
                    break;
                case "escape":
                    main = "Esc";
                    break;
                default:
                    if (main.Length == 0)
                    {
                        main = part;
                    }
                    break;
            }
        }

        var result = new List<string>(5);
        if (ctrl)
        {
            result.Add("Ctrl");
        }
        if (shift)
        {
            result.Add("Shift");
        }
        if (alt)
        {
            result.Add("Alt");
        }
        if (win)
        {
            result.Add("Win");
        }
        if (main.Length > 0)
        {
            result.Add(main);
        }
        return string.Join("+", result);
    }

    private static string BuildCanonical(bool ctrl, bool shift, bool alt, bool win, string mainKey)
    {
        var parts = new List<string>(5);
        if (ctrl)
        {
            parts.Add("Ctrl");
        }
        if (shift)
        {
            parts.Add("Shift");
        }
        if (alt)
        {
            parts.Add("Alt");
        }
        if (win)
        {
            parts.Add("Win");
        }
        parts.Add(mainKey);
        return string.Join("+", parts);
    }
}
