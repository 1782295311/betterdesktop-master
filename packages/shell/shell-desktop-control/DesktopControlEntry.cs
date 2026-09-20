// BetterDesktop.DesktopControl —「桌面服务」进程的全部入口模式与服务运行时
//
// 【M1 → M2 的定位变化】
//   M1（2026-09-17 上午）：本 exe 只是"桌面控制菜单"的短命渲染器（弹完即退）。
//   M2（2026-09-17 下午 · 用户定调"托盘=中转站，功能按需启动"）：本 exe 升级为**常驻「桌面服务」** ——
//   它是"自绘桌面层（全屏窗口 + 图标网格 + 自绘右键菜单）+ 桌面控制菜单 + 原生模式双击隐藏图标钩子"
//   的唯一拥有者，**不依赖主程序**（主程序关掉后桌面照样在）。桌面层为什么必须搬家：
//     · 用户 2026-09-17 拍的边界："菜单栏和 Dock 需要主程序，自绘桌面和连带的自绘右键菜单不需要主程序"；
//     · 顺带解决内存：壳（菜单栏/Dock/状态栏/灵动岛/开始菜单 ≈125MB）变成"用户要才启动"。
//
// 【模式（跨进程契约）】
//   无参 / --service          常驻桌面服务（托盘/看门狗/宿主按需拉起）
//   --desktop-controls        弹「桌面控制」菜单：已有常驻服务 → 转发让它弹（勾选态按它内存里的真值）；
//                             没有 → 自己弹（M1 行为，短命进程）
//   --toggle-key <name>       翻转开关键：有服务 → 转发（服务进程内翻，插件即时反应）；没有 → 本地直写+原生生效
//   --toggle-desktop          翻转"自绘桌面"总开关：同上；本地兜底时翻到"开"会把服务拉起来
//   --stop                    优雅停止常驻服务
//   --icon-restore-sentinel <pid>  图标恢复哨兵（DesktopPlugin 隐藏原生图标后拉起本 exe 的这个模式，
//                             等目标 pid 退出 → 复原图标/任务栏；覆盖被 TerminateProcess 的死亡路径）
//
// 【装配】kernel loader + desktop.yml 子集（vibrancy/settings/context-menu/convert/clipboard-history，
// 五个提供者都不建窗口）+ **动态装载** desktop 插件（可卸载 → components.desktop 可进程内即时启停）。
// 【命令通道】命名管道 BetterDesktop.DesktopCmd（契约在 shell-core/DesktopControl/DesktopControlPipe.cs）。

// CA2000 全文件豁免：本文件的多处 `new SettingsService()` 是**手工管理生命周期**的——
// 短命路径里所有权交给"菜单关闭回调"（那时才允许落盘释放，提前 using 会在菜单还开着时就把服务释放掉），
// 常驻路径里交给字段并在 ShutdownService 统一释放。分析器无法跨闭包/字段追踪这条所有权链。
#pragma warning disable CA2000

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.DesktopControl;
using BetterDesktop.Shell.Desktop;
using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>「桌面服务」进程的入口分派与常驻运行时。</summary>
internal static class DesktopControlEntry
{
    // ===== 跨进程契约（与 DesktopControlPipe / CLI / 托盘 / 看门狗同步） =====

    private const string ServiceMutexName = "BetterDesktop.DesktopControl.Service";
    private const string DesktopControlsArg = "--desktop-controls";
    private const string StopArg = "--stop";
    private const string ToggleKeyArg = "--toggle-key";
    private const string ToggleDesktopArg = "--toggle-desktop";
    private const string SentinelArg = "--icon-restore-sentinel";

    /// <summary>
    /// 单连接读取超时（毫秒）。
    /// <para>
    /// 【为什么必须有】管道实例数限 1：对端只要**连上却不发消息**（探活探针、半开连接、
    /// 被杀进程留下的句柄），就会一直占着唯一的实例槽，后续所有命令静默失败。
    /// 这与 Host 侧 2026-09-16 真机事故是同一形态（见 host/MenuCommandPipe.cs 的 ReadTimeoutMs）。
    /// </para>
    /// </summary>
    private const int ReadTimeoutMs = 2000;

    /// <summary>自绘桌面总开关（与 DesktopPlugin.IsDesktopEnabled 同键）。</summary>
    private const string DesktopEnabledKey = "components.desktop";

    // ===== 运行时状态 =====

    private static readonly object Gate = new();

