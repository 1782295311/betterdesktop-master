// BetterDesktop.Shell.HotkeyPanel — 侧板显隐的设置键与判定（单一真相源，2026-09-17）
//
// 【用户需求】"增加热键侧边面板的启动和关闭"——侧板原来只会随宿主常驻，用户没有任何关闭入口。
// 现在**入口同源**（都只写这一个设置键，由 HotkeyPanelPlugin 统一应用）：
//   ① 【用户指定入口】桌面空白右键 →「桌面控制」→「热键侧板显隐」
//      （键名来自 DesktopToggleCatalog.HotkeyPanelKey，菜单与本插件共享同一个键）；
//   ② 侧板右键菜单「显示热键侧板」勾选项（可操作态下可达）；
//   ③ 可操作态底部「关闭侧板」文字入口（菜单万一不可达时的保险丝）；
//   ④ 设置中心「热键」分节的勾选框 / 全局热键 Ctrl+Alt+H。
// 把键名与默认值收在这里，避免多处各写一遍字符串（必然会漂移）。
//
// ⚠️ 【键名字面量两处必须一致】shell-desktop 不得反向依赖本包（依赖方向），故同一键在
//    BetterDesktop.Shell.Core.DesktopControl.DesktopToggleCatalog.HotkeyPanelKey 里另存一份字面量
//    （2026-09-17 目录单点化之前它写在 DesktopControlMenu.HotkeyPanelEnabledKey）——
//    **改这里的 EnabledKey 必须同步改目录那份**。本类单测（HotkeyPanelSettingsTests）钉住了字面量，
//    改错了会在测试里红。

using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>热键侧板的持久化设置（显隐）与切换热键标识。</summary>
internal static class HotkeyPanelSettings
{
    /// <summary>侧板显隐设置键（true = 常驻显示）。</summary>
    public const string EnabledKey = "hotkeys-panel.enabled";

    /// <summary>默认显示（保持既有行为：装好即常驻，不想看再关）。</summary>
    public const bool EnabledDefault = true;

    /// <summary>切换侧板显隐的全局热键 Id（注册进热键注册表，可在设置中心改键/停用）。</summary>
    public const string ToggleHotkeyId = "hotkeys-panel.toggle";

    /// <summary>切换热键默认键位（H = Hotkey 侧板；与仓库既有热键无冲突，冲突时注册表 fail-closed 会在侧板明示）。</summary>
    public const string ToggleHotkeyDefault = "Ctrl+Alt+H";

    /// <summary>切换热键的功能说明（侧板列表与设置中心正文用它）。</summary>
    public const string ToggleHotkeyDescription = "显示 / 隐藏热键侧板";

    /// <summary>读取显隐意图（设置服务缺失时按默认值——降级不静默改变既有行为）。</summary>
    public static bool IsEnabled(ISettingsService? settings)
        => settings?.Get(EnabledKey, EnabledDefault) ?? EnabledDefault;

    /// <summary>写入显隐意图（唯一写入口；应用动作由插件订阅 SettingsChanged 统一执行）。</summary>
    public static void SetEnabled(ISettingsService? settings, bool enabled)
        => settings?.Set(EnabledKey, enabled);
}
