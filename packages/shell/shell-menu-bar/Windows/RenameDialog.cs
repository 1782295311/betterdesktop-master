// BetterDesktop.Shell.MenuBar — 重命名对话框（菜单栏风格小面板）
// 输入新名称 → 确定回调。走 MenuBarPopupWindow 统一基类（毛玻璃/失焦收起/主题令牌）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>重命名小面板：输入新名称，确定回调（Enter 确定 / Esc 取消）。</summary>
internal sealed class RenameDialog : MenuBarPopupWindow
{
    // 含名称输入框：禁用 WS_EX_NOACTIVATE，否则点击后窗口不获焦点、键盘输入落不进 TextBox。
    protected override bool UseNoActivateWindowStyle => false;

    private const double DefaultWidth = 260;

    private readonly string _currentName;
    private readonly Action<string> _onConfirm;
    private TextBox? _box;

    public RenameDialog(string currentName, Action<string> onConfirm, IVibrancyService vibrancy, IAppearanceService? appearance)
        : base(vibrancy, appearance)
    {
        _currentName = currentName;
        _onConfirm = onConfirm;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
    }

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        var column = new StackPanel { Orientation = Orientation.Vertical };

        var label = new TextBlock { Text = "重命名为：", FontSize = 12, Margin = new Thickness(2, 0, 0, 4) };
        column.Children.Add(label);

        _box = new TextBox
        {
            Text = _currentName,
            FontSize = 13,
            Height = 28,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        _box.SelectAll();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Brushes.White,
            Child = _box
        };
        column.Children.Add(inputBorder);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        buttons.Children.Add(MakeButton("确定", Confirm));
        buttons.Children.Add(MakeButton("取消", Hide));
        column.Children.Add(buttons);

        root.Child = column;
        return root;
    }

    private FrameworkElement MakeButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            Width = 64,
            Height = 26,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void Confirm()
    {
        var name = _box?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        Hide();
        _onConfirm(name);
    }
}
