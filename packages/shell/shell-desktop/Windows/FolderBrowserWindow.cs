// BetterDesktop.Shell.Desktop — 独立文件夹浏览窗口
//
// 【对齐 cairoshell OpenLocationInWindow 范式】菜单栏"位置/下载/文档"等入口点击 →
// 打开独立文件浏览窗口（cairoshell 中为 FileManager 设置项，默认 explorer、可指向自研
// Cairo Explorer；本仓库对齐其"独立界面"语义，自绘浏览窗口），**桌面保持显示不动**。
// 桌面内导航（双击桌面文件夹等）仍走桌面自身，两者互不影响。
//
// 外观：ShellWindow 主题体系（ThemePanelBackground 半透明 + 毛玻璃 + 圆角 + 描边），
// 与菜单栏弹出面板同风格；不透皮肤图（UseSkinBackground=false）。
//
// 交互（MVP）：
//   - 双击文件夹 → 窗口内导航；双击文件 → ShellExecute 打开
//   - ↑ 上级 → 父目录（根目录时静默）
//   - 标题栏可拖拽移动，× 关闭；单例复用（Open 已有实例则导航 + 激活，不堆窗口）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Controls; // 复用 RelayCommand / DesktopItemRenderer 同款基础设施
using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Desktop.Templates;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Windows;

/// <summary>独立文件夹浏览窗口（单例；桌面导航之外的独立文件界面）。</summary>
public sealed class FolderBrowserWindow : ShellWindow
{
    private const double CellWidth = 86;
    private const double CellHeight = 92;

    private static FolderBrowserWindow? _instance;

    private readonly IVibrancyService _vibrancy;
    private readonly ISettingsService? _settings;
    private readonly BetterDesktop.Shell.ContextMenus.Contracts.IMenuService? _menus;
    private readonly BetterDesktop.Shell.ContextMenus.Contracts.IFileClassifier? _classifier;
    private readonly List<IDisposable> _menuHandles = [];
    private readonly Dictionary<Border, string> _cellPaths = [];
    private string _path;
    private WrapPanel? _panel;
    private TextBlock? _pathText;
    private TextBlock? _titleText;
    private ScrollViewer? _scroll;

    private FolderBrowserWindow(string path, IVibrancyService vibrancy, IAppearanceService? appearance,
        ISettingsService? settings = null,
        BetterDesktop.Shell.ContextMenus.Contracts.IMenuService? menus = null,
        BetterDesktop.Shell.ContextMenus.Contracts.IFileClassifier? classifier = null)
        : base(appearance, vibrancy)
    {
        _vibrancy = vibrancy;
        _settings = settings;
        _menus = menus;
        _classifier = classifier;
        _path = path;

        Title = "文件";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 860;
        Height = 560;
        MinWidth = 480;
        MinHeight = 320;

        BuildContent();

        // 统一右键菜单（shell-context-menu）：Scope=ShellFile 模板 + 能力过滤
        if (_menus is not null)
        {
            _menuHandles.Add(_menus.RegisterTemplate(new FolderMenuTemplate(this)));
            PreviewMouseRightButtonUp += OnMenuMouseUp;
        }
    }

    // ======== 窗口属性：文档窗口（可激活、任务栏可见、不置顶、可缩放） ========

    /// <inheritdoc />
    protected override bool DefaultTopmost => false;

    /// <inheritdoc />
    protected override bool DefaultShowActivated => true;

    /// <inheritdoc />
    protected override bool ShowInTaskbarDefault => true;

    /// <inheritdoc />
    protected override ResizeMode DefaultResizeMode => ResizeMode.CanResize;

    /// <summary>面板风格：不透皮肤图，半透明面板色透出毛玻璃（与菜单栏弹出面板一致）。</summary>
    protected override bool UseSkinBackground => false;

    // ======== 打开入口（单例复用） ========

