// BetterDesktop.Shell.Desktop — 桌面/文件夹浏览器契约
// 自绘桌面的核心交互面：Location 可导航（默认 = 用户桌面），支持历史导航与对选中项的文件操作。
// 菜单栏左区（导航入口 + 工具条）仅依赖本契约（由 DesktopPlugin Provide），不感知实现。

using System;

namespace BetterDesktop.Shell.Desktop.Contracts;

/// <summary>桌面 / 文件夹浏览器（自绘桌面的图标层 + 导航 + 文件操作）。</summary>
public interface IDesktopBrowser
{
    /// <summary>当前浏览目录（完整路径）。默认 = 用户桌面目录。</summary>
    string Location { get; }

    /// <summary>默认浏览位置（用户桌面目录完整路径）。</summary>
    string DesktopPath { get; }

    /// <summary>当前选中项的完整路径集合（可能为空）。</summary>
    System.Collections.Generic.IReadOnlyList<string> SelectedPaths { get; }

    /// <summary>当前条目快照（ItemsChanged 后 UI 网格读取）。</summary>
    System.Collections.Generic.IReadOnlyList<BrowserEntry> Items { get; }

    /// <summary>当前选中集快照。</summary>
    System.Collections.Generic.IReadOnlyCollection<string> Selection { get; }

    /// <summary>当前是否有剪贴板内容可粘贴（本浏览器剪切/复制过）。</summary>
    bool CanPaste { get; }

    /// <summary>是否可后退。</summary>
    bool CanGoBack { get; }

    /// <summary>是否可前进。</summary>
    bool CanGoForward { get; }

    /// <summary>当前位置变化（导航/刷新后触发；已在 UI 线程）。</summary>
    event EventHandler<string>? LocationChanged;

    /// <summary>选中集变化（已在 UI 线程）。</summary>
    event EventHandler? SelectionChanged;

    /// <summary>条目枚举完成（UI 重建图标网格；已在 UI 线程）。</summary>
    event EventHandler? ItemsChanged;

    /// <summary>导航到指定目录（压入历史）。非目录时忽略。</summary>
    void Navigate(string path);

    bool Back();

    bool Forward();

    /// <summary>向上（父目录）；已在根时返回 false。</summary>
    bool Up();

    /// <summary>重新枚举当前位置。</summary>
    void Refresh();

    /// <summary>替换选中集（外部清空/同步用）。</summary>
    void SetSelection(System.Collections.Generic.IEnumerable<string> paths);

    /// <summary>剪切选中项（粘贴时移动）。</summary>
    void Cut();

    /// <summary>复制选中项（粘贴时复制）。</summary>
    void Copy();

    /// <summary>粘贴剪贴板内容到当前位置（本浏览器 Cut/Copy 过的内容）。</summary>
    void Paste();

    /// <summary>重命名唯一选中项为 newName；无/多项选中时忽略。</summary>
    void Rename(string newName);

    /// <summary>删除选中项（回收站，带系统确认）。</summary>
    void Delete();

    /// <summary>在当前浏览目录新建文件夹（自动生成唯一名"新建文件夹"/"新建文件夹 (2)"），完成后刷新。</summary>
    void NewFolder();

    /// <summary>在当前浏览目录新建文本文档（"新建文本文档.txt"，重名自增），完成后刷新。</summary>
    void CreateTextFile();

    /// <summary>当前排序键（null=智能默认；name/size/type/modified）。</summary>
    string? SortKey { get; }

    /// <summary>设置排序键并重新枚举（null 恢复智能默认）。持久化由设置层负责。</summary>
    void SetSort(string? key);

    /// <summary>把外部文件/目录导入当前浏览目录（拖放落点）。move=true 移动、false 复制；重名自动唯一化；完成后刷新。</summary>
    void ImportFiles(System.Collections.Generic.IEnumerable<string> paths, bool move);
}
