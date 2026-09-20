// MediaPlayerCore —— SMTC（System Media Transport Controls）媒体会话封装。
// 会话采集与命令执行已迁入原生 MediaCore.dll（C++/WinRT，见 Native/src/media_core.cpp），
// 本类保留为包内编排层：快照 DTO 构建、活动会话选择、命令状态机（随机/循环取反与循环三态推进）、
// 缩略图懒加载、友好应用名。公共契约（快照 DTO / 命令枚举 / 播放状态）在
// BetterDesktop.Api（BetterDesktop.Shell.Music.Contracts）。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BetterDesktop.Shell.Music.Contracts;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>某次记录到的媒体会话只读快照（带内部会话 id，供下发控制命令；UI 消费用公共 MediaPlaybackSnapshot）。</summary>
public sealed class MediaSessionSnapshot
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string AppName { get; init; }

    public required MediaPlaybackState State { get; init; }
    public bool CanPlay { get; init; }
    public bool CanPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrev { get; init; }

    // ── 现代化扩展（声音面板音乐控制器改造）：封面 / 进度 / 播放模式 ──
    /// <summary>专辑封面缩略图引用（只持引用不读流，UI 层异步取流；无封面为 null）。</summary>
    public IRandomAccessStreamReference? ThumbnailRef { get; init; }

    /// <summary>当前播放位置；应用未提供时间线时为 null。</summary>
    public TimeSpan? Position { get; init; }

    /// <summary>曲目时间线起点；未知时为 null。</summary>
    public TimeSpan? StartTime { get; init; }

    /// <summary>曲目总时长（时间线 EndTime）；未知时为 null。</summary>
    public TimeSpan? EndTime { get; init; }

    /// <summary>是否支持拖动跳转（WinRT IsPlaybackPositionEnabled）。</summary>
    public bool CanSeek { get; init; }

    /// <summary>随机播放是否开启（PlaybackInfo.IsShuffleActive）。</summary>
    public bool IsShuffle { get; init; }

    /// <summary>应用是否支持随机切换（WinRT 无真实能力位：会话在手即可尝试，TryChange 失败返回 false，UI 不预禁用）。</summary>
    public bool CanShuffle { get; init; }

    /// <summary>循环播放是否开启（AutoRepeatMode ≠ None，Track=单曲 / List=列表）。</summary>
    public bool IsRepeat { get; init; }

    /// <summary>应用是否支持循环切换（同上，无真实能力位，恒 true）。</summary>
    public bool CanRepeat { get; init; }

    /// <summary>对应的原生会话标识（供下发控制命令）。不对外暴露，仅托管内部流转。</summary>
    internal long SessionId { get; init; }

    /// <summary>循环模式原始值（0=None, 1=Track, 2=List；供 ToggleRepeat 三态推进）。不对外暴露。</summary>
    internal int RepeatMode { get; init; }
}

/// <summary>SMTC 会话管理器封装（原生 MediaCore.dll 后端）。</summary>
public static class MediaPlayerCore
{
    private static bool _initStarted;

    /// <summary>初始化原生 SMTC 管理器（幂等，最多探测一次；DLL 缺失/平台不支持时静默降级为空会话）。</summary>
    public static async Task<bool> EnsureInitializedAsync()
    {
        if (_initStarted) return MediaCoreNative.IsAvailable;
        _initStarted = true;
        // 原生层惰性初始化：首个查询触达管理器；此处先行探测一次，失败静默降级。
        await Task.Run(() =>
        {
            try { _ = MediaCoreNative.GetSessions(); }
            catch { /* 降级：IsAvailable 仍反映 DLL 可用性 */ }
        }).ConfigureAwait(false);
        return MediaCoreNative.IsAvailable;
    }

    /// <summary>读取当前所有媒体会话快照（标题 / 艺术家 / 应用 / 状态 / 可用控件 / 时间线 / 播放模式）。</summary>
    public static async Task<IReadOnlyList<MediaSessionSnapshot>> GetSnapshotAsync()
    {
        if (!MediaCoreNative.IsAvailable) return Array.Empty<MediaSessionSnapshot>();
        try
        {
            var natives = await Task.Run(() => MediaCoreNative.GetSessions()).ConfigureAwait(false);
            var result = new List<MediaSessionSnapshot>(natives.Count);
            foreach (var s in natives)
            {
                result.Add(ToSnapshot(s));
            }
            return result;
        }
        catch
        {
            // 会话枚举失败不扩散，返回空列表。
            return Array.Empty<MediaSessionSnapshot>();
        }
    }

