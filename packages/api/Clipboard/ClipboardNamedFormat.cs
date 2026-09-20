namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 自定义（命名）剪贴板格式载荷（2026-09-13 P1-4 命名格式透传）。
/// <para>
/// **用途**：Excel / WPS 表格的"可编辑表格"数据不在纯文本 / HTML / RTF 中，而在应用自己注册的
/// 命名格式里（如 <c>Microsoft Excel Worksheet</c>）。引擎捕获时按上限收下、复制时按同名还原，
/// 于是历史条目粘回表格**仍是可编辑表格**而非降级纯文本。
/// </para>
/// <para>
/// C# 侧仅**承载**该字段（legacy 后端与诊断使用），写回由 Rust 引擎完成 —— 契约保持可加性，
/// 旧消费方不受影响。
/// </para>
/// </summary>
public sealed class ClipboardNamedFormat
{
    /// <summary>格式名（如 "Microsoft Excel Worksheet"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>原始字节的 base64（可能较大；列表摘要载荷由引擎剥离，不会下发给面板列表）。</summary>
    public string DataBase64 { get; set; } = string.Empty;
}
