// BetterDesktop.Shell.QuickNote — 浮launch按钮（ShellWindow 子类）
// 常驻屏幕右上角（菜单栏下方），左键切换笔记窗口，右键关闭扩展（置 extensions.quick-note.enabled=false）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.QuickNote;

/// <summary>快速笔记常驻启动按钮：点击切换笔记，右键关闭扩展。</summary>
internal sealed class QuickNoteLauncher : ShellWindow
{
    public event Action? NoteToggle;
    public event Action? RequestDisable;

    public QuickNoteLauncher(IAppearanceService? appearance, IVibrancyService? vibrancy)
        : base(appearance, vibrancy)
    {
        Width = 42;
        Height = 42;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Topmost = true;

        // 定位：右上角、菜单栏（高度 16）下方留白。
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - 16 - Width;
        Top = 22;

        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "📝",
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
        SetThemeBinding(root, Border.BorderBrushProperty, "CardBorderBrush");
        SetThemeBinding(root, Border.BackgroundProperty, "ThemePanelBackground");
        if (root.Child is FrameworkElement glyph)
        {
            SetThemeBinding(glyph, TextElement.ForegroundProperty, "ThemeForeground");
        }
        Content = root;

        root.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                NoteToggle?.Invoke();
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                RequestDisable?.Invoke();
            }
            e.Handled = true;
        };
    }

    protected override void OnLoadedCore()
    {
        // 必须赋值 ChromeBorder，否则基类 DEBUG 断言失败且外观失效。
        ChromeBorder = (Border)Content;
    }

    /// <summary>启动按钮用主题面板背景，不套皮肤大图。</summary>
    protected override bool UseSkinBackground => false;
}
