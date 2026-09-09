// BetterDesktop.Shell.Status — 语义快照（用户可读的语言 + UI 提示）
// 采集层产出裸值（百分比/字节/枚举）后，语义层统一归一为 StatusSnapshot：
//   - HumanText / ShortText：人话描述，供菜单栏文本、控制中心详情、无障碍朗读
//   - Severity：严重级别，供 UI 选配色
//   - Progress：0-100 进度（图标填充/进度条），-1 表示不适用
//   - IconKey：UI 图标键，与 shell-menu-bar 的图标资源一一对应，UI 不感知底层协议

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>
/// 某个系统状态项的语义快照：把裸系统值翻译成用户能直接读懂的语言与 UI 提示。
/// </summary>
public sealed class StatusSnapshot
{
    /// <summary>监控项标识（"memory" / "battery" / "volume" / "microphone" / "network" / "ime"）。</summary>
    public required string MonitorId { get; init; }

    /// <summary>菜单栏短线文本，如 "72%"、"🔋 充电中"、"静音"。</summary>
    public required string ShortText { get; init; }

    /// <summary>完整人话描述，如 "内存占用 72%，共 32GB，可用 9GB"。用于详情面板/无障碍。</summary>
    public required string HumanText { get; init; }

    /// <summary>严重级别，供 UI 映射配色与强调。</summary>
    public StatusSeverity Severity { get; init; } = StatusSeverity.Normal;

    /// <summary>0-100 的进度值（内存/电量填充、音量条）；不适用时取 -1。</summary>
    public double Progress { get; init; } = -1;

    /// <summary>UI 图标键（对应菜单栏图标资源，如 "battery-charging" / "volume-muted"）。</summary>
    public string IconKey { get; init; } = string.Empty;

    /// <summary>原始可读性标签（可选，某些项展示额外状态，如电量剩余时间）。</summary>
    public string? Detail { get; init; }
}
