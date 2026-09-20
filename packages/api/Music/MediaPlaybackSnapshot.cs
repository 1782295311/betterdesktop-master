using System;
using Windows.Storage.Streams;

namespace BetterDesktop.Shell.Music.Contracts;

/// <summary>
/// 媒体会话只读快照（公共契约 DTO，供 UI / 第三方扩展 / 灵动岛消费）。
/// 纯数据，不含内部 WinRT 会话句柄——命令操作以"当前活动会话"为粒度，见 <see cref="IMediaPlaybackService.SendCommandAsync"/>。
/// </summary>
public sealed class MediaPlaybackSnapshot
{
    /// <summary>曲目名（无元数据时实现层填 "(未命名媒体)"）。</summary>
    public required string Title { get; init; }

    /// <summary>艺术家（可为空串）。</summary>
    public required string Artist { get; init; }

    /// <summary>播放来源应用可读名（如 Spotify / chrome）。</summary>
    public required string AppName { get; init; }

    /// <summary>播放状态（<see cref="MediaPlaybackState"/>）。</summary>
    public required MediaPlaybackState State { get; init; }

    public bool CanPlay { get; init; }
    public bool CanPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrev { get; init; }

    /// <summary>是否支持拖动跳转（WinRT IsPlaybackPositionEnabled）。</summary>
    public bool CanSeek { get; init; }

    /// <summary>随机播放是否开启（IsShuffleActive）。</summary>
    public bool IsShuffle { get; init; }

    /// <summary>应用是否支持随机切换（WinRT 无真实能力位：会话在手即可尝试，TryChange 失败返回 false，UI 不预禁用）。</summary>
    public bool CanShuffle { get; init; }

    /// <summary>循环播放是否开启（AutoRepeatMode ≠ None，Track=单曲 / List=列表）。</summary>
    public bool IsRepeat { get; init; }

    /// <summary>应用是否支持循环切换（同上，无真实能力位，恒 true）。</summary>
    public bool CanRepeat { get; init; }

    /// <summary>当前播放位置；应用未提供时间线时为 null。</summary>
    public TimeSpan? Position { get; init; }

    /// <summary>曲目时间线起点；未知时为 null。</summary>
    public TimeSpan? StartTime { get; init; }

    /// <summary>曲目总时长（时间线 EndTime）；未知时为 null。</summary>
    public TimeSpan? EndTime { get; init; }

    /// <summary>专辑封面缩略图引用（只持引用不读流，消费方异步取流；无封面为 null）。</summary>
    public IRandomAccessStreamReference? ThumbnailRef { get; init; }
}
