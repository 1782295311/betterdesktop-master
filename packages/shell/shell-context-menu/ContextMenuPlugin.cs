using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;

namespace BetterDesktop.Shell.ContextMenus;

/// <summary>
/// 右键菜单插件（context-menu）：向内核 Provide 统一菜单服务。
/// 场景模板与贡献项由消费者插件（shell-desktop/shell-convert…）经
/// IMenuService.RegisterTemplate / RegisterContributor 注册。
/// </summary>
public sealed class ContextMenuPlugin : IPlugin
{
    private MenuService? _service;

    public string Name => "context-menu";

    public IReadOnlyList<Type> Inject { get; } = [];

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var service = new MenuService();
        _service = service;
        context.Provide<IMenuService>(service);
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service?.Dismiss();
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
