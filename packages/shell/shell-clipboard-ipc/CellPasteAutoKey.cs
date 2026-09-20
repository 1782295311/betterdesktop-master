namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 【按格粘 · 2026-09-13】"粘完一格后是否自动按键、按哪个键"。
/// <para>
/// <b>为什么必须让用户选</b>（用户原话）："表格内容的形式多种多样，我们固定的形式无法应对" ——
/// 业务系统的表单差异极大：有的逐格 <c>Tab</c>、有的行末要 <c>Enter</c>、有的（如网页富文本框）
/// 完全不接受自动按键。因此本项**不做固定行为**：启动时可选、并记住上次选择。
/// </para>
/// </summary>
public enum CellPasteAutoKey
{
    /// <summary>只粘贴，不按键（Tab / Enter 由用户自己控制）。</summary>
    None = 0,

    /// <summary>粘完发一次 <c>Tab</c>（跳到下一格；逐格表单的常见形态）。</summary>
    Tab = 1,

    /// <summary>粘完发一次 <c>Enter</c>（换行/提交；部分系统用回车确认一格）。</summary>
    Enter = 2,
}
