using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

/// <summary>
/// <see cref="HotkeySpec"/>（用户热键：规范串 ⇄ Win32 注册参数）回归测试。
/// <para>
/// 【为什么值得钉死】它是**设置界面与面板共用的单一真相源**：一旦两侧解析口径漂移，
/// 症状是"设置里配了热键、按下去却没反应" —— 用户完全无法定位（看不出是哪一侧的锅）。
/// 本项目在"指纹键构造"上踩过一次同类坑（TECH-KNOWLEDGE 1301），故此处用测试锁住口径。
/// </para>
/// </summary>
public sealed class HotkeySpecTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Enter")]
    [InlineData("ctrl+shift+enter")]
    [InlineData("Shift+Ctrl+Enter")]      // 顺序颠倒 → 归一为固定顺序
    [InlineData("CTRL + SHIFT + Enter")]  // 多余空白 → 容忍
    public void Parse_normalizes_case_order_and_spacing(string input)
    {
        Assert.True(HotkeySpec.TryParse(input, out var mods, out var mainKey, out var canonical));

        Assert.Equal("Ctrl+Shift+Enter", canonical);
        Assert.Equal("Enter", mainKey);
        Assert.Equal(HotkeySpec.ModControl | HotkeySpec.ModShift | HotkeySpec.ModNoRepeat, mods);
    }

    [Fact]
    public void Parse_always_sets_no_repeat()
    {
        // 按住不连发（7413 纪律）：否则用户按住热键会连续触发粘贴，瞬间贴出一串
        Assert.True(HotkeySpec.TryParse("Alt+F5", out var mods, out _, out _));
        Assert.Equal(HotkeySpec.ModNoRepeat, mods & HotkeySpec.ModNoRepeat);
    }

    [Fact]
    public void Parse_windows_modifier_and_digit_key()
    {
        Assert.True(HotkeySpec.TryParse("Win+D1", out var mods, out var mainKey, out var canonical));
        Assert.Equal(HotkeySpec.ModWin, mods & HotkeySpec.ModWin);
        Assert.Equal("D1", mainKey);
        Assert.Equal("Win+D1", canonical);
    }

    /// <summary>
    /// 【关键回归】手工编辑 settings.json 的用户会写 Win32 风格键名（`Backspace` / `Esc`）。
    /// 若不归一到 WPF `Key` 枚举名（`Back` / `Escape`），面板 `Enum.Parse&lt;Key&gt;` 会失败 →
    /// 静默不注册 → "配了热键却按不响"。
    /// </summary>
    [Fact]
    public void Parse_normalizes_win32_style_main_key_names()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Alt+Backspace", out _, out var k1, out var c1));
        Assert.Equal("Back", k1);
        Assert.Equal("Ctrl+Alt+Back", c1);

        Assert.True(HotkeySpec.TryParse("Ctrl+Alt+Esc", out _, out var k2, out _));
        Assert.Equal("Escape", k2);

        Assert.True(HotkeySpec.TryParse("Ctrl+Shift+Return", out _, out var k3, out _));
        Assert.Equal("Enter", k3);
    }

    [Theory]
    [InlineData("")]         // 空
    [InlineData("   ")]      // 空白
    [InlineData("Ctrl")]     // 只有修饰键
    [InlineData("Enter")]    // 裸主键（规范要求至少一个修饰键）
    [InlineData("Ctrl+A+B")] // 两个主键
    public void Parse_rejects_invalid(string input)
    {
        Assert.False(HotkeySpec.TryParse(input, out var mods, out var mainKey, out var canonical));
        Assert.Equal(0, mods);
        Assert.Equal(string.Empty, mainKey);
        Assert.Equal(string.Empty, canonical);
    }

    [Theory]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("ctrl+shift+v")]       // 大小写
    [InlineData("Shift+Ctrl+V")]       // 顺序
    [InlineData("Ctrl+Shift+Backspace")]
    [InlineData("Ctrl+Shift+Back")]    // 归一后与 Backspace 同义（用户两种写法都算抢内置键）
    public void Conflicts_detects_builtin_global_hotkeys(string input)
        => Assert.True(HotkeySpec.ConflictsWithBuiltIn(input));

    [Theory]
    [InlineData("Ctrl+Shift+Enter")]
    [InlineData("Alt+D1")]
    [InlineData("Ctrl+Alt+F5")]
    [InlineData("")]
    [InlineData(null)]
    public void Conflicts_ignores_non_builtin(string? input)
        => Assert.False(HotkeySpec.ConflictsWithBuiltIn(input));

    [Fact]
    public void Pretty_shows_human_readable_main_key()
    {
        Assert.Equal("Alt+1", HotkeySpec.Pretty("Alt+D1"));
        Assert.Equal("Ctrl+Num5", HotkeySpec.Pretty("Ctrl+NumPad5"));
        Assert.Equal("Ctrl+Shift+Backspace", HotkeySpec.Pretty("Ctrl+Shift+Back"));
        Assert.Equal("Ctrl+Alt+Esc", HotkeySpec.Pretty("Ctrl+Alt+Escape"));
        Assert.Equal("Ctrl+Shift+Enter", HotkeySpec.Pretty("Ctrl+Shift+Enter"));
        Assert.Equal(string.Empty, HotkeySpec.Pretty(""));
    }

    /// <summary>
    /// 闭环测试：设置界面「录制 → <c>Build</c>」产出的串，必须能被面板「<c>TryParse</c> → 虚拟键码」
    /// 完整吃下并还原 —— 这正是本类存在的意义（两侧不能各写一遍）。
    /// </summary>
    [Fact]
    public void Build_roundtrips_with_parse()
    {
        var spec = HotkeySpec.Build(ctrl: true, shift: false, alt: true, win: false, mainKey: "D1");
        Assert.Equal("Ctrl+Alt+D1", spec);

        Assert.True(HotkeySpec.TryParse(spec, out var mods, out var mainKey, out var canonical));
        Assert.Equal("Ctrl+Alt+D1", canonical);
        Assert.Equal("D1", mainKey);
        Assert.Equal(HotkeySpec.ModControl | HotkeySpec.ModAlt | HotkeySpec.ModNoRepeat, mods);
    }
}
