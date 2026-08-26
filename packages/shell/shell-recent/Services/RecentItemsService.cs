using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.Recent.Services;

/// <summary>
/// 最近项服务实现。
/// - 最近程序：随 <see cref="IWindowTrackerService.ForegroundWindowChanged"/> 自动记录（前台 hwnd → 运行中应用 → 解析 AppItem → 计数），
///   持久化到 %APPDATA%/BetterDesktop/recent-programs.json；
/// - 最近文档：读 %APPDATA%/Microsoft/Windows/Recent 下的 .lnk（按最近写入时间降序）；
/// - 跳转列表固定项：持久化到 %APPDATA%/BetterDesktop/recent-jumplist.json（按宿主应用分区）。
/// </summary>
public sealed class RecentItemsService : IRecentItemsService, IDisposable
{
    private const int MaxTrackedPrograms = 30;

    private readonly IAppSourceService _appSource;
    private readonly IWindowTrackerService _windowTracker;
    private readonly IKernelLogger _logger;
    private readonly string _programsPath;
    private readonly string _jumplistPath;

    private readonly object _sync = new();
    private List<ProgramUsage> _programs = new();
    private Dictionary<string, List<string>> _jumplist = new();
    private IntPtr _lastForegroundHwnd;
    private string _lastRecordedAppId = string.Empty;

    public RecentItemsService(
        IAppSourceService appSource,
        IWindowTrackerService windowTracker,
        IKernelLogger logger,
        string? dataDirectory = null)
    {
        _appSource = appSource ?? throw new ArgumentNullException(nameof(appSource));
        _windowTracker = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var appData = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");
        _programsPath = Path.Combine(appData, "recent-programs.json");
        _jumplistPath = Path.Combine(appData, "recent-jumplist.json");

        LoadPrograms();
        LoadJumplist();
        _windowTracker.ForegroundWindowChanged += OnForegroundChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<RecentItem> GetRecentPrograms(int count)
    {
        lock (_sync)
        {
            return _programs
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.LastUsed)
                .Take(Math.Max(0, count))
                .Select(x => new RecentItem
                {
                    Name = x.Name,
                    Path = x.TargetPath,
                    Kind = "Program",
                    LastUsed = x.LastUsed,
                    AppItem = new AppItem
                    {
                        Id = new AppItemId(x.AppId),
                        Name = x.Name,
                        ShortcutPath = x.ShortcutPath,
                        TargetPath = x.TargetPath,
                        Source = BetterDesktop.Shell.AppSource.Models.AppSource.Installed
                    }
                })
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecentItem> GetRecentDocuments(int count)
    {
        try
        {
            var recentDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Recent");
            if (!Directory.Exists(recentDir))
            {
                return Array.Empty<RecentItem>();
            }

            return Directory.EnumerateFiles(recentDir, "*.lnk")
                .Select(path => new RecentItem
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    Kind = "Document",
                    LastUsed = SafeLastWrite(path)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .OrderByDescending(x => x.LastUsed)
                .Take(Math.Max(0, count))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error($"[Recent] 读取最近文档失败：{ex.Message}");
            return Array.Empty<RecentItem>();
        }
    }

    /// <inheritdoc />
    public void RecordProgramUse(AppItem app)
    {
        if (app is null || app.Id.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            var existing = _programs.FirstOrDefault(x => string.Equals(x.AppId, app.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Count++;
                existing.LastUsed = DateTime.Now;
                existing.Name = app.Name;
                existing.TargetPath = app.TargetPath;
                existing.ShortcutPath = app.ShortcutPath;
            }
            else
            {
                _programs.Add(new ProgramUsage
                {
                    AppId = app.Id.ToString(),
                    Name = app.Name,
                    TargetPath = app.TargetPath,
                    ShortcutPath = app.ShortcutPath,
                    Count = 1,
                    LastUsed = DateTime.Now
                });

                // 超限丢弃最不常用的，保持列表有界。
                if (_programs.Count > MaxTrackedPrograms)
                {
                    _programs = _programs
                        .OrderByDescending(x => x.Count)
                        .ThenByDescending(x => x.LastUsed)
                        .Take(MaxTrackedPrograms)
                        .ToList();
                }
            }
        }

        SavePrograms();
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItemId> GetJumpListPinned(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return Array.Empty<AppItemId>();
        }

        lock (_sync)
        {
            return _jumplist.TryGetValue(appId, out var list)
                ? list.Select(x => new AppItemId(x)).ToList()
                : Array.Empty<AppItemId>();
        }
    }

    /// <inheritdoc />
    public void SetJumpListPinned(string appId, IReadOnlyList<AppItemId> pinned)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        lock (_sync)
        {
            _jumplist[appId] = (pinned ?? Array.Empty<AppItemId>()).Select(x => x.ToString()).ToList();
        }

        SaveJumplist();
    }

    /// <summary>
    /// 前台窗口变化：把前台 hwnd 映射到运行中应用并记录一次使用。
    /// 仅在切到不同应用时计数（同一应用 alt-tab 不重复计数）；后台线程触发，本方法线程安全。
    /// </summary>
    private void OnForegroundChanged(object? sender, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 前台句柄未变化（同窗口重复触发）忽略。
        if (hwnd == _lastForegroundHwnd)
        {
            return;
        }

        _lastForegroundHwnd = hwnd;

        try
        {
            var app = FindAppForHwnd(hwnd);
            if (app is null)
            {
                return;
            }

            if (string.Equals(app.Id, _lastRecordedAppId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastRecordedAppId = app.Id.ToString();
            RecordProgramUse(app);
        }
        catch
        {
            // 前台追踪失败不阻断主流程（M10）。
        }
    }

    private AppItem? FindAppForHwnd(IntPtr hwnd)
    {
        foreach (var info in _windowTracker.GetRunningApps())
        {
            if (info.Windows.Any(w => w.Hwnd == hwnd) && !string.IsNullOrWhiteSpace(info.ExePath))
            {
                return _appSource.ResolveFromPath(info.ExePath);
            }
        }

        return null;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void LoadPrograms()
    {
        try
        {
            if (!File.Exists(_programsPath))
            {
                return;
            }

            var json = File.ReadAllText(_programsPath);
            var items = JsonSerializer.Deserialize<List<ProgramUsage>>(json);
            if (items is not null)
            {
                lock (_sync)
                {
                    _programs = items;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Recent] 最近程序加载失败：{ex.Message}");
        }
    }

    private void SavePrograms()
    {
        try
        {
            var directory = Path.GetDirectoryName(_programsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<ProgramUsage> snapshot;
            lock (_sync)
            {
                snapshot = _programs.ToList();
            }

            File.WriteAllText(_programsPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Recent] 最近程序保存失败：{ex.Message}");
        }
    }

    private void LoadJumplist()
    {
        try
        {
            if (!File.Exists(_jumplistPath))
            {
                return;
            }

            var json = File.ReadAllText(_jumplistPath);
            var items = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (items is not null)
            {
                lock (_sync)
                {
                    _jumplist = items;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Recent] 跳转列表加载失败：{ex.Message}");
        }
    }

    private void SaveJumplist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_jumplistPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Dictionary<string, List<string>> snapshot;
            lock (_sync)
            {
                snapshot = _jumplist.ToDictionary(x => x.Key, x => x.Value.ToList());
            }

            File.WriteAllText(_jumplistPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Recent] 跳转列表保存失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _windowTracker.ForegroundWindowChanged -= OnForegroundChanged;
    }

    private sealed class ProgramUsage
    {
        public string AppId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public string ShortcutPath { get; set; } = string.Empty;
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
    }
}
