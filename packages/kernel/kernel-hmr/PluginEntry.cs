// BetterDesktop.Kernel.Hmr — PluginEntry 插件入口探测
// 从程序集查找唯一 IPlugin 实现并实例化（每次加载反射一次）

using System.Reflection;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件入口探测：从程序集查找唯一 IPlugin 实现并实例化（每次加载反射一次）。</summary>
internal static class PluginEntry
{
    /// <summary>按入口类型名或自动探测查找并实例化 IPlugin 实现。</summary>
    public static IPlugin Find(Assembly assembly, string? entryTypeName)
    {
        Type? entryType;
        if (!string.IsNullOrWhiteSpace(entryTypeName))
        {
            entryType = assembly.GetType(entryTypeName, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw new TypeLoadException($"找不到入口类型 {entryTypeName}（程序集 {assembly.GetName().Name}）");
            }
        }
        else
        {
            var candidates = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && !t.IsGenericTypeDefinition && typeof(IPlugin).IsAssignableFrom(t))
                .ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException($"程序集 {assembly.GetName().Name} 未包含任何 IPlugin 实现");
            }
            if (candidates.Count > 1)
            {
                throw new InvalidOperationException($"程序集 {assembly.GetName().Name} 包含多个 IPlugin 实现，请用 manifest.EntryTypeName 指定入口");
            }
            entryType = candidates[0];
        }

        if (!typeof(IPlugin).IsAssignableFrom(entryType))
        {
            throw new InvalidOperationException($"类型 {entryType.FullName} 未实现 IPlugin");
        }
        var constructor = entryType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
        {
            throw new InvalidOperationException($"类型 {entryType.FullName} 缺少公共无参构造函数");
        }
        return (IPlugin)constructor.Invoke(null);
    }
}
