// BetterDesktop.Kernel.Hmr — PluginDependencyResolver 插件依赖工具
// 必需依赖校验 + 稳定拓扑排序

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件依赖工具：必需依赖校验 + 稳定拓扑排序。</summary>
public static class PluginDependencyResolver
{
    /// <summary>找出缺失的必需依赖（可选依赖缺失不返回；版本不足会带上「需要/实际」描述）。</summary>
    public static IReadOnlyList<string> FindMissingRequiredDependencies(
        PluginManifest manifest,
        IReadOnlyDictionary<string, SemanticVersion> availableVersions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(availableVersions);
        var missing = new List<string>();
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.Optional)
            {
                continue;
            }
            if (!availableVersions.TryGetValue(dependency.Id, out var available))
            {
                missing.Add(dependency.Id);
                continue;
            }
            if (dependency.MinimumVersion is { } minimum && available < minimum)
            {
                missing.Add($"{dependency.Id}（需要 >= {minimum}，实际 {available}）");
            }
        }
        return missing;
    }

    /// <summary>按必需依赖做稳定拓扑排序（Kahn，同层保持输入顺序）；存在环时抛 InvalidOperationException。</summary>
    public static IReadOnlyList<PluginManifest> TopologicalOrder(IReadOnlyList<PluginManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var byId = new Dictionary<string, PluginManifest>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            byId[manifest.Id] = manifest;
        }

        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            inDegree[manifest.Id] = 0;
            dependents[manifest.Id] = new List<string>();
        }

        foreach (var manifest in manifests)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (dependency.Optional || !byId.ContainsKey(dependency.Id))
                {
                    continue;
                }
                inDegree[manifest.Id]++;
                dependents[dependency.Id].Add(manifest.Id);
            }
        }

        var ready = new Queue<string>();
        foreach (var manifest in manifests)
        {
            if (inDegree[manifest.Id] == 0)
            {
                ready.Enqueue(manifest.Id);
            }
        }

        var ordered = new List<PluginManifest>();
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            ordered.Add(byId[id]);
            foreach (var dependent in dependents[id])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        if (ordered.Count != manifests.Count)
        {
            throw new InvalidOperationException("插件依赖存在环，无法拓扑排序");
        }
        return ordered;
    }
}
