// 麦克风面板：输入音量整数滑块 + 输入设备枚举（capture 端）。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Native;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

internal sealed class MicrophonePanelWindow : MenuBarPopupWindow
{
    public MicrophonePanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = NativePanelStyles.DefaultWidth;
        MinWidth = NativePanelStyles.DefaultWidth;
        SizeToContent = SizeToContent.Height;
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var devicesBox = new StackPanel();
        var sliderValue = new TextBlock
        {
            Text = "50",
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 40,
            TextAlignment = TextAlignment.Right
        };
        // 滑杆外观与亮度/音量滑杆统一：两端半圆轨道 + 白色圆球拇指 + 强调色已填充段。
        // 此前这里是原生 Slider 默认样式，与其余面板不是同一套视觉。
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            SmallChange = 1,
            LargeChange = 10,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };

        // 滑杆 + 数值并排（与 BrightnessSliderControl 同一布局：滑杆占满、数值右对齐固定宽）
        var sliderRow = new Grid();
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 0);
        sliderRow.Children.Add(slider);
        Grid.SetColumn(sliderValue, 1);
        sliderRow.Children.Add(sliderValue);

        var root = NativePanelStyles.Root(withColumn: col =>
        {
            col.Children.Add(NativePanelStyles.Title("麦克风"));

            // 音量行：数值 + 滑块
            col.Children.Add(sliderRow);

            col.Children.Add(NativePanelStyles.Separator(top: 8, bottom: 4));

            col.Children.Add(new TextBlock
            {
                Text = "输入",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 4)
            });
            col.Children.Add(devicesBox);

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 4));

            // 声音偏好设置：此前是一个纯 TextBlock（没挂点击事件），点了没反应。
            // 改为共用跳转链接——可点击、强调色、打开系统声音设置页面。
            col.Children.Add(NativePanelStyles.CreateSettingsLink("声音偏好设置…", "ms-settings:sound", fontSize: 11));
        });

        var vm = new MicrophonePanelViewModel();
        vm.Attach(v =>
        {
            sliderValue.Text = v.VolumePct.ToString("0");
            if (Math.Abs(slider.Value - v.VolumePct) > 0.5)
            {
                // 外部（系统音量变化）引起：抑制回写，避免 SetVolume 与 Reload 互相触发形成回环
                _programmatic = true;
                try { slider.Value = v.VolumePct; }
                finally { _programmatic = false; }
            }
            devicesBox.Children.Clear();
            foreach (var d in v.Devices)
            {
                devicesBox.Children.Add(BuildDeviceRow(d));
            }
        });

        // 流畅滑杆：拖动中只更新数值显示，松手才提交到系统 API（与亮度/音量滑杆同一行为）。
        // 原实现在 ValueChanged 里直接调 SetVolume，拖动过程会高频调系统 API 造成卡顿。
        NativePanelStyles.ConfigureSmoothSlider(slider,
            onValueCommitted: v =>
            {
                if (_programmatic) return;
                var newVol = (int)Math.Round(v);
                if (newVol != vm.VolumePct)
                {
                    vm.SetVolume(newVol);
                }
            },
            onValueChanging: v => sliderValue.Text = ((int)Math.Round(v)).ToString("0"));

        return root;
    }

    /// <summary>程序化设置滑杆值（外部同步）时置位，避免触发 SetVolume 回环。</summary>
    private bool _programmatic;

    private static FrameworkElement BuildDeviceRow(AudioDeviceNative d)
    {
        // Grid 本身没有 CornerRadius，外层用 Border 实现圆角背景
        var wrap = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Background = d.IsDefault ? new SolidColorBrush(Color.FromArgb(35, 10, 132, 255)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(8)
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrap.Child = row;

        var icon = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Child = new TextBlock
            {
                Text = "🎙",
                Foreground = d.IsDefault ? MenuBarTheme.Foreground : NativePanelStyles.TextPrimary,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        // 默认设备图标底：强调色；非默认：内容层背景，均走主题令牌
        icon.SetResourceReference(Border.BackgroundProperty, d.IsDefault ? "AccentBrush" : "ThemeContentBackground");
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = d.Name,
            FontSize = 11.5,
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        if (d.IsDefault)
        {
            var mark = new TextBlock
            {
                Text = "✔",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            // 默认标记：强调色走主题令牌
            mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(mark, 2);
            row.Children.Add(mark);
        }
        return wrap;
    }
}

/// <summary>MicrophonePanel 内部 ViewModel。</summary>
internal sealed class MicrophonePanelViewModel
{
    public int VolumePct { get; private set; } = 50;
    public IReadOnlyList<AudioDeviceNative> Devices { get; private set; } = Array.Empty<AudioDeviceNative>();

    private readonly DispatcherTimer _timer;
    private Action<MicrophonePanelViewModel>? _changed;

    public MicrophonePanelViewModel()
    {
        Reload();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Reload();
        _timer.Start();
    }

    public void Attach(Action<MicrophonePanelViewModel> changed)
    {
        _changed = changed;
        _changed?.Invoke(this);
    }

    public void SetVolume(int pct)
    {
        if (!AudioCoreNative.IsAvailable) return;
        var vol = Math.Clamp(pct, 0, 100) / 100f;
        AudioCoreNative.SetCaptureVolume(vol, false);
        VolumePct = pct;
        _changed?.Invoke(this);
    }

    private void Reload()
    {
        if (AudioCoreNative.IsAvailable)
        {
            var s = AudioCoreNative.GetStatus(AudioFlow.Capture);
            if (s.Ok) VolumePct = (int)Math.Round(s.VolumeFloat * 100, MidpointRounding.AwayFromZero);
            Devices = AudioCoreNative.EnumerateDevices(AudioFlow.Capture);
        }
        _changed?.Invoke(this);
    }
}
