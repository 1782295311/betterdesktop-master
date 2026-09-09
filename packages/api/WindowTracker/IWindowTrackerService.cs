using System;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// 运行中窗口 / 应用追踪服务。
/// 从 Dock 抽出，供 Dock（运行指示器）、未来开始菜单（运行标记/点击激活）、任务栏（窗口列表/预览）共用。
/// 所有方法均 try-catch，异常记日志不冒泡（M10）。
/// </summary>
public interface IWindowTrackerService
{
    /// <summary>返回当前所有运行中应用（按 AppItem 或可执行路径聚合）。</summary>
    IReadOnlyList<RunningAppInfo> GetRunningApps();

    /// <summary>指定应用是否正在运行。</summary>
    bool IsRunning(AppItemId appId);

    /// <summary>激活指定应用的第一个窗口（还原最小化 + 置前），成功返回 true。</summary>
    bool Activate(AppItemId appId);

    /// <summary>返回指定应用的所有窗口。</summary>
    IReadOnlyList<WindowInfo> GetWindowsOfApp(AppItemId appId);

    /// <summary>运行中应用集合变化时触发（窗口创建/销毁/标题变化，防抖后）。</summary>
    event EventHandler<RunningAppsChangedEventArgs>? RunningAppsChanged;

    /// <summary>前台窗口变化时触发（传入前台窗口句柄）。</summary>
    event EventHandler<IntPtr>? ForegroundWindowChanged;

    /// <summary>取指定窗口的标题（GetWindowText 收口）。无标题/无效句柄返回空串。</summary>
    string GetWindowTitle(IntPtr hwnd);
}
