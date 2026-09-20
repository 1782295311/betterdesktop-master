// 声音面板：主功能是“应用”会话音量混音器（按进程图标/名称/音量/静音），
// 由 AudioCore 原生模块提供；主音量滑块与输出设备列表属冗余（Windows 托盘已覆盖），已移除。
// 另含 SMTC 正在播放（IMediaPlaybackService）与播放控制。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Native;
using Windows.Storage.Streams;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（声音面板，白话 → 方法）──
//   "上一首/播放/下一首控件"    → MakeControls；播放/暂停字形 GetGlyph
//   "每个应用的独立音量行"      → BuildAppRow；设置应用音量 SetAppVolume
//   "音频轮询后台工作线程"      → AudioWorkerLoop（经 SubmitAudioRefresh 归并到 UI 线程；B8 已修：CloseSelf 置 _audioWorkerRunning=false 并 Pulse 让其退出）
//   "重新加载音频会话/设备"     → ReloadAudio；主音量 SetVolume；外部数据入口 Attach
//   数据模型 SoundPanelViewModel。面板族同构见 MemoryPanelWindow；B8 已修：隐藏 Pause/显示 Resume/关闭 CloseSelf。
// ────────────────────────────────────

internal sealed class SoundPanelWindow : MenuBarPopupWindow
{
    private SoundPanelViewModel? _vm;
    private readonly IMediaPlaybackService? _media;

