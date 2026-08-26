// BetterDesktop.Kernel.Hmr.Tests — AssemblyPluginSource 程序集加载契约测试
// 以 BetterDesktop.Kernel.Timer.dll 为真实插件夹具验证 ALC 隔离加载与卸载

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using Xunit;

namespace BetterDesktop.Kernel.Hmr.Tests;

public sealed class AssemblyPluginSourceTests
{
    [Fact(DisplayName = "AssemblyPluginSource 在全新 ALC 中加载插件程序集")]
    public async Task LoadsPluginFromIsolatedAlc()
    {
        _ = typeof(IPlugin).Assembly;
        var dll = Path.Combine(AppContext.BaseDirectory, "BetterDesktop.Kernel.Timer.dll");
        Assert.True(File.Exists(dll), $"找不到插件程序集 {dll}");

        var manifest = new PluginManifest("fixture.timer", "kernel.timer", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0))
        {
            AssemblyPath = dll
        };

        var module = await new AssemblyPluginSource().LoadAsync(manifest);
        Assert.Equal("kernel.timer", module.Plugin.Name);
        await module.DisposeAsync();
    }

    [Fact(DisplayName = "HmrManager 经程序集来源加载并卸载真实插件")]
    public async Task ManagerLoadsAndUnloadsAssemblyPlugin()
    {
        _ = typeof(IPlugin).Assembly;
        var dll = Path.Combine(AppContext.BaseDirectory, "BetterDesktop.Kernel.Timer.dll");
        using var context = new CordisContext();
        using var manager = new HmrManager(context);

        var manifest = new PluginManifest("fixture.timer", "kernel.timer", new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0))
        {
            AssemblyPath = dll
        };

        var load = await manager.LoadPluginAsync(manifest);
        Assert.True(load.Success, load.Error);
        Assert.Equal(PluginReloadStatus.Loaded, manager.GetPluginStatus("fixture.timer"));

        Assert.True(await manager.UnloadPluginAsync("fixture.timer"));
        Assert.Equal(PluginReloadStatus.Unloaded, manager.GetPluginStatus("fixture.timer"));
    }
}
