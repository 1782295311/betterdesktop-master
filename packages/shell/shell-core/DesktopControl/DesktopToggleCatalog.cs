// BetterDesktop.Shell.Core —「桌面控制」开关目录（命令名 → 设置键 的唯一真相源）
//
// 【为什么要有这份目录】同一个开关此前在四处各写一份映射，源码注释已自陈"三处映射漂移是隐患"，且漂移
// 真实发生过（2026-09-11：CLI 漏了 doubleclick → 宿主没跑时点「双击隐藏图标」报"未知 toggle-key"）：
//   · 宿主命令桥      host/Bootstrap.cs            case "toggle-key"
//   · CLI 无宿主直写  BetterDesktop.Cli/HeadlessExecutor.ToggleKeyMap
//   · 宿主短命入口    host/ToggleKeyCommand
//   · 独立进程        BetterDesktop.DesktopControl（2026-09-17 桌面控制独立化新增）
// 故把「命令名 → 设置键 / 默认值 / 宿主离线时可执行性」收敛到此单点，各调用方只消费、不再自持副本。
//
// 【字段语义】
//   · Name            命令名（--toggle-key 参数 / 命令桥 path / 批协议 args[0]）——跨进程契约，改名需两端同步；
//   · SettingsKey     落盘键（settings.json）；
//   · Default         键缺失时的默认值（与宿主内既有默认值一致，勿单方面改）；
//   · RequiresHost    宿主不在时是否必然无效（true → 菜单置灰并注明「需启动 BetterDesktop」）；
//   · NativeEffective 宿主不在时能否靠 explorer 原生层**立即生效**（图标 / 任务栏）；
//   · ExplicitVisibleKey 用户"显式要求可见"的留痕键（可空；见下"优先级"）。

using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Core.DesktopControl;

/// <summary>「桌面控制」里的一个开关（跨进程契约的元数据）。</summary>
public sealed record DesktopToggle(
    string Name,
    string SettingsKey,
    bool Default,
    bool RequiresHost,
    bool NativeEffective,
    /// <summary>
    /// 面向用户的显示名（中文）。**加它的直接原因（2026-09-20）**：独立进程
    ///（BetterDesktop.DesktopControl）在"宿主缺席且本开关无原生效果"时要给用户一句人话提示，
    /// 而它拿不到宿主内菜单那份中文标题（跨包，见 shell-desktop/DesktopControlMenu 的 AddToggle）。
    /// 放在这里而不是各调用方各写一份 —— 本文件头就是为"映射漂移"而设的单点。
    /// </summary>
    string DisplayName = "",
    /// <summary>
    /// 「显式可见」留痕键（可空）。写入 true = **用户显式要求该部件可见**，用来压过其它功能的默认隐藏效果
    /// （目前只有原生任务栏：dock 启用即默认隐藏它）。为什么需要留痕：默认值本身就是"可见"，
    /// 不留痕就无法把"用户明确要它可见"与"用户从没动过"区分开——而这两者的正确行为相反。
    /// </summary>
    string? ExplicitVisibleKey = null,
    /// <summary>
    /// 翻转本开关时要**复位**的"显式覆盖"键（可空）。语义：本开关一变，之前对别的开关做的显式覆盖即失效，
    /// 回到各自默认行为。
    /// <para>
    /// 【2026-09-17 用户拍板】关掉 / 重开 Dock → 清掉任务栏的显式可见留痕，回到 dock 默认隐藏。
    /// 为什么是"复位"而不是"记住"：Dock 是任务栏默认隐藏的**驱动方**，它一变，用户之前对任务栏的显式要求
    /// 就失去了上下文（用户原话："关掉/重开 Dock 时清掉留痕，回到 dock 默认隐藏"）。
    /// </para>
    /// </summary>
    IReadOnlyList<string>? ResetsExplicitKeys = null);

/// <summary>
/// 「桌面控制」开关目录（命令名 → 设置键 的唯一真相源）。
/// <para>
/// 自 2026-09-17 起也兼作**托盘「功能开关」与 CLI <c>--toggle-key</c> 的共享表**：
/// 除桌面控制项外，还登记了热键侧板、索引引擎、截图工具等"非桌面归属"的开关
/// （托盘纪律：开关一律经 CLI 契约落地，故需要一处命令名→设置键的单点）。
/// </para>
/// </summary>
public static class DesktopToggleCatalog
{
    // ===== 命令名（跨进程契约） =====

