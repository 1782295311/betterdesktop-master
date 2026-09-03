using System.Windows;

namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 统一右键菜单服务（所有表面共用，禁止各插件自绘菜单）。
/// 展示流程：模板 + 贡献项 → 组排序 → 能力过滤 → Opening 注入点 → MenuHost 渲染。
/// </summary>
public interface IMenuService
{
    /// <summary>展示菜单（必须在 UI 线程调用；返回的 Task 在菜单关闭时完成）。</summary>
    Task<MenuResult> ShowAsync(MenuRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 低层入口：直接展示调用方构建的菜单项（跳过模板/贡献项管线）。
    /// 供 dock/开始菜单等非文件场景的按钮菜单、快捷菜单接入统一弹层；
    /// 渲染与样式仍由 MenuHost + MenuStyling 统一驱动（与模板路径完全同源）。
    /// </summary>
    Task<MenuResult> ShowAsync(IReadOnlyList<MenuItemDef> items, Point screenPos);

    /// <summary>幂等关闭全部活动菜单（可任意线程调用，内部编队回 UI 线程）。</summary>
    void Dismiss();

    /// <summary>当前是否有活动菜单（供失焦自毁型宿主豁免：弹层抢激活 ≠ 用户离开宿主）。</summary>
    bool IsOpen { get; }

    /// <summary>活动菜单关闭时触发（UI 线程；供宿主在豁免后补收尾，如开始菜单延迟隐藏）。</summary>
    event EventHandler? Closed;

    /// <summary>注册贡献项（返回注销句柄；Priority 越大越靠前，同优先级按注册序稳定）。</summary>
    IDisposable RegisterContributor(IContextMenuContributor contributor);

    /// <summary>注册场景模板（同 Scope 后注册者覆盖先注册者；返回注销句柄）。</summary>
    IDisposable RegisterTemplate(IMenuTemplate template);

    /// <summary>展示前注入点（展示前最后修改菜单项的机会）。</summary>
    event EventHandler<MenuOpeningArgs>? Opening;
}
