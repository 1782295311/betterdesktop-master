// BetterDesktop.Shell.MenuBar — 搜索独立弹出面板（继承 MenuBarPopupWindow：失焦关闭）
// 复用 shell-search 的聚合搜索服务（IStartMenuSearchService：程序 / 设置 / 文件三类）。
// 设计：
//   - 顶部搜索框（TextBox）；输入后防抖 350ms 触发搜索（SearchAsync 同步执行各 Provider，
//     必须后台 Task 执行避免卡 UI 线程；防抖由 UI 层负责，shell-search README 明示）。
//   - 结果按 Category（App / Settings / File）分组展示；点击行启动：
//       结果.Execute() → AppItem(ShortcutPath/TargetPath，UWP 走 shell:AppsFolder AUMID) → LaunchPath。
//   - 服务缺失（null）时显示占位"搜索不可用"，不崩溃（M10 降级）。
//   - 颜色/字体/动画全部走主题令牌（基类 ApplyContent 统一挂载）。

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（全局搜索面板，白话 → 方法）──
//   "面板整体 / 执行搜索 / 渲染结果" → BuildContent / RunSearchAsync / RenderResults
//   "分组标题 / 单条结果行 / 详情文案" → CreateGroupHeader / CreateResultRow / ResolveDetailText；结果图标异步加载 LoadResultIconAsync
//   "结果右键菜单 / 打开 / 在资源管理器定位" → ShowResultMenu（AddMenuItem 加项）/ Launch / RevealInExplorer / ResolveRevealPath
//   "空结果态 / 类型字形"             → ShowEmpty / CreateSettingsGlyph / CreateFolderGlyph
//   搜索数据源（应用/设置/文件）在 StartMenuService.SearchAsync 与各搜索 Provider。
// ────────────────────────────────────

/// <summary>菜单栏搜索面板：搜索程序 / 设置 / 文件（复用 shell-search 聚合服务）。</summary>
internal sealed class SearchPopupWindow : MenuBarPopupWindow
{
    // 含搜索输入框：禁用 WS_EX_NOACTIVATE，否则点击后窗口不获焦点、键盘输入落不进 TextBox。
    protected override bool UseNoActivateWindowStyle => false;

    private const double DefaultWidth = 440;

    private readonly IStartMenuSearchService? _search;
    private readonly IAppIconService? _appIcon;
    private readonly IPinningService? _pinning;
    private readonly DispatcherTimer _debounce;
    private TextBox? _queryBox;
    private StackPanel? _resultHost;
    private int _generation; // 丢弃过期结果：每次新搜索递增，异步回写前比对

