using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Notification.Contracts;
using BetterDesktop.Shell.Notification.Services;
using BetterDesktop.Shell.Pinning.Contracts;

namespace BetterDesktop.Shell.Notification;

/// <summary>
/// 新装应用通知插件。
/// 订阅 <see cref="IAppSourceService.AppSourceChanged"/>，检测到新装应用后右下角弹窗（一键固定/忽略）。
/// 与 Dock 解耦：不依赖 shell-dock，Dock 卸载后通知仍可用（步骤4 验收项）。
/// </summary>
public sealed class NotificationPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.notification";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private NotificationService? _service;
    private IAppSourceService? _appSource;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _appSource = context.Get<IAppSourceService>()!;
        _service = new NotificationService(
            _appSource,
            context.Get<IPinningService>(),
            context.Get<IAppIconService>(),
            context.Get<IVibrancyService>(),
            context.Get<IAppearanceService>(),
            context.Logger);

        context.Provide<INotificationService>(_service);

        // 订阅开始菜单变化（监控已防抖 1s）。
        _appSource.AppSourceChanged += _service.OnAppSourceChanged;

        // 启动首查：后台线程（首次调用会建立"已见"基线并返回空，不打扰用户；
        // 注册表扫描代价高，避免在 UI 线程同步执行卡顿）。
        var service = _service;
        _ = Task.Run(() => service.OnAppSourceChanged(null, EventArgs.Empty));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_appSource is not null && _service is not null)
        {
            _appSource.AppSourceChanged -= _service.OnAppSourceChanged;
        }

        _service?.Dispose();
        _service = null;
        _appSource = null;
        return Task.CompletedTask;
    }
}