    private static Application? _app;
    private static Mutex? _serviceMutex;
    private static SettingsService? _settings;
    private static ServiceRuntime? _runtime;
    private static bool _stopping;
    private static bool _menuShown;

    public static int Run(Application app, string[] args)
    {
        _app = app;

        // ① 哨兵模式：必须在单实例互斥**之前**（服务/主实例持锁时哨兵若先抢锁会立刻退出，失去守护）。
        if (args.Length >= 2 && string.Equals(args[0], SentinelArg, StringComparison.Ordinal) &&
            int.TryParse(args[1], out var watchedPid))
        {
            app.Shutdown();
            return RunIconRestoreSentinel(watchedPid);
        }

        // ② 转发类模式：优先请常驻服务干活（它能就地翻设置、插件即时反应）。
        if (args.Length >= 1 && string.Equals(args[0], DesktopControlsArg, StringComparison.Ordinal))
        {
            if (DesktopControlPipe.TrySend(DesktopControlPipe.ActionShowMenu, string.Empty))
            {
                DesktopControlLog.Trace("已请常驻桌面服务弹出「桌面控制」菜单");
                app.Shutdown();
                return ExitCodes.Ok;
            }

            DesktopControlLog.Trace("无常驻桌面服务：本进程自行弹菜单（M1 短命路径）");
            return ShowMenuStandalone(app);
        }

        if (args.Length >= 1 && string.Equals(args[0], StopArg, StringComparison.Ordinal))
        {
            var sent = DesktopControlPipe.TrySend(DesktopControlPipe.ActionStop, string.Empty);
            DesktopControlLog.Trace(sent ? "已请求桌面服务优雅停止" : "桌面服务未在运行（无需停止）");
            app.Shutdown();
            return sent ? ExitCodes.Ok : ExitCodes.Ok; // 未运行也算成功（幂等的"停"）
        }

        if (args.Length >= 2 && string.Equals(args[0], ToggleKeyArg, StringComparison.Ordinal))
        {
            if (DesktopControlPipe.TrySend(DesktopControlPipe.ActionToggleKey, args[1]))
            {
                DesktopControlLog.Trace($"已请桌面服务翻转开关：{args[1]}");
                app.Shutdown();
                return ExitCodes.Ok;
            }

            return ToggleKeyLocally(app, args[1]);
        }

        if (args.Length >= 1 && string.Equals(args[0], ToggleDesktopArg, StringComparison.Ordinal))
        {
            if (DesktopControlPipe.TrySend(DesktopControlPipe.ActionToggleDesktop, string.Empty))
            {
                DesktopControlLog.Trace("已请桌面服务翻转「自绘桌面」");
                app.Shutdown();
                return ExitCodes.Ok;
            }

            return ToggleDesktopLocally(app);
        }

        // ③ 默认：常驻桌面服务
        if (TryParseServiceArgs(args, out var unknown))
        {
            return RunService(app);
        }

        DesktopControlLog.Trace($"未知参数：{unknown}（只认 {DesktopControlsArg} / {StopArg} / {ToggleKeyArg} / {ToggleDesktopArg}）");
        app.Shutdown();
        return ExitCodes.Usage;
    }

    /// <summary>服务模式只接受空参数或 --service。</summary>
    private static bool TryParseServiceArgs(string[] args, out string unknown)
    {
        unknown = string.Empty;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            unknown = arg;
            return false;
        }

