// BetterDesktop.Kernel.Hmr.Tests — FailingPlugin 必崩插件
// LoadAsync 抛异常，用于回滚演练

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr.Tests;

/// <summary>必崩插件：LoadAsync 抛异常（回滚演练用）。</summary>
internal sealed class FailingPlugin : IPlugin
{
    public string Name => "failing";

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("boom");

    public Task UnloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
