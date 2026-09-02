using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Convert.Services;

namespace BetterDesktop.Shell.Convert;

/// <summary>
/// 文档转换插件（shell-convert）：经 IContextMenuContributor 把「转 PDF」平铺进文件右键菜单
/// （贡献者扩展点组件消费者；转换执行与菜单解耦——shell-convert 仅依赖 shell-context-menu 契约）。
/// </summary>
public sealed class ConvertPlugin : IPlugin
{
    private readonly List<IDisposable> _handles = [];

    public string Name => "shell-convert";

    public IReadOnlyList<Type> Inject { get; } = [];

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var menus = context.Get<IMenuService>();
        if (menus is null)
        {
            DiagnosticLog.Trace("shell-convert", "IMenuService 缺失：转换菜单未启用（不影响转换服务）");
            return Task.CompletedTask;
        }

        var events = context.Get<IEventBus>();
        var service = new DocumentConversionService(events);
        _handles.Add(menus.RegisterContributor(new ConvertToPdfContributor(service, MenuScope.DesktopIcon)));
        _handles.Add(menus.RegisterContributor(new ConvertToPdfContributor(service, MenuScope.ShellFile)));
        DiagnosticLog.Trace("shell-convert", $"已注册「转 PDF」贡献项（引擎可用={ConvertEngineLocator.HasPdfEngine()}）");
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
        return Task.CompletedTask;
    }
}
