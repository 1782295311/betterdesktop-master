// BetterDesktop.Shell.Island — 格式转换来源：进度环 + 终态提示
//
// 事件契约见 packages/api/Convert/Events.cs（convert/progress|finished|failed|batch-finished）。
// 事件本身即注释写明"供给通知中心与灵动岛"，本类只做映射，不改转换实现的任何行为。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Island.Rendering;

namespace BetterDesktop.Shell.Island.Sources;

/// <summary>格式转换活动来源。</summary>
internal sealed class ConvertActivitySource : IActivitySource, IDisposable
{
    /// <summary>进度活动 Id（同 Id 更新进度，不重排队列）。</summary>
    private const string ProgressId = "island.convert.progress";

    /// <summary>单文件终态提示 Id。</summary>
    private const string DoneId = "island.convert.done";

    /// <summary>批量终态提示 Id。</summary>
    private const string BatchId = "island.convert.batch";

    /// <summary>进度活动 TTL：批量转换可能跑很久，给足余量（终态到达时会显式 Complete）。</summary>
    private static readonly TimeSpan ProgressTtl = TimeSpan.FromMinutes(30);

    /// <summary>终态提示存活时间。</summary>
    private static readonly TimeSpan DoneTtl = TimeSpan.FromSeconds(4.0);

    private readonly IEventBus _events;
    private readonly IActivityService _activity;
    private readonly IKernelLogger? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _started;

    public ConvertActivitySource(IEventBus events, IActivityService activity, IKernelLogger? logger)
    {
        _events = events;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>订阅四个转换事件（幂等）。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _subscriptions.Add(_events.On<ConvertProgressEventPayload>("convert/progress", OnProgress));
        _subscriptions.Add(_events.On<ConvertEventPayload>("convert/finished", OnFinished));
        _subscriptions.Add(_events.On<ConvertEventPayload>("convert/failed", OnFailed));
        _subscriptions.Add(_events.On<ConvertBatchEventPayload>("convert/batch-finished", OnBatchFinished));
    }

    /// <summary>退订全部（幂等，7438）。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _started = false;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private Task OnProgress(ConvertProgressEventPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            var name = FileNameOf(payload.Source);
            var title = $"转换 {name}";
            var phase = DescribePhase(payload.Phase);
            var body = payload.Percent is { } percent
                ? $"{phase} · {percent}%"
                : phase;

            _activity.Post(new ActivityItem(
                Id: ProgressId,
                Source: IslandContentMapper.SourceConvert,
                Kind: ActivityKind.Progress,
                Priority: ActivityPriority.Progress,
                Title: title,
                Body: body,
                IconPath: null,
                Progress: payload.Percent is { } p ? Math.Clamp(p / 100.0, 0.0, 1.0) : null,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: ProgressTtl,
                Actions: Array.Empty<ActivityAction>()));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 转换进度映射失败（已隔离）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task OnFinished(ConvertEventPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            _activity.Complete(ProgressId);
            // 标题固定 → 仲裁的同源合并会把连续多文件折叠成"转换完成 ×N"
            _activity.Post(new ActivityItem(
                Id: DoneId,
                Source: IslandContentMapper.SourceConvert,
                Kind: ActivityKind.Transient,
                Priority: ActivityPriority.Notice,
                Title: "转换完成",
                Body: $"{FileNameOf(payload.Source)} · {FormatElapsed(payload.ElapsedMs)}",
                IconPath: null,
                Progress: 1.0,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: DoneTtl,
                Actions: Array.Empty<ActivityAction>()));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 转换完成提示失败（已隔离）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task OnFailed(ConvertEventPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            _activity.Complete(ProgressId);
            // 失败要压过普通提示：Attention 优先级 + Failed 标记（呈现层据此走警示色与警示字形）
            _activity.Post(new ActivityItem(
                Id: DoneId,
                Source: IslandContentMapper.SourceConvert,
                Kind: ActivityKind.Transient,
                Priority: ActivityPriority.Attention,
                Title: "转换失败",
                Body: $"{FileNameOf(payload.Source)} · {Truncate(payload.Error, 48)}",
                IconPath: null,
                Progress: null,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: TimeSpan.FromSeconds(6.0),
                Actions: Array.Empty<ActivityAction>(),
                Failed: true));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 转换失败提示失败（已隔离）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task OnBatchFinished(ConvertBatchEventPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            _activity.Complete(ProgressId);
            var body = payload.Failed > 0
                ? $"成功 {payload.Succeeded} 项 · 失败 {payload.Failed} 项 · {FormatElapsed(payload.ElapsedMs)}"
                : $"成功 {payload.Succeeded} 项 · {FormatElapsed(payload.ElapsedMs)}";

            _activity.Post(new ActivityItem(
                Id: BatchId,
                Source: IslandContentMapper.SourceConvert,
                Kind: ActivityKind.Transient,
                Priority: payload.Failed > 0 ? ActivityPriority.Attention : ActivityPriority.Notice,
                Title: payload.Failed > 0 ? "转换完成（有失败）" : "转换完成",
                Body: body,
                IconPath: null,
                Progress: null,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: DoneTtl,
                Actions: Array.Empty<ActivityAction>(),
                Failed: payload.Failed > 0));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 批量完成提示失败（已隔离）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static string FileNameOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "文件";
        }

        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private static string DescribePhase(string? phase) => phase switch
    {
        "verifying" => "校验产物",
        "publishing" => "写入目标",
        "finalizing" => "收尾",
        "running" => "转换中",
        _ => "转换中",
    };

    private static string FormatElapsed(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "完成";
        }

        return milliseconds < 1000
            ? milliseconds.ToString(CultureInfo.InvariantCulture) + " ms"
            : (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    private static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "未知原因";
        }

        var trimmed = text.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }
}
