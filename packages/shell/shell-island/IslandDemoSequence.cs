// BetterDesktop.Shell.Island — 开发期动画走查（env 门控，默认零副作用）
//
// 【为什么需要它】岛的全部形态（到达 / 进度 / 不确定进度 / 成功 / 失败 / 同源折叠 / 展开 /
// 常驻媒体 / 会话进度 / 收出）都靠"有消息"驱动，真机上看全需要凑齐一堆外部条件。
// 本类用一段脚本按顺序把这些活动**通过真实的 IActivityService** 发出去（走完整仲裁与渲染链路），
// 让形态可以一次性看全。
//
// 【门控】只有设置环境变量 BD_ISLAND_DEMO 才启动；未设置时本类不被实例化，生产零副作用。
//
// 【与消息管线的边界】它只发"合成活动"，不代表任何来源已接通；管线是否接通与动画观感无关。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Island.Rendering;

namespace BetterDesktop.Shell.Island;

/// <summary>动画走查脚本（仅 BD_ISLAND_DEMO 存在时运行）。</summary>
internal sealed class IslandDemoSequence : IDisposable
{
    private const string SourceDemo = "demo";

    private readonly IActivityService _activity;
    private readonly IKernelLogger? _logger;
    private readonly CancellationTokenSource _cts = new();

    public IslandDemoSequence(IActivityService activity, IKernelLogger? logger)
    {
        _activity = activity;
        _logger = logger;
    }

    /// <summary>是否启用（env 门控）。</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BD_ISLAND_DEMO"));

    /// <summary>跑一轮完整走查（后台任务；每段开头的 <c>[demo] stage=</c> 日志便于对齐截图）。</summary>
    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    /// <inheritdoc />
    public void Dispose() => _cts.Cancel();

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            // 进入走查前先确保岛是干净的（无活动态）
            Complete("demo.notice", "demo.progress", "demo.scan", "demo.batch", "demo.media", "demo.merge");

