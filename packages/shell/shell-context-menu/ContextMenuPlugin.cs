using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus;

/// <summary>
/// 右键菜单插件（context-menu）：向内核 Provide 统一菜单服务。
/// 场景模板与贡献项由消费者插件（shell-desktop/shell-convert…）经
/// IMenuService.RegisterTemplate / RegisterContributor 注册。
/// </summary>
public sealed class ContextMenuPlugin : IPlugin
{
    private MenuService? _service;
    private readonly List<IDisposable> _handles = [];

    public string Name => "context-menu";

    public IReadOnlyList<Type> Inject { get; } = [];

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var service = new MenuService();
        _service = service;
        context.Provide<IMenuService>(service);
        context.Provide<IFileClassifier>(new FileClassifier());

        // 用户自定义项（零代码 DIY 层）：每个 Scope 注册一个贡献者，Build 时读设置热更新。
        var settings = context.Get<ISettingsService>();
        if (settings is not null)
        {
            foreach (MenuScope scope in Enum.GetValues<MenuScope>())
            {
                var contributor = new UserMenuContributor(settings, scope);
                _handles.Add(service.RegisterContributor(contributor));
            }
        }
        else
        {
            DiagnosticLog.Trace("context-menu", "ISettingsService 缺失：用户自定义菜单项未启用");
        }

        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var handle in _handles)
        {
            try { handle.Dispose(); }
            catch { /* 注销失败不阻断（M10） */ }
        }
        _handles.Clear();
        _service?.Dismiss();
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
