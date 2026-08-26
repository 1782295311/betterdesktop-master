using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// 单个运行中应用的聚合信息（按 AppItem 或可执行路径去重后的一个图标）。
/// </summary>
public sealed record RunningAppInfo(
    /// <summary>匹配到的应用主键；未匹配到已知应用时为 <see cref="AppItemId.Empty"/>。</summary>
    AppItemId AppId,
    /// <summary>可执行路径（未匹配时的去重键）。</summary>
    string ExePath,
    /// <summary>显示名（应用名或窗口标题）。</summary>
    string DisplayName,
    /// <summary>该应用的所有窗口。</summary>
    IReadOnlyList<WindowInfo> Windows);
