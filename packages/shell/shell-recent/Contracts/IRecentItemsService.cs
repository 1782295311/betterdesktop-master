using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Recent.Contracts;

/// <summary>
/// 最近项服务：最近程序（本地使用计数，随前台窗口变化自动记录）、
/// 最近文档（读 Windows Recent 目录）、跳转列表固定项（按宿主应用分区持久化）。
/// 所有方法 try-catch，异常记日志不冒泡（M10）。
/// </summary>
public interface IRecentItemsService
{
    /// <summary>返回最近使用的程序（按使用次数与时间降序），最多 count 条。</summary>
    IReadOnlyList<RecentItem> GetRecentPrograms(int count);

    /// <summary>返回最近打开的文档（按最近写入时间降序），最多 count 条。</summary>
    IReadOnlyList<RecentItem> GetRecentDocuments(int count);

    /// <summary>记录一次程序使用（自增计数并更新最近时间，持久化）。</summary>
    void RecordProgramUse(AppItem app);

    /// <summary>返回指定宿主应用的跳转列表固定项（AppItemId 列表，按固定顺序）。</summary>
    IReadOnlyList<AppItemId> GetJumpListPinned(string appId);

    /// <summary>设置指定宿主应用的跳转列表固定项（覆盖并持久化）。</summary>
    void SetJumpListPinned(string appId, IReadOnlyList<AppItemId> pinned);
}
