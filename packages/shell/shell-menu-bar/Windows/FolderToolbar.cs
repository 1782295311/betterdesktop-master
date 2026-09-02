// BetterDesktop.Shell.MenuBar — 左区文件夹工具条（可折叠收纳）
// 位置：文档按钮右侧。展开 = 路径显示 + 导航行（← → ↑ ⟳）+ 操作行（剪/复/贴/改/删）；
// 收起 = 一个折叠按钮（不占空间）。操作对象 = 自绘桌面浏览器（IDesktopBrowser）选中项。
// 浏览器缺失（自绘桌面未加载）时整条不呈现（M10）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>文件夹工具条（可折叠）：导航 + 文件操作 + 路径显示。</summary>
internal sealed class FolderToolbar : StackPanel, IDisposable
{
    private const double ButtonSize = 18.0;

    private readonly IDesktopBrowser _browser;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly TextBlock _toggleGlyph;

    private RenameDialog? _renameDialog;
    private bool _collapsed = true;
    private bool _disposed;

    public FolderToolbar(IDesktopBrowser browser, IVibrancyService vibrancy, IAppearanceService? appearance)
    {
        _browser = browser;
        _vibrancy = vibrancy;
        _appearance = appearance;

        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(6, 0, 0, 0);

        // 折叠按钮（常驻）
        _toggleGlyph = new TextBlock
        {
            Text = "▸",
            Foreground = MenuBarTheme.Foreground,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Children.Add(WrapButton(_toggleGlyph, Toggle, "文件夹工具"));

        // 展开区（独立容器便于整体显隐）
        var expanded = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };

        // 路径显示：实时反映桌面/浏览器当前浏览路径（Location），非固定入口路径
        var pathText = new TextBlock
        {
            Foreground = MenuBarTheme.Foreground,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
            Text = _browser.Location,
            ToolTip = _browser.Location
        };
        expanded.Children.Add(pathText);
        _browser.LocationChanged += (_, path) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                pathText.Text = path;
                pathText.ToolTip = path;
            }));
        };

        // 导航行：← → ↑ ⟳
        expanded.Children.Add(WrapNavButton("←", () => _browser.Back(), "后退"));
        expanded.Children.Add(WrapNavButton("→", () => _browser.Forward(), "前进"));
        expanded.Children.Add(WrapNavButton("↑", () => _browser.Up(), "向上"));
        expanded.Children.Add(WrapNavButton("⟳", () => _browser.Refresh(), "刷新"));

        // 操作行：功能全名按钮（剪切/复制/粘贴/重命名/删除），对选中项操作
        expanded.Children.Add(WrapNavButton("剪切", () => _browser.Cut(), "剪切选中项"));
        expanded.Children.Add(WrapNavButton("复制", () => _browser.Copy(), "复制选中项"));
        expanded.Children.Add(WrapNavButton("粘贴", () => _browser.Paste(), "粘贴到当前位置"));
        expanded.Children.Add(WrapNavButton("重命名", ShowRename, "重命名唯一选中项"));
        expanded.Children.Add(WrapNavButton("删除", () => _browser.Delete(), "删除选中项（回收站）"));

        Children.Add(expanded);
        _expanded = expanded;
    }

    private StackPanel? _expanded;

    private void Toggle()
    {
        _collapsed = !_collapsed;
        _toggleGlyph.Text = _collapsed ? "▸" : "▾";
        if (_expanded is not null)
        {
            _expanded.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>导航/操作按钮：宽度随文字自适应（导航符号单字符紧凑、操作词全名显示），统一悬停反馈。</summary>
    private FrameworkElement WrapNavButton(string label, Action onClick, string tooltip)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = MenuBarTheme.Foreground,
            FontSize = label.Length == 1 && char.IsAscii(label[0]) ? 13 : 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return WrapButton(text, onClick, tooltip, autoWidth: true);
    }

    private FrameworkElement WrapButton(UIElement content, Action onClick, string? tooltip = null, bool autoWidth = false)
    {
        var btn = new Border
        {
            Width = autoWidth ? double.NaN : ButtonSize, // 自适应宽度（工具条操作词全名）；固定宽（Logo 折叠钮）
            Height = ButtonSize,
            Padding = autoWidth ? new Thickness(5, 0, 5, 0) : new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 1, 0),
            Child = content,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        if (autoWidth && content is TextBlock tb)
        {
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
        }
        MenuBarTheme.AttachHoverFeedback(btn);
        btn.MouseLeftButtonUp += (_, _) =>
        {
            btn.Background = MenuBarTheme.Hover;
            try { onClick(); }
            catch { /* 操作失败静默（M10） */ }
        };
        return btn;
    }

    /// <summary>重命名：对唯一选中项弹小输入面板。</summary>
    private void ShowRename()
    {
        var selected = _browser.SelectedPaths;
        if (selected.Count != 1) return;
        var current = System.IO.Path.GetFileName(selected[0]);

        _renameDialog?.Close();
        _renameDialog = new RenameDialog(current, name => _browser.Rename(name), _vibrancy, _appearance);
        var pos = PopupAnchor.Compute(this, ButtonSize, new Size(260, 120), MenuBarMetrics.MenuBarHeight);
        _renameDialog.ShowAt(pos);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renameDialog?.Close();
        _renameDialog = null;
    }
}
