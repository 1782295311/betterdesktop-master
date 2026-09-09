// 声音面板：主功能是“应用”会话音量混音器（按进程图标/名称/音量/静音），
// 由 AudioCore 原生模块提供；主音量滑块与输出设备列表属冗余（Windows 托盘已覆盖），已移除。
// 另含 SMTC 正在播放（MediaPlayerCore）与播放控制。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Native;

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

    public SoundPanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
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
        var micIcon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            // 麦克风圆底：内容层背景走主题令牌（与窗口属性一致）
            Child = new TextBlock
            {
                Text = "\uE720",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = NativePanelStyles.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        micIcon.SetResourceReference(Border.BackgroundProperty, "ThemeContentBackground");
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

        // 正在播放信息（SMTC）：两行——歌名 + “艺术家 · 应用”
        var mediaTitle = new TextBlock
        {
            Text = "—",
            Foreground = NativePanelStyles.TextPrimary,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var mediaSub = new TextBlock
        {
            Text = "",
            Foreground = NativePanelStyles.TextSecondary,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var (prevBtn, playBtn, nextBtn) = MakeControls();

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

            // 正在播放（SMTC 会话） + 播放控制
            col.Children.Add(new TextBlock
            {
                Text = "正在播放",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            });
            col.Children.Add(mediaTitle);
            col.Children.Add(mediaSub);

            var ctrl = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctrl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(prevBtn, 0);
            Grid.SetColumn(playBtn, 1);
            Grid.SetColumn(nextBtn, 2);
            ctrl.Children.Add(prevBtn);
            ctrl.Children.Add(playBtn);
            ctrl.Children.Add(nextBtn);
            col.Children.Add(ctrl);

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

        var vm = _vm = new SoundPanelViewModel();
        vm.Attach(v =>
        {
            volumeValue.Text = v.VolumePct.ToString("0");
            if (Math.Abs(volumeSlider.Value - v.VolumePct) > 0.5) volumeSlider.Value = v.VolumePct;
            sessions.Children.Clear();
            foreach (var a in v.Apps) sessions.Children.Add(BuildAppRow(a, vm));

            mediaTitle.Text = v.NowPlayingTitle;
            mediaSub.Text = v.NowPlayingSub;
            playBtn.Child = GetGlyph(v.Playing);
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

        prevBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaCommand.Previous);
        nextBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaCommand.Next);
        playBtn.MouseLeftButtonUp += async (_, _) => await vm.SendMediaCommandAsync(MediaCommand.Toggle);

        return root;
    }

    private static UIElement GetGlyph(bool playing)
    {
        return new TextBlock
        {
            Text = playing ? "⏸" : "▶",
            Foreground = NativePanelStyles.TextPrimary,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static (Border prev, Border play, Border next) MakeControls()
    {
        Border MakeButton(string glyph, int w = 38)
        {
            var b = new Border
            {
                Width = w,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = Brushes.Transparent,
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock
                {
                    Text = glyph,
                    Foreground = NativePanelStyles.TextPrimary,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            return b;
        }
        var prev = MakeButton("⏮");
        var play = MakeButton("▶", w: 46);
        var next = MakeButton("⏭");
        return (prev, play, next);
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
                // 应用图标占位：白色半透明（原深色半透明在毛玻璃面板上几乎不可见）
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
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
            Background = muted ? new SolidColorBrush(Color.FromRgb(255, 59, 48))
                               : new SolidColorBrush(Color.FromArgb(140, 230, 230, 230)),
            Child = new TextBlock
            {
                Text = muted ? "⍻" : "♩",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 静音红底用白字；未静音浅灰底用深字
                Foreground = muted ? Brushes.White : new SolidColorBrush(Color.FromRgb(60, 60, 60))
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

    public IReadOnlyList<MediaSessionSnapshot> MediaSessions { get; private set; } = Array.Empty<MediaSessionSnapshot>();
    public MediaSessionSnapshot? ActiveMedia { get; private set; }
    public string NowPlayingTitle => ActiveMedia?.Title ?? "—";
    public string NowPlayingSub => ActiveMedia is null ? "" : $"{ActiveMedia.Artist} · {ActiveMedia.AppName}".TrimStart(' ', '·');
    public bool Playing => ActiveMedia?.State == MediaPlaybackState.Playing;

    public SoundPanelViewModel()
    {
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
    private void ReloadAudio()
    {
        SubmitAudioRefresh(() =>
        {
            var s = AudioCoreNative.GetStatus(AudioFlow.Render);
            var apps = AudioCoreNative.EnumerateSessions();
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                if (s.Ok) VolumePct = (int)Math.Round(s.VolumeFloat * 100f, MidpointRounding.AwayFromZero);
                Apps = apps;
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

    /// <summary>向下发一个媒体控制命令，并在命令后立即刷新媒体状态。</summary>
    public async Task SendMediaCommandAsync(MediaCommand command)
    {
        if (ActiveMedia is null) return;
        await MediaPlayerCore.SendCommandAsync(ActiveMedia, command);
        await RefreshMediaAsync();
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaRefreshing) return;
        _mediaRefreshing = true;
        try
        {
            if (!await MediaPlayerCore.EnsureInitializedAsync()) return;
            var snap = MediaSessions = await MediaPlayerCore.GetSnapshotAsync();
            ActiveMedia = PickActiveMedia(snap);
        }
        finally
        {
            _mediaRefreshing = false;
        }
        _changed?.Invoke(this);
    }

    // 优先正在播放的会话，其次任意非关闭会话；都没有则返回 null。
    private static MediaSessionSnapshot? PickActiveMedia(IReadOnlyList<MediaSessionSnapshot> snap)
    {
        MediaSessionSnapshot? fallback = null;
        foreach (var item in snap)
        {
            if (item.State == MediaPlaybackState.Playing) return item;
            fallback ??= item;
        }
        return fallback;
    }
}
