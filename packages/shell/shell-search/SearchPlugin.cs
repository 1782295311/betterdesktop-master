using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.IndexIpc;
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
    private IndexIpcClient? _indexClient;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var appSource = context.Get<IAppSourceService>()!;

        // 【M3b · 2026-09-14】文件搜索的引擎后端：常驻索引取代每次查询的磁盘兜底扫描。
        // 每个消费者各自持有一条管道连接（引擎 IPC server 支持并发连接：
        // 宿主 / 应用源 / 搜索各一条）。装配失败**绝不阻断**搜索——只记 Warn 走本地实现。
        _indexClient = TryAssembleIndexClient(context);

        _service = new StartMenuSearchService();
        _service.RegisterProvider(new ProgramSearchProvider(appSource, context.Logger));
        _service.RegisterProvider(new SettingsSearchProvider());
        _service.RegisterProvider(new FileSearchProvider(context.Logger, _indexClient));

        context.Provide<IStartMenuSearchService>(_service);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 装配索引引擎客户端；失败返回 <c>null</c>（不抛、不阻断搜索）。
    /// <para>门控与 <c>AppSourcePlugin</c> 同一语义：环境变量 <c>BETTERDESKTOP_INDEX_BACKEND=local</c> 强制走本地，
    /// 与仓库既有 <c>BETTERDESKTOP_*</c> 零依赖门控惯例一致（设置中心可见开关归 M5）。</para>
    /// </summary>
    private static IndexIpcClient? TryAssembleIndexClient(IContext context)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("BETTERDESKTOP_INDEX_BACKEND"),
                "local",
                StringComparison.OrdinalIgnoreCase))
        {
            context.Logger.Warn("[search] 索引后端被环境变量强制为 local → 文件搜索走本地实现");
            return null;
        }

        try
        {
            IndexEngineLauncher.EnsureEngine(msg => context.Logger.Warn($"[search] {msg}"));
            // CA2000：transport 的所有权**移交**给 IndexIpcClient（其 Dispose 会 Dispose transport）——
            // 分析器识别不了这种"构造期所有权转移"，故局部豁免（与剪贴板 / app-source 侧同写法）。
#pragma warning disable CA2000
            var client = new IndexIpcClient(new NamedPipeTransport());
#pragma warning restore CA2000
            client.Connect();
            context.Logger.Info("[search] 索引引擎客户端已装配（文件搜索后端=engine）");
            return client;
        }
        catch (Exception e)
        {
            context.Logger.Warn($"[search] 索引引擎客户端装配失败（{e.Message}）→ 文件搜索走本地实现");
            return null;
        }
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service = null;
        // 只断开客户端，不关停引擎进程（引擎是常驻服务，退出策略归宿主/launcher——与剪贴板同纪律）
        _indexClient?.Dispose();
        _indexClient = null;
        return Task.CompletedTask;
    }
}
