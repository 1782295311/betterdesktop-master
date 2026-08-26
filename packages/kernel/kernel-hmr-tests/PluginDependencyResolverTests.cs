// BetterDesktop.Kernel.Hmr.Tests — PluginDependencyResolver 契约测试

using BetterDesktop.Kernel.Hmr;
using Xunit;

namespace BetterDesktop.Kernel.Hmr.Tests;

public sealed class PluginDependencyResolverTests
{
    [Fact(DisplayName = "必需依赖缺失与版本不足被识别，可选缺失不报")]
    public void FindMissingRequiredDependencies_Covers()
    {
        var manifest = new PluginManifest("dep", "dep", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        manifest.Dependencies.Add(new PluginDependency("required"));
        manifest.Dependencies.Add(new PluginDependency("versioned", new SemanticVersion(2, 0, 0)));
        manifest.Dependencies.Add(new PluginDependency("optional", optional: true));

        var available = new Dictionary<string, SemanticVersion> { ["versioned"] = new SemanticVersion(1, 0, 0) };

        var missing = PluginDependencyResolver.FindMissingRequiredDependencies(manifest, available);
        Assert.Contains("required", missing);
        Assert.Contains(missing, m => m.StartsWith("versioned", StringComparison.Ordinal));
        Assert.DoesNotContain(missing, m => m.StartsWith("optional", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "拓扑排序按依赖顺序输出")]
    public void TopologicalOrder_Orders()
    {
        var a = new PluginManifest("a", "a", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        var b = new PluginManifest("b", "b", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        var c = new PluginManifest("c", "c", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        b.Dependencies.Add(new PluginDependency("a"));
        c.Dependencies.Add(new PluginDependency("a"));

        var order = PluginDependencyResolver.TopologicalOrder(new[] { c, b, a });
        Assert.Equal("a", order[0].Id);
        Assert.Equal(new[] { "b", "c" }, order.Skip(1).Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "依赖环抛异常")]
    public void TopologicalOrder_Cycle_Throws()
    {
        var a = new PluginManifest("a", "a", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        var b = new PluginManifest("b", "b", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));
        a.Dependencies.Add(new PluginDependency("b"));
        b.Dependencies.Add(new PluginDependency("a"));

        Assert.Throws<InvalidOperationException>(() => PluginDependencyResolver.TopologicalOrder(new[] { a, b }));
    }
}
