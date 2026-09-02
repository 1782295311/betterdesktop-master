using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一次菜单展示的会话（MenuService 持有以支持 Dismiss 幂等关闭）。</summary>
public sealed class MenuHostSession
{
    private readonly TaskCompletionSource<MenuResult> _tcs;

    internal MenuHostSession(ContextMenu menu, TaskCompletionSource<MenuResult> tcs)
    {
        Menu = menu;
        _tcs = tcs;
    }

    public ContextMenu Menu { get; }

    /// <summary>菜单关闭时完成（点击 → CommandExecuted；Esc/失焦 → Cancelled）。</summary>
    public Task<MenuResult> Completion => _tcs.Task;

    /// <summary>关闭菜单（幂等；仅 UI 线程调用）。</summary>
    public void Dismiss() => Menu.IsOpen = false;
}

/// <summary>
/// 菜单弹层宿主（第一版）：把 MenuItemDef 列表渲染为 WPF ContextMenu 并弹出。
/// 保留 WPF 定位/失焦/Esc 语义 + MenuStyling 主题皮肤；独立弹层（Popup/WebView2）形态决策延 M2。
/// </summary>
public static class MenuHost
{
    /// <summary>展示菜单（UI 线程调用；立即打开并返回会话）。</summary>
    public static MenuHostSession Show(FrameworkElement placementTarget, IReadOnlyList<MenuItemDef> items)
    {
        var menu = MenuStyling.CreateMenu();
        string? executedId = null;
        var tcs = new TaskCompletionSource<MenuResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Fill(menu.Items, items, id =>
        {
            executedId = id;
            menu.IsOpen = false;
        });

        menu.Closed += (_, _) =>
            tcs.TrySetResult(executedId is null
                ? new MenuResult(MenuResultKind.Cancelled)
                : new MenuResult(MenuResultKind.CommandExecuted, executedId));

        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
        return new MenuHostSession(menu, tcs);
    }

    private static void Fill(ItemCollection target, IReadOnlyList<MenuItemDef> items, Action<string> execute)
    {
        foreach (var def in items)
        {
            switch (def.Kind)
            {
                case MenuItemKind.Separator:
                    target.Add(new Separator());
                    break;

                case MenuItemKind.Submenu:
                {
                    var sub = new MenuItem
                    {
                        Header = def.Text,
                        IsEnabled = def.IsEnabled,
                        FontWeight = def.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
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
