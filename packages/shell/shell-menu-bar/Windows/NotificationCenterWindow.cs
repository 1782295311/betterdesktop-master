// BetterDesktop.Shell.MenuBar — 通知中心独立弹出面板（C11 通知图标双用入口的「右键」目标）
//
// 角色：通知图标（MenuBarStatusButtonId.Notification）同一图标空间的低频入口。
//   左键（高频）= 控制中心（ControlCenterWindow）；右键（低频）= 本通知中心。
//   左右键分发见 Services/StatusBarMenuBarExtension.cs OnButtonClicked 的 Notification 分支。
//
// 【当前状态 · 如实标注】系统通知源（WinRT UserNotificationListener）仍待接入；当前面板呈现的是
//   转换功能（shell-convert）的进度与完成消息——IEventBus convert/progress、finished、failed、
//   batch-finished 的消费端（S8：进度/完成 API 供给通知中心）。灵动岛按同一契约另行接入。
//   Phase 序列 running → verifying → publishing → finalizing；Percent 为 null 时不画假进度条。
//   系统通知接入后，在现有活动区/消息区之上追加系统通知列表即可。
// 与其它菜单栏面板一致：继承 MenuBarPopupWindow，外观走主题令牌（基类统一挂载）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（通知中心面板，白话 → 方法）──
//   "面板整体布局"        → BuildContent（标题行 + 活动转换区 + 完成消息区 / 空态）
//   "正在转换的行"        → BuildActivityRow（源文件名 → 目标 + 阶段徽标）
//   "完成/失败消息的行"   → BuildHistoryRow（✓/✕ + 摘要 + 相对时间）
//   "阶段徽标中文名"      → PhaseLabel（running→运行中 …）
//   "空态占位"            → BuildEmptyState（大铃铛 + 暂无通知）
//   事件订阅在构造函数（convert/progress|finished|failed|batch-finished），回调走 Dispatcher 重建。
//   面板基类 MenuBarPopupWindow；弹窗互斥/定位在 Services/StatusBarMenuBarExtension.cs。
// ────────────────────────────────────

/// <summary>
/// 通知中心独立面板（C11 双用图标 · 右键入口）。
/// 消费转换进度/完成事件（IEventBus convert/*）；系统通知源接入前，这是面板的主要真实内容。
/// </summary>
internal sealed class NotificationCenterWindow : MenuBarPopupWindow
{
    private const double PanelWidth = 320;
    private const int MaxHistory = 8;

    private readonly IEventBus? _events;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Dictionary<string, ActivityState> _activities = new();
    private readonly List<HistoryItem> _history = new();
    private readonly object _gate = new();

    /// <summary>进行中的转换（key = 源文件全路径，同文件同时只允许一个转换——与 ConversionService InFlight 一致）。</summary>
    private sealed record ActivityState(string? Target, string Engine, string Phase, long ElapsedMs);

    /// <summary>完成消息（ok=false 含批量部分失败——红线 13：部分成功不得当全成功）。</summary>
    private sealed record HistoryItem(bool Ok, string Title, string Detail);

    public NotificationCenterWindow(
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null,
        IEventBus? events = null)
        : base(vibrancy, appearance)
    {
        _events = events;
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 420;

        if (events is not null)
        {
            // 订阅转换事件；窗口为单例复用（ShowPopup 缓存），订阅长期有效，面板隐藏时状态继续累计。
            _subscriptions.Add(events.On<ConvertProgressEventPayload>("convert/progress", OnProgress));
            _subscriptions.Add(events.On<ConvertEventPayload>("convert/finished", OnFinished));
            _subscriptions.Add(events.On<ConvertEventPayload>("convert/failed", OnFailed));
            _subscriptions.Add(events.On<ConvertBatchEventPayload>("convert/batch-finished", OnBatchFinished));
        }
    }

    // ── 事件回调（可能非 UI 线程：状态入锁 + Dispatcher 重建） ──

    private Task OnProgress(ConvertProgressEventPayload e, CancellationToken _)
    {
        lock (_gate)
        {
            _activities[e.Source] = new ActivityState(e.Target, e.Engine, e.Phase, e.ElapsedMs);
        }
        Dispatcher.InvokeAsync(RebuildContent);
        return Task.CompletedTask;
    }

    private Task OnFinished(ConvertEventPayload e, CancellationToken _)
    {
        lock (_gate)
        {
            _activities.Remove(e.Source);
            _history.Insert(0, new HistoryItem(true,
                System.IO.Path.GetFileName(e.Source),
                $"→ {System.IO.Path.GetFileName(e.Target ?? "?")} · {e.Engine} · {e.ElapsedMs}ms"));
            TrimHistory();
        }
        Dispatcher.InvokeAsync(RebuildContent);
        return Task.CompletedTask;
    }

    private Task OnFailed(ConvertEventPayload e, CancellationToken _)
    {
        lock (_gate)
        {
            _activities.Remove(e.Source);
            _history.Insert(0, new HistoryItem(false,
                System.IO.Path.GetFileName(e.Source),
                e.Error ?? "转换失败"));
            TrimHistory();
        }
        Dispatcher.InvokeAsync(RebuildContent);
        return Task.CompletedTask;
    }

    private Task OnBatchFinished(ConvertBatchEventPayload e, CancellationToken _)
    {
        lock (_gate)
        {
            var allOk = e.Failed == 0; // 红线 13：部分成功不得当全成功
            _history.Insert(0, new HistoryItem(allOk,
                "批量转换",
                allOk
                    ? $"{e.Succeeded}/{e.Total} 成功 · {e.ElapsedMs}ms"
                    : $"{e.Succeeded}/{e.Total} 成功，{e.Failed} 失败 · {e.ElapsedMs}ms"));
            TrimHistory();
        }
        Dispatcher.InvokeAsync(RebuildContent);
        return Task.CompletedTask;
    }

