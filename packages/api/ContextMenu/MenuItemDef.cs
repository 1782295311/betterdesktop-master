using System.Windows.Media;

namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 菜单项定义（跨场景统一数据模型）。
/// 能力过滤：RequiredCapability ≠ None 且 FileIdentity 不满足时，该项被隐藏（隐藏优先）。
/// </summary>
public sealed record MenuItemDef
{
    /// <summary>稳定标识（MenuResult 返回；同菜单内唯一）。</summary>
    public required string Id { get; init; }

    /// <summary>显示文本（≤ 60 字符；过长由渲染层截断）。</summary>
    public required string Text { get; init; }

    /// <summary>
    /// 菜单项图标（可空；必须是**已 Freeze** 的 ImageSource，渲染层按 16px 画）。
    /// <para>
    /// 【2026-09-17 用户实测】自绘右键菜单「打开方式」只列应用名、没有图标，用户认不出是哪个软件——
    /// 由产出方（如 DesktopIconsControl 经 MenuIconProvider）解析文件 / 应用 exe 图标后填入。
    /// 取不到图标时保持 null：渲染层**不占位**（绝不画空白占位破坏行高与对齐）。
    /// </para>
    /// </summary>
    public ImageSource? Icon { get; init; }

    /// <summary>主题图标键（可空；M1 渲染暂不画图标，字段为 M2 预留）。</summary>
    public string? IconKey { get; init; }

    /// <summary>菜单项种类。</summary>
    public MenuItemKind Kind { get; init; } = MenuItemKind.Command;

    /// <summary>所属区块（贡献项必须声明，模板项由模板决定）。</summary>
    public MenuGroup Group { get; init; } = MenuGroup.Common;

    /// <summary>是否可执行（不可执行置灰保留位置，避免菜单跳动）。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>高亮标记（无损转换项；渲染层用主题色/加粗强调，2026-09-10 新增——高亮=可无损转）。</summary>
    public bool Highlighted { get; init; }

    /// <summary>勾选态（Toggle/Radio 用）。</summary>
    public bool IsChecked { get; init; }

    /// <summary>默认动作加粗（如"打开"）。</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// 右侧快捷键文本（如 "Ctrl+C" / "F2" / "Shift+Delete"）。
    /// 红线：只写**真实响应**的键——写了不响应 = 放假提示（计划 ⑤ 保证键盘处理器同步落地）。
    /// </summary>
    public string? GestureText { get; init; }

    /// <summary>
    /// Shift 扩展项（Win10 语义）：仅 Shift+右键（或设置 context-menu.extended.always=常驻）时显示。
    /// 低频/危险项的正确归宿是 Shift 扩展，**不是**二级收纳。
    /// </summary>
    public bool Extended { get; init; }

    /// <summary>点击回调（Kind=Command/Toggle/Radio 时应提供；Submenu 忽略）。</summary>
    public Action? Command { get; init; }

    /// <summary>
    /// 稳定执行标识（M3.1 统一注册体系：跨自绘/系统/CLI 同源）。
    /// = 系统注册表 verb 名 = CLI --menu-cmd 动作路由键（如 "convert-to-pdf" / "plugin:&lt;id&gt;:&lt;action&gt;"）。
    /// 宿主在线时自绘菜单直接走 <see cref="Command"/> 进程内执行；本字段保证与系统菜单/CLI 路径同一功能同一标识。
    /// </summary>
    public string? Action { get; init; }

    /// <summary>子菜单（Kind=Submenu 时有效；子项能力过滤递归生效）。</summary>
    public IReadOnlyList<MenuItemDef>? Children { get; init; }

    /// <summary>所需文件能力（0 = 不参与能力过滤）。</summary>
    public FileCapabilities RequiredCapability { get; init; } = FileCapabilities.None;
}
