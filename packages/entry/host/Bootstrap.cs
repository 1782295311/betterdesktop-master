using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using BetterDesktop.Kernel.Loader;
using BetterDesktop.Kernel.Timer;
using BetterDesktop.Shell.Clipboard;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windowing;
using BetterDesktop.Shell.Desktop;
using BetterDesktop.Shell.Dock;
using BetterDesktop.Shell.MenuBar;
using BetterDesktop.Shell.Notification;
using BetterDesktop.Shell.Pinning;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.QuickNote;
using BetterDesktop.Shell.Recent;
using BetterDesktop.Shell.Search;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;
using BetterDesktop.Shell.Status;

namespace BetterDesktop.Host;

// ============================================================
// 【白话导航 · 宿主层 host】程序如何启动、命令行从哪进来：
//   "按序粘贴会话进度（面板上报）"        → PublishPasteSession（MenuCmd 管道 case "paste-session"）

//   "程序入口 / 启动顺序"                → App.xaml.cs（WPF Application）+ 本文件 Bootstrap.Build（内核与插件装配）
//   "插件清单 / 加载顺序"                → cordis.yml（声明式插件树，由 kernel-loader 的 LoaderService 装配）
//   "第二个实例 / 命令行参数转发"        → MenuCommandPipe.cs（单实例管道）
//   "命令行：切换桌面/切换键"             → DesktopToggleCommand.cs、ToggleKeyCommand.cs（--menu-service 子进程已 2026-09-10 移除：桌面右键回归自绘）
//   "崩溃看门狗 / 异常自动恢复"          → HostWatchdog.cs
//   "退出/崩溃后恢复 explorer 桌面图标"  → IconRestoreSentinel.cs
//   "日志文件写在哪"                    → FileLogSink.cs（%LocalAppData%/BetterDesktop/logs）
//   "启动闪屏"                          → Views/SplashWindow.xaml.cs
//   "对外窗口句柄服务"                  → WindowHandleService.cs
// ============================================================

