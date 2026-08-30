// BetterDesktop.Shell.MenuBar — 亮度滑块共用控件
// 控制中心 / 主题等所有展示亮度的地方都绑定同一个 IBrightnessMonitor 实例，
// 从而：① 读写逻辑只有一份（不各自调 DisplayCoreNative）；② 任一面板调整后，
// 通过 monitor.Changed 同步其它已打开面板的滑块与百分比（_programmatic 防止写回回环）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>可拖动亮度滑块（共用控件）：标题 + 滑块 + 百分比 + 可选"显示设置"跳转。</summary>
internal sealed class BrightnessSliderControl : IDisposable
{
    private readonly IBrightnessMonitor _brightness;
    private readonly Slider _slider = null!;
    private readonly TextBlock _label = null!;
    private bool _programmatic;

    /// <summary>整体 UI（可直接加入面板竖向列）。</summary>
    public FrameworkElement Root { get; }

    /// <param name="brightness">亮度单一数据源（不可为 null，已由调用方降级）。</param>
    /// <param name="title">区域标题，传 null/空则不显示。</param>
    /// <param name="showSettingsLink">是否在底部追加"显示设置"跳转。</param>
    public BrightnessSliderControl(IBrightnessMonitor brightness, string? title, bool showSettingsLink)
    {
        _brightness = brightness;

        var column = new StackPanel { Orientation = Orientation.Vertical };

        if (!string.IsNullOrEmpty(title))
        {
            // 标题继承主题前景色（面板基类自动绑 ThemeForeground）
            column.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Opacity = 0.9,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        if (!brightness.TryGetRange(out var min, out var cur, out var max))
        {
            // 环境不支持亮度调节：明确降级提示，不写假数字。
            var unavailable = new TextBlock
            {
                Text = "亮度调节不可用",
                FontSize = 11,
                Margin = new Thickness(2, 0, 0, 0)
            };
            // 次要提示：次要前景走主题令牌
            unavailable.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMutedForeground");
            column.Children.Add(unavailable);
            if (showSettingsLink)
            {
                column.Children.Add(CreateSettingsLink());
            }
            Root = column;
            return;
        }

        _label = new TextBlock
        {
            Text = FormatLabel(cur, min, max),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 40,
            TextAlignment = TextAlignment.Right
        };

        _slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(cur, min, max),
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            // 与控制中心音量滑杆统一：两端半圆轨道 + 白色圆球拇指 + 强调色已填充段
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        // 流畅滑杆：拖动过程中只更新百分比显示，拖动结束/点击跳转时才设置亮度（避免频繁调系统 API 卡顿）
        NativePanelStyles.ConfigureSmoothSlider(_slider,
            onValueCommitted: v =>
            {
                if (_programmatic) return; // 外部（其它面板）设置亮度时不回写
                int val = (int)Math.Round(v);
                _label.Text = FormatLabel(val, min, max);
                _brightness.SetValue(val);
            },
            onValueChanging: v =>
            {
                int val = (int)Math.Round(v);
                _label.Text = FormatLabel(val, min, max);
            });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_slider, 0);
        row.Children.Add(_slider);
        Grid.SetColumn(_label, 1);
        row.Children.Add(_label);
        column.Children.Add(row);

        if (showSettingsLink)
        {
            column.Children.Add(CreateSettingsLink());
        }

        // 任一面板调整亮度后，同步本控件的滑块与百分比（跨面板）。
        _brightness.Changed += OnBrightnessChanged;

        Root = column;
    }

    private void OnBrightnessChanged(object? sender, StatusSnapshot snapshot)
    {
        if (snapshot.Progress < 0 || _slider is null || _label is null)
        {
            return;
        }
        // 由外部（其它面板）调整引起：把 0-100 百分比映射回本控件原始范围，更新滑块与标签。
        double mn = _slider.Minimum, mx = _slider.Maximum;
        int value = (int)Math.Round(mn + snapshot.Progress / 100.0 * (mx - mn));
        _programmatic = true;
        try
        {
            _slider.Value = Math.Clamp(value, mn, mx);
            _label.Text = $"{Math.Clamp((int)snapshot.Progress, 0, 100)}%";
        }
        finally
        {
            _programmatic = false;
        }
    }

    private static string FormatLabel(double value, double min, double max)
    {
        int span = (int)Math.Max(1, max - min);
        int pct = (int)Math.Round((value - min) * 100.0 / span);
        return $"{Math.Clamp(pct, 0, 100)}%";
    }

    private static FrameworkElement CreateSettingsLink()
    {
        var text = new TextBlock
        {
            Text = "显示设置",
            FontSize = 12,
            Margin = new Thickness(4, 4, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 操作/链接入口：强调色走主题令牌
        text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        text.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };
        return text;
    }

    public void Dispose() => _brightness.Changed -= OnBrightnessChanged;
}