    /// <summary>向指定会话下发一条控制命令；Seek 命令必须传 position（目标播放位置）。
    /// Toggle/ToggleShuffle/ToggleRepeat 以命令时刻的会话最新状态为准（下发前重读），
    /// 避免基于过期快照取反造成"连点无变化"（语义同原托管实现读取 PlaybackInfo 的最新状态）。</summary>
    public static async Task<bool> SendCommandAsync(MediaSessionSnapshot target, MediaPlaybackCommand command, TimeSpan? position = null)
    {
        if (target.SessionId == 0 || !MediaCoreNative.IsAvailable) return false;
        try
        {
            switch (command)
            {
                case MediaPlaybackCommand.Play: return await RunSendAsync(target.SessionId, 0).ConfigureAwait(false);
                case MediaPlaybackCommand.Pause: return await RunSendAsync(target.SessionId, 1).ConfigureAwait(false);
                case MediaPlaybackCommand.Next: return await RunSendAsync(target.SessionId, 2).ConfigureAwait(false);
                case MediaPlaybackCommand.Previous: return await RunSendAsync(target.SessionId, 3).ConfigureAwait(false);
                case MediaPlaybackCommand.Seek:
                    return position is not null && await RunSendAsync(target.SessionId, 4, positionTicks: position.Value.Ticks).ConfigureAwait(false);
                case MediaPlaybackCommand.ToggleShuffle:
                    {
                        var fresh = await ReadFreshStateAsync(target.SessionId).ConfigureAwait(false);
                        var active = fresh?.IsShuffle ?? target.IsShuffle;
                        return await RunSendAsync(target.SessionId, 6, shuffleActive: !active).ConfigureAwait(false);
                    }
                case MediaPlaybackCommand.ToggleRepeat:
                    {
                        var fresh = await ReadFreshStateAsync(target.SessionId).ConfigureAwait(false);
                        var mode = fresh?.RepeatMode ?? target.RepeatMode;
                        return await RunSendAsync(target.SessionId, 7, repeatMode: NextRepeatMode(mode)).ConfigureAwait(false);
                    }
                case MediaPlaybackCommand.Toggle:
                    {
                        var fresh = await ReadFreshStateAsync(target.SessionId).ConfigureAwait(false);
                        var state = fresh?.State ?? target.State;
                        return await RunSendAsync(target.SessionId, state == MediaPlaybackState.Playing ? 1 : 0).ConfigureAwait(false);
                    }
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>循环三态推进：关 → 列表循环 → 单曲循环 → 关（语义同原托管实现 None→List→Track→None）。
    /// 入参 0=None, 1=Track, 2=List；返回下一模式原始值。</summary>
    internal static int NextRepeatMode(int currentMode) => currentMode switch
    {
        1 => 0, // Track → None
        2 => 1, // List → Track
        _ => 2  // None（含未知值兜底）→ List
    };

    private static async Task<MediaSessionSnapshot?> ReadFreshStateAsync(long sessionId)
    {
        var snap = await GetSnapshotAsync().ConfigureAwait(false);
        foreach (var s in snap)
        {
            if (s.SessionId == sessionId) return s;
        }
        return null;
    }

    private static Task<bool> RunSendAsync(long sessionId, int cmd, long positionTicks = 0, bool shuffleActive = false, int repeatMode = 0)
        => Task.Run(() => MediaCoreNative.SendControl(sessionId, cmd, positionTicks, shuffleActive, repeatMode));

    private static MediaSessionSnapshot ToSnapshot(MediaSessionNative s) => new()
    {
        Title = string.IsNullOrWhiteSpace(s.Title) ? "(未命名媒体)" : s.Title,
        Artist = s.Artist,
        AppName = FriendlyAppName(s.SourceAppId),
        State = s.State,
        CanPlay = s.CanPlay,
        CanPause = s.CanPause,
        CanNext = s.CanNext,
        CanPrev = s.CanPrev,
        ThumbnailRef = s.SessionId == 0 || !s.HasThumbnail ? null : new NativeThumbnailReference(s.SessionId),
        Position = s.Position,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        CanSeek = s.CanSeek,
        IsShuffle = s.IsShuffle,
        CanShuffle = true,
        IsRepeat = s.IsRepeat,
        CanRepeat = true,
        SessionId = s.SessionId,
        RepeatMode = s.RepeatMode
    };

    /// <summary>从快照列表选择当前活动媒体：优先正在播放的会话，其次任意非关闭会话；都没有返回 null。
    /// 声音面板与控制中心共用（单一来源，原 SoundPanelViewModel.PickActiveMedia 迁入）。</summary>
    public static MediaSessionSnapshot? PickActive(IReadOnlyList<MediaSessionSnapshot> snap)
    {
        MediaSessionSnapshot? fallback = null;
        foreach (var item in snap)
        {
            if (item.State == MediaPlaybackState.Playing) return item;
            fallback ??= item;
        }
        return fallback;
    }

    // SourceAppUserModelId 形如 "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!App" 或
    // "chrome.exe"。取可读应用名：去掉 AUMID 尾缀，或直接用于显示。
    private static string FriendlyAppName(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "(未知应用)";
        var cleaned = sourceId.Trim();
        int exclaim = cleaned.IndexOf('!');
        if (exclaim > 0) cleaned = cleaned[..exclaim];
        int dot = cleaned.LastIndexOf('.');
        if (dot > 0) cleaned = cleaned[(dot + 1)..];
        return string.IsNullOrWhiteSpace(cleaned) ? "(未知应用)" : cleaned;
    }

    /// <summary>原生缩略图的懒加载引用：UI 调 OpenReadAsync 时才经 MediaCore.dll 取字节（后台线程），
    /// 无缩略图时返回空流（UI 回退占位）。切歌后旧 id 失效由调用方以歌曲身份判重（同原托管语义）。</summary>
    private sealed class NativeThumbnailReference : IRandomAccessStreamReference
    {
        private readonly long _sessionId;

        public NativeThumbnailReference(long sessionId) => _sessionId = sessionId;

        public IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync()
        {
            return Task.Run(async () =>
            {
                var bytes = MediaCoreNative.GetThumbnailBytes(_sessionId);
                var stream = new InMemoryRandomAccessStream();
                if (bytes is { Length: > 0 })
                {
                    using (var writer = new DataWriter(stream))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync().AsTask().ConfigureAwait(false);
                        await writer.FlushAsync().AsTask().ConfigureAwait(false);
                    }
                    stream.Seek(0);
                }
                // InMemoryRandomAccessStream 未直接实现 IRandomAccessStreamWithContentType；
                // 经 RandomAccessStreamReference 包装后 OpenReadAsync 返回该契约类型。
                var reference = RandomAccessStreamReference.CreateFromStream(stream);
                return await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
            }).AsAsyncOperation();
        }
    }
}
