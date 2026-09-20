// BetterDesktop.Shell.MenuBar — 插件入口（IPlugin）
// 角色：`shell.menu-bar` — 顶部菜单栏（左区导航入口 + 右区状态条扩展）。
// 左区 = MenuBarLeftZone：程序菜单（复用 IStartMenuService）+ 位置/下载/文档。
// 右区 = StatusBarMenuBarExtension（移植自 tools/ShellComponentsPlayground 的紧凑状态条）：
//   系统托盘/FPS/CPU/内存/WiFi/实时网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面，
//   各图标左键/右键打开对应独立面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Sections;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.MenuBar;

// ============================================================
// 【白话导航 · 顶部菜单栏域】凭白话需求定位到精确文件：
//   "顶部菜单栏窗口本体"                → Windows/MenuBarWindow.cs；通用弹层基类 Windows/MenuBarPopupWindow.cs
//   "菜单栏左区（程序/位置/下载/文档）"  → Windows/MenuBarLeftZone.cs + Services/LeftZonePopupExtensions.cs
//   "菜单栏右区状态图标条"              → Status/MenuBarStatusStrip.cs + Services/StatusBarMenuBarExtension.cs
//   "WiFi 列表/密码面板"               → Windows/WifiPopupWindow.cs + Services/WifiEnumerator.cs
//   "蓝牙面板"                         → Windows/BluetoothPopupWindow.cs + Services/BluetoothEnumerator.cs
//   "电源/电池面板"                    → Windows/PowerPopupWindow.cs + Services/PowerEnumerator.cs
//   "输入法面板"                       → Windows/ImePopupWindow.cs + Services/ImeLayoutEnumerator.cs
//   "CPU/内存/网络/声音/麦克风面板"     → Windows/CpuPanelWindow.cs、MemoryPanelWindow.cs、NetworkPanelWindow.cs、SoundPanelWindow.cs、MicrophonePanelWindow.cs
//   "控制中心 / 亮度 / 主题 / 扩展中心"  → Windows/ControlCenterWindow.cs、BrightnessSliderControl.cs、ThemePopupWindow.cs、ExtensionsCenterWindow.cs
//   "第三方往菜单栏加扩展图标"          → shell-core/Contracts/IMenuBarExtension.cs + Services/MenuBarExtensions.cs + Contracts/ExtensionCatalog.cs
//   状态数据本身来自 shell-status（Inject I*Monitor），本域只做 UI。
// ============================================================

/// <summary>菜单栏插件：注册扩展点 + 构造 MenuBarWindow 并显示。</summary>
public sealed class MenuBarPlugin : IPlugin
{
    public string Name => "shell.menu-bar";

    /// <summary>
    /// 依赖声明（内核据此调度：未满足时本插件停在 <c>Pending</c>，不静默降级）。
    /// <para><see cref="IPinningService"/> 必须写在这里，而不是只靠可空 <c>Get</c> 采集：
    /// 可空 Get 让「搜索结果右键的固定项」依赖装配顺序（历史坑：dock 先于 context-menu 激活
    /// 导致 <c>Get</c> 恒 null，菜单项静默消失 —— 用户表现为「功能时有时无」）。
    /// 声明后顺序由内核保证，缺失是显式 Pending 而不是少一个菜单项。</para>
    /// <para>与 <c>StartMenuPlugin.Inject</c> 的既有做法一致（该插件同样声明本服务），
    /// 故不引入新的装配约束。</para>
    /// </summary>
    public IReadOnlyList<Type> Inject => new[] { typeof(IPinningService) };

    private MenuBarWindow? _window;
    private StatusBarMenuBarExtension? _statusBar;
    private IAppearanceService? _appearance;
    private ISettingsService? _settings;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // DI：从内核注入的上下文 Get 采集服务（全部可能为 null，插件按 M10 降级）
        var ime = context.Get<IImeMonitor>();
        var net = context.Get<INetworkMonitor>();
        var vol = context.Get<IVolumeMonitor>();
        var mic = context.Get<IMicrophoneMonitor>();
        var bat = context.Get<IBatteryMonitor>();
        var brightness = context.Get<IBrightnessMonitor>();
        var mem = context.Get<IMemoryMonitor>();
        var cpu = context.Get<ICpuMonitor>();
        var vibrancy = context.Get<IVibrancyService>();
        var appearance = context.Get<IAppearanceService>();
        var settings = context.Get<ISettingsService>();
        // 左区 Logo 快捷功能菜单的"设置"项：打开设置窗口（SettingsPlugin 提供；缺失时菜单项点击无动作，M10）。
        var settingsWindow = context.Get<ISettingsWindowService>();
        // 左区前台窗口标题：IWindowTrackerService（WinEvent 钩子事件驱动，Bootstrap 4.6 注册）。
        var windowTracker = context.Get<IWindowTrackerService>();
        // 【2026-09-17 用户拍板】不再消费 IDesktopBrowser：左区文件夹工具条整条移除（操作已在自绘桌面右键菜单）。
        // 搜索按钮复用 shell-search 聚合搜索服务（SearchPlugin 在 Bootstrap 4.7 注册，早于本插件）；
        // 未注册时为 null，SearchPopupWindow 内显示"搜索不可用"占位（M10 降级）。
        var search = context.Get<IStartMenuSearchService>();
        // 搜索结果图标：IAppIconService（AppSourcePlugin 提供，按 AppItem 提取真实应用图标）。
        var appIcon = context.Get<IAppIconService>();
        // 搜索结果右键「固定到 Dock」的解析源：IAppSourceService.ResolveFromPath（LNK/URL/EXE → AppItem），
        // 让引擎索引命中的程序文件（如 MAA.exe）也能固定到 Dock（2026-09-17）；缺失时该项不显示（M10）。
        var appSource = context.Get<IAppSourceService>();
        // 日历（shell.calendar）：农历/节假日与调休/节气/节日/系统日程/天气；未注册时降级为纯农历月视图（M10）。
        var calendar = context.Get<BetterDesktop.Shell.Calendar.Contracts.ICalendarService>();
        // 搜索结果右键菜单的「固定到 Dock」：IPinningService（PinningPlugin 在 Bootstrap 4.6.x 注册，
        // 且已列入上方 Inject —— 到这里必定非 null）。此处保留可空取用只是外层兜底：
        // 正常路径不再依赖它，装配顺序由内核保证（2026-09-14 由可空 Get 改为声明式依赖）。
        var pinning = context.Get<IPinningService>();

