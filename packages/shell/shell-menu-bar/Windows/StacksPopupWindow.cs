// BetterDesktop.Shell.MenuBar — cairoshell「Stacks」式全宽文件条带
// 对齐 cairoshell 菜单栏 Stacks 的展现方式（源码依据：CairoDesktop.MenuBar\StacksContainer.xaml.cs
// PopupWidth = MenuBar.Width；StacksScroller = ListView(FolderViewStyle)；Cairo.xaml FolderViewStyle）：
//   - 弹出面板**宽度 = 所在显示器工作区全宽**（cairoshell 绑 MenuBar.Width = AppBar 全屏宽），
//     高度固定条带（cairoshell 85px），贴菜单栏正下方（Placement=Bottom）；
//   - 内部 = **横向单排**（cairoshell: VirtualizingStackPanel Orientation=Horizontal）：
//     每项垂直小卡片（32px 图标在上 + 80px 文件名在下），**不分列不换行**，滚轮横向滚动；
//   - 排序：文件夹优先 → 名称升序（cairoshell FolderView.cs 同规则）；
//   - 「位置」入口 = places 条带（known folders 固定项横排，cairoshell Places 菜单同数据源），
//     点击固定项在同一全宽条带内进入该文件夹内容（带回退卡片），与下载/文档界面完全同构对齐；
//   - 条目点击：文件夹 → 条带内进入下一级；文件 → 系统默认程序打开；
//   - 空文件夹 → 居中提示文本（cairoshell sStacks_Empty 同款）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>全宽文件条带面板（cairoshell Stacks 范式）：贴菜单栏下方、横向单排文件卡片。
/// 「位置 / 下载 / 文档」三个入口共用本面板，界面形态完全对齐。</summary>
internal sealed class StacksPopupWindow : MenuBarPopupWindow
{
    /// <summary>条带高度（cairoshell FolderViewStyle Height=85，此处含主题内边距略放宽）。</summary>
    public const double StripHeight = 92;

    private const int MaxEntries = 100;      // 条目上限（横向单排无虚拟化，防大目录卡顿，M10）
    private const double EntryWidth = 80;    // 条目宽（cairoshell Icon.xaml 文件名 Width=80）
    private const double IconSizePx = 32;    // 条目图标尺寸（cairoshell 32px）

    private readonly IVibrancyService _vibrancy;
    private readonly ISettingsService? _settings;
    private Border? _host;
    private ScrollViewer? _scroller;
    private readonly List<string> _navStack = new(); // Folder 模式的目录栈（末位 = 当前目录；空 = 位于位置条带）

