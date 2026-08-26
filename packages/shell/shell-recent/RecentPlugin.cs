using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.Recent.Services;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.Recent;

/// <summary>
/// 最近项插件：Provide IRecentItemsService。
/// 注入 IWindowTrackerService 订阅前台变化以自动记录最近程序（依赖 WindowTrackerPlugin，须在其后注册）。
/// </summary>
public sealed class RecentPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.recent";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private RecentItemsService? _service;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _service = new RecentItemsService(
            context.Get<IAppSourceService>()!,
            context.Get<IWindowTrackerService>()!,
            context.Logger);

        context.Provide<IRecentItemsService>(_service);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
