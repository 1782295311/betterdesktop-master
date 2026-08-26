using System.Collections.Generic;
using System.Windows.Controls;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// 布局宿主交互面（StartMenuWindow 使用）：搜索框 + 结果渲染 + 主列表刷新。
/// 布局负责自己渲染搜索结果与刷新主列表，窗口只做防抖调度与数据获取。
/// </summary>
internal interface IStartMenuLayoutHost
{
    /// <summary>搜索输入框。</summary>
    TextBox SearchBox { get; }

    /// <summary>渲染搜索结果（query 为空时还原默认视图）。</summary>
    void RenderResults(string query, IReadOnlyList<SearchResult> results);

    /// <summary>重建主列表（如卸载完成后）。</summary>
    void RefreshItems();
}
