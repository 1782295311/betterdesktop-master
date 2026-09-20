namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 粘贴按键注入方式（2026-09-13，对标 TieZ 的"三档注入"，先落两档）。
/// <para>
/// 【为什么需要】`Ctrl+V` **并非到处都能粘贴** —— 少数终端把它当字面输入/控制字符
///（`mintty`（Git Bash / MSYS2）、`PuTTY` 等 SSH/串口客户端），那里真正的粘贴键是 `Shift+Insert`。
/// 用户在那种终端里执行"粘贴回原窗口"会毫无反应。
/// </para>
/// <para>
/// 【⚠️ 重要更正 · 2026-09-13 用户实测】`Ctrl+V` 在**现代 Windows 终端里是能粘贴的** ——
/// conhost（Win10 1809 起）、`cmd`、PowerShell、Windows Terminal **全部支持**。
/// 本项最初把这几类也列入"需换成 Shift+Insert"的白名单，属**用过时印象改动本来能用的行为**，
/// 已按用户实测收缩白名单（见 `ClipboardIpcClient.TerminalProcessNames` 注释）。
/// </para>
/// <para>
/// 默认 <see cref="CtrlV"/>：**等于旧行为、零变化** —— 不拿用户"本来能用的东西"去冒险。
/// 确实在 mintty / PuTTY 里粘不上的用户，可切到 <see cref="Auto"/> 或 <see cref="ShiftInsert"/>。
/// </para>
/// <para>
/// 未采纳（记录以备将来）：TieZ 还有"扫描码注入（游戏）"与"逐字符 Unicode（游戏模式 + IME）"两档 ——
/// 收益场景小众且实现面大，暂不做；若做，入口同样应加在这个枚举上。
/// </para>
/// </summary>
public enum PasteInjectMode
{
    /// <summary>总是 `Ctrl+V`（**默认**）：现代 Windows 终端与常规应用都支持，等同旧行为。</summary>
    CtrlV,

    /// <summary>
    /// 自动：仅对**明确不认 `Ctrl+V`** 的终端（mintty / PuTTY / SSH 客户端等）改用 `Shift+Insert`，
    /// 其余一律 `Ctrl+V`。**白名单刻意收得很窄** —— 宁可漏判（用户可手动切）也不要误判。
    /// </summary>
    Auto,

    /// <summary>总是 `Shift+Insert`（少数终端的粘贴键；对多数 Windows 应用同样是"粘贴"）。</summary>
    ShiftInsert,
}
