using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterDesktop.Ocr.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Capture.Ocr;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// OCR 面板：后台识别（不卡 UI）→ 行列表 + 复制全部/复制选中/关闭。
/// 复制走系统剪贴板 = 用户主动动作 → 引擎正常入库为文本条目（「OCR 识别结果进剪贴板历史」主链路）。
/// <para>
/// 【视觉 2026-09-15】与剪贴板面板同一套语言（<b>窗口表面</b>：跟随主题令牌，见 <see cref="CaptureUi"/>）：
/// 顶部状态行 + 识别中脉冲条、圆角可选中行（hover/选中过渡、长文本**换行**不再截断）、
/// 底部主次分明的胶囊动作条。此前是裸 WPF 默认控件 + 硬编码 #888/Red/黑体，与面板观感割裂。
/// </para>
/// </summary>
public sealed class OcrPanelWindow : Window
{
    /// <summary>状态语义（决定状态行颜色；不再就地写 Brushes.Red / #1A8A3C）。</summary>
    private enum StatusKind { Normal, Success, Danger }

    private readonly string _pngPath;
    private readonly Action _onClosed;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBlock _countBadge;
    private readonly Border _busyBar;
    private readonly Button _copyAll;
    private readonly Button _copyWhole;
    private readonly Button _copySplit;
    private readonly CancellationTokenSource _cts = new();
    private OcrResult? _result;

    /// <summary>识别完成（成功且非空）后回调文本——供会话流「OCR 挂同一截图条目」（DoD D5）。</summary>
    public event Action<string>? ResultCaptured;

