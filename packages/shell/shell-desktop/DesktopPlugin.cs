// BetterDesktop.Shell.Desktop — 自绘桌面插件入口
// 装配：DesktopBrowser（Provide 给菜单栏左区/工具条）+ DesktopWindow（透明"文件显示器"，壁纸归 explorer）。
// 启用时隐藏 explorer 原桌面图标（SetNativeIconsVisible：ShowWindow(SysListView32)），退出/卸载时还原（用户环境不可破坏）。
//
// 【组件开关：即时生效】
//   设置中心「桌面」分区的 components.desktop 开关**即时生效**：
//   订阅 ISettingsService.Changed，开→StartDesktop()（建窗口+隐藏原生图标），
//   关→StopDesktop()（关窗口+恢复原生图标），无需重启。
//
// 【退出兜底机制】
//   UnloadAsync 只在插件正常卸载时触发；进程被 kill / 崩溃 / 强制退出时不走这里 → explorer 图标残留隐藏。
//   兜底两层：
//     1) 正常退出：Application.Current.Exit 恢复（Application.Shutdown 路径）。
//     2) 进程退出：AppDomain.ProcessExit 恢复（进程被结束时兜底；幂等防双触发）。
//   恢复为无条件 SW_SHOW：壳退出后用户桌面必须回到 explorer 默认可见态（环境不可破坏），
//   多个钩子先后触发重复调用无副作用。
//
// 【双击桌面空白 → 切换图标显隐（desktop.iconsHidden，双模式）】
//   自绘模式：DesktopWindow 窗口级双击 → 只藏自绘网格（原生层仍由本插件恒隐藏，两层零交叉）。
//   原生模式（components.desktop=false）：无自绘窗口可接事件，由本插件的 WH_MOUSE_LL 钩子感知
//   "双击落在原生桌面上"→ 以【实际可见性】为基准切换 desktop.iconsHidden → Changed 幂等落到
//   explorer 图标层。钩子按 WindowFromPoint 类名过滤，自绘模式天然不命中（两层互不打架）。
//
// 【重要：不再使用 ShellHelper.ToggleDesktopIcons（翻转语义）】
//   ManagedShell 的 ToggleDesktopIcons(bool) 实测为**翻转**（每次调用切换显示/隐藏，
//   bool 参数不改变其行为），且其内部窗口查找只认 Progman 直子的 SHELLDLL_DefView——
//   壁纸引擎（Wallpaper Engine）等会把 DefView 移到 WorkerW 下，此时隐藏/恢复都会静默失效
//   （曾导致"关闭自绘桌面后 explorer 图标不恢复"）。
//   现改为自实现**幂等**的显隐控制，查找链兼容 Progman 直子与 WorkerW 变体。
//
// 【已证伪存档 · 2026-09-11 一/二轮结论：下列归因均被四轮差分实证推翻，勿再据其决策】
//   旧结论 A：ShowWindow(SysListView32/DefView) 会触发 comctl32 渲染路径崩溃 → 弃用 ShowWindow。
//   旧结论 B：SendMessageTimeout(DefView, WM_COMMAND, 0x7402) 会崩溃 → 弃用 0x7402。
//             （"不用 0x7402"仍然成立，但理由不同：它是"翻转"语义、不幂等，与崩溃无关。）
//   旧结论 C：正确做法 = SetParent 摘除法（隐藏时 SetParent(NULL) + SW_HIDE）。
//   旧结论 D：comctl32 0x71f2d 是"ListView 图标隐藏路径固有 bug，宿主只是放大器"，
//             并据此加了两层补偿：摘除/挂回延迟 250ms、先 SW_HIDE 再 SetParent。
//   ⚠️ 为什么这些归因是错的：它们都缺对照实验。四轮差分实证把真凶定位为**跨进程 LVM_HITTEST**
//   （见下方「四轮修复」块，附可复现命令）。真凶一直潜伏在"每次左键按下"的路径里，
//   所以无论换成哪个原语都照样崩——于是被依次误判为 ShowWindow / 0x7402 / SetParent 的问题。
//   ⚠️ 最值得警惕的证据：**同一批"当日 9 次崩溃"在本文件里被归因于 ShowWindow/0x7402，
//   同时被 shell-taskbar/TaskbarAppearanceEngine.cs 归因于"跨进程 COM 轰炸"——两处互相矛盾。**
//   ⇒ 纪律：**因果结论/禁令必须附可复现方法（命令 + 期望现象），否则一律写"疑似"**；
//      已证伪的结论要显式标注，不能以"实证/根治/正确做法"的口吻留在文件里。
//
//   【2026-09-11 三轮修复 · 对照"草特码工具"钩子过程（FUNC 0x13B860）的实证纪律】
//   逆向复核发现：首版"对齐草特码工具 SetParent 摘除法"是**误证**——hwndparentold_tmp /
//   styleold_tmp 是 SetPropW/GetPropW/RemovePropW 的**窗口属性名**（任务栏重挂、壁纸 WebView2
//   挂 Progman 用的"可回滚重挂"助手 0x3E308/0x3E270），CTM 从不碰 explorer 的 SysListView32。
//   但 CTM 的钩子过程给出了三条硬纪律，本文件按此整改（三条均可独立回滚）：
//     ① nCode/msg 先判后做、零封送：回调内不再查设置、不再整结构体反射封送。原实现每个
//        鼠标事件（含 WM_MOUSEMOVE，全局钩子收系统全部鼠标输入）都执行 Marshal.PtrToStructure
//        与一次 settings.Get，而 LL 钩子回调阻塞全局输入、超时会被系统**静默摘钩**
//        → 这是"双击时灵时不灵/鼠标发涩"的直接来源。现只按需读 MSLLHOOKSTRUCT 前 8 字节 POINT。
//     ② 回调内不做窗口操作：EnsureHiddenIntentApplied（跨进程 FindWindowEx+GetParent）原挂在
//        每次左键按下，改为 2s DispatcherTimer（仅"原生模式 + 有隐藏意图"时运行）。
//     ③ 可吞掉触发点击：判定为桌面空白双击后返回 1（吞 DOWN2 及配对的 UP），explorer 不再
//        处理本次双击（不选中/不重绘）→ 从源头消除"两方同时操作同一 ListView"的竞争
//        （0x71f2d 的成因）。
//   配套：钩子改为随"原生模式 + desktop.doubleClickHideIcons"装/卸（自绘模式不再常驻空转、不再
//   白白参与全局输入链）。
//   ⚠️ 上述第 ③ 条（吞点击 + desktop.swallowToggleClick 开关）与"LVM_HITTEST 改
//   SendMessageTimeout(100ms)"**已在四轮修复中整体删除**：竞争假设与 LVM_HITTEST 本身就是
//   崩溃源，不是解法。此段仅作历史存档。
//
//   【2026-09-11 四轮修复 · 崩溃真凶实证（差分对照 + 事件日志时间线）】
//   结论：explorer 崩溃（comctl32.dll 0xc0000005 @0x71f2d）的**唯一真凶是跨进程 LVM_HITTEST**，
//   与隐藏原语、吞点击、执行线程都无关。三组实证（同一台机器、同一天）：
//     ① 独立探针自监听（纯类名判定 + ShowWindow(SW_HIDE)，点击路径**零跨进程消息**）：
//        用户手动双击桌面 16 次 → 16 次成功切换，explorer 崩溃 **0** 次；
//     ② 同一时段启动本宿主（钩子已装）后，桌面双击 3 次 → 事件日志 3 次同一签名崩溃；
//     ③ 纯净对照：仅对桌面 SysListView32 发 **1 次** LVM_HITTEST → explorer 立即崩溃
//        （探针记录 hittest-crash afterCalls=1；事件日志同一签名）。
//   ⇒ 据此本文件整改（全部是"删掉为旧方案兜底的补偿"，不是新增机制）：
//     · 钩子回调内**彻底移除任何跨进程窗口消息**：LVM_HITTEST/SendMessageTimeout 全删，
//       桌面判定只按 WindowFromPoint 类名白名单（与零崩溃实证配方一致）；
//     · 隐藏/显示回归 **ShowWindow(SysListView32)**（探针 16 连击实证零崩溃）。
//       此前"ShowWindow / 0x7402 会崩"是**误判**——那些旧构建在每次左键按下都发 LVM_HITTEST，
//       崩溃记在了原语头上；0x7402 仍然不用（它的语义是翻转，不是幂等）；
//     · SetParent 摘除法、吞点击(swallowToggleClick)、Task 延迟 250ms + 线程池执行、
//       来源去重窗口(ToggleSourceGuardMs) **全部删除**；
//     · 可见性判据随原语同步改为 **IsWindowVisible(SysListView32)**（原 GetParent 判据是摘除法的
//       产物，摘除法退场后会恒判"可见"）；
//     · "双击落在图标上不切换"的命中测试**整体取消**：参照物「草特码工具」的钩子同样只按类名
//       白名单判定、无命中测试（双击图标也会切换），本实现与之保持一致。若将来要恢复该语义，
//       必须用消息自由的方式（IFolderView::ItemFromPoint / OLEACC）且在 Dispatcher 线程调用，
//       不可再用 LVM_HITTEST。可复现验证：`DesktopIconProbe --op=hittest --count=1`。

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Sections;