    public StacksPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null, ISettingsService? settings = null)
        : base(vibrancy, appearance)
    {
        _vibrancy = vibrancy;
        _settings = settings;
    }

    protected override FrameworkElement BuildContent()
    {
        _host = new Border();
        return _host;
    }

    /// <summary>
    /// 在按钮所在显示器打开全宽条带。
    /// ⚠️ 不走 PopupAnchor（它会按按钮左对齐 + 回钳）：Stacks 的规范是**横贯整个工作区**，
    /// x = 工作区左缘、宽 = 工作区宽、y = 菜单栏正下方（cairoshell Popup Placement=Bottom 同位）。
    /// </summary>
    /// <param name="anchorPhysicalPoint">触发按钮的物理屏幕坐标（PointToScreen 结果）。</param>
    /// <param name="path">要展示的文件夹。</param>
    public void Open(Point anchorPhysicalPoint, string path)
    {
        EnsureBuilt();
        MoveTo(anchorPhysicalPoint);

        _navStack.Clear();
        _navStack.Add(path);
        _host!.Child = BuildFolderStrip(showBack: false); // 直达入口（下载/文档）：无返回卡

        ShowAt(new Point(workAreaLeft, workAreaTop));
        ResetScroll();
    }

    /// <summary>打开「位置」条带：known folders 固定项横排（与下载/文档同形态对齐）。</summary>
    public void OpenPlaces(Point anchorPhysicalPoint)
    {
        EnsureBuilt();
        MoveTo(anchorPhysicalPoint);
        _host!.Child = BuildPlacesStrip();
        ShowAt(new Point(workAreaLeft, workAreaTop));
        ResetScroll();
    }

    // 工作区几何缓存（Open/OpenPlaces 计算一次，ShowAt 使用）
    private double workAreaLeft;
    private double workAreaTop;

    private void MoveTo(Point anchorPhysicalPoint)
    {
        var workArea = MenuBarScreen.GetWorkArea(anchorPhysicalPoint);
        Width = workArea.Width;
        Height = StripHeight;
        workAreaLeft = workArea.Left;
        workAreaTop = workArea.Top + MenuBarMetrics.MenuBarHeight + 4;
    }

    private void ResetScroll()
    {
        _scroller?.ScrollToHorizontalOffset(0); // 重开回到开头
    }

    private void EnsureBuilt()
    {
        if (_host is null)
        {
            ApplyContent(BuildContent());
        }
    }

    // ======== 视图一：位置条带（known folders 固定项） ========

    private FrameworkElement BuildPlacesStrip()
    {
        _navStack.Clear();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(CreateLabelCard("位置", "选择要浏览的位置"));

        foreach (var (name, dir, shell) in KnownPlaces())
        {
            var (d, s) = (dir, shell);
            if (d is null && s is null)
            {
                continue;
            }

            panel.Children.Add(CreateCard(name, toolTip: d ?? s ?? name, iconPath: d, onClick: () =>
            {
                if (d is not null && Directory.Exists(d))
                {
                    // 条带内进入：位置 → 文件夹内容，同一全宽条带（与下载/文档界面同构）
                    _navStack.Add(d);
                    _host!.Child = BuildFolderStrip(showBack: true);
                    ResetScroll();
                }
                else if (s is not null)
                {
                    OpenInExplorer(s);
                }
            }));
        }

        return WrapInScroller(panel);
    }

    // ======== 视图二：文件夹内容条带 ========

    private FrameworkElement BuildFolderStrip(bool showBack)
    {
        if (_navStack.Count == 0)
        {
            return BuildPlacesStrip();
        }

        var path = _navStack[^1];
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };

        if (showBack)
        {
            panel.Children.Add(CreateCard("← 返回", toolTip: "返回上一级", iconPath: null, onClick: GoBackInStrip));
        }

        var dirs = new List<string>();
        var files = new List<string>();
        try
        {
            dirs = Directory.EnumerateDirectories(path).ToList();
            files = Directory.EnumerateFiles(path).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return BuildHint("（无法访问该文件夹）");
        }
        catch (IOException)
        {
            return BuildHint("（读取失败）");
        }

        // cairoshell FolderView 同规则：文件夹优先，再按名称升序
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var dir in dirs)
        {
            if (count >= MaxEntries)
            {
                break;
            }

            var full = dir;
            panel.Children.Add(CreateCard(Path.GetFileName(full), toolTip: full, iconPath: full, onClick: () =>
            {
                // 文件夹 → 条带内进入下一级（保持全宽条带形态，← 返回可逐级回退）
                _navStack.Add(full);
                _host!.Child = BuildFolderStrip(showBack: true);
                ResetScroll();
            }));
            count++;
        }

        foreach (var file in files)
        {
            if (count >= MaxEntries)
            {
                break;
            }

            var full = file;
            panel.Children.Add(CreateCard(Path.GetFileName(full), toolTip: full, iconPath: full, onClick: () => OpenFile(full)));
            count++;
        }

        if (count == 0)
        {
            return BuildHint(showBack ? "（空文件夹）" : "（空文件夹）");
        }

        return WrapInScroller(panel);
    }

    private void GoBackInStrip()
    {
        if (_navStack.Count == 0)
        {
            _host!.Child = BuildPlacesStrip();
            ResetScroll();
            return;
        }

        _navStack.RemoveAt(_navStack.Count - 1);
        if (_navStack.Count == 0)
        {
            // 栈底：回到位置条带
            _host!.Child = BuildPlacesStrip();
        }
        else
        {
            _host!.Child = BuildFolderStrip(showBack: true);
        }

        ResetScroll();
    }

    // ======== 通用件 ========

    private FrameworkElement WrapInScroller(StackPanel panel)
    {
        // 横向滚动容器：竖向禁用、滚轮转横向（cairoshell Scroller_PreviewMouseWheel 同款）
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = panel
        };
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        return _scroller;
    }

    private static FrameworkElement BuildHint(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0),
            Opacity = 0.7
        };
    }

    /// <summary>条带首位的标题卡（不可点击，用于标识当前条带语义）。</summary>
    private FrameworkElement CreateLabelCard(string label, string toolTip)
    {
        return new Border
        {
            Width = EntryWidth,
            CornerRadius = new CornerRadius(4),
            ToolTip = toolTip,
            Opacity = 0.75,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>
    /// 条目 = 垂直小卡片：32px 图标在上 + 80px 名称在下（cairoshell Icon.xaml 同构）。
    /// iconPath 非空 → 异步提取真实图标（IconScheduler 调度，不卡 UI）；否则用描边占位块。
    /// </summary>
    private FrameworkElement CreateCard(string label, string toolTip, string? iconPath, Action onClick)
    {
        FrameworkElement icon = iconPath is not null
            ? CreateAsyncIcon(iconPath)
            : new Border
            {
                Width = IconSizePx,
                Height = IconSizePx,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrushes.Tint("ThemeForeground", 0.47)
            };

        var entry = new Border
        {
            Width = EntryWidth,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Child = new StackPanel
            {
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 28,
                        Margin = new Thickness(2, 0, 2, 2)
                    }
                }
            }
        };

        entry.MouseEnter += (_, _) => entry.Background =
            ThemeBrushes.AccentTint(0.15);
        entry.MouseLeave += (_, _) => entry.Background = Brushes.Transparent;
        entry.MouseLeftButtonUp += (_, _) => onClick();
        return entry;
    }

    private Image CreateAsyncIcon(string path)
    {
        var icon = new Image
        {
            Width = IconSizePx,
            Height = IconSizePx,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0, 2, 0, 2)
        };
        _ = LoadIconAsync(path, icon);
        return icon;
    }

    private static void OpenInExplorer(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败静默（M10）
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败静默（M10）
        }
    }

    /// <summary>
    /// known folders 固定项（对齐 cairoshell PlacesMenu 数据源 + 文件管理器左侧导航栏）。
    /// 下载目录 .NET 无 SpecialFolder 项且中文系统已本地化：优先探测 Downloads/下载，
    /// 都不存在则回退 shell:Downloads 交给 explorer 解析。
    /// </summary>
    private static IEnumerable<(string Name, string? Dir, string? Shell)> KnownPlaces()
    {
        yield return ("此电脑", null, "shell:MyComputerFolder");

        var profile = SafeProfile();
        var downloads = profile is null ? null : Path.Combine(profile, "Downloads");
        var downloadsCn = profile is null ? null : Path.Combine(profile, "下载");
        var dl = downloads is not null && Directory.Exists(downloads)
            ? downloads
            : downloadsCn is not null && Directory.Exists(downloadsCn) ? downloadsCn : null;
        yield return ("下载", dl, dl is null ? "shell:Downloads" : null);

        yield return ("桌面", SafeFolder(Environment.SpecialFolder.DesktopDirectory), null);
        yield return ("文档", SafeFolder(Environment.SpecialFolder.Personal), null);
        yield return ("图片", SafeFolder(Environment.SpecialFolder.MyPictures), null);
        yield return ("音乐", SafeFolder(Environment.SpecialFolder.MyMusic), null);
        yield return ("视频", SafeFolder(Environment.SpecialFolder.MyVideos), null);
    }

    private static string? SafeProfile()
    {
        try
        {
            var p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var p = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(p) || !Directory.Exists(p) ? null : p;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>图标异步提取（Large，IconScheduler 串行调度；shell-desktop 同款管线）。</summary>
    private static async Task LoadIconAsync(string path, Image target)
    {
        try
        {
            var source = await Task.Factory.StartNew(
                () =>
                {
                    var hIcon = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(
                        path, ManagedShell.Common.Enums.IconSize.Large);
                    return hIcon == IntPtr.Zero
                        ? null
                        : ManagedShell.Common.Helpers.IconImageConverter.GetImageFromHIcon(hIcon);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
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
}
