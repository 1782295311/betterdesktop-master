// BetterDesktop.Shell.Island — 岛控制器：活动 → 表面 的胶水层
//
// 【为什么需要它】活动服务的 Changed 只在"当前活动身份/队列顺序"变化时触发（进度更新不触发，避免
// 每帧广播）；而进度与媒体位置是连续量。所以这里做两件事：
//   ① 订阅 IEventBus 的 shell.activity/changed（跨包唯一通道）→ 身份变化即时上屏；
//   ② 只在"当前活动是进度型/媒体"时挂一个 500 ms 慢轮询补连续量——其余时间定时器停着，零开销。

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Island.Services;
using BetterDesktop.Shell.Island.Windows;

namespace BetterDesktop.Shell.Island;

/// <summary>把活动服务的仲裁结果推到岛表面（含连续量慢轮询与抑制监听）。</summary>
internal sealed class IslandController : IDisposable
{
    /// <summary>连续量（进度/播放位置）轮询周期：人眼感知"在动"即可，不为它抬高重绘预算。</summary>
    private static readonly TimeSpan ContentPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IActivityService _activity;
    private readonly IslandWindow _window;
    private readonly IKernelLogger? _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _contentPoll;
    private readonly SuppressionWatcher _suppression;
    private readonly IEventBus _events;
    private readonly Func<ActivityChangedNotice, CancellationToken, Task> _handler;

    private IDisposable? _subscription;
    private IslandOptions _options;
    private string? _lastId;
    private double? _lastProgress;
    private int _lastMergeCount;

    /// <summary>表面当前是否处在"休眠形态"（无活动且用户要求常驻）。</summary>
    private bool _idleActive;

    public IslandController(
        IActivityService activity,
        IslandWindow window,
        IEventBus events,
        IslandOptions options,
        IKernelLogger? logger)
    {
        _activity = activity;
        _window = window;
        _events = events;
        _options = options;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _handler = OnActivityChanged;

        _contentPoll = new DispatcherTimer { Interval = ContentPollInterval };
        _contentPoll.Tick += (_, _) => Pull();
        _suppression = new SuppressionWatcher(activity, logger);
    }

    /// <summary>开始工作：订阅活动广播 + 抑制轮询 + 立即按当前活动上屏。</summary>
    public void Start()
    {
        _subscription = _events.On<ActivityChangedNotice>(ShellEvents.ActivityChanged, _handler);
        _suppression.Start();
        Pull();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _contentPoll.Stop();
        _suppression.Dispose();
        _subscription?.Dispose();
        _subscription = null;
        _lastId = null;
        _lastProgress = null;
    }

    /// <summary>活动广播 → UI 线程（总线回调线程不定，必须 marshal）。</summary>
    private Task OnActivityChanged(ActivityChangedNotice notice, CancellationToken cancellationToken)
    {
        Post(() => Apply(notice.Current));
        return Task.CompletedTask;
    }

    /// <summary>选项变化（休眠胶囊开关等）即时生效，不必等下一次活动。</summary>
    public void ApplyOptions(IslandOptions options)
    {
        _options = options;

        if (_idleActive && !options.IdleVisible)
        {
            // 窗口侧 ApplyOptions 会自己收出；这里只同步状态，让"再打开"能重新常驻。
            _idleActive = false;
        }
        else if (!_idleActive && options.IdleVisible && options.Enabled && _activity.Current is null)
        {
            _idleActive = true;
            _window.ShowIdle();
        }
    }

    /// <summary>立即采样一次当前活动（进度/位置更新走这里）。</summary>
    private void Pull() => Apply(_activity.Current);

    private void Apply(ActivityItem? item)
    {
        if (item is null)
        {
            _contentPoll.Stop();
            _lastId = null;
            _lastProgress = null;
            _lastMergeCount = 0;

            // 无活动：显示休眠胶囊（用户没关）或彻底收起。"已经在这个状态"时不重复驱动，
            // 否则每次 Changed/轮询都会重新起一次帧时钟（那是无效重绘）。
            if (!_idleActive)
            {
                _idleActive = true;
                if (_options.IdleVisible && _options.Enabled)
                {
                    _window.ShowIdle();
                }
                else
                {
                    _window.HideActivity();
                }
            }

            return;
        }

        _idleActive = false;

        var idChanged = !string.Equals(item.Id, _lastId, StringComparison.Ordinal);
        var progressChanged = item.Progress != _lastProgress;
        var merged = !idChanged && item.MergeCount > _lastMergeCount;
        _lastMergeCount = item.MergeCount;

        if (!idChanged && !progressChanged && !merged)
        {
            return; // 内容与身份都没变：不重排、不重绘（空闲零重绘的前置）
        }

        try
        {
            _window.ShowActivity(IslandContentMapper.FromActivity(item));
            if (merged)
            {
                _window.Breathe();
            }

            if (idChanged)
            {
                // 只在身份变化时记一条（进度更新不记，避免 500 ms 轮询刷屏）
                _logger?.Info($"shell.island: 上屏活动 {item.Source}/{item.Id}（{item.Title}）");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 活动上屏失败（已隔离，不打断仲裁）：{ex.Message}");
            return;
        }

        _lastId = item.Id;
        _lastProgress = item.Progress;
        UpdateContentPoll(item);
    }

    /// <summary>按当前活动类型决定是否开启连续量慢轮询（其余情况停表）。</summary>
    private void UpdateContentPoll(ActivityItem item)
    {
        var needsPoll = item.Kind == ActivityKind.Progress
            || string.Equals(item.Source, IslandContentMapper.SourceMedia, StringComparison.Ordinal);
        if (needsPoll)
        {
            if (!_contentPoll.IsEnabled)
            {
                _contentPoll.Start();
            }
        }
        else
        {
            _contentPoll.Stop();
        }
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // 不等待：热路径上不能因为 UI 忙就阻塞总线线程
            _ = _dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
        }
    }
}
