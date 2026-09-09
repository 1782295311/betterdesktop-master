using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>剪贴板快照（测试接缝载体，生产实现从真实剪贴板读取，测试子类注入固定内容）。</summary>
internal sealed class ClipboardSnapshot
{
    public bool HasContent { get; init; }
    public ClipboardItemKind Kind { get; init; }
    public string Html { get; init; } = string.Empty;
    public string Rtf { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public byte[]? ImagePng { get; init; }
    public string[]? Files { get; init; }
}

/// <summary>
/// 剪贴板系统级服务实现（探索版移植 + 原版抑制令牌/测试接缝 + 外部实现图片落盘/最近复制/总量预算）。
/// 契约见 api 包 <see cref="IClipboardService"/>；本类经内核服务图 Provide 注册，服务常驻，扩展中心开关只控制监听活性。
/// </summary>
public class ClipboardManager : IClipboardService
{
    // ---------- 容量与生命周期（G 组） ----------
    public const int MaxHistoryItems = 10000;
    public const int MaxPinnedItems = 200;
    public const long MaxImageBytes = 5 * 1024 * 1024;
    public const long MaxTotalImageBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan ExpirationAge = TimeSpan.FromDays(90);
    private const int SaveDebounceMs = 800;
    private const int CleanupIntervalMs = 3600_000;

    /// <summary>轮询周期：WM_CLIPBOARDUPDATE 广播在部分 Win11 版本/环境不可用时的兜底通道（序列号变化检测）。</summary>
    private const int PollIntervalMs = 500;
    private const int WmClipboardUpdate = 0x0310;
    private const string StorageFileName = "clipboard_history.json";
    private const string ImagesRelativeDir = "clipboard\\images";
    private const string EncryptionHeader = "CBENC1\0";

    // ---------- Phase B：热键 / 分段 / 合并 / 按序粘贴（K/D/L 组） ----------
    private const int HotKeyIdPanel = 0x01;        // Ctrl+Shift+V：打开面板
    private const int HotKeyIdFavorites = 0x02;    // Ctrl+Shift+P：收藏视图
    private const int HotKeyIdPause = 0x03;        // Ctrl+Shift+Backspace：暂停/恢复
    private const int SequentialPasteDelayMs = 250; // 分段/按序粘贴段间间隔
    private const string DefaultMergeSeparator = "\r\n";
    private const uint HotKeyModifiers = ClipboardNative.ModControl | ClipboardNative.ModShift;

    // ---------- 隐私黑名单（F 组，探索版同款） ----------
    private static readonly HashSet<string> PrivacyBlacklistProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "1password.exe", "bitwarden", "bitwarden.exe",
        "keepass", "keepass.exe", "keepassxc", "keepassxc.exe",
        "lastpass", "lastpass.exe", "dashlane", "dashlane.exe",
        "enpass", "enpass.exe", "keychain", "passwordmanager",
        "authy", "authy.exe", "microsoft.aad.brokerplugin",
        "credentialuibroker", "vcred", "alipaysecurity", "wcps",
    };

    private static readonly string[] PrivacyBlacklistTitleKeywords =
    {
        "密码", "password", "Password", "PASSWORD", "网银", "Bank", "bank",
        "二步验证", "验证码", "verification",
        "1Password", "Bitwarden", "KeePass", "LastPass", "Dashlane",
    };

    // ---------- 状态 ----------
    private readonly object _sync = new();
    private readonly List<ClipboardEntry> _history = new();
    private readonly IKernelLogger _logger;
    private readonly string _storageDir;
    private readonly string _storageFilePath;
    private readonly string _imagesDir;

    private int _suppressCapture;                 // 原版抑制令牌（Interlocked 消费式）
    private string _lastClipboardContent = string.Empty; // 内容对比双保险
    private bool _isMonitoring;
    private bool _isTemporarilyPaused;
    private DateTime _pauseUntil;

    private HwndSource? _hwndSource;
    private IntPtr _listenerHwnd;
    private DispatcherTimer? _saveTimer;
    private bool _savePending;
    private DispatcherTimer? _cleanupTimer;
    private DispatcherTimer? _pollTimer;
    private uint _lastSequenceNumber;
    private DispatcherTimer? _pauseResumeTimer;

    // Phase B：热键注册表（id → 是否注册成功；0x581 冲突单键降级，不影响其余键）。
    private readonly Dictionary<int, bool> _hotKeyRegistered = new();
    private readonly IAppearanceService? _appearance;
    private readonly IVibrancyService? _vibrancy;
    private bool _showFavoritesOnly;
    private List<ClipboardEntry>? _sequentialQueue;
    private int _sequentialIndex;
    private ClipboardHistoryWindow? _historyWindow;

    // G6/K5 配置化运行时值（默认=常量契约值，设置分区可覆盖；锁保护）。
    private int _capacityLimit = MaxHistoryItems;
    private int _pinnedLimit = MaxPinnedItems;
    private long _maxImageBytes = MaxImageBytes;
    private long _maxTotalImageBytes = MaxTotalImageBytes;
    private TimeSpan _expirationAge = ExpirationAge;

    /// <summary>应用运行时设置（G6/K5 配置化；任一参数为 null 保持现值；非法值忽略并记日志）。</summary>
    public void ApplyRuntimeSettings(
        int? capacity = null,
        int? pinnedLimit = null,
        long? maxImageBytes = null,
        long? maxTotalImageBytes = null,
        TimeSpan? expirationAge = null)
    {
        lock (_sync)
        {
            if (capacity is > 0)
            {
                _capacityLimit = capacity.Value;
            }

            if (pinnedLimit is > 0)
            {
                _pinnedLimit = pinnedLimit.Value;
            }

            if (maxImageBytes is > 0)
            {
                _maxImageBytes = maxImageBytes.Value;
            }

            if (maxTotalImageBytes is > 0)
            {
                _maxTotalImageBytes = maxTotalImageBytes.Value;
            }

            if (expirationAge is { } age && age > TimeSpan.Zero)
            {
                _expirationAge = age;
            }
        }

        _logger.Info($"[Clipboard] 运行时设置已应用：容量={_capacityLimit} 收藏上限={_pinnedLimit} 保留={_expirationAge.TotalDays:0}天");
    }

