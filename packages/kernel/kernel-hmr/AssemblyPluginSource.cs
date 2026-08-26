// BetterDesktop.Kernel.Hmr — AssemblyPluginSource 程序集插件来源
// 每次 LoadAsync 在全新可回收 ALC 中加载程序集并实例化入口插件

using System.Reflection;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>程序集插件来源：每次 LoadAsync 在全新可回收 ALC 中加载程序集并实例化入口插件。</summary>
public sealed class AssemblyPluginSource : IPluginSource
{
    /// <inheritdoc />
    public Task<PluginModule> LoadAsync(PluginManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.AssemblyPath))
        {
            throw new ArgumentException("manifest.AssemblyPath 不能为空（程序集来源需要插件程序集路径）", nameof(manifest));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(manifest.AssemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"插件程序集不存在：{fullPath}", fullPath);
        }

        var loadContext = new PluginLoadContext(fullPath);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(fullPath);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        IPlugin plugin;
        try
        {
            plugin = PluginEntry.Find(assembly, manifest.EntryTypeName);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        return Task.FromResult(new PluginModule(plugin, () =>
        {
            loadContext.Unload();
            return Task.CompletedTask;
        }));
    }
}
