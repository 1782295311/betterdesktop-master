// BetterDesktop.Shell.Island — 「按序粘贴 / 按格粘」会话来源（唯一的剪贴板类来源）
//
// 【为什么不是"复制就上屏"】2026-09-16 用户明确否决：随便复制就弹胶囊会把剪贴板内容暴露在屏幕上，
// 属隐私事故。用户要的是**自己主动发起的按序粘贴会话**在岛上有进度感（"第 2/5 项"）。
// 因此本来源只消费 ClipboardPasteSessionNotice —— 载荷里**只有进度，没有内容**（见 api 该 DTO 注释）。
//
// 【状态从哪来】按序粘贴的状态机活在面板 exe 进程（ClipboardIpcClient），壳进程看不到；
// 面板经 BetterDesktop.MenuCmd 管道把进度推给宿主，宿主广播 shell.clipboard/paste-session（见 Bootstrap）。

using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Island.Rendering;

namespace BetterDesktop.Shell.Island.Sources;

/// <summary>按序粘贴会话来源（Progress：会话期间常驻，结束/取消即收起）。</summary>
internal sealed class PasteSessionActivitySource : IActivitySource, IDisposable
{
    private const string ActivityId = "island.paste-session";

    /// <summary>
    /// 会话存活上限：正常路径由"会话结束"显式收起（面板会推 Active=false）。
    /// 这是**面板进程被强杀/句柄泄漏时的兜底**——宁可 30 分钟后过期，也不能让岛永久挂着一条假进度。
    /// </summary>
    private static readonly TimeSpan SafetyTtl = TimeSpan.FromMinutes(30);

    private readonly IEventBus _events;
    private readonly IActivityService _activity;
    private readonly IKernelLogger? _logger;

    private Func<ClipboardPasteSessionNotice, CancellationToken, Task>? _handler;
    private IDisposable? _subscription;
    private bool _posted;

    public PasteSessionActivitySource(IEventBus events, IActivityService activity, IKernelLogger? logger)
    {
        _events = events;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>开始监听（幂等）。</summary>
    public void Start()
    {
        if (_subscription is not null)
        {
            return;
        }

        _handler = OnNotice;
        _subscription = _events.On<ClipboardPasteSessionNotice>(ShellEvents.ClipboardPasteSession, _handler);
    }

    /// <summary>停止监听（幂等）：退订并收起会话进度。</summary>
    public void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;
        _handler = null;

        if (_posted)
        {
            _activity.Complete(ActivityId);
            _posted = false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private Task OnNotice(ClipboardPasteSessionNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            if (!notice.Active || notice.Total <= 0)
            {
                // 会话结束/取消：收起（幂等）
                if (_posted)
                {
                    _posted = false;
                    _activity.Complete(ActivityId);
                    _logger?.Info($"shell.island: {Describe(notice.Cell)}会话结束，进度已收起");
                }

                return Task.CompletedTask;
            }

            _posted = true;
            var kind = Describe(notice.Cell);

            // 【D2 按序粘贴预览】标题优先显示"下一次 Ctrl+V 将粘出的内容"（截断到 ~20 字），
            // 这样收起态胶囊直接可读要粘什么；详情行保留进度与操作提示。
            // 没有预览（图片/文件等无文本）时退回类型名，绝不显示空白胶囊。
            var title = string.IsNullOrWhiteSpace(notice.Preview)
                ? kind
                : Truncate(notice.Preview!, 20);

            _activity.Post(new ActivityItem(
                Id: ActivityId,
                Source: IslandContentMapper.SourcePasteSession,
                Kind: ActivityKind.Progress,
                Priority: ActivityPriority.Notice,
                Title: title,
                Body: $"{kind} 第 {notice.Index}/{notice.Total} 项 · 到目标窗口按 Ctrl+V",
                IconPath: null,
                Progress: notice.Progress,
                CreatedAt: DateTimeOffset.Now,
                TimeToLive: SafetyTtl,
                Actions: Array.Empty<ActivityAction>()));
        }
        catch (Exception ex)
        {
            // 会话进度失败绝不能打断岛或仲裁（M10 降级）。
            _logger?.Warn($"shell.island: 按序粘贴进度上屏失败（已隔离）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static string Describe(bool cell) => cell ? "按格粘" : "按序粘贴";

    /// <summary>单行截断（换行压成空格：胶囊是单行文本，含换行会撑高头部行）。</summary>
    private static string Truncate(string text, int limit)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }
}
