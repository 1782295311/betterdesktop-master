// BetterDesktop.Shell.QuickNote — 笔记编辑窗口（ShellWindow 子类）
// 纯代码组装：标题栏 + 可换行 TextBox，文本实时持久化到 extensions.quick-note.text。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.QuickNote;

/// <summary>快速笔记编辑窗口：文本实时存盘，关闭（X）仅隐藏，重新点击启动按钮再次唤出。</summary>
internal sealed class QuickNoteWindow : ShellWindow
{
    private readonly ISettingsService? _settings;
    private readonly TextBox _text;

    public QuickNoteWindow(IAppearanceService? appearance, IVibrancyService? vibrancy, ISettingsService? settings)
        : base(appearance, vibrancy)
    {
        _settings = settings;

        Width = 300;
        Height = 220;
        ResizeMode = ResizeMode.CanResize;
        ShowActivated = true;
        Topmost = true;

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - 16 - Width;
        Top = 22 + 42 + 8;

        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        SetThemeBinding(root, Border.BorderBrushProperty, "CardBorderBrush");
        SetThemeBinding(root, Border.BackgroundProperty, "ThemePanelBackground");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "📝 快速笔记",
            FontSize = 13,
            Margin = new Thickness(10, 8, 10, 6),
        };
        SetThemeBinding(title, TextBlock.ForegroundProperty, "ThemeForeground");
        grid.Children.Add(title);

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(10, 0, 10, 10),
            Text = _settings?.Get("extensions.quick-note.text", "") ?? "",
        };
        SpellCheck.SetIsEnabled(_text, false);
        SetThemeBinding(_text, TextBox.ForegroundProperty, "ThemeForeground");
        SetThemeBinding(_text, TextBox.BackgroundProperty, "ThemeContentBackground");
        SetThemeBinding(_text, TextBox.BorderBrushProperty, "ThemeSeparator");
        Grid.SetRow(_text, 1);
        grid.Children.Add(_text);

        // 实时存盘（Changed 事件按 Key 过滤，不会与 enabled 开关互相触发）。
        _text.TextChanged += (_, _) =>
        {
            if (_settings is not null)
            {
                _settings.Set("extensions.quick-note.text", _text.Text);
            }
        };

        root.Child = grid;
        Content = root;
    }

    protected override void OnLoadedCore()
    {
        ChromeBorder = (Border)Content;
    }

    protected override bool UseSkinBackground => false;
}
