using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>
/// 媒体播放控制服务（公共契约 <see cref="IMediaPlaybackService"/> 的 shell-status 实现）。
/// 内部流转仍用带 Handle 的 <see cref="MediaSessionSnapshot"/>；对外只暴露纯数据 <see cref="MediaPlaybackSnapshot"/>。
/// 命令粒度收敛为"当前活动会话"（与菜单栏 UI 既有消费模式一致）。
/// </summary>
public sealed class MediaPlaybackService : IMediaPlaybackService
{
    private readonly object _sync = new();
    private bool _initialized;
    private Timer? _changeTimer;
    private string? _lastSignature;
    private event EventHandler? MediaPlaybackChangedInternal;

    public event EventHandler? MediaPlaybackChanged
    {
        add
        {
            lock (_sync)
            {
                MediaPlaybackChangedInternal += value;
                // 仅存在订阅者时启动变更检测轮询（1s）；无订阅者零开销
                if (MediaPlaybackChangedInternal is not null && _changeTimer is null)
                {
                    _lastSignature = null; // 首轮只建立基线，不触发（避免订阅即误触发）
                    _changeTimer = new Timer(_ => PollForChanges(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
            }
        }
        remove
        {
            lock (_sync)
            {
                MediaPlaybackChangedInternal -= value;
                if (MediaPlaybackChangedInternal is null)
                {
                    _changeTimer?.Dispose();
                    _changeTimer = null;
                    _lastSignature = null;
                }
            }
        }
    }

    public async Task<IReadOnlyList<MediaPlaybackSnapshot>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();
        var raw = await MediaPlayerCore.GetSnapshotAsync();
        return raw.Select(ToPublic).ToList();
    }

    public async Task<MediaPlaybackSnapshot?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await GetSessionsAsync(cancellationToken);
        // 优先正在播放的会话，其次任意第一个；无会话返回 null（与 MediaPlayerCore.PickActive 语义一致）
        MediaPlaybackSnapshot? fallback = null;
        foreach (var item in sessions)
        {
            if (item.State == MediaPlaybackState.Playing) return item;
            fallback ??= item;
        }
        return fallback;
    }

    public async Task<bool> SendCommandAsync(MediaPlaybackCommand command, TimeSpan? position = null, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();
        var raw = await MediaPlayerCore.GetSnapshotAsync();
        var active = MediaPlayerCore.PickActive(raw);
        if (active is null) return false;
        return await MediaPlayerCore.SendCommandAsync(active, command, position);
    }

    // ── 内部 ──

    private async Task EnsureReadyAsync()
    {
        if (_initialized) return;
        await MediaPlayerCore.EnsureInitializedAsync();
        _initialized = true;
    }

    /// <summary>后台变更检测：快照签名（不含 Position）变化才触发事件；异常静默（降级纪律）。</summary>
    private async void PollForChanges()
    {
        try
        {
            EventHandler? handler;
            lock (_sync) handler = MediaPlaybackChangedInternal;
            if (handler is null) return;

            var raw = await MediaPlayerCore.GetSnapshotAsync();
            var snapshots = raw.Select(ToPublic).ToList();
            var signature = MediaPlaybackChangeDetector.GetSignature(snapshots);
            lock (_sync)
            {
                if (_lastSignature is not null && signature != _lastSignature)
                {
                    _lastSignature = signature;
                    handler?.Invoke(this, EventArgs.Empty);
                    return;
                }
                _lastSignature = signature;
            }
        }
        catch
        {
            // 轮询失败不扩散：下一轮继续
        }
    }

    private static MediaPlaybackSnapshot ToPublic(MediaSessionSnapshot s) => new()
    {
        Title = s.Title,
        Artist = s.Artist,
        AppName = s.AppName,
        State = s.State,
        CanPlay = s.CanPlay,
        CanPause = s.CanPause,
        CanNext = s.CanNext,
        CanPrev = s.CanPrev,
        CanSeek = s.CanSeek,
        IsShuffle = s.IsShuffle,
        CanShuffle = s.CanShuffle,
        IsRepeat = s.IsRepeat,
        CanRepeat = s.CanRepeat,
        Position = s.Position,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        ThumbnailRef = s.ThumbnailRef
    };
}

/// <summary>
/// 变更检测签名基础设施（纯函数，可单测）。
/// 签名排除 Position/StartTime/EndTime——进度变化不触发 <see cref="IMediaPlaybackService.MediaPlaybackChanged"/>，
/// 防止每秒刷屏；进度由消费方按需轮询。
/// </summary>
public static class MediaPlaybackChangeDetector
{
    /// <summary>单会话签名（Title/Artist/AppName/State/能力位/随机/循环）。</summary>
    public static string GetSignature(MediaPlaybackSnapshot s) =>
        $"{s.Title}|{s.Artist}|{s.AppName}|{s.State}|{s.CanPlay}|{s.CanPause}|{s.CanNext}|{s.CanPrev}|{s.CanSeek}|{s.IsShuffle}|{s.CanShuffle}|{s.IsRepeat}|{s.CanRepeat}";

    /// <summary>多会话签名（按列表顺序拼接；会话增删/顺序变化均视为变更）。</summary>
    public static string GetSignature(IReadOnlyList<MediaPlaybackSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return string.Empty;
        var sb = new StringBuilder(snapshots.Count * 64);
        foreach (var s in snapshots)
        {
            sb.Append(GetSignature(s)).Append(';');
        }
        return sb.ToString();
    }
}
