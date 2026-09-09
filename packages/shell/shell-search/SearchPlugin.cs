using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Search.Services;

namespace BetterDesktop.Shell.Search;

// ============================================================
// 【白话导航 · 搜索域】凭白话需求定位到精确文件：
//   "开始菜单搜索的聚合入口"      → Services/StartMenuSearchService.cs（IStartMenuSearchService，合并各来源）
//   "搜已安装程序"               → Services/ProgramSearchProvider.cs
//   "搜文件"                     → Services/FileSearchProvider.cs
//   "搜设置项"                   → Services/SettingsSearchProvider.cs
//   "新增一个搜索来源"           → 实现 Contracts/ISearchResultProvider.cs（结果模型 Contracts/SearchResult.cs）
// ============================================================

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
