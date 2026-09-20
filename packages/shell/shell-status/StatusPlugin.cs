// BetterDesktop.Shell.Status — 状态采集/语义插件
// 角色：`shell.status` — 为菜单栏右区/控制中心提供状态采集（采集层）与语义快照（语义层）。
// 依赖方向：shell-status → kernel（IEventBus/IContext）；不依赖任何 UI 插件。
// UI 层订阅 "status.changed" 事件 + Get<IMemoryMonitor> 等类型化服务读取语义快照。

using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;
using BetterDesktop.Shell.Status.Services;

namespace BetterDesktop.Shell.Status;

// ============================================================
// 【白话导航 · 状态采集域】凭白话需求定位到精确文件：
//   "CPU / 内存占用数据"              → Services/CpuMonitor.cs、Services/MemoryMonitor.cs（原生读数在 Native/CpuCoreNative.cs、Native/MemoryCoreNative.cs）
//   "电池 / 电量"                     → Services/BatteryMonitor.cs + Native/BatteryInterop.cs
//   "音量 / 麦克风"                   → Services/VolumeMonitor.cs、Services/MicrophoneMonitor.cs + Native/AudioCoreNative.cs（CoreAudio COM）
//   "WiFi / 网速 / 网络状态"          → Services/NetworkMonitor.cs + Native/NetworkInterop.cs、Native/WlanCoreNative.cs
//   "屏幕亮度"                        → Services/BrightnessMonitor.cs + Native/DisplayCoreNative.cs
//   "输入法 / 键盘布局状态"           → Services/ImeMonitor.cs + Native/KeyboardLayoutInterop.cs、Native/TsfInputProcessor.cs
//   "轮询节奏 / 刷新节流"             → Services/StatusPoller.cs（统一轮询器）
//   "UI 怎么拿到这些状态"             → 订阅 IEventBus 的 "status.changed"，或 Inject 对应 I*Monitor（本域不依赖任何 UI 包）
// ============================================================

/// <summary>状态插件：注册各监控项 + 统一轮询器并启动采集。</summary>
public sealed class StatusPlugin : IPlugin
{
    private StatusPoller? _poller;
    private IStatusMonitor[]? _monitors;
    private bool _audioEventWired;
    private bool _networkEventWired;
    // C13：音频/网络事件驱动防抖 Timer——高频事件（音量拖动、IP 续租）合并为一次全量轮询，避免风暴。
    private Timer? _eventDebounce;
    private const int EventDebounceMs = 150;

    public string Name => "shell.status";

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var source = new NativeSystemSource();

        // 采集 + 语义：每一监控项在采集的同时产出用户可读的语义快照。
        var cpu = new CpuMonitor();
        var memory = new MemoryMonitor(source);
        var battery = new BatteryMonitor(source);
        var volume = new VolumeMonitor(source);
        var microphone = new MicrophoneMonitor(source);
        var network = new NetworkMonitor(source);
        var ime = new ImeMonitor(source);
        var brightness = new BrightnessMonitor();

        _monitors = new IStatusMonitor[] { cpu, memory, battery, volume, microphone, network, ime };

        // 类型化注册，供 UI / 其他插件按需 Get + 订阅 Changed。
        context.Provide<ICpuMonitor>(cpu);
        context.Provide<IMemoryMonitor>(memory);
        context.Provide<IBatteryMonitor>(battery);
        context.Provide<IVolumeMonitor>(volume);
        context.Provide<IMicrophoneMonitor>(microphone);
        context.Provide<INetworkMonitor>(network);
        context.Provide<IImeMonitor>(ime);
        // 亮度：手动/事件驱动（PollIntervalMilliseconds=0），只注册服务不参与固定轮询。
        context.Provide<IBrightnessMonitor>(brightness);

        // 媒体播放控制公共服务（SMTC）：供菜单栏 / 第三方扩展 / 灵动岛经 IContext.Get<IMediaPlaybackService>() 消费。
        // 变更事件由服务内部按订阅者轮询（无订阅者零开销），不占用统一轮询器。
        context.Provide<IMediaPlaybackService>(new MediaPlaybackService());

        // 统一轮询器：检测变化 → 触发 Changed + 经 IEventBus 广播 "status.changed"。
        // 注意：不 Provide<IStatusPoller>——全仓零消费方（面板由 _poller 字段直接驱动），
        // 注册只会让接口成为"对外承诺但无人取"的准孤儿（W3 孤儿服务判别表 2026-09-03）。
        _poller = new StatusPoller(_monitors, context.Events);
        context.Provide<ISystemSource>(source);
        _poller.Start();

        // P1-A：接入音频事件回调（设备插拔/默认设备切换/音量静音/会话变化）。成功后音量/麦克风
        //        由事件即时触发轮询，原生轮询降级为兜底；失败则保持原有兜底轮询不受影响。
        if (AudioCoreNative.IsAvailable && AudioCoreNative.TryInitialize())
        {
            _audioEventWired = AudioCoreNative.SetChangeCallback(OnStatusChanged);
            if (_audioEventWired)
            {
                context.Logger.Info($"{Name}：音频已启用事件驱动（设备/音量/会话变化即时刷新）");
            }
        }

        // P1-B：接入网络变更回调（地址/路由变化：插拔网线、切换热点、IP 变更即时刷新）。
        //        失败则降级为原有 1s 兜底轮询，不影响采集。
        if (NetworkCoreNative.IsAvailable && NetworkCoreNative.SetChangeCallback(OnStatusChanged))
        {
            _networkEventWired = true;
            context.Logger.Info($"{Name}：网络已启用事件驱动（地址/路由变化即时刷新）");
        }

        context.Logger.Info($"{Name} 已加载：已注册 {_monitors.Length} 个状态监控项");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_audioEventWired)
        {
            AudioCoreNative.SetChangeCallback(null);
            AudioCoreNative.Shutdown();
            _audioEventWired = false;
        }
        if (_networkEventWired)
        {
            NetworkCoreNative.Shutdown();
            _networkEventWired = false;
        }
        _eventDebounce?.Dispose();
        _eventDebounce = null;
        _poller?.Dispose();
        _poller = null;
        _monitors = null;
        return Task.CompletedTask;
    }

    /// <summary>C13 修复：音频/网络事件回调经 150ms 防抖后再触发全量轮询，
    /// 避免高频事件（音量滑块拖动、网络续租抖动）直接引发全量采集风暴。</summary>
    private void OnStatusChanged()
    {
        _eventDebounce?.Dispose();
        _eventDebounce = new Timer(_ => _poller?.PollNow(), null, EventDebounceMs, Timeout.Infinite);
    }
}
