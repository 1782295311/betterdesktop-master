// BetterDesktop.Shell.Core — 活动服务插件（唯一仲裁者的入图点）
//
// 【为什么单独成插件而不是塞进 ShellCorePlugin】`ShellCorePlugin` 在 cordis.yml 迁移后已无任何引用
// （全仓 grep 只有它自己的定义），把服务挂在死代码上等于没 Provide —— 2026-09-16 真机日志实证：
// 灵动岛拿到 null 并如实降级（"IActivityService 未入图"），岛根本没起来。
// 教训：新增跨包服务必须挂到**实际被加载的**插件上，并到 cordis.yml + Bootstrap.Factories 同步登记。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Shell.Core.Activity;

/// <summary>
/// 活动仲裁服务插件：Provide <see cref="IActivityService"/>、1 s TTL 心跳、变更经 IEventBus 广播。
/// <para>
/// 职责边界：本插件只"保管服务"——不产出活动（各来源各自 Post）、不呈现活动（灵容岛等表面各自消费）。
/// </para>
/// </summary>
public sealed class ActivityPlugin : IPlugin
{
    /// <summary>活动 TTL 心跳周期：1 s 粒度足够（TTL 以秒计），仲裁实现内部加锁故与线程无关。</summary>
    private const int TickIntervalMs = 1000;

    private ActivityService? _activity;
    private Timer? _tick;
    private IEventBus? _events;
    private IKernelLogger? _logger;

    /// <inheritdoc />
    public string Name => "shell.activity";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var activity = new ActivityService();
        _activity = activity;
        _events = context.Events;
        _logger = context.Logger;

        context.Provide<IActivityService>(activity);

        // 变更 → IEventBus 桥：接口按 ADR-002 D4 不声明裸 event，跨包消费走事件名（表面只订阅一次）。
        activity.Changed += OnActivityChanged;

        // TTL 心跳：Transient/Progress 到期自动收起、抑制解除后补播都靠它推进。
        _tick = new Timer(_ => OnTick(), null, TickIntervalMs, TickIntervalMs);

        context.Logger.Info($"{Name} 已加载：活动仲裁服务入图（TTL 心跳 {TickIntervalMs} ms，广播 {ShellEvents.ActivityChanged}）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;
            _activity = null;
        }

        _tick?.Dispose();
        _tick = null;
        _events = null;
        _logger = null;
        return Task.CompletedTask;
    }

    /// <summary>TTL 心跳：异常不得中断后续心跳（M10 降级）。</summary>
    private void OnTick()
    {
        try
        {
            _activity?.Tick();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"{Name}: 活动 TTL 心跳异常（已隔离，仲裁状态不受影响）：{ex.Message}");
        }
    }

    /// <summary>把"当前活动/队列顺序变化"广播出去（进度更新不触发，订阅方自行采样 Current）。</summary>
    private void OnActivityChanged()
    {
        var bus = _events;
        var service = _activity;
        if (bus is null || service is null)
        {
            return;
        }

        try
        {
            // 显式丢弃：总线内部已隔离各监听器异常，这里不等待（表面自行 marshal 到 UI 线程）。
            _ = bus.EmitAsync(ShellEvents.ActivityChanged, new ActivityChangedNotice(service.Current, service.Queue.Count));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"{Name}: 活动变更广播失败（已隔离）：{ex.Message}");
        }
    }
}
