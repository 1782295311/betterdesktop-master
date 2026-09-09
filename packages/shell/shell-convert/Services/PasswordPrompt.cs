// BetterDesktop.Shell.Convert — 密码输入框（2026-09-07：PDF 加密/解密菜单输入密码用）
// 极简 WPF 模态窗口：标题 + 提示 + 输入框（PasswordBox/TextBox）+ 确定/取消。null = 取消。

using System.Windows;
using System.Windows.Controls;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>模态密码/文本输入框。返回输入内容；用户取消返回 null。</summary>
public static class PasswordPrompt
{
    public static string? Show(string title, string prompt, bool isPassword)
    {
        var input = isPassword ? (Control)new PasswordBox() : new TextBox();
        input.Margin = new Thickness(0, 0, 0, 14);

        var ok = new Button { Content = "确定", Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 380,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = panel,
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = input is PasswordBox pb ? pb.Password : ((TextBox)input).Text;
            window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;

        return window.ShowDialog() == true ? result : null;
    }
}
