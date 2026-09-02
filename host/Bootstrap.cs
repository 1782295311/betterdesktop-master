using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using BetterDesktop.Kernel.Loader;
using BetterDesktop.Kernel.Timer;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windowing;
using BetterDesktop.Shell.Dock;
using BetterDesktop.Shell.MenuBar;
using BetterDesktop.Shell.Notification;
using BetterDesktop.Shell.QuickNote;
using BetterDesktop.Shell.Pinning;
using BetterDesktop.Shell.Recent;
using BetterDesktop.Shell.Search;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;
using BetterDesktop.Shell.Status;
using BetterDesktop.Shell.Desktop;

namespace BetterDesktop.Host;

/// <summary>宿主引导：装配内核与核心服务，注册电源管理插件。</summary>
public static class Bootstrap
{
    /// <summary>
    /// 构建桌面内核。
    /// 流程：CordisContext（挂日志 sink）→ 隐藏宿主窗口取句柄 → Provide 窗口服务
    ///       → Plugin PowerManagement（降优先级+放行休眠）→ Plugin Timer → Plugin Vibrancy。
    /// </summary>
    public static IContext Build()
    {
        // 1. 内核上下文 + 日志 sink
        var context = new CordisContext(logSink: (level, msg) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}");
        });

        // 2. 隐藏宿主窗口（不调 Show，只取句柄）
        var hostWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            Opacity = 0
        };
        // 显示窗口（WPF 需要 Show 才能创建 Hwnd），然后立即隐藏
        hostWindow.Show();
        var hwnd = new WindowInteropHelper(hostWindow).EnsureHandle();
        hostWindow.Hide();
        // 防止隐藏窗口后 WPF 自动退出（ShutdownMode 默认 OnLastWindowClose）
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        context.Provide<IWindowHandleService>(new WindowHandleService(hwnd));

        // 2.5 提前提供设置服务与分区注册表，使所有插件（含 Dock）的 LoadAsync 能可靠取到设置，
        // 不依赖插件加载时序。SettingsPlugin 随后复用此实例（不再重复创建）。
        // 主创建点（进程级单例）：持有引用，进程退出时显式 Dispose（flush 末次改动 + 释放 Timer）。
        // 生命周期由下方 Application.Current.Exit 钩子托管，分析器 CA2000 压制：
#pragma warning disable CA2000
        var settingsSvc = new SettingsService();
#pragma warning restore CA2000
        context.Provide<ISettingsService>(settingsSvc);
        context.Provide<ISettingsSectionRegistry>(new SettingsSectionRegistry());

        // 3. 电源管理插件（最先加载，立刻降优先级 + 放行系统休眠）
        context.Plugin(new PowerManagement(context.Logger));

        // 4. 基础服务
        context.Plugin(new TimerService());
        context.Plugin(new VibrancyService());

        // 4.1 系统状态采集插件（shell.status）：向全局提供 CPU/内存/电池/音量/麦克风/网络/输入法/亮度
        //     监控服务。必须早于任何 UI 消费插件（菜单栏/任务栏/控制中心）加载；
        //     依赖为空，放在基础服务之后、AppSource 之前。
        context.Plugin(new StatusPlugin());

        // 4.25 装配 HMR 热重载管理器 + 内存治理器（进程级单例）。
        // 阈值采用内核经验值（比例 25/50/75% 按物理内存自适应，采样 5s，熔断 3 次/60s），
        // 不暴露给用户设置；可用环境变量 BD_DISABLE_GOVERNOR=1 临时完全关闭以便排查。
        // 治理器定位为"出错管理"而非"常态管理"：仅在插件真正出错/内存真正失控时动作。
        // 持有引用，进程退出时显式 Dispose（释放治理器 Timer）；分析器 CA2000 压制：
#pragma warning disable CA2000
        var governorOptions = new ResourceGovernorOptions();
        governorOptions.Enabled = !string.Equals(
            Environment.GetEnvironmentVariable("BD_DISABLE_GOVERNOR"), "1", StringComparison.Ordinal);
        governorOptions.Resolve();
        var hmrManager = new HmrManager(context, governorOptions: governorOptions);
