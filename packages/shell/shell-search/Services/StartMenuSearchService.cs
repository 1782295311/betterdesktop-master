using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.Search.Services;

/// <summary>
/// 开始菜单搜索服务：聚合所有已注册 Provider，汇总、排序、截断返回。
/// SearchAsync 同步执行各 Provider（内存 / 索引查询）；输入防抖由 UI 层负责（M10：单 Provider 异常隔离）。
/// </summary>
public sealed class StartMenuSearchService : IStartMenuSearchService
{
    private readonly List<ISearchResultProvider> _providers = new();

    /// <inheritdoc />
    public void RegisterProvider(ISearchResultProvider provider)
    {
        if (provider is null)
        {
            return;
        }

        lock (_providers)
        {
            // 同名单覆盖，允许热替换内置 Provider。
            _providers.RemoveAll(p => p.Name == provider.Name);
            _providers.Add(provider);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        ISearchResultProvider[] providers;
        lock (_providers)
        {
            providers = _providers.ToArray();
        }

        var results = new List<SearchResult>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(provider.Search(query, ct));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
            }
            catch
            {
                // 单个 Provider 异常不阻断整体搜索。
            }
        }

        var ranked = results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(ranked);
    }
}
