using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// 单个运行中应用的聚合信息（按 AppItem 或可执行路径去重后的一个图标）。
/// </summary>
/// <param name="AppId">匹配到的应用主键；未匹配到已知应用时为 <see cref="AppItemId.Empty"/>。</param>
/// <param name="ExePath">可执行路径（未匹配时的去重键）。</param>
/// <param name="DisplayName">显示名（应用名或窗口标题）。</param>
/// <param name="Windows">该应用的所有窗口。</param>
public sealed record RunningAppInfo(
    AppItemId AppId,
    string ExePath,
    string DisplayName,
    IReadOnlyList<WindowInfo> Windows);
