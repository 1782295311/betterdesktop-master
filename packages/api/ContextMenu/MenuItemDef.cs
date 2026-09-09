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

    /// <summary>主题图标键（可空；M1 渲染暂不画图标，字段为 M2 预留）。</summary>
    public string? IconKey { get; init; }

    /// <summary>菜单项种类。</summary>
    public MenuItemKind Kind { get; init; } = MenuItemKind.Command;

    /// <summary>所属区块（贡献项必须声明，模板项由模板决定）。</summary>
    public MenuGroup Group { get; init; } = MenuGroup.Common;

    /// <summary>是否可执行（不可执行置灰保留位置，避免菜单跳动）。</summary>
    public bool IsEnabled { get; init; } = true;

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

    /// <summary>子菜单（Kind=Submenu 时有效；子项能力过滤递归生效）。</summary>
    public IReadOnlyList<MenuItemDef>? Children { get; init; }

    /// <summary>所需文件能力（0 = 不参与能力过滤）。</summary>
    public FileCapabilities RequiredCapability { get; init; } = FileCapabilities.None;
}
