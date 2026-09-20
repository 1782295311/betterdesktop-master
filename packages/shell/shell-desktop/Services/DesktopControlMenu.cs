// BetterDesktop.Shell.Desktop —「桌面控制」二级菜单（内容每次现算）
//
// 【2026-09-11 用户拍板】桌面右键里的「桌面控制」只注册**一条静态项**，二级菜单在点击时**代码动态生成**。
// 为什么不做成注册表级联 / ExtendedSubCommandsKey：
//   ① 注册表级联是**静态**机制——子项集合只能来自注册表枚举，无法按调用上下文生成，
//      官方也没提供"注册表里放动态项"的路子（真动态要 COM：IExplorerCommand / IContextMenu）；
//   ② Windows 11 新版右键菜单**不呈现任何注册表级联**（只能在"显示更多选项"的经典菜单里看到），
//      用户实测"桌面控制的二级从来没有"。
// 代码生成二级的额外好处：可带勾选状态、可按"组件是否存在"条件显示、可即时反映设置变化。
//
// 【2026-09-17 桌面控制独立化（用户需求）】本类现在是**两个进程共用的同一份实现**：
//   · 宿主内自绘桌面右键（DesktopIconsControl，进程内渲染；宿主在 = 全部项可用）；
//   · 独立进程 BetterDesktop.DesktopControl.exe（主程序没跑时由原生扩展派发过来，见该 exe 文件头）。
// 调用链（无宿主）：explorer 原生扩展 → Cli.exe --menu-batch → DesktopControl.exe --desktop-controls
//                 → 本类 Build/ShowAtCursor（独立进程的 Dispatcher 线程）。
// 因此 Build **不能假设宿主在线**：只属于宿主的组件（菜单栏 / Dock / 热键侧板 / 双击钩子）在宿主缺席时
// 必须**置灰 + 注明**（用户 2026-09-17 拍板："宿主组件项置灰，不假装能点"），而图标 / 任务栏 / 剪贴板历史照常可用。
//
// 键名与默认值的唯一真相源 = BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog（勿在本类另写一份）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.DesktopControl;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Windowing;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>「桌面控制」二级菜单（内容动态生成，在光标处弹出）。</summary>
public static class DesktopControlMenu
{
    /// <summary>
    /// 宿主缺席时，宿主内组件项的置灰提示（渲染在行右侧的灰字）。
    /// 用 GestureText 承载是刻意选择：**不是快捷键**，而是"为什么点不了"——禁用项右侧注因是通行做法，
    /// 且比把提示塞进标题（"菜单栏显隐（需启动…）"）更短、不破坏标题口径。
    /// </summary>
    public const string HostRequiredHint = "需启动 BetterDesktop";

    /// <summary>
    /// 「菜单宿主注入」：宿主进程是否在线的探测（null = 宿主内运行，一律按在线处理）。
    /// 与 <see cref="ToggleRouter"/> 成对注入，说明见其注释。
    /// </summary>
    public static Func<bool>? HostRunningProbe { get; set; }

    /// <summary>
    /// 「菜单宿主注入」：跨进程翻转路由（null = 宿主内运行 → 进程内直接翻设置，走事件总线即时反应）。
    /// <para>
    /// 【为什么需要注入】自绘桌面的右键菜单在**本包**内构建（<c>DesktopIconsControl.BuildDesktopMenu</c>），
    /// 而"宿主在线走命令桥 / 缺席走免宿主直写"的实现在 shell-desktop-control/DesktopToggleExecutor ——
    /// shell-desktop **不能**反向依赖它（依赖方向）。
    /// </para>
    /// <para>
    /// 【2026-09-17 修 bug】此前 <c>DesktopIconsControl</c> 调 <c>Build</c> 时两个参数都用默认值
    /// （hostRunning=true、toggle=null）→ 菜单栏 / Dock / 热键侧板这些**宿主内组件**的翻转只写进了
    /// 桌面服务自己那份设置快照，宿主永远收不到 → 用户实测"自绘右键菜单里有这项但点了没反应"。
    /// 现在桌面服务在装配时注入这两个委托（见 DesktopControlEntry.InitializeServiceAsync）。
    /// </para>
    /// </summary>
    public static Action<string>? ToggleRouter { get; set; }

