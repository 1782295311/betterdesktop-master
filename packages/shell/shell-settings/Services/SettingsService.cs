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
///
/// 【2026-09-11 修复 · 设置被整份抹掉（用户实测：components.desktop 等键无声消失）】
/// 原实现 = **把本进程内存快照整份覆盖写**，叠加两个放大条件即成数据丢失：
///   ① 同一个 settings.json 被**多个进程**（Host / Agent / Tray 经 CLI / 设置中心）各持一份
///      SettingsService 实例 → 谁最后写，谁的内存快照就成了全文件的唯一真相；
///   ② <c>Load()</c> 原为 <c>catch { /* 保持空存储 */ }</c>：读盘失败（他进程正 replace 文件
///      → IOException）静默退化成空存储，此后**第一次 Set 就会把用户全部设置写成只剩那一个键**。
/// 现改为三条纪律：
///   · <b>读-合并-写</b>：落盘前重读磁盘，只覆盖「本实例改过的键」（<c>_changedKeys</c>）——
///     别的进程写入的键一律保留；
///   · <b>跨进程串行</b>：命名 Mutex（同会话内 Host/Agent/Tray/CLI 共用）包住读-合并-写；
///   · <b>读不到就不覆盖</b>：读盘失败时宁可本次不落盘（内存态仍生效），也绝不拿空快照覆盖磁盘；
///     内容确实是坏的（JsonException）则先备份成 <c>settings.json.corrupt-&lt;时间戳&gt;</c> 再允许重建。
/// </summary>
public sealed class SettingsService : Contracts.ISettingsService, IDisposable
{
    private readonly IContext? _context;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _store = new();
    private readonly Timer _saveTimer;
    private const int SaveDebounceMs = 500;

    /// <summary>本实例"改过"的键：合并落盘时只有这些键以本进程内存值为准（其余键以磁盘为准）。</summary>
    private readonly HashSet<string> _changedKeys = new(StringComparer.Ordinal);

    /// <summary>跨进程落盘互斥（同会话：Host / Agent / Tray / CLI 共用同一份 settings.json）。</summary>
    private const string SaveMutexName = @"Local\BetterDesktop.Settings.json";

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
            _changedKeys.Add(key); // 合并落盘时只有"我改过的键"以我方内存值为准
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
        // 【2026-09-11】重试 + 失败可见：他进程正在"临时文件 + 替换"落盘时，这里会拿到
        // IOException（共享冲突）——原实现直接 catch 成空存储，下一次 Set 就把磁盘写成只剩一个键。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return; // 首次运行：空存储是合法状态
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                ApplyLoaded(doc);
                return;
            }
            catch (JsonException)
            {
                // 内容确实是坏的：先备份（用户数据可人工救回），再允许以空存储重建
                BackupCorruptFile();
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(60); // 多半是他进程正在替换文件：稍后重试
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(60);
            }
            catch
            {
                return;
            }
        }

        // 三次都读不到：本次会话内存态仍可用；落盘侧 SaveLocked 会再读一次磁盘，
        // 读不到就跳过写入（绝不拿不完整的内存快照覆盖用户设置）。
        _context?.Logger.Info($"settings.json 读取失败（他进程占用？）：落盘前会重试，必要时跳过本次写入（{_filePath}）");
    }

    private void ApplyLoaded(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            lock (_gate)
            {
                _store.Clear();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    _store[property.Name] = property.Value.Clone();
                }
            }
        }
    }

    /// <summary>把损坏的 settings.json 挪到带时间戳的备份名（不删除用户数据）。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_filePath, backup, overwrite: true);
            _context?.Logger.Info($"settings.json 内容损坏，已备份为 {Path.GetFileName(backup)} 并重建");
        }
        catch
        {
            // 连备份都做不到：不改动磁盘，让 SaveLocked 的"读不到就不写"兜住
        }
    }

    /// <summary>读磁盘当前键值（合并落盘用）；读不到返回 null（调用方据此放弃本次写入）。</summary>
    private Dictionary<string, JsonElement>? TryReadStore()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal); // 尚无文件：空集合
            }

            var json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }

            return result;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// 落盘：<b>读-合并-写</b>，而不是"把内存快照整份写出"。
    /// 步骤：跨进程 Mutex → 重读磁盘现有键值（作为基线）→ 只用本实例改过的键覆盖基线 → 原子替换。
    /// 关键防线：磁盘内容读不到时**跳过本次写入**（内存态仍生效）——宁可晚一点落盘，
    /// 也绝不用一份不完整的快照把用户设置抹成"只剩本次改的键"。
    /// </summary>
    private void SaveLocked()
    {
        try
        {
            if (_changedKeys.Count == 0)
            {
                return; // 本实例无改动：不写（避免无意义的整份覆盖）
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var mutex = new Mutex(false, SaveMutexName);
            var owned = false;
            try
            {
                try
                {
                    owned = mutex.WaitOne(2000);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // 上一个持有者异常退出：互斥量已归本线程
                }

                if (!owned)
                {
                    _context?.Logger.Info("settings.json 落盘等待互斥超时，本次跳过（内存态仍生效）");
                    return;
                }

                var onDisk = TryReadStore();
                if (onDisk is null)
                {
                    _context?.Logger.Info("settings.json 当前内容不可读，本次跳过落盘以免覆盖既有设置");
                    return;
                }

                // 基线 = 磁盘现状（保留别的进程写入的键），再用本实例改过的键覆盖
                var merged = new Dictionary<string, JsonElement>(onDisk, StringComparer.Ordinal);
                lock (_gate)
                {
                    foreach (var key in _changedKeys)
                    {
                        if (_store.TryGetValue(key, out var value))
                        {
                            merged[key] = value;
                        }
                    }
                }

                var json = JsonSerializer.Serialize(merged);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
                _changedKeys.Clear();
            }
            finally
            {
                if (owned)
                {
                    mutex.ReleaseMutex();
                }
            }
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
