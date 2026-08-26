// BetterDesktop.Shell.StartMenu — StartMenuPlugin
// 开始菜单插件：自绘 WPF 开始菜单（唯一后端）。
// 创建 StartMenuService（数据聚合）、StartKeyHook（Win 键）、ClassicLayout（经典两栏），
// 注册最近程序栏目与设置分区。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Sections;
using BetterDesktop.Shell.StartMenu.Services;
using BetterDesktop.Shell.StartMenu.Windows.Layouts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.StartMenu;

/// <summary>
/// 开始菜单插件：纯自绘 WPF 菜单（唯一后端）。
/// 依赖在它之前注册（Bootstrap 顺序）：AppSource → WindowTracker → Pinning → Search → Recent → Settings → StartMenu。
/// 订阅 IEventBus "shell.start.toggle"（Dock 左端开始图标 / Win 键钩子发出）→ 切换经典菜单；
/// 订阅 "shell.start.show-all-apps"（Dock 入口发出）→ 切换"所有应用"布局并弹出。
/// </summary>
public sealed class StartMenuPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.start-menu";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => new[]
    {
        typeof(IAppSourceService),
        typeof(IWindowTrackerService),
        typeof(IPinningService),
        typeof(IStartMenuSearchService),
        typeof(IRecentItemsService),
        typeof(IVibrancyService),
        typeof(IAppearanceService),
        typeof(ISettingsService)
    };

    private StartMenuService? _service;
    private IDisposable? _toggleSub;
    private IDisposable? _showAllAppsSub;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 默认活动布局跟随默认样式（win11）。后续 ShowLayout 按 startmenu.style 切换。
        var win11 = new Win11Layout();
        var service = new StartMenuService(
            context.Get<IAppSourceService>()!,
            context.Get<IWindowTrackerService>()!,
            context.Get<IStartMenuSearchService>()!,
            context.Get<IRecentItemsService>()!,
            context.Get<IPinningService>(),
            context.Get<IVibrancyService>(),
            context.Get<IAppearanceService>(),
            context.Get<ISettingsService>()!,
            context.Get<ISettingsWindowService>(),
            context.Get<IAppIconService>(),
            context.Logger,
            win11);

        service.RegisterLayout(win11);
        service.RegisterLayout(new AllAppsLayout());
        service.RegisterLayout(new Win10Layout());
        service.RegisterLayout(new Win7Layout());
        service.RegisterLayout(new ClassicLayout());
        service.RegisterSection(new RecentSectionProvider());
        service.RegisterSection(new PlacesSectionProvider());
        service.RegisterSection(new PowerSectionProvider());

        // 默认样式（startmenu.style：win7 / win10 / win11）→ 活动布局。
        var style = context.Get<ISettingsService>()!.Get("startmenu.style", "win11") ?? "win11";
        service.ShowLayout(StartMenuService.StyleToLayoutName(style));
        _service = service;

        context.Provide<IStartMenuService>(service);
        Services.StartMenuServiceBridge.Bind(service);

        var registry = context.Get<ISettingsSectionRegistry>();
        registry?.Register(new StartMenuSection());

        // Dock 左端「开始」图标 → 切换经典开始菜单（与 Win 键钩子共用契约）。
        _toggleSub = context.Events.On<string>("shell.start.toggle", (_, _) =>
        {
            service.Toggle();
            return Task.CompletedTask;
        });

        // Dock「所有应用」入口 → 开始菜单切 allapps 布局并弹出。
        _showAllAppsSub = context.Events.On<string>("shell.start.show-all-apps", (_, _) =>
        {
            service.ShowLayout("allapps");
            service.Show();
            return Task.CompletedTask;
        });

        // 启用自绘后端（挂 Win 键钩子）。
        service.Enable();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _toggleSub?.Dispose();
        _toggleSub = null;
        _showAllAppsSub?.Dispose();
        _showAllAppsSub = null;
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
