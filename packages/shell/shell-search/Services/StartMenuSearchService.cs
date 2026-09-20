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

        // 【2026-09-17 分组展示改造】不再全局 Score 降序 + Take(30) 截断：
        //  - 不截断：所有适配结果全量返回（各 Provider 已全量收集），UI 分组折叠 + 筛选消费；
        //  - 组序：App 固定第一（程序优先级最前）；其余类别按组内命中数升序（结果少的类别放前，
        //    同数 Settings 在 File 前，保稳定）；组内 Score 降序 → Title 升序。
        // 说明：组序是「类别优先级」不是「逐条置信度」——某组命中多不代表它该排前，
        //    少而精确的类别（如设置 2 条 vs 文件 40 条）放前面让用户先看到精匹配。
        var ordered = results
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => GroupRank(g.Key, g.Count()))
            .SelectMany(g => g
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(ordered);
    }

    /// <summary>
    /// 组排序键：App 固定 (0,0,0) 恒第一；其余 (1, 命中数, Settings=0/File=1)
    /// ——结果少的类别放前，同数时 Settings 在 File 前（元组按序比较）。
    /// </summary>
    private static (int, int, int) GroupRank(string category, int count) =>
        category.Equals("App", StringComparison.OrdinalIgnoreCase) ? (0, 0, 0)
        : (1, count, category.Equals("Settings", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
}
