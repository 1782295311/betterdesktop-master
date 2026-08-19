// BetterDesktop.Kernel.Tests — TestPlugin 测试用插件
// 记录加载/卸载次数并支持注入 effect

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Tests;

/// <summary>测试用插件：记录加载/卸载次数。</summary>
internal sealed class TestPlugin : IPlugin
{
    private readonly Action<IContext>? _onLoad;
    private readonly Func<IDisposable>[] _effects;

    public TestPlugin(string name, Action<IContext>? onLoad = null, params Func<IDisposable>[] effects)
    {
        Name = name;
        _onLoad = onLoad;
        _effects = effects;
    }

    public string Name { get; }

    public IReadOnlyList<Type> Inject { get; set; } = Array.Empty<Type>();

    public int LoadCount { get; private set; }

    public int UnloadCount { get; private set; }

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        LoadCount++;
        _onLoad?.Invoke(context);
        foreach (var effect in _effects)
        {
            context.Effect(effect);
        }
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        UnloadCount++;
        return Task.CompletedTask;
    }
}