        // Vibrancy 必要：窗口需毛玻璃；降级为共享空实现保证不抛（shell-core NullVibrancyService）
        vibrancy ??= NullVibrancyService.Instance;

        // 主题接线：菜单栏自绘图标此前 55 处硬编码 Brushes.White，切亮色主题会白字白底不可读。
        // MenuBarTheme 持有单一共享画刷，这里只需把外观服务的当前前景色同步进去并订阅变更，
        // 全部图标/文字即随主题实时换色（SyncFrom 内部已做 UI 线程兜底）。
        if (appearance is not null)
        {
            _appearance = appearance;
            MenuBarTheme.SyncFrom(appearance);
            // 违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期
            context.Effect(() => context.Events.On<AppearanceChangedArgs>(
                ShellEvents.AppearanceChanged,
                (e, _) =>
                {
                    OnAppearanceChanged(e);
                    return Task.CompletedTask;
                }));
        }

        // 右区 = 紧凑状态条（系统托盘/FPS/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
        var media = context.Get<BetterDesktop.Shell.Music.Contracts.IMediaPlaybackService>();
        _statusBar = new StatusBarMenuBarExtension(vol, mic, bat, ime, brightness, net, mem, cpu, vibrancy, appearance, settings, search, appIcon, calendar, pinning, null, media, context.Events, appSource);
        var extensions = new List<IMenuBarExtension>(capacity: 1)
        {
            _statusBar
        };

        // P0-3/C2: all extensions go through the registry (right-zone buttons + left-zone popup providers)
        var registry = new MenuBarExtensionRegistry();
        registry.Register(_statusBar);
        registry.Register(new LogoMenuBarExtension(settingsWindow, vibrancy, appearance, context.Events));
        registry.Register(new StacksPopupBarExtension(vibrancy, appearance, settings));

        // 构造并显示菜单栏主窗口
        // events：Logo 菜单「应用提取器」等跨包入口经 IEventBus 契约（shell.appgrabber.show → shell.dock）。
        _window = new MenuBarWindow(registry, vibrancy, appearance, context.Logger, settingsWindow, windowTracker, settings, context.Events);
        _window.Show();

        // 组件开关（2026-09-07 用户拍板）：components.menubar 即时启停——启动时按设置决定是否显示；
        // 运行中变更（自绘右键「功能管理」/ 系统右键「自绘桌面 ▸」命令桥）即时 Show/Hide，无需重启。
        _settings = settings;
        if (settings is not null)
        {
            if (!settings.Get("components.menubar", true))
            {
                _window.Hide();
            }
            // 违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                }));
        }

        // 设置 → 菜单栏：系统功能（系统托盘/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
        // 的显隐统一由设置分区管理；「+」扩展中心只管外部扩展功能插件。
        // 这里把「切换即时生效」的回调接到运行状态条上（注册表由 SettingsPlugin 在 6.0 节提前 Provide）。
        context.Get<ISettingsSectionRegistry>()?.Register(
            new MenuBarSection(
                (id, on) => _statusBar?.SetComponentVisible(id, on),
                hide => _statusBar?.SetTrayHideSystemIcons(hide)));

        context.Logger.Info($"{Name} 已加载：已显示菜单栏主窗口，左区 Logo 快捷功能菜单{(settingsWindow is null ? "（设置服务缺失，设置项降级）" : string.Empty)}，右区状态条（{extensions.Count} 个扩展）就绪");
        return Task.CompletedTask;
    }

    private void OnAppearanceChanged(AppearanceChangedArgs e)
    {
        MenuBarTheme.SyncFrom(_appearance);
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (e.Key != "components.menubar" || _window is null)
        {
            return;
        }
        var enabled = _settings?.Get("components.menubar", true) ?? true;
        var dispatcher = _window.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            if (enabled) { _window.Show(); } else { _window.Hide(); }
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (enabled) { _window.Show(); } else { _window.Hide(); }
            }));
        }
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _window?.Close();
        _window = null;
        _statusBar?.Dispose();
        _statusBar = null;
        return Task.CompletedTask;
    }
}