    public OcrPanelWindow(string pngPath, Action onClosed)
    {
        _pngPath = pngPath;
        _onClosed = onClosed;

        Title = "OCR 识别";
        Width = 460;
        Height = 520;
        MinWidth = 380;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        // 覆盖层（全屏 Topmost）之上显示，否则 Show 后落在覆盖层下面不可见（2026-09-15 真机）
        Topmost = true;
        // 普通窗口（无 DWM 亚克力）→ 用**实色**内容底色，而不是带透明度的面板底色
        // （ThemePanelBackground 带 0.2 不透明度，用于 WPF 非透明窗口会叠在黑底上变成近黑）
        Background = ThemeBrushes.Get("ThemeContentBackground");

        var root = new DockPanel { LastChildFill = true };

        // ---- 顶部：状态行（左状态文字 + 右行数徽标） + 识别中脉冲条 ----
        var header = new Grid
        {
            Margin = new Thickness(
                EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceM,
                EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = CaptureUi.Label("识别中…", hud: false, EntryTheme.Scale.FontBody);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_status);

        _countBadge = CaptureUi.Muted(string.Empty, hud: false, EntryTheme.Scale.FontCaption);
        _countBadge.Background = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentSubtle);
        _countBadge.Foreground = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong);
        _countBadge.Padding = new Thickness(
            EntryTheme.Scale.BadgePadX, 1, EntryTheme.Scale.BadgePadX, 1);
        _countBadge.Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0);
        _countBadge.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_countBadge, 1);
        header.Children.Add(_countBadge);

        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        // 识别中的活动指示：2px 强调色脉冲条（WPF 默认 ProgressBar 模板过于笨重，且无「细线」形态）
        _busyBar = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(EntryTheme.AccentColor),
            Margin = new Thickness(EntryTheme.Scale.GutterX, 0, EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS),
            Opacity = 0,
        };
        _busyBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = CaptureUi.MotionEase(),
        });
        DockPanel.SetDock(_busyBar, System.Windows.Controls.Dock.Top);
        root.Children.Add(_busyBar);

        // ---- 底部：提示 + 动作条（先于列表加入，DockPanel LastChildFill 用列表填中部） ----
        var footer = new StackPanel
        {
            Margin = new Thickness(
                EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS,
                EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceM),
        };
        var hint = CaptureUi.Muted(
            "多选行后可：整体保存 = 按行序拼成一条 / 逐条保存 = 每行独立进历史，均可立即 Ctrl+V",
            hud: false,
            EntryTheme.Scale.FontSmall);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS);
        footer.Children.Add(hint);

        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 按钮间距统一走 ButtonGap（12px）：8px 实测仍显挤（用户 2026-09-15 两轮反馈）
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        _copyAll = CaptureUi.PillButton("复制全部", CopyAll, CaptureUi.Face.Primary);
        _copyAll.IsEnabled = false;
        // 首/末按钮把朝外的那一侧外边距归零 → 外缘与页脚内缩对齐（16px），组内两两仍是 12px
        _copyAll.Margin = new Thickness(0, 0, CaptureUi.ButtonGap / 2, 0);
        _copyWhole = CaptureUi.PillButton("整体保存", () => _ = CopySelectedWholeAsync());
        _copyWhole.IsEnabled = false;
        _copySplit = CaptureUi.PillButton("逐条保存", () => _ = CopySelectedSplitAsync());
        _copySplit.IsEnabled = false;
        left.Children.Add(_copyAll);
        left.Children.Add(_copyWhole);
        left.Children.Add(_copySplit);
        actions.Children.Add(left);

        var close = CaptureUi.PillButton("关闭", Close);
        close.Margin = new Thickness(CaptureUi.ButtonGap / 2, 0, 0, 0);
        Grid.SetColumn(close, 1);
        actions.Children.Add(close);

        footer.Children.Add(actions);
        DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(footer);

        // ---- 中部：识别行列表（自绘行：圆角 / hover / 选中过渡 / 长文本换行） ----
        _list = new ListBox
        {
            Margin = new Thickness(EntryTheme.Scale.GutterX, 0, EntryTheme.Scale.GutterX, 0),
            SelectionMode = SelectionMode.Multiple,
            FontSize = EntryTheme.Scale.FontBody,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = BuildRowStyle(),
            ItemTemplate = BuildRowTemplate(),
        };
        // 行内换行显示，故不出横向滚动条（附加属性不能写在初始化器里）
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => RefreshSelectionButtons();
        root.Children.Add(_list); // 最后一个子元素 → 填充中部

        Content = root;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _onClosed();
        };

        _ = RunOcrAsync();
    }

    /// <summary>
    /// 行容器样式（<b>唯一</b>行外观来源）：圆角 + hover 过渡 + 选中强调底 + 选中勾。
    /// <para>
    /// 【为什么要自绘】默认 ListBoxItem 是"直角边框 + 系统蓝底"（与主题无关的 Windows 经典色），
    /// 且多选时**没有任何选中指示**（只靠底色变化），用户看不出哪几行会被"整体保存"。
    /// </para>
    /// </summary>
    private static Style BuildRowStyle()
    {
        var rest = Colors.Transparent;
        var hover = EntryTheme.Blend(EntryTheme.ContentBackgroundColor,
            ThemeBrushes.TryGetColor("ControlBackgroundHover") ?? Color.FromRgb(0x48, 0x48, 0x4A), 0.75);
        var selected = EntryTheme.Blend(EntryTheme.ContentBackgroundColor, EntryTheme.AccentColor,
            EntryTheme.Scale.AccentMedium);

        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusM));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(rest));
        border.SetValue(Border.PaddingProperty,
            new Thickness(EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS + 2,
                          EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceS + 2));
        border.SetValue(FrameworkElement.MarginProperty,
            new Thickness(0, EntryTheme.Scale.SpaceXS / 2, 0, EntryTheme.Scale.SpaceXS / 2));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(presenter);

        var check = new FrameworkElementFactory(typeof(TextBlock), "Chk");
        check.SetValue(TextBlock.TextProperty, "\uE73E");
        check.SetValue(TextBlock.FontFamilyProperty, CaptureUi.IconFont);
        check.SetValue(TextBlock.FontSizeProperty, EntryTheme.Scale.FontSmall);
        check.SetValue(TextBlock.ForegroundProperty, ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong));
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0));
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(check);

        border.AppendChild(grid);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };

        // hover：150ms 颜色过渡（禁止 0ms 瞬变）
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.EnterActions.Add(Fade("Bd", hover));
        hoverTrigger.ExitActions.Add(Fade("Bd", rest));
        template.Triggers.Add(hoverTrigger);

        // 选中：强调底 + 勾（单行光标移出后仍保持，多选时能一眼看全）
        var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(selected)) { TargetName = "Bd" });
        selTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "Chk" });
        template.Triggers.Add(selTrigger);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeBrushes.Get("ThemeForeground")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        return style;
    }

    /// <summary>
    /// 行内容模板：**换行**显示整行文字。
    /// <para>
    /// 【为什么必须换行 · 2026-09-15】原实现用 <c>DisplayMemberPath="Text"</c> → 每行一个单行 TextBlock，
    /// 识别出来的长句会被**直接截断**且无法查看/复制完整内容（OCR 结果恰恰经常是长句）。
    /// </para>
    /// </summary>
    private static DataTemplate BuildRowTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        return new DataTemplate(typeof(OcrBlockItem)) { VisualTree = text };
    }

    private static BeginStoryboard Fade(string targetName, Color to)
    {
        var animation = new ColorAnimation(to, TimeSpan.FromMilliseconds(EntryTheme.Scale.MotionFastMs))
        {
            EasingFunction = CaptureUi.MotionEase(),
        };
        Storyboard.SetTargetName(animation, targetName);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Background.Color"));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return new BeginStoryboard { Storyboard = storyboard };
    }

    /// <summary>状态行唯一刷新点（语义色 + 文本）。</summary>
    private void SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        _status.Foreground = kind switch
        {
            StatusKind.Success => new SolidColorBrush(ThemeBrushes.SuccessColor),
            StatusKind.Danger => new SolidColorBrush(ThemeBrushes.DangerColor),
            _ => ThemeBrushes.Get("ThemeMutedForeground"),
        };
    }

    /// <summary>识别结束：停掉脉冲条（并清空其动画，避免后台永远跑动画）。</summary>
    private void StopBusy()
    {
        _busyBar.BeginAnimation(OpacityProperty, null);
        _busyBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>选中相关的动作按钮随选中数启停（没选中就不该看起来能点）。</summary>
    private void RefreshSelectionButtons()
    {
        var has = _list.SelectedItems.Count > 0;
        _copyWhole.IsEnabled = has && _result?.Success == true;
        _copySplit.IsEnabled = has && _result?.Success == true;
    }

    private async Task RunOcrAsync()
    {
        try
        {
            var service = new WindowsMediaOcr();
            var result = await service.RecognizeAsync(_pngPath, new OcrOptions(), _cts.Token).ConfigureAwait(false);
            await Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowResult(result));
        }
        catch (Exception ex)
        {
            await Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                StopBusy();
                SetStatus("识别失败", StatusKind.Danger);
                _list.Items.Add(new OcrBlockItem("错误：" + ex.Message));
            });
        }
    }

    private void ShowResult(OcrResult result)
    {
        _result = result;
        StopBusy();
        if (!result.Success)
        {
            SetStatus("识别失败", StatusKind.Danger);
            _list.Items.Add(new OcrBlockItem("错误：" + result.DegradeReason));
            return;
        }
        if (result.Blocks.Count == 0)
        {
            SetStatus("未识别到文字", StatusKind.Normal);
            return;
        }
        SetStatus($"识别完成：{result.Blocks.Count} 行", StatusKind.Success);
        _countBadge.Text = $"{result.Blocks.Count} 行";
        _countBadge.Visibility = Visibility.Visible;
        foreach (var b in result.Blocks)
        {
            _list.Items.Add(new OcrBlockItem(b.Text));
        }
        // 「复制全部」以"有可用结果"为启用条件（识别失败时它点了也没有意义）
        _copyAll.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            ResultCaptured?.Invoke(result.Text);
        }
    }

    private void CopyAll()
    {
        if (_result is null || !_result.Success)
        {
            return;
        }
        _ = CopyToClipboardCoreAsync(_result.Text);
    }

    /// <summary>选中行按显示顺序拼接成一条整体保存（一次 Ctrl+V 全部粘贴）。</summary>
    private async Task CopySelectedWholeAsync()
    {
        var text = BuildSelectedText();
        if (text is not null)
        {
            await CopyToClipboardCoreAsync(text);
        }
    }

    /// <summary>选中行按显示顺序逐条切分保存（每行独立进剪贴板历史，可分别 Ctrl+V）。</summary>
    private async Task CopySelectedSplitAsync()
    {
        var items = OrderedSelected();
        if (items.Count == 0)
        {
            return;
        }
        int ok = 0;
        int fail = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                // 条间间隔让引擎监听稳定抓取；await 不阻塞 UI 渲染
                await Task.Delay(80);
            }
            if (await CopyToClipboardCoreAsync(items[i].Text))
            {
                ok++;
            }
            else
            {
                fail++;
            }
        }
        if (fail == 0)
        {
            SetStatus($"已按选中逐条保存 {ok} 条到剪贴板历史，可分别 Ctrl+V", StatusKind.Success);
        }
        else
        {
            SetStatus($"逐条保存部分失败（成功 {ok}/{items.Count}）", StatusKind.Danger);
        }
    }

    private List<OcrBlockItem> OrderedSelected() => _list.SelectedItems
        .Cast<OcrBlockItem>()
        .OrderBy(x => _list.Items.IndexOf(x)) // 「按序」= 行显示顺序，而非点击选择顺序
        .ToList();

    private string? BuildSelectedText()
    {
        var items = OrderedSelected();
        return items.Count == 0 ? null : string.Join("\n", items.Select(x => x.Text));
    }

    /// <summary>
    /// 写剪贴板核心——成功判据与引擎历史同源（2026-09-15 架构对齐）：
    /// 引擎历史 = 系统剪贴板快照流，截图/OCR 都经此链路共享数据。因此「复制成功」的唯一判据是
    /// 「剪贴板内容 == 目标文本」（引擎同步快照 → 历史完整 → 用户可 Ctrl+V），
    /// 而不是「SetText API 没抛异常」。分段写入时 API 会抛 CLIPBRD_E_CANT_OPEN(0x800401D0)
    /// 但内容已完整写入——旧代码把 API 异常当失败、历史却已记录，才出现「界面失败但历史有」的矛盾。
    /// SetText 只作为尽力写入手段（异常吞掉记日志），成败完全由内容验证决定。
    /// </summary>
    private async Task<bool> CopyToClipboardCoreAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        for (int i = 0; i < 10; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                // 系统剪贴板是共享资源（其他进程随时读写，尤其文本）→ 竞争是常态；
                // 是否成功不看这里，看下面的内容验证（与引擎历史同源判据）
                CaptureLog.Warn($"SetText 第 {i + 1} 次竞争异常（继续验证内容）：{ex.Message}");
            }
            if (VerifyClipboardContains(text))
            {
                MarkCopied(text);
                return true;
            }
            await Task.Delay(100 + i * 50);
        }
        // 最终兜底：竞争窗口彻底过去后内容仍不在 → 真失败（此时历史也不会有本条）
        foreach (int delay in new[] { 600, 2000 })
        {
            await Task.Delay(delay);
            if (VerifyClipboardContains(text))
            {
                MarkCopied(text);
                return true;
            }
        }
        CaptureLog.Error("OCR 复制失败：10 次写入后剪贴板内容仍非目标文本");
        SetStatus("复制失败，请重试", StatusKind.Danger);
        return false;
    }

    /// <summary>读回剪贴板验证完整文本已在（分段写入成功判据）。</summary>
    private static bool VerifyClipboardContains(string text)
    {
        try
        {
            return System.Windows.Clipboard.GetText() == text;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>复制成功反馈：状态更新独立 try——绝不让 UI 步骤异常掩盖"已复制"结论。</summary>
    private void MarkCopied(string text)
    {
        try
        {
            SetStatus("已复制（完整），可在剪贴板历史中搜索/粘贴", StatusKind.Success);
        }
        catch
        {
            // 状态栏更新失败不影响复制结果
        }
    }

    private sealed record OcrBlockItem(string Text);
}