    public ClipboardManager(IKernelLogger logger, string? storageDir = null, IAppearanceService? appearance = null, IVibrancyService? vibrancy = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appearance = appearance;
        _vibrancy = vibrancy;
        _storageDir = storageDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDesktop");
        _storageFilePath = Path.Combine(_storageDir, StorageFileName);
        _imagesDir = Path.Combine(_storageDir, ImagesRelativeDir);
    }

    // ---------- IClipboardService : 查询 ----------

    public IReadOnlyList<ClipboardEntry> GetFilteredEntries(
        ClipboardItemKind? kind = null,
        ContentCategory? category = null,
        string? keyword = null,
        string? sourceApp = null)
    {
        lock (_sync)
        {
            IEnumerable<ClipboardEntry> query = _history;
            if (kind.HasValue)
            {
                query = query.Where(e => e.ContentType == kind.Value);
            }

            if (category.HasValue)
            {
                query = query.Where(e => e.Category == category.Value);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string kw = keyword.Trim();
                query = query.Where(e =>
                    e.Content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Preview.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Tags.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.FilePaths != null && e.FilePaths.Any(f => f.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)));
            }

            if (!string.IsNullOrWhiteSpace(sourceApp))
            {
                query = query.Where(e => string.Equals(e.SourceAppDisplayName, sourceApp.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return query.ToList();
        }
    }

    public IReadOnlyList<string> GetSourceApps()
    {
        lock (_sync)
        {
            return _history
                .Select(e => e.SourceAppDisplayName)
                .Where(s => !string.Equals(s, "未知", StringComparison.Ordinal))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
        }
    }

    public LastCopiedContent? GetLastCopiedContent(TimeSpan? timeLimit = null)
    {
        lock (_sync)
        {
            ClipboardEntry? latest = _history.FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            if (timeLimit.HasValue && DateTime.Now - latest.Timestamp > timeLimit.Value)
            {
                return null;
            }

            string? contentOrPath = latest.ContentType switch
            {
                ClipboardItemKind.Image => latest.ImagePath,
                ClipboardItemKind.Files => latest.FilePaths is { Length: > 0 } ? string.Join("; ", latest.FilePaths) : null,
                _ => string.IsNullOrEmpty(latest.Content) ? (string.IsNullOrEmpty(latest.HtmlContent) ? null : latest.HtmlContent) : latest.Content,
            };
            return new LastCopiedContent(latest.ContentType, contentOrPath, latest.Timestamp);
        }
    }

    // ---------- IClipboardService : 变更 ----------

    public void PinEntry(ClipboardEntry entry)
    {
        lock (_sync)
        {
            if (entry is null)
            {
                return;
            }

            entry.IsPinned = true;
            RaiseHistoryChangedLocked(ClipboardChangeKind.Updated, entry.Id);
            ScheduleSave();
        }
    }

    public void UnpinEntry(ClipboardEntry entry)
    {
        lock (_sync)
        {
            if (entry is null)
            {
                return;
            }

            entry.IsPinned = false;
            RaiseHistoryChangedLocked(ClipboardChangeKind.Updated, entry.Id);
            ScheduleSave();
        }
    }

    public void TogglePin(ClipboardEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.IsPinned)
        {
            UnpinEntry(entry);
        }
        else
        {
            PinEntry(entry);
        }
    }

    public void DeleteEntry(ClipboardEntry entry)
    {
        lock (_sync)
        {
            if (entry is null)
            {
                return;
            }

            if (_history.Remove(entry))
            {
                TryDeleteImageFile(entry);
                RaiseHistoryChangedLocked(ClipboardChangeKind.Removed, entry.Id);
                ScheduleSave();
            }
        }
    }

    public void DeleteEntries(IEnumerable<ClipboardEntry> entries)
    {
        if (entries is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (ClipboardEntry entry in entries.ToList())
            {
                if (_history.Remove(entry))
                {
                    TryDeleteImageFile(entry);
                }
            }

            RaiseHistoryChangedLocked(ClipboardChangeKind.Removed, string.Empty);
            ScheduleSave();
        }
    }

    public void ClearAllUnpinned()
    {
        lock (_sync)
        {
            var removed = _history.Where(e => !e.IsPinned).ToList();
            foreach (ClipboardEntry entry in removed)
            {
                TryDeleteImageFile(entry);
            }

            _history.RemoveAll(e => !e.IsPinned);
            RaiseHistoryChangedLocked(ClipboardChangeKind.Cleared, string.Empty);
            ScheduleSave();
        }
    }

    public void SetEntryTags(ClipboardEntry entry, string tags)
    {
        lock (_sync)
        {
            if (entry is null)
            {
                return;
            }

            entry.Tags = tags ?? string.Empty;
            RaiseHistoryChangedLocked(ClipboardChangeKind.Updated, entry.Id);
            ScheduleSave();
        }
    }

    // ---------- IClipboardService : 粘贴 ----------

    public void CopyEntryToClipboard(ClipboardEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        // 写回前置抑制令牌：本次写入引发的 WM_CLIPBOARDUPDATE 将被消费式跳过（原版机制）。
        Volatile.Write(ref _suppressCapture, 1);
        bool wrote = false;
        try
        {
            if (entry.Category == ContentCategory.Code)
            {
                // 代码强制纯文本（保留缩进换行，避免 IDE 富文本乱码）。
                System.Windows.Clipboard.SetText(entry.Content);
                wrote = true;
            }
            else
            {
                switch (entry.ContentType)
                {
                    case ClipboardItemKind.Text:
                        System.Windows.Clipboard.SetText(entry.Content);
                        wrote = true;
                        break;

                    case ClipboardItemKind.Html:
                    case ClipboardItemKind.RichText:
                        var data = new DataObject();
                        if (!string.IsNullOrEmpty(entry.HtmlContent))
                        {
                            // CF_HTML 头包装（生死线 7 ②）：禁止裸 HTML SetData（Word/微信乱码）。
                            data.SetData(DataFormats.Html, ClipboardNative.WrapHtmlForClipboard(entry.HtmlContent));
                        }

                        if (!string.IsNullOrEmpty(entry.RtfContent))
                        {
                            data.SetData(DataFormats.Rtf, entry.RtfContent);
                        }

                        string plain = entry.PlainText;
                        if (!string.IsNullOrEmpty(plain))
                        {
                            data.SetText(plain);
                        }

                        System.Windows.Clipboard.SetDataObject(data, true);
                        wrote = true;
                        break;

                    case ClipboardItemKind.Image:
                        string? full = ResolveImageFullPath(entry.ImagePath);
                        if (string.IsNullOrEmpty(full) || !File.Exists(full))
                        {
                            throw new InvalidOperationException($"图片文件不存在：{entry.ImagePath}");
                        }

                        using (var stream = File.OpenRead(full))
                        {
                            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                            System.Windows.Clipboard.SetImage(frame);
                        }

                        wrote = true;
                        break;

                    case ClipboardItemKind.Files:
                        if (entry.FilePaths is not { Length: > 0 })
                        {
                            throw new InvalidOperationException("文件条目路径为空");
                        }

                        var files = new StringCollection();
                        foreach (string path in entry.FilePaths)
                        {
                            files.Add(path);
                        }

                        System.Windows.Clipboard.SetFileDropList(files);
                        wrote = true;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(entry.ContentType), entry.ContentType, "未知剪贴板条目类型");
                }
            }
        }
        finally
        {
            // 仅当写失败（不会有 WM_CLIPBOARDUPDATE 回填）时立即复位，避免令牌卡死监控。
            if (!wrote)
            {
                Volatile.Write(ref _suppressCapture, 0);
            }
        }
    }

