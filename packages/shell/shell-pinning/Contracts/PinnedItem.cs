using System.Text.Json.Serialization;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Pinning.Contracts;

/// <summary>
/// 固定项模型：某个 zone 下的一项固定应用。
/// <see cref="Zone"/> 由 pinning.json 的 zone 字典键承载，序列化时忽略避免冗余；
/// <see cref="Order"/> 与所在 zone 列表索引一致，序列化时忽略，读取时按索引计算。
/// </summary>
public sealed record PinnedItem
{
    /// <summary>被固定的应用快照（Pin 时捕获，供消费方零成本重建展示信息）。</summary>
    public required AppItem AppItem { get; init; }

    /// <summary>所在分区（dock / startmenu / taskbar 等）；序列化忽略，由 pinning.json 的 zone 键承载。</summary>
    [JsonIgnore]
    public string Zone { get; init; } = string.Empty;

    /// <summary>固定顺序（0 起）；与所在 zone 列表索引一致，仅作运行时快照字段，不持久化。</summary>
    [JsonIgnore]
    public int Order { get; init; }
}
