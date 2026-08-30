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
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            SmallChange = 1,
            LargeChange = 10,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };

        var root = NativePanelStyles.Root(withColumn: col =>
        {
            col.Children.Add(NativePanelStyles.Title("麦克风"));

            // 音量行：数值 + 滑块
            col.Children.Add(sliderValue);
            col.Children.Add(slider);

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

            col.Children.Add(new TextBlock
            {
                Text = "声音偏好设置…",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 0, 0),
                Opacity = 0.9
            });
        });

        var vm = new MicrophonePanelViewModel();
        vm.Attach(v =>
        {
            sliderValue.Text = v.VolumePct.ToString("0");
            if (Math.Abs(slider.Value - v.VolumePct) > 0.5)
            {
                slider.Value = v.VolumePct;
            }
            devicesBox.Children.Clear();
            foreach (var d in v.Devices)
            {
                devicesBox.Children.Add(BuildDeviceRow(d));
            }
        });
        slider.ValueChanged += (_, e) =>
        {
            var newVol = (int)Math.Round(e.NewValue);
            if (newVol != vm.VolumePct)
            {
                vm.SetVolume(newVol);
            }
        };

        return root;
    }

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
                Foreground = d.IsDefault ? Brushes.White : NativePanelStyles.TextPrimary,
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
