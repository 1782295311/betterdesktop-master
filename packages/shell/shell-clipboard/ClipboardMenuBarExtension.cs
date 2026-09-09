// BetterDesktop.Shell.Clipboard — 菜单栏按钮扩展（Phase B 6.6 I6）
// 右区按钮「📋」：点击直接打开历史面板（Manager 单实例懒创建，不重复实现面板）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Core.Contracts;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>菜单栏剪贴板历史按钮（IMenuBarExtension 右区按钮范式）。</summary>
internal sealed class ClipboardMenuBarExtension : IMenuBarExtension
{
    private readonly IClipboardService _service;

    public ClipboardMenuBarExtension(IClipboardService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public string Id => "clipboard-history";

    public FrameworkElement? GetVisual()
    {
        var button = new Button
        {
            Content = "📋",
            FontSize = 12,
            ToolTip = "剪贴板历史（Ctrl+Shift+V）",
            Cursor = Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 26,
            Height = 24,
        };
        button.Click += (_, _) => _service.OpenHistoryWindow();
        return button;
    }

    public void OpenPopup(Point anchorScreenTopLeft) => _service.OpenHistoryWindow();

    public void ClosePopup()
    {
        // 面板生命周期由 Manager 统一管理（失焦自隐/关闭置空），此处无需动作。
    }
}
