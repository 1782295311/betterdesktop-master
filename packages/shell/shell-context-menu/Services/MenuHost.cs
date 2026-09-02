using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

// 命名空间避让：根命名空间旧名以 ContextMenu 结尾时会遮蔽 WPF 类型（教训见 72 域 README）
using ContextMenu = System.Windows.Controls.ContextMenu;

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
/// 菜单弹层宿主：**独立无边框窗口**承载菜单（cordis MenuBarPopupWindow 同款范式）。
/// 为什么不用 WPF ContextMenu（Popup）：自绘桌面窗口被 SetParent 为 explorer 桌面的
/// WS_CHILD——Popup/ContextMenu 在嵌入 child window 中不可靠（可能不显示/秒关）；
/// 独立 Topmost Window 在桌面层之上稳定显示，且天然支持失焦关闭与键盘导航。
/// </summary>
public static class MenuHost
{
    /// <summary>展示菜单（UI 线程调用；立即打开并返回会话）。screenPos 为屏幕 DIP 坐标。</summary>
    public static MenuHostSession Show(IReadOnlyList<MenuItemDef> items, Point screenPos)
    {
        string? executedId = null;
        var tcs = new TaskCompletionSource<MenuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window window = null!;

        window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,           // 接收键盘导航（Esc/方向键）；失焦即关
            Topmost = true,                 // 盖过 explorer 桌面层与其它窗口（菜单短生命周期）
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = BuildPanel(items, id =>
            {
                executedId = id;
                window.Close();
            }),
        };

        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        window.Deactivated += (_, _) => window.Close(); // 点外部/切窗 → 关闭
        window.Closed += (_, _) =>
            tcs.TrySetResult(executedId is null
                ? new MenuResult(MenuResultKind.Cancelled)
                : new MenuResult(MenuResultKind.CommandExecuted, executedId));

        // 定位：SizeToContent 需布局完成后才知道尺寸 → Loaded 后贴鼠标点 + 工作区边缘钳制
        window.Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            var left = Math.Min(screenPos.X, work.Right - window.ActualWidth - 2);
            var top = Math.Min(screenPos.Y, work.Bottom - window.ActualHeight - 2);
            window.Left = Math.Max(work.Left + 2, left);
            window.Top = Math.Max(work.Top + 2, top);
        };

        window.Show();
        return new MenuHostSession(window, tcs);
    }

    /// <summary>构建菜单面板（主题令牌 Border + MenuItem 树；MenuItem 在普通视觉树中子菜单照常弹出）。</summary>
    private static UIElement BuildPanel(IReadOnlyList<MenuItemDef> items, Action<string> execute)
    {
        var stack = new StackPanel();
        Fill(stack.Children, items, execute);
        return new Border
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
                    };
                    ApplyItemStyle(sub);
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
                    };
                    ApplyItemStyle(item);
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

    /// <summary>应用主题项样式（MenuStyling 同源；令牌缺失时 ThemeAdapter 回退由 XAML DynamicResource 处理）。</summary>
    private static void ApplyItemStyle(FrameworkElement item) => item.Style = MenuStyling.CreateItemStyle();
}
