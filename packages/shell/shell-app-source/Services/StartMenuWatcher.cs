using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 开始菜单目录监控封装：监听用户 / 公共两个 Programs 目录的文件与子目录变化
/// （创建 / 删除 / 改名），防抖 1s 后统一触发一次 <see cref="Changed"/>。
/// FileSystemWatcher 回调与防抖计时回调均运行在后台线程，事件在回调线程直接触发，
/// 订阅方需自行保证线程安全（AppSourceService 内部仅做缓存失效，消费方应切回 UI 线程）。
/// 生命周期由 <see cref="AppSourceService"/> 持有，随其 Dispose。
/// </summary>
public sealed class StartMenuWatcher : IDisposable
{
    private const int DebounceMs = 1000;

    private readonly FileSystemWatcher[] _watchers;
    private readonly Timer _debounce;
    private bool _disposed;

    public event EventHandler? Changed;

    public StartMenuWatcher(IEnumerable<string> directories)
    {
        var watchers = new List<FileSystemWatcher>();
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch
            {
                // 单个目录监控创建失败不影响其他目录（M10）。
            }
        }

        _watchers = watchers.ToArray();
        _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            // 防抖：1s 内无更多变化才触发一次（事件风暴抑制）。
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
        catch
        {
            // 计时器变更失败忽略（M10）。
        }
    }

    private void OnDebounceElapsed()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 回调异常不冒泡（M10）。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Dispose();
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // 单个 watcher 释放失败不影响整体。
            }
        }
    }
}