    /// <summary>桌面图标显隐。</summary>
    public const string Icons = "icons";

    /// <summary>隐藏任务栏。</summary>
    public const string Taskbar = "taskbar";

    /// <summary>双击隐藏图标。</summary>
    public const string DoubleClick = "doubleclick";

    /// <summary>菜单栏显隐。</summary>
    public const string MenuBar = "menubar";

    /// <summary>Dock 显隐。</summary>
    public const string Dock = "dock";

    /// <summary>热键侧板显隐。</summary>
    public const string HotkeyPanel = "hotkey-panel";

    /// <summary>灵动岛显隐（宿主内插件；IslandPlugin 订阅 island.* 键变化即时拆窗/重建）。</summary>
    public const string Island = "island";

    /// <summary>索引引擎启停（工具类常驻 L2）。</summary>
    public const string Index = "index";

    /// <summary>截图工具启停（常驻热键监听）。</summary>
    public const string Capture = "capture";

    /// <summary>剪贴板历史启停（工具类常驻 L2；关掉=面板/引擎进程退场）。</summary>
    public const string Clipboard = "clipboard";

    // ===== 设置键 =====

    /// <summary>桌面图标显隐键（值语义 = 已隐藏）。</summary>
    public const string IconsKey = "desktop.iconsHidden";

    /// <summary>原生任务栏显隐键（值语义 = 显示）。</summary>
    public const string TaskbarKey = "components.wintaskbar";

    /// <summary>双击隐藏图标键。</summary>
    public const string DoubleClickKey = "desktop.doubleClickHideIcons";

    /// <summary>菜单栏显隐键。</summary>
    public const string MenuBarKey = "components.menubar";

    /// <summary>Dock 显隐键。</summary>
    public const string DockKey = "components.dock";

    /// <summary>热键侧板显隐键（owner = shell-hotkey-panel/HotkeyPanelSettings.EnabledKey，键名须与之一致）。</summary>
    public const string HotkeyPanelKey = "hotkeys-panel.enabled";

    /// <summary>灵动岛显隐键（owner = shell-island/IslandOptions.EnabledKey，键名须与之一致）。</summary>
    public const string IslandKey = "island.enabled";

    /// <summary>
    /// 索引引擎启停键。**2026-09-17 新增**：此前索引引擎没有任何门控键，看门狗无条件守护它。
    /// 索引引擎自己的 <c>extensions.index</c> 配置节（engine-index/src/settings.rs）已存在，本键是它的兄弟键。
    /// </summary>
    public const string IndexKey = "extensions.index.enabled";

    /// <summary>
    /// 截图工具启停键（扩展中心 id = screenshot，见 shell-menu-bar/Contracts/ExtensionCatalog.cs，
    /// 遵循 <c>extensions.&lt;id&gt;.enabled</c> 约定）。
    /// </summary>
    public const string CaptureKey = "extensions.screenshot.enabled";

    /// <summary>
    /// 剪贴板历史启停键（owner = shell-clipboard/ClipboardPlugin.EnabledKey，键名须与之一致）。
    /// 【2026-09-17】托盘一直缺这一项 → 用户反馈"只有打开面板的入口、没有关掉这个功能的入口"，
    /// 补进共享表后托盘「功能开关」可直接开关它（关掉 = 面板/引擎进程退场，见 ClipboardPlugin.ApplyEnabledState）。
    /// </summary>
    public const string ClipboardKey = "extensions.clipboard-history.enabled";

    /// <summary>
    /// 用户"显式要求原生任务栏可见"的留痕键（默认 false = 从未显式要求）。
    /// 优先级规则见 <see cref="DesktopControlRules.ShouldShowNativeTaskbar"/>。
    /// </summary>
    public const string TaskbarExplicitVisibleKey = "desktop.taskbarExplicitVisible";