        return true;
    }

    // =====================================================================
    // 常驻「桌面服务」
    // =====================================================================

    private static int RunService(Application app)
    {
        _serviceMutex = new Mutex(initiallyOwned: true, ServiceMutexName, out var isPrimary);
        if (!isPrimary)
        {
            // 正常不会走到（转发类模式在上面已处理）；真出现说明服务正在启停窗口期。
            DesktopControlLog.Trace("已有桌面服务在运行，本次退出");
            app.Shutdown();
            return ExitCodes.Ok;
        }

        // 承载窗：菜单的 DPI/工作区换算源 + 前台锚点。服务常驻，但这个窗口**永远不可见**（1px 屏幕外透明）。
        var anchor = new MenuAnchorWindow();
        app.MainWindow = anchor;
        anchor.Show();

        _ = InitializeServiceAsync(app);
        return ExitCodes.Ok;
    }

    private static async Task InitializeServiceAsync(Application app)
    {
        try
        {
            var context = new CordisContext(logSink: (level, message) => DesktopControlLog.Trace($"[{level}] {message}"));

            // 设置服务：context 非空 → 本进程内的 Set 会经事件总线广播，插件（desktop）即时反应。
            // 与宿主/CLI 共用同一份 settings.json，落盘走跨进程 mutex + 读-合并-写。
            _settings = new SettingsService(context);
            context.Provide<ISettingsService>(_settings);
            context.Provide<ISettingsSectionRegistry>(new SettingsSectionRegistry());

            var loader = new BetterDesktop.Kernel.Loader.LoaderService(new BetterDesktop.Kernel.Loader.LoaderOptions
            {
                ConfigPath = "desktop.yml",
                Factories =
                {
                    ["vibrancy"] = () => new BetterDesktop.Shell.Core.Vibrancy.VibrancyService(),
                    ["settings"] = () => new BetterDesktop.Shell.Settings.SettingsPlugin(),
                    ["context-menu"] = () => new BetterDesktop.Shell.ContextMenus.ContextMenuPlugin(),
                    ["convert"] = () => new BetterDesktop.Shell.Convert.ConvertPlugin(),
                    ["clipboard-history"] = () => new BetterDesktop.Shell.Clipboard.ClipboardPlugin(),
                },
            });

            var loaderHandle = context.Plugin(loader);

            // ⚠️ 必须回到 UI 线程再继续（ConfigureAwait(true)，**不是** false）：
            //   · DesktopPlugin 要创建 WPF 窗口（DesktopWindow / DesktopIconsControl）—— WPF 要求 STA/Dispatcher 线程；
            //   · 它还要注册 `Application.Current.Exit` 退出兜底 —— DispatcherObject 跨线程访问会直接抛
            //     "调用线程无法访问此对象，因为另一个线程拥有该对象"（2026-09-17 真机日志实测到这条）。
            // 从 UI 线程发起的 await 默认回 Dispatcher（WPF 装了 DispatcherSynchronizationContext）；
            // 照抄 agent/Program.cs 的 ConfigureAwait(false) 会落到线程池 → 上面两件事都会失败。
            await loaderHandle.AwaitAsync().ConfigureAwait(true);
            DesktopControlLog.Trace($"服务支撑插件装配完成（state={loaderHandle.State}）");

            // 桌面层本体：动态装载（可卸载），components.desktop 变化时由插件自身即时启停。
            var desktopHandle = context.Plugin(new DesktopPlugin());
            DesktopControlLog.Trace($"自绘桌面插件已装载（state={desktopHandle.State}）");

            // 【2026-09-17 修复"自绘右键菜单里点了没反应"】自绘桌面的右键菜单在 shell-desktop 内构建
            //（DesktopIconsControl → DesktopControlMenu.Build），它拿不到本进程的动作路由（依赖方向不允许）
            // → 由本进程注入，否则菜单栏 / Dock / 热键侧板这些**宿主内组件**的翻转只会写进本进程的设置
            //   快照，宿主永远收不到（用户实测的"没反应"，且本进程日志里连执行器那一行都不会有）。
            DesktopControlMenu.HostRunningProbe = HostPresence.IsRunning;
            DesktopControlMenu.ToggleRouter =
                name => DesktopToggleExecutor.Apply(name, _settings!, HostPresence.IsRunning());

            _runtime = new ServiceRuntime(context, loaderHandle, desktopHandle, _settings);

            StartPipeServer();
            DesktopControlLog.Trace("=== 桌面服务已就绪（常驻；自绘桌面 + 桌面控制菜单 + 双击钩子） ===");
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"桌面服务装配失败：{ex}");
            ShutdownService();
        }
    }

    /// <summary>常驻命令通道：单行协议（magic + action|path），一律回 UI 线程处理。</summary>
    private static void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (!_stopping)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        DesktopControlPipe.PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                    server.WaitForConnection();

                    // C1：**有界**读（上限 = 协议 MAX_MESSAGE_BYTES）+ 总超时。
                    // 此前是裸 ReadLine()：对端不发换行符就能把内存吃满，而实例数=1
                    // ⇒ 一条连接即可钉死整条命令通道（用户侧表现为"桌面控制点了没反应"）。
                    var outcome = BoundedPipeLine.TryRead(
                        server, BoundedPipeLine.DefaultMaxBytes, ReadTimeoutMs, out var line);
                    if (outcome != BoundedPipeLine.Outcome.Line)
                    {
                        DesktopControlLog.Trace($"命令通道：丢弃连接（{outcome}，未收到合法单行命令）");
                        continue;
                    }
                    if (string.IsNullOrEmpty(line) || !line.StartsWith(DesktopControlPipe.MagicPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var payload = line[DesktopControlPipe.MagicPrefix.Length..];
                    var separator = payload.IndexOf('|');
                    var action = separator < 0 ? payload : payload[..separator];
                    var path = separator < 0 ? string.Empty : payload[(separator + 1)..];

                    var dispatcher = _app?.Dispatcher;
                    if (dispatcher is null)
                    {
                        continue;
                    }

                    dispatcher.Invoke(() => HandleAction(action, path));
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        DesktopControlLog.Trace($"命令通道异常（继续监听）: {ex.Message}");
                        Thread.Sleep(200);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "DesktopCmdPipe"
        };
        thread.Start();
    }

    private static void HandleAction(string action, string path)
    {
        try
        {
            DesktopControlLog.Trace($"收到命令：{action} {path}");

            switch (action)
            {
                case DesktopControlPipe.ActionShowMenu:
                    ShowMenu(hostRunning: HostPresence.IsRunning());
                    break;

                case DesktopControlPipe.ActionToggleKey:
                    DesktopToggleExecutor.Apply(path, _settings!, HostPresence.IsRunning());
                    break;

                case DesktopControlPipe.ActionToggleDesktop:
                    ToggleDesktopInProcess();
                    break;

                case DesktopControlPipe.ActionStop:
                    ShutdownService();
                    break;

                default:
                    DesktopControlLog.Trace($"未知命令：{action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"命令处理失败（{action}）: {ex}");
        }
    }

    /// <summary>翻转"自绘桌面"总开关：在本进程内翻，DesktopPlugin 的 SettingsChanged 订阅会即时启停桌面层。</summary>
    private static void ToggleDesktopInProcess()
    {
        if (_settings is null)
        {
            return;
        }

        var current = _settings.Get(DesktopEnabledKey, true);
        var next = !current;
        _settings.Set(DesktopEnabledKey, next);
        DesktopControlLog.Trace($"切换自绘桌面（服务进程内）: {DesktopEnabledKey}={next}");
    }

    private static void ShutdownService()
    {
        lock (Gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
        }

        try
        {
            _runtime?.Dispose();
            _runtime = null;
            _settings?.Dispose();
            _settings = null;
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"服务收尾异常: {ex.Message}");
        }

        DesktopControlLog.Trace("桌面服务退出");
        _app?.Dispatcher.BeginInvoke(new Action(() => _app.Shutdown()));
    }

    // =====================================================================
    // M1 短命路径（无常驻服务时的兜底：弹菜单 / 翻开关）
    // =====================================================================

    private static int ShowMenuStandalone(Application app)
    {
        var hostAlive = HostPresence.IsRunning();
        var settings = new SettingsService();
        MenuAnchorWindow? anchor = null;

        // 无主窗口时贴边收敛会退化（拿不到 DPI 换算源）→ 临时挂一个不可见承载窗，弹完即收。
        try
        {
            anchor = new MenuAnchorWindow();
            app.MainWindow = anchor;
            anchor.Show();

            var count = DesktopControlMenu.ShowAtCursor(
                settings,
                () => ClipboardEngineLauncher.OpenPanel(DesktopControlLog.Trace),
                hostAlive,
                name => DesktopToggleExecutor.Apply(name, settings, hostAlive),
                onClosed: () =>
                {
                    try { settings.Dispose(); } catch { /* 落盘失败不阻断退出 */ }
                    app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
                });

            DesktopControlLog.Trace(count > 0 ? $"菜单已弹出：项数={count}" : "菜单无项可弹");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"弹菜单失败：{ex}");
            try { settings.Dispose(); } catch { /* 见上 */ }
            app.Shutdown();
            return ExitCodes.Failed;
        }
    }

    private static int ToggleKeyLocally(Application app, string name)
    {
        var hostAlive = HostPresence.IsRunning();
        var settings = new SettingsService();
        try
        {
            DesktopToggleExecutor.Apply(name, settings, hostAlive);
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"本地翻转失败（{name}）: {ex}");
            return ExitCodes.Failed;
        }
        finally
        {
            try { settings.Dispose(); } catch { /* 见上 */ }
            app.Shutdown();
        }
    }

    private static int ToggleDesktopLocally(Application app)
    {
        var settings = new SettingsService();
        try
        {
            var current = settings.Get(DesktopEnabledKey, true);
            var next = !current;
            settings.Set(DesktopEnabledKey, next);
            DesktopControlLog.Trace($"切换自绘桌面（本地直写）: {DesktopEnabledKey}={next}");

            if (next)
            {
                EnsureServiceStarted();
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"本地切换自绘桌面失败: {ex.Message}");
            return ExitCodes.Failed;
        }
        finally
        {
            try { settings.Dispose(); } catch { /* 见上 */ }
            app.Shutdown();
        }
    }

    /// <summary>把常驻桌面服务拉起来（"翻到开"必须有东西把桌面画出来）。</summary>
    private static void EnsureServiceStarted()
    {
        // 【2026-09-20 收敛】原先这里用 Environment.ProcessPath 直接把自己（同一个 exe）再拉一份 ——
        // 那是**短命菜单进程在替 core 做生命周期决策**：它绕过了 gate、绕过了"已在跑不重复拉起"、
        // 绕过了监护，core 也看不见这次启动（restarts / pending_spawn 不会更新）。
        // core 的 explicit_start_is_noop 正好覆盖这个场景 —— 且判活必须由 core 按**声明**做：
        // 菜单进程与常驻服务**同名**（都是 BetterDesktop.DesktopControl.exe），
        // 本地按进程名判活会把"正在弹菜单"读成"服务在运行"（审计 #9 的真机事故）。
        if (!CoreComponents.Start(CoreComponents.Desktop, DesktopControlLog.Trace))
        {
            DesktopControlLog.Trace("请 core 拉起桌面服务未成功（core 不可达或桌面组件已被设置关闭）");
        }
    }

    private static void ShowMenu(bool hostRunning)
    {
        if (_menuShown)
        {
            DesktopControlLog.Trace("菜单已在显示中，忽略重复请求");
            return;
        }

        _menuShown = true;
        try
        {
            var count = DesktopControlMenu.ShowAtCursor(
                _settings,
                () => ClipboardEngineLauncher.OpenPanel(DesktopControlLog.Trace),
                hostRunning,
                name => DesktopToggleExecutor.Apply(name, _settings!, hostRunning),
                onClosed: () => _menuShown = false);

            DesktopControlLog.Trace(count > 0 ? $"菜单已弹出：项数={count}" : "菜单无项可弹");
        }
        catch (Exception ex)
        {
            _menuShown = false;
            DesktopControlLog.Trace($"弹菜单失败：{ex}");
        }
    }

    // =====================================================================
    // 图标恢复哨兵（覆盖被 TerminateProcess / 冻结后结束的死亡路径）
    // =====================================================================

    /// <summary>
    /// 等目标进程退出后"复原桌面环境"：显示 explorer 原生图标 + 显示原生任务栏。
    /// 为什么必须有：`Application.Exit`/`AppDomain.ProcessExit` 在**被强杀**时都不触发，
    /// 桌面层隐藏原生图标后进程死亡 → 用户桌面图标永久消失（2026-09-06 真机记录的老问题）。
    /// </summary>
    private static int RunIconRestoreSentinel(int watchedPid)
    {
        DesktopControlLog.Trace($"图标恢复哨兵启动：等待 pid={watchedPid} 退出");

        try
        {
            using var watched = Process.GetProcessById(watchedPid);
            watched.WaitForExit();
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"哨兵：目标进程已不存在或取不到（{ex.Message}）");
        }

        var icons = DesktopControlNative.ApplyIconsHidden(false);
        var taskbar = DesktopControlNative.ApplyTaskbarVisible(true);
        DesktopControlLog.Trace($"哨兵已复原：原生图标={icons} 任务栏={taskbar}");
        return ExitCodes.Ok;
    }

    /// <summary>服务运行时（装配句柄集合）——退出时按逆序释放。</summary>
    private sealed class ServiceRuntime : IDisposable
    {
        private readonly IContext _context;
        private readonly IPluginHandle _loaderHandle;
        private readonly IPluginHandle _desktopHandle;
        private readonly SettingsService _settings;

        public ServiceRuntime(IContext context, IPluginHandle loaderHandle, IPluginHandle desktopHandle, SettingsService settings)
        {
            _context = context;
            _loaderHandle = loaderHandle;
            _desktopHandle = desktopHandle;
            _settings = settings;
        }

        public void Dispose()
        {
            // 逆序释放：desktop（会恢复原生图标）→ 支撑插件 → 设置落盘。
            _ = _desktopHandle.DisposeAsync();
            _ = _loaderHandle.DisposeAsync();
            _settings.Dispose();
            (_context as IDisposable)?.Dispose();
        }
    }
}
