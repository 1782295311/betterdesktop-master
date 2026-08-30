// BetterDesktop.Shell.MenuBar — 控制中心媒体会话控制器（SMTC 薄壳）
//
// 【为什么单独写一个】
// 控制中心此前把"正在播放"写成写死的空格串 + 三个没有点击事件的装饰按钮，
// 而 SMTC 能力其实已经在 shell-status 的 MediaPlayerCore 里实现好了，
// 只是唯一消费方是声音面板的 SoundPanelViewModel（它同时驱动音频工作线程、
// 会话枚举等一整套后台逻辑）。控制中心如果复用它，会为了显示一行歌名而
// 额外拉起一整套音频采集线程——代价过大。
//
// 因此这里做一层只依赖 MediaPlayerCore 的轻量壳：
//   - 2s 轮询 SMTC 会话（SMTC 无稳定变更事件，只能轮询）
//   - 下发播放/暂停/上一首/下一首命令后立刻刷新一次（不等下一轮轮询）
//   - 任何异常一律降级为空结果，不影响控制中心其余模块

using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>控制中心用的媒体会话控制器（SMTC）。用完必须 <see cref="Dispose"/>（停轮询）。</summary>
internal sealed class MediaSessionController : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private bool _disposed;

    /// <summary>当前优先展示的会话（优先正在播放者，其次任意非关闭会话）。没有则为 null。</summary>
    public MediaSessionSnapshot? Active { get; private set; }

    /// <summary>当前会话是否正在播放（决定播放/暂停按钮显示哪个图标）。无会话时为 false。</summary>
    /// <remarks>
    /// 注意 <c>MediaPlaybackState</c> 是**本项目自定义枚举**（shell-status/Native/MediaCoreNative.cs），
    /// 不是 WinRT 的 Windows.Media.Control 类型——MediaPlayerCore 内部已把 WinRT 播放状态映射过来了。
    /// 把类型判定收敛在这里，UI 层（控制中心）不必关心底层枚举来源。
    /// </remarks>
    public bool IsPlaying => Active is not null && Active.State == MediaPlaybackState.Playing;

    /// <summary>会话集合或播放状态发生变化（轮询/命令回读后触发，已在 UI 线程）。</summary>
    public event EventHandler? Changed;

    public MediaSessionController()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>启动轮询并立即读一次（幂等，重复调用只重置计时）。</summary>
    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>下发媒体控制命令，并在命令后立刻回读一次状态（避免等 2s 轮询才更新按钮）。</summary>
    public async Task SendAsync(MediaCommand command)
    {
        if (_disposed || Active is null) return;
        await MediaPlayerCore.SendCommandAsync(Active, command);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            if (!await MediaPlayerCore.EnsureInitializedAsync()) return;
            var sessions = await MediaPlayerCore.GetSnapshotAsync();
            Active = Pick(sessions);
        }
        catch
        {
            // SMTC 不可用/会话枚举异常：保持上一次结果，不扩散到 UI
        }
        finally
        {
            _refreshing = false;
        }

        // MediaPlayerCore 内部用 ConfigureAwait(false)，回调不在 UI 线程；
        // 事件订阅方要直接改 WPF 控件，这里统一切回创建该控制器的 Dispatcher 线程。
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>优先正在播放的会话，其次任意会话；都没有则返回 null。</summary>
    private static MediaSessionSnapshot? Pick(System.Collections.Generic.IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        MediaSessionSnapshot? fallback = null;
        foreach (var item in sessions)
        {
            if (item.State == MediaPlaybackState.Playing) return item;
            fallback ??= item;
        }
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Changed = null;
    }
}