    public SoundPanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null, IMediaPlaybackService? media = null)
        : base(vibrancy, appearance)
    {
        _media = media;
        Width = 320;
        MinWidth = 320;
        SizeToContent = SizeToContent.Height;
        // B8：隐藏即停 1s 轮询（worker 无任务时本就在 Monitor.Wait 休眠，停 timer 即不再投递）；
        // 显示恢复并立即刷新；关闭时停 timer 并让音频 worker 线程退出、释放控件树闭包。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _vm?.Resume();
            else _vm?.Pause();
        };
        Closed += (_, _) => _vm?.CloseSelf();
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var sessions = new StackPanel();

        // 主音量：胶囊式控件（"声音"标签 + 数值 + 圆形拇指滑杆 + 麦克风图标）
        var volumeValue = new TextBlock
        {
            Text = "50",
            // 数值走主题前景（与窗口属性一致）
            Foreground = NativePanelStyles.TextPrimary,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var volumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            SmallChange = 1,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            MinHeight = 22,
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        var micGlyph = new TextBlock
        {
            Text = "\uE720",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = NativePanelStyles.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var micIcon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Cursor = Cursors.Hand,
            ToolTip = "点击切换麦克风静音",
            VerticalAlignment = VerticalAlignment.Center,
            // 麦克风圆底：内容层背景走主题令牌（与窗口属性一致）
            Child = micGlyph
        };
        micIcon.SetResourceReference(Border.BackgroundProperty, "ThemeContentBackground");
        // 点击翻转麦克风静音（替代原纯装饰图标；控制中心移除内嵌声音卡片后，麦克风静音在此唯一承载）
        micIcon.MouseLeftButtonUp += (_, _) => _vm?.ToggleMicMute();
        var volumeRow = new Grid();
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(volumeValue, 0); volumeRow.Children.Add(volumeValue);
        Grid.SetColumn(volumeSlider, 1); volumeRow.Children.Add(volumeSlider);
        Grid.SetColumn(micIcon, 2); volumeRow.Children.Add(micIcon);

        var volumePill = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(8, 4, 8, 4),
            // 主音量胶囊：透明背景，与窗口属性一致（融入面板毛玻璃背景，无独立色块）
            Background = Brushes.Transparent
        };
        var pillStack = new StackPanel();
        pillStack.Children.Add(new TextBlock
        {
            Text = "声音",
            Foreground = NativePanelStyles.TextSecondary,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6)
        });
        pillStack.Children.Add(volumeRow);
        volumePill.Child = pillStack;

        // 正在播放（SMTC）：封面 + 歌名/艺术家 · 应用 + 可拖动进度条 + 播放模式按钮
        var cover = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, 56, 56)) { RadiusX = 10, RadiusY = 10 }
        };
        cover.SetResourceReference(Border.BackgroundProperty, "ThemeContentBackground");
        cover.Child = CoverPlaceholderGlyph();

        var mediaTitle = new TextBlock
        {
            Text = "—",
            Foreground = NativePanelStyles.TextPrimary,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 1)
        };
        var mediaSub = new TextBlock
        {
            Text = "",
            Foreground = NativePanelStyles.TextSecondary,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        textCol.Children.Add(mediaTitle);
        textCol.Children.Add(mediaSub);

        var mediaTop = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        mediaTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mediaTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(cover, 0); mediaTop.Children.Add(cover);
        Grid.SetColumn(textCol, 1); mediaTop.Children.Add(textCol);

        // 进度条（可拖动 seek，0-1000 映射曲目位置/时长）+ 时间标签
        var progressSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            SmallChange = 5,
            LargeChange = 50,
            IsEnabled = false,
            MinHeight = 22,
            Margin = new Thickness(2, 0, 2, 0),
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        var timeLabel = new TextBlock
        {
            Text = "--:-- / --:--",
            FontSize = 10,
            Foreground = NativePanelStyles.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var (prevBtn, playBtn, nextBtn) = MakeControls();
        // 播放模式：随机 / 循环（状态高亮由 Attach 里 IsShuffle/IsRepeat 驱动）
        var shuffleBtn = MakeGlyphButton("\uE8B1");
        var repeatBtn = MakeGlyphButton("\uE8EE");

        var root = NativePanelStyles.Root(withColumn: col =>
        {
            col.Children.Add(NativePanelStyles.Title("声音"));

            // 主音量胶囊
            col.Children.Add(volumePill);

            col.Children.Add(NativePanelStyles.Separator(top: 8, bottom: 4));

            // 应用（按进程的真实音频会话，名称/音量/静音）——本面板主功能
            col.Children.Add(new TextBlock
            {
                Text = "应用",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 2)
            });
            col.Children.Add(sessions);

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 6));

            // 正在播放（SMTC 会话）：封面 + 信息 + 可拖动进度条 + 播放控制
            col.Children.Add(new TextBlock
            {
                Text = "正在播放",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            });

            // 实时视图：封面 + 信息 + 进度条 + 播放控制（无会话时由控件自身的空态文案承接）
            var ctrl = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(mediaTop);
            col.Children.Add(progressSlider);
            col.Children.Add(timeLabel);
            col.Children.Add(ctrl);
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(shuffleBtn, 0);
            Grid.SetColumn(prevBtn, 1);
            Grid.SetColumn(playBtn, 2);
            Grid.SetColumn(nextBtn, 3);
            Grid.SetColumn(repeatBtn, 4);
            ctrl.Children.Add(shuffleBtn);
            ctrl.Children.Add(prevBtn);
            ctrl.Children.Add(playBtn);
            ctrl.Children.Add(nextBtn);
            ctrl.Children.Add(repeatBtn);

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 4));

            var prefLink = new TextBlock
            {
                Text = "声音偏好设置…",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 0, 0),
                Opacity = 0.9,
                Cursor = Cursors.Hand
            };
            prefLink.MouseLeftButtonUp += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
                catch { /* 忽略：系统不支持时静默降级 */ }
            };
            col.Children.Add(prefLink);
        });

        var vm = _vm = new SoundPanelViewModel(_media);
        vm.Attach(v =>
        {
            // 主音量：拖动中（已捕获鼠标）不覆盖数值与滑块，避免 1s 轮询把滑块/读数拉回（对齐控制中心先例）。
            if (!volumeSlider.IsMouseCaptureWithin)
            {
                volumeValue.Text = v.VolumePct.ToString("0");
                if (Math.Abs(volumeSlider.Value - v.VolumePct) > 0.5)
                    volumeSlider.Value = v.VolumePct;
            }

            // 应用列表：有行正在拖动时本轮跳过全量重建（重建会销毁正在拖的滑块、丢失鼠标捕获）。
            if (v.AppDraggingCount == 0)
            {
                sessions.Children.Clear();
                foreach (var a in v.Apps) sessions.Children.Add(BuildAppRow(a, vm));
            }

            // 空态：无活动会话时由实时视图内控件自身的空态文案承接（"没有正在播放的媒体"等）。
            mediaTitle.Text = v.NowPlayingTitle;
            mediaSub.Text = v.NowPlayingSub;
            playBtn.Child = GetGlyph(v.Playing);

            // 播放控制能力态：播放器明确不支持某操作时置灰；无会话全灰（SMTC 无真实能力位时
            // CanPlay/CanPause/CanNext/CanPrev 为 false，避免"点了没反应"的错觉）。
            var session = v.ActiveMedia;
            ApplyButtonEnabled(prevBtn, session?.CanPrev == true);
            ApplyButtonEnabled(nextBtn, session?.CanNext == true);
            ApplyButtonEnabled(playBtn, session is not null && (session.CanPlay || session.CanPause));

            UpdateMediaExtras(v, cover, progressSlider, timeLabel, shuffleBtn, repeatBtn, micIcon, micGlyph);
        });

        // 流畅滑杆：拖动过程中只更新百分比显示，拖动结束/点击跳转时才设置主音量
        NativePanelStyles.ConfigureSmoothSlider(volumeSlider,
            onValueCommitted: v =>
            {
                var pct = (int)Math.Round(v);
                volumeValue.Text = pct.ToString("0");
                if (pct != vm.VolumePct) vm.SetVolume(pct);
            },
            onValueChanging: v =>
            {
                volumeValue.Text = ((int)Math.Round(v)).ToString("0");
            });

        // 进度条：拖动中只预览时间标签，拖动结束/点击跳转才下发 Seek
        NativePanelStyles.ConfigureSmoothSlider(progressSlider,
            onValueCommitted: v =>
            {
                var session = vm.ActiveMedia;
                if (session is null || session.EndTime is not { Ticks: > 0 } || !session.CanSeek) return;
                var target = TimeSpan.FromTicks((long)(session.EndTime.Value.Ticks * v / 1000.0));
                _ = vm.SendMediaCommandAsync(MediaPlaybackCommand.Seek, target);
            },
            onValueChanging: v =>
            {
                var session = vm.ActiveMedia;
                var total = session?.EndTime;
                timeLabel.Text = total is { Ticks: > 0 }
                    ? $"{FormatTime(TimeSpan.FromTicks((long)(total.Value.Ticks * v / 1000.0)))} / {FormatTime(total)}"
                    : "--:-- / --:--";
            });

        prevBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaPlaybackCommand.Previous);
        nextBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaPlaybackCommand.Next);
        playBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaPlaybackCommand.Toggle);
        shuffleBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaPlaybackCommand.ToggleShuffle);
        repeatBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaPlaybackCommand.ToggleRepeat);

        return root;
    }

    private static UIElement GetGlyph(bool playing)
    {
        return new TextBlock
        {
            Text = playing ? "\uE769" : "\uE768", // Pause / Play（Segoe MDL2 Assets）
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            // 播放中 Pause 用强调色（主按钮视觉），暂停时主题前景
            Foreground = playing ? NativePanelStyles.AccentBack : NativePanelStyles.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static (Border prev, Border play, Border next) MakeControls()
    {
        // MDL2 字形：Previous / Play（暂停态由 GetGlyph 切换）/ Next
        var prev = MakeGlyphButton("\uE892");
        var play = MakeGlyphButton("\uE768", w: 46);
        var next = MakeGlyphButton("\uE893");
        return (prev, play, next);
    }

    /// <summary>MDL2 字形圆形按钮（播放控制行通用工厂；字形颜色由 SetGlyphAccent 动态高亮）。
    /// 悬停/按下反馈对齐控制中心：Enter→Hover、Leave→透明、Down→Pressed、Up→点击。</summary>
    private static Border MakeGlyphButton(string glyph, int w = 38)
    {
        var btn = new Border
        {
            Width = w,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = NativePanelStyles.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        btn.MouseEnter += (_, _) => { if (btn.IsHitTestVisible) btn.Background = NativePanelStyles.RowHover; };
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        btn.MouseLeftButtonDown += (_, _) => btn.Background = PressedBack;
        btn.MouseLeftButtonUp += (_, _) => btn.Background = NativePanelStyles.RowHover;
        return btn;
    }

    /// <summary>播放控制按钮能力态：禁用时置灰 + 阻断点击（IsHitTestVisible=false 同时阻断 hover 反馈）。</summary>
    private static void ApplyButtonEnabled(Border btn, bool enabled)
    {
        btn.Opacity = enabled ? 1.0 : 0.35;
        btn.Cursor = enabled ? Cursors.Hand : null;
        btn.IsHitTestVisible = enabled;
    }

    /// <summary>按钮按下态底色（比 RowHover 更深的半透明黑）。</summary>
    private static readonly Brush PressedBack = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0));

    /// <summary>按钮字形开/关高亮（开启时强调色，不重建控件树，避免闪烁）。</summary>
    private static void SetGlyphAccent(Border btn, bool on)
    {
        if (btn.Child is TextBlock tb)
            tb.Foreground = on ? NativePanelStyles.AccentBack : NativePanelStyles.TextPrimary;
    }

    private static FrameworkElement CoverPlaceholderGlyph()
    {
        return new Viewbox
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            Child = new TextBlock
            {
                Text = "\uE8D6", // MusicInfo
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = NativePanelStyles.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>实时封面防重键（歌曲身份 Title|Artist|AppName；引用相等不可靠，见 UpdateCoverAsync）。</summary>
    private string? _coverKey;

    /// <summary>每帧刷新音乐区扩展与麦克风状态（进度条、随机/循环高亮、麦克风静音态、封面异步加载）。</summary>
    private void UpdateMediaExtras(
        SoundPanelViewModel v, Border cover, Slider progressSlider, TextBlock timeLabel,
        Border shuffleBtn, Border repeatBtn, Border micIcon, TextBlock micGlyph)
    {
        var session = v.ActiveMedia;
        var total = session?.EndTime;

        // 进度条/时间标签：拖动中不拉回（拖动预览由 onValueChanging 维护）；有时长且可 seek 才启用
        if (!progressSlider.IsMouseCaptureWithin && total is { Ticks: > 0 })
        {
            var frac = session!.Position is { } pos ? pos.Ticks / (double)total.Value.Ticks : 0d;
            progressSlider.Value = Math.Clamp(frac * 1000d, 0d, 1000d);
            timeLabel.Text = $"{FormatTime(session!.Position)} / {FormatTime(total)}";
        }
        progressSlider.IsEnabled = session is not null && session.CanSeek && total is { Ticks: > 0 };

        // 播放模式高亮：开启时强调色
        SetGlyphAccent(shuffleBtn, session?.IsShuffle == true);
        SetGlyphAccent(repeatBtn, session?.IsRepeat == true);

        // 麦克风：静音红色高亮，正常回主题背景
        if (v.MicMuted)
        {
            micIcon.Background = ThemeBrushes.Get("StatusDanger");
            micGlyph.Foreground = Brushes.White;
        }
        else
        {
            micIcon.SetResourceReference(Border.BackgroundProperty, "ThemeContentBackground");
            micGlyph.Foreground = NativePanelStyles.TextSecondary;
        }

        // 封面：引用变化才异步重载，失败/无封面回退占位
        _ = UpdateCoverAsync(cover, v);
    }

    private async Task UpdateCoverAsync(Border cover, SoundPanelViewModel v)
    {
        var session = v.ActiveMedia;
        var thumbRef = session?.ThumbnailRef;
        // 防重键 = 歌曲身份（Title|Artist|AppName）而非 ThumbnailRef 引用：
        // 切歌后系统可能复用同一 IRandomAccessStreamReference 实例（引用相等），
        // 若按引用判重，封面永不刷新、一直显示上一首的图。
        var key = session is null ? null : $"{session.Title}|{session.Artist}|{session.AppName}";
        if (string.Equals(_coverKey, key, StringComparison.Ordinal)) return;
        _coverKey = key;
        var bytes = await ReadThumbnailBytesAsync(thumbRef);
        if (!string.Equals(_coverKey, key, StringComparison.Ordinal)) return; // 已切歌：丢弃过期结果
        var img = ToBitmapImage(bytes);
        if (img is null)
        {
            cover.Child = CoverPlaceholderGlyph();
            return;
        }
        var image = new Image
        {
            Source = img,
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        cover.Child = image;
    }

    /// <summary>读取 SMTC 封面缩略图字节（后台 I/O，失败静默返回 null 由 UI 回退占位）。</summary>
    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            var size = (uint)Math.Min(stream.Size, 10 * 1024 * 1024); // 封面图不应过大，设 10MB 上限
            using var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    private static BitmapImage? ToBitmapImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static string FormatTime(TimeSpan? t)
    {
        if (t is not { } v || v < TimeSpan.Zero) return "--:--";
        return v.TotalHours >= 1 ? v.ToString(@"h\:mm\:ss") : v.ToString(@"mm\:ss");
    }

    private static FrameworkElement BuildAppRow(AudioSessionNative a, SoundPanelViewModel vm)
    {
        // 每行独立可调节：图标 | 名称+音量滑块 | 静音按钮。
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标：优先真实进程图标；系统提示音/无进程会话回退占位符。
        var appIcon = ProcessAppInfo.GetIcon(a.ProcessId);
        FrameworkElement icon;
        if (appIcon is not null)
        {
            icon = new Image
            {
                Source = appIcon,
                Width = 24,
                Height = 24,
                SnapsToDevicePixels = true,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        }
        else
        {
            icon = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                // 应用图标占位：前景色半透明（深色半透明在毛玻璃面板上几乎不可见）
                Background = ThemeBrushes.Tint("ThemeForeground", 0.24),
                Child = new TextBlock
                {
                    Text = "♫",
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
        row.Children.Add(icon);

        // 名称：优先可读的进程友好名，其次会话显示名。
        var friendlyName = ProcessAppInfo.GetName(a.ProcessId);
        var displayName = !string.IsNullOrWhiteSpace(friendlyName)
            ? friendlyName
            : (string.IsNullOrWhiteSpace(a.Name) ? "(未知应用)" : a.Name);

        // 初始值使用 ViewModel 裁定值（拖动中/未回读前用本地覆盖值，避免被定时器拉回）。
        var pct = vm.EffectiveAppVolumePct(a);
        var stack = new StackPanel();
        var name = new TextBlock
        {
            Text = displayName,
            FontSize = 11.5,
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        stack.Children.Add(name);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = pct,
            SmallChange = 1,
            LargeChange = 10,
            Margin = new Thickness(8, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 22,
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        stack.Children.Add(slider);
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        var muted = a.IsMuted;

        // 流畅滑杆：拖动过程中标记拖动状态 + 更新本地缓存，拖动结束/点击跳转时才提交到系统 API
        NativePanelStyles.ConfigureSmoothSlider(slider,
            onValueCommitted: v =>
            {
                var newPct = (int)Math.Round(v);
                pct = newPct;
                vm.SetAppVolume(a.ProcessId, newPct / 100f, muted);
                vm.MarkAppVolume(a.ProcessId, newPct);
                vm.MarkAppDragging(a.ProcessId, false);
            },
            onValueChanging: v =>
            {
                var newPct = (int)Math.Round(v);
                pct = newPct;
                vm.MarkAppVolume(a.ProcessId, newPct);
            });

        // 拖动开始时标记拖动状态（防止 1s 定时器用旧原生值把滑块拉回）
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => vm.MarkAppDragging(a.ProcessId, true)));
        slider.LostMouseCapture += (_, _) => vm.MarkAppDragging(a.ProcessId, false);

        var mute = new Border
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            // 静音：红色；未静音：浅灰（与白色滑杆基调一致）
            Background = muted ? ThemeBrushes.Get("StatusDanger")
                               : ThemeBrushes.Tint("ThemeForeground", 0.55),
            Child = new TextBlock
            {
                Text = muted ? "⍻" : "♩",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 静音红底用白字；未静音浅灰底用深字
                Foreground = muted ? Brushes.White : Brushes.DimGray
            },
            Cursor = Cursors.Hand,
            ToolTip = muted ? "取消静音" : "静音",
            VerticalAlignment = VerticalAlignment.Center
        };
        mute.MouseLeftButtonUp += (_, _) =>
        {
            if (a.ProcessId > 0) vm.ToggleAppMute(a.ProcessId, !muted, pct / 100f);
        };
        Grid.SetColumn(mute, 2);
        row.Children.Add(mute);
        return row;
    }
}

internal sealed class SoundPanelViewModel
{
    private readonly DispatcherTimer _timer;

    private readonly HashSet<int> _appDragging = new();
    private readonly Dictionary<int, int> _appVolumeOverride = new();

    private Action<SoundPanelViewModel>? _changed;
    private bool _mediaRefreshing;

    // ---- 音频枚举专用后台线程 ----
    // CoreAudio 的会话/设备枚举在音频服务异常（如音频设备切换、僵尸会话）时可无限阻塞。
    // 为保证 UI 永不冻结，所有原生音频调用统一投递到这一条专用 STA 线程串行执行；
    // 若某次调用卡死，只占住这一条后台线程，UI 线程与线程池均不受影响。
    private readonly Dispatcher _uiDispatcher;
    private readonly System.Threading.Thread _audioWorker;
    private readonly object _audioGate = new();
    private Action? _audioJob;
    private bool _audioWorkerBusy;
    private volatile bool _audioWorkerRunning = true;

    public int VolumePct { get; private set; } = 12;
    public IReadOnlyList<AudioSessionNative> Apps { get; private set; } = Array.Empty<AudioSessionNative>();

    /// <summary>麦克风（采集端点）是否静音；面板显示与切换静音的状态源。</summary>
    public bool MicMuted { get; private set; }

    /// <summary>当前正在拖动中的应用音量行数（UI 据此跳过全量重建，防止拖动中断）。</summary>
    public int AppDraggingCount => _appDragging.Count;

    public IReadOnlyList<MediaPlaybackSnapshot> MediaSessions { get; private set; } = Array.Empty<MediaPlaybackSnapshot>();
    public MediaPlaybackSnapshot? ActiveMedia { get; private set; }
    public string NowPlayingTitle => ActiveMedia?.Title ?? "没有正在播放的媒体";
    public string NowPlayingSub => ActiveMedia is null
        ? "打开音乐或视频应用后会显示在这里"
        : $"{ActiveMedia.Artist} · {ActiveMedia.AppName}".TrimStart(' ', '·');
    public bool Playing => ActiveMedia?.State == MediaPlaybackState.Playing;

    private readonly IMediaPlaybackService? _media;

    public SoundPanelViewModel(IMediaPlaybackService? media = null)
    {
        _media = media;
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _audioWorker = new System.Threading.Thread(AudioWorkerLoop)
        {
            IsBackground = true,
            Name = "AudioPollWorker"
        };
        _audioWorker.SetApartmentState(System.Threading.ApartmentState.STA);
        _audioWorker.Start();

        ReloadAudio();
        _ = RefreshMediaAsync();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) =>
        {
            ReloadAudio();
            await RefreshMediaAsync();
        };
        _timer.Start();
    }

    private void AudioWorkerLoop()
    {
        for (; ; )
        {
            Action? job;
            lock (_audioGate)
            {
                while (_audioWorkerRunning && _audioJob is null)
                    Monitor.Wait(_audioGate);
                if (!_audioWorkerRunning) return;
                job = _audioJob;
                _audioJob = null;
                _audioWorkerBusy = true;
            }
            try { job?.Invoke(); }
            catch { }
            lock (_audioGate)
            {
                _audioWorkerBusy = false;
                Monitor.PulseAll(_audioGate);
            }
        }
    }

    /// <summary>投递一次音频任务；worker 正忙（含底层卡死）时丢弃本轮，避免排队堆积与线程无限增长。</summary>
    private bool SubmitAudioRefresh(Action job)
    {
        lock (_audioGate)
        {
            if (_audioWorkerBusy || !_audioWorkerRunning) return false;
            _audioJob = job;
            Monitor.PulseAll(_audioGate);
            return true;
        }
    }

    // 主音量 + 会话枚举移到专用后台线程，结果回 UI 线程应用；worker 忙时跳过本轮，保留上一帧。
    // 应用列表按进程聚合后再渲染（同进程多会话并一行，见 AggregateSessions）。
    private void ReloadAudio()
    {
        SubmitAudioRefresh(() =>
        {
            var s = AudioCoreNative.GetStatus(AudioFlow.Render);
            var mic = AudioCoreNative.GetStatus(AudioFlow.Capture);
            var apps = AudioCoreNative.EnumerateSessions();
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                if (s.Ok) VolumePct = (int)Math.Round(s.VolumeFloat * 100f, MidpointRounding.AwayFromZero);
                MicMuted = mic.Ok && mic.Muted;
                Apps = AggregateSessions(apps);
                _changed?.Invoke(this);
            }));
        });
    }

    /// <summary>
    /// 把原生平铺会话列表按进程聚合为 UI 行：同一进程多会话并一行（对齐 103 聚合红线，
    /// 同一 App 多窗口不再重复出现）；无进程会话（系统声音）归并为一行。
    /// 设置侧 <see cref="AudioCoreNative.SetSessionVolume"/> 按 PID 对该进程全部会话生效，聚合不损失控制。
    /// </summary>
    internal static IReadOnlyList<AudioSessionNative> AggregateSessions(IReadOnlyList<AudioSessionNative> raw)
    {
        if (raw is null || raw.Count == 0) return Array.Empty<AudioSessionNative>();
        var byPid = new Dictionary<int, AudioSessionNative>();
        AudioSessionNative? system = null;
        foreach (var s in raw)
        {
            if (s.ProcessId <= 0)
            {
                // 系统声音等无进程会话：并入一行（保留首个非空显示名）。
                if (system is null || string.IsNullOrWhiteSpace(system.Value.Name))
                    system = s;
                continue;
            }
            if (!byPid.ContainsKey(s.ProcessId))
                byPid[s.ProcessId] = s;
        }
        var list = new List<AudioSessionNative>(byPid.Count + (system is null ? 0 : 1));
        list.AddRange(byPid.Values);
        if (system is not null) list.Add(system.Value);
        return list;
    }

    /// <summary>切换麦克风静音（保持当前采集音量不变），随后回读系统真值刷新 UI。</summary>
    public void ToggleMicMute()
    {
        SubmitAudioRefresh(() =>
        {
            var s = AudioCoreNative.GetStatus(AudioFlow.Capture);
            if (!s.Ok) return;
            AudioCoreNative.SetCaptureVolume(s.VolumeFloat, !s.Muted);
            var after = AudioCoreNative.GetStatus(AudioFlow.Capture);
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                MicMuted = after.Ok && after.Muted;
                _changed?.Invoke(this);
            }));
        });
    }

    public void Attach(Action<SoundPanelViewModel> changed)
    {
        _changed = changed;
        _changed?.Invoke(this);
    }

    /// <summary>面板隐藏：停 1s 轮询，不再向 worker 投递任务（worker 无任务即在 Monitor.Wait 休眠）（B8）。</summary>
    public void Pause() => _timer.Stop();

    /// <summary>面板重新显示：恢复轮询并立即刷新一帧（B8）。</summary>
    public void Resume()
    {
        if (!_timer.IsEnabled) _timer.Start();
        ReloadAudio();
        _ = RefreshMediaAsync();
    }

    /// <summary>窗口最终关闭：停轮询、通知音频 worker 线程退出、释放刷新闭包（B8）。</summary>
    public void CloseSelf()
    {
        _timer.Stop();
        lock (_audioGate)
        {
            _audioWorkerRunning = false;
            // 唤醒可能正阻塞在 Monitor.Wait 的 worker，使其看到 false 后 return 退出。
            Monitor.PulseAll(_audioGate);
        }
        _changed = null;
    }

    /// <summary>设置主音量（0-100），设置后回读系统真值，避免 UI 与系统不一致。原生调用走后台线程。</summary>
    public void SetVolume(int pct)
    {
        if (!AudioCoreNative.IsAvailable) return;
        var vol = Math.Clamp(pct, 0, 100) / 100f;
        SubmitAudioRefresh(() =>
        {
            AudioCoreNative.SetMasterVolume(vol);
            var s = AudioCoreNative.GetStatus(AudioFlow.Render);
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                VolumePct = s.Ok ? (int)Math.Round(s.VolumeFloat * 100f, MidpointRounding.AwayFromZero) : pct;
                _changed?.Invoke(this);
            }));
        });
    }

    /// <summary>设置某应用会话音量（保持当前静音状态不变）。</summary>
    /// <remarks>不触发 Reload：避免拖拽时的 1s tick 全量重建面板；由 1s 定时器回读校正。</remarks>
    public void SetAppVolume(int pid, float volume, bool muted)
    {
        if (pid <= 0) return;
        var v = Math.Clamp(volume, 0f, 1f);
        SubmitAudioRefresh(() => AudioCoreNative.SetSessionVolume(pid, muted, v));
    }

    /// <summary>记录用户最新设定的应用音量（供拖动期间抵消定时器拉回）。</summary>
    public void MarkAppVolume(int pid, int pct)
    {
        if (pid > 0) _appVolumeOverride[pid] = pct;
    }

    /// <summary>标记某应用音量是否处于拖动中。</summary>
    public void MarkAppDragging(int pid, bool dragging)
    {
        if (pid <= 0) return;
        if (dragging) _appDragging.Add(pid);
        else _appDragging.Remove(pid);
    }

    /// <summary>裁定某会话当前应显示的音量百分比。</summary>
    /// <remarks>拖动中或本地覆盖值与系统原生值尚未一致时，返回本地覆盖值——
    /// 防止定时器用旧的原生快照把滑块拉回；一旦与系统一致（回读校正）即清除覆盖。</remarks>
    public int EffectiveAppVolumePct(AudioSessionNative a)
    {
        var native = (int)Math.Round(Math.Clamp(a.VolumeFloat, 0f, 1f) * 100f);
        if (a.ProcessId > 0 && _appVolumeOverride.TryGetValue(a.ProcessId, out var overridden))
        {
            if (_appDragging.Contains(a.ProcessId) || Math.Abs(overridden - native) > 1)
                return overridden;
            _appVolumeOverride.Remove(a.ProcessId); // 已与系统一致，切换回原生值。
        }
        return native;
    }

    /// <summary>切换某应用会话静音（保持当前音量不变）。</summary>
    public void ToggleAppMute(int pid, bool muted, float volume)
    {
        if (pid <= 0) return;
        var v = Math.Clamp(volume, 0f, 1f);
        SubmitAudioRefresh(() => AudioCoreNative.SetSessionVolume(pid, muted, v));
        ReloadAudio();
    }

    /// <summary>向下发一个媒体控制命令，并在命令后立即刷新媒体状态。Seek 命令需传 position。</summary>
    public async Task SendMediaCommandAsync(MediaPlaybackCommand command, TimeSpan? position = null)
    {
        var media = _media;
        if (media is null || ActiveMedia is null) return;
        await media.SendCommandAsync(command, position);
        await RefreshMediaAsync();
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaRefreshing) return;
        _mediaRefreshing = true;
        try
        {
            var media = _media;
            if (media is null) return;
            MediaSessions = await media.GetSessionsAsync();
            ActiveMedia = await media.GetActiveSessionAsync();
        }
        finally
        {
            _mediaRefreshing = false;
        }
        _changed?.Invoke(this);
    }
}
