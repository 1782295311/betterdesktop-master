// BetterDesktop.Kernel.Hmr.Tests — TestHmrPlugin 测试插件
// 记录加载/卸载次数并实现状态迁移

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr.Tests;

/// <summary>测试插件：记录加载/卸载次数并实现状态捕获与恢复。</summary>
internal sealed class TestHmrPlugin : IPlugin, IPluginStateProvider
{
    public TestHmrPlugin(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<Type> Inject { get; set; } = Array.Empty<Type>();

    public int LoadCount { get; private set; }

    public int UnloadCount { get; private set; }

    public int CaptureCount { get; private set; }

    public int RestoreCount { get; private set; }

    public string? CapturedPayload { get; private set; }

    public string? RestoredPayload { get; private set; }

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        LoadCount++;
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        UnloadCount++;
        return Task.CompletedTask;
    }

    public Task<PluginStateSnapshot?> CaptureStateAsync(CancellationToken cancellationToken = default)
    {
        CaptureCount++;
        CapturedPayload = $"state-{LoadCount}";
        return Task.FromResult<PluginStateSnapshot?>(new PluginStateSnapshot("v1", CapturedPayload));
    }

    public Task RestoreStateAsync(PluginStateSnapshot state, CancellationToken cancellationToken = default)
    {
        RestoreCount++;
        RestoredPayload = state.Payload;
        return Task.CompletedTask;
    }
}
