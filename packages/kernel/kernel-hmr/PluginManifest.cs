// BetterDesktop.Kernel.Hmr — PluginManifest 插件清单
// 声明式元数据：id / 名称 / 版本 / 内核 ABI / 依赖 / 程序集入口

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件清单：热重载所需的全部声明式元数据。</summary>
public sealed class PluginManifest
{
    /// <summary>构造。</summary>
    public PluginManifest(string id, string name, SemanticVersion version, SemanticVersion kernelAbi)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("插件 id 不能为空", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("插件名称不能为空", nameof(name));
        }
        Id = id;
        Name = name;
        Version = version;
        KernelAbi = kernelAbi;
        Dependencies = new List<PluginDependency>();
    }

    /// <summary>稳定 id（诊断、依赖与状态中心标识）。</summary>
    public string Id { get; }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>插件版本。</summary>
    public SemanticVersion Version { get; }

    /// <summary>要求的最低内核 ABI 版本。</summary>
    public SemanticVersion KernelAbi { get; }

    /// <summary>依赖列表（可在构造后追加）。</summary>
    public IList<PluginDependency> Dependencies { get; }

    /// <summary>插件程序集路径（AssemblyPluginSource 使用）。</summary>
    public string? AssemblyPath { get; set; }

    /// <summary>入口类型全名（null 时自动探测唯一 IPlugin 实现）。</summary>
    public string? EntryTypeName { get; set; }
}
