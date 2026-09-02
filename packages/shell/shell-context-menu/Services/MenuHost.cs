using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Windows;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一次菜单展示的会话（MenuService 持有以支持 Dismiss 幂等关闭）。</summary>
public sealed class MenuHostSession
{
    private readonly TaskCompletionSource<MenuResult> _tcs;

    internal MenuHostSession(Window window, TaskCompletionSource<MenuResult> tcs)
    {
        Window = window;
        _tcs = tcs;
    }

    public Window Window { get; }

    /// <summary>菜单关闭时完成（点击 → CommandExecuted；Esc/失焦 → Cancelled）。</summary>
    public Task<MenuResult> Completion => _tcs.Task;

    /// <summary>关闭菜单（幂等；UI 线程调用）。</summary>
    public void Dismiss() => Window.Close();
}

/// <summary>
/// 菜单弹层宿主：**独立弹层窗口（ContextMenuPopupWindow，继承统一窗口基类 ShellWindow）**承载菜单。
/// 为什么不用 WPF ContextMenu（Popup）：自绘桌面窗口被 SetParent 为 explorer 桌面的
/// WS_CHILD——Popup/ContextMenu 在嵌入 child window 中不可靠（可能不显示/秒关）；
/// 独立 Topmost 窗口在桌面层之上稳定显示，且天然支持失焦关闭与键盘导航。
/// 窗口属性对齐 shell-menu-bar 的 MenuBarPopupWindow 范式（ShellWindow 统一基类驱动）。
/// </summary>
public static class MenuHost
{
    /// <summary>展示菜单（UI 线程调用；立即打开并返回会话）。screenPos 为屏幕 DIP 坐标。</summary>
    public static MenuHostSession Show(IReadOnlyList<MenuItemDef> items, Point screenPos,
        IAppearanceService? appearance, IVibrancyService? vibrancy)
    {
        string? executedId = null;
        var tcs = new TaskCompletionSource<MenuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window window = null!;

        window = new ContextMenuPopupWindow(
            BuildPanel(items, id =>
            {
                executedId = id;
                window.Close();
            }),
            screenPos, appearance, vibrancy);

        window.Closed += (_, _) =>
            tcs.TrySetResult(executedId is null
                ? new MenuResult(MenuResultKind.Cancelled)
                : new MenuResult(MenuResultKind.CommandExecuted, executedId));

        window.Show();
        return new MenuHostSession(window, tcs);
    }

    /// <summary>构建菜单面板（主题令牌 Border + MenuItem 树；MenuItem 在普通视觉树中子菜单照常弹出）。</summary>
    private static Border BuildPanel(IReadOnlyList<MenuItemDef> items, Action<string> execute)
    {
        var stack = new StackPanel();
        Fill(stack.Children, items, execute);
        var border = new Border
        {
            Background = FindToken("PopupBackground", System.Windows.Media.Brushes.White),
            BorderBrush = FindToken("PopupBorder", System.Windows.Media.Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = stack,
        };

        // ★ 前景传导生死线：弹层窗口不经过 ShellWindow.ApplyFontScale 的前景传导路径
        //   （AppearanceService 可能为 null），必须在面板根上直接挂 ThemeForeground——
        //   否则 MenuItem 用默认黑字渲染在深色 PopupBackground 上 = 整个菜单看起来"纯黑"。
        //   用 DynamicResource 绑定：主题亮/暗切换即时跟随。
        border.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ThemeForeground");
        return border;
    }

    private static System.Windows.Media.Brush FindToken(string key, System.Windows.Media.Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.Brush brush ? brush : fallback;

    private static void Fill(System.Collections.IList target, IReadOnlyList<MenuItemDef> items, Action<string> execute)
    {
        foreach (var def in items)
        {
            switch (def.Kind)
            {
                case MenuItemKind.Separator:
                    target.Add(new Separator
                    {
                        Style = MenuStyling.CreateSeparatorStyle(),
                    });
                    break;

                case MenuItemKind.Submenu:
                {
                    var sub = new MenuItem
                    {
                        Header = def.Text,
                        IsEnabled = def.IsEnabled,
                        FontWeight = def.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                        Style = MenuStyling.CreateItemStyle(),
                    };
                    if (def.Children is { Count: > 0 })
                        Fill(sub.Items, def.Children, execute);
                    else
                        sub.IsEnabled = false; // 空子菜单禁用（构建期动态项未就绪时降级）
                    target.Add(sub);
                    break;
                }

                default: // Command / Toggle / Radio
                {
                    var item = new MenuItem
                    {
                        Header = def.Text,
                        IsEnabled = def.IsEnabled,
                        IsChecked = def.IsChecked,
                        FontWeight = def.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                        Style = MenuStyling.CreateItemStyle(),
                    };
                    item.Click += (_, _) =>
                    {
                        try
                        {
                            def.Command?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            // 单项命令失败仅记录，不影响其余项（README §11）
                            DiagnosticLog.Trace("context-menu", $"[{def.Id}] 执行失败: {ex.Message}");
                        }
                        execute(def.Id);
                    };
                    target.Add(item);
                    break;
                }
            }
        }
    }
}
