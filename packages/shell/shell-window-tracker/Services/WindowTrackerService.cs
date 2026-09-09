using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.WindowTracker.Contracts;
using BetterDesktop.Shell.WindowTracker.Native;

namespace BetterDesktop.Shell.WindowTracker.Services;

/// <summary>
/// 运行中窗口 / 应用追踪服务实现。
/// 枚举可见窗口（复用 RunningAppDetector），按 IAppSourceService 的应用路径关联到 AppItem，
/// 通过 WinEvent 钩子监听窗口变化并防抖后发出 RunningAppsChanged；前台变化发出 ForegroundWindowChanged。
/// 所有对外方法均 try-catch，异常记日志不冒泡（M10）。
/// </summary>
public sealed class WindowTrackerService : IWindowTrackerService, IDisposable
{
    private readonly IAppSourceService _appSource;
    private readonly IKernelLogger _logger;
    private readonly WinEventPump _pump;
    private readonly Timer _debounce;
    private readonly SynchronizationContext? _sync;
    private bool _disposed;

    public WindowTrackerService(IAppSourceService appSource, IKernelLogger logger)
    {
        _appSource = appSource;
        _logger = logger;
        _sync = SynchronizationContext.Current;
        _debounce = new Timer(_ => RaiseRunningChanged());
        // 7435 收口：统一走 shell-core WinEventPump（专用 STA 泵线程）。
        // 原实现（Native/WinEventHook）在构造线程直接注册、无泵线程 → 回调依赖调用方消息泵，
        // 线程池构造场景回调永不触发（变体 C 反例）；改接公共泵后事件稳定送达（修复）。
        _pump = new WinEventPump();
        // 窗口集合变化：EVENT_OBJECT_CREATE..NAMECHANGE（0x8000-0x800C）
        _pump.Subscribe(0x8000, 0x800C, (_, _) => OnWindowsChanged());
        // 前台窗口切换：EVENT_SYSTEM_FOREGROUND（0x0003）
        _pump.Subscribe(0x0003, 0x0003, (_, hwnd) => OnForegroundChanged(hwnd));
    }

    /// <inheritdoc />
    public event EventHandler<RunningAppsChangedEventArgs>? RunningAppsChanged;

    /// <inheritdoc />
    public event EventHandler<IntPtr>? ForegroundWindowChanged;

    /// <inheritdoc />
    public string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            return RunningAppDetector.GetWindowText(hwnd) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RunningAppInfo> GetRunningApps()
    {
        try
        {
            var apps = _appSource.ScanStartMenu()
                .Concat(_appSource.ScanInstalledApps())
                .ToList();

            var byPath = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps)
            {
                if (!string.IsNullOrEmpty(a.TargetPath))
                {
                    byPath[a.TargetPath] = a;
                }

                if (!string.IsNullOrEmpty(a.ShortcutPath) && !byPath.ContainsKey(a.ShortcutPath))
                {
                    byPath[a.ShortcutPath] = a;
                }
            }

            var map = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in RunningAppDetector.GetRunningWindows())
            {
                byPath.TryGetValue(w.ExePath, out var app);
                var key = app is null ? w.ExePath : app.Id.Value;

                List<WindowInfo> windows;
                if (!map.TryGetValue(key, out var info))
                {
                    windows = new List<WindowInfo>();
                    info = new RunningAppInfo(
                        app?.Id ?? AppItemId.Empty,
                        w.ExePath,
                        app?.Name ?? w.Title,
                        windows);
                    map[key] = info;
                }
                else
                {
                    windows = (List<WindowInfo>)info.Windows;
                }

                windows.Add(new WindowInfo(w.Hwnd, w.ProcessId, w.Title, IsMinimized(w.Hwnd)));
            }

            return map.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] GetRunningApps 失败: {ex.Message}");
            return Array.Empty<RunningAppInfo>();
        }
    }

    /// <inheritdoc />
    public bool IsRunning(AppItemId appId)
    {
        try
        {
            return GetRunningApps().Any(a => a.AppId == appId);
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] IsRunning 失败: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public bool Activate(AppItemId appId)
    {
        try
        {
            var info = GetRunningApps().FirstOrDefault(a => a.AppId == appId);
            if (info is null || info.Windows.Count == 0)
            {
                return false;
            }

            RunningAppDetector.ActivateWindow(info.Windows[0].Hwnd);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] Activate 失败: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowInfo> GetWindowsOfApp(AppItemId appId)
    {
        try
        {
            return GetRunningApps().FirstOrDefault(a => a.AppId == appId)?.Windows
                   ?? Array.Empty<WindowInfo>();
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] GetWindowsOfApp 失败: {ex.Message}");
            return Array.Empty<WindowInfo>();
        }
    }

    private static bool IsMinimized(IntPtr hwnd)
    {
        RunningAppDetector.GetWindowPlacement(hwnd, out var placement);
        // SW_SHOWMINIMIZED = 2
        return placement.showCmd == 2;
    }

    private void OnWindowsChanged()
    {
        // 防抖 500ms：连续窗口事件只触发一次重枚举
        try
        {
            _debounce.Change(500, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] 防抖调度失败: {ex.Message}");
        }
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        RaiseForegroundChanged(hwnd);
    }

    private void RaiseForegroundChanged(IntPtr hwnd)
    {
        if (_sync is not null)
        {
            _sync.Post(_ => ForegroundWindowChanged?.Invoke(this, hwnd), null);
        }
        else
        {
            ForegroundWindowChanged?.Invoke(this, hwnd);
        }
    }

    private void RaiseRunningChanged()
    {
        try
        {
            var apps = GetRunningApps();
            var args = new RunningAppsChangedEventArgs { Apps = apps };
            if (_sync is not null)
            {
                _sync.Post(_ => RunningAppsChanged?.Invoke(this, args), null);
            }
            else
            {
                RunningAppsChanged?.Invoke(this, args);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[WindowTracker] RaiseRunningChanged 失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _pump.Dispose();
            _debounce.Dispose();
        }
        catch
        {
            // 忽略清理异常
        }
    }
}
