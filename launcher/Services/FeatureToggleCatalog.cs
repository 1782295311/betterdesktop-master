// BetterDesktop 启动器 —— 用户可见功能开关目录（展示 + 落地路径）。
//
// 【单一真相源纪律】这些键名/默认值不是本文件"发明"的，逐条对齐：
//   · packages/shell/shell-core/DesktopControl/DesktopToggleCatalog.cs —— 命令名 → 键/默认 的权威映射
//   · tray/TrayApplicationContext.cs:19-45 —— 托盘「功能开关」菜单（用户已经熟悉这一份清单）
//   · packages/shell/shell-menu-bar/Contracts/ExtensionCatalog.cs —— 菜单栏扩展与系统功能
// 本目录只做"给启动器 UI 用"的投影，**不新增语义**；键或默认值漂移时以那三处为准。
//
// 【两种落地语义，不要混用】
//   · FlipArgs（翻转）：CLI 的 --toggle-key / --toggle-desktop 是**翻转**语义（读当前值再取反）。
//     因此调用方必须先比对当前值，只在"不一致"时才调用 —— 否则会把用户的选择弄反。
//   · EnableArgs/DisableArgs（幂等设定）：系统整合的 register/unregister 是幂等动词，直接按意图调用。

namespace BetterDesktop.Launcher.Services;

/// <summary>一个用户可见开关。</summary>
/// <param name="Label">显示名。</param>
/// <param name="Description">一句话说明（做什么用的）。</param>
/// <param name="Key">settings.json 键名（读取当前值 / 比对用）。</param>
/// <param name="Default">从未设置过时的默认值（与权威映射一致）。</param>
/// <param name="FlipArgs">翻转式落地（CLI 参数）；null = 不用翻转语义。</param>
/// <param name="EnableArgs">幂等"打开"（CLI 参数）；null = 不用。</param>
/// <param name="DisableArgs">幂等"关闭"（CLI 参数）；null = 不用。</param>
internal sealed record FeatureToggle(
    string Label,
    string Description,
    string Key,
    bool Default,
    string[]? FlipArgs = null,
    string[]? EnableArgs = null,
    string[]? DisableArgs = null)
{
    /// <summary>是否走"翻转"落地（见文件头说明）。</summary>
    public bool IsFlip => FlipArgs is not null;
}

/// <summary>开关目录。</summary>
internal static class FeatureToggleCatalog
{
    /// <summary>全部开关（顺序即 UI 展示顺序：外壳 → 桌面 → 能力）。</summary>
    public static IReadOnlyList<FeatureToggle> All { get; } = new[]
    {
        new FeatureToggle(
            "顶部菜单栏", "顶部的状态栏与功能入口（音量 / 网络 / 时间等）",
            "components.menubar", true, FlipArgs: new[] { "--toggle-key", "menubar" }),
        new FeatureToggle(
            "底部 Dock", "底部应用坞（固定图标 / 运行中窗口预览）",
            "components.dock", true, FlipArgs: new[] { "--toggle-key", "dock" }),
        new FeatureToggle(
            "自绘桌面", "用自绘图标网格接管桌面（关掉即用回原生桌面）",
            "components.desktop", true, FlipArgs: new[] { "--toggle-desktop" }),
        new FeatureToggle(
            "任务栏外观", "接管系统任务栏外观（与 Dock 联动）",
            "components.wintaskbar", true, FlipArgs: new[] { "--toggle-key", "taskbar" }),
        new FeatureToggle(
            "隐藏桌面图标", "隐藏桌面上的原生图标（自绘桌面生效）",
            "desktop.iconsHidden", false, FlipArgs: new[] { "--toggle-key", "icons" }),
        new FeatureToggle(
            "双击隐藏图标", "双击桌面空白处隐藏 / 恢复图标",
            "desktop.doubleClickHideIcons", true, FlipArgs: new[] { "--toggle-key", "doubleclick" }),
        new FeatureToggle(
            "热键侧板", "按热键唤出的可操作侧板（Ctrl+Alt+H）",
            "hotkeys-panel.enabled", true, FlipArgs: new[] { "--toggle-key", "hotkey-panel" }),
        new FeatureToggle(
            "灵动岛", "媒体 / 剪贴板 / 转换等活动的浮层提示",
            "island.enabled", true, FlipArgs: new[] { "--toggle-key", "island" }),
        new FeatureToggle(
            "剪贴板历史", "记录剪贴板历史，搜索 / 收藏 / 一键粘贴（Ctrl+Shift+V）",
            "extensions.clipboard-history.enabled", true, FlipArgs: new[] { "--toggle-key", "clipboard" }),
        new FeatureToggle(
            "截图工具", "区域 / 全屏截屏与标注（默认 Win+Shift+B）",
            "extensions.screenshot.enabled", true, FlipArgs: new[] { "--toggle-key", "capture" }),
        new FeatureToggle(
            "搜索索引", "为全局搜索建立文件索引（关掉则只搜程序与设置）",
            "extensions.index.enabled", true, FlipArgs: new[] { "--toggle-key", "index" }),
        new FeatureToggle(
            "系统右键菜单", "资源管理器右键里的 BetterDesktop 菜单（格式转换 / 复制路径等）",
            "shellmenu.comExtension", true,
            EnableArgs: new[] { "--system-integration", "register" },
            DisableArgs: new[] { "--system-integration", "unregister" }),
    };

    /// <summary>"推荐默认"：全部取各自默认值（供界面上的「恢复推荐」按钮使用）。</summary>
    public static IReadOnlyDictionary<string, bool> RecommendedDefaults()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var toggle in All)
        {
            map[toggle.Key] = toggle.Default;
        }

        return map;
    }
}