/// <summary>宿主引导：装配内核与核心服务，注册电源管理插件。</summary>
public static class Bootstrap
{
    /// <summary>
    /// 构建桌面内核。
    /// 流程：CordisContext（挂日志 sink）→ 隐藏宿主窗口取句柄 → Provide 窗口服务
    ///       → Provide 设置服务/HMR 治理器 → LoaderService 按 cordis.yml 声明式装配插件树
    ///       → 原生任务栏管理 → 菜单命令桥 → 插件状态落盘。
    /// </summary>
    public static async Task<IContext> Build()
    {
        // 1. 内核上下文 + 文件日志 sink（M10 单一管道：写入 %LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log）
        // FileLogSink 生命周期由 Application.Current.Exit 释放（CA2000 抑制：分析器无法跨事件追踪）。
#pragma warning disable CA2000
        var logSink = new FileLogSink();
#pragma warning restore CA2000
        // 一并交出 flush：崩溃/退出路径需要同步刷盘（异步队列在进程被终止时会丢最后几条）。
        DiagnosticLog.SetSink(logSink.Invoke, logSink.Flush);
        var context = new CordisContext(logSink: logSink.Invoke);

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
        var settingsSvc = new SettingsService(context);
#pragma warning restore CA2000
        context.Provide<ISettingsService>(settingsSvc);
        context.Provide<ISettingsSectionRegistry>(new SettingsSectionRegistry());

        // 2.75 装配 HMR 热重载管理器 + 内存治理器（进程级单例）。
        // 必须在插件加载前 Provide IResourceGovernor，使 CordisContext.Plugin 把每个插件注册给治理器。
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

        // 3-6. 声明式插件树装配（cordis.yml，违规4修复）：
        // LoaderService 自身注册为插件，其 LoadAsync 读取 cordis.yml 并按条目顺序串行加载子插件。
        // 迁移期保序：cordis.yml 条目顺序与原硬编码 context.Plugin() 顺序完全一致，行为等价。
        // Factories 字典处理有构造参数的插件（仅 PowerManagement 需要 context.Logger）。
        var loaderOptions = new LoaderOptions
        {
            ConfigPath = "cordis.yml",
            Factories =
            {
                ["power-management"] = () => new PowerManagement(context.Logger),
                ["timer"] = () => new TimerService(),
                ["vibrancy"] = () => new VibrancyService(),
                ["activity"] = () => new BetterDesktop.Shell.Core.Activity.ActivityPlugin(),
                ["status"] = () => new StatusPlugin(),
                ["app-source"] = () => new BetterDesktop.Shell.AppSource.AppSourcePlugin(),
                ["window-tracker"] = () => new BetterDesktop.Shell.WindowTracker.WindowTrackerPlugin(),
                ["pinning"] = () => new PinningPlugin(),
                ["search"] = () => new BetterDesktop.Shell.Search.SearchPlugin(),
                ["recent"] = () => new BetterDesktop.Shell.Recent.RecentPlugin(),
                ["dock"] = () => new DockPlugin(),
                ["settings"] = () => new BetterDesktop.Shell.Settings.SettingsPlugin(),
                ["hotkeys"] = () => new BetterDesktop.Shell.Core.Hotkeys.HotkeysPlugin(),
                ["hotkeys-panel"] = () => new BetterDesktop.Shell.HotkeyPanel.HotkeyPanelPlugin(),
                ["notification"] = () => new BetterDesktop.Shell.Notification.NotificationPlugin(),
                ["taskbar-appearance"] = () => new BetterDesktop.Shell.Taskbar.TaskbarAppearancePlugin(),
                ["start-menu"] = () => new BetterDesktop.Shell.StartMenu.StartMenuPlugin(),
                ["context-menu"] = () => new BetterDesktop.Shell.ContextMenus.ContextMenuPlugin(),
                ["convert"] = () => new BetterDesktop.Shell.Convert.ConvertPlugin(),
                ["desktop"] = () => new BetterDesktop.Shell.Desktop.DesktopPlugin(),
                ["calendar"] = () => new BetterDesktop.Shell.Calendar.CalendarPlugin(),
                ["menu-bar"] = () => new MenuBarPlugin(),
                ["quick-note"] = () => new QuickNotePlugin(),
                ["clipboard-history"] = () => new ClipboardPlugin(),
                ["island"] = () => new BetterDesktop.Shell.Island.IslandPlugin(),
            },
        };
        var loader = new LoaderService(loaderOptions);
        var loaderHandle = context.Plugin(loader);
        await loaderHandle.AwaitAsync();
        if (loaderHandle.State == PluginState.Failed)
        {
            throw new InvalidOperationException(
                "LoaderService 加载失败（cordis.yml 缺失或解析错误），插件树未装配——宿主无法启动。");
        }

        // 7. 原生 Windows 部件管理（参考 Cairo 的 ExplorerHelper.HideExplorerTaskbar）：
        //    本桌面环境不实现原生任务栏，只管理其显示/隐藏。
        //    2026-09-02 定稿：dock 启用（components.dock，默认 true）即隐藏原生任务栏——dock 独占
        //    底部条带，dock.bottomMargin 从屏幕底边算起（AppBar 协商才不会被任务栏顶回去）。
        //    2026-09-07 增补「任务栏显隐」独立开关（components.wintaskbar，默认 true）：
        //    显式关闭 → 无条件隐藏原生任务栏（压倒 dock 联动）；保持开启 → 回退 dock 独占联动。
        //    三键任一变更即时生效。退出时无条件恢复显示，避免桌面环境退出后原生任务栏消失。
        var settings = context.Get<ISettingsService>();
        void ApplyNativeTaskbar()
        {
            // 【2026-09-17 优先级 · 用户拍板】「桌面控制」的显式选择 > 其它功能的默认隐藏效果
            //（dock 启用即默认隐藏原生任务栏）。规则抽成纯函数在 shell-core（DesktopControlRules，单测钉住）：
            //   显式隐藏 → 隐藏；显式显示（留痕键 true）→ 显示；从未动过 → 沿用 dock 联动。
            var show = settings is null ||
                       BetterDesktop.Shell.Core.DesktopControl.DesktopControlRules.ShouldShowNativeTaskbar(
                           settings.Get("components.wintaskbar", true),
                           settings.Get("components.dock", true),
                           settings.Get(BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TaskbarExplicitVisibleKey, false));
            NativeTaskbarManager.SetTaskbarVisible(show);
        }

        void EnsureDesktopServiceRunning()
        {
            try
            {
                if (settings?.Get("components.desktop", true) != true)
                {
                    return; // 用户关了自绘桌面：不拉起（服务可能因双击隐藏图标另在跑，那是另一条线）
                }

                var running = System.Diagnostics.Process.GetProcessesByName("BetterDesktop.DesktopControl");
                try
                {
                    if (running.Length > 0)
                    {
                        return;
                    }
                }
                finally
                {
                    // 句柄必须释放（长会话下每次启动/设置变更都会走这里，漏释放会缓慢累积）。
                    foreach (var p in running)
                    {
                        p.Dispose();
                    }
                }

                // 定位约定见 DesktopControlLocator：同目录 → %LOCALAPPDATA%\BetterDesktop（开发/局部部署）。
                // 2026-09-17 真机教训：只查同目录 → 开发态必然"未部署"（各工程各自 bin）。
                var exe = BetterDesktop.Shell.Core.DesktopControl.DesktopControlLocator.Find();
                if (exe is null)
                {
                    context.Logger.Info("[desktop] 桌面服务未部署（同目录与 %LOCALAPPDATA%\\BetterDesktop 都没有 BetterDesktop.DesktopControl.exe），自绘桌面不可用");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                context.Logger.Info("[desktop] 已拉起桌面服务（自绘桌面由独立进程承载，壳退出后它继续在）");
            }
            catch (Exception ex)
            {
                context.Logger.Info($"[desktop] 拉起桌面服务失败: {ex.Message}");
            }
        }

        // 【S4-4（2026-09-19）此处原为 `EnsureAgentRunning()`，已随 Agent 退役删除】
        //
        // 保留这段说明是为了让"这里**原来有东西**"可见 —— 否则下一个人只会看到"宿主不拉 Agent"，
        // 以为从来如此。Agent（常驻能力宿主）的三项能力各自已有归宿：
        //   ① 截图热键        → core **独占注册**（`core/src/hotkeys.rs`，S4-1）
        //   ② 系统右键扩展自愈 → core 触发 / CLI 执行（`core/src/shellmenu.rs` + RepairGate，S4-2）
        //   ③ 桌面服务监护     → core 的 `supervisor::reconcile`（S4-3，含 stopFlag / 管道判活）
        // 这三条迁完之前，删掉这个函数会让能力在"宿主启动路径"上消失 —— 所以它是**最后**删的。

        // 【2026-09-18 真机缺口续 · "托盘程序也不在"】
        // **托盘本身**只由"开机自启"或用户手动双击 Tray.exe 带起。
        // 而 README 明确把"直接启动 BetterDesktop.Host.exe"列为合法入口 —— 走这条路时：
        //   ① 任务栏没有托盘图标（用户观感："托盘程序不在"，与"程序没装上"无法区分）；
        //   ② 托盘菜单里的入口（打开设置中心 / 剪贴板面板 / 系统集成状态 / 应急恢复）全部无从触达。
        // 故宿主启动路径同样补齐托盘，语义与自启一致、与上面那段被删的 Agent 补齐同一套幂等写法。
        // 刻意**不做"复活抑制"**：托盘的 ExitTray 不写留痕文件（只隐藏图标后退出线程），
        // 而"用户主动启动宿主"= 要一个完整可用的外壳，此时托盘就该在。
        void EnsureTrayRunning()
        {
            try
            {
                var running = System.Diagnostics.Process.GetProcessesByName("BetterDesktop.Tray");
                try
                {
                    if (running.Length > 0)
                    {
                        return;
                    }
                }
                finally
                {
                    foreach (var p in running)
                    {
                        p.Dispose();
                    }
                }

                var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "BetterDesktop.Tray.exe");
                if (!System.IO.File.Exists(exe))
                {
                    context.Logger.Info("[tray] 托盘未部署（同目录无 BetterDesktop.Tray.exe），跳过拉起");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                context.Logger.Info("[tray] 已拉起托盘（常驻控制面：托盘菜单 / 设置中心 / 剪贴板面板 / 系统集成状态）");
            }
            catch (Exception ex)
            {
                context.Logger.Info($"[tray] 拉起托盘失败: {ex.Message}");
            }
        }

        void ClearTaskbarExplicitLatchOnDockChange()
        {
            // 只在留痕为 true 时写（避免无意义写盘 + 不产生多余的设置变更事件）。
            if (settings is not null &&
                settings.Get(BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TaskbarExplicitVisibleKey, false))
            {
                settings.Set(BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TaskbarExplicitVisibleKey, false);
                context.Logger.Info("[menu-cmd] Dock 变更 → 已清除任务栏「显式可见」留痕（回到 dock 默认隐藏）");
            }
        }
        ApplyNativeTaskbar();

        // 【M2 2026-09-17】桌面层已搬到独立进程：壳启动时"确保桌面服务在跑"，用户观感与搬家前一致；
        // 但**不接管、不随壳退出**（用户拍板：自绘桌面 + 自绘右键菜单不需要主程序）。
        EnsureDesktopServiceRunning();

        // 托盘（见 EnsureTrayRunning 注释）：它是常驻控制面，缺了它用户会觉得"程序没装上"。
        EnsureTrayRunning();

        if (settings is not null)
        {
            // 违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期（宿主卸载时自动取消订阅）。
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    // 【2026-09-17 用户拍板】Dock 一变（关掉 / 重开）→ 清掉任务栏"显式可见"留痕，回到 dock 默认隐藏。
                    // 放在宿主侧是为了覆盖**旁路写入**：设置中心的 Dock 开关直接写 settings，不经过桌面控制的翻转 helper。
                    // （命令行/托盘/独立进程那几条翻转路径已在写入时自行复位，见 DesktopToggleCatalog.ExplicitOverrides。）
                    if (e.Key == "components.dock")
                    {
                        ClearTaskbarExplicitLatchOnDockChange();
                    }

                    if (e.Key is "components.dock" or "components.wintaskbar"
                        or BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TaskbarExplicitVisibleKey)
                    {
                        ApplyNativeTaskbar();
                    }
                    return Task.CompletedTask;
                }));
        }
        Application.Current.Exit += (_, _) => NativeTaskbarManager.SetTaskbarVisible(true);
        // 进程退出时释放设置服务（落盘末次 debounce 内的改动 + 释放后台 Timer）。
        Application.Current.Exit += (_, _) => settingsSvc.Dispose();
        // 进程退出时释放 HMR 管理器（释放内存治理器 Timer 与监控任务）。
        Application.Current.Exit += (_, _) => hmrManager.Dispose();
        // 进程退出时释放文件日志 sink（排空队列 + 释放文件句柄）。
        Application.Current.Exit += (_, _) => logSink.Dispose();

        // 8. M3 菜单命令桥：命名管道服务端（系统右键注入项 → 宿主）
        MenuCommandPipe.StartServer(async (action, path) =>
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                switch (action)
                {
                    case "notify-error":
                        // 2026-09-10：CLI headless 错误转发（文件不存在/转换失败等）→ 宿主统一提示，
                        // 避免原生 Win32 MessageBox 与自绘体验割裂；path 字段承载单行消息（管道按行读）。
                        if (path.Length > 0)
                        {
                            System.Windows.MessageBox.Show(path, "BetterDesktop",
                                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        }
                        break;
                    case "open-settings":
                        context.Get<ISettingsWindowService>()?.Show();
                        break;
                    case "dock-pin":
                        var pinning = context.Get<IPinningService>();
                        if (pinning is not null && path.Length > 0)
                        {
                            pinning.Pin("dock", new BetterDesktop.Shell.AppSource.Models.AppItem
                            {
                                Id = path,
                                Name = System.IO.Path.GetFileName(path),
                                Source = BetterDesktop.Shell.AppSource.Models.AppSource.UserAdded,
                            });
                            pinning.Save();
                        }
                        break;
                    case "toggle-key":
                        // 自绘UI开关（系统右键「桌面控制」→ 命令桥）。
                        //
                        // 【M2 2026-09-17】桌面自有的键（桌面图标显隐 / 双击隐藏图标）**不在这里翻**：
                        // 桌面层已搬到独立进程，自绘图标网格读的是**它自己**内存里的设置快照 →
                        // 宿主进程内翻只会写盘、网格不反应（用户看到的还是"点了没反应"）。转交桌面服务就地翻。
                        if (BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TryGet(path, out var svcToggle) &&
                            svcToggle.Name is BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.Icons
                                or BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.DoubleClick)
                        {
                            EnsureDesktopServiceRunning();
                            if (BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.TrySend(
                                    BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.ActionToggleKey, path))
                            {
                                context.Logger.Info($"[menu-cmd] 切换UI 已转交桌面服务: {path}");
                                break;
                            }

                            context.Logger.Info($"[menu-cmd] 桌面服务不可达，回退宿主内翻转: {path}");
                        }

                        // 键名/默认值单点 = DesktopToggleCatalog：此前宿主/CLI/Host ToggleKeyCommand 各持一份映射，
                        // 已漂移过一次（CLI 漏了 doubleclick）→ 这里不再自持副本，顺带支持 hotkey-panel。
                        if (settings is not null &&
                            BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.TryGet(path, out var uiToggle))
                        {
                            var cur = settings.Get(uiToggle.SettingsKey, uiToggle.Default);
                            var nextVal = !cur;
                            settings.Set(uiToggle.SettingsKey, nextVal);

                            // 显式留痕：让「桌面控制」的选择压过其它功能的默认隐藏（如 dock 默认隐藏任务栏）。
                            foreach (var (overrideKey, overrideValue) in
                                     BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.ExplicitOverrides(uiToggle, nextVal))
                            {
                                settings.Set(overrideKey, overrideValue);
                            }

                            context.Logger.Info($"[menu-cmd] 切换UI(命令桥): {uiToggle.SettingsKey}={nextVal}");
                        }
                        break;
                    case "toggle-desktop":
                        // 自绘桌面开关（系统右键「切换到自绘桌面」/ 托盘「功能开关」→ 命令桥）。
                        //
                        // 【M2 2026-09-17】桌面层已搬到独立进程，翻这个键**必须由服务进程执行**
                        // （它读的是自己内存里的设置快照，宿主翻一眼它不会变）：
                        //   ① 服务在跑 → 转交给它翻：进程内插件订阅 SettingsChanged → 即时建/停桌面层 + 恢复原生图标；
                        //   ② 服务不在 → 本地写盘 + 拉起来（**先写盘再拉起**，否则服务启动时读到旧值 → 翻到"开"却画不出桌面）。
                        if (settings is not null)
                        {
                            if (BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.TrySend(
                                    BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.ActionToggleDesktop,
                                    string.Empty))
                            {
                                context.Logger.Info("[menu-cmd] 切换自绘桌面 已转交桌面服务");
                                break;
                            }

                            var curDesktop = settings.Get("components.desktop", true);
                            settings.Set("components.desktop", !curDesktop);
                            context.Logger.Info($"[menu-cmd] 切换自绘桌面(本地): components.desktop={!curDesktop}");
                            EnsureDesktopServiceRunning();
                        }
                        break;
                    case "paste-session":
                        // 剪贴板面板 → 壳：按序粘贴/按格粘会话进度（灵动岛上屏）。载荷 "index|total|cell"，
                        // **只含进度、不含剪贴板内容**（2026-09-16 用户明确的隐私要求，见 ClipboardPasteSessionNotice）。
                        PublishPasteSession(context, path);
                        break;
                    case "clipboard-history":
                        // 系统右键「剪贴板历史…」/菜单栏按钮 → 单实例管道 → 打开历史面板。
                        var clipboard = context.Get<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService>();
                        if (clipboard is not null)
                        {
                            clipboard.OpenHistoryWindow();
                            context.Logger.Info("[menu-cmd] 打开剪贴板历史");
                        }
                        else
                        {
                            context.Logger.Info("[menu-cmd] 剪贴板服务未注册（shell-clipboard 未加载）");
                        }
                        break;
                    case "convert":
                    case "convert-more":
                        // 系统文件右键「转换为… / 更多格式…」（2026-09-07）：弹自绘转换菜单（光标处），
                        // 矩阵全部目标 + 引擎置灰 + 执行反馈闭环都在 ConvertMenuService。
                        if (path.Length > 0)
                        {
                            ShowConvertMenu(path);
                        }
                        break;
                    case "desktop-controls":
                        // 【M2 2026-09-17】桌面层已搬到独立进程（BetterDesktop.DesktopControl.exe），
                        // 「桌面控制」菜单也随之归它渲染：勾选态/置灰按它内存里的真值，桌面自有开关由它就地翻转。
                        // 服务不在时才退回下面的宿主内渲染（老路径，保证功能不丢）。
                        if (BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.TrySend(
                                BetterDesktop.Shell.Core.DesktopControl.DesktopControlPipe.ActionShowMenu,
                                string.Empty))
                        {
                            context.Logger.Info("[menu-cmd] desktop-controls 已转交桌面服务");
                            break;
                        }
                        goto case "desktop-controls-local";
                    case "desktop-controls-local":
                        // 系统桌面右键「桌面控制」单入口（2026-09-11 · 静态一级 + 动态二级）：
                        // 二级菜单由 DesktopControlMenu 按当前设置动态生成（勾选态 + 条件项）。
                        // 剪贴板历史项由本进程服务注入——shell-desktop 不依赖 shell-clipboard。
                        var clipSvc = context.Get<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService>();
                        var ctlCount = BetterDesktop.Shell.Desktop.Services.DesktopControlMenu.ShowAtCursor(
                            settings,
                            clipSvc is null ? null : clipSvc.OpenHistoryWindow);
                        context.Logger.Info($"[menu-cmd] desktop-controls 菜单已显示: 项数={ctlCount}");
                        break;
                    case "compress":
                    case "compress-zip":
                    case "compress-7z":
                    case "compress-rar":
                    case "unzip-here":
                    case "unzip-to":
                        // 系统文件右键「压缩到 ▸ / 解压到 ▸」（2026-09-07）：命令桥直调归档服务 + MessageBox 反馈
                        if (path.Length > 0)
                        {
                            RunArchiveCommand(action, path);
                        }
                        break;
                    default:
                        if (action.StartsWith("convert-to-", StringComparison.Ordinal))
                        {
                            // 系统级联子菜单直转：convert-to-<目标格式> <文件> → 查矩阵直接执行 + MessageBox 反馈
                            var target = action["convert-to-".Length..];
                            ConvertDirect(path, target);
                            break;
                        }
                        context.Logger.Info($"[menu-cmd] 未知命令: {action}");
                        break;
                }
            });
        });


        // 系统文件右键压缩/解压（2026-09-07）：compress（任意文件/文件夹）→ 压缩为 zip；
        // unzip-here/unzip-to → 解压（zip 内置；rar/7z 走 WinRAR，引擎缺失给明确提示）。
        void RunArchiveCommand(string action, string filePath)
        {
            try
            {
                var archive = context.Get<BetterDesktop.Shell.Convert.Contracts.IArchiveService>();
                if (archive is null)
                {
                    context.Logger.Info("[menu-cmd] 压缩/解压服务未注册（shell-convert 未加载）");
                    return;
                }

                var isCompress = action == "compress"
                    || action == "compress-zip"
                    || action == "compress-7z"
                    || action == "compress-rar";
                if (isCompress && !System.IO.File.Exists(filePath) && !System.IO.Directory.Exists(filePath))
                {
                    context.Logger.Info($"[menu-cmd] compress 路径不存在: {filePath}");
                    return;
                }
                if (!isCompress && !System.IO.File.Exists(filePath))
                {
                    context.Logger.Info($"[menu-cmd] unzip 文件不存在: {filePath}");
                    return;
                }

                context.Logger.Info($"[menu-cmd] {action} 开始: {filePath}");
                _ = RunArchiveAsync(archive, action, filePath);
            }
            catch (Exception ex)
            {
                context.Logger.Info($"[menu-cmd] {action} 异常: {ex.Message}");
            }
        }

        async Task RunArchiveAsync(BetterDesktop.Shell.Convert.Contracts.IArchiveService archive, string action, string filePath)
        {
            string message;
            try
            {
                var result = action switch
                {
                    "compress" or "compress-zip" => await archive.CompressZipAsync(new[] { filePath }),
                    "compress-7z" => await archive.Compress7zAsync(new[] { filePath }),
                    "compress-rar" => await archive.CompressRarAsync(new[] { filePath }),
                    _ => await archive.ExtractAsync(filePath, toNamedFolder: action == "unzip-to"),
                };
                message = result.Success ? result.Message : result.Message;
            }
            catch (Exception ex)
            {
                message = $"操作异常：{ex.Message}";
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(message, "压缩与解压"));
        }
        // 系统级联子菜单直转（2026-09-07）：convert-to-<目标> → 查矩阵存在性 → ConversionService 执行 → MessageBox 反馈。
        void ConvertDirect(string filePath, string target)
        {
            try
            {
                if (filePath.Length == 0 || !System.IO.File.Exists(filePath))
                {
                    context.Logger.Info($"[menu-cmd] convert-to 文件不存在: {filePath}");
                    return;
                }

                if (ConversionMatrix.Find(System.IO.Path.GetExtension(filePath), target) is null)
                {
                    context.Logger.Info($"[menu-cmd] convert-to 不支持: {filePath} → {target}");
                    Application.Current.Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(
                            $"当前文件类型不支持转换为 {target}。", "格式转换"));
                    return;
                }

                var svc = context.Get<BetterDesktop.Shell.Convert.Services.ConversionService>();
                if (svc is null)
                {
                    context.Logger.Info("[menu-cmd] convert-to 服务未注册（shell-convert 未加载）");
                    return;
                }

                context.Logger.Info($"[menu-cmd] convert-to 直转开始: {filePath} → {target}");
                _ = RunDirectConvertAsync(svc, filePath, target);
            }
            catch (Exception ex)
            {
                context.Logger.Info($"[menu-cmd] convert-to 直转异常: {ex.Message}");
            }
        }

        async Task RunDirectConvertAsync(BetterDesktop.Shell.Convert.Services.ConversionService svc, string filePath, string target)
        {
            string message;
            try
            {
                var results = await svc.ConvertAsync(new[] { filePath }, target);
                var ok = results.Count(r => r.Success);
                message = ok == 1
                    ? $"转换完成：{results[0].Output ?? "(未知输出)"}"
                    : $"转换失败：{(string.IsNullOrWhiteSpace(results[0].Message) ? results[0].Error.ToString() : results[0].Message)}";
            }
            catch (Exception ex)
            {
                message = $"转换异常：{ex.Message}";
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(message, "格式转换"));
        }

        // 系统文件右键转换菜单（2026-09-07）：光标处弹自绘菜单（DesktopMenuPopup），
        // 点选目标格式 → ConvertMenuService 执行 + MessageBox 反馈。
        void ShowConvertMenu(string filePath)
        {
            try
            {
                if (!System.IO.File.Exists(filePath))
                {
                    context.Logger.Info($"[menu-cmd] convert 文件不存在: {filePath}");
                    return;
                }

                var convertMenu = context.Get<BetterDesktop.Shell.Convert.Contracts.IConvertMenuService>();
                if (convertMenu is null)
                {
                    context.Logger.Info("[menu-cmd] convert 菜单服务未注册（shell-convert 未加载）");
                    return;
                }

                var entries = convertMenu.BuildMenuItems(new[] { filePath });
                if (entries.Count == 0)
                {
                    context.Logger.Info($"[menu-cmd] convert 无可转目标: {filePath}");
                    return;
                }

                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Application.Current?.MainWindow)
                    .PixelsPerDip;
                if (dpi <= 0)
                {
                    dpi = 1.0;
                }

                if (NativeMethods.GetCursorPos(out var pt))
                {
                    BetterDesktop.Shell.Desktop.Services.DesktopMenuPopup.Show(
                        entries, new System.Windows.Point(pt.X / dpi, pt.Y / dpi));
                    context.Logger.Info($"[menu-cmd] convert 菜单已显示: {filePath} 项数={entries.Count}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.Info($"[menu-cmd] convert 菜单显示失败: {ex.Message}");
            }
        }

        // 启动追踪：把各插件最终状态落盘（同步落盘已就绪），便于定位"窗口空/内核未加载"。
        try
        {
            foreach (var handle in context.GetHandles())
            {
                context.Logger.Info($"[Bootstrap] plugin {handle.PluginName} => {handle.State}");
            }
        }
        catch (Exception ex)
        {
            context.Logger.Info($"[Bootstrap] 列举插件状态失败: {ex.Message}");
        }

        return context;
    }

    /// <summary>
    /// 把剪贴板面板上报的「按序粘贴 / 按格粘」进度广播给壳内订阅方（灵动岛）。
    /// <para>
    /// 载荷格式 <c>index|total|cell</c>（<c>index&lt;=0</c> 或 <c>total&lt;=0</c> = 会话已结束/已取消）。
    /// <b>只含进度、不含剪贴板内容</b>：面板进程拥有会话状态机，壳看不见，所以必须有这条通道；
    /// 但任何内容字段都会让"粘贴就上屏"变成隐私事故（2026-09-16 用户明确要求）。
    /// </para>
    /// <para>解析失败只记日志并丢弃——管道线程绝不能抛（M10），更不能让畸形载荷影响仲裁。</para>
    /// </summary>
    private static void PublishPasteSession(IContext context, string payload)
    {
        var parts = payload.Split('|');
        if (parts.Length < 3
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var total))
        {
            context.Logger.Warn($"[menu-cmd] paste-session 载荷无法解析（已丢弃）：{payload}");
            return;
        }

        var cell = parts[2] == "1";
        // 第 4 段（可选）= 下一条内容预览（面板已压成单行/截断；管道按行读，故换行必须已被面板去掉）
        var preview = parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : null;
        var notice = new BetterDesktop.Shell.Clipboard.Contracts.ClipboardPasteSessionNotice(
            Active: index > 0 && total > 0,
            Index: index,
            Total: total,
            Cell: cell,
            Preview: preview);

        _ = context.Events.EmitAsync(BetterDesktop.Shell.Core.ShellEvents.ClipboardPasteSession, notice);
        context.Logger.Info($"[menu-cmd] 按序粘贴会话：{(notice.Active ? $"第 {index}/{total} 项" : "结束")}{(cell ? "（按格粘）" : string.Empty)}");
    }
}

