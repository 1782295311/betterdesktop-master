using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;
using BetterDesktop.Shell.WindowTracker.Services;

namespace BetterDesktop.Shell.WindowTracker;

/// <summary>
/// 运行中窗口 / 应用追踪插件。
/// 无对外依赖（仅消费 IAppSourceService），加载时 Provide IWindowTrackerService。
/// Dock / 未来开始菜单 / 任务栏通过 Inject 该服务消费，消除各自重复实现（M7）。
/// </summary>
public sealed class WindowTrackerPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.window-tracker";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private WindowTrackerService? _service;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var appSource = context.Get<IAppSourceService>()!;
        _service = new WindowTrackerService(appSource, context.Logger);
        context.Provide<IWindowTrackerService>(_service);

        // 注：IWindowThumbnailService 已按 W3 孤儿服务判别表（docs/design-proposals/2026-09-03-孤儿服务判别表.md）
        // 删除——Provide 后全仓零消费方，Dock 缩略图链路走自有 ThumbnailWindow/DwmThumbnail；
        // "DockWindow 改为 inject"的旧 TODO（OPEN_SHELL_INTEGRATION_STEPS.md）已被自有实现覆盖。

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