using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Desktop.Windows;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop;

// ============================================================
// 【白话导航 · 自绘桌面域】凭白话需求定位到精确文件：
//   "桌面图标排列/刷新/拖拽/重命名/选中" → Controls/DesktopIconsControl.cs（图标画布，最大文件）
//   "桌面窗口本体（嵌入 explorer、吞消息）" → Windows/DesktopWindow.cs
//   "双击图标打开文件夹（自绘文件管理器）" → Services/DesktopBrowser.cs（IDesktopBrowser）+ Windows/FolderBrowserWindow.cs
//   "桌面右键菜单"                      → 本域 Controls/DesktopIconsControl.ShowMenu 统一自绘（DesktopMenuPopup）；跨进程委托已 2026-09-10 移除
//   "桌面右键新建文件模板"              → Services/NewFileTemplates.cs
//   "隐藏/恢复 explorer 原生桌面图标"    → 本文件（SetNativeIconsVisible + 退出兜底）
//   "系统项显示名/卸载信息/命名空间解析"  → Services/DesktopBrowser.cs、Services/UninstallResolver.cs（图标见 shell-core `Surface/ShellItemIcon.cs`）
//   "桌面设置分区"                      → Sections/DesktopSection.cs
// ============================================================

// ── 本文件方法级白话索引（桌面插件生命周期 + 原生图标接管，白话 → 方法）──
//   "插件加载/卸载、桌面启停"        → LoadAsync / UnloadAsync / StartDesktop / StopDesktop / ApplyEnabledState；设置变更 OnSettingsChanged
//   "退出时恢复 explorer 原生图标（多重保险）" → RegisterExitRestoreHooks / OnExitRestoreIcons / OnProcessExitRestoreIcons / SpawnIconRestoreSentinel / RestoreIcons
//   "原生桌面图标显隐与窗口查找"      → SetNativeIconsVisible / AreNativeIconsVisible / FindDesktopListView / FindDesktopDefView
//   "桌面空白双击切换图标（低级鼠标钩子）" → InstallDesktopDoubleClickHook / RemoveDesktopDoubleClickHook / OnMouseHookProc / IsOverNativeDesktop / ToggleNativeIconsByDoubleClick
//   桌面窗口本体在 Windows/DesktopWindow.cs，图标画布在 Controls/DesktopIconsControl.cs。
// ────────────────────────────────────

/// <summary>自绘桌面插件（shell.desktop）。</summary>
public sealed class DesktopPlugin : IPlugin
{
    public string Name => "shell.desktop";

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private IContext? _context;
    private ISettingsService? _settings;
    private DesktopBrowser? _browser;
    private DesktopWindow? _window;
    private bool _running;

    // 双击落点（屏幕坐标）：仅用于日志/诊断（可定位"哪次双击触发了切换"）。
    // 钩子回调内不做任何命中测试（跨进程消息 1 次即打崩 explorer，见文件头实证）。
    private NativeMethods.POINT _pendingTogglePoint;

    // WH_MOUSE_LL：原生桌面模式的双击监听（自绘模式由 DesktopWindow 自行处理）
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc; // 持有委托引用，防被 GC 回收导致回调失效
    // 【2026-09-11】自判定双击状态（不依赖 WM_LBUTTONDBLCLK，见块注释）
    private long _lastDownTicks;
    private NativeMethods.POINT _lastDownPt;
    private bool _doubleClickArmed;
    // 【2026-09-11】切换冷却：切换后 ToggleCooldownMs 内不再触发（用户实测一次操作触发 5 次
    // ShowWindow 切换 → 桌面图标疯狂闪烁/延迟。冷却拦下同一序列的连环按下）。
    private long _lastToggleTicks;

    // ── 【钩子热路径缓存】LL 钩子回调里禁止查设置/封送结构体：回调阻塞全局鼠标输入，
    //   超时会被系统静默摘钩。两个开关值一律缓存，仅在设置变更时刷新。
    private bool _desktopEnabledCached = true;      // components.desktop
    private bool _doubleClickHideCached = true;     // desktop.doubleClickHideIcons
    // 隐藏意图兜底定时器（原挂在钩子回调的每次左键按下；现仅"原生模式 + 有隐藏意图"时 2s 轮询）。
    private System.Windows.Threading.DispatcherTimer? _intentTimer;

    /// <summary>桌面组件开关（设置中心「桌面」分区里的 components.desktop，默认启用）。</summary>
    private bool IsDesktopEnabled => _settings?.Get("components.desktop", true) ?? true;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _settings = context.Get<ISettingsService>();

        // 钩子热路径缓存（回调内禁止查设置，见字段区注释）
        _desktopEnabledCached = IsDesktopEnabled;
        _doubleClickHideCached = _settings?.Get("desktop.doubleClickHideIcons", true) ?? true;

        // 系统右键菜单贡献（2026-09-11）：全部收敛到 ApplyShellMenuRegistration——
        // 每一项都由设置键 shellmenu.* 控制（设置中心「右键菜单」分区），关闭即注销、打开即注册。
        ApplyShellMenuRegistration();

