using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Search.Services;

namespace BetterDesktop.Shell.Search;

/// <summary>
/// 开始菜单搜索插件：注册内置 Provider（程序 / 设置 / 文件）并 Provide IStartMenuSearchService。
/// 消费方（开始菜单搜索框等）通过 Inject 获取服务即可统一搜索三类结果。
/// </summary>
public sealed class SearchPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.search";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private StartMenuSearchService? _service;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var appSource = context.Get<IAppSourceService>()!;
        _service = new StartMenuSearchService();
        _service.RegisterProvider(new ProgramSearchProvider(appSource, context.Logger));
        _service.RegisterProvider(new SettingsSearchProvider());
        _service.RegisterProvider(new FileSearchProvider(context.Logger));

        context.Provide<IStartMenuSearchService>(_service);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service = null;
        return Task.CompletedTask;
    }
}
