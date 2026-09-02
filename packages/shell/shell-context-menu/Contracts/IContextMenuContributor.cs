namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 插件贡献菜单项契约（右键菜单扩展点）。
/// 实现者：shell-dock（固定到 Dock）、shell-convert（转 PDF）、UserMenuContributor（用户自定义项）等。
/// </summary>
public interface IContextMenuContributor
{
    /// <summary>贡献目标作用域。</summary>
    MenuScope Scope { get; }

    /// <summary>排序优先级，越大越靠前；同优先级按注册顺序稳定排序。</summary>
    int Priority { get; }

    /// <summary>构建贡献项（在菜单展示前同步调用；抛异常的贡献者仅被跳过，不影响整体）。</summary>
    IReadOnlyList<MenuItemDef> Build(MenuRequest request);
}
