// BetterDesktop.Kernel.Hmr — PluginDependency 插件级依赖声明
// 插件 id + 最低版本 + 可选标记

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件级依赖：声明依赖另一个插件（按稳定 id），可指定最低版本与可选标记。</summary>
public sealed class PluginDependency
{
    /// <summary>构造。</summary>
    public PluginDependency(string id, SemanticVersion? minimumVersion = null, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("依赖 id 不能为空", nameof(id));
        }
        Id = id;
        MinimumVersion = minimumVersion;
        Optional = optional;
    }

    /// <summary>依赖的插件稳定 id。</summary>
    public string Id { get; }

    /// <summary>最低版本要求（null 表示不限制版本）。</summary>
    public SemanticVersion? MinimumVersion { get; }

    /// <summary>是否可选依赖（缺失不阻断加载）。</summary>
    public bool Optional { get; }
}
