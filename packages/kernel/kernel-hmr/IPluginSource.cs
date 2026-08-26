// BetterDesktop.Kernel.Hmr — IPluginSource 插件实例来源抽象
// 每次 LoadAsync 返回一个全新、可独立卸载的插件模块

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件实例来源抽象：每次 LoadAsync 返回一个全新、可独立卸载的插件模块。</summary>
public interface IPluginSource
{
    /// <summary>构造一个全新的插件模块（AssemblyPluginSource 每次进入全新 ALC）。</summary>
    Task<PluginModule> LoadAsync(PluginManifest manifest, CancellationToken cancellationToken = default);
}
