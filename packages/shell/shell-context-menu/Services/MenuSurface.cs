// BetterDesktop.Shell.ContextMenus — 统一右键接入点（MenuSurface）
// 参考 ContextMenuManager 的"来源分层"思想落到进程内：
//   ContextMenuManager 管"Windows 原生菜单内容"（注册表层），本类型管"自研 shell 的
//   右键入口"——项目内任何表面（dock/开始菜单/程序树/磁贴/搜索结果）都经它接到
//   统一弹层，禁止再 new WPF ContextMenu()（系统原生样式、非 ShellWindow、嵌入
//   child window 中不可靠——三宗罪，2026-09-02 审查定论）。

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>统一右键接入点：任意元素右键/按钮点击 → 统一菜单弹层（ShellWindow）。</summary>
public static class MenuSurface
{
    /// <summary>
    /// 把任意 FrameworkElement 的右键接到统一菜单弹层（替代 <c>el.ContextMenu = …</c> 附加属性）。
    /// build 在每次右键时调用（菜单内容可即时反映最新状态）；返回 null/空集则不弹。
    /// menus 为 null 时静默不弹（装配顺序兜底：context-menu 插件先于表面插件加载）。
    /// </summary>
    public static IDisposable Attach(FrameworkElement host, Func<IReadOnlyList<MenuItemDef>?> build, IMenuService? menus)
    {
        if (menus is null)
        {
            DiagnosticLog.Trace("context-menu", $"{host.GetType().Name} 右键接入降级：IMenuService 缺失");
            return new Disposable(() => { });
        }

        MouseButtonEventHandler onUp = (_, e) =>
        {
            e.Handled = true; // 抑制 WPF 默认 ContextMenu 附加属性/系统样式菜单
            try
            {
                var items = build();
                if (items is { Count: > 0 })
                {
                    _ = menus.ShowAsync(items, GetScreenPos(host, e.GetPosition(host)));
                }
            }
            catch (Exception ex)
            {
                // 构建失败不拖垮宿主交互（M10）
                DiagnosticLog.Trace("context-menu", $"右键弹层构建失败: {ex.Message}");
            }
        };
        host.MouseRightButtonUp += onUp;
        return new Disposable(() => host.MouseRightButtonUp -= onUp);
    }

    /// <summary>
    /// 把源元素内的相对坐标（DIP）换算为屏幕 DIP 坐标。
    /// 生死线（与桌面路径同口径）：PointToScreen 返回物理像素，必须 ÷ 源元素 PixelsPerDip。
    /// </summary>
    public static Point GetScreenPos(Visual source, Point relative)
    {
        var physical = source.PointToScreen(relative);
        var dpi = VisualTreeHelper.GetDpi(source).PixelsPerDip;
        return new Point(physical.X / dpi, physical.Y / dpi);
    }

    /// <summary>元素底边中点下方 4px 的屏幕坐标（按钮点击弹菜单的定位惯例，如电源菜单）。</summary>
    public static Point BelowOf(FrameworkElement el)
        => GetScreenPos(el, new Point(el.ActualWidth / 2, el.ActualHeight + 4));

    /// <summary>当前光标相对 source 的屏幕 DIP 坐标（替代 ContextMenu 的"默认弹在鼠标处"）。</summary>
    public static Point AtCursor(FrameworkElement source) => GetScreenPos(source, Mouse.GetPosition(source));

    private sealed class Disposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
