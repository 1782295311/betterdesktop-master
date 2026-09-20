using System.Text.Json.Serialization;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>
/// 【P1-5 按应用清洗规则 · 2026-09-13】一条用户规则（与引擎 <c>settings::AppRule</c> 逐字段对齐）。
/// <para>
/// 用户口径：某个应用总复制垃圾时，**自己**设规则治它（忽略整个应用 / 正则丢弃 / 正则替换），
/// 不必等我们去猜。
/// </para>
/// <para>
/// 持久化：设置键 <c>extensions.clipboard-history.app-rules</c> = 本对象的 JSON 数组字符串；
/// 推送引擎时由 <c>ClipboardPlugin</c> 反序列化后作为 <c>app-rules</c> 数组下发（引擎 serde 直接收数组）。
/// 属性显式标注小写名 —— 引擎 kebab-case 字段即 <c>app/action/pattern/replacement</c>。
/// </para>
/// </summary>
public sealed class ClipboardAppRule
{
    /// <summary>应用匹配串（进程名或窗口标题子串；空 = 匹配所有应用）。</summary>
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    /// <summary>动作：<c>ignore</c> / <c>drop</c> / <c>replace</c>。</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "ignore";

    /// <summary>正则（ignore 时忽略；drop/replace 必填）。</summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>替换文本（仅 replace 使用）。</summary>
    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;
}
