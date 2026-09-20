// BetterDesktop.Shell.Island — 抑制状态轮询（1 s 粒度；只在状态翻转时写入仲裁服务）

using System;
using System.Windows.Threading;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Shell.Island.Services;

/// <summary>全屏/游戏/演示模式的抑制监听：状态翻转才调 <see cref="IActivityService.SetSuppressed"/>。</summary>
internal sealed class SuppressionWatcher : IDisposable
{
    private readonly IActivityService _activity;
    private readonly IKernelLogger? _logger;
    private readonly DispatcherTimer _timer;
    private bool _suppressed;

    public SuppressionWatcher(IActivityService activity, IKernelLogger? logger)
    {
        _activity = activity;
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>开始轮询（幂等）。</summary>
    public void Start()
    {
        Poll();
        _timer.Start();
    }

    /// <summary>停止轮询（幂等；停止时确保解除抑制，避免退出后残留"抑制中"状态）。</summary>
    public void Stop()
    {
        _timer.Stop();
        if (_suppressed)
        {
            _suppressed = false;
            _activity.SetSuppressed(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void Poll()
    {
        bool suppressed;
        try
        {
            suppressed = SuppressionPolicy.IsSuppressedNow();
        }
        catch (Exception ex)
        {
            // 查询失败按"不抑制"处理：消息不能被静默吞掉（M10 + 失败可见）
            _logger?.Warn($"shell.island: 抑制状态查询失败（按不抑制处理）：{ex.Message}");
            return;
        }

        if (suppressed == _suppressed)
        {
            return;
        }

        _suppressed = suppressed;
        _activity.SetSuppressed(suppressed);
        _logger?.Info(suppressed
            ? "shell.island: 检测到全屏/演示模式——活动进队列，不弹出"
            : "shell.island: 已退出全屏/演示模式——队列中的活动按优先级补播");
    }
}