    public SearchPopupWindow(
        IStartMenuSearchService? search,
        IAppIconService? appIcon,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null,
        IPinningService? pinning = null)
        : base(vibrancy, appearance)
    {
        _search = search;
        _appIcon = appIcon;
        _pinning = pinning;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        // 防抖：输入停止 350ms 后才搜索，避免逐键触发 SearchAsync（同步枚举，卡 UI）。
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 搜索框 ----
        // 白底黑字：显式设置 Foreground（否则基类 BindThemeForeground 会把未设色的 Control
        // 绑到 ThemeForeground——暗色主题下即白字，白字白底不可读）。显式设色后被基类跳过。
        _queryBox = new TextBox
        {
            Height = 30,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
        };
        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            Child = _queryBox,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _queryBox.TextChanged += (_, _) =>
        {
            // 防抖：每次输入重启 350ms 计时；空串则立即清空结果
            _debounce.Stop();
            if (string.IsNullOrWhiteSpace(_queryBox.Text))
            {
                ShowEmpty("输入关键字开始搜索");
                return;
            }
            _debounce.Start();
        };
        _queryBox.PreviewKeyDown += (_, e) =>
        {
            // Esc 关闭面板
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
        column.Children.Add(inputBorder);

        column.Children.Add(CreateSeparator());

        // ---- 结果区 ----
        _resultHost = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer
        {
            Content = _resultHost,
            MaxHeight = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        column.Children.Add(scroll);

        root.Child = column;

        if (_search is null)
        {
            ShowEmpty("搜索服务不可用（shell.search 未加载）");
        }
        else
        {
            ShowEmpty("输入关键字开始搜索");
            // 面板打开即聚焦搜索框（不抢输入法焦点，只抢键盘焦点到输入框）
            Dispatcher.BeginInvoke(new Action(() => _queryBox?.Focus()), DispatcherPriority.Input);
        }

        return root;
    }

    /// <summary>后台执行搜索（SearchAsync 同步枚举，后台 Task 防卡 UI）。</summary>
    private async Task RunSearchAsync()
    {
        var query = _queryBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query) || _search is null) return;
        var gen = ++_generation;
        try
        {
            var results = await Task.Run(() => _search.SearchAsync(query, CancellationToken.None)).ConfigureAwait(true);
            // 丢弃过期结果：搜索期间用户又输入了
            if (gen != _generation || _resultHost is null) return;
            if (results.Count == 0)
            {
                ShowEmpty("没有找到匹配结果");
                return;
            }
            RenderResults(results);
        }
        catch
        {
            // M10：搜索失败静默降级，不打断菜单栏其余部分
            if (gen == _generation) ShowEmpty("搜索失败，请重试");
        }
    }

    /// <summary>
    /// 渲染结果：**全局按 Score 排序**（聚合层已降序排好），只在类别切换处插入分组标题。
    /// 刻意不按"应用→设置→文件"固定分组渲染——那会让分组顺序压过置信度
    /// （搜 maa 时强匹配的 MAA 文件被无关应用组压在下面，正是用户反馈的问题）。
    /// </summary>
    private void RenderResults(IReadOnlyList<SearchResult> results)
    {
        if (_resultHost is null) return;
        _resultHost.Children.Clear();

        string? lastCategory = null;
        foreach (var result in results)
        {
            if (!string.Equals(result.Category, lastCategory, StringComparison.OrdinalIgnoreCase))
            {
                lastCategory = result.Category;
                _resultHost.Children.Add(CreateGroupHeader(CategoryDisplayName(result.Category)));
            }
            _resultHost.Children.Add(CreateResultRow(result));
        }
    }

