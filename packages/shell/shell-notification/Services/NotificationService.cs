using System;
using System.Collections.Generic;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Notification.Contracts;
using BetterDesktop.Shell.Notification.Windows;
using BetterDesktop.Shell.Pinning.Contracts;

namespace BetterDesktop.Shell.Notification.Services;

/// <summary>
/// 新装应用通知服务实现。
/// 依赖 <see cref="IAppSourceService"/>（新增检测 / 已见标记）、<see cref="IPinningService"/>（一键固定）、
/// <see cref="IAppIconService"/>（图标）、<see cref="IVibrancyService"/> 与 <see cref="IAppearanceService"/>（弹窗外观）。
/// 与 Dock 解耦：Dock 卸载后通知仍能弹出（验收项）。
/// </summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly IAppSourceService _appSource;
    private readonly IPinningService? _pinning;
    private readonly IAppIconService? _iconService;
    private readonly IVibrancyService? _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly IKernelLogger _logger;
    private NewAppsNotificationWindow? _window;

    public NotificationService(
        IAppSourceService appSource,
        IPinningService? pinning,
        IAppIconService? iconService,
        IVibrancyService? vibrancy,
        IAppearanceService? appearance,
        IKernelLogger logger)
    {
        _appSource = appSource ?? throw new ArgumentNullException(nameof(appSource));
        _pinning = pinning;
        _iconService = iconService;
        _vibrancy = vibrancy;
        _appearance = appearance;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// <see cref="IAppSourceService.AppSourceChanged"/> 订阅入口（后台线程触发，事件已由开始菜单监控防抖 1s）。
    /// 查真正新增的应用，有则切回 UI 线程弹窗。也复用为启动首查（首次调用建立"已见"基线，返回空不打扰）。
    /// </summary>
    public void OnAppSourceChanged(object? sender, EventArgs e)
    {
        try
        {
            var newly = _appSource.GetNewlyInstalledApps();
            if (newly.Count == 0)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                ShowNewAppsNotification(newly);
            }
            else
            {
                dispatcher.BeginInvoke((Action)(() => ShowNewAppsNotification(newly)));
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[Notification] 检测新装应用失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void ShowNewAppsNotification(IReadOnlyList<AppItem> apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return;
        }

        if (_vibrancy is null)
        {
            // 无毛玻璃服务时不弹窗（VibrancyService 由内核常驻提供，正常情况下不会走到）。
            return;
        }

        // 单实例守卫：已有弹窗则不重复创建。
        if (_window is not null)
        {
            return;
        }

        try
        {
            var window = new NewAppsNotificationWindow(_vibrancy, _appSource, _iconService, _pinning, _appearance, apps);
            _window = window;
            window.Closed += (_, _) => _window = null;
            window.Show();
        }
        catch (Exception ex)
        {
            _window = null;
            _logger.Error($"[Notification] 弹窗创建失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _window?.Close();
            _window = null;
        }
        catch
        {
            // 释放失败不阻断（M10）。
        }
    }
}
