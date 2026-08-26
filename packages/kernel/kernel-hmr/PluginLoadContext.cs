// BetterDesktop.Kernel.Hmr — PluginLoadContext 可回收程序集加载上下文
// 插件私有依赖从插件目录解析，已加载的共享程序集复用默认上下文

using System.Reflection;
using System.Runtime.Loader;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>可回收程序集加载上下文：插件私有依赖从插件目录解析，已加载的共享程序集（内核/BCL）复用默认上下文。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>构造：以可回收模式解析插件程序集。</summary>
    public PluginLoadContext(string assemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = Default.Assemblies.FirstOrDefault(
            a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
        if (shared is not null)
        {
            return shared;
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
