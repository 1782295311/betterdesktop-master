// BetterDesktop.Shell.MenuBar — 自绘 ToggleSwitch（可跨窗口共享）。
// net8-windows 不直接提供 ToggleSwitch；用自绘 36×20 的开关更稳，样式与截图一致。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 自绘开关控件（用于设置项的 On/Off Toggle）。
/// 可跨窗口复用；尺寸 36×20，符合 macOS/控制中心风格。
/// </summary>
internal sealed class ToggleSwitch : ContentControl
{
    private Border? _track;
    private Border? _thumb;

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(false, OnIsOnChanged));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public event EventHandler<object>? Toggled;

    public ToggleSwitch()
    {
        Width = 36; Height = 20;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var grid = new Grid { ClipToBounds = true };
        _track = new Border
        {
            Height = 20,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255))
        };
        _thumb = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2, 2, 0, 0),
            Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.5 }
        };
        grid.Children.Add(_track);
        grid.Children.Add(_thumb);
        Content = grid;
        UpdateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        IsOn = !IsOn;
        Toggled?.Invoke(this, IsOn);
        e.Handled = true;
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleSwitch sw) sw.UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (_track is null || _thumb is null) return;
        // 开态：强调色走主题令牌；关态：明显中性灰（确保开/关对比强烈，不再用可能透明的内容背景）
        if (IsOn)
        {
            _track.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        }
        else
        {
            _track.Background = new SolidColorBrush(Color.FromRgb(130, 130, 130));
        }
        _thumb.HorizontalAlignment = IsOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _thumb.Margin = IsOn ? new Thickness(0, 2, 2, 2) : new Thickness(2, 2, 0, 2);
    }
}