        // 设置分区（侧栏「桌面」）：无论桌面开关如何都注册，保证用户能在设置里重新开启。
        try
        {
            var registry = context.Get<ISettingsSectionRegistry>();
            if (registry is null)
            {
                DiagnosticLog.Trace("shell.desktop", "设置分区注册跳过：ISettingsSectionRegistry 未注册");
            }
            else
            {
                registry.Register(new DesktopSection());
                // 【2026-09-11 撤回】曾在此注册 ShellMenuSection（Title=「右键菜单」）→ 依 ISettingsSectionRegistry
                // 的"标题重复时后注册覆盖"规则，它把 shell-context-menu 的 MenuManagerSection（真正的「右键菜单」页）
                // 整个顶掉了（用户实测"新界面挤占原界面"）。现改为：那 4 个注册开关**并入** MenuManagerSection，
                // 本插件只管 shellmenu.* 的落地（ApplyShellMenuRegistration），不再注册分区。
                DiagnosticLog.Trace("shell.desktop", $"已注册设置分区「桌面」（当前分区数={registry.Sections.Count}）");
            }
        }
        catch (Exception ex)
        {
            // 分区注册失败不阻断（M10）
            DiagnosticLog.Trace("shell.desktop", $"设置分区注册失败：{ex.Message}");
        }

