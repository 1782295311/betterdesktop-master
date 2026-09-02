namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>菜单触发作用域（一个 Scope 对应一份 MenuTemplate 与一组贡献项）。</summary>
public enum MenuScope
{
    /// <summary>桌面空白处。</summary>
    Desktop,

    /// <summary>桌面图标（文件/文件夹/虚拟项）。</summary>
    DesktopIcon,

    /// <summary>任务栏空白处。</summary>
    Taskbar,

    /// <summary>Dock 固定项。</summary>
    DockItem,

    /// <summary>资源管理器内文件/文件夹（M2）。</summary>
    ShellFile,

    /// <summary>窗口标题栏/缩略图（M2）。</summary>
    Window,

    /// <summary>应用管理中心项（M3）。</summary>
    AppCenterItem,

    /// <summary>控制中心（M3）。</summary>
    ControlCenter,
}

/// <summary>
/// 通用基础菜单五区块，顺序恒定（第一菜单原则固化在此）：
/// ①常用操作组 → ②管理组 → ③插件贡献组 → ④系统组 → ⑤动态/原生组。
/// 高频项永远落 Common；贡献项（插件/自定义/转换）永远落 Contribution。
/// </summary>
public enum MenuGroup
{
    /// <summary>① 常用操作组（高频项一层直达，禁止收纳进子菜单）。</summary>
    Common,

    /// <summary>② 管理组（剪切/复制/删除/重命名等）。</summary>
    Manage,

    /// <summary>③ 插件贡献组（IContextMenuContributor 产出，按 Priority 排序）。</summary>
    Contribution,

    /// <summary>④ 系统组（设置/个性化等系统入口）。</summary>
    System,

    /// <summary>⑤ 动态/原生组（窗口列表/Shell 原生项，可无）。</summary>
    Dynamic,
}

/// <summary>菜单项种类。</summary>
public enum MenuItemKind
{
    /// <summary>普通命令项。</summary>
    Command,

    /// <summary>带子菜单（仅集合语义/动态列表允许，第一菜单原则）。</summary>
    Submenu,

    /// <summary>分隔线（由渲染器按区块自动插入，一般不手工声明）。</summary>
    Separator,

    /// <summary>开关项（带勾选态）。</summary>
    Toggle,

    /// <summary>单选项（带勾选态）。</summary>
    Radio,
}
