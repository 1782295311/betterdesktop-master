// MediaPlayerCore —— SMTC（System Media Transport Controls）媒体会话封装。
// 通过 Windows.Media.Control 的全局会话管理器枚举正在播放/暂停的媒体应用，
// 并向下发送播放/暂停/上一首/下一首命令。这是 WinRT 托管投影（非 P/Invoke），
// 直接使用即可，失败一律降级为空结果，不影响其他面板。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>媒体控制命令（与面板按钮一一对应）。</summary>
public enum MediaCommand
{
    Toggle = 0,
    Play = 1,
    Pause = 2,
    Next = 3,
    Previous = 4
}

/// <summary>某次记录到的媒体会话只读快照（供 UI 绑定）。</summary>
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

    /// <summary>对应的 WinRT 会话句柄（供下发控制命令）。等同不对外暴露，仅托管内部流转。</summary>
    internal GlobalSystemMediaTransportControlsSession? Handle { get; init; }
}

/// <summary>SMTC 会话管理器封装（单例式）。</summary>
public static class MediaPlayerCore
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static bool _initStarted;

    /// <summary>异步初始化全局媒体会话管理器（幂等，最多初始化一次）。</summary>
    public static async Task<bool> EnsureInitializedAsync()
    {
        if (_manager is not null) return true;
        if (_initStarted) return false; // 已在初始化中，避免并发重复请求
        _initStarted = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取当前所有媒体会话快照（标题 / 艺术家 / 应用 / 状态 / 可用控件）。</summary>
    public static async Task<IReadOnlyList<MediaSessionSnapshot>> GetSnapshotAsync()
    {
        var result = new List<MediaSessionSnapshot>();
        if (_manager is null) return result;
        try
        {
            foreach (var session in _manager.GetSessions())
            {
                result.Add(await DescribeAsync(session).ConfigureAwait(false));
            }
        }
        catch
        {
            // 会话枚举失败不扩散，返回已收集的部分。
        }
        return result;
    }

    /// <summary>向指定会话下发一条控制命令。</summary>
    public static async Task<bool> SendCommandAsync(MediaSessionSnapshot target, MediaCommand command)
    {
        var handle = target.Handle;
        if (handle is null) return false;
        try
        {
            switch (command)
            {
                case MediaCommand.Play: return await handle.TryPlayAsync();
                case MediaCommand.Pause: return await handle.TryPauseAsync();
                case MediaCommand.Next: return await handle.TrySkipNextAsync();
                case MediaCommand.Previous: return await handle.TrySkipPreviousAsync();
                case MediaCommand.Toggle:
                    return target.State == MediaPlaybackState.Playing
                        ? await handle.TryPauseAsync()
                        : await handle.TryPlayAsync();
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<MediaSessionSnapshot> DescribeAsync(GlobalSystemMediaTransportControlsSession session)
    {
        string title = string.Empty;
        string artist = string.Empty;
        string sourceId = string.Empty;
        var state = MediaPlaybackState.Closed;
        bool canPlay = false, canPause = false, canNext = false, canPrev = false;

        try
        {
            var info = session.GetPlaybackInfo();
            state = MapPlaybackState(info.PlaybackStatus);
            canPlay = info.Controls.IsPlayEnabled;
            canPause = info.Controls.IsPauseEnabled;
            canNext = info.Controls.IsNextEnabled;
            canPrev = info.Controls.IsPreviousEnabled;
        }
        catch { }

        try { sourceId = session.SourceAppUserModelId ?? string.Empty; } catch { }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is not null)
            {
                title = props.Title ?? string.Empty;
                artist = props.Artist ?? string.Empty;
            }
        }
        catch { }

        return new MediaSessionSnapshot
        {
            Title = string.IsNullOrWhiteSpace(title) ? "(未命名媒体)" : title,
            Artist = artist,
            AppName = FriendlyAppName(sourceId),
            State = state,
            CanPlay = canPlay,
            CanPause = canPause,
            CanNext = canNext,
            CanPrev = canPrev,
            Handle = session
        };
    }

    // WinRT 的 GlobalSystemMediaTransportControlsSessionPlaybackStatus 枚举序号与
    // 本项目 MediaPlaybackState 不同，必须显式映射，不能按数值强转。
    private static MediaPlaybackState MapPlaybackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus s)
    {
        return s switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            _ => MediaPlaybackState.Closed
        };
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
}