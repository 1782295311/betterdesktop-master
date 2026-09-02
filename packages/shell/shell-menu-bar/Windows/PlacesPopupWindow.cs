// BetterDesktop.Shell.MenuBar — cairoshell 式「位置」弹出面板
// 对齐 cairoshell 菜单栏的位置（Places）行为：
//   - 左区「位置」按钮 → 面板显示 known folders 固定项（= 文件管理器左侧导航栏：桌面/下载/文档/图片/音乐/视频）；
//   - 左区「下载/文档」按钮 → 直接展开该文件夹的内容列表；
//   - 内容视图 = 单列条目列表，先填满一列再从左到右开新列（WrapPanel 竖排 + 横向滚动）；
//   - 条目单击：文件夹进入下一层、文件用系统默认程序打开；顶部「←」返回上一级/回到位置列表。
// 面板始终锚定触发按钮正下方（PopupAnchor 统一左对齐），不再弹出居中独立窗口。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>位置导航面板：known folders 列表 + 文件夹内容单列列表（从左到右换列）。</summary>
internal sealed class PlacesPopupWindow : MenuBarPopupWindow
{
    // 面板尺寸（逻辑单位）：左区按钮锚定弹窗时引用同源常量，避免两处漂移
    public const double PlacesWidth = 230;
    public const double PlacesHeight = 430;
    public const double FolderWidth = 330;
    public const double FolderHeight = 480;

    private const int MaxEntries = 200;    // 单目录条目上限（大目录防卡顿，M10）
    private const double EntryWidth = 280; // 内容列表单列宽度
    private const double EntryHeight = 30;

    private Border? _host;                 // 视图容器：视图切换时替换 Child
    private string? _currentPath;
    private readonly List<string> _history = new();

    public PlacesPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
    }

    protected override FrameworkElement BuildContent()
    {
        _host = new Border();
        _host.Child = BuildPlacesView();
        return _host;
    }

    /// <summary>在指定坐标（按钮正下方）打开「位置」列表视图。</summary>
    public void OpenPlaces(Point pos)
    {
        EnsureBuilt();
        _host!.Child = BuildPlacesView();
        ShowAt(pos);
    }

    /// <summary>在指定坐标（按钮正下方）直接打开某文件夹的内容列表视图。</summary>
    public void OpenFolder(Point pos, string path)
    {
        EnsureBuilt();
        _history.Clear();
        _currentPath = null;
        NavigateTo(path, recordHistory: false);
        ShowAt(pos);
    }

    /// <summary>首次构建面板外观（之后视图切换只替换 _host.Child）。</summary>
    private void EnsureBuilt()
    {
        if (_host is null)
        {
            ApplyContent(BuildContent());
        }
    }

    // ======== 视图一：位置列表（known folders 固定项） ========

    private FrameworkElement BuildPlacesView()
    {
        _currentPath = null;
        _history.Clear();
        Width = PlacesWidth;
        Height = PlacesHeight;

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(new TextBlock
        {
            Text = "位置",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 8)
        });

        foreach (var (name, dir, shell) in KnownPlaces())
        {
            var (d, s) = (dir, shell);
            if (d is null && s is null)
            {
                continue;
            }

            root.Children.Add(CreateRow(name, isFolder: true, () =>
            {
                if (d is not null && Directory.Exists(d))
                {
                    NavigateTo(d, recordHistory: true);
                }
                else if (s is not null)
                {
                    OpenInExplorer(s);
                }
            }));
        }

        return root;
    }

    // ======== 视图二：文件夹内容（单列列表，从左到右换列） ========

    private FrameworkElement BuildFolderView(string path)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶部：返回 + 当前目录名
        var header = new Border
        {
            Padding = new Thickness(6, 6, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    CreateRow("←", isFolder: false, GoBack),
                    new TextBlock
                    {
                        Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 内容：先填满一列再从左到右开新列（竖向 WrapPanel + 横向滚动）
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Vertical,
            ItemWidth = EntryWidth,
            ItemHeight = EntryHeight,
            Margin = new Thickness(4)
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = wrap
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                if (wrap.Children.Count >= MaxEntries)
                {
                    break;
                }

                var full = dir;
                wrap.Children.Add(CreateRow(Path.GetFileName(dir), isFolder: true,
                    () => NavigateTo(full, recordHistory: true)));
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (wrap.Children.Count >= MaxEntries)
                {
                    break;
                }

                var full = file;
                wrap.Children.Add(CreateRow(Path.GetFileName(file), isFolder: false,
                    () => OpenFile(full)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            wrap.Children.Add(CreateRow("（无法访问该文件夹）", isFolder: false, () => { }));
        }
        catch (IOException)
        {
            wrap.Children.Add(CreateRow("（读取失败）", isFolder: false, () => { }));
        }

        if (wrap.Children.Count == 0)
        {
            wrap.Children.Add(CreateRow("（空文件夹）", isFolder: false, () => { }));
        }

        return root;
    }

    // ======== 导航 ========

    private void NavigateTo(string path, bool recordHistory)
    {
        if (!Directory.Exists(path) || _host is null)
        {
            return;
        }

        if (recordHistory && _currentPath is not null)
        {
            _history.Add(_currentPath);
        }

        _currentPath = path;
        Width = FolderWidth;
        Height = FolderHeight;
        _host.Child = BuildFolderView(path);
    }

    private void GoBack()
    {
        if (_history.Count > 0)
        {
            var prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            _currentPath = prev;
            Width = FolderWidth;
            Height = FolderHeight;
            if (_host is not null)
            {
                _host.Child = BuildFolderView(prev);
            }

            return;
        }

        // 已到栈底：回到位置列表
        if (_host is not null)
        {
            _host.Child = BuildPlacesView();
        }
    }

    // ======== 条目 / 图标 / 打开 ========

    /// <summary>列表条目：小图标 + 名称；悬停高亮，单击回调。</summary>
    private static Border CreateRow(string label, bool isFolder, Action onClick)
    {
        var row = new Border
        {
            Height = EntryHeight,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    CreateEntryIcon(isFolder),
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };

        row.MouseEnter += (_, _) => row.Background =
            new SolidColorBrush(Color.FromArgb(38, 0x7F, 0xB3, 0xFF));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    /// <summary>条目小图标：文件夹 = 金黄实心块；文件 = 灰白描边块（不依赖字体字形，任何主题可读）。</summary>
    private static FrameworkElement CreateEntryIcon(bool isFolder)
    {
        return new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = isFolder
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x5A))
                : Brushes.Transparent,
            BorderThickness = isFolder ? new Thickness(0) : new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 0xC0, 0xC8, 0xD4))
        };
    }

    /// <summary>
    /// known folders 固定项（对齐文件管理器左侧导航栏）。
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
}
