// BetterDesktop.Shell.Island — 媒体播放来源：常驻胶囊 + 展开态播放控制
//
// 【为什么不订阅 IMediaPlaybackService.MediaPlaybackChanged】该 event 定义在 api 程序集、
// 消费在 shell-island 程序集，属 ADR-002 D4 明令禁止的"跨程序集裸 event"（门禁 verify-no-cross-assembly-event 会拦）。
// 因此这里改为**自持 1 s 慢轮询 + 本地变更检测**：服务契约本身就写明"进度变化不触发事件"，
// 且实现层的变更检测也是 1 s 粒度的轮询——换成消费方轮询，代价与语义都等价，还顺带避免了订阅/退订配对问题。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Music.Contracts;

namespace BetterDesktop.Shell.Island.Sources;

/// <summary>媒体播放活动来源（Sticky：播放期间常驻，直到停止/关闭）。</summary>
internal sealed class MediaActivitySource : IActivitySource, IDisposable
{
    private const string ActivityId = "island.media";

    /// <summary>轮询周期：与 StatusPlugin 媒体服务的内部检测同粒度（1 s）。</summary>
    private const int PollIntervalMs = 1000;

    private readonly IMediaPlaybackService _media;
    private readonly IActivityService _activity;
    private readonly IKernelLogger? _logger;

    private Timer? _poll;
    private bool _started;
    private bool _posted;
    private bool _refreshBusy;

    /// <summary>上次快照签名：只有内容真的变了才重发活动（避免每秒 Post 造成无意义刷新）。</summary>
    private string? _lastSignature;

    public MediaActivitySource(IMediaPlaybackService media, IActivityService activity, IKernelLogger? logger)
    {
        _media = media;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>开始监听（幂等）：起 1 s 慢轮询并立即取一次快照。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _poll = new Timer(_ => RequestRefresh(), null, PollIntervalMs, PollIntervalMs);
        RequestRefresh();
    }

    /// <summary>停止监听（幂等）：停表 + 收起媒体活动。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _poll?.Dispose();
        _poll = null;
        _lastSignature = null;
        if (_posted)
        {
            _activity.Complete(ActivityId);
            _posted = false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>串行化刷新：SMTC 查询是异步的，避免多次查询叠成一堆并发。</summary>
    private void RequestRefresh()
    {
        if (_refreshBusy)
        {
            return;
        }

        _refreshBusy = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _media.GetActiveSessionAsync().ConfigureAwait(false);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 媒体快照读取失败（已隔离）：{ex.Message}");
        }
        finally
        {
            _refreshBusy = false;
        }
    }

    private void Apply(MediaPlaybackSnapshot? snapshot)
    {
        if (snapshot is null
            || snapshot.State is MediaPlaybackState.Closed or MediaPlaybackState.Stopped)
        {
            _lastSignature = null;
            if (_posted)
            {
                _activity.Complete(ActivityId);
                _posted = false;
            }

            return;
        }

        try
        {
            // 变更检测：标题/歌手/状态/能力位/播放位置（秒级）都没变就不打扰仲裁（每秒 Post 会让订阅方白跑）。
            var signature = string.Join(
                '|',
                snapshot.Title,
                snapshot.Artist,
                snapshot.AppName,
                snapshot.State.ToString(),
                snapshot.CanPlay ? "1" : "0",
                snapshot.CanPause ? "1" : "0",
                snapshot.CanNext ? "1" : "0",
                snapshot.CanPrev ? "1" : "0",
                ((int)(snapshot.Position?.TotalSeconds ?? -1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSignature = signature;

            var isPlaying = snapshot.State == MediaPlaybackState.Playing;
            double? progress = null;
            if (snapshot.Position is { } position
                && snapshot.EndTime is { } end
                && end > TimeSpan.Zero)
            {
                progress = Math.Clamp(position.TotalSeconds / end.TotalSeconds, 0.0, 1.0);
            }

            var actions = new List<ActivityAction>();
            if (snapshot.CanPrev)
            {
                actions.Add(Command("上一首", MediaPlaybackCommand.Previous));
            }

            actions.Add(Command(isPlaying ? "暂停" : "播放", isPlaying ? MediaPlaybackCommand.Pause : MediaPlaybackCommand.Play));
            if (snapshot.CanNext)
            {
                actions.Add(Command("下一首", MediaPlaybackCommand.Next));
            }

            var subtitle = string.IsNullOrWhiteSpace(snapshot.Artist)
                ? snapshot.AppName
                : $"{snapshot.Artist} · {snapshot.AppName}";

            _activity.Post(new ActivityItem(
                Id: ActivityId,
                Source: IslandContentMapper.SourceMedia,
                Kind: ActivityKind.Sticky,
                Priority: ActivityPriority.Media,
                Title: snapshot.Title,
                Body: subtitle,
                IconPath: null,
                Progress: progress,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: TimeSpan.Zero, // Sticky 不受 TTL 约束（仲裁实现只对非 Sticky 判过期）
                Actions: actions));
            _posted = true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 媒体活动发布失败（已隔离）：{ex.Message}");
        }
    }

    private ActivityAction Command(string label, MediaPlaybackCommand command)
        => new(Id: $"media.{command}", Label: label, Invoke: () => _media.SendCommandAsync(command));
}
