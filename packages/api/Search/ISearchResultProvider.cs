using System.Collections.Generic;
using System.Threading;

namespace BetterDesktop.Shell.Search.Contracts;

/// <summary>
/// 搜索结果源扩展点：任何模块可注册一个 Provider 参与开始菜单搜索。
/// Search 必须同步返回结果并遵守 CancellationToken（M10：内部异常记日志不冒泡）。
/// </summary>
public interface ISearchResultProvider
{
    /// <summary>Provider 唯一名（注册时同名单覆盖）。</summary>
    string Name { get; }

    /// <summary>执行搜索并返回按得分降序的结果（未匹配返回空列表）。</summary>
    IReadOnlyList<SearchResult> Search(string query, CancellationToken ct);
}
