using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Ipc.Tests;

/// <summary>
/// 「哪些终端需要换 `Shift+Insert`」的白名单回归测试（2026-09-13）。
/// <para>
/// 【为什么值得钉死】这个白名单**改动的是粘贴路径的默认行为**，判错两个方向都有代价：
/// **误判**（把本来支持 Ctrl+V 的终端收进来）→ 该终端里粘贴键被擅自换掉，
/// 用户完全不知道发生了什么；**漏判**（真不认 Ctrl+V 的没收进来）→ 用户在那个终端里粘不上。
/// 误判严重得多，故本测试用**用户实测的反例**把常见终端钉在"false"上。
/// </para>
/// <para>
/// ⚠️ 教训来源：本项最初把 `cmd` / `powershell` / `WindowsTerminal` 也当"需换键"，
/// 依据是"终端里 Ctrl+V 是字面输入"这个**过时印象** —— 用户实测直接推翻：
/// "我使用 Ctrl+V 可以粘贴到 powershell 和 cmd 里面啊"。故下面这几条断言是**反例锁定**，别删。
/// </para>
/// </summary>
public sealed class PasteInjectModeTests
{
    [Theory]
    // ⚠️ 现代 Windows 终端**支持 Ctrl+V** → 必须判为"不需要换键"（用户实测反例，勿改）
    [InlineData("WindowsTerminal", false)]
    [InlineData("WindowsTerminalPreview", false)]
    [InlineData("conhost", false)]
    [InlineData("cmd", false)]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    // 少数确实不认 Ctrl+V 的终端 → Shift+Insert
    [InlineData("mintty", true)]
    [InlineData("MINTTY", true)] // 大小写不敏感（进程名大小写不保证）
    [InlineData("  mintty  ", true)] // 前后空白容忍
    [InlineData("putty", true)]
    [InlineData("Xshell", true)]
    [InlineData("SecureCRT", true)]
    [InlineData("MobaXterm", true)]
    // 普通应用 → 必须走 Ctrl+V
    [InlineData("notepad", false)]
    [InlineData("explorer", false)]
    [InlineData("WINWORD", false)]
    [InlineData("chrome", false)]
    // VS Code 无论如何都不收：编辑器与集成终端同进程名，且它支持 Ctrl+V
    [InlineData("Code", false)]
    // 取不到进程名时按"非终端"处理（保持 Ctrl+V 原行为，不引入新失败面）
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsTerminalProcessName_matches_whitelist(string? processName, bool expected)
        => Assert.Equal(expected, ClipboardIpcClient.IsTerminalProcessName(processName));
}
