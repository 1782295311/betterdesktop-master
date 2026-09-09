using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 设置服务实现：扁平键值 JSON 持久化（%APPDATA%\BetterDesktop\settings.json）。
/// 线程安全：读写与落盘均加锁。
/// 内存写入即时生效（UI 刷新依赖）；变更经内核事件总线 EmitAsync 广播（ShellEvents.SettingsChanged），
/// 落盘走 debounce 合并——高频 Set（如拖动滑块）在 500ms 内只落盘一次，避免反复全量序列化写文件。
/// 落盘本身走"临时文件 + 替换"避免写一半损坏。
/// </summary>
public sealed class SettingsService : Contracts.ISettingsService, IDisposable
{
    private readonly IContext? _context;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _store = new();
    private readonly Timer _saveTimer;
    private const int SaveDebounceMs = 500;

    public SettingsService(IContext? context = null, string? dataDirectory = null)
    {
        _context = context;
        var appData = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");
        _filePath = Path.Combine(appData, "settings.json");
        Load();
        _saveTimer = new Timer(_ => FlushNow(), null, Timeout.Infinite, Timeout.Infinite);
        // 进程退出兜底：确保在 500ms debounce 窗口内退出时也能落盘最后一次改动。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushNow();
    }

    /// <inheritdoc />
    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        lock (_gate)
        {
            if (_store.TryGetValue(key, out var element))
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(element.GetRawText()) ?? defaultValue;
                }
                catch
                {
                    // 值类型不匹配：返回默认值，不抛
                }
            }
        }

        return defaultValue;
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var element = JsonSerializer.SerializeToElement(value);
        // 内存更新 + 事件即时生效，保证 Get 立刻读到新值、UI 立即刷新。
        lock (_gate)
        {
            _store[key] = element;
        }

        // 变更经内核事件总线广播（fire-and-forget：Set 保持同步 void，不阻塞调用方；
        // EmitAsync 异常由内核单监听器隔离机制记录到内核日志，不阻断主流程）。
        var args = new Contracts.SettingsChangedEventArgs(key, value);
        if (_context is not null) _ = _context.Events.EmitAsync(ShellEvents.SettingsChanged, args);
        // 落盘 debounce：拖滑块期间只预约一次，500ms 后合并落盘。
        try { _saveTimer.Change(SaveDebounceMs, Timeout.Infinite); }
        catch { /* timer 已释放则忽略 */ }
    }

    /// <summary>立即落盘（忽略 debounce）。进程退出前调用，避免丢失最后一次改动。</summary>
    public void FlushNow()
    {
        lock (_gate)
        {
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            lock (_gate)
            {
                _store.Clear();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    _store[property.Name] = property.Value.Clone();
                }
            }
        }
        catch
        {
            // 读取失败：保持空存储，后续 Set 重建
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_store);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // 落盘失败不阻断内存态（下次 Set/Flush 再试）
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Dispose();
        FlushNow();
    }
}
