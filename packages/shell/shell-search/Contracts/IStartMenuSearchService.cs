using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterDesktop.Shell.Search.Contracts;

/// <summary>
/// 开始菜单搜索服务：聚合所有已注册 <see cref="ISearchResultProvider"/>，汇总排序后返回。
/// 输入防抖由 UI 层负责；SearchAsync 同步执行各 Provider（内存 / 索引查询），
/// 中途取消时返回空结果（M10）。
/// </summary>
public interface IStartMenuSearchService
{
    /// <summary>
    /// 搜索并返回按得分降序、类别与标题稳定的结果（上限约 30 条）。
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// 注册搜索源（同名单覆盖旧 Provider）。
    /// </summary>
    void RegisterProvider(ISearchResultProvider provider);
}
