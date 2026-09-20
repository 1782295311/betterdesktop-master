using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Animation;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Services;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.Dock;

// ============================================================
// 【白话导航 · Dock 域】凭白话需求定位到精确文件：
//   "Dock 显示/隐藏、显隐动画节奏"        → DockWindow.xaml.cs（窗口本体）+ DockTickPolicy.cs（帧节拍）
//   "Dock 贴边/占屏（AppBar 协商）"       → DockWindow.AppBar.cs + Native/DockAppBarReservation.cs
//   "Dock 上有哪些图标/固定/排序/分组"    → Services/DockAppsService.cs + Services/AppGroupStore.cs（数据）、Services/DockItemTemplate.cs（项模板）
//   "Dock 布局/尺寸/边距/放大效果"        → Services/DockLayoutService.cs + Models/DockLayoutMetrics.cs + Controls/DockItem.xaml.cs
//   "Dock 图标右键菜单"                  → Services/DockMenuPopup.cs（dock 自管 WPF 菜单）+ Services/DockItemTemplate.cs
//   "应用提取器/拖拽排序窗口"             → Windows/AppGrabberWindow.cs
//   "Dock 图标模糊/高清"                 → Services/DockIconService.cs（图标来自 shell-app-source）
//   "拼音/字母搜索匹配"                  → Services/PinyinMatcher.cs
//   "Win 键/多任务视图等键盘互操作"       → Native/KeyboardInterop.cs、Native/MultitaskingViewVisibilityService.cs
// ============================================================