    public void CopyEntryAsPlainText(ClipboardEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        Volatile.Write(ref _suppressCapture, 1);
        bool wrote = false;
        try
        {
            System.Windows.Clipboard.SetText(entry.PlainText);
            wrote = true;
        }
        finally
        {
            if (!wrote)
            {
                Volatile.Write(ref _suppressCapture, 0);
            }
        }
    }

    public void PasteEntryToActiveWindow(ClipboardEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        // D3 自动分段（用户无感）：大段（HTML ≥2 块级段 / 纯文本 ≥2 空行段）→ 分段依次带格式粘贴；
        // 单段/短条目 → 原单次粘贴路径。代码条目不分段（代码完整性优先，强制纯文本单次写回）。
        var segments = GetSegments(entry);
        if (segments is not null)
        {
            foreach (var segment in segments)
            {
                CopySegmentToClipboard(segment);
                Thread.Sleep(SequentialPasteDelayMs);
                SendPaste();
                Thread.Sleep(SequentialPasteDelayMs);
            }

            TouchEntry(entry);
            return;
        }

        CopyEntryToClipboard(entry);
        Thread.Sleep(50);
        SendPaste();
        TouchEntry(entry);
    }

    public void PasteEntryAsPlainTextToActiveWindow(ClipboardEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        CopyEntryAsPlainText(entry);
        Thread.Sleep(50);
        SendPaste();
        TouchEntry(entry);
    }

    /// <summary>内部粘贴片段（D3 自动分段产物；IsHtml=true 表示须经 CF_HTML 头包装写回）。</summary>
    internal sealed record PasteSegment(string Content, bool IsHtml);

    /// <summary>
    /// 判定并切分大段条目：返回 null 表示不分段（走原单次粘贴）；否则为依次粘贴的片段。
    /// HTML/RichText 按块级边界切分（保留内联格式）；纯文本按空行拆段；代码/图片/文件不分段。
    /// </summary>
    internal IReadOnlyList<PasteSegment>? GetSegments(ClipboardEntry entry)
    {
        if (entry is null)
        {
            return null;
        }

        // 代码/图片/文件：不分段（代码完整性、图片/文件单对象语义）。
        if (entry.Category == ContentCategory.Code || entry.ContentType == ClipboardItemKind.Image || entry.ContentType == ClipboardItemKind.Files)
        {
            return null;
        }

        if (entry.ContentType is ClipboardItemKind.Html or ClipboardItemKind.RichText && !string.IsNullOrEmpty(entry.HtmlContent))
        {
            var htmlSegments = ClipboardSegmenter.SplitHtml(entry.HtmlContent);
            if (htmlSegments.Count >= 2)
            {
                return htmlSegments
                    .Where(s => !string.IsNullOrWhiteSpace(s.Content))
                    .Select(s => new PasteSegment(s.Content, !s.IsFallback))
                    .ToList();
            }

            return null;
        }

        if (entry.ContentType == ClipboardItemKind.Text && !string.IsNullOrEmpty(entry.Content))
        {
            var textSegments = ClipboardSegmenter.SplitText(entry.Content);
            if (textSegments.Count >= 2)
            {
                return textSegments.Select(t => new PasteSegment(t, false)).ToList();
            }
        }

        return null;
    }

    /// <summary>写单个片段到剪贴板（写前置抑制令牌；HTML 段经 CF_HTML 头包装）。</summary>
    internal void CopySegmentToClipboard(PasteSegment segment)
    {
        if (segment is null)
        {
            throw new ArgumentNullException(nameof(segment));
        }

        Volatile.Write(ref _suppressCapture, 1);
        bool wrote = false;
        try
        {
            if (segment.IsHtml)
            {
                var data = new DataObject();
                data.SetData(DataFormats.Html, ClipboardNative.WrapHtmlForClipboard(segment.Content));
                string plain = ClipboardSegmenter.StripToPlainTextForWrite(segment.Content);
                if (!string.IsNullOrEmpty(plain))
                {
                    data.SetText(plain);
                }

                System.Windows.Clipboard.SetDataObject(data, true);
            }
            else
            {
                System.Windows.Clipboard.SetText(segment.Content);
            }

            wrote = true;
        }
        finally
        {
            if (!wrote)
            {
                Volatile.Write(ref _suppressCapture, 0);
            }
        }
    }

    /// <summary>D5 多选合并粘贴：多条纯文本以分隔符合并一次粘贴。</summary>
    public void MergePasteToActiveWindow(IEnumerable<ClipboardEntry> entries, string? separator = null)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        var list = entries.Where(e => e is not null).ToList();
        if (list.Count == 0)
        {
            return;
        }

        string merged = BuildMergeText(list, string.IsNullOrEmpty(separator) ? DefaultMergeSeparator : separator);

        Volatile.Write(ref _suppressCapture, 1);
        bool wrote = false;
        try
        {
            System.Windows.Clipboard.SetText(merged);
            wrote = true;
        }
        finally
        {
            if (!wrote)
            {
                Volatile.Write(ref _suppressCapture, 0);
            }
        }

