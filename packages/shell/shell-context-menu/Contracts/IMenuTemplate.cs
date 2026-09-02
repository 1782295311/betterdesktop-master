namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 场景菜单模板：声明某 Scope 下 ①常用②管理④系统⑤动态区块的内容。
/// ③贡献组由 IContextMenuContributor 自动填充，模板不负责。
/// </summary>
public interface IMenuTemplate
{
    /// <summary>适配的作用域。</summary>
    MenuScope Scope { get; }

    /// <summary>填充模板项（每次展示都会调用，可按 request 动态产出）。</summary>
    void Build(IMenuTemplateBuilder builder, MenuRequest request);
}

/// <summary>模板填充器（MenuService 提供；AddItem 的 Group 即区块归属）。</summary>
public interface IMenuTemplateBuilder
{
    void AddItem(MenuItemDef item);

    void AddSeparator(MenuGroup group);
}