    /// <summary>
    /// 在光标处弹出「桌面控制」菜单。返回项数（供日志/诊断）。
    /// </summary>
    /// <param name="settings">设置服务（读写各开关）。</param>
    /// <param name="openClipboardHistory">打开剪贴板历史（由宿主/独立进程注入——shell-desktop 不依赖 shell-clipboard）。</param>
    /// <param name="hostRunning">宿主是否在线（决定宿主内组件项是否置灰）。</param>
    /// <param name="toggle">动作执行路由（独立进程注入；宿主内传 null = 进程内直接翻设置）。</param>
    /// <param name="onClosed">菜单关闭回调（短命进程用它退出；宿主内为 null）。</param>
    public static int ShowAtCursor(
        ISettingsService? settings,
        Action? openClipboardHistory = null,
        bool hostRunning = true,
        Action<string>? toggle = null,
        Action? onClosed = null)
    {
        var items = Build(settings, openClipboardHistory, hostRunning, toggle);

        // 光标位置：物理像素 → DIP（与 Bootstrap.ShowConvertMenu 同款换算）。
        var dpi = VisualTreeHelper.GetDpi(Application.Current?.MainWindow).PixelsPerDip;
        if (dpi <= 0)
        {
            dpi = 1.0;
        }

        if (!NativeMethods.GetCursorPos(out var pt))
        {
            DiagnosticLog.Trace("shell.desktop", "桌面控制菜单：GetCursorPos 失败");
            onClosed?.Invoke();
            return 0;
        }

        // 第三参 = DPI 参考（贴边收敛换算工作区用）：此处无触发控件，退回主窗口
        //（独立进程的主窗口 = 1px 不可见承载窗，见 DesktopControl 的 MenuAnchorWindow）。
        DesktopMenuPopup.Show(items, new Point(pt.X / dpi, pt.Y / dpi), Application.Current?.MainWindow, onClosed);
        DiagnosticLog.Trace("shell.desktop", $"桌面控制菜单已显示: 项数={items.Count} 宿主在线={hostRunning}");
        return items.Count;
    }