        Thread.Sleep(50);
        SendPaste();
        foreach (var entry in list)
        {
            TouchEntry(entry);
        }
    }

    /// <summary>合并文本拼接（纯函数，供单测；空列表返回空串）。</summary>
    internal static string BuildMergeText(IReadOnlyList<ClipboardEntry> entries, string separator)
    {
        if (entries is null || entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(separator);
            }

            sb.Append(entries[i].PlainText);
        }

        return sb.ToString();
    }

    // ---------- L 按序粘贴状态机（v1.3，服务层纯逻辑） ----------

    public bool IsSequentialPasteActive => _sequentialQueue is { Count: > 0 } && _sequentialIndex < _sequentialQueue.Count;

    public int SequentialRemaining => IsSequentialPasteActive ? _sequentialQueue!.Count - _sequentialIndex : 0;

    public void BeginSequentialPaste(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        var list = entries.Where(e => e is not null).ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("按序粘贴列表为空", nameof(entries));
        }

        _sequentialQueue = list;
        _sequentialIndex = 0;
        _logger.Info($"[Clipboard] 按序粘贴开始：{list.Count} 条");
    }

    public void PasteNextSequential()
    {
        if (!IsSequentialPasteActive)
        {
            _logger.Warn("[Clipboard] 按序粘贴未激活或已完成");
            return;
        }

        var entry = _sequentialQueue![_sequentialIndex];
        CopyEntryToClipboard(entry);
        Thread.Sleep(50);
        SendPaste();
        TouchEntry(entry);
        _sequentialIndex++;

        if (_sequentialIndex >= _sequentialQueue.Count)
        {
            _logger.Info("[Clipboard] 按序粘贴完成");
        }
    }

    public void CancelSequentialPaste()
    {
        if (_sequentialQueue is null)
        {
            return;
        }

        _sequentialQueue = null;
        _sequentialIndex = 0;
        _logger.Info("[Clipboard] 按序粘贴已取消");
    }

    public void ResetSequentialPaste()
    {
        _sequentialQueue = null;
        _sequentialIndex = 0;
    }

    public void OpenFileLocation(ClipboardEntry entry)
    {
        if (entry is null || entry.ContentType != ClipboardItemKind.Files || entry.FilePaths is not { Length: > 0 })
        {
            return;
        }

        string path = entry.FilePaths[0];
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 打开文件位置失败：{ex.Message}");
        }
    }

    // ---------- IClipboardService : 录入（OCR/外部来源） ----------

    public int ImportEntries(IEnumerable<ClipboardImportItem> items)
    {
        if (items is null)
        {
            return 0;
        }

        int added = 0;
        foreach (ClipboardImportItem item in items)
        {
            ClipboardEntry? entry = BuildEntryFromImport(item);
            if (entry is null)
            {
                continue;
            }

            if (AddEntryCore(entry))
            {
                added++;
            }
        }

        return added;
    }

    // ---------- IClipboardService : 状态与控制 ----------

    public bool IsMonitoringEnabled
    {
        get
        {
            lock (_sync)
            {
                return _isMonitoring;
            }
        }
    }

    public bool IsTemporarilyPaused => _isTemporarilyPaused;

    public int PauseRemainingSeconds =>
        _isTemporarilyPaused ? Math.Max(0, (int)Math.Ceiling((_pauseUntil - DateTime.Now).TotalSeconds)) : 0;

    public void PauseTemporarily(int seconds = 60)
    {
        lock (_sync)
        {
            if (_isTemporarilyPaused)
            {
                return;
            }

            int safeSeconds = Math.Clamp(seconds, 1, 3600);
            _isTemporarilyPaused = true;
            _pauseUntil = DateTime.Now.AddSeconds(safeSeconds);
            _pauseResumeTimer ??= new DispatcherTimer();
            _pauseResumeTimer.Interval = TimeSpan.FromSeconds(safeSeconds);
            _pauseResumeTimer.Tick -= OnPauseResumeTick;
            _pauseResumeTimer.Tick += OnPauseResumeTick;
            _pauseResumeTimer.Start();
            _logger.Info($"[Clipboard] 临时暂停 {safeSeconds}s");
        }

        PauseStateChanged?.Invoke(true);
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_isTemporarilyPaused)
            {
                return;
            }

            _isTemporarilyPaused = false;
            if (_pauseResumeTimer is not null)
            {
                _pauseResumeTimer.Stop();
                _pauseResumeTimer.Tick -= OnPauseResumeTick;
            }

            _logger.Info("[Clipboard] 恢复监控");
        }

        PauseStateChanged?.Invoke(false);
    }

    public bool ShowFavoritesOnly
    {
        get => _showFavoritesOnly;
        set
        {
            _showFavoritesOnly = value;
            RunOnUi(() =>
            {
                if (_historyWindow is not null)
                {
                    _historyWindow.ShowFavoritesOnly = value;
                }
            });
        }
    }

    /// <summary>UI 线程跳转（宿主 Dispatcher；无宿主时直跑，测试友好）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void OpenHistoryWindow()
    {
        RunOnUi(() =>
        {
            try
            {
                if (_historyWindow is null)
                {
                    _historyWindow = new ClipboardHistoryWindow(this, _appearance, _vibrancy);
                    _historyWindow.Closed += (_, _) => _historyWindow = null;
                }

                _historyWindow.ShowFavoritesOnly = _showFavoritesOnly;
                _historyWindow.ShowAtCursor();
                _historyWindow.Activate();
                _logger.Info("[Clipboard] 历史面板已打开");
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Clipboard] 打开历史面板失败：{ex.Message}");
            }
        });
    }

    public void CloseHistoryWindow()
    {
        RunOnUi(() =>
        {
            try
            {
                _historyWindow?.Close();
                _historyWindow = null;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Clipboard] 关闭历史面板失败：{ex.Message}");
            }
        });
    }

    // ---------- IClipboardService : 事件 ----------

    public event Action<ClipboardHistoryChangedEventArgs>? HistoryChanged;
    public event Action<bool>? PauseStateChanged;
    public event Action<bool>? MonitoringStateChanged;

    // ---------- 监听生命周期（插件 Activate/Deactivate 调用，幂等） ----------

    public void Start()
    {
        lock (_sync)
        {
            if (_isMonitoring)
            {
                return;
            }

            StartClipboardListener();
            RegisterHotkeys();
            LoadFromFile();
            CleanupExpiredEntries();
            _cleanupTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CleanupIntervalMs) };
            _cleanupTimer.Tick += OnCleanupTick;
            _cleanupTimer.Start();
            _lastSequenceNumber = ClipboardNative.GetClipboardSequenceNumber();
            _pollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
            _isMonitoring = true;
            _logger.Info("[Clipboard] 监控已启动");
        }

        MonitoringStateChanged?.Invoke(true);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_isMonitoring)
            {
                return;
            }

            UnregisterHotkeys();
            StopClipboardListener();
            _cleanupTimer?.Stop();
            if (_cleanupTimer is not null)
            {
                _cleanupTimer.Tick -= OnCleanupTick;
            }

            _pollTimer?.Stop();
            if (_pollTimer is not null)
            {
                _pollTimer.Tick -= OnPollTick;
            }

            FlushSave();
            _isMonitoring = false;
            _logger.Info("[Clipboard] 监控已停止（历史保留可查）");
        }

        MonitoringStateChanged?.Invoke(false);
    }

    // ---------- 内部：监听 ----------

    private void StartClipboardListener()
    {
        try
        {
            var parameters = new HwndSourceParameters("BetterDesktopClipboardListener")
            {
                PositionX = 0,
                PositionY = 0,
                Width = 1,
                Height = 1,
                WindowStyle = 0,
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(OnWindowMessage);
            _listenerHwnd = _hwndSource.Handle;

            if (!ClipboardNative.AddClipboardFormatListener(_listenerHwnd))
            {
                _logger.Warn($"[Clipboard] AddClipboardFormatListener 失败（Win32 {Marshal.GetLastWin32Error()}），监听降级：历史仍可查");
                _hwndSource.Dispose();
                _hwndSource = null;
                _listenerHwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 启动剪贴板监听失败：{ex.Message}");
            _hwndSource = null;
            _listenerHwnd = IntPtr.Zero;
        }
    }

    private void StopClipboardListener()
    {
        try
        {
            if (_listenerHwnd != IntPtr.Zero)
            {
                ClipboardNative.RemoveClipboardFormatListener(_listenerHwnd);
            }

            _hwndSource?.Dispose();
            _hwndSource = null;
            _listenerHwnd = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 停止剪贴板监听失败：{ex.Message}");
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            handled = true;
            OnClipboardUpdate();
        }
        else if (msg == ClipboardNative.WmHotKey)
        {
            // 全局热键（K 组）：与监听共用同一 HwndSource 收 WM_HOTKEY（3101 纪律）。
            handled = true;
            HandleHotKey(wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    // ---------- Phase B：全局热键（K 组，3101-global-hotkey 纪律：0x581 捕获 + 配对释放） ----------

    private void RegisterHotkeys()
    {
        if (_listenerHwnd == IntPtr.Zero)
        {
            return;
        }

        _hotKeyRegistered.Clear();
        RegisterHotKeyCore(HotKeyIdPanel, ClipboardNative.VK_V);
        RegisterHotKeyCore(HotKeyIdFavorites, ClipboardNative.VK_P);
        RegisterHotKeyCore(HotKeyIdPause, ClipboardNative.VK_BACK);
    }

    private void RegisterHotKeyCore(int id, byte vk)
    {
        try
        {
            bool ok = ClipboardNative.RegisterHotKey(_listenerHwnd, id, HotKeyModifiers, vk);
            _hotKeyRegistered[id] = ok;
            if (!ok)
            {
                // 0x581（组合键被占）：单键降级，不影响其余键与监听本身。
                _logger.Warn($"[Clipboard] 热键注册失败（Win32 {Marshal.GetLastWin32Error()}，组合键可能被占用）：id={id}");
            }
        }
        catch (Exception ex)
        {
            _hotKeyRegistered[id] = false;
            _logger.Warn($"[Clipboard] 热键注册异常（id={id}）：{ex.Message}");
        }
    }

    private void UnregisterHotkeys()
    {
        foreach (var kv in _hotKeyRegistered)
        {
            if (!kv.Value)
            {
                continue;
            }

            try
            {
                ClipboardNative.UnregisterHotKey(_listenerHwnd, kv.Key);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Clipboard] 热键注销异常（id={kv.Key}）：{ex.Message}");
            }
        }

        _hotKeyRegistered.Clear();
    }

    private void HandleHotKey(int id)
    {
        switch (id)
        {
            case HotKeyIdPanel:
                // L 按序粘贴激活期间：Ctrl+Shift+V 优先逐条粘贴，而非打开面板。
                if (IsSequentialPasteActive)
                {
                    PasteNextSequential();
                }
                else
                {
                    OpenHistoryWindow();
                }

                break;

            case HotKeyIdFavorites:
                ShowFavoritesOnly = !ShowFavoritesOnly;
                OpenHistoryWindow();
                break;

            case HotKeyIdPause:
                if (_isTemporarilyPaused)
                {
                    Resume();
                }
                else
                {
                    PauseTemporarily();
                }

                break;
        }
    }

    /// <summary>剪贴板变化入口：WndProc（广播）与轮询兜底共用。消费式抑制令牌 → 暂停 → 隐私 → 捕获。</summary>
    internal void OnClipboardUpdate()
    {
        // 消费式抑制：令牌置位说明是本次写回引发，清零并跳过（消除回环/历史抖动）。
        // 同时消费最新序列号：写回引发的序列号变化不被轮询二次触发（防广播+轮询双路径回环）。
        if (Interlocked.Exchange(ref _suppressCapture, 0) != 0)
        {
            _lastSequenceNumber = ClipboardNative.GetClipboardSequenceNumber();
            return;
        }

        if (_isTemporarilyPaused)
        {
            return;
        }

        // 广播与轮询双通道去重：真正处理更新时消费当前序列号，轮询检测到相等即不再触发。
        _lastSequenceNumber = ClipboardNative.GetClipboardSequenceNumber();

        try
        {
            var sourceInfo = GetForegroundAppInfo();
            if (IsPrivacySensitive(sourceInfo))
            {
                return;
            }

            CaptureOnce(sourceInfo);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 处理剪贴板更新失败：{ex.Message}");
        }
    }

    /// <summary>测试钩子：置位抑制令牌（供 ClipboardSuppressionTests 验证消费语义，不触碰真实剪贴板）。</summary>
    internal void SetSuppressTokenForTest() => Volatile.Write(ref _suppressCapture, 1);

    /// <summary>捕获快照探针：internal virtual 供测试子类（InternalsVisibleTo）注入固定内容（原版测试接缝）。</summary>
    internal virtual ClipboardSnapshot ReadClipboardSnapshot()
    {
        var snapshot = new ClipboardSnapshot();

        // 捕获顺序（计划 A2）：HTML > Text > Image > Files。
        if (System.Windows.Clipboard.ContainsData(DataFormats.Html))
        {
            string? html = System.Windows.Clipboard.GetData(DataFormats.Html) as string;
            if (!string.IsNullOrEmpty(html))
            {
                string rtf = System.Windows.Clipboard.ContainsData(DataFormats.Rtf) ? (System.Windows.Clipboard.GetData(DataFormats.Rtf) as string) ?? string.Empty : string.Empty;
                string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
                return new ClipboardSnapshot
                {
                    HasContent = true,
                    Kind = string.IsNullOrEmpty(rtf) ? ClipboardItemKind.Html : ClipboardItemKind.RichText,
                    Html = html,
                    Rtf = rtf ?? string.Empty,
                    Text = text ?? string.Empty,
                };
            }
        }

        if (System.Windows.Clipboard.ContainsText())
        {
            string plain = System.Windows.Clipboard.GetText();
            if (!string.IsNullOrEmpty(plain))
            {
                return new ClipboardSnapshot
                {
                    HasContent = true,
                    Kind = ClipboardItemKind.Text,
                    Text = plain,
                };
            }
        }

        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is not null)
            {
                byte[]? png = ToPngBytes(image);
                if (png is not null && png.Length > 0)
                {
                    if (png.Length > _maxImageBytes)
                    {
                        _logger.Info($"[Clipboard] 图片超 {_maxImageBytes / 1024 / 1024}MB 上限，跳过捕获");
                        return snapshot;
                    }

                    return new ClipboardSnapshot
                    {
                        HasContent = true,
                        Kind = ClipboardItemKind.Image,
                        ImagePng = png,
                    };
                }
            }
        }

        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var collection = System.Windows.Clipboard.GetFileDropList();
            if (collection.Count > 0)
            {
                return new ClipboardSnapshot
                {
                    HasContent = true,
                    Kind = ClipboardItemKind.Files,
                    Files = collection.Cast<string>().ToArray(),
                };
            }
        }

        return snapshot;
    }

    private void CaptureOnce((string processName, string windowTitle) sourceInfo)
    {
        ClipboardSnapshot snapshot;
        try
        {
            snapshot = ReadClipboardSnapshot();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 读取剪贴板内容失败：{ex.Message}");
            return;
        }

        if (snapshot is null || !snapshot.HasContent)
        {
            return;
        }

        ClipboardEntry? entry = BuildEntry(snapshot, sourceInfo);
        if (entry is null)
        {
            return;
        }

        // 内容对比双保险（探索版机制）：与最近一次捕获相同则不重复入库。
        string fingerprint = entry.ContentFingerprint;
        if (string.Equals(_lastClipboardContent, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastClipboardContent = fingerprint;
        AddEntryCore(entry);
    }

    private ClipboardEntry? BuildEntry(ClipboardSnapshot snapshot, (string processName, string windowTitle) sourceInfo)
    {
        switch (snapshot.Kind)
        {
            case ClipboardItemKind.Html:
            case ClipboardItemKind.RichText:
                {
                    var entry = new ClipboardEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ContentType = snapshot.Kind,
                        HtmlContent = snapshot.Html,
                        RtfContent = snapshot.Rtf,
                        Content = snapshot.Text,
                        Timestamp = DateTime.Now,
                        SourceProcessName = sourceInfo.processName ?? string.Empty,
                        SourceWindowTitle = sourceInfo.windowTitle ?? string.Empty,
                    };
                    ApplyCategory(entry);
                    entry.FinalizeMetadata();
                    return entry;
                }

            case ClipboardItemKind.Text:
                {
                    var entry = new ClipboardEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ContentType = ClipboardItemKind.Text,
                        Content = snapshot.Text ?? string.Empty,
                        Timestamp = DateTime.Now,
                        SourceProcessName = sourceInfo.processName ?? string.Empty,
                        SourceWindowTitle = sourceInfo.windowTitle ?? string.Empty,
                    };
                    ApplyCategory(entry);
                    entry.FinalizeMetadata();
                    return entry;
                }

            case ClipboardItemKind.Image:
                {
                    if (snapshot.ImagePng is null || snapshot.ImagePng.Length == 0)
                    {
                        return null;
                    }

                    string relative = SaveImagePng(snapshot.ImagePng);
                    (int width, int height) = ReadPngSize(snapshot.ImagePng);
                    var entry = new ClipboardEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ContentType = ClipboardItemKind.Image,
                        Content = string.Empty,
                        Timestamp = DateTime.Now,
                        ImagePath = relative,
                        ImageWidth = width,
                        ImageHeight = height,
                        SizeBytes = snapshot.ImagePng.Length,
                        SourceProcessName = sourceInfo.processName ?? string.Empty,
                        SourceWindowTitle = sourceInfo.windowTitle ?? string.Empty,
                    };
                    ApplyCategory(entry);
                    return entry;
                }

            case ClipboardItemKind.Files:
                {
                    var entry = new ClipboardEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ContentType = ClipboardItemKind.Files,
                        Content = string.Empty,
                        FilePaths = snapshot.Files ?? Array.Empty<string>(),
                        Timestamp = DateTime.Now,
                        SourceProcessName = sourceInfo.processName ?? string.Empty,
                        SourceWindowTitle = sourceInfo.windowTitle ?? string.Empty,
                    };
                    ApplyCategory(entry);
                    return entry;
                }

            default:
                return null;
        }
    }

    private ClipboardEntry? BuildEntryFromImport(ClipboardImportItem item)
    {
        if (item is null)
        {
            return null;
        }

        var entry = new ClipboardEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ContentType = item.Kind,
            Content = item.Content ?? string.Empty,
            HtmlContent = item.HtmlContent ?? string.Empty,
            RtfContent = item.RtfContent ?? string.Empty,
            ImagePath = item.ImagePath ?? string.Empty,
            FilePaths = item.FilePaths ?? Array.Empty<string>(),
            Timestamp = DateTime.Now,
            SourceProcessName = item.SourceApp ?? string.Empty,
            SourceWindowTitle = string.Empty,
        };
        ApplyCategory(entry);
        entry.FinalizeMetadata();
        return entry;
    }

    private void ApplyCategory(ClipboardEntry entry)
    {
        ContentProfile profile = ContentAnalyzer.Analyze(entry.ContentType, entry.HtmlContent, entry.Content);
        entry.Category = profile.Category;
        entry.HasImages = profile.HasImages;
        entry.HasTable = profile.HasTable;
        entry.IsCode = profile.IsCode;
    }

    /// <summary>入库核心：去重置顶 / 驱逐 / 图片预算 / 节流保存 / 事件。返回是否新增。</summary>
    private bool AddEntryCore(ClipboardEntry entry)
    {
        bool added;
        lock (_sync)
        {
            ClipboardEntry? existing = _history.FirstOrDefault(h => h.IsSameContent(entry));
            if (existing is not null)
            {
                // 同内容去重置顶：保留固定状态，刷新时间与复制次数（B1/B5）。
                existing.Touch();
                MoveToTopLocked(existing);
                RaiseHistoryChangedLocked(ClipboardChangeKind.Updated, existing.Id);
                ScheduleSave();
                added = false;
            }
            else
            {
                _history.Insert(0, entry);
                EvictExcessUnpinnedEntriesLocked();
                EvictExcessImageBytesLocked();
                RaiseHistoryChangedLocked(ClipboardChangeKind.Added, entry.Id);
                ScheduleSave();
                added = true;
            }
        }

        return added;
    }

    private void TouchEntry(ClipboardEntry entry)
    {
        lock (_sync)
        {
            ClipboardEntry? existing = _history.FirstOrDefault(h => h.Id == entry.Id);
            if (existing is null)
            {
                return;
            }

            existing.Touch();
            MoveToTopLocked(existing);
            RaiseHistoryChangedLocked(ClipboardChangeKind.Updated, existing.Id);
            ScheduleSave();
        }
    }

    private void MoveToTopLocked(ClipboardEntry entry)
    {
        int idx = _history.IndexOf(entry);
        if (idx > 0)
        {
            _history.RemoveAt(idx);
            _history.Insert(0, entry);
        }
    }

    // ---------- 内部：驱逐 / 清理 ----------

    private void EvictExcessUnpinnedEntriesLocked()
    {
        int pinnedCount = _history.Count(e => e.IsPinned);

        // 收藏超限：驱逐最旧收藏。
        while (pinnedCount > _pinnedLimit)
        {
            ClipboardEntry? oldestPinned = _history.LastOrDefault(e => e.IsPinned);
            if (oldestPinned is null)
            {
                break;
            }

            _history.Remove(oldestPinned);
            TryDeleteImageFile(oldestPinned);
            pinnedCount--;
        }

        // 总量超限：驱逐最旧非收藏。
        int maxUnpinned = _capacityLimit - _history.Count(e => e.IsPinned);
        int unpinned = _history.Count(e => !e.IsPinned);
        while (unpinned > maxUnpinned)
        {
            ClipboardEntry? oldestUnpinned = _history.LastOrDefault(e => !e.IsPinned);
            if (oldestUnpinned is null)
            {
                break;
            }

            _history.Remove(oldestUnpinned);
            TryDeleteImageFile(oldestUnpinned);
            unpinned--;
        }
    }

    private void EvictExcessImageBytesLocked()
    {
        long total = _history
            .Where(e => e.ContentType == ClipboardItemKind.Image)
            .Sum(e => e.SizeBytes);
        if (total <= _maxTotalImageBytes)
        {
            return;
        }

        // 总量预算：超限淘汰最旧未固定图片条目（含删文件），保留文本/固定项。
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            ClipboardEntry e = _history[i];
            if (e.IsPinned || e.ContentType != ClipboardItemKind.Image || e.SizeBytes <= 0)
            {
                continue;
            }

            total -= e.SizeBytes;
            _history.RemoveAt(i);
            TryDeleteImageFile(e);
            if (total <= _maxTotalImageBytes)
            {
                break;
            }
        }
    }

    internal void CleanupExpiredEntries()
    {
        DateTime cutoff = DateTime.Now - _expirationAge;
        int removedCount = 0;
        lock (_sync)
        {
            var expired = _history.Where(e => !e.IsPinned && e.Timestamp < cutoff).ToList();
            foreach (ClipboardEntry entry in expired)
            {
                _history.Remove(entry);
                TryDeleteImageFile(entry);
                removedCount++;
            }

            CleanupOrphanImagesLocked();
            if (removedCount > 0)
            {
                RaiseHistoryChangedLocked(ClipboardChangeKind.Removed, string.Empty);
                ScheduleSave();
            }
        }

        if (removedCount > 0)
        {
            _logger.Info($"[Clipboard] 清理 {removedCount} 条过期记录（> {_expirationAge.TotalDays:0} 天）");
        }
    }

    private void CleanupOrphanImagesLocked()
    {
        if (!Directory.Exists(_imagesDir))
        {
            return;
        }

        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardEntry e in _history)
        {
            string? full = ResolveImageFullPath(e.ImagePath);
            if (full is not null)
            {
                inUse.Add(Path.GetFullPath(full));
            }
        }

        foreach (string file in Directory.GetFiles(_imagesDir, "*.png"))
        {
            if (!inUse.Contains(Path.GetFullPath(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[Clipboard] 删除孤儿图片失败 {Path.GetFileName(file)}：{ex.Message}");
                }
            }
        }
    }

    private void OnCleanupTick(object? sender, EventArgs e)
    {
        if (_isTemporarilyPaused && DateTime.Now >= _pauseUntil)
        {
            Resume();
        }

        CleanupExpiredEntries();
    }

    /// <summary>轮询兜底：序列号变化即视为剪贴板更新（广播不可用环境仍能捕获；广播正常时序列号已被 OnClipboardUpdate 消费，不重复触发）。</summary>
    private void OnPollTick(object? sender, EventArgs e)
    {
        uint sequenceNumber;
        try
        {
            sequenceNumber = ClipboardNative.GetClipboardSequenceNumber();
        }
        catch (Exception)
        {
            return; // 轮询失败静默：广播通道若可用仍能覆盖；下次 tick 再试。
        }

        if (sequenceNumber != _lastSequenceNumber)
        {
            _lastSequenceNumber = sequenceNumber;
            OnClipboardUpdate();
        }
    }

    private void OnPauseResumeTick(object? sender, EventArgs e) => Resume();

    // ---------- 内部：图片落盘 ----------

    private string SaveImagePng(byte[] png)
    {
        Directory.CreateDirectory(_imagesDir);
        string id = Guid.NewGuid().ToString("N");
        string relative = $"{ImagesRelativeDir}\\{id}.png";
        string full = Path.Combine(_storageDir, relative);
        File.WriteAllBytes(full, png);
        return relative;
    }

    private string? ResolveImageFullPath(string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        return Path.Combine(_storageDir, relative);
    }

    private void TryDeleteImageFile(ClipboardEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.ImagePath))
        {
            return;
        }

        try
        {
            string full = ResolveImageFullPath(entry.ImagePath)!;
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 删除图片文件失败 {entry.ImagePath}：{ex.Message}");
        }
    }

    private static (int Width, int Height) ReadPngSize(byte[] png)
    {
        // PNG 固定头 8 字节 + IHDR 长度/类型 8 字节，宽高为大端 4 字节。
        if (png is { Length: >= 24 } && png[0] == 0x89 && png[1] == 0x50)
        {
            int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return (0, 0);
    }

    private static byte[]? ToPngBytes(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---------- 内部：来源 / 隐私 ----------

    private (string processName, string windowTitle) GetForegroundAppInfo()
    {
        try
        {
            IntPtr hwnd = ClipboardNative.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return (string.Empty, string.Empty);
            }

            ClipboardNative.GetWindowThreadProcessId(hwnd, out uint processId);

            string processName = string.Empty;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
                // 进程已退出等场景，来源未知不阻断捕获。
            }

            string windowTitle = string.Empty;
            try
            {
                int length = ClipboardNative.GetWindowTextLength(hwnd);
                if (length > 0)
                {
                    var sb = new StringBuilder(length + 1);
                    ClipboardNative.GetWindowText(hwnd, sb, sb.Capacity);
                    windowTitle = sb.ToString();
                }
            }
            catch
            {
                // 窗口标题读取失败不阻断捕获。
            }

            return (processName, windowTitle);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    internal static bool IsPrivacySensitive((string processName, string windowTitle) sourceInfo)
    {
        if (!string.IsNullOrEmpty(sourceInfo.processName) &&
            PrivacyBlacklistProcesses.Contains(sourceInfo.processName))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(sourceInfo.windowTitle))
        {
            foreach (string keyword in PrivacyBlacklistTitleKeywords)
            {
                if (sourceInfo.windowTitle.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---------- 内部：粘贴辅助 ----------

    private void SendPaste()
    {
        ClipboardNative.keybd_event(ClipboardNative.VK_CONTROL, 0, 0, UIntPtr.Zero);
        ClipboardNative.keybd_event(ClipboardNative.VK_V, 0, 0, UIntPtr.Zero);
        ClipboardNative.keybd_event(ClipboardNative.VK_V, 0, ClipboardNative.KEYEVENTF_KEYUP, UIntPtr.Zero);
        ClipboardNative.keybd_event(ClipboardNative.VK_CONTROL, 0, ClipboardNative.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---------- 内部：持久化（H 组：System.Text.Json + DPAPI + CBENC1 头） ----------

    private void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        _saveTimer.Tick += OnSaveTick;
        _savePending = true;
        if (!_saveTimer.IsEnabled)
        {
            _saveTimer.Start();
        }
    }

    private void OnSaveTick(object? sender, EventArgs e) => FlushSave();

    private void FlushSave()
    {
        if (_saveTimer is not null)
        {
            _saveTimer.Stop();
            _saveTimer.Tick -= OnSaveTick;
        }

        if (!_savePending)
        {
            return;
        }

        _savePending = false;
        SaveToFile();
    }

    internal void SaveToFile()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            string json;
            lock (_sync)
            {
                json = JsonSerializer.Serialize(_history, JsonOptions);
            }

            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] cipher = ClipboardNative.Protect(plain);
            byte[] withHeader = new byte[EncryptionHeader.Length + cipher.Length];
            Encoding.ASCII.GetBytes(EncryptionHeader).CopyTo(withHeader, 0);
            cipher.CopyTo(withHeader, EncryptionHeader.Length);
            File.WriteAllBytes(_storageFilePath, withHeader);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Clipboard] 保存历史失败：{ex.Message}");
        }
    }

    internal void LoadFromFile()
    {
        try
        {
            if (!File.Exists(_storageFilePath))
            {
                return;
            }

            byte[] content = File.ReadAllBytes(_storageFilePath);
            byte[] jsonBytes;
            if (content.Length > EncryptionHeader.Length &&
                Encoding.ASCII.GetString(content, 0, EncryptionHeader.Length) == EncryptionHeader)
            {
                byte[] cipher = new byte[content.Length - EncryptionHeader.Length];
                Array.Copy(content, EncryptionHeader.Length, cipher, 0, cipher.Length);
                jsonBytes = ClipboardNative.Unprotect(cipher);
            }
            else
            {
                // 明文旧格式回退（CBENC1 头缺失）。
                jsonBytes = content;
            }

            string json = Encoding.UTF8.GetString(jsonBytes);
            lock (_sync)
            {
                var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json, JsonOptions);
                if (loaded is not null)
                {
                    _history.Clear();
                    _history.AddRange(loaded);
                }
            }

            _logger.Info($"[Clipboard] 已加载 {_history.Count} 条历史");
        }
        catch (Exception ex)
        {
            // 损坏数据自愈：空历史不崩溃（H2）。
            _logger.Warn($"[Clipboard] 加载历史失败（按空历史启动）：{ex.Message}");
            lock (_sync)
            {
                _history.Clear();
            }
        }
    }

    private void RaiseHistoryChangedLocked(ClipboardChangeKind change, string entryId)
    {
        HistoryChanged?.Invoke(new ClipboardHistoryChangedEventArgs(change, entryId));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}