        // 订阅开关变更 → 即时启停（违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期）。
        if (_settings is not null)
        {
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                }));
        }

        // 退出兜底无条件注册（2026-09-06 修复）：自绘/原生两种模式都可能留下隐藏态
        //（原生模式 iconsHidden=true 也会隐藏 explorer 图标层），退出必须恢复，环境不可破坏。
        // 先反注册再注册：插件可能多次 Load（HMR），幂等。
        RegisterExitRestoreHooks();

        // 双击桌面空白切换图标显隐（desktop.iconsHidden）：
        //   自绘模式 → DesktopWindow 窗口级双击；原生模式 → 本钩子感知（窗口不存在，无别的感知手段）。
        //   【2026-09-11 三轮修复】不再"常驻安装"——按"原生模式 + desktop.doubleClickHideIcons"
        //   决定装/卸（自绘模式装了也全被首行放行，白白参与全局输入链；CTM 同款纪律：装/卸收口
        //   在唯一的配置应用点）。
        ApplyDoubleClickHookState();

        if (IsDesktopEnabled)
        {
            StartDesktop();
        }
        else
        {
            context.Logger.Info($"{Name} 已跳过：components.desktop=false（使用 explorer 原生桌面）");

            // 【2026-09-11 五轮修复】启动时**双向**应用用户意图（原来只处理"隐藏"这一半）：
            //   只做"隐藏意图恢复"会造成单向残留——上一次会话若是被强杀（退出兜底/哨兵都没跑到，
            //   实测 Stop-Process 即如此），explorer 图标会停在隐藏态；而设置里写的是"可见"，
            //   启动却不去纠正 → 用户看到空桌面（本次实测 lvVisible=0、设置 iconsHidden=false 的矛盾）。
            // ShowWindow 幂等，两个方向都安全。
            var iconsHidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
            SetNativeIconsVisible(!iconsHidden);
            context.Logger.Info($"原生桌面：启动即应用用户意图 iconsHidden={iconsHidden} → {(iconsHidden ? "隐藏" : "显示")}图标");
            if (iconsHidden)
            {
                SpawnIconRestoreSentinel();
            }
        }

        // 隐藏意图兜底（explorer 崩溃重启后补摘除）改为定时轮询，仅需要时运行
        ApplyIntentWatchState();

        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        RemoveDesktopDoubleClickHook();
        StopIntentWatch();
        StopDesktop();
        _browser = null;
        return Task.CompletedTask;
    }

    /// <summary>设置变更：components.desktop 开关即时启停；desktop.iconsHidden 在原生模式落到 explorer 图标层。</summary>
    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        // 【2026-09-11】系统右键菜单全部由设置开关控制（设置中心「右键菜单」分区），
        // 任一项开关变更即重算全部注册（幂等）——含"菜单栏/Dock 存在与否"这两个条件项。
        //
        // 【2026-09-18 必须订阅开关自身的键】「桌面控制」子菜单里的每个开关，其**勾选态是写进快照的静态值**
        // （原生菜单点击后不会回写）。用户在右键里点了开关 → CLI 写 settings.json → 若这里不订阅该键，
        // 快照就不会重写 → 下次右键看到的勾选态还是旧的（表现为"点了没反应"）。
        // 故这里把开关组用到的键全部纳入。
        if (e.Key.StartsWith("shellmenu.", StringComparison.Ordinal)
            || e.Key is "components.menubar" or "components.dock" or "components.desktop"
                or "components.wintaskbar" or "desktop.iconsHidden" or "desktop.doubleClickHideIcons"
                or "extensions.clipboard-history.enabled" or "hotkeys-panel.enabled")
        {
            ApplyShellMenuRegistration();
        }

        if (e.Key == "components.desktop")
        {
            _desktopEnabledCached = IsDesktopEnabled; // 热路径缓存同步（钩子回调不再查设置）
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyEnabledState();
                    ApplyDoubleClickHookState();
                }));
                return;
            }

            ApplyEnabledState();
            ApplyDoubleClickHookState();
            return;
        }

        // 双击隐藏开关：刷新缓存 + 重算钩子装/卸（关掉即卸钩，不再参与全局输入链）
        if (e.Key == "desktop.doubleClickHideIcons")
        {
            _doubleClickHideCached = _settings?.Get("desktop.doubleClickHideIcons", true) ?? true;
            ApplyDoubleClickHookState();
            return;
        }

        if (e.Key == "desktop.iconsHidden")
        {
            // 意图变化 → 同步兜底轮询启停（幂等；与下面的立即应用逻辑无关，也不受来源去重影响）
            ApplyIntentWatchState();

            // 自绘模式：意图由 DesktopWindow 自行应用（藏的是自绘网格），这里不插手——两层零交叉。
            if (_running)
            {
                return;
            }

            // 原生桌面模式：用户意图直接作用于 explorer 图标层（幂等 ShowWindow）。
            // 【2026-09-11 四轮修复】原"来源去重窗口(ToggleSourceGuardMs)"随延迟任务一并删除：
            // 应用现在是同步幂等的 ShowWindow，双击路径与设置界面路径都不需要去重。
            // 自绘模式由 DesktopWindow 自行应用（藏的是自绘网格），这里不插手——两层零交叉。
            var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => SetNativeIconsVisible(!hidden)));
                return;
            }

            SetNativeIconsVisible(!hidden);
        }
    }

    private void ApplyEnabledState()
    {
        if (IsDesktopEnabled)
        {
            StartDesktop();
            return;
        }

        StopDesktop();
        // 运行时切回原生桌面：若用户此前隐藏了图标，把意图落到原生层。
        // （正常退出路径不走这里——退出必须无条件恢复 explorer 默认可见，环境不可破坏。）
        if (_settings?.Get("desktop.iconsHidden", false) ?? false)
        {
            SetNativeIconsVisible(false);
        }
    }

    /// <summary>启动自绘桌面：创建桌面窗口 + 隐藏 explorer 原生图标 + 注册退出兜底。</summary>
    private void StartDesktop()
    {
        if (_running || _context is null)
        {
            return;
        }

        try
        {
            // 步骤日志：12:47 会话曾冻结在创建/显示窗口阶段无从定位，每步落点。
            DiagnosticLog.Trace("shell.desktop", "启动：创建 DesktopWindow");
            _browser ??= new DesktopBrowser();
            _context.Provide<IDesktopBrowser>(_browser);

            var vibrancy = _context.Get<IVibrancyService>() ?? NullVibrancyService.Instance;
            var appearance = _context.Get<IAppearanceService>();
            var convertMenu = _context.Get<BetterDesktop.Shell.Convert.Contracts.IConvertMenuService>();
            var archiveService = _context.Get<BetterDesktop.Shell.Convert.Contracts.IArchiveService>();
            // 剪贴板服务延迟解析：插件加载并行，desktop 窗口创建时 clipboard-history 可能尚未注册
            //（Get 返回 null）→ 传 Func，右键菜单构建时（用户操作时）必然已注册。
            _window = new DesktopWindow(_browser, vibrancy, appearance, _settings, convertMenu, archiveService, _context?.Events,
                () => _context?.Get<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService>());
            _window.Show();
            DiagnosticLog.Trace("shell.desktop", "启动：窗口已显示，隐藏原生图标");

            // 幂等隐藏：NativeMethods.ShowWindow(SW_HIDE) 重复调用无副作用，无翻转语义的时序坑。
            // （仅隐 SysListView32 图标层；DefView 保持可见以承载自绘层。）
            SetNativeIconsVisible(false);
            // 哨兵进程：宿主进程死亡（含强杀/冻结被结束任务等不触发 Exit/ProcessExit 的路径）
            // 即恢复 explorer 图标层——退出恢复的最后一道兜底。
            SpawnIconRestoreSentinel();

            _running = true;
            _context?.Logger.Info($"{Name} 已启动：透明文件显示器已嵌入桌面（壁纸归 explorer），explorer 桌面图标已隐藏（含退出兜底）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"启动自绘桌面失败：{ex.Message}");
        }
    }

    /// <summary>停止自绘桌面：关闭窗口 + 恢复 explorer 原生图标 + 移除退出兜底。</summary>
    private void StopDesktop()
    {
        if (!_running)
        {
            return;
        }

        // 退出兜底钩子保持订阅不摘除（恢复在退出时无条件执行，幂等无害；摘除反而会让
        // "运行过自绘桌面后关闭组件再退出"的场景失去兜底）。
        RestoreIcons();
        _window?.Close();
        _window = null;
        _running = false;
        _context?.Logger.Info($"{Name} 已停止：已恢复 explorer 原生桌面");
    }

    /// <summary>Application.Exit：恢复 explorer 桌面图标（幂等）。</summary>
    private void OnExitRestoreIcons(object? sender, EventArgs e)
    {
        DiagnosticLog.Trace("shell.desktop", "Application.Exit → 恢复 explorer 图标层");
        RestoreIcons();
    }

    /// <summary>AppDomain.ProcessExit：恢复 explorer 桌面图标（幂等，防双触发）。</summary>
    private void OnProcessExitRestoreIcons(object? sender, EventArgs e)
    {
        DiagnosticLog.Trace("shell.desktop", "ProcessExit → 恢复 explorer 图标层");
        RestoreIcons();
    }

    /// <summary>注册退出兜底（Application.Exit + AppDomain.ProcessExit，幂等）。</summary>
    private void RegisterExitRestoreHooks()
    {
        try
        {
            Application.Current.Exit -= OnExitRestoreIcons;
            Application.Current.Exit += OnExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExitRestoreIcons;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"退出兜底注册失败：{ex.Message}");
        }
    }

    /// <summary>拉起图标恢复哨兵：宿主 exe 自身以 --icon-restore-sentinel &lt;pid&gt; 运行，
    /// 等待本进程死亡后恢复 explorer 图标层。覆盖 TerminateProcess/强杀/冻结被结束任务等
    /// 不触发任何托管退出事件的死亡路径（2026-09-06 真机实证：会话冻结后被结束任务，
    /// Exit/ProcessExit 均未执行，图标层残留隐藏、桌面右键失效）。</summary>
    private void SpawnIconRestoreSentinel()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "图标恢复哨兵未拉起：MainModule 为空");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(
                exe, $"--icon-restore-sentinel {System.Diagnostics.Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _ = System.Diagnostics.Process.Start(psi);
            DiagnosticLog.Trace("shell.desktop", "图标恢复哨兵已拉起");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"图标恢复哨兵拉起失败：{ex.Message}");
        }
    }

    /// <summary>恢复 explorer 桌面图标为可见（无条件 SW_SHOW，幂等）：壳退出后用户桌面必须
    /// 回到 explorer 默认可见态（环境不可破坏）——无论此前是壳隐藏的还是用户双击隐藏的。
    /// 多个退出钩子先后触发重复调用无副作用。</summary>
    private void RestoreIcons()
    {
        try
        {
            // 图标层整体恢复（DefView + SysListView32）：只恢复 ListView 而 DefView 残留隐藏
            // 会让桌面右键整体失效（真机实证）。
            var listView = FindDesktopListView();
            var defView = FindDesktopDefView();
            if (listView != IntPtr.Zero && defView != IntPtr.Zero &&
                NativeMethods.GetParent(listView) != defView)
            {
                // 遗留态自愈：旧版本（SetParent 摘除法）或外部工具把列表视图摘出窗口树时，
                // 只 SW_SHOW 会让"无父 ListView"以顶层窗口形式闪现 → 先挂回 DefView。
                //（样式无需回写：本版本不再改样式位，ShowWindow 已足以恢复可见性。）
                _ = NativeMethods.SetParent(listView, defView);
                DiagnosticLog.Trace("shell.desktop", $"图标恢复：listView=0x{listView:X} 遗留摘除态已挂回 DefView");
            }

            if (listView != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(listView, SW_SHOW);
            }
            if (defView != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(defView, SW_SHOW);
            }

            if (listView != IntPtr.Zero || defView != IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.desktop", $"图标恢复：listView=0x{listView:X} defView=0x{defView:X} SW_SHOW 已执行");
            }
            else
            {
                // 查找失败必须落盘：这是"恢复静默落空"的唯一路径（explorer 桌面结构瞬时变化等）
                DiagnosticLog.Trace("shell.desktop", "图标恢复失败：找不到 DefView/SysListView32（Progman/WorkerW 链）");
            }

            // 【回归修复 2026-09-06】任务栏恢复：explorer 崩溃/壳异常退出后任务栏可能残留隐藏或消失。
            // 1) explorer 未运行则手动拉起（崩溃后未自动重启的场景）；
            // 2) 确保 Shell_TrayWnd / Shell_SecondaryTrayWnd 可见（NativeTaskbarManager 幂等）。
            try
            {
                var explorerProcs = System.Diagnostics.Process.GetProcessesByName("explorer");
                if (explorerProcs.Length == 0)
                {
                    DiagnosticLog.Trace("shell.desktop", "任务栏恢复：explorer 未运行，启动 explorer.exe");
                    System.Diagnostics.Process.Start("explorer.exe");
                }
                else
                {
                    BetterDesktop.Shell.Core.Windowing.NativeTaskbarManager.SetTaskbarVisible(true);
                    DiagnosticLog.Trace("shell.desktop", "任务栏恢复：Shell_TrayWnd SW_SHOW 已执行");
                }
            }
            catch (Exception ex2)
            {
                DiagnosticLog.Trace("shell.desktop", $"任务栏恢复异常：{ex2.Message}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"图标恢复异常：{ex.Message}");
        }
    }

    // ======== explorer 原生桌面图标的幂等显隐 ========
    // 窗口链：Shell(Progman) → SHELLDLL_DefView → SysListView32("FolderView")。
    // ⚠️ DefView 不一定挂在 Progman 直下：壁纸引擎（Wallpaper Engine）等会创建 **顶层** WorkerW
    //    并把 DefView 移过去。查找失败曾导致"隐藏成功、恢复失效"——这条路径（显隐 + 挂载查找）
    //    统一收口到 shell-core `DesktopHostWindow.FindDefView/FindIconListView/FindMountPoint`，
    //    保证三处用的是同一条兼容查找（2026-09-14）。

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;



    /// <summary>
    /// 定位 explorer 桌面图标 ListView（SysListView32）。
    /// 实现收口 shell-core <c>DesktopHostWindow.FindIconListView</c>（Progman 直子 → 顶层 WorkerW 变体）。
    /// </summary>
    private static IntPtr FindDesktopListView() => DesktopHostWindow.FindIconListView();

    /// <summary>桌面 ListView 当前是否有选中项 —— 即"本次双击是否落在图标上"的判据。
    ///
    /// 原理：explorer 处理双击的**第一次按下**时会把该图标设为选中（点空白则清空选中），
    /// 所以在我们看到 DOWN2 的这一刻，选中态已经能反映落点。
    ///
    /// 安全纪律（见文件内实证明细）：
    ///   · 只发 **LVM_GETSELECTEDCOUNT**（wParam/lParam 无指针）——实测 200 次零崩溃；
    ///     绝不用 LVM_HITTEST（lParam 指向本进程内存 → explorer 内存破坏，1 次即崩）；
    ///   · 只在 Dispatcher 线程调用（本方法全部调用点都在双击后的窗口操作路径上）；
    ///   · 查询失败/超时（explorer 忙）**保守返回 true** = 当作"在图标上" → 本次不切换。
    ///     宁可这一次不隐藏，也绝不把用户"启动应用"的双击变成"图标全隐藏"。
    /// </summary>
    private bool NativeDesktopHasSelection()
    {
        try
        {
            var listView = FindDesktopListView();
            if (listView == IntPtr.Zero)
            {
                return true; // 找不到 ListView：保守不切换
            }

            var ok = SendMessageTimeoutPtr(listView, LvmGetSelectedCount, IntPtr.Zero, IntPtr.Zero,
                SmtoBlock | SmtoAbortIfHung, SelectionQueryTimeoutMs, out var selected);
            if (ok == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.desktop", "原生桌面双击：选中态查询超时 → 保守按\"在图标上\"处理（不切换）");
                return true;
            }

            return selected.ToInt64() > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>「格式转换」快照的延迟重试计数（见 ApplyShellMenuRegistration 第 ④ 步）。</summary>
    private int _shellMenuConvertRetries;

    /// <summary>
    /// 系统右键菜单贡献的**唯一应用点**：按设置键决定"注册/注销 + 写快照"（幂等，可反复调用）。
    ///
    /// 【2026-09-11 架构换代 · 免宿主原生扩展】
    ///   旧形态：每项一个注册表静态 verb（级联/单入口），命令 = `Cli --menu-cmd <action>` ——
    ///     「桌面控制」「格式转换」都得"拉起 CLI → 宿主 → 弹自绘 WPF 菜单"，即**必须有宿主在跑**；
    ///     且静态 verb 只支持 %1，多选时 explorer 逐文件调用，拿不到选中全集。
    ///   新形态：一枚进程内 COM 扩展（native/BetterDesktopShellMenu.dll）接管这两组菜单 ——
    ///     读 shellmenu.json 快照自行渲染、`IShellItemArray` 一次拿多选全集、点击直接派发 CLI（**免宿主**）。
    ///     本方法因此只负责**写快照**（注册/注销/自愈见 ① 步的归属说明：已收归 L1 常驻者）。
    ///
    /// 【设置键】
    ///   · `shellmenu.comExtension`（新）= 扩展总开关：关 → 注销扩展 + 删除快照；
    ///   · `shellmenu.desktopControls` / `shellmenu.convert` = 两组菜单各自是否写进快照。
    ///
    /// 【剪贴板历史刻意不并入快照】它必须宿主在线（读宿主内存里的历史），塞进快照只会在无宿主时
    /// 弹"需要 BetterDesktop 正在运行"；且 ClipboardShellMenuRegistrar 已在 4 个场景注册独立项，
    /// 并入会在背景菜单出现重复项。故其现状完全不动。
    ///
    /// 【为什么第 ④ 步要延迟重试】插件装配顺序不保证：shell-convert 与 shell-desktop 并行加载，
    /// 本方法在 LoadAsync 中运行时 IConvertMenuService 可能尚未 Provide —— 快照就会缺「格式转换」，
    /// 用户看到"少一项"（无报错、极难排查）。有限次重试 + 幂等重写把这个问题消掉。
    /// </summary>
    private void ApplyShellMenuRegistration()
    {
        try
        {
            // ① 总开关：关 → 删除快照（原生侧随即不再显示任何项）
            //    【为什么不在这里注销注册表键】注册权 2026-09-17 已收归 L1 常驻者（Agent）：
            //    桌面服务与宿主谁先起来都可能在跑本方法，双写注册表会漂移（M2b 登记的遗留）。
            //    快照删除已足以让菜单立刻"什么都不显示"，故关开关的效果与原行为一致；
            //    注册表键的注销由 Agent（agent/Program.cs 的 StartContextMenuMaintenance）与
            //    安装器（Cli --system-integration unregister）负责。
            if (!(_settings?.Get("shellmenu.comExtension", true) ?? true))
            {
                BetterDesktop.Shell.ContextMenus.Services.ShellMenuConfigWriter.Remove();
                return;
            }

            // ② 【已移除】曾在此直调 ComShellExtensionRegistrar.Register —— 见 ① 的归属说明，
            //    注册/自愈/注销现在只在 SystemIntegrationRegistrar（Agent 与安装器调用）一处发生。
            //    本方法保留的职责：写快照 + 清理历史静态注册键（③④ 步）。

            // ③ 写配置快照（菜单树：桌面控制 + 格式转换）
            var convertMenu = _context?.Get<BetterDesktop.Shell.Convert.Contracts.IConvertMenuService>();
            var items = ShellMenuContentBuilder.Build(_settings, convertMenu);
            if (BetterDesktop.Shell.ContextMenus.Services.ShellMenuConfigWriter.Write(items, extensionEnabled: true, out var writeError))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"shellmenu.json 已更新：{items.Count} 组（转换服务可用={convertMenu is not null}）");
            }
            else
            {
                DiagnosticLog.Trace("shell.desktop", $"shellmenu.json 写入失败：{writeError}");
            }

            // ④ 快照缺「格式转换」→ 有限次延迟重试（等 shell-convert 注册服务）
            if (convertMenu is null && _shellMenuConvertRetries < 3)
            {
                _shellMenuConvertRetries++;
                var attempt = _shellMenuConvertRetries;
                _ = Task.Delay(1500).ContinueWith(
                    _ =>
                    {
                        DiagnosticLog.Trace("shell.desktop",
                            $"shellmenu 快照重试第 {attempt} 次（等 IConvertMenuService 注册）");
                        ApplyShellMenuRegistration();
                    },
                    TaskScheduler.Default);
            }

            // ⑤ 历史静态注册键清理：旧「桌面控制」「格式转换」项已由原生扩展取代，必须删掉，
            //    否则同一功能会在菜单里出现两遍（一份走宿主、一份免宿主）。
            DesktopSystemMenuRegistrar.UnregisterUiControls();
            DesktopSystemMenuRegistrar.UnregisterConvert();

            // ⑥ 【2026-09-18 形态统一】「切换到自绘桌面」已并入快照的「桌面控制」开关组
            //    （与其它开关同形态：图标 + 勾选态 + 走批协议 toggle-desktop）。
            //    这里改为**只注销静态项**：否则同一功能会在经典菜单（静态项）与新版菜单（快照项）
            //    各出现一次，且一个带图标勾选框、一个没有——正是用户反馈的"不统一"。
            DesktopSystemMenuRegistrar.UnregisterToggleDesktop();

            // ⑦ 压缩/解压：用户拍板不再注册系统菜单 → 启动/变更即注销（幂等清理历史键）。
            DesktopSystemMenuRegistrar.UnregisterArchive();
        }
        catch (Exception ex)
        {
            // 注册失败不阻断插件加载（M10）
            DiagnosticLog.Trace("shell.desktop", $"系统右键菜单应用失败: {ex.Message}");
        }
    }

    /// <summary>explorer 原生桌面图标当前是否可见。
    /// 【2026-09-11 四轮修复】判据随隐藏原语同步改回 **IsWindowVisible(SysListView32)**：
    /// 原 GetParent(lv)==DefView 判据是 SetParent 摘除法的产物——摘除法退场后父窗口恒等于 DefView，
    /// 该判据会恒为"可见"，双击将退化成单调隐藏（今晨日志里"每次双击都报当前隐藏"正是这类
    /// 判据/原语不同步的系统性偏差，只是方向相反）。
    /// 窗口找不到时按**可见**处理（保守：宁可多切一次，也不把用户桌面永久留在隐藏态）。</summary>
    private bool AreNativeIconsVisible()
    {
        try
        {
            var listView = FindDesktopListView();
            if (listView == IntPtr.Zero)
            {
                return true;
            }

            // 遗留态自愈：旧版本（SetParent 摘除法）/外部工具可能把列表视图摘出窗口树，
            // 此时 IsWindowVisible 判据不可靠 → 先挂回 DefView（不写样式，ShowWindow 足够）。
            var defView = FindDesktopDefView();
            if (defView != IntPtr.Zero && NativeMethods.GetParent(listView) != defView)
            {
                _ = NativeMethods.SetParent(listView, defView);
                DiagnosticLog.Trace("shell.desktop", $"图标可见性判据：检测到遗留摘除态，已挂回 DefView=0x{defView:X}");
            }

            return NativeMethods.IsWindowVisible(listView);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 幂等设置 explorer 原生桌面图标可见性。
    /// 【2026-09-11 四轮修复】自绘/原生两种模式**统一**用 ShowWindow(SysListView32)：
    ///   自绘模式：DefView 是自绘层宿主必须保持可见 → 只隐 SysListView32；
    ///   原生模式：同样只隐/显 SysListView32（独立探针 16 连击实证零崩溃）。
    /// 幂等判据 = IsWindowVisible(SysListView32)。
    /// ⚠️ 钩子/点击/显隐路径**禁止任何跨进程窗口消息**（LVM_HITTEST / WM_COMMAND /
    ///    SendMessageTimeout）。可复现验证：`DesktopIconProbe --op=hittest --count=1`
    ///    即触发 explorer comctl32 0xc0000005 @0x71f2d 崩溃（事件日志可查）。
    ///    不要只信本注释——跑一次命令即可自行证实或证伪。
    /// ⚠️ 历史上此处写过"禁止回退 ShowWindow/0x7402"，该结论已被推翻（真凶是 LVM_HITTEST，
    ///    见文件头「已证伪存档」）。
    /// </summary>
    private void SetNativeIconsVisible(bool visible)
    {
        try
        {
            // 【2026-09-11 四轮修复】自绘/原生两种模式**统一**用 ShowWindow(SysListView32)：
            //   自绘模式：DefView 是自绘层宿主必须保持可见 → 只隐 SysListView32（长期验证路径）；
            //   原生模式：探针 16 连击实证 ShowWindow 零崩溃 —— 此前"ShowWindow 会崩"是**误判**
            //             （那批崩溃的真凶是每次左键按下都发的 LVM_HITTEST，见文件头实证）。
            // ⚠️ 绝不发跨进程窗口消息（LVM_HITTEST / WM_COMMAND 0x7402）：实测对桌面 ListView
            //    发 1 次 LVM_HITTEST 即打崩 explorer（comctl32 0xc0000005 @0x71f2d）。
            var lv = FindDesktopListView();
            if (lv == IntPtr.Zero)
            {
                // explorer 重启/桌面结构瞬变中：本次不动作，由 EnsureHiddenIntentApplied 兜底
                return;
            }

            if (NativeMethods.IsWindowVisible(lv) == visible)
            {
                return; // 幂等：已是目标状态
            }

            _ = NativeMethods.ShowWindow(lv, visible ? SW_SHOW : SW_HIDE);
            DiagnosticLog.Trace("shell.desktop",
                $"原生图标{(visible ? "显示" : "隐藏")}：ShowWindow listView=0x{lv:X} {(visible ? "SW_SHOW" : "SW_HIDE")}");
        }
        catch
        {
            // 显隐失败不阻断（M10）
        }
    }

    /// <summary>
    /// 定位 SHELLDLL_DefView（FindDesktopListView 的父级）。
    /// 实现收口 shell-core <c>DesktopHostWindow.FindDefView</c>（Progman 直子 → 顶层 WorkerW 变体）。
    /// </summary>
    private static IntPtr FindDesktopDefView() => DesktopHostWindow.FindDefView();

    // ======== 原生桌面模式的双击切换（WH_MOUSE_LL） ========
    // 自绘桌面关闭时没有自绘窗口可接双击，只能用低级鼠标钩子感知"双击落在原生桌面上"。
    // 判定链（每步都是快路径，不命中立即放行）：
    //   1) 【2026-09-11】不再依赖 WM_LBUTTONDBLCLK——桌面空白（Progman/WorkerW）类未注册
    //      CS_DBLCLKS，Windows 不产生 DBLCLK 消息（用户实测原生模式双击切换无效）。
    //      改为自判定双击：跟踪 WM_LBUTTONDOWN 序列，两次按下落在系统双击时间/距离内 → 双击。
    //   2) WindowFromPoint 的窗口类 ∈ {Progman, WorkerW, SHELLDLL_DefView, SysListView32, FolderView}
    //      —— FolderView 为对齐草特码工具白名单（Win11 新版桌面/文件夹视图宿主）；
    //      自绘模式下桌面被 DesktopWindow 覆盖（类名 HwndWrapper[...]），天然不命中，
    //      两种模式互不打架；dock/菜单栏/开始菜单等普通窗口同样不命中。
    //   （原第 3 步"命中 SysListView32 时做 LVM_HITTEST 以排除图标"已于四轮修复删除：
    //     跨进程 LVM_HITTEST 1 次即打崩 explorer，且参照物草特码工具本就只按类名判定。）
    // 切换以【实际可见性】为基准写 desktop.iconsHidden（而非持久化值）：用户用 explorer
    // 自带的「查看 > 显示桌面图标」改过状态时自动对齐，避免状态漂移导致一次"空切换"。

    private const int WhMouseLl = 14;
    private const long WmLbuttondown = 0x0201;
    // 隐藏意图兜底轮询间隔（ms）：explorer 崩溃重启后补做隐藏（仅需要时运行）
    private const int IntentWatchIntervalMs = 2000;
    // GA_ROOT：顶层祖先（IsOverNativeDesktop 覆盖 Wallpaper Engine 悬浮层归属判定）
    private const uint GA_ROOT = 2;
    // 自判定双击参数：时间基准 = 系统 GetDoubleClickTime()（对齐草特码工具/用户双击速度设置，
    // 默认 500ms）；距离 8px 容忍 DPI 缩放误差（Windows 内部阈值 4px）。
    private const int DoubleClickDistPx = 8;
    // 切换冷却（毫秒）：一次双击/连点序列只允许一次切换
    private const long ToggleCooldownMs = 800;

    // 【2026-09-11 五轮修复 · 双击"太敏感"（点图标启动应用也被切换显隐）】
    //   判据 = **explorer 自己的选中态**：explorer 处理本次双击的 DOWN1 会把该图标置为选中，
    //   双击空白则清空选中 → 可见态下"选中数 > 0"即"这次双击落在了图标上"。
    //   查询用 LVM_GETSELECTEDCOUNT(0x1032)：wParam/lParam 均**不携带指针**。
    //   ⚠️ 纠正上一轮的过度结论（"跨进程消息一律禁用"是错的）。实测对照：
    //     · LVM_HITTEST：lParam 是**指向本进程地址空间**的 LVHITTESTINFO* →
    //       explorer 会把命中结果写进它自己地址空间里的那个外部指针 = 内存破坏，**1 次即崩**
    //       （comctl32 0xc0000005 @0x71f2d，事件日志实证）；
    //     · LVM_GETSELECTEDCOUNT（无指针）→ **200 次零崩溃**（probe --op=selcount 实测 3.1s/200 次）。
    //   因此真正要守的纪律是：① 只发**无指针参数**的消息；② 只在 Dispatcher 线程发（绝不进钩子回调）。
    private const int LvmGetSelectedCount = 0x1032; // LVM_FIRST(0x1000) + 50
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SelectionQueryTimeoutMs = 100;

    /// <summary>无指针参数的 SendMessageTimeout：仅用于"只回整数"的查询消息（见上方实证明细）。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutPtr(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);








    private void InstallDesktopDoubleClickHook()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(InstallDesktopDoubleClickHook));
            return;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        // LL 钩子装在 UI 线程（有消息泵即工作）；回调内只做快判定，切换经 Dispatcher 落设置
        _mouseHookProc = OnMouseHookProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(WhMouseLl, _mouseHookProc, NativeMethods.GetModuleHandle(null), 0);
        DiagnosticLog.Trace("shell.desktop", $"原生桌面双击钩子安装：{_mouseHook != IntPtr.Zero}");
    }

    private void RemoveDesktopDoubleClickHook()
    {
        // UnhookWindowsHookEx 必须在【安装钩子的那个线程】调用，否则静默失败（返回 false）。
        // 钩子装在 UI 线程，故此处与 Install 一样做 Dispatcher 编队。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RemoveDesktopDoubleClickHook));
            return;
        }

        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
        DiagnosticLog.Trace("shell.desktop", "原生桌面双击钩子已卸载");
    }

    /// <summary>按"当前模式 + 开关"决定钩子装/卸（幂等）。只有【原生模式 + desktop.doubleClickHideIcons】
    /// 才需要钩子：自绘模式由 DesktopWindow 的 WPF 事件处理，钩子装了也全被首行放行、白白参与
    /// 全局输入链。CTM 同款纪律：装/卸收口在唯一的配置应用点。</summary>
    private void ApplyDoubleClickHookState()
    {
        if (!_desktopEnabledCached && _doubleClickHideCached)
        {
            InstallDesktopDoubleClickHook();
        }
        else
        {
            RemoveDesktopDoubleClickHook();
        }

        ApplyIntentWatchState();
    }

    /// <summary>隐藏意图兜底轮询的启停（幂等，自带线程编队）：仅"原生模式 + 有隐藏意图"时运行——
    /// explorer 崩溃重启后新建的 ListView 默认挂回 DefView，需要补摘除。
    /// 【2026-09-11 三轮修复】原实现挂在 LL 钩子回调的每次左键按下（在阻塞全局输入的回调里做
    /// 跨进程 FindWindowEx），现改为 2s DispatcherTimer，彻底移出输入路径。</summary>
    private void ApplyIntentWatchState()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return; // 无 UI 线程（测试宿主等）：不轮询
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyIntentWatchState));
            return;
        }

        var needed = !_running && (_settings?.Get("desktop.iconsHidden", false) ?? false);
        if (!needed)
        {
            _intentTimer?.Stop();
            return;
        }

        if (_intentTimer is null)
        {
            _intentTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(IntentWatchIntervalMs),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => EnsureHiddenIntentApplied(),
                dispatcher);
        }

        _intentTimer.Start();
    }

    /// <summary>停止隐藏意图兜底轮询（幂等，自带线程编队）。</summary>
    private void StopIntentWatch()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(StopIntentWatch));
            return;
        }

        _intentTimer?.Stop();
        _intentTimer = null;
    }

    private IntPtr OnMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 【2026-09-11 四轮修复 · LL 钩子四条纪律】
        //   ① nCode/msg 先判后做 → 未命中路径零分配、零系统调用（本回调阻塞全局鼠标输入，
        //      超时会被系统静默摘钩）；
        //   ② 只按需读 MSLLHOOKSTRUCT 前 8 字节 POINT，不做整结构体反射封送；
        //   ③ 【本轮关键】回调内**绝不发任何跨进程窗口消息**：LVM_HITTEST / SendMessageTimeout 全删。
        //      实证：对桌面 SysListView32 发 1 次 LVM_HITTEST 即打崩 explorer（comctl32
        //      0xc0000005 @0x71f2d，事件日志可见）；桌面判定只按 WindowFromPoint + GetClassName
        //      （均为本地 API，零消息）；
        //   ④ 不再吞点击：撤销"两方同时操作同一 ListView"的竞争假设——该竞争是旧方案
        //      （SetParent 摘除法 + 每次左键按下都发 LVM_HITTEST）自己制造的；零消息的实证
        //      配方不吞点击，16 连击零崩溃。
        //   ⚠️ 本回调内【禁止】查设置（Get）、封送结构体、做窗口枚举/窗口树操作/命中测试。
        try
        {
            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            var msg = wParam.ToInt64();
            if (msg != WmLbuttondown || _desktopEnabledCached || lParam == IntPtr.Zero)
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            // 【2026-09-11 修复】原生模式双击自判定：桌面空白（Progman/WorkerW）类未注册
            // CS_DBLCLKS → 不产生 WM_LBUTTONDBLCLK（用户实测双击切换无效）。改为跟踪
            // WM_LBUTTONDOWN 序列：两次按下落在双击时间/距离内 → 判定双击。
            // MSLLHOOKSTRUCT 首字段即 POINT pt（X@0, Y@4）：直接指针读取，零封送零装箱。
            var pt = new NativeMethods.POINT
            {
                X = Marshal.ReadInt32(lParam),
                Y = Marshal.ReadInt32(lParam, 4),
            };

            var now = Environment.TickCount64;
            var withinTime = now - _lastDownTicks <= NativeMethods.GetDoubleClickTime();
            var withinDist = Math.Abs(pt.X - _lastDownPt.X) <= DoubleClickDistPx
                          && Math.Abs(pt.Y - _lastDownPt.Y) <= DoubleClickDistPx;
            if (!_doubleClickArmed || !withinTime || !withinDist)
            {
                // 非双击序列：只记状态（一次 TickCount64 + 两次 int 读取，无系统调用）
                _doubleClickArmed = true;
                _lastDownTicks = now;
                _lastDownPt = pt;
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            _doubleClickArmed = false;

            // 冷却（廉价，先判）→ 命中判定（WindowFromPoint/GetClassName，后判）：
            // 同一时刻的连环按下（实测一次双击可连发多次 DOWN）只允许一次切换，否则图标在
            // 100ms 内被反复隐藏/显示 = 桌面闪烁 + 延迟。
            if (now - _lastToggleTicks < ToggleCooldownMs || !IsOverNativeDesktop(pt))
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            _lastToggleTicks = now;
            // 落点留给 Dispatcher 上的命中测试（"双击在图标上"不切换；钩子内绝不做命中测试）
            _pendingTogglePoint = pt;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ToggleNativeIconsByDoubleClick();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(ToggleNativeIconsByDoubleClick));
            }
        }
        catch
        {
            // 钩子回调绝不允许抛异常（挂掉会拖垮全局鼠标输入）
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>屏幕点是否落在原生桌面上（Progman/WorkerW/DefView/SysListView32 任一，
    /// 或命中窗口的顶层祖先属于桌面层——覆盖 Wallpaper Engine RenderWindow 等悬浮层遮挡）。
    /// 只按类名判定，**不做命中测试**：跨进程 LVM_HITTEST 1 次即打崩 explorer（见文件头实证），
    /// 且参照物「草特码工具」同样只按类名白名单放行（双击图标也会切换，行为对齐）。</summary>
    private static bool IsOverNativeDesktop(NativeMethods.POINT pt)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var sb = new StringBuilder(64);
            if (NativeMethods.GetClassName(hwnd, sb, 64) <= 0)
            {
                return false;
            }

            var cls = sb.ToString();
            if (cls == "SysListView32")
            {
                // 【2026-09-11 四轮修复】此处原本发跨进程 LVM_HITTEST（后改 SendMessageTimeout 100ms），
                // 已实证为 explorer 崩溃真凶：对桌面 SysListView32 发 1 次即崩
                // （comctl32 0xc0000005 @0x71f2d；复现命令 `DesktopIconProbe --op=hittest --count=1`）。
                // 现改为纯类名放行——与"16 连击零崩溃"的实证配方一致（点击路径零跨进程消息）。
                // 代价：双击桌面图标也会切换（参照物草特码工具同样如此，属行为对齐）。
                return true;
            }

            if (cls is "SHELLDLL_DefView" or "Progman" or "WorkerW" or "FolderView")
            {
                return true;
            }

            // 【2026-09-11 根祖先判定】壁纸引擎（Wallpaper Engine）的 RenderWindow 悬浮层盖住
            // 大部分"桌面空白"（本机实测 x<420,y<250 外大片区域命中它），其顶层祖先=WorkerW →
            // 视为桌面空白；Chrome/Explorer 文件夹等普通应用窗口顶层祖先=自身 → 放行不误伤。
            var root = NativeMethods.GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero || root == hwnd)
            {
                return false;
            }

            if (NativeMethods.GetClassName(root, sb, 64) <= 0)
            {
                return false;
            }

            return sb.ToString() is "Progman" or "WorkerW";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>explorer 重启重摘兜底：原生模式 + intent=hidden + ListView 已挂载（explorer 崩溃
    /// 重启后新建的 ListView 默认挂回 DefView）→ 重新摘除。由 ApplyIntentWatchState 的 2s
    /// DispatcherTimer 轮询触发（两次 FindWindowEx + GetParent，微秒级），保证"隐藏意图"在
    /// explorer 重启后自动恢复。
    /// 【2026-09-11 三轮修复】原挂在 LL 钩子回调的每次左键按下——在阻塞全局鼠标输入的回调里
    /// 做跨进程窗口枚举，违反钩子纪律（超时会被系统静默摘钩），已移到定时器。</summary>
    private void EnsureHiddenIntentApplied()
    {
        if (_running)
        {
            return;
        }

        if (!(_settings?.Get("desktop.iconsHidden", false) ?? false))
        {
            return;
        }

        try
        {
            var lv = FindDesktopListView();
            var defView = FindDesktopDefView();
            if (lv == IntPtr.Zero || defView == IntPtr.Zero)
            {
                return;
            }

            if (NativeMethods.GetParent(lv) == defView)
            {
                SetNativeIconsVisible(false); // 新 ListView 挂载态 → 补摘除（幂等）
            }
        }
        catch
        {
            // 兜底失败不阻断（M10）
        }
    }

    /// <summary>原生桌面空白双击 → 切换 desktop.iconsHidden（以实际可见性为基准，见块注释）。
    /// 【2026-09-11 四轮修复】删除"延迟 250ms + Task 线程池 + 来源去重"整套补偿机制——它们是给
    /// SetParent 摘除法 + 每次左键按下都发 LVM_HITTEST 的旧方案兜底的；实证配方（点击路径零跨进程
    /// 消息 + ShowWindow 原语）在消息泵/Dispatcher 线程同步执行即零崩溃。现在只做三件事：
    ///   ① 消息自由的命中测试（落点在图标上 → 不切换，保留"双击打开文件"语义）；
    ///   ② 以实际可见性为基准写意图（desktop.iconsHidden）；
    ///   ③ 应用收口在 OnSettingsChanged（唯一配置应用点），此处不重复操作窗口树。</summary>
    private void ToggleNativeIconsByDoubleClick()
    {
        try
        {
            // 2026-09-10 开关：desktop.doubleClickHideIcons=false 时原生桌面双击不切换（与自绘模式同键同语义）。
            if (!_doubleClickHideCached)
            {
                return;
            }

            var nowVisible = AreNativeIconsVisible();

            // 【2026-09-11 五轮修复】可见态下，若本次双击落在**图标**上 → 只放行"打开文件"，
            //   不切换显隐（用户实测："双击应用图标启动应用"也被当成双击空白而把图标全隐藏）。
            //   判据见 NativeDesktopHasSelection()：explorer 的选中态。
            if (nowVisible && NativeDesktopHasSelection())
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"原生桌面双击：落点在图标上（explorer 选中数>0）→ 不切换（落点 {_pendingTogglePoint.X},{_pendingTogglePoint.Y}）");
                return;
            }

            DiagnosticLog.Trace("shell.desktop",
                $"原生桌面双击：图标当前{(nowVisible ? "可见" : "隐藏")} → 切换为{(nowVisible ? "隐藏" : "可见")}"
                + $"（落点 {_pendingTogglePoint.X},{_pendingTogglePoint.Y}）");
            DiagnosticLog.Trace("shell.desktop",
                $"原生桌面双击：图标当前{(nowVisible ? "可见" : "隐藏")} → 切换为{(nowVisible ? "隐藏" : "可见")}"
                + $"（落点 {_pendingTogglePoint.X},{_pendingTogglePoint.Y}）");

            // 只落意图：应用收口在 OnSettingsChanged（唯一配置应用点），避免双重执行窗口操作
            _settings?.Set("desktop.iconsHidden", nowVisible); // 可见→隐藏(true)；隐藏→显示(false)
            ApplyIntentWatchState(); // 意图落地后同步兜底轮询（隐藏→需轮询；显示→停表）
        }
        catch
        {
            // 切换失败不阻断（M10）
        }
    }
}