    /// <summary>
    /// 构建「桌面控制」菜单项（**每次弹出时按当前设置重新生成** = 动态二级）。
    /// 条件项规则（用户 2026-09-11 拍板）：菜单栏 / Dock 只在对应组件**存在**时才给出控制。
    /// 置灰规则（用户 2026-09-17 拍板）：宿主内组件在宿主缺席时置灰 + 注明，不隐藏（隐藏会让人以为功能没了）。
    /// </summary>
    public static IReadOnlyList<MenuItemDef> Build(
        ISettingsService? settings,
        Action? openClipboardHistory = null,
        bool hostRunning = true,
        Action<string>? toggle = null)
    {
        bool Get(string key, bool def) => settings?.Get(key, def) ?? def;

        // openClipboardHistory 保留在签名里：调用方（宿主 / 独立进程）都注入它，"打开面板"仍可被别的
        // 菜单项复用；本项改为开关后不再直接使用，不做签名变动以免调用方跟着改。
        _ = openClipboardHistory;

        var items = new List<MenuItemDef>();

        // 目录缺项（键被改名/删除）时**不显示该项**，而不是抛异常或画一个点不动的空项。
        void AddToggle(string id, string text, string toggleName, bool isChecked)
        {
            if (!DesktopToggleCatalog.TryGet(toggleName, out var spec))
            {
                DiagnosticLog.Trace("shell.desktop", $"桌面控制菜单：目录缺少开关 {toggleName}，已跳过");
                return;
            }

            var disabled = spec.RequiresHost && !hostRunning;
            items.Add(new MenuItemDef
            {
                Id = id,
                Text = text,
                Kind = MenuItemKind.Toggle,
                IsChecked = isChecked,
                IsEnabled = !disabled,
                GestureText = disabled ? HostRequiredHint : null,
                Command = () => Execute(spec, isChecked, settings, toggle),
            });
        }

        // 勾选态 = "当前处于该状态"：桌面图标显隐（勾=图标可见）、隐藏任务栏（勾=已隐藏）、双击隐藏图标（勾=功能开）
        AddToggle("ctlIcons", "桌面图标显隐", DesktopToggleCatalog.Icons,
            !Get(DesktopToggleCatalog.IconsKey, false));

        // 【2026-09-17 优先级 · 用户拍板】任务栏勾选态取**实际**可见性，而不是意图键：
        // dock 启用时会默认隐藏原生任务栏（其它功能的默认隐藏效果），只看 components.wintaskbar 会显示
        // "未勾（没隐藏）"而任务栏其实已经消失（用户实测的错位）——翻转时会把"用户显式要求可见"写进留痕键，
        // 于是「桌面控制」的显式选择能压过 dock 的默认隐藏（裁决在 DesktopControlRules，单测钉住）。
        var taskbarIntentVisible = Get(DesktopToggleCatalog.TaskbarKey, true);
        var taskbarActualVisible = NativeTaskbarManager.IsTaskbarVisible() ?? taskbarIntentVisible;
        // 留痕：意图与实际不一致时（典型 = dock 默认隐藏）必须能在日志里看出来，否则又是一轮"点了没反应"的死查。
        DiagnosticLog.Trace("shell.desktop",
            $"任务栏：意图可见={taskbarIntentVisible} 实际可见={taskbarActualVisible} " +
            $"显式留痕={Get(DesktopToggleCatalog.TaskbarExplicitVisibleKey, false)} dock={Get(DesktopToggleCatalog.DockKey, true)}");
        AddToggle("ctlTaskbar", "隐藏任务栏", DesktopToggleCatalog.Taskbar, taskbarActualVisible);

        AddToggle("ctlDoubleClick", "双击隐藏图标", DesktopToggleCatalog.DoubleClick,
            Get(DesktopToggleCatalog.DoubleClickKey, true));

        // 条件项：组件存在才有对应控制
        if (Get(DesktopToggleCatalog.MenuBarKey, true))
        {
            AddToggle("ctlMenuBar", "菜单栏显隐", DesktopToggleCatalog.MenuBar,
                Get(DesktopToggleCatalog.MenuBarKey, true));
        }

        if (Get(DesktopToggleCatalog.DockKey, true))
        {
            AddToggle("ctlDock", "Dock 显隐", DesktopToggleCatalog.Dock,
                Get(DesktopToggleCatalog.DockKey, true));
        }

        // 【2026-09-17 用户反馈："自绘右键菜单的桌面控制里，剪贴板历史只有打开功能，没有关闭功能"】
        // 此前这里是**命令项**（Kind=Command + 标题带省略号）→ 只能打开面板，无法停用整个功能。
        // 现改为与「热键侧板显隐」同构的**开关**（目录单点驱动）：
        //   勾选 = 功能已启用；点一下 = 翻转 →
        //     关：面板 exe + 引擎退场，右缘「›」侧边手柄一并消失（不留进程）；
        //     开：引擎/入口回来，并**顺便打开侧边面板**（"开启时顺便打开侧边面板"，见 ClipboardPlugin.ApplyEnabledState）。
        // 打开面板的另外两条路仍在：全局热键、侧边「›」手柄。
        if (Get(ClipboardEntryKey, true))
        {
            AddToggle("ctlClipboard", "剪贴板历史", DesktopToggleCatalog.Clipboard,
                Get(DesktopToggleCatalog.ClipboardKey, true));
        }

        AddToggle("ctlHotkeyPanel", "热键侧板显隐", DesktopToggleCatalog.HotkeyPanel,
            Get(DesktopToggleCatalog.HotkeyPanelKey, true));

        return items;
    }

    /// <summary>剪贴板历史项的显隐开关键（入口开关，不是"开关本身的状态"）。</summary>
    private const string ClipboardEntryKey = "shellmenu.clipboard";

    /// <summary>
    /// 执行一次翻转。
    /// <para>
    /// 两条路径刻意分开：
    /// · **独立进程**（<paramref name="toggle"/> 非空）：交给统一动作路由——宿主在线走命令桥热切（宿主进程内改设置
    ///   → 事件 → 组件即时反应），宿主缺席则直写设置 + 原生层立即生效（图标 / 任务栏）；
    /// · **宿主内**（<paramref name="toggle"/> 为空）：进程内直接翻，事件总线即时反应，与既有行为一致。
    /// </para>
    /// </summary>
    private static void Execute(DesktopToggle spec, bool currentVisibleState, ISettingsService? settings, Action<string>? toggle)
    {
        if (toggle is not null)
        {
            toggle(spec.Name);
            return;
        }

        var next = !currentVisibleState;
        settings?.Set(spec.SettingsKey, next);

        // 显式留痕：让「桌面控制」的选择压过其它功能的默认隐藏效果（见 DesktopToggleCatalog.ExplicitOverrides）。
        if (settings is not null)
        {
            foreach (var (key, value) in DesktopToggleCatalog.ExplicitOverrides(spec, next))
            {
                settings.Set(key, value);
            }
        }
    }
}