    private static readonly DesktopToggle[] All =
    [
        new(Icons, IconsKey, Default: false, RequiresHost: false, NativeEffective: true,
            DisplayName: "桌面图标显隐"),
        new(Taskbar, TaskbarKey, Default: true, RequiresHost: false, NativeEffective: true,
            DisplayName: "隐藏任务栏",
            ExplicitVisibleKey: TaskbarExplicitVisibleKey),
        new(DoubleClick, DoubleClickKey, Default: true, RequiresHost: true, NativeEffective: false,
            DisplayName: "双击隐藏图标"),
        new(MenuBar, MenuBarKey, Default: true, RequiresHost: true, NativeEffective: false,
            DisplayName: "菜单栏显隐"),
        // Dock 是"默认隐藏原生任务栏"的驱动方：它一变（关掉或重开）→ 复位任务栏的显式可见留痕。
        new(Dock, DockKey, Default: true, RequiresHost: true, NativeEffective: false,
            DisplayName: "Dock 显隐",
            ResetsExplicitKeys: [TaskbarExplicitVisibleKey]),
        new(HotkeyPanel, HotkeyPanelKey, Default: true, RequiresHost: true, NativeEffective: false,
            DisplayName: "热键侧板显隐"),
        new(Island, IslandKey, Default: true, RequiresHost: true, NativeEffective: false,
            DisplayName: "灵动岛"),

        // ===== 工具类常驻（2026-09-17 用户拍板纳入托盘控制面）=====
        // 这两项不在「桌面控制」菜单里，但共用同一张"命令名 → 设置键"表：CLI 的 --toggle-key 与
        // 托盘「功能开关」都消费它，避免各处再写一份映射（本文件头说的问题就是映射漂移）。
        // 开关只写设置，**实际启停由看门狗执行**（对应目标带 StopWhenDisabled：关掉即停进程）。
        new(Index, IndexKey, Default: true, RequiresHost: false, NativeEffective: false,
            DisplayName: "索引引擎"),
        new(Capture, CaptureKey, Default: true, RequiresHost: false, NativeEffective: false,
            DisplayName: "截图工具"),
        new(Clipboard, ClipboardKey, Default: true, RequiresHost: false, NativeEffective: false,
            DisplayName: "剪贴板历史"),
    ];

    /// <summary>全部开关（顺序 = 菜单里的展示顺序）。</summary>
    public static IReadOnlyList<DesktopToggle> Items => All;

    /// <summary>按命令名查（大小写不敏感，与 CLI/命令桥既有语义一致）；未知返回 false。</summary>
    public static bool TryGet(string name, out DesktopToggle toggle)
    {
        foreach (var item in All)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                toggle = item;
                return true;
            }
        }

        toggle = null!;
        return false;
    }

    /// <summary>
    /// 一次**用户显式翻转**要一并写入的留痕键（值 = 该开关翻到的新值）。
    /// <para>
    /// 【优先级规则 · 2026-09-17 用户拍板】「桌面控制」的显式选择 > 其它功能的默认隐藏效果。
    /// 现实里唯一的冲突是"dock 启用即默认隐藏原生任务栏"（2026-09-02 定稿的联动）。
    /// 旧规则只有单向优先级（显式隐藏压过 dock，显式显示却被 dock 压掉）→ 用户实测的错位：
    /// 菜单里「隐藏任务栏」显示"未勾（没隐藏）"，可任务栏其实是被 dock 藏起来的，再点只会更隐藏（像没反应）。
    /// </para>
    /// <para>
    /// 实现取舍：只需给"显式显示"留痕——显式隐藏本身就是压过默认的方向（直接写 false）；
    /// 该键为 false 时语义回到"从未显式要求"（dock 联动照旧生效）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Key, bool Value)> ExplicitOverrides(DesktopToggle spec, bool newValue)
    {
        var result = new List<(string Key, bool Value)>();

        if (spec.ExplicitVisibleKey is { } key)
        {
            result.Add((key, newValue));
        }

        // 复位：本开关一变，之前对**别的**开关做的显式覆盖即失效（用户拍板：Dock 一变就清任务栏留痕）。
        foreach (var resetKey in spec.ResetsExplicitKeys ?? [])
        {
            result.Add((resetKey, false));
        }

        return result;
    }
}