/// <summary>
/// Dock 插件：管理 Dock 本体与应用提取器（AppGrabberWindow，经 "shell.appgrabber.show" 事件唤起）。
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

    private DockWindow? _dockWindow;
    private IAppSourceService _appSourceService = null!;

    /// <summary>
    /// 应用源服务（供 <c>DockWindow</c> 做运行项**溯源**用，T3）：判定某个进程 exe 是否为
    /// "应用索引里已知的应用"只能问它。
    /// <para>走属性而非再加构造参数：dock 窗口的构造链已经很长，且这属于"同一插件内共享"的依赖；
    /// 加参数的收益（显式）不抵它带来的调用点改动面。</para>
    /// </summary>
    internal IAppSourceService? AppSource => _appSourceService;
    private IAppIconService _appIconService = null!;
    private IDockAppsService _dockAppsService = null!;
    private IDockIconService _dockIconService = null!;
    private IPinningService _pinningService = null!;
    private IVibrancyService? _vibrancy;
    private IAppearanceService? _appearance;
    private IEventBus? _events;
    private IDisposable? _appGrabberShowSub;
    private ISettingsService? _settings;
    private IContext? _buildContext;

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
        // 关闭则不创建任何 Dock 窗口（含应用提取器 AppGrabber，其已融合原 Launchpad 能力）。默认启用。
        // 2026-09-07：订阅与依赖获取前置——dock=false 启动也订阅，运行中经自绘/系统右键开关
        // 切回开启时热建窗口（BuildDockWindow），无需重启宿主。
        var settings = context.Get<ISettingsService>();
        _settings = settings;
        if (settings is not null)
        {
            // 违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                }));
        }

        _buildContext = context;
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

        if (settings is not null && !settings.Get("components.dock", true))
        {
            return Task.CompletedTask;
        }

        try
        {
            BuildDockWindow(context, settings);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"DockPlugin.LoadAsync 失败: {ex.Message}", ex);
        }
    }

    /// <summary>构建并显示 Dock 主窗口（LoadAsync 首次 + 运行中 components.dock 关→开热建复用）。
    /// 依赖字段已在 LoadAsync 前置获取；服务/窗口全部重建，事件订阅先退订再订阅防重。</summary>
    private void BuildDockWindow(IContext context, ISettingsService? settings)
    {
        // 字段在 LoadAsync 前置获取并断言非空；跨方法流分析失效，此处显式解引用。
        var vibrancy = _vibrancy!;
        var appSource = _appSourceService!;
        var appIcon = _appIconService!;
        var pinning = _pinningService!;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var storagePath = Path.Combine(appData, "BetterDesktop", "dock-pinned.json");

        _dockAppsService = new DockAppsService(appSource, pinning, storagePath);
        var dockVisual = new DockVisualSettings(settings, context.Events);
        _dockIconService = new DockIconService(appIcon, dockVisual);

        // 注册 Dock 专属门面服务供其他 UI 插件（应用提取器 / 新装通知）消费。
        context.Provide<IDockAppsService>(_dockAppsService);
        context.Provide<IDockIconService>(_dockIconService);
        context.Provide<DockVisualSettings>(dockVisual);

        // 【S8 · 2026-09-14】设置中心「Dock 固定项」：固定项健康态的可观测出口
        //（此前失效只静默让位，用户只感知「图标没了」）。分区签名只给 (ISettingsService, IThemeTokens)，
        // 拿不到内核上下文，故经桥接暴露门面——仓库既有做法（TaskbarServiceBridge / StartMenuServiceBridge）。
        DockAppsServiceBridge.Bind(_dockAppsService);
        context.Get<ISettingsSectionRegistry>()?.Register(new Sections.DockPinnedSection());

        // 2026-09-05 收口：dock 菜单自管（DockWindow 内建 DockItemTemplate + DockMenuPopup），
        // 不再注册中央贡献者/模板；「固定到 Dock」贡献者随中央管线退役（桌面右键已转系统原生）。
        var classifier = context.Get<IFileClassifier>();

        _dockWindow = new DockWindow(
            vibrancy,
            new AnimationService(),
            new DockService(),
            _dockAppsService,
            _dockIconService,
            new DockLayoutService(bottomMargin: dockVisual.BottomMargin, settings: settings),
            this,
            appIcon,
            settings,
            _appearance,
            dockVisual,
            classifier,
            context.Events);
        _dockWindow.Show();

        // 应用源变化（开始菜单创建/删除/改名）时失效扫描缓存并刷新 Dock 固定面板，
        // 安装/卸载程序后无需重启即自动生效。先退订再订阅防重复挂载。
        appSource.AppSourceChanged -= OnAppSourceChanged;
        appSource.AppSourceChanged += OnAppSourceChanged;

        // 事件桥：Logo 菜单「应用提取器」等外部入口 → shell.appgrabber.show → 打开提取器
        // （窗口懒创建复用在 DockWindow 内，跨包只经 IEventBus 契约，不引 shell-dock 类型）。
        _appGrabberShowSub?.Dispose();
        _appGrabberShowSub = _events?.On<string>("shell.appgrabber.show", (_, _) =>
        {
            ShowAppGrabberFromEvent();
            return Task.CompletedTask;
        });
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

    /// <summary>处理 shell.appgrabber.show：切 UI 线程打开应用提取器（事件可能在任意线程到达）。</summary>
    private void ShowAppGrabberFromEvent()
    {
        try
        {
            if (_dockWindow is not DockWindow dockWindow)
            {
                return;
            }

            var dispatcher = dockWindow.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                dockWindow.InvokeShowAppGrabber();
            }
            else
            {
                dispatcher.BeginInvoke((Action)dockWindow.InvokeShowAppGrabber);
            }
        }
        catch
        {
            // 打开应用提取器失败不阻断（M10）。
        }
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (e.Key != "components.dock")
        {
            return;
        }
        var enabled = _settings?.Get("components.dock", true) ?? true;
        if (enabled && _dockWindow is null)
        {
            // 运行中从关→开（dock=false 启动后经自绘/系统右键开关切回）：依赖字段已就绪，热建窗口。
            try
            {
                if (_buildContext is not null)
                {
                    BuildDockWindow(_buildContext, _settings);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.dock", $"dock 热建窗口失败: {ex.Message}");
            }
            return;
        }
        if (_dockWindow is null)
        {
            return;
        }
        var dispatcher = _dockWindow.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            // 组件级启停：停/启 auto-hide tick，防止「隐藏 Dock」后被 tick 每拍拉回。
            _dockWindow.SetComponentEnabled(enabled);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => _dockWindow.SetComponentEnabled(enabled)));
        }
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _appGrabberShowSub?.Dispose();
        _appGrabberShowSub = null;
        _appSourceService.AppSourceChanged -= OnAppSourceChanged;

        _dockWindow?.Close();
        _dockWindow = null;
        return Task.CompletedTask;
    }
}
