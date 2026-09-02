namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 统一右键菜单服务（所有表面共用，禁止各插件自绘菜单）。
/// 展示流程：模板 + 贡献项 → 组排序 → 能力过滤 → Opening 注入点 → MenuHost 渲染。
/// </summary>
public interface IMenuService
{
    /// <summary>展示菜单（必须在 UI 线程调用；返回的 Task 在菜单关闭时完成）。</summary>
    Task<MenuResult> ShowAsync(MenuRequest request, CancellationToken cancellationToken = default);

    /// <summary>幂等关闭全部活动菜单（可任意线程调用，内部编队回 UI 线程）。</summary>
    void Dismiss();

    /// <summary>注册贡献项（返回注销句柄；Priority 越大越靠前，同优先级按注册序稳定）。</summary>
    IDisposable RegisterContributor(IContextMenuContributor contributor);

    /// <summary>注册场景模板（同 Scope 后注册者覆盖先注册者；返回注销句柄）。</summary>
    IDisposable RegisterTemplate(IMenuTemplate template);

    /// <summary>展示前注入点（展示前最后修改菜单项的机会）。</summary>
    event EventHandler<MenuOpeningArgs>? Opening;
}