            await StageAsync("notice", token).ConfigureAwait(false);        // 普通消息：到达 + 自动收出
            await StageAsync("progress", token).ConfigureAwait(false);      // 下载中：进度环 + 百分比
            await StageAsync("scan", token).ConfigureAwait(false);          // 不确定进度：旋转扫描
            await StageAsync("success", token).ConfigureAwait(false);       // 成功终态：对勾 + 常驻波形
            await StageAsync("merge", token).ConfigureAwait(false);         // 同源折叠：×3
            await StageAsync("copy", token).ConfigureAwait(false);          // 长文本（复制内容观感）
            await StageAsync("fail", token).ConfigureAwait(false);          // 失败终态：警示色 + 单次抖动
            await StageAsync("batch", token).ConfigureAwait(false);         // 会话式进度：1/5 → 5/5
            await StageAsync("media", token).ConfigureAwait(false);         // 常驻媒体：播放控制
            Log("done");
        }
        catch (OperationCanceledException)
        {
            // 卸载/退出：正常路径
        }
        catch (Exception ex)
        {
            _logger?.Warn($"shell.island: 动画走查异常（已隔离）：{ex.Message}");
        }
    }

    private async Task StageAsync(string stage, CancellationToken token)
    {
        Log(stage);
        switch (stage)
        {
            case "notice":
                _activity.Post(new ActivityItem(
                    "demo.notice", SourceDemo, ActivityKind.Transient, ActivityPriority.Notice,
                    "已连接到 Wi-Fi", "BetterDesktop-5G · 5 GHz", null, null,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(4.5), Array.Empty<ActivityAction>()));
                await Task.Delay(6500, token).ConfigureAwait(false);
                break;

            case "progress":
                // 下载类：确定的进度 + 阶段文案（每秒推进，模拟真实下载）
                for (var i = 0; i <= 100; i += 4)
                {
                    _activity.Post(new ActivityItem(
                        "demo.progress", SourceDemo, ActivityKind.Progress, ActivityPriority.Notice,
                        "正在下载更新", $"系统更新 {24.8 + i * 0.36:0.0} MB / {60.8:0.0} MB", null, i / 100.0,
                        DateTimeOffset.Now, TimeSpan.FromSeconds(30), Array.Empty<ActivityAction>()));
                    await Task.Delay(140, token).ConfigureAwait(false);
                }

                await Task.Delay(1200, token).ConfigureAwait(false);
                break;

            case "scan":
                _activity.Post(new ActivityItem(
                    "demo.scan", SourceDemo, ActivityKind.Progress, ActivityPriority.Notice,
                    "正在扫描附近设备", "发现 0 个设备…", null, null,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(8), Array.Empty<ActivityAction>()));
                await Task.Delay(5500, token).ConfigureAwait(false);
                break;

            case "success":
                _activity.Post(new ActivityItem(
                    "demo.progress", SourceDemo, ActivityKind.Progress, ActivityPriority.Notice,
                    "下载完成", "系统更新 60.8 MB · 用时 3 秒", null, 1.0,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(4.5), Array.Empty<ActivityAction>()));
                await Task.Delay(6000, token).ConfigureAwait(false);
                break;

            case "merge":
                // 同源同标题连续三次 → 岛上折叠成"已保存到剪贴板 ×3"
                for (var i = 0; i < 3; i++)
                {
                    _activity.Post(new ActivityItem(
                        "demo.merge", SourceDemo, ActivityKind.Transient, ActivityPriority.Notice,
                        "已保存到剪贴板", null, null, null,
                        DateTimeOffset.Now, TimeSpan.FromSeconds(6), Array.Empty<ActivityAction>()));
                    await Task.Delay(700, token).ConfigureAwait(false);
                }

                await Task.Delay(5000, token).ConfigureAwait(false);
                break;

            case "copy":
                // 复制内容观感（演示用）：标题=内容预览，正文=类型。生产路径默认不发这类活动（隐私）。
                _activity.Post(new ActivityItem(
                    "demo.copy", SourceDemo, ActivityKind.Transient, ActivityPriority.Notice,
                    "https://betterdesktop.dev/release/v1.3.0", "已复制链接", null, null,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(5), Array.Empty<ActivityAction>()));
                await Task.Delay(6500, token).ConfigureAwait(false);
                break;

            case "fail":
                _activity.Post(new ActivityItem(
                    "demo.fail", SourceConvertOrDemo(), ActivityKind.Transient, ActivityPriority.Attention,
                    "下载失败", "网络连接已断开 · 稍后重试", null, null,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(6), Array.Empty<ActivityAction>(),
                    MergeCount: 1, Failed: true));
                await Task.Delay(7000, token).ConfigureAwait(false);
                break;

            case "batch":
                // 会话式进度（按序粘贴同款呈现）：第 1/5 → 第 5/5
                for (var i = 1; i <= 5; i++)
                {
                    _activity.Post(new ActivityItem(
                        "demo.batch", IslandContentMapper.SourcePasteSession, ActivityKind.Progress,
                        ActivityPriority.Notice, "按序粘贴", $"第 {i}/5 项 · 到目标窗口按 Ctrl+V", null,
                        i / 5.0, DateTimeOffset.Now, TimeSpan.FromSeconds(30), Array.Empty<ActivityAction>()));
                    await Task.Delay(1100, token).ConfigureAwait(false);
                }

                await Task.Delay(2000, token).ConfigureAwait(false);
                break;

            case "media":
                _activity.Post(new ActivityItem(
                    "demo.media", IslandContentMapper.SourceMedia, ActivityKind.Sticky, ActivityPriority.Media,
                    "夜曲", "周杰伦 · 十一月的萧邦", null, null,
                    DateTimeOffset.Now, TimeSpan.FromSeconds(12), BuildMediaActions()));
                await Task.Delay(12000, token).ConfigureAwait(false);
                break;

            default:
                break;
        }

        // 每段结束把本段的活动收掉，保证段与段之间岛是干净的
        Complete(
            "demo.notice", "demo.progress", "demo.scan", "demo.batch", "demo.merge", "demo.copy", "demo.fail");
    }

    private static IReadOnlyList<ActivityAction> BuildMediaActions() => new[]
    {
        new ActivityAction("prev", "上一首", () => Task.CompletedTask),
        new ActivityAction("play", "播放 / 暂停", () => Task.CompletedTask),
        new ActivityAction("next", "下一首", () => Task.CompletedTask),
    };

    private static string SourceConvertOrDemo() => "convert";

    private void Complete(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                _activity.Complete(id);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"shell.island: 走查清理失败 {id}（已隔离）：{ex.Message}");
            }
        }
    }

    private void Log(string stage) => _logger?.Info($"shell.island: [demo] stage={stage}");
}