    /// <summary>分组标题（类别切换处显示，次要色）。</summary>
    private FrameworkElement CreateGroupHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 8, 0, 2)
        };
        SetThemeBinding(header, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return header;
    }

    private static string CategoryDisplayName(string category) => category.ToLowerInvariant() switch
    {
        "app" => "应用",
        "settings" => "设置",
        "file" => "文件",
        _ => category
    };

    /// <summary>
    /// 一条结果行（图标列 + 两行文本，适用于所有类别）：上行主标题（加粗）、下行路径/说明（次要色，可截断）。
    /// 副标题为空时从 AppItem 补真实目标路径（TargetPath → ShortcutPath），保证每条结果都能看到来源。
    /// 图标按类别取：App=真实应用图标（IAppIconService 异步提取）；File=关联图标/文件夹自绘；
    /// Settings=自绘齿轮（字体码位不可信，一律自绘几何）；提取失败=空白占位（对齐不乱）。
    /// </summary>
    private FrameworkElement CreateResultRow(SearchResult result)
    {
        var row = new Border
        {
            MinHeight = 44,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(10, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent
        };

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 图标列（始终占位，保证列对齐）
        var iconHost = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        if (string.Equals(result.Category, "Settings", StringComparison.OrdinalIgnoreCase))
        {
            iconHost.Children.Add(CreateSettingsGlyph());
        }
        else if (result.LaunchPath is not null && Directory.Exists(result.LaunchPath))
        {
            iconHost.Children.Add(CreateFolderGlyph());
        }
        else
        {
            var image = new Image
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            iconHost.Children.Add(image);
            _ = LoadResultIconAsync(result, image);
        }
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 上行：主标题
        var title = new TextBlock
        {
            Text = result.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        column.Children.Add(title);

        // 下行：路径 / 说明（Subtitle 缺省时从 AppItem 补真实路径）
        var detail = ResolveDetailText(result);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var sub = new TextBlock
            {
                Text = detail,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            SetThemeBinding(sub, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            column.Children.Add(sub);
        }

        Grid.SetColumn(column, 2);
        grid.Children.Add(column);
        row.Child = grid;

        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = MenuBarTheme.Pressed;
        row.MouseLeftButtonUp += (_, _) =>
        {
            Launch(result);
            Hide(); // 点结果即收起面板
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowResultMenu(row, result);
        };
        return row;
    }

    // ============================================================
    //  结果右键菜单（"谁的菜单谁管理"：本面板自建 WPF ContextMenu）
    // ============================================================

    /// <summary>按结果类别给右键菜单：打开（默认）→ 固定/取消固定（应用）→ 位置与路径（有真实路径的）。</summary>
    private void ShowResultMenu(FrameworkElement anchor, SearchResult result)
    {
        var menu = new ContextMenu();
        var hasItem = false;

        // 1) 打开（默认动作，加粗；与左键行为一致）
        hasItem |= AddMenuItem(menu, "打开", isDefault: true, () =>
        {
            Launch(result);
            Hide();
        });

        // 2) 应用类：固定到 Dock / 从 Dock 取消固定（固定服务缺失则跳过，M10）
        if (result.AppItem is not null && _pinning is not null)
        {
            var pinned = false;
            try { pinned = _pinning.IsPinned("dock", result.AppItem.Id); } catch { /* 判定失败按未固定处理 */ }

            if (pinned)
            {
                var appId = result.AppItem.Id;
                hasItem |= AddMenuItem(menu, "从 Dock 取消固定", isDefault: false, () => _pinning.Unpin("dock", appId));
            }
            else
            {
                var appItem = result.AppItem;
                hasItem |= AddMenuItem(menu, "固定到 Dock", isDefault: false, () => _pinning.Pin("dock", appItem));
            }
        }

        // 3) 有真实文件路径的结果：打开所在位置 / 复制路径；设置类给复制链接
        var path = ResolveRevealPath(result);
        if (!string.IsNullOrEmpty(path))
        {
            hasItem |= AddMenuItem(menu, "打开所在位置", isDefault: false, () => RevealInExplorer(path));
            hasItem |= AddMenuItem(menu, "复制路径", isDefault: false, () => Clipboard.SetText(path));
        }
        else if (result.LaunchPath is not null && result.LaunchPath.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            hasItem |= AddMenuItem(menu, "复制链接", isDefault: false, () => Clipboard.SetText(result.LaunchPath));
        }

        if (!hasItem)
        {
            return;
        }

        // 弹菜单期间挂起"外点收起"：点菜单项在几何上位于面板外，不挂起会菜单未执行就收面板
        SetOutsideClickHideSuppressed(true);
        menu.Closed += (_, _) => SetOutsideClickHideSuppressed(false);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        var physical = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(anchor).PixelsPerDip;
        menu.HorizontalOffset = physical.X / dpi;
        menu.VerticalOffset = physical.Y / dpi;
        menu.IsOpen = true;
    }

    private static bool AddMenuItem(ContextMenu menu, string header, bool isDefault, Action action)
    {
        var item = new MenuItem { Header = header };
        if (isDefault)
        {
            item.FontWeight = FontWeights.Bold;
        }
        item.Click += (_, _) =>
        {
            try { action(); }
            catch { /* 菜单动作失败静默（M10） */ }
        };
        _ = menu.Items.Add(item);
        return true;
    }

    /// <summary>可"打开所在位置/复制"的真实文件路径：应用取 TargetPath→ShortcutPath，文件取 LaunchPath（排除 ms-settings: URI）。</summary>
    private static string? ResolveRevealPath(SearchResult result)
    {
        if (result.AppItem is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.AppItem.TargetPath) && File.Exists(result.AppItem.TargetPath))
            {
                return result.AppItem.TargetPath;
            }
            if (!string.IsNullOrWhiteSpace(result.AppItem.ShortcutPath) && File.Exists(result.AppItem.ShortcutPath))
            {
                return result.AppItem.ShortcutPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.LaunchPath)
            && !result.LaunchPath.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            && (File.Exists(result.LaunchPath) || Directory.Exists(result.LaunchPath)))
        {
            return result.LaunchPath;
        }

        return null;
    }

    /// <summary>在资源管理器中定位文件（目录则直接打开该目录）。</summary>
    private static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\""));
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
    }

    /// <summary>
    /// 异步提取结果图标（大图标缩小显示，保证清晰）：应用走 IAppIconService（ExtraLarge/Jumbo 高清源），
    /// 文件走 ManagedShell IconHelper（ExtraLarge 48px 原生帧，与 App 类同管线）。
    /// 刻意不用 Icon.ExtractAssociatedIcon——它只有 32×32，高 DPI 下缩小显示会发糊。
    /// 失败保持空白占位（列对齐不乱，M10 降级）。
    /// </summary>
    private async Task LoadResultIconAsync(SearchResult result, Image target)
    {
        try
        {
            ImageSource? source = null;

            if (result.AppItem is not null)
            {
                if (_appIcon is not null)
                {
                    source = await _appIcon.GetIconAsync(result.AppItem, CancellationToken.None).ConfigureAwait(true);
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.LaunchPath) && File.Exists(result.LaunchPath))
            {
                var path = result.LaunchPath;
                // 文件类：与 shell-app-source 的图标服务同管线（ManagedShell）取 ExtraLarge(48) 原生帧。
                // 关键：IconHelper 必须在其专用 COM 任务调度器（IconScheduler）上执行——
                // 普通线程池线程 COM 初始化不对，提取会静默失败（此前图标空白的根因）。
                // GetImageFromHIcon 内部会自动 Freeze 并 DestroyIcon(hIcon)。
                source = await Task.Factory.StartNew(() =>
                {
                    var hIcon = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(
                        path, ManagedShell.Common.Enums.IconSize.ExtraLarge);
                    return hIcon == IntPtr.Zero
                        ? null
                        : ManagedShell.Common.Helpers.IconImageConverter.GetImageFromHIcon(hIcon);
                }, CancellationToken.None, TaskCreationOptions.None, ManagedShell.Common.Helpers.IconHelper.IconScheduler)
                    .ConfigureAwait(true);

                // 回退：IconScheduler 提取失败（无关联图标等）时用 ExtractAssociatedIcon（32px，聊胜于无）
                if (source is null)
                {
                    try
                    {
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (icon is not null)
                        {
                            source = Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                        }
                    }
                    catch { /* 回退也失败：保持空白占位 */ }
                }
            }

            if (source is not null)
            {
                target.Source = source;
            }
        }
        catch
        {
            // 图标提取失败：保持空白占位（列对齐不乱，M10 降级）
        }
    }

    /// <summary>设置类结果的自绘齿轮占位图标（圆环 + 4 齿 + 中心点）。</summary>
    private static FrameworkElement CreateSettingsGlyph()
    {
        var brush = MenuBarTheme.Foreground;
        var canvas = new Canvas { Width = 16, Height = 16 };

        var ring = new Ellipse
        {
            Width = 7.5,
            Height = 7.5,
            Stroke = brush,
            StrokeThickness = 1.3,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(ring, 4.25);
        Canvas.SetTop(ring, 4.25);
        canvas.Children.Add(ring);

        var dot = new Ellipse { Width = 2.2, Height = 2.2, Fill = brush };
        Canvas.SetLeft(dot, 6.9);
        Canvas.SetTop(dot, 6.9);
        canvas.Children.Add(dot);

        // 4 齿：上/下/左/右短线（从圆环向外延伸）
        void AddTooth(double x, double y, double w, double h)
        {
            var tooth = new Rectangle { Width = w, Height = h, RadiusX = 0.5, RadiusY = 0.5, Fill = brush };
            Canvas.SetLeft(tooth, x);
            Canvas.SetTop(tooth, y);
            canvas.Children.Add(tooth);
        }
        AddTooth(7.2, 1.2, 1.6, 2.6);   // 上
        AddTooth(7.2, 12.2, 1.6, 2.6);  // 下
        AddTooth(1.2, 7.2, 2.6, 1.6);   // 左
        AddTooth(12.2, 7.2, 2.6, 1.6);  // 右

        return new Viewbox { Child = canvas, Width = 16, Height = 16, Stretch = Stretch.Uniform };
    }

    /// <summary>文件夹自绘图标（标签 + 主体轮廓），用于文件类结果中目录条目的占位。</summary>
    private static FrameworkElement CreateFolderGlyph()
    {
        var brush = MenuBarTheme.Foreground;
        var canvas = new Canvas { Width = 16, Height = 14 };

        // 标签
        var tab = new Rectangle
        {
            Width = 6,
            Height = 3,
            RadiusX = 0.8,
            RadiusY = 0.8,
            Fill = brush
        };
        Canvas.SetLeft(tab, 1.0);
        Canvas.SetTop(tab, 2.0);
        canvas.Children.Add(tab);

        // 主体（描边圆角矩形）
        var body = new Rectangle
        {
            Width = 14,
            Height = 9,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Stroke = brush,
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(body, 1.0);
        Canvas.SetTop(body, 4.0);
        canvas.Children.Add(body);

        return new Viewbox { Child = canvas, Width = 16, Height = 14, Stretch = Stretch.Uniform };
    }

    /// <summary>结果详情文本：优先 Subtitle；为空且是应用结果时取真实目标路径（TargetPath → ShortcutPath）。</summary>
    private static string ResolveDetailText(SearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Subtitle)) return result.Subtitle;
        if (result.AppItem is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.AppItem.TargetPath)) return result.AppItem.TargetPath!;
            if (!string.IsNullOrWhiteSpace(result.AppItem.ShortcutPath)) return result.AppItem.ShortcutPath!;
        }
        return string.Empty;
    }

    /// <summary>启动结果：Execute → AppItem → LaunchPath（自包含，不依赖开始菜单服务）。</summary>
    private static void Launch(SearchResult result)
    {
        try
        {
            if (result.Execute is not null) { result.Execute(); return; }

            if (result.AppItem is not null)
            {
                // UWP 应用：通过 AppUserModelId 经 shell:AppsFolder 启动
                if (!string.IsNullOrWhiteSpace(result.AppItem.AppUserModelId))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "shell:AppsFolder\\" + result.AppItem.AppUserModelId)
                    { UseShellExecute = true });
                    return;
                }
                var appPath = !string.IsNullOrWhiteSpace(result.AppItem.TargetPath)
                    ? result.AppItem.TargetPath
                    : result.AppItem.ShortcutPath;
                if (!string.IsNullOrWhiteSpace(appPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appPath) { UseShellExecute = true });
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(result.LaunchPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.LaunchPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默，不打断菜单栏（M10）
        }
    }

    private void ShowEmpty(string text)
    {
        if (_resultHost is null) return;
        _resultHost.Children.Clear();
        var hint = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 18, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        _resultHost.Children.Add(hint);
    }

    private static Border CreateSeparator()
    {
        var sep = new Border { Height = 1, Margin = new Thickness(4, 6, 4, 6) };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
        _generation++; // 使在途搜索结果作废
        base.OnClosed(e);
    }
}