    /// <summary>打开目录浏览窗口：已有实例则导航到目标目录并置前，否则新建。</summary>
    public static void Open(string path, IVibrancyService vibrancy, IAppearanceService? appearance,
        ISettingsService? settings = null,
        BetterDesktop.Shell.ContextMenus.Contracts.IMenuService? menus = null,
        BetterDesktop.Shell.ContextMenus.Contracts.IFileClassifier? classifier = null)
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.NavigateTo(path);
            _instance.Activate();
            return;
        }

        _instance = new FolderBrowserWindow(path, vibrancy, appearance, settings);
        _instance.Show();
        _instance.Activate();
    }

    // ======== 内容构建 ========

    private void BuildContent()
    {
        // 标题栏：标题 + 关闭（可拖拽移动窗口）
        _titleText = new TextBlock
        {
            Text = Path.GetFileName(_path.TrimEnd(Path.DirectorySeparatorChar)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var closeButton = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetThemeBinding(closeButton, TextBlock.ForegroundProperty, "ThemeForeground");
        closeButton.MouseLeftButtonUp += (_, _) => Close();

        var titleBar = new Border
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new Grid()
        };
        var titleGrid = (Grid)titleBar.Child;
        titleGrid.Children.Add(_titleText);
        titleGrid.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.MouseLeftButtonDown += (_, _) =>
        {
            if (MouseButtonState.Pressed == Mouse.LeftButton)
            {
                try { DragMove(); } catch { /* 非激活态拖拽异常忽略 */ }
            }
        };

        // 工具行：↑ 上级 + 路径显示
        var upButton = new Border
        {
            Width = 20,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock
            {
                Text = "↑",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetThemeBinding(upButton, TextBlock.ForegroundProperty, "ThemeForeground");
        upButton.MouseLeftButtonUp += (_, _) => NavigateToParent();

        _pathText = new TextBlock
        {
            Text = _path,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(_pathText, TextBlock.ForegroundProperty, "ThemeForeground");

        var toolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 6)
        };
        toolRow.Children.Add(upButton);
        toolRow.Children.Add(_pathText);

        // 图标区：瀑布列（横向滚动），对齐 cairoshell DesktopFolderViewStyle
        _panel = new WrapPanel { Orientation = Orientation.Vertical, ItemWidth = CellWidth, ItemHeight = CellHeight };
        _scroll = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
        };

        var body = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(toolRow, Dock.Top);
        body.Children.Add(titleBar);
        body.Children.Add(toolRow);
        body.Children.Add(_scroll);

        // 根 Border：主题令牌外观（与 MenuBarPopupWindow.ApplyContent 同款）
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(SystemCornerRadius),
            BorderThickness = new Thickness(AppearanceService?.CardBorderThickness ?? 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = body
        };
        SetThemeBinding(chrome, Border.BackgroundProperty, "ThemePanelBackground");
        SetThemeBinding(chrome, Border.BorderBrushProperty, "CardBorderBrush");
        SetThemeBinding(chrome, TextElement.ForegroundProperty, "ThemeForeground");

        Content = chrome;
        ChromeBorder = chrome;

        Loaded += (_, _) => Populate();
    }

    // ======== 导航与枚举 ========

    /// <summary>导航到指定目录（窗口内）。</summary>
    public void NavigateTo(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        _path = path;
        Populate();
    }

    private void NavigateToParent()
    {
        try
        {
            var parent = Directory.GetParent(_path);
            if (parent is not null)
            {
                NavigateTo(parent.FullName);
            }
        }
        catch
        {
            // 根目录/无权限：静默
        }
    }

    private void Populate()
    {
        if (_panel is null)
        {
            return;
        }

        _panel.Children.Clear();
        _pathText!.Text = _path;
        _pathText.ToolTip = _path;
        _titleText!.Text = Path.GetFileName(_path.TrimEnd(Path.DirectorySeparatorChar));

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(_path)
                .OrderBy(p => Directory.Exists(p) ? 0 : 1) // 目录在前
                .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                .Take(2000) // 防御超大目录首帧卡顿
                .ToList();
        }
        catch
        {
            return; // 无权限/目录消失：显示空
        }

        foreach (var entry in entries)
        {
            _panel.Children.Add(CreateItem(entry, Directory.Exists(entry)));
        }
    }

    private FrameworkElement CreateItem(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        // 显示名是否剥 .lnk 由桌面设置统一控制（与桌面图标一致）；ToolTip 保留完整名
        var hideLnk = _settings?.Get("desktop.hideLnkExtension", true) ?? true;
        var displayName = hideLnk && !isDirectory && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
        var icon = new Border
        {
            Width = 44,
            Height = 40,
            Child = isDirectory
                ? FolderGlyph()
                : new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true }
        };
        if (!isDirectory)
        {
            if (icon.Child is Image img)
            {
                _ = LoadFileIconAsync(path, img);
            }
        }

        var label = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.6 }
        };

        var cell = new Border
        {
            Width = CellWidth - 6,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 4, 2, 4),
            Child = Stack(icon, label),
            Cursor = Cursors.Hand,
            ToolTip = name
        };
        _cellPaths[cell] = path; // 右键路由映射（统一菜单服务）

        var dbl = new MouseBinding(
            new RelayCommand(() =>
            {
                if (isDirectory) NavigateTo(path);
                else StartFile(path);
            }),
            new MouseGesture(MouseAction.LeftDoubleClick));
        cell.InputBindings.Add(dbl);
        cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(40, 0x80, 0x80, 0x80));
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;

        return cell;
    }

    // ======== 统一右键菜单路由（shell-context-menu；Scope=ShellFile） ========

    private void OnMenuMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_menus is null)
        {
            return;
        }

        var path = FindCellPath(e.OriginalSource as DependencyObject);
        if (path is null)
        {
            return; // 空白处无菜单（浏览窗口空白不弹，避免误触）
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var physical = PointToScreen(e.GetPosition(this));
            var identity = _classifier?.Classify(path);
            var request = new BetterDesktop.Shell.ContextMenus.Contracts.MenuRequest(
                BetterDesktop.Shell.ContextMenus.Contracts.MenuScope.ShellFile,
                new FolderItemTarget(path, Directory.Exists(path)),
                new Point(physical.X / dpi, physical.Y / dpi),
                File: identity);
            _ = _menus.ShowAsync(request);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"右键菜单展示失败: {ex.Message}");
        }
    }

    private string? FindCellPath(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && _cellPaths.TryGetValue(border, out var path))
            {
                return path;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    internal void InvokeOpenEntry(string path, bool isDirectory)
    {
        if (isDirectory) NavigateTo(path);
        else StartFile(path);
    }

    internal void InvokeDeleteToRecycleBin(string path)
    {
        Task.Run(() =>
        {
            try
            {
                FileOps.DeleteToRecycleBin(path);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.desktop", $"删除失败 {path}: {ex.Message}");
            }
            Dispatcher.BeginInvoke(() => NavigateTo(_path)); // 重载列表
        });
    }

    private static StackPanel Stack(FrameworkElement icon, FrameworkElement label)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        sp.Children.Add(icon);
        sp.Children.Add(label);
        return sp;
    }

    private static void StartFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败静默（M10）
        }
    }

    /// <summary>文件图标异步提取（ExtraLarge，IconScheduler 调度；与桌面图标同源）。</summary>
    private static async System.Threading.Tasks.Task LoadFileIconAsync(string path, Image target)
    {
        try
        {
            var source = await System.Threading.Tasks.Task.Factory.StartNew(
                () =>
                {
                    var hIcon = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(
                        path, ManagedShell.Common.Enums.IconSize.ExtraLarge);
                    return hIcon == IntPtr.Zero
                        ? null
                        : ManagedShell.Common.Helpers.IconImageConverter.GetImageFromHIcon(hIcon);
                },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskCreationOptions.None,
                ManagedShell.Common.Helpers.IconHelper.IconScheduler);

            if (source is not null)
            {
                target.Source = source;
            }
        }
        catch
        {
            // 提取失败留空白（M10）
        }
    }

    /// <summary>文件夹自绘 glyph（与桌面图标一致）。</summary>
    private static FrameworkElement FolderGlyph()
    {
        var canvas = new Canvas { Width = 24, Height = 20, Background = Brushes.Transparent };
        var tab = new System.Windows.Shapes.Rectangle
        {
            Width = 9,
            Height = 4,
            RadiusX = 1,
            RadiusY = 1,
            Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0xC8, 0x4B))
        };
        Canvas.SetLeft(tab, 2);
        Canvas.SetTop(tab, 3);
        var body = new System.Windows.Shapes.Rectangle
        {
            Width = 21,
            Height = 13,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x5A)),
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0x00, 0x00, 0x00)),
            StrokeThickness = 0.8
        };
        Canvas.SetLeft(body, 1.5);
        Canvas.SetTop(body, 5.5);
        canvas.Children.Add(tab);
        canvas.Children.Add(body);
        return new Viewbox { Child = canvas, Width = 36, Height = 30, Stretch = Stretch.Uniform };
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
        base.OnClosed(e);
    }
}
