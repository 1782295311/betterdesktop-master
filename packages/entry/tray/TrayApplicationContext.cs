using System.ComponentModel;
using System.Reflection;

namespace BetterDesktop.Tray;

/// <summary>
/// 托盘应用上下文：图标 + 右键菜单 + 全部菜单行为。
/// 菜单语义遵循"托盘=控制面"：勾选项一律以 settings.json 的真实值为准（打开菜单时刷新）；
/// **功能开关一律经 CLI 契约落地**（不自己写设置、不自己碰窗口），保证与主程序状态永不漂移。
/// 一处**有意的例外**（超出 CLI 契约的能力，已在代码处标注）：
///   1. 开机自启 —— 直接写 HKCU\Run（托盘零包引用，registry 写入不需要走 CLI）。
///
/// 【S4-4（2026-09-19）】原先还有第 2 条例外"常驻服务 / 更新器 / 应急恢复的启停 —— 直接拉起组件进程"。
/// 其中的**常驻服务（Agent）与看门狗（Watchdog）已随 S4-4 退役**，对应菜单一并删除：
/// 它们的职责迁入 core（截图热键 `core/src/hotkeys.rs` / 右键自愈 `core/src/shellmenu.rs` /
/// 组件监护 `core/src/supervisor.rs`）。更新器与应急恢复的拉起**保留**（那两件事仍由各自的 exe 承担）。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>功能开关项：显示名 / settings 键 / 默认值 / 落地用的 CLI 参数。</summary>
    private sealed record ToggleSpec(string Label, string Key, bool Default, string[] CliArgs);

    private static readonly ToggleSpec[] Toggles =
    [
        new("自绘桌面", "components.desktop", true, ["--toggle-desktop"]),
        new("隐藏桌面图标", "desktop.iconsHidden", false, ["--toggle-key", "icons"]),
        new("顶部菜单栏", "components.menubar", true, ["--toggle-key", "menubar"]),
        new("底部 Dock", "components.dock", true, ["--toggle-key", "dock"]),
        new("任务栏外观", "components.wintaskbar", true, ["--toggle-key", "taskbar"]),
        // 工具类常驻（2026-09-17 用户拍板纳入托盘控制面）。
        // 【语义边界 · 2026-09-17 审计纠偏】这两项与"剪贴板/灵动岛"不同：**看门狗刻意不守护它们**
        // （见 watchdog/Program.cs 的 BuildTargets 注释：索引/截图是"按需拉起 + 空闲即退"的工作型进程，
        // 若纳入守护会与空闲退场互相打架）。所以关掉的效果是：
        //   · 索引：门控"后续按需拉起"（IndexEngineLauncher），已在跑的实例空闲后自行退出；
        //   · 截图：由常驻 Agent 交还全局热键（CaptureHotkeyOwner），Agent 不在时无即时效果。
        // 这里如实标注，避免用户以为"关掉就立刻没了"。
        new("索引引擎（停止后续按需拉起）", "extensions.index.enabled", true, ["--toggle-key", "index"]),
        new("截图工具（需常驻服务在运行）", "extensions.screenshot.enabled", true, ["--toggle-key", "capture"]),
        // 【2026-09-17】托盘此前缺这一项：用户只能打开剪贴板面板，没有"关掉这个功能"的入口。
        // 关掉 → 宿主内 ClipboardPlugin 走 ApplyEnabledState(false)：面板/引擎进程退场，侧边「›」手柄一并消失。
        new("剪贴板历史", "extensions.clipboard-history.enabled", true, ["--toggle-key", "clipboard"]),
        // 【2026-09-17】灵动岛（宿主内插件，与扩展中心同键 island.enabled）：
        // 关掉 → IslandPlugin 订阅 island.* 变化即时拆窗+退订来源，无需看门狗。
        new("灵动岛", "island.enabled", true, ["--toggle-key", "island"]),
        // 【2026-09-17 补齐两处缺失入口】这两个开关在 DesktopToggleCatalog（CLI/宿主的共享表）里
        // 早已定义、CLI 与宿主都支持，但托盘「功能开关」一直没有 → 用户只能在设置中心改，属纯入口缺失。
        new("双击隐藏桌面图标", "desktop.doubleClickHideIcons", true, ["--toggle-key", "doubleclick"]),
        new("热键侧板", "hotkeys-panel.enabled", true, ["--toggle-key", "hotkey-panel"]),
    ];

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miOpen = new("打开设置中心");
    private readonly ToolStripMenuItem _miClipboardHistory = new("打开剪贴板历史");
    private readonly ToolStripMenuItem _miUpdate = new("检查更新…");
    private readonly ToolStripMenuItem _miInstallUpdate = new("下载并安装更新…");
    private readonly ToolStripMenuItem _miDesktopServiceStart = new("启动桌面服务（自绘桌面 / 桌面控制）");
    private readonly ToolStripMenuItem _miDesktopServiceStop = new("停止桌面服务（还原桌面图标）");
    private readonly ToolStripMenuItem _miRestart = new("重启主程序");
    private readonly ToolStripMenuItem _miHostStart = new("启动主程序（菜单栏 / Dock）");
    private readonly ToolStripMenuItem _miHostStop = new("停止主程序（保留常驻功能）");
    private readonly ToolStripMenuItem _miAutoStart = new("开机自启（随系统启动托盘）");
    private readonly ToolStripMenuItem _miLogs = new("打开日志目录");
    private readonly ToolStripMenuItem _miDiagBundle = new("导出诊断包（收集日志给开发者）…");
    private readonly ToolStripMenuItem _miRecovery = new("应急恢复（显示任务栏 / 清理残留）…");
    private readonly ToolStripMenuItem _miStatus = new("组件状态…");
    private readonly ToolStripMenuItem _miAbout = new("关于");
    private readonly ToolStripMenuItem _miExit = new("退出托盘");
    private readonly ToolStripMenuItem _miRestoreVisual = new("恢复系统外观（还原任务栏/桌面图标/自绘桌面）");
    private readonly Dictionary<ToggleSpec, ToolStripMenuItem> _toggleItems = [];

    // —— 系统集成（安装器级落地 2026-09-17）：把"我们装进系统里的状态"摆到用户面前 ——
    // 全部经 CLI `--system-integration` 与卸载脚本落地；托盘保持零包引用，
    // 不在这里重复实现注册逻辑（两套语义必然漂移）。
    private readonly ToolStripMenuItem _miSysStatus = new("查看注册状态…");
    private readonly ToolStripMenuItem _miSysRegister = new("注册到系统（右键扩展 + 开机自启）");
    private readonly ToolStripMenuItem _miSysRepair = new("修复注册（按状态补缺）");
    private readonly ToolStripMenuItem _miSysUnregister = new("注销系统右键扩展（保留程序与自启）");
    private readonly ToolStripMenuItem _miSysUninstall = new("卸载 BetterDesktop…");

    public TrayApplicationContext()
    {
        BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = $"BetterDesktop {ProductVersion()} · 桌面增强常驻托盘",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => Run(OpenShell, "打开设置中心");
        _menu.Opening += OnMenuOpening;
        TrayLog.Write($"托盘图标已注册（版本 {ProductVersion()}，目录 {AppPaths.BaseDir}）");
        EnsureDesktopServiceAutoStart();
        ShowFirstRunTrayHint();
    }

    /// <summary>
    /// 首次运行时提示托盘图标在哪。
    /// <para>
    /// 【为什么需要】Windows 11 默认把**新出现的**托盘图标折叠进任务栏的「^」溢出区，
    /// 用户看不到我们的图标会以为"程序没装上"（实测反馈："系统托盘里看不到托盘程序"）。
    /// 系统没有 API 能替用户把它固定到任务栏，只能引导一次：点「^」展开、把图标拖出来即可常驻。
    /// </para>
    /// <para>
    /// 【为什么用标记文件而不是设置键】托盘的纪律是"功能开关一律经 CLI 落地、自己不写 settings.json"，
    /// 而这是一次性 UI 提示标记（与 host-stopped 等同类），落在同一个数据目录即可。
    /// 标记写不上时**不提示**——避免每次启动都弹。
    /// </para>
    /// </summary>
    private void ShowFirstRunTrayHint()
    {
        var flag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterDesktop", "tray-icon-hint.flag");
        try
        {
            if (File.Exists(flag))
            {
                return;
            }

            var directory = Path.GetDirectoryName(flag);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(flag, DateTime.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TrayLog.Write("写首次提示标记失败（跳过提示）: " + ex.Message);
            return;
        }

        Notify("BetterDesktop 已在后台运行。若任务栏里看不到本图标，点任务栏的「^」展开，把图标拖到任务栏即可常驻。");
    }

    // ---------------- 菜单构建 ----------------

    private void BuildMenu()
    {
        Attach(_miOpen, OpenShell);
        _menu.Items.Add(_miOpen);

        // 「打开剪贴板历史」入口：CLI 与宿主自 2026-09-17 起就支持 `clipboard-history`，
        // 托盘却只有"开/关这个功能"、没有"打开面板"——用户只能靠屏幕侧边手柄或全局热键，
        // 属纯入口缺失（本次审计确认：托盘全部菜单项里零处出现 clipboard-history）。
        Attach(_miClipboardHistory, OpenClipboardHistory);
        _menu.Items.Add(_miClipboardHistory);

        // 托盘 = 程序中转站（2026-09-17 用户定调）：主程序（菜单栏/Dock 等重视觉）**按需启停**，
        // 停掉后常驻功能（设置窗 / 自绘桌面 / 剪贴板 / 常驻服务）继续可用，从而省下壳的内存。
        Attach(_miHostStart, StartHostProcess);
        _menu.Items.Add(_miHostStart);
        Attach(_miHostStop, StopHostProcess);
        _menu.Items.Add(_miHostStop);

        var togglesMenu = new ToolStripMenuItem("功能开关");
        foreach (var spec in Toggles)
        {
            var item = new ToolStripMenuItem(spec.Label);
            var captured = spec;
            item.Click += (_, _) => Run(() => ToggleFeature(captured), captured.Label);
            _toggleItems[spec] = item;
            togglesMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(togglesMenu);

        Attach(_miRestoreVisual, RestoreVisual);
        _menu.Items.Add(_miRestoreVisual);

        // 桌面服务（2026-09-17 独立化）：自绘桌面 + 桌面控制菜单 + 原生双击钩子都在它里面，
        // **不依赖主程序**——"桌面控制像剪贴板历史一样独立、主程序不启动也能用"的落点。
        var desktopServiceMenu = new ToolStripMenuItem("桌面服务（不依赖主程序）");
        Attach(_miDesktopServiceStart, StartDesktopService);
        desktopServiceMenu.DropDownItems.Add(_miDesktopServiceStart);
        Attach(_miDesktopServiceStop, StopDesktopService);
        desktopServiceMenu.DropDownItems.Add(_miDesktopServiceStop);
        _menu.Items.Add(desktopServiceMenu);

        // 【S4-4】此处原有「常驻服务（Agent）」与「看门狗」两个子菜单（含「暂停守护」勾选项），已删除：
        // Agent 与 Watchdog 退役、职责迁 core；"暂停监护"的新入口在 **core 的托盘菜单**
        //（写 `user-pause.flag`，与更新器写的 `watchdog-pause.flag` 分开 —— 见 core/src/supervisor.rs）。
        // 删旧世界的入口比留着更安全：留着就是"点了没反应"的死按钮。

        // 系统集成（2026-09-17 安装器级）：注册 / 修复 / 注销 / 状态 / 卸载。
        // 「把功能正式注册到系统」的用户可见出口 —— 与安装脚本、Agent 自愈共用同一份实现
        // （CLI --system-integration），所以三者永远说同一件事。
        var integrationMenu = new ToolStripMenuItem("系统集成（注册到系统）");
        Attach(_miSysStatus, ShowIntegrationStatus);
        integrationMenu.DropDownItems.Add(_miSysStatus);
        Attach(_miSysRegister, RegisterToSystem);
        integrationMenu.DropDownItems.Add(_miSysRegister);
        Attach(_miSysRepair, RepairRegistration);
        integrationMenu.DropDownItems.Add(_miSysRepair);
        integrationMenu.DropDownItems.Add(new ToolStripSeparator());
        Attach(_miSysUnregister, UnregisterShellMenu);
        integrationMenu.DropDownItems.Add(_miSysUnregister);
        Attach(_miSysUninstall, UninstallProduct);
        integrationMenu.DropDownItems.Add(_miSysUninstall);
        _menu.Items.Add(integrationMenu);

        _menu.Items.Add(new ToolStripSeparator());

        Attach(_miUpdate, CheckForUpdate);
        _menu.Items.Add(_miUpdate);
        Attach(_miInstallUpdate, InstallUpdate);
        _menu.Items.Add(_miInstallUpdate);
        Attach(_miRestart, RestartHost);
        _menu.Items.Add(_miRestart);

        _menu.Items.Add(new ToolStripSeparator());

        Attach(_miAutoStart, ToggleAutoStart);
        _menu.Items.Add(_miAutoStart);
        Attach(_miLogs, () => ProcessBridge.OpenPath(AppPaths.LogDir));
        _menu.Items.Add(_miLogs);

        // 导出诊断包：面向"把程序分发给他人实地测试"——出问题时用户点一下，
        // 日志 + 环境信息打包成 zip 落到桌面，直接回传即可，不需要指导其翻目录。
        Attach(_miDiagBundle, () =>
        {
            TrayLog.Write("导出诊断包：开始");
            TrayLog.Flush(); // 先把托盘自己排队的日志落盘，保证包里含最新记录
            var zip = BetterDesktop.Diagnostics.DiagnosticBundle.Export(null, "tray");
            Notify("诊断包已导出到桌面：" + Path.GetFileName(zip));
            ProcessBridge.OpenPath(Path.GetDirectoryName(zip) ?? AppPaths.LogDir);
        });
        _menu.Items.Add(_miDiagBundle);
        Attach(_miRecovery, RunRecovery);
        _menu.Items.Add(_miRecovery);

        _menu.Items.Add(new ToolStripSeparator());

        Attach(_miStatus, ShowStatus);
        _menu.Items.Add(_miStatus);
        Attach(_miAbout, ShowAbout);
        _menu.Items.Add(_miAbout);

        _menu.Items.Add(new ToolStripSeparator());

        Attach(_miExit, ExitTray);
        _menu.Items.Add(_miExit);
    }

    /// <summary>统一入口：菜单项回调绝不抛异常（常驻组件的铁律）。</summary>
    private static void Attach(ToolStripMenuItem item, Action action)
        => item.Click += (_, _) => Run(action, item.Text);

    private static void Run(Action action, string? what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TrayLog.Error($"菜单项「{what ?? "(未命名)"}」", ex);
        }
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        try
        {
            RefreshChecks();
            var hostRunning = ProcessBridge.IsHostRunning();

            // 【文案纠偏 2026-09-17】旧文案在宿主未运行时写"（将启动主程序）"，但 OpenShell 的实际行为是
            // **直接拉起独立设置进程 BetterDesktop.Settings.exe，不启动主程序**（见该方法注释与实现），
            // 二者语义相反会误导用户 → 统一为"打开设置中心"，不承诺启动任何东西。
            _miOpen.Text = "打开设置中心";

            _miHostStart.Enabled = !hostRunning && File.Exists(AppPaths.HostExe);
            _miHostStop.Enabled = hostRunning;
            _miRestart.Enabled = File.Exists(AppPaths.HostExe); // 此前从不置灰：主程序没部署也显示可点

            // 更新项：只判断 updater 存在还不够——缺 update.config.json 时它必然以"更新源不可达"失败
            // （退出码 3）。让用户白点一次再收到失败提示，不如直接置灰。
            var updaterReady = File.Exists(AppPaths.UpdaterExe);
            var updateSourceReady = File.Exists(AppPaths.UpdateConfigFile);
            _miUpdate.Enabled = updaterReady && updateSourceReady;
            _miInstallUpdate.Enabled = updaterReady && updateSourceReady;
            _miRecovery.Enabled = File.Exists(AppPaths.RecoveryExe);

            var desktopServiceRunning = ProcessBridge.IsDesktopServiceRunning();
            _miDesktopServiceStart.Enabled = !desktopServiceRunning && File.Exists(AppPaths.DesktopServiceExe);
            _miDesktopServiceStop.Enabled = desktopServiceRunning;

            // 系统集成：注册类动作全靠 CLI 实现（缺失就整组不可用，避免点了静默无效）
            var cliReady = File.Exists(AppPaths.CliExe);
            _miSysStatus.Enabled = cliReady;
            _miSysRegister.Enabled = cliReady;
            _miSysRepair.Enabled = cliReady;
            _miSysUnregister.Enabled = cliReady;
            _miSysUninstall.Enabled = File.Exists(AppPaths.UninstallScript);

            // 功能开关同样全靠 CLI 落地（--toggle-desktop / --toggle-key）：CLI 缺失时点了只会得到
            // 退出码 -1 且勾选不变，表现就是"可点但没效果"——与系统集成一致统一置灰。
            foreach (var (_, toggleItem) in _toggleItems)
            {
                toggleItem.Enabled = cliReady;
            }
        }
        catch (Exception ex)
        {
            TrayLog.Error("刷新菜单", ex);
        }
    }

    private void RefreshChecks()
    {
        foreach (var (spec, item) in _toggleItems)
        {
            item.Checked = SettingsBridge.GetBool(spec.Key, spec.Default);
        }

        _miAutoStart.Checked = AutoStart.IsEnabled();
    }

    // ---------------- 菜单行为 ----------------

    /// <summary>
    /// 打开设置中心：**直接拉起独立设置进程**（2026-09-17 用户定调"托盘=中转站，功能按需启动、省内存"）。
    /// 旧实现走 CLI `open-settings` → 命名管道 → **宿主** → 宿主进程内开窗，即"为了看一眼设置先把整个壳
    ///（≈125MB + 菜单栏/Dock/自绘桌面）拉起来"。现在设置中心自成一个进程：不依赖宿主、关窗即退、内存立刻归还。
    /// 老部署（未带该 exe）自动回退旧链路，功能不丢。
    /// </summary>
    private void OpenShell()
    {
        if (File.Exists(AppPaths.SettingsExe))
        {
            if (!ProcessBridge.StartDetached(AppPaths.SettingsExe))
            {
                Notify("设置中心启动失败（详见托盘日志）", ToolTipIcon.Warning);
            }

            return;
        }

        TrayLog.Write("独立设置进程未部署（同目录无 BetterDesktop.Settings.exe），回退旧链路");
        FallbackOpenSettingsViaHost();
    }

    /// <summary>
    /// 打开剪贴板历史面板。优先**直连面板进程**（<c>--open</c> 会唤醒已运行实例，不重复起进程）；
    /// 面板未部署时退回 CLI <c>clipboard-history</c>（由宿主拉起面板，宿主不在则 CLI 按需启动宿主）。
    /// <para>
    /// 【为什么必须有这条】托盘此前只有"开/关剪贴板历史"，没有"打开面板"的入口 ——
    /// 而 CLI 与宿主自 2026-09-17 起就支持该 action，属纯入口缺失（用户只能靠侧边手柄或全局热键）。
    /// </para>
    /// </summary>
    private void OpenClipboardHistory()
    {
        if (File.Exists(AppPaths.ClipboardPanelExe)
            && ProcessBridge.StartDetached(AppPaths.ClipboardPanelExe, "--open"))
        {
            return;
        }

        TrayLog.Write("面板独立进程未部署或启动失败，回退 CLI clipboard-history");
        if (SendCommand("clipboard-history"))
        {
            return;
        }

        Notify("打开剪贴板历史失败：面板未部署，且主程序链路不可用（详见托盘日志）", ToolTipIcon.Warning);
    }

    /// <summary>
    /// 旧链路（回退用，逐行保留）：宿主不在则先启动，**等宿主管道就绪后**才发 open-settings。
    /// 必须先等管道：CLI 在无宿主状态下对"需宿主动作"会弹原生对话框并阻塞约 10s（实测），
    /// 直接发命令会让用户看到一个莫名弹窗。
    /// </summary>
    private void FallbackOpenSettingsViaHost()
    {
        if (PipeProbe.IsMenuPipeUp())
        {
            SendCommand("open-settings");
            return;
        }

        var started = ProcessBridge.StartHost();
        if (!started && !ProcessBridge.IsHostRunning())
        {
            Notify("主程序未部署：同目录找不到 " + AppPaths.HostExe, ToolTipIcon.Warning);
            return;
        }

        Notify("主程序正在启动，就绪后自动打开设置中心…");
        Task.Run(() =>
        {
            if (PipeProbe.WaitForMenuPipe(30000))
            {
                SendCommand("open-settings");
            }
            else
            {
                TrayLog.Write("打开设置中心失败：宿主管道未就绪（主程序可能启动失败，见宿主日志）");
            }
        });
    }

    /// <summary>经 CLI 契约发命令（调用前应确保宿主管道已就绪）。返回是否送达。</summary>
    private static bool SendCommand(string action, int retries = 3)
    {
        for (var i = 0; i < retries; i++)
        {
            if (ProcessBridge.RunCli("--menu-cmd", action) == 0)
            {
                return true;
            }

            Thread.Sleep(500);
        }

        TrayLog.Write($"命令未送达（已重试 {retries} 次）: {action}");
        return false;
    }

    private void ToggleFeature(ToggleSpec spec)
    {
        var code = ProcessBridge.RunCli(spec.CliArgs);
        if (code != 0)
        {
            Notify($"切换「{spec.Label}」失败（退出码 {code}）", ToolTipIcon.Warning);
            return;
        }

        RefreshChecks();
        var now = SettingsBridge.GetBool(spec.Key, spec.Default);
        var state = now ? "开" : "关";
        if (ProcessBridge.IsHostRunning())
        {
            Notify($"「{spec.Label}」已切换为 {state}");
        }
        else
        {
            Notify($"「{spec.Label}」已写入设置（{state}），主程序启动后生效");
        }
    }

    /// <summary>
    /// 恢复系统外观（2026-09-16 L1 常驻化的手动还原入口）：一键关闭任务栏外观接管、
    /// 取消桌面图标隐藏、关闭自绘桌面，把系统外观还原为原生状态。
    /// 经 CLI 契约写设置（与主程序/Agent 共享同一 settings.json，Agent 监听后落地）。
    /// 说明：托盘「功能开关」常规项经 CLI 落地，「恢复系统外观」是批量还原动作，同样只写设置，
    /// 不直接碰窗口/注册表，与主程序状态不漂移。
    /// </summary>
    private void RestoreVisual()
    {
        var restored = new List<string>();
        if (SettingsBridge.GetBool("components.wintaskbar", true))
        {
            if (ProcessBridge.RunCli("--toggle-key", "taskbar") == 0)
            {
                restored.Add("任务栏外观");
            }
        }

        if (SettingsBridge.GetBool("desktop.iconsHidden", false))
        {
            if (ProcessBridge.RunCli("--toggle-key", "icons") == 0)
            {
                restored.Add("桌面图标");
            }
        }

        if (SettingsBridge.GetBool("components.desktop", true))
        {
            if (ProcessBridge.RunCli("--toggle-desktop") == 0)
            {
                restored.Add("自绘桌面");
            }
        }

        if (restored.Count > 0)
        {
            Notify("已恢复系统外观：" + string.Join(" / ", restored) + "（如需重新接管，在功能开关中重新打开）");
        }
        else if (!File.Exists(AppPaths.CliExe))
        {
            // 【文案纠偏 2026-09-17】RunCli 在 CLI 缺失时返回 -1，导致 restored 恒为空 ——
            // 旧逻辑会报"系统外观已是原生状态"，而真实原因是"命令根本没执行"。两者必须区分。
            TrayLog.Write("恢复系统外观失败：CLI 未部署 " + AppPaths.CliExe);
            Notify("无法恢复系统外观：CLI 未部署（" + Path.GetFileName(AppPaths.CliExe) + "）", ToolTipIcon.Warning);
        }
        else
        {
            Notify("系统外观已是原生状态，无需恢复");
        }

        RefreshChecks();
    }
    private void ToggleAutoStart()
    {
        var next = !AutoStart.IsEnabled();
        AutoStart.Set(next);
        RefreshChecks();
        Notify(next ? "已开启开机自启（随系统启动托盘）" : "已关闭开机自启");
    }

    /// <summary>检查更新：跑更新器 --check（后台），读它写出的状态文件再弹气泡（不阻塞菜单）。</summary>
    private void CheckForUpdate()
    {
        if (!File.Exists(AppPaths.UpdaterExe))
        {
            Notify("更新组件未部署：同目录找不到 " + Path.GetFileName(AppPaths.UpdaterExe), ToolTipIcon.Warning);
            return;
        }

        Notify("正在检查更新…");
        Task.Run(() =>
        {
            var code = ProcessBridge.RunTool(AppPaths.UpdaterExe, 120000, "--check", "--quiet");
            var message = UpdateStatusReader.LastMessage()
                          ?? $"检查结束（退出码 {code}）：未取得结果，详见更新日志";
            TrayLog.Write($"检查更新结束 code={code} msg={message}");
            Notify(message, code is 0 or 10 ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
    }

    /// <summary>
    /// 下载并安装：托盘负责编排"下载 → 退出主程序 → 替换 → 重启"。
    /// 由托盘（而不是更新器）主动停宿主，是因为宿主退出时要做图标/任务栏恢复，交回壳自己走正常退出更安全；
    /// 更新器内部仍会再等一次宿主退出，作为双保险。
    /// </summary>
    private void InstallUpdate()
    {
        if (!File.Exists(AppPaths.UpdaterExe))
        {
            Notify("更新组件未部署：同目录找不到 " + Path.GetFileName(AppPaths.UpdaterExe), ToolTipIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            "将要：① 下载并校验新版本；② 退出主程序；③ 替换文件；④ 自动重启。"
            + Environment.NewLine + Environment.NewLine + "继续？",
            "下载并安装更新",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        Notify("开始下载更新（完成后将自动退出主程序并替换）…");
        Task.Run(() =>
        {
            var download = ProcessBridge.RunTool(AppPaths.UpdaterExe, 600000, "--download", "--quiet");
            if (download != 11)
            {
                var fail = UpdateStatusReader.LastMessage() ?? $"下载失败（退出码 {download}）";
                TrayLog.Write($"更新下载未完成 code={download} msg={fail}");
                Notify(fail, ToolTipIcon.Warning);
                return;
            }

            ProcessBridge.StopHost();
            Thread.Sleep(1500);

            var apply = ProcessBridge.RunTool(AppPaths.UpdaterExe, 300000, "--apply", "--quiet");
            var message = UpdateStatusReader.LastMessage()
                          ?? (apply == 12 ? "更新完成并已重启主程序" : $"安装结束（退出码 {apply}）");
            TrayLog.Write($"更新安装结束 code={apply} msg={message}");
            Notify(message, apply == 12 ? ToolTipIcon.Info : ToolTipIcon.Error);
        });
    }

    /// <summary>按需启动主程序（中转站语义：用户要菜单栏/Dock 时才付这份内存）。</summary>
    private void StartHostProcess()
    {
        if (ProcessBridge.IsHostRunning())
        {
            Notify("主程序已在运行");
            return;
        }

        Notify(ProcessBridge.StartHost() ? "主程序已启动" : "主程序未部署或启动失败");
    }

    /// <summary>
    /// 按需停止主程序（保留常驻功能：设置窗 / 自绘桌面 / 剪贴板 / 常驻服务）。
    /// StopHost 会写看门狗豁免标记 → 壳不会被自动拉回；下次"启动主程序"清标记恢复守护。
    /// </summary>
    private void StopHostProcess()
    {
        if (!ProcessBridge.IsHostRunning())
        {
            Notify("主程序未在运行");
            return;
        }

        Notify(ProcessBridge.StopHost()
            ? "主程序已停止（常驻功能继续运行；看门狗不会自动拉回）"
            : "停止主程序失败，详见托盘日志");
    }

    private void RestartHost()
    {
        var stopped = ProcessBridge.StopHost();
        Thread.Sleep(1500);
        var started = ProcessBridge.StartHost();
        Notify(started
            ? (stopped ? "主程序已重启" : "主程序已启动")
            : "主程序未部署，无法启动",
            started ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // ---------------- 常驻服务（Agent）与看门狗：已随 S4-4 删除 ----------------
    //
    // 这里原为 StartAgentService / StopAgentService / StartWatchdog / StopWatchdog / ToggleGuardPause
    // 五个方法（以及上面那两个子菜单）。它们随 agent/ 与 watchdog/ 目录一起退役：
    //   · Agent 的三项能力 → core（截图热键 / 右键扩展自愈 / 桌面服务监护）；
    //   · Watchdog 的监护   → core 的 supervisor（含 stopFlag 与"管道判活"语义）；
    //   · 「暂停守护」       → core 托盘菜单的「暂停组件监护」（写 user-pause.flag）。
    //
    // 保留这段说明是为了让"这里**原来有东西**"可见 —— 否则下一个人会以为托盘从来不管这两件事。

    // ---------------- 桌面服务（自绘桌面 / 桌面控制，不依赖主程序） ----------------

    private void StartDesktopService()
    {
        if (!File.Exists(AppPaths.DesktopServiceExe))
        {
            Notify("桌面服务组件未部署：找不到 " + Path.GetFileName(AppPaths.DesktopServiceExe), ToolTipIcon.Warning);
            return;
        }

        if (ProcessBridge.IsDesktopServiceRunning())
        {
            Notify("桌面服务已在运行");
            return;
        }

        if (!ProcessBridge.StartDesktopService())
        {
            Notify("桌面服务启动失败，详见托盘日志", ToolTipIcon.Warning);
            return;
        }

        TrayLog.Write("已启动桌面服务（自绘桌面 / 桌面控制）");
        Notify("桌面服务已启动：自绘桌面与「桌面控制」不再依赖主程序");
    }

    private void StopDesktopService()
    {
        if (!ProcessBridge.IsDesktopServiceRunning())
        {
            Notify("桌面服务未在运行");
            return;
        }

        Notify("正在停止桌面服务（会还原桌面图标与任务栏）…");
        Task.Run(() =>
        {
            var ok = ProcessBridge.StopDesktopService();
            Notify(ok ? "桌面服务已停止（系统外观已还原）" : "桌面服务停止失败，详见托盘日志",
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
    }

    private void RunRecovery()
    {
        if (!ProcessBridge.StartDetached(AppPaths.RecoveryExe))
        {
            Notify("应急恢复程序未部署：" + Path.GetFileName(AppPaths.RecoveryExe), ToolTipIcon.Warning);
        }
    }

    // ---------------- 系统集成（2026-09-17 安装器级） ----------------

    /// <summary>
    /// 查看注册状态：CLI 采集（安装根 / B 路扩展 / A 路稀疏包 / 三项自启 / 快照），原样展示。
    /// 直接展示 CLI 输出而不是托盘自己拼文案 —— 状态文案只有一份（SystemIntegrationRegistrar.Describe）。
    /// </summary>
    private void ShowIntegrationStatus()
    {
        var (code, output) = ProcessBridge.RunCliCapture("--system-integration", "status");
        var body = string.IsNullOrWhiteSpace(output)
            ? $"未能取得状态（退出码 {code}）。CLI 诊断日志：%TEMP%\\bdt-cli.log"
            : output.Trim();
        MessageBox.Show(body, "BetterDesktop 系统集成状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RegisterToSystem() => RunIntegrationAction("注册到系统", "--system-integration", "register");

    private void RepairRegistration() => RunIntegrationAction("修复注册", "--system-integration", "repair");

    private void UnregisterShellMenu()
    {
        var confirm = MessageBox.Show(
            "注销系统右键扩展？" + Environment.NewLine + Environment.NewLine
            + "系统右键菜单里不再出现「桌面控制 / 格式转换」；程序与开机自启保留，随时可以再注册。",
            "BetterDesktop",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        RunIntegrationAction("注销系统右键扩展", "--system-integration", "unregister");
    }

    /// <summary>统一执行注册类动作：拿退出码 + 输出，成功/失败都如实告诉用户（失败写日志，不静默）。</summary>
    private void RunIntegrationAction(string what, params string[] args)
    {
        var (code, output) = ProcessBridge.RunCliCapture(args);
        var ok = code == 0;

        // B 路是**进程内 shellex 处理程序**，explorer 启动时枚举并缓存 —— 注册/注销后不重启 explorer
        // 就不会在右键菜单里体现。以前这里只弹"完成"，用户等了半天看不到菜单项，体感就是"没落实"。
        Notify(
            ok ? $"{what}：完成（右键菜单需重启资源管理器后生效）" : $"{what}：失败（退出码 {code}）",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);

        if (!ok)
        {
            TrayLog.Write($"[系统集成] {what} 失败（退出码 {code}）：{output.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}");
        }
    }

    private void UninstallProduct()
    {
        if (!File.Exists(AppPaths.UninstallScript))
        {
            Notify("未找到卸载脚本（本次构建未随包分发）：" + Path.GetFileName(AppPaths.UninstallScript), ToolTipIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "卸载 BetterDesktop？" + Environment.NewLine + Environment.NewLine
            + "将注销右键扩展与系统菜单包、清除开机自启、还原任务栏与桌面图标，并删除程序文件。"
            + "用户设置与剪贴板数据默认保留。" + Environment.NewLine + Environment.NewLine
            + "过程中桌面与任务栏会闪一下（explorer 重启），属正常现象。",
            "卸载 BetterDesktop",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        if (!ProcessBridge.StartPowerShellScript(AppPaths.UninstallScript))
        {
            Notify("卸载脚本启动失败（详见托盘日志）", ToolTipIcon.Warning);
            return;
        }

        // 脚本第一步会停掉托盘自身；这里主动退出，避免托盘占着安装目录里的文件名导致删不掉。
        TrayLog.Write("已拉起卸载脚本，托盘退出");
        ExitTray();
    }

    private void ShowStatus()
    {
        var lines = new List<string>
        {
            $"托盘：运行中（{ProductVersion()}）",
            $"主程序（Host）：{(ProcessBridge.IsHostRunning() ? "运行中" : "未运行")}",
            $"桌面服务（自绘桌面 / 桌面控制）：{(ProcessBridge.IsDesktopServiceRunning() ? "运行中（不依赖主程序）" : "未运行")}",
            $"更新组件：{(File.Exists(AppPaths.UpdaterExe) ? "已部署" : "未部署")}",
            string.Empty,
            "功能开关：",
        };

        foreach (var spec in Toggles)
        {
            lines.Add($"    {spec.Label}：{(SettingsBridge.GetBool(spec.Key, spec.Default) ? "开" : "关")}");
        }

        lines.Add(string.Empty);
        lines.Add("设置文件：" + AppPaths.SettingsFile);
        lines.Add("日志目录：" + AppPaths.LogDir);

        MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "BetterDesktop 组件状态",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"BetterDesktop {ProductVersion()}{Environment.NewLine}{Environment.NewLine}"
            + $"组件目录：{AppPaths.BaseDir}{Environment.NewLine}"
            + $"托盘为常驻组件：主程序退出后仍可在此开关功能、查看状态、拉起主程序。",
            "关于 BetterDesktop",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitTray()
    {
        TrayLog.Write("用户请求退出托盘");
        Notify("托盘已退出（主程序与各功能不受影响；如需恢复用应急恢复程序）");
        _notifyIcon.Visible = false;
        ExitThread();
    }

    /// <summary>
    /// 桌面服务随托盘自启（2026-09-17 桌面控制独立化）：托盘是开机自启的常驻控制面，
    /// 由它把「自绘桌面 / 桌面控制」带起来 → **主程序不启动也能用**（本次需求的原话目标）。
    /// 用户显式「停止桌面服务」后（desktop-stopped.flag 存在）不复活；
    /// 下次手动启动（会清标记）或主程序启动时的 EnsureDesktopServiceRunning 兜底。
    /// </summary>
    private void EnsureDesktopServiceAutoStart()
    {
        try
        {
            var flag = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop", "desktop-stopped.flag");
            if (File.Exists(flag))
            {
                TrayLog.Write("存在 desktop-stopped.flag（用户显式停止），托盘不自动启动桌面服务");
                return;
            }

            if (ProcessBridge.IsDesktopServiceRunning())
            {
                return;
            }

            if (!File.Exists(AppPaths.DesktopServiceExe))
            {
                TrayLog.Write("桌面服务未部署，托盘不自动启动（同目录与 %LOCALAPPDATA% 都没有）");
                return;
            }

            ProcessBridge.StartDesktopService();
        }
        catch (Exception ex)
        {
            TrayLog.Error("桌面服务随托盘自启", ex);
        }
    }

    // ---------------- 基础设施 ----------------

    private void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = "BetterDesktop";
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex)
        {
            TrayLog.Error("气泡通知", ex);
        }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex)
        {
            TrayLog.Error("加载托盘图标", ex);
        }

        return SystemIcons.Application;
    }

    private static string ProductVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                return informational;
            }

            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch (Exception ex)
            {
                TrayLog.Error("释放托盘图标", ex);
            }

            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}