    private void TrimHistory()
    {
        while (_history.Count > MaxHistory)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>按当前状态重建面板内容（事件回调经 Dispatcher 调用）。</summary>
    private void RebuildContent()
    {
        ApplyContent(BuildContent());
    }

    /// <inheritdoc />
    protected override FrameworkElement BuildContent()
    {
        List<KeyValuePair<string, ActivityState>> activities;
        List<HistoryItem> history;
        lock (_gate)
        {
            activities = _activities.OrderBy(kv => kv.Value.ElapsedMs).ToList();
            history = _history.ToList();
        }

        var root = new Border { Padding = new Thickness(14) };
        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 标题行：标题 + 内容副标。
        var title = new TextBlock
        {
            Text = "通知中心",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        SetThemeBinding(title, TextBlock.ForegroundProperty, "ThemeForeground");
        column.Children.Add(title);

        var hint = new TextBlock
        {
            Text = activities.Count > 0 ? "转换活动进行中" : "转换进度与完成消息",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(hint);

        // 无活动且无历史 → 空态占位（系统通知接入前的兜底）。
        if (activities.Count == 0 && history.Count == 0)
        {
            column.Children.Add(BuildEmptyState());
            root.Child = column;
            return root;
        }

        if (activities.Count > 0)
        {
            column.Children.Add(BuildSectionLabel("进行中"));
            foreach (var (source, state) in activities)
            {
                column.Children.Add(BuildActivityRow(source, state));
            }
        }

        if (history.Count > 0)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 8) };
            SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
            column.Children.Add(sep);
            column.Children.Add(BuildSectionLabel("最近完成"));
            foreach (var item in history)
            {
                column.Children.Add(BuildHistoryRow(item));
            }
        }

        root.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            Content = column
        };
        return root;
    }

    private static FrameworkElement BuildSectionLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 2)
        };
        return label;
    }

    /// <summary>进行中转换行：源文件名 → 目标（左，截断）+ 阶段徽标（右）。</summary>
    private FrameworkElement BuildActivityRow(string source, ActivityState state)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var badgeText = new TextBlock { Text = PhaseLabel(state.Phase), FontSize = 11 };
        SetThemeBinding(badgeText, TextBlock.ForegroundProperty, "AccentBrush");
        badge.Child = badgeText;
        DockPanel.SetDock(badge, System.Windows.Controls.Dock.Right);
        dock.Children.Add(badge);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = System.IO.Path.GetFileName(source),
            FontSize = 12.5,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(name, TextBlock.ForegroundProperty, "ThemeForeground");
        left.Children.Add(name);
        var target = new TextBlock
        {
            Text = $"→ {state.Target ?? "?"}",
            FontSize = 11,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(target, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        left.Children.Add(target);

        dock.Children.Add(left);
        return dock;
    }

    /// <summary>完成/失败消息行：✓/✕ + 标题 + 明细（失败醒目色）。</summary>
    private FrameworkElement BuildHistoryRow(HistoryItem item)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };

        var mark = new TextBlock
        {
            Text = item.Ok ? "✓" : "✕",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 6, 0)
        };
        SetThemeBinding(mark, TextBlock.ForegroundProperty, item.Ok ? "AccentBrush" : "StatusWarning");
        dock.Children.Add(mark);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = item.Title,
            FontSize = 12.5,
            MaxWidth = 250,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(titleText, TextBlock.ForegroundProperty, "ThemeForeground");
        textCol.Children.Add(titleText);
        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 11,
            MaxWidth = 250,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(detail, TextBlock.ForegroundProperty, item.Ok ? "ThemeMutedForeground" : "StatusWarning");
        textCol.Children.Add(detail);

        dock.Children.Add(textCol);
        return dock;
    }

    /// <summary>阶段 → 中文徽标（与 convert-engine 运行契约 phase 序列一致）。</summary>
    private static string PhaseLabel(string phase) => phase switch
    {
        "running" => "运行中",
        "verifying" => "校验中",
        "publishing" => "落盘中",
        "finalizing" => "收尾中",
        _ => phase,
    };

    /// <summary>空态占位：大铃铛 + 「暂无通知」+ 接入说明（无活动且无历史时的兜底，不写死假数据）。</summary>
    private FrameworkElement BuildEmptyState()
    {
        var wrap = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 24, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 铃铛轮廓（24×24 视口，Stroke 风格：钟体 + 铃舌）。
        var bell = new Path
        {
            Width = 46,
            Height = 46,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            Data = Geometry.Parse(
                "M 12 3 C 8.8 3 6 5.8 6 9 C 6 13.2 4.4 14.6 4.4 16.4 L 19.6 16.4 C 19.6 14.6 18 13.2 18 9 C 18 5.8 15.2 3 12 3 Z M 10 20 C 10.4 21 11.1 21.5 12 21.5 C 12.9 21.5 13.6 21 14 20")
        };
        SetThemeBinding(bell, Shape.StrokeProperty, "ThemeMutedForeground");
        wrap.Children.Add(bell);

        var empty = new TextBlock
        {
            Text = "暂无通知",
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(empty, TextBlock.ForegroundProperty, "ThemeForeground");
        wrap.Children.Add(empty);

        var note = new TextBlock
        {
            Text = "转换进度与完成消息将在此显示",
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(note, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        wrap.Children.Add(note);

        return wrap;
    }
}
