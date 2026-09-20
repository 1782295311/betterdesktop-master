using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterDesktop.Shell.Music.Contracts;

/// <summary>
/// 媒体播放控制服务契约（公共 API；注册于内核服务图 ADR-002，实现方 shell-status）。
/// 任意模块（含第三方扩展，仅引用 BetterDesktop.Api）经 <c>IContext.Get&lt;IMediaPlaybackService&gt;()</c> 消费；
/// 亦为灵动岛预留数据同步入口（订阅 <see cref="MediaPlaybackChanged"/> 或轮询快照）。
/// 契约可加性：v1.1+ 新能力以新增方法/事件扩展，不改既有成员。
/// </summary>
public interface IMediaPlaybackService
{
    /// <summary>读取当前所有媒体会话快照（无会话返回空列表，不抛异常）。</summary>
    Task<IReadOnlyList<MediaPlaybackSnapshot>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>读取当前活动媒体会话（优先正在播放；无会话返回 null）。</summary>
    Task<MediaPlaybackSnapshot?> GetActiveSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>向当前活动会话下发一条控制命令；Seek 必须传 position（目标播放位置）。失败返回 false。</summary>
    Task<bool> SendCommandAsync(MediaPlaybackCommand command, TimeSpan? position = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 播放数据变更事件：切歌 / 播放状态 / 能力位 / 随机 / 循环 变化时触发；
    /// 进度（Position）变化不触发（防高频刷新，进度由消费方按需轮询）。
    /// 仅存在订阅者时内部轮询检测，无订阅者零开销。为灵动岛等实时数据同步预留。
    /// </summary>
    event EventHandler? MediaPlaybackChanged;
}
