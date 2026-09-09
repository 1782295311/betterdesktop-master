// BetterDesktop.Api — 开始菜单数据契约（2026-09-08 独立化新增）
// 从 StartMenuService 实现类中抽出的"布局数据面"：布局/栏目录扩展点（IStartMenuLayoutProvider /
// IStartMenuSectionProvider）只依赖本契约，不再依赖实现类——第三方布局提供者无需了解
// StartMenuService 内部实现，仅凭数据契约即可构建自己的开始菜单布局。
// 契约方法均为 StartMenuService 已公开能力（数据聚合 + 动作收口），零新实现。

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.StartMenu.Contracts;

/// <summary>
/// 开始菜单数据契约：布局扩展点可用的全部数据聚合与动作收口。
/// 由 StartMenuService 实现并注入各布局/栏目提供者。
/// </summary>
public interface IStartMenuDataService
{
    /// <summary>当前主题令牌（布局配色/字号的唯一来源；无主题时为 null）。</summary>
    IThemeTokens? ThemeTokens { get; }

    /// <summary>统一设置服务（布局/栏目读写 startmenu.* 键）。</summary>
    ISettingsService Settings { get; }

    /// <summary>最近程序显示条数（设置 startmenu.recent-count）。</summary>
    int GetRecentCount();

    /// <summary>全部应用（开始菜单 + 已安装 + 全盘大盘点结果）。</summary>
    IReadOnlyList<AppItem> GetAllApps();

    /// <summary>固定到开始菜单的应用（截断到 cap 条）。</summary>
    IReadOnlyList<AppItem> GetPinnedStartMenuApps(int cap);

    /// <summary>开始菜单 "Programs" 文件夹层级树（经典布局左栏程序树）。</summary>
    ProgramFolder GetProgramTree();

    /// <summary>已注册的栏目扩展提供者（右栏按注册顺序渲染）。</summary>
    IReadOnlyList<IStartMenuSectionProvider> GetSectionProviders();

    /// <summary>切换布局（"win7" / "win10" / "win11" / "classic"）。</summary>
    void ShowLayout(string name);

    /// <summary>当前布局名（设置 startmenu.style）。</summary>
    string GetMenuStyle();

    /// <summary>激活或启动应用（还原最小化 + 置前；未运行则启动）。</summary>
    void ActivateOrLaunch(AppItem app);

    /// <summary>关闭开始菜单。</summary>
    void Hide();

    /// <summary>取应用图标（高清优先，带缓存）。</summary>
    Task<ImageSource?> GetIconAsync(AppItem app, CancellationToken ct = default);

    /// <summary>当前用户显示名（开始菜单用户区）。</summary>
    string GetUserName();

    /// <summary>打开设置窗口。</summary>
    void OpenSettings();

    /// <summary>强制刷新当前布局（主题/固定项变化后重绘）。</summary>
    void RefreshLayout();

    /// <summary>检测到的新安装应用（相对上次已见快照）。</summary>
    IReadOnlyList<AppItem> GetNewlyInstalledApps();

    /// <summary>最近打开的程序（按最近访问排序，截断到 count 条）。</summary>
    IReadOnlyList<RecentItem> GetRecentPrograms(int count);
}

