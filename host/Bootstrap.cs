using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using BetterDesktop.Kernel.Loader;
using BetterDesktop.Kernel.Timer;
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
//   "程序入口 / 启动顺序"                → App.xaml.cs（WPF Application）+ 本文件 Bootstrap.Build（内核与插件装配）
//   "插件清单 / 加载顺序"                → cordis.yml（声明式插件树，由 kernel-loader 的 LoaderService 装配）
//   "第二个实例 / 命令行参数转发"        → MenuCommandPipe.cs（单实例管道）
//   "命令行：切换桌面/切换键/右键子进程"  → DesktopToggleCommand.cs、ToggleKeyCommand.cs、MenuService.cs（--menu-service 独立进程加载 ShellEx 防崩）
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
        DiagnosticLog.SetSink(logSink.Invoke);
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
                ["status"] = () => new StatusPlugin(),
                ["app-source"] = () => new BetterDesktop.Shell.AppSource.AppSourcePlugin(),
                ["window-tracker"] = () => new BetterDesktop.Shell.WindowTracker.WindowTrackerPlugin(),
                ["pinning"] = () => new PinningPlugin(),
                ["search"] = () => new BetterDesktop.Shell.Search.SearchPlugin(),
                ["recent"] = () => new BetterDesktop.Shell.Recent.RecentPlugin(),
                ["dock"] = () => new DockPlugin(),
                ["settings"] = () => new BetterDesktop.Shell.Settings.SettingsPlugin(),
                ["notification"] = () => new BetterDesktop.Shell.Notification.NotificationPlugin(),
                ["taskbar-appearance"] = () => new BetterDesktop.Shell.Taskbar.TaskbarAppearancePlugin(),
                ["start-menu"] = () => new BetterDesktop.Shell.StartMenu.StartMenuPlugin(),
                ["context-menu"] = () => new BetterDesktop.Shell.ContextMenus.ContextMenuPlugin(),
                ["convert"] = () => new BetterDesktop.Shell.Convert.ConvertPlugin(),
                ["desktop"] = () => new BetterDesktop.Shell.Desktop.DesktopPlugin(),
                ["calendar"] = () => new BetterDesktop.Shell.Calendar.CalendarPlugin(),
                ["menu-bar"] = () => new MenuBarPlugin(),
                ["quick-note"] = () => new QuickNotePlugin(),
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
            var show = settings is null ||
                       (settings.Get("components.wintaskbar", true) &&
                        !settings.Get("components.dock", true));
            NativeTaskbarManager.SetTaskbarVisible(show);
        }
        ApplyNativeTaskbar();
        if (settings is not null)
        {
            // 违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期（宿主卸载时自动取消订阅）。
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    if (e.Key is "components.dock" or "components.wintaskbar")
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
                        // 自绘UI开关（系统右键「自绘桌面 ▸」→ 命令桥 → 热切，即时生效）
                        if (settings is not null && path.Length > 0)
                        {
                            var uiKey = path.ToLowerInvariant() switch
                            {
                                "icons" => "desktop.iconsHidden",
                                "menubar" => "components.menubar",
                                "dock" => "components.dock",
                                "taskbar" => "components.wintaskbar",
                                _ => null
                            };
                            if (uiKey is not null)
                            {
                                var def = string.Equals(uiKey, "desktop.iconsHidden", StringComparison.Ordinal) ? false : true;
                                var cur = settings.Get(uiKey, def);
                                settings.Set(uiKey, !cur);
                                context.Logger.Info($"[menu-cmd] 切换UI(命令桥): {uiKey}={!cur}");
                            }
                        }
                        break;
                    case "toggle-desktop":
                        // 自绘桌面开关（系统右键菜单 → 命令桥 → 热切换，即时启停）
                        if (settings is not null)
                        {
                            var curDesktop = settings.Get("components.desktop", true);
                            settings.Set("components.desktop", !curDesktop);
                            context.Logger.Info($"[menu-cmd] 切换自绘桌面(命令桥): components.desktop={!curDesktop}");
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
                            $"当前文件不支持直接转换为 {target}\n\n请改用「更多格式…」查看完整目标列表。", "格式转换"));
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
}