#pragma warning restore CA2000
        context.Provide<IHmrManager>(hmrManager);
        // 同一实例同时注册为 IResourceGovernor，供 CordisContext.Plugin 把每个插件注册给治理器（消除空转）。
        context.Provide<IResourceGovernor>(hmrManager);
        // 进程级内存兜底：仅告警，不主动杀死进程（避免把常态内存波动当成致命异常）。
        // 真正的进程重启由崩溃捕获（AppDomain.UnhandledException）负责——那是"出错管理"。
        hmrManager.OnProcessCritical = source =>
            context.Logger.Error($"[Governor] 进程级内存持续超致命阈值（{source}）——仅告警，宿主不主动重启");

        // 4.5 应用数据来源插件（shell.app-source）：必须早于 Dock 加载，
        // 因为它通过 Inject 向 Dock 提供 IAppSourceService / IAppIconService。
        // 若此处漏接，DockPlugin 的 AllDependenciesAvailable 永远为 false，
        // 内核会让 DockPlugin 停在 Pending、LoadAsync 永不执行 → 所有窗口都不创建。
        context.Plugin(new BetterDesktop.Shell.AppSource.AppSourcePlugin());

        // 4.6 运行中窗口 / 应用追踪插件（shell.window-tracker）：抽出 Dock 的窗口检测能力，
        //     通过 Inject IWindowTrackerService 向 Dock / 未来开始菜单 / 任务栏提供统一服务。
        //     必须早于 DockPlugin 加载（DockPlugin 的 Inject 声明依赖它）。
        context.Plugin(new BetterDesktop.Shell.WindowTracker.WindowTrackerPlugin());

        // 4.6.x 通用固定 / 收藏服务插件（shell.pinning）：抽出 Dock 的固定能力，按 zone 分区，
        //     供 Dock / 未来开始菜单 / 任务栏共用（M7）。必须早于 DockPlugin 加载
        //     （DockPlugin 的 Inject 新增 IPinningService 依赖）。依赖 IAppSourceService（已由 AppSourcePlugin 提供）。
        context.Plugin(new PinningPlugin());

        // 4.7 搜索与最近项服务插件（shell.search / shell.recent）：开始菜单搜索（程序/设置/文件）
        //     与最近程序/文档/跳转列表。依赖 IAppSourceService(AppSource)、IWindowTrackerService(WindowTracker)。
        //     在 WindowTracker/Pinning 之后注册，供 Step 7 开始菜单消费。
        context.Plugin(new BetterDesktop.Shell.Search.SearchPlugin());
        context.Plugin(new BetterDesktop.Shell.Recent.RecentPlugin());

        // 5. Dock 插件（单独 try-catch，便于定位窗口层失败）
        try
        {
            context.Plugin(new DockPlugin());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Bootstrap 加载 DockPlugin 失败: {ex.Message}", ex);
        }

        // 6. 设置中心插件（shell.settings）：Provide ISettingsService / ISettingsSectionRegistry / ISettingsWindowService。
        // 依赖为空，加载顺序不敏感；各插件后续经 ISettingsSectionRegistry.Register 贡献设置分区。
        // 启动即打开设置窗口的行为由 SettingsPlugin.LoadAsync 内部完成（与应用提取器一致）。
        context.Plugin(new BetterDesktop.Shell.Settings.SettingsPlugin());

        // 6.1 新装应用通知插件（shell.notification）：订阅 IAppSourceService.AppSourceChanged，
        //     右下角弹窗提醒新装应用（一键固定/忽略）。与 Dock 解耦（不依赖 shell-dock），
        //     Dock 卸载后通知仍可用。在 SettingsPlugin 之后注册，确保 IAppearanceService 在 LoadAsync 时可用。
        context.Plugin(new BetterDesktop.Shell.Notification.NotificationPlugin());

        // 6.5 任务栏外观控制插件（shell.taskbar.appearance）：控制原生 Windows 任务栏的
        //     透明/模糊/亚克力与场景化外观联动（参考 TranslucentTB / Open-Shell）。
        //     必须在 SettingsPlugin 之后加载（依赖其 Provide 的 ISettingsSectionRegistry 注册分区）。
        //     进程退出/卸载时由插件自身 ReturnToStock 还原任务栏默认外观。
        // ⚠️ 2026-08-25 取证结论：注入目标=Shell_TrayWnd（非 Dock）、未被杀软拦截；对照原版已修
        //    color 参数 ARGB→ABGR（Win10/Win11 双路径）。当前重新启用验证导航栏分区与外观效果，
        //    若仍异常再评估回退禁用。
        context.Plugin(new BetterDesktop.Shell.Taskbar.TaskbarAppearancePlugin());

        // 6.6 开始菜单（shell.start-menu）：Open-Shell Metro/Win11 风弹出菜单 + 开始菜单外观控制
        //     （风格/颜色/透明度/模糊，复用 shell-core Vibrancy + ThemeCenter 令牌）。
        //     依赖：ISettingsSectionRegistry(SettingsPlugin) + IAppSourceService/IAppIconService(AppSourcePlugin)
        //     + IVibrancyService + IAppearanceService。必须在上述插件之后加载。
        context.Plugin(new BetterDesktop.Shell.StartMenu.StartMenuPlugin());

        // 6.65 自绘桌面（shell.desktop）：全屏壁纸 + 可导航桌面/文件夹浏览器（Provide IDesktopBrowser
        //      给菜单栏左区联动）。必须在 menu-bar 之前加载：桌面窗口 Z 序底 + 浏览器先注册。
        // 6.6.4 统一右键菜单（shell.context-menu）：五区块骨架 + 场景模板/贡献项注册点。
        //     无依赖（Inject 为空）；必须先于 desktop 装配（其 LoadAsync 消费 IMenuService/IFileClassifier）。
        context.Plugin(new BetterDesktop.Shell.ContextMenus.ContextMenuPlugin());

        // 6.6.5 文档转换（shell-convert）：经贡献项把「转 PDF」平铺进文件右键菜单。
        context.Plugin(new BetterDesktop.Shell.Convert.ConvertPlugin());

        context.Plugin(new BetterDesktop.Shell.Desktop.DesktopPlugin());

        // 6.7 顶部菜单栏（shell.menu-bar）：右区 = 移植自 tools/ShellComponentsPlayground 的紧凑状态条
        //     （系统托盘/FPS/CPU/内存/WiFi/实时网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面），
        //     各图标点击打开对应独立面板。依赖 shell.status 监控（4.1 已加载）+ IVibrancyService
        //     + IAppearanceService（SettingsPlugin 提供），必须在此之后加载。
        context.Plugin(new MenuBarPlugin());

        // 6.8 快速笔记外部扩展（shell.quick-note）：由扩展中心开关（extensions.quick-note.enabled）
        //     驱动的常驻浮窗。无依赖（Inject 为空），缺失服务时静默降级；订阅 ISettingsService.Changed
        //     实现运行时启停，使「扩展中心」从记忆开关变为真正启停的端到端闭环。
        context.Plugin(new QuickNotePlugin());

        // 7. 原生 Windows 部件管理（参考 Cairo 的 ExplorerHelper.HideExplorerTaskbar）：
        //    本桌面环境不实现原生任务栏，只管理其显示/隐藏。components.wintaskbar=false 时隐藏原生任务栏
        //    （让 Dock 独占底部区域）；true（默认）则保留原生任务栏。退出时恢复显示，避免桌面环境退出后
        //    原生任务栏消失（对齐 Cairo 在 Dispose 时把 HideExplorerTaskbar 复位为 false 的行为）。
        var settings = context.Get<ISettingsService>();
        var showNativeTaskbar = settings is null || settings.Get("components.wintaskbar", true);
        NativeTaskbarManager.SetTaskbarVisible(showNativeTaskbar);
        if (!showNativeTaskbar)
        {
            Application.Current.Exit += (_, _) => NativeTaskbarManager.SetTaskbarVisible(true);
        }
        // 进程退出时释放设置服务（落盘末次 debounce 内的改动 + 释放后台 Timer）。
        Application.Current.Exit += (_, _) => settingsSvc.Dispose();
        // 进程退出时释放 HMR 管理器（释放内存治理器 Timer 与监控任务）。
        Application.Current.Exit += (_, _) => hmrManager.Dispose();

        // 启动追踪：把各插件最终状态落盘（同步落盘已就绪），便于定位"窗口空/内核未加载"。
        try
        {
            foreach (var handle in context.GetHandles())
            {
                DiagnosticLog.Trace("Bootstrap", $"plugin {handle.PluginName} => {handle.State}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("Bootstrap", $"列举插件状态失败: {ex.Message}");
        }

        return context;
    }
}

