using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Core.Animation;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Services;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.Dock;

/// <summary>
/// Dock 插件：管理 Dock 本体（应用管理中心 AppGrabber 已废弃，由开始菜单"所有应用"接管）。
/// 应用来源/图标等底层能力统一由 shell.app-source 插件通过 Inject 注入，
/// 本插件不再手动 new AppSourceService / Win32IconProvider，消除强耦合。
/// </summary>
public sealed class DockPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.dock";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => new[]
    {
        typeof(IVibrancyService),
        typeof(IAppSourceService),
        typeof(IAppIconService),
        typeof(IWindowTrackerService),
        typeof(IPinningService)
    };

    private Window? _dockWindow;
    private IAppSourceService _appSourceService = null!;
    private IAppIconService _appIconService = null!;
    private IDockAppsService _dockAppsService = null!;
    private IDockIconService _dockIconService = null!;
    private IPinningService _pinningService = null!;
    private IVibrancyService? _vibrancy;
    private IAppearanceService? _appearance;
    private IEventBus? _events;

    public DockPlugin()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var storagePath = Path.Combine(appData, "BetterDesktop", "dock-pinned.json");

        // 底层服务由内核在 LoadAsync 前注入；此处仅持有引用（实际赋值在 LoadAsync）。
        // 字段已在声明处初始化为 null!，构造函数无需重复赋值。
    }

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 组件开关：系统管理"组件管理"里的 Dock 栏开关（components.dock）。
        // 关闭则不创建任何 Dock 窗口（含 Launchpad / 应用提取器）。默认启用。
        var settings = context.Get<ISettingsService>();
        if (settings is not null && !settings.Get("components.dock", true))
        {
            return Task.CompletedTask;
        }

        try
        {
            _vibrancy = context.Get<IVibrancyService>()!;
            _appSourceService = context.Get<IAppSourceService>()!;
            _appIconService = context.Get<IAppIconService>()!;
            _events = context.Events;
            // 通用固定/收藏服务（shell.pinning）：Dock 固定列表经 IPinningService("dock") 承载。
            _pinningService = context.Get<IPinningService>()!;
            // 全局外观服务（由 shell.settings 的 SettingsPlugin Provide）：让 Dock 系窗口
            // 的根 Border 描边跟随主题统一驱动（基类 ApplySurfaceChrome 生效）。
            // 注：圆角裁剪路线（SetWindowRgn）已在本项目 layered 窗口证伪并弃用，窗口走系统默认直角。
            _appearance = context.Get<IAppearanceService>();

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var storagePath = Path.Combine(appData, "BetterDesktop", "dock-pinned.json");

            _dockAppsService = new DockAppsService(_appSourceService, _pinningService, storagePath);
            var dockVisual = new DockVisualSettings(settings);
            _dockIconService = new DockIconService(_appIconService, dockVisual);

            // 注册 Dock 专属门面服务供其他 UI 插件（AppGrabber / Launchpad / NewApps）消费。
            context.Provide<IDockAppsService>(_dockAppsService);
            context.Provide<IDockIconService>(_dockIconService);
            context.Provide<DockVisualSettings>(dockVisual);

            _dockWindow = new DockWindow(
                _vibrancy,
                new AnimationService(),
                new DockService(),
                _dockAppsService,
                _dockIconService,
                new DockLayoutService(bottomMargin: dockVisual.BottomMargin, settings: settings),
                this,
                _appIconService,
                settings,
                _appearance,
                dockVisual);
            _dockWindow.Show();

            // 应用源变化（开始菜单创建/删除/改名）时失效扫描缓存并刷新 Dock 固定面板，
            // 安装/卸载程序后无需重启即自动生效。
            _appSourceService.AppSourceChanged += OnAppSourceChanged;

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"DockPlugin.LoadAsync 失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Dock「所有应用」入口：发 IEventBus "shell.start.show-all-apps"，开始菜单切"所有应用"布局并弹出。
    /// </summary>
    public void ShowAllApps()
    {
        try
        {
            _ = _events?.EmitAsync<string>("shell.start.show-all-apps", string.Empty);
        }
        catch
        {
            // 事件发送失败不阻断（M10）。
        }
    }

    /// <summary>
    /// Dock「开始菜单」入口：发 IEventBus "shell.start.toggle"，由 shell.start-menu 切换经典开始菜单。
    /// Win 键钩子与 Dock 开始按钮共用同一契约，保证"点按/Dock/键盘"开关状态一致（M5）。
    /// </summary>
    public void ToggleStartMenu()
    {
        try
        {
            _ = _events?.EmitAsync<string>("shell.start.toggle", string.Empty);
        }
        catch
        {
            // 事件发送失败不阻断（M10）。
        }
    }

    /// <summary>
    /// 应用源变化处理：失效扫描缓存（AppGrabber/全应用视图下次查询即刷新），
    /// 并在 UI 线程重建 Dock 固定面板。AppSourceChanged 在后台线程触发，故需切 Dispatcher。
    /// </summary>
    private void OnAppSourceChanged(object? sender, EventArgs e)
    {
        try
        {
            _dockAppsService.InvalidateScanCache();

            if (_dockWindow is DockWindow dockWindow)
            {
                var dispatcher = dockWindow.Dispatcher;
                if (dispatcher.CheckAccess())
                {
                    dockWindow.RefreshPinnedPanelFromSource();
                }
                else
                {
                    dispatcher.BeginInvoke((Action)dockWindow.RefreshPinnedPanelFromSource);
                }
            }
        }
        catch
        {
            // 应用源变化刷新失败不阻断主流程。
        }
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _appSourceService.AppSourceChanged -= OnAppSourceChanged;

        _dockWindow?.Close();
        _dockWindow = null;
        return Task.CompletedTask;
    }
}
