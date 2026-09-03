using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// <summary>展示菜单（UI 线程调用；立即打开并返回会话）。screenPos 为屏幕 DIP 坐标；panelOpacity 为面板底色不透明度（用户设置）。</summary>
    public static MenuHostSession Show(IReadOnlyList<MenuItemDef> items, Point screenPos,
        IAppearanceService? appearance, IVibrancyService? vibrancy, double panelOpacity = 0.82)
    {
        string? executedId = null;
        var tcs = new TaskCompletionSource<MenuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window window = null!;

        window = new ContextMenuPopupWindow(
            BuildPanel(items, id =>
            {
                executedId = id;
                window.Close();
            }, panelOpacity),
            screenPos, appearance, vibrancy);

        window.Closed += (_, _) =>
            tcs.TrySetResult(executedId is null
                ? new MenuResult(MenuResultKind.Cancelled)
                : new MenuResult(MenuResultKind.CommandExecuted, executedId));

        window.Show();
        return new MenuHostSession(window, tcs);
    }

    /// <summary>构建菜单面板（主题令牌 Border + MenuItem 树；MenuItem 在普通视觉树中子菜单照常弹出）。</summary>
    private static Border BuildPanel(IReadOnlyList<MenuItemDef> items, Action<string> execute, double panelOpacity)
    {
        var stack = new StackPanel();
        Fill(stack.Children, items, execute);

        // 面板底色半透明：透出窗口 vibrancy 毛玻璃（对齐 dock/设置窗口观感——"统一窗口基类该有的样子"）。
        // 不透明度由用户在设置页调节（context-menu.opacity）。
        // 必须克隆后再改 Opacity：FindToken 返回的是 App 级共享画刷，直接改会污染全局令牌。
        var bg = FindToken("PopupBackground", System.Windows.Media.Brushes.White).Clone();
        bg.Opacity = Math.Clamp(panelOpacity, 0.3, 1.0);

        var border = new Border
        {
            Background = bg,
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

    /// <summary>
    /// 挂接菜单项图标（三协议）：
    ///   sys:&lt;hex&gt;  → Segoe 字形矢量图标（零 IO；码位不在白名单则留空）
    ///   file:&lt;path&gt; → 文件关联图标（MenuIconCache / SHGetFileInfo）
    ///   tool:&lt;exe&gt;  → 程序图标（同上）
    /// 【防卡顿红线】未命中缓存时**后台提取、菜单先出后补**，绝不在 UI 线程做磁盘 IO
    /// （Win11 式"右键后转圈"的根因就是同步图标提取）。
    /// </summary>
    private static void AttachIcon(MenuItem item, MenuItemDef def)
    {
        if (string.IsNullOrEmpty(def.IconKey))
        {
            return;
        }

        var key = def.IconKey!;
        try
        {
            if (key.StartsWith("sys:", StringComparison.Ordinal))
            {
                var glyph = SystemGlyphs.Create(key["sys:".Length..]);
                if (glyph is not null)
                {
                    item.Icon = glyph;
                }
                return;
            }

            var colon = key.IndexOf(':');
            if (colon <= 0)
            {
                return;
            }

            var prefix = key[..colon];
            if (prefix is not ("file" or "tool"))
            {
                return;
            }

            var path = key[(colon + 1)..];
            if (MenuIconCache.TryGetCached(path, out var cached))
            {
                if (cached is not null)
                {
                    item.Icon = MakeIconImage(cached);
                }
                return; // 缓存命中（含"提取失败"的负缓存）→ 不再排队
            }

            // 未命中：后台提取后回填（菜单此刻已可交互）
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                var source = MenuIconCache.Get(path);
                if (source is null)
                {
                    return;
                }

                _ = item.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        if (item.IsLoaded)
                        {
                            item.Icon = MakeIconImage(source);
                        }
                    });
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("context-menu", $"图标处理失败 {def.IconKey}: {ex.Message}");
        }
    }

    private static System.Windows.Controls.Image MakeIconImage(System.Windows.Media.ImageSource source)
        => new()
        {
            Source = source,
            Width = 16,
            Height = 16,
            SnapsToDevicePixels = true,
        };

    /// <summary>
    /// 子菜单悬停交互（explorer 同款，用户 2026-09-02 定版）：
    /// 悬停宿主项 → 自动展开（并收回同级其它子菜单）；移出宿主与弹层 → 延迟 350ms 收回
    /// （给鼠标从宿主移入弹层留缓冲）。不依赖 MenuItem 角色内建逻辑——我们的项挂在
    /// StackPanel 而非 Menu/ContextMenu 下，角色内建的悬停展开不可靠（实测点不开）。
    /// </summary>
    private static void WireSubmenuHover(MenuItem sub)
    {
        // 立即套用模板以取得 PART_Popup（Style 在构造期已赋值，ApplyTemplate 可同步构建）
        sub.ApplyTemplate();
        var popup = sub.Template?.FindName("PART_Popup", sub) as Popup;

        sub.MouseEnter += (_, _) =>
        {
            // 同级互斥：收回兄弟子菜单（兄弟 = 同一宿主面板里的其它 MenuItem）
            if (sub.Parent is System.Windows.Controls.Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is MenuItem { IsSubmenuOpen: true } other && !ReferenceEquals(other, sub))
                    {
                        other.IsSubmenuOpen = false;
                    }
                }
            }
            sub.IsSubmenuOpen = true;
        };

        System.Windows.Threading.DispatcherTimer? closeTimer = null;
        void ScheduleClose()
        {
            closeTimer?.Stop();
            closeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer?.Stop();
                // 鼠标已回到宿主或弹层内则不收（IsMouseOver 跨 Popup 视觉树成立）
                if (!sub.IsMouseOver && !(popup?.IsMouseOver ?? false))
                {
                    sub.IsSubmenuOpen = false;
                }
            };
            closeTimer.Start();
        }

        sub.MouseLeave += (_, _) => ScheduleClose();
        if (popup is not null)
        {
            popup.MouseEnter += (_, _) => closeTimer?.Stop(); // 移入弹层：取消收回
            popup.MouseLeave += (_, _) => ScheduleClose();
        }

        // 点击兜底：仅当角色未被识别为 header（StackPanel 宿主下可能发生）时手动开合——
        // header 角色下 MenuItem 内建已开合一次，再 toggle 会互相抵消（"点不开"的机制之一）。
        sub.Click += (_, _) =>
        {
            if (sub.Role is MenuItemRole.TopLevelItem or MenuItemRole.SubmenuItem)
            {
                sub.IsSubmenuOpen = !sub.IsSubmenuOpen;
            }
        };
    }

    private static void Fill(System.Collections.IList target, IReadOnlyList<MenuItemDef> items, Action<string> execute)
    {
        // 同层唯一助记（Alt 访问键）：Separator 与无拉丁字符的中文项不分配。
        var accessKeys = MenuAccessKeys.Assign(
            items.Select(i => i.Kind == MenuItemKind.Separator ? string.Empty : i.Text).ToList());

        for (var index = 0; index < items.Count; index++)
        {
            var def = items[index];
            var header = (object?)accessKeys[index] ?? def.Text;
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
                        Header = header,
                        IsEnabled = def.IsEnabled,
                        FontWeight = def.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                        InputGestureText = def.GestureText,
                        // 子菜单宿主必须用自建模板（PART_Popup 令牌背板）——默认模板 Popup 是
                        // 系统 SystemColors 背板（深色系统=黑块，"点击新建没反应"的实况）。
                        Style = MenuStyling.CreateSubmenuStyle(),
                    };
                    AttachIcon(sub, def);
                    WireSubmenuHover(sub);
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
                        Header = header,
                        IsEnabled = def.IsEnabled,
                        IsChecked = def.IsChecked,
                        FontWeight = def.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                        InputGestureText = def.GestureText,
                        Style = MenuStyling.CreateItemStyle(),
                    };
                    AttachIcon(item, def);
                    item.Click += (_, _) =>
                    {
                        // 先关窗再执行（审查 P2-2）：旧顺序"命令同步执行后才关窗"有两个问题——
                        // ① 命令里有同步阻塞/模态操作时菜单滞留屏幕；② 内联重命名靠"命令内 Focus 抢激活
                        //    → 菜单失焦 → Deactivated 关窗"成立，属巧合契约。改为关窗后经 Dispatcher
                        //    （Input 优先级）执行命令：关窗完成、宿主窗口恢复激活后命令才跑，焦点依赖按
                        //    常规时序成立。
                        execute(def.Id);
                        item.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
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
                        });
                    };
                    target.Add(item);
                    break;
                }
            }
        }
    }
}
