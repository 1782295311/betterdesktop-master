namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 系统右键菜单注入场景（注册表路径映射；与自绘 MenuScope 分离——本枚举只管系统注册表场景）。
/// </summary>
public enum MenuScene
{
    /// <summary>文件（HKCU\Software\Classes\*\shell）—— %1 = 选中文件。</summary>
    File,

    /// <summary>目录（HKCU\Software\Classes\Directory\shell）—— %1 = 选中目录。</summary>
    Directory,

    /// <summary>目录背景（HKCU\Software\Classes\Directory\Background\shell）—— 无 %1。</summary>
    Background,

    /// <summary>全部场景（文件 + 目录 + 背景）。</summary>
    All,
}

/// <summary>
/// 统一菜单功能声明（M3.1 统一注册体系 v1）：一个功能声明一次，三路输出同源——
/// ① 自绘菜单项：各主体产出 MenuItemDef 时填 <see cref="MenuItemDef.Action"/> = Action（标识对齐）；
/// ② 系统右键注册表 verb：ContextMenuRegistry 按 Scene 注入 HKCU，命令 = CLI --menu-cmd &lt;Action&gt;；
/// ③ CLI 动作路由键：Action 即 CLI 的 --menu-cmd 参数（headless 直执行 / 需宿主提示 / plugin 分派）。
/// 三处一致由「Action 标识」保证，消灭两套维护。
/// 对应 plugin.json 的 contextMenus 字段（v1 可选）：scene/verb/title/icon/action/args/extended。
/// </summary>
public sealed record ContextMenuContribution
{
    /// <summary>稳定功能标识（同场景内唯一；也作注册表 verb 键名）。</summary>
    public required string Id { get; init; }

    /// <summary>执行标识（CLI 路由键；= 注册表 verb = MenuItemDef.Action）。建议形如 convert-to-pdf / plugin:&lt;id&gt;:&lt;action&gt;。</summary>
    public required string Action { get; init; }

    /// <summary>菜单显示名（MUIVerb，≤80 字符红线）。</summary>
    public required string Title { get; init; }

    /// <summary>注入场景（默认 File）。</summary>
    public MenuScene Scene { get; init; } = MenuScene.File;

    /// <summary>图标路径（可空；写入注册表 Icon 值）。</summary>
    public string? IconPath { get; init; }

    /// <summary>命令参数模板（默认 ["%1"]；Background 场景应为空）。</summary>
    public string[] Args { get; init; } = ["%1"];

    /// <summary>Shift 扩展项（仅 Shift+右键显示，Win10 语义）。</summary>
    public bool Extended { get; init; }
}
