// BetterDesktop.Kernel.Loader — 插件树配置模型（cordis.yml）
// 声明式插件树：id / name（工厂名）/ enabled

using YamlDotNet.Serialization;

namespace BetterDesktop.Kernel.Loader;

/// <summary>cordis.yml 顶层配置。</summary>
public sealed class LoaderConfig
{
    /// <summary>插件条目列表。</summary>
    [YamlMember(Alias = "plugins")]
    public List<LoaderEntry> Plugins { get; set; } = new();
}

/// <summary>单个插件条目。</summary>
public sealed class LoaderEntry
{
    /// <summary>稳定 id（诊断与报告用）。</summary>
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>工厂名：宿主注册的内置插件键。</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>是否启用（缺省启用）。</summary>
    [YamlMember(Alias = "enabled")]
    public bool? Enabled { get; set; }
}
