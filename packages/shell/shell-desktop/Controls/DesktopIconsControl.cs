// BetterDesktop.Shell.Desktop — 桌面图标网格
// 绑定 IDesktopBrowser：ItemsChanged 重建网格（图标 40 + 名称两行，竖排列优先近似 explorer 桌面）；
// 单击切换选中（Ctrl 多选）、双击启动（目录导航 / 文件 ShellExecute）。
// 交互（对齐 shell-context-menu MENU-SPECS §1/§2 实用子集）：
//   - 图标右键菜单：打开 / 剪切 / 复制 / 重命名（内联 TextBox）/ 删除 / 属性（SHObjectProperties）
//   - 空白右键菜单：新建文件夹 / 粘贴（CanPaste 有）/ 刷新 / 显示设置 / 个性化
//   - 拖放：图标拖出 = FileDrop（复制/移动到资源管理器等）；外部拖入 = 复制/移动进当前 Location
// 图标：目录与文件统一走 ManagedShell IconHelper（ExtraLarge，IconScheduler 调度）；
// 目录提取失败回退自绘文件夹 glyph（原 FolderGlyph）。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Desktop.Templates;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Controls;

/// <summary>桌面图标网格（透明背景，铺满桌面窗口工作区）。</summary>
public sealed class DesktopIconsControl : ScrollViewer, IDisposable
{
    private readonly IDesktopBrowser _browser;
    private readonly ISettingsService? _settings;
    private readonly IMenuService? _menus;
    private readonly IFileClassifier? _classifier;
    private readonly bool _useMenuService;
    private readonly List<IDisposable> _menuHandles = [];
    private readonly Dictionary<Border, (BrowserEntry Entry, TextBlock Label)> _cellMenuTargets = [];
    private MenuItem? _pasteItem;
    private bool _disposed;

    // 拖出状态
    private Point _dragStart;
    private string? _dragPath;
    private bool _dragPossible;

    // 拖动重排状态（自由布局）
    private bool _dragMoving;
    private double _itemOriginX;
    private double _itemOriginY;
    private FrameworkElement? _draggingCell;

    // 批量拖动：框选（或多选）后按下其中一个图标拖动 → 整个选中集一起移动。
    // _dragCells 与 _dragOrigins 一一对应，保持集合内图标相对位置不变。
    private readonly List<FrameworkElement> _dragCells = new();
    private readonly List<(double X, double Y)> _dragOrigins = new();

    // 落点预览指示器（拖动时高亮显示松手后会落在哪个格，便于确认松手效果）
    private Border? _dropIndicator;

    // ===== 框选（橡皮筋多选） =====
    private bool _rubberActive;
    private bool _rubberCtrl;              // 按下 Ctrl → 框选结果追加到原选中
    private Point _rubberStart;
    private Border? _rubberBand;           // 橡皮筋视觉矩形
    private string[] _rubberBaseSelection = Array.Empty<string>();

    // ===== 拖动让位的临时状态（未松手前只是预览，不落盘） =====
    // _basePositions：拖动开始时的基准位置快照；移开/取消时整体回滚到这里
    // _livePositions：当前"逻辑位置"（含临时让位）——判定占位与最终提交都以此为准
    //   ⚠️ 不能用 Canvas.GetLeft 判定：WPF 动画只覆盖呈现值、不改属性基值，
    //      动画期间 GetLeft 返回的仍是旧值，会导致重叠误判与"图标堆叠"。
    private Dictionary<FrameworkElement, (double X, double Y)>? _basePositions;
    private readonly Dictionary<FrameworkElement, (double X, double Y)> _livePositions = new();

    // 上一次让位时被拖集合的目标格（批量拖动 = 多个目标格），用于变化检测避免每帧重算
    private List<(int Col, int Row)>? _lastAvoidTargets;

    // ===== 互斥占位表：位置量化为格索引，"一格至多一个图标" =====
    // 这是杜绝堆叠的根本手段：任何落点都必须先在表里确认是空格才能放，
    // 而不是像早期版本那样"算个偏移就移过去"，导致直接叠在别人身上。
    private readonly Dictionary<(int Col, int Row), FrameworkElement> _occupancy = new();

    // 内联重命名状态
    private string? _editingPath;

    public DesktopIconsControl(
        IDesktopBrowser browser,
        ISettingsService? settings = null,
        IMenuService? menus = null,
        IFileClassifier? classifier = null)
    {
        _browser = browser;
        _settings = settings;
        _menus = menus;
        _classifier = classifier;
        // 回退开关：context-menu.migrated=false 走旧自绘路径；菜单服务缺失同样自动回退。
        _useMenuService = menus is not null && (settings?.Get("context-menu.migrated", true) ?? true);
        Background = Brushes.Transparent; // 空白处点击穿透到桌面窗口（右键/框选由窗口层接）
        // 对齐 cairoshell DesktopFolderViewStyle：横向滚动（纵向禁用），先填满一列再横向开新列
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(7, 13, 0, 0); // cairoshell DesktopIcons.setPosition 同值

        // 拖入支持：外部文件/文件夹拖到桌面 → 复制/移动进当前浏览目录（始终启用）
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        if (_useMenuService)
        {
            // 新路径：统一菜单服务（模板 + 贡献项 + 能力过滤）。旧自绘菜单不预赋，右键经路由弹出。
            _menuHandles.Add(_menus!.RegisterTemplate(new DesktopBlankTemplate(this)));
            _menuHandles.Add(_menus.RegisterTemplate(new DesktopIconTemplate(this)));
            MouseRightButtonUp += OnMenuServiceMouseUp;
        }
        else
        {
            ContextMenu = BuildBlankMenu();
        }

        _browser.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(Rebuild);

        // 恢复持久化排序（desktop.sortKey；菜单改排序时同步写此键；空串=默认）
        if (settings is not null)
        {
            _browser.SetSort(settings.Get("desktop.sortKey", string.Empty));
        }

        // 图标大小/间距变化 → 网格格子变了，旧坐标会出现空洞 → 自动紧凑重排补位。
        // BeginInvoke 到 Background 优先级：等 Rebuild/布局完成后再整理，避免对旧 canvas 操作。
        if (settings is not null)
        {
            settings.Changed += (_, e) =>
            {
                if (e.Key is "desktop.iconSize" or "desktop.itemSpacingX" or "desktop.itemSpacingY")
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, CompactLayout);
                }
            };
        }

        // 首次加载必须显式触发：DesktopBrowser 构造只设 Location 不枚举，
        // 不调 Refresh 则 Items 永远为空（此前"桌面无图标"的根因）。
        _browser.Refresh();
    }

    /// <summary>
    /// 空白处双击（图标之外的自由布局空白区）。供 DesktopWindow 实现「双击切换隐藏桌面图标」。
    /// 图标自身的双击（打开文件）在 cell 层已处理并标记 Handled，不会触发本事件。
    /// </summary>
    public event EventHandler? BlankAreaDoubleClick;

    // ======== 桌面设置（与设置中心「桌面」分区同键同默认；默认值 = 2026-09-01 用户实测调优值） ========

    private double IconSize => Clamp(_settings?.Get("desktop.iconSize", 31d) ?? 31d, 24, 96);

    private double SpacingX => Clamp(_settings?.Get("desktop.itemSpacingX", 9d) ?? 9d, 0, 48);

    private double SpacingY => Clamp(_settings?.Get("desktop.itemSpacingY", 9d) ?? 9d, 0, 48);

    // 格尺寸由「图标大小 + 行/列间距」自动推导：调间距时网格整体适配，不在桌面下方留空白。
    // 默认 44 + 12 + 30 = 86（宽）/ 44 + 12 + 36 = 92（高），与历史默认布局一致。
    private double CellWidth => IconSize + SpacingX + 30;

    private double CellHeight => IconSize + SpacingY + 36;

    private double LabelFontSize => Clamp(_settings?.Get("desktop.labelFontSize", 9d) ?? 9d, 8, 18);

    private bool HideLnkExtension => _settings?.Get("desktop.hideLnkExtension", true) ?? true;

    /// <summary>自动排列：true（默认）=瀑布列自动排列（不可拖动重排，拖动=拖出文件）；
    /// false=自由布局，可拖动图标改变位置（拖拽重排/框选/让位在该模式下生效）。</summary>
    private bool AutoArrange => _settings?.Get("desktop.autoArrange", true) ?? true;

    /// <summary>对齐网格：自由布局下拖动松手后吸附到网格（默认 true）。</summary>
    private bool SnapToGrid => _settings?.Get("desktop.snapToGrid", true) ?? true;

    // ======== 图标位置持久化（自由布局） ========
    // 存 desktop.iconPositions：{ 完整路径: [x, y] }。SettingsService 走 System.Text.Json，支持该结构。

    private Dictionary<string, double[]> LoadPositions()
        => _settings?.Get<Dictionary<string, double[]>>("desktop.iconPositions", null)
           ?? new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

    // 位置写入统一由 CommitLayout 在松手时一次性落盘（见下方拖动逻辑）。
    // 重置由设置分区直接写入空坐标表（settings.Set("desktop.iconPositions", 空字典)），
    // 触发 Changed → 本控件重排并自动分配初始网格位置，故此处无需额外方法。

    /// <summary>条目显示名：按设置决定是否剥 .lnk 后缀（explorer 桌面惯例）。</summary>
    private string DisplayNameOf(BrowserEntry entry) => HideLnkExtension ? entry.DisplayName : entry.Name;

    private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    /// <summary>重建图标网格（当前 Location 的条目）。按设置走自动排列或自由布局。</summary>
    public void Rebuild()
    {
        if (_disposed) return;

        // 竖向滚动条：自动排列是"填满一列再换列"（无需竖向滚动）；
        // 自由布局图标可摆到任意 Y（含视口下方），故需要竖向滚动。
        VerticalScrollBarVisibility = AutoArrange ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        if (AutoArrange)
        {
            RebuildAutoArrange();
        }
        else
        {
            RebuildFreeLayout();
        }
    }

    /// <summary>自动排列：WrapPanel 竖向瀑布列（先填一列再开新列），不可拖动重排。</summary>
    private void RebuildAutoArrange()
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Vertical,
            ItemWidth = CellWidth,
            ItemHeight = CellHeight
        };

        foreach (var entry in _browser.Items)
        {
            panel.Children.Add(CreateItem(entry));
        }

        Content = panel;
    }

    /// <summary>
    /// 自由布局：Canvas + 每个图标保存的坐标（desktop.iconPositions）；
    /// 无保存坐标的条目按「列优先」网格自动分配初始位置（列满换列）。
    /// 用户可拖动图标任意摆放，松手保存（可选吸附网格）。
    /// </summary>
    private void RebuildFreeLayout()
    {
        var canvas = new Canvas { Background = Brushes.Transparent };

        // ⚠️ 重建会丢弃旧 cell 并新建一批：拖动期间若发生重建（如文件变化触发刷新），
        //    临时状态里持有的旧 FrameworkElement 会全部失效，再拿它判定/让位就会错乱
        //    甚至把新图标推到同一格造成堆叠。这里先清理，让后续拖动重新取快照。
        ClearDragState();

        var positions = LoadPositions();

        // 落点预览指示器：拖动时高亮目标格（松手后会落到这里）
        _dropIndicator = new Border
        {
            Width = CellWidth - 6,
            Height = CellHeight - 6,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 0x4C, 0x9A, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(32, 0x4C, 0x9A, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_dropIndicator, 0);
        canvas.Children.Add(_dropIndicator);

        // ===== 框选（橡皮筋多选）：只在自由布局 Canvas 上启用 =====
        // 橡皮筋矩形置于最顶层；命中测试按 Canvas 坐标做矩形相交。
        _rubberBand = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0x4C, 0x9A, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(38, 0x4C, 0x9A, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_rubberBand, 3000);
        canvas.Children.Add(_rubberBand);

        // 空白处按下 → 开始框选（图标上的按下由 cell 自己处理并标记 Handled，不会冒泡到这里）
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            // 空白处双击 → 切换「隐藏桌面图标」（DesktopWindow 订阅 BlankAreaDoubleClick）。
            // 双击的第一次按下已启动框选并捕获鼠标，必须先撤干净，否则残留的 rubber band
            // 会跟着双击闪一下、捕获的鼠标也影响后续交互。
            if (e.ClickCount >= 2)
            {
                _rubberActive = false;
                canvas.ReleaseMouseCapture();
                if (_rubberBand is not null)
                {
                    _rubberBand.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
                BlankAreaDoubleClick?.Invoke(this, EventArgs.Empty);
                return;
            }

            _rubberActive = true;
            _rubberCtrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            _rubberStart = e.GetPosition(canvas);
            _rubberBaseSelection = _rubberCtrl
                ? _browser.SelectedPaths.ToArray()
                : Array.Empty<string>();
            Canvas.SetLeft(_rubberBand, _rubberStart.X);
            Canvas.SetTop(_rubberBand, _rubberStart.Y);
            _rubberBand.Width = 0;
            _rubberBand.Height = 0;
            _rubberBand.Visibility = Visibility.Visible;
            canvas.CaptureMouse(); // 拖出可视区/快速甩动也持续收到 Move/Up
            e.Handled = true;
        };

        canvas.MouseMove += (_, e) =>
        {
            if (!_rubberActive || _rubberBand is null)
            {
                return;
            }

            var cur = e.GetPosition(canvas);
            var rect = new Rect(_rubberStart, cur);
            Canvas.SetLeft(_rubberBand, rect.X);
            Canvas.SetTop(_rubberBand, rect.Y);
            _rubberBand.Width = rect.Width;
            _rubberBand.Height = rect.Height;

            // 实时高亮命中的图标（矩形相交）
            var hit = new List<string>();
            foreach (var child in canvas.Children)
            {
                if (child is not Border fe ||
                    ReferenceEquals(fe, _rubberBand) ||
                    ReferenceEquals(fe, _dropIndicator) ||
                    fe.Tag is not string path)
                {
                    continue;
                }

                if (GetCellRect(fe).IntersectsWith(rect))
                {
                    hit.Add(path);
                }
            }

            if (_rubberCtrl)
            {
                var set = new HashSet<string>(_rubberBaseSelection, StringComparer.OrdinalIgnoreCase);
                set.UnionWith(hit);
                _browser.SetSelection(set);
            }
            else
            {
                _browser.SetSelection(hit);
            }
        };

        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!_rubberActive)
            {
                return;
            }

            _rubberActive = false;
            canvas.ReleaseMouseCapture();
            if (_rubberBand is not null)
            {
                _rubberBand.Visibility = Visibility.Collapsed;
            }

            // 几乎没拖动 = 纯点击空白：非 Ctrl 时清空选中（explorer 惯例）
            var cur = e.GetPosition(canvas);
            if (!_rubberCtrl &&
                Math.Abs(cur.X - _rubberStart.X) < 3 &&
                Math.Abs(cur.Y - _rubberStart.Y) < 3)
            {
                _browser.SetSelection(Array.Empty<string>());
            }
        };

        // 每列容量：按可用高度（ScrollViewer 实际高度；首帧未布局时回退主屏工作区高度）
        var available = ActualHeight > 0
            ? ActualHeight
            : SystemParameters.WorkArea.Height - 80;
        var rowsPerColumn = Math.Max(1, (int)Math.Floor(available / CellHeight));

        var index = 0;
        var maxRight = CellWidth;
        var maxBottom = CellHeight;
        foreach (var entry in _browser.Items)
        {
            var cell = CreateItem(entry);

            double x, y;
            if (positions.TryGetValue(entry.Path, out var saved) && saved is { Length: >= 2 })
            {
                // 存量坐标规整：历史版本的重叠消解可能把图标推到几千像素深的纵向位置
                // （远超屏幕）。纵向不允许超出列容量——把溢出的行折算进后续列，
                // 保持相对次序（横向扩展），启动时即自动修复存量烂布局。
                var col = (int)Math.Round(saved[0] / CellWidth);
                var row = (int)Math.Round(saved[1] / CellHeight);
                if (row >= rowsPerColumn)
                {
                    var linear = Math.Max(0, col) * rowsPerColumn + row;
                    col = linear / rowsPerColumn;
                    row = linear % rowsPerColumn;
                }

                x = col * CellWidth;
                y = row * CellHeight;
            }
            else
            {
                var col = index / rowsPerColumn;
                var row = index % rowsPerColumn;
                x = col * CellWidth;
                y = row * CellHeight;
            }

            x = Math.Max(0, x);
            y = Math.Max(0, y);
            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            canvas.Children.Add(cell);
            index++;

            maxRight = Math.Max(maxRight, x + CellWidth);
            maxBottom = Math.Max(maxBottom, y + CellHeight);
        }

        // Canvas 的 DesiredSize 恒为 0（不参与子元素尺寸计算），必须显式给出尺寸，
        // 否则外层 ScrollViewer 认为无内容可滚动，摆在下方的图标会看不见。
        canvas.Width = maxRight;
        canvas.Height = maxBottom;

        Content = canvas;
    }

    private FrameworkElement CreateItem(BrowserEntry entry)
    {
        var icon = new Border
        {
            // 图标尺寸由「图标大小」设置驱动（desktop.iconSize）
            Width = IconSize,
            Height = IconSize,
            Child = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true }
        };
        var label = new TextBlock
        {
            // 显示名按设置决定是否剥 .lnk（explorer 桌面惯例）；ToolTip 始终保留完整名
            Text = DisplayNameOf(entry),
            FontSize = LabelFontSize,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            // 图标文字加投影保证亮暗壁纸上都可读
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = 0.8
            }
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        var cell = new Border
        {
            Width = CellWidth - 6,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 4, 2, 4),
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = entry.Name,
            // 拖动避让时用于回写被挤开图标的坐标（存完整路径）
            Tag = entry.Path
        };

        // 图标真实化：目录与文件统一 IconHelper 提取；目录失败回退自绘 glyph；
        // shell 虚拟项（::CLSID）IconHelper 按文件路径取必失败，改走 shell PIDL 提取（同步，项少开销可忽略）。
        if (icon.Child is Image img)
        {
            if (entry.IsShellNamespace)
            {
                img.Source = ShellNamespaceHelper.GetIcon(entry.Path);
            }
            else
            {
                _ = LoadFileIconAsync(entry.Path, entry.IsDirectory, img, icon);
            }
        }

        // 选中态跟随
        SyncSelectionVisual(cell, entry.Path);
        _browser.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncSelectionVisual(cell, entry.Path));

        // 单击选中 / 右键选中（右键让当前项入选中集，explorer 同款）
        // 同时记录拖出起点：左键按下且移动超过阈值 → 进入拖出（DoDragDrop）。
        // CaptureMouse 保证鼠标移出图标单元格仍能收到 MouseMove/Up（拖出判定不丢）。
        cell.MouseLeftButtonDown += (_, e) =>
        {
            SelectForClick(entry.Path, extend: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            _dragStart = e.GetPosition(this);
            _dragPath = entry.Path;
            _itemOriginX = Canvas.GetLeft(cell);
            _itemOriginY = Canvas.GetTop(cell);
            _dragPossible = true;
            _dragMoving = false;
            _draggingCell = cell;

            // 批量拖动：按下时该图标已在多选集合里（框选/Ctrl 点选的结果）→ 整个选中集一起拖；
            // 仅自由布局（Canvas 父容器）启用重排式批量拖动，否则只拖当前这一个
            _dragCells.Clear();
            _dragOrigins.Clear();
            if (cell.Parent is Canvas canvasParent &&
                _browser.SelectedPaths.Count > 1 &&
                _browser.SelectedPaths.Contains(entry.Path))
            {
                foreach (var child in canvasParent.Children)
                {
                    if (child is Border fe && fe.Tag is string p && _browser.SelectedPaths.Contains(p))
                    {
                        _dragCells.Add(fe);
                        _dragOrigins.Add((Canvas.GetLeft(fe), Canvas.GetTop(fe)));
                    }
                }
            }

            if (_dragCells.Count == 0)
            {
                _dragCells.Add(cell);
                _dragOrigins.Add((_itemOriginX, _itemOriginY));
            }

            cell.CaptureMouse();
            e.Handled = true;
        };
        cell.MouseRightButtonDown += (_, _) =>
        {
            if (!_browser.SelectedPaths.Contains(entry.Path))
            {
                _browser.SetSelection(new[] { entry.Path });
            }
        };

        // 双击：目录导航 / 文件启动
        cell.InputBindings.Clear();
        var dbl = new MouseBinding(
            new RelayCommand(() => Open(entry)),
            new MouseGesture(MouseAction.LeftDoubleClick));
        cell.InputBindings.Add(dbl);

        // ===== 拖动语义按布局模式分流 =====
        //   自由布局（默认）  → 桌面内重排：实时跟随鼠标 = 拖动动画，松手保存位置
        //   自动排列          → 不可重排：拖动 = 把文件拖出到资源管理器等（OLE FileDrop）
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragMoving && ReferenceEquals(_draggingCell, cell))
            {
                // 主 cell 吸附落位；把吸附偏移量应用到整个被拖集合，保持相对位置不变
                var cur = _livePositions.TryGetValue(cell, out var cp)
                    ? cp
                    : (X: Canvas.GetLeft(cell), Y: Canvas.GetTop(cell));
                var (snappedX, snappedY) = SnapTarget(cur.X, cur.Y);
                var offX = Math.Max(0, snappedX) - cur.X;
                var offY = Math.Max(0, snappedY) - cur.Y;

                var drops = new List<(FrameworkElement Cell, double X, double Y)>();
                foreach (var fe in _dragCells)
                {
                    var p = _livePositions.TryGetValue(fe, out var pp)
                        ? pp
                        : (X: Canvas.GetLeft(fe), Y: Canvas.GetTop(fe));
                    var fx = Math.Max(0, p.X + offX);
                    var fy = Math.Max(0, p.Y + offY);
                    Canvas.SetLeft(fe, fx);
                    Canvas.SetTop(fe, fy);
                    _livePositions[fe] = (fx, fy);
                    drops.Add((fe, fx, fy));
                }

                // 松手才提交：所有被拖图标落点 + 所有被让位图标的最终位置（一次性落盘）
                CommitLayout(drops);
                HideDropIndicator();
                UpdateCanvasExtent();
                foreach (var fe in _dragCells)
                {
                    EndDragVisual(fe);
                }
            }
            else
            {
                HideDropIndicator();
            }

            // 未落位（点击或取消）时：让位只是预览，随拖动状态清理即恢复基准排布
            ClearDragState();

            _dragMoving = false;
            _dragPossible = false;
            _dragPath = null;
            _draggingCell = null;
            cell.ReleaseMouseCapture();
        };

        if (AutoArrange)
        {
            // 自动排列：拖动 = 拖出文件（拖到资源管理器/其他应用复制或移动）
            cell.MouseMove += (_, e) =>
            {
                if (!_dragPossible || _dragPath is null || e.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                var pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _dragStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _dragPossible = false;
                try
                {
                    // 批量拖出：按下时该图标在多选集合里 → 把整个选中集一起拖出
                    var paths = (_browser.SelectedPaths.Count > 1 && _browser.SelectedPaths.Contains(_dragPath))
                        ? _browser.SelectedPaths.ToArray()
                        : new[] { _dragPath };
                    var data = new DataObject(DataFormats.FileDrop, paths);
                    DragDrop.DoDragDrop(cell, data, DragDropEffects.Copy | DragDropEffects.Move);
                }
                catch
                {
                    // 拖出失败静默（M10）
                }
            };
        }
        else
        {
            // 自由布局：拖动 = 改变图标位置（实时更新 Canvas 坐标 = 跟随动画）
            cell.MouseMove += (_, e) =>
            {
                if (!_dragPossible || e.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                if (!ReferenceEquals(_draggingCell, cell))
                {
                    return;
                }

                var pos = e.GetPosition(this);
                var dx = pos.X - _dragStart.X;
                var dy = pos.Y - _dragStart.Y;

                if (!_dragMoving)
                {
                    // 3px 阈值：区分"点击选中"与"拖动重排"
                    if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
                    {
                        return;
                    }

                    _dragMoving = true;
                    // 记录基准快照：让位全程只是预览，移开即回滚到这里
                    CaptureLayoutSnapshot();
                    foreach (var fe in _dragCells)
                    {
                        BeginDragVisual(fe);
                    }
                }

                // 批量跟随：整个被拖集合保持相对位置一起移动
                var nx = Math.Max(0, _itemOriginX + dx);
                var ny = Math.Max(0, _itemOriginY + dy);
                for (var i = 0; i < _dragCells.Count; i++)
                {
                    var fe = _dragCells[i];
                    var (ox, oy) = _dragOrigins[i];
                    var fx = Math.Max(0, ox + dx);
                    var fy = Math.Max(0, oy + dy);
                    Canvas.SetLeft(fe, fx);
                    Canvas.SetTop(fe, fy);
                    // 被拖图标自身的逻辑位置也要同步（让位判定会读它）
                    _livePositions[fe] = (fx, fy);
                }

                // 落点预览：按当前吸附规则算出主拖图标松手后的目标格并高亮
                var (tx, ty) = SnapTarget(nx, ny);
                ShowDropIndicator(tx, ty);
                // 临时让位：目标格上的图标往下推（未松手，不落盘）
                ApplyAvoidance();

                // ⚠️ 拖动会扩大内容范围：必须同步更新 Canvas 尺寸，
                //    否则 ScrollViewer 仍按旧内容范围裁剪，拖到右侧/下方的图标会被截断不显示。
                UpdateCanvasExtent();
            };
        }

        // 图标右键菜单（MENU-SPECS §2 实用子集，自绘主题呈现）：
        // 新路径记录 cell→entry 映射，右键统一走 MouseRightButtonUp → IMenuService。
        if (_useMenuService)
        {
            _cellMenuTargets[cell] = (entry, label);
        }
        else
        {
            cell.ContextMenu = BuildIconMenu(entry, cell, label);
        }

        return cell;
    }

    /// <summary>图标单元格在 Canvas 坐标系下的矩形（框选命中测试用）。</summary>
    private Rect GetCellRect(FrameworkElement fe)
    {
        var w = fe.ActualWidth > 0 ? fe.ActualWidth : CellWidth - 6;
        var h = fe.ActualHeight > 0 ? fe.ActualHeight : CellHeight - 8;
        return new Rect(Canvas.GetLeft(fe), Canvas.GetTop(fe), w, h);
    }

    /// <summary>按当前吸附设置，把坐标换算成"松手后会落到的目标格"左上角。</summary>
    private (double X, double Y) SnapTarget(double x, double y)
    {
        if (!SnapToGrid)
        {
            return (Math.Max(0, x), Math.Max(0, y));
        }

        return (Math.Max(0, Math.Round(x / CellWidth) * CellWidth),
                Math.Max(0, Math.Round(y / CellHeight) * CellHeight));
    }

    /// <summary>显示落点预览高亮（让用户在松手前就能确认会落到哪一格）。</summary>
    private void ShowDropIndicator(double x, double y)
    {
        if (_dropIndicator is null)
        {
            return;
        }

        Canvas.SetLeft(_dropIndicator, x + 3);
        Canvas.SetTop(_dropIndicator, y + 3);
        _dropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator is not null)
        {
            _dropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 记录拖动开始时的基准位置快照，并以此初始化逻辑位置表。
    /// 快照是"移开即回滚"的依据——未松手前所有让位都只是预览。
    /// </summary>
    private void CaptureLayoutSnapshot()
    {
        _basePositions = new Dictionary<FrameworkElement, (double X, double Y)>();
        _livePositions.Clear();
        _lastAvoidTargets = null;

        if (Content is not Canvas canvas)
        {
            return;
        }

        foreach (var child in canvas.Children)
        {
            if (child is not FrameworkElement fe || ReferenceEquals(fe, _dropIndicator))
            {
                continue;
            }

            var pos = (Canvas.GetLeft(fe), Canvas.GetTop(fe));
            _basePositions[fe] = pos;
            _livePositions[fe] = pos;
        }
    }

    /// <summary>把所有被让位的图标回滚到基准位置（被拖集合不回滚，它们跟随鼠标）。</summary>
    private void RestoreBasePositions(ISet<FrameworkElement> draggedSet)
    {
        if (_basePositions is null)
        {
            return;
        }

        foreach (var kv in _basePositions)
        {
            if (draggedSet.Contains(kv.Key))
            {
                continue;
            }

            var fe = kv.Key;
            var cur = _livePositions.TryGetValue(fe, out var p)
                ? p
                : (X: Canvas.GetLeft(fe), Y: Canvas.GetTop(fe));
            _livePositions[fe] = kv.Value;

            // 位置没变就不必重起动画：否则每次目标格变化都给全部图标刷新动画，既浪费又抖
            if (Math.Abs(cur.X - kv.Value.X) < 1 && Math.Abs(cur.Y - kv.Value.Y) < 1)
            {
                continue;
            }

            AnimateTo(fe, kv.Value.X, kv.Value.Y);
        }
    }

    // ===== 格索引 ↔ 像素坐标 =====
    // 量化为格索引后，半格以内的偏差会被归到同一格，
    // 避免"差几像素就判成不冲突"的漏判（这正是早期版本堆叠的根源）。

    // 注意：CellWidth/CellHeight 是实例依赖属性，因此这些辅助方法不能用 static。

    private (int Col, int Row) ToCell(double x, double y) =>
        ((int)Math.Round(x / CellWidth), (int)Math.Round(y / CellHeight));

    private (double X, double Y) FromCell(int col, int row) =>
        (col * CellWidth, row * CellHeight);

    /// <summary>
    /// 按当前逻辑位置重建占位表（排除整个被拖集合）。
    /// 一格只认第一个登记的图标（互斥）；重复占位者交给 ResolveOverlaps 消解。
    /// </summary>
    private void RebuildOccupancy(ISet<FrameworkElement> exclude)
    {
        _occupancy.Clear();
        foreach (var kv in _livePositions)
        {
            if (exclude.Contains(kv.Key))
            {
                continue;
            }

            var idx = ToCell(kv.Value.X, kv.Value.Y);
            _occupancy.TryAdd(idx, kv.Key);
        }
    }

    /// <summary>
    /// 列容量（一列最多摆几个图标）：受屏幕/视口高度限制。
    /// ⚠️ 让位与冲突消解必须遵守此上限——向下不许无限扩展；
    /// 列满后向右溢出到新列（横向扩展到屏幕极限由 64 列上限兜底）。
    /// </summary>
    private int ColumnCapacity => Math.Max(1, (int)Math.Floor(
        (ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height - 80) / CellHeight));

    /// <summary>
    /// 找第一个空格：先在本列从 startRow 往下找（不超过列容量），
    /// 本列放不下则**向右开新列**从第一行找（绝不下探到屏幕之外）。
    /// </summary>
    /// <param name="extraOccupied">额外视为占用的格（如 ResolveOverlaps 的本地占用表）。</param>
    private (int Col, int Row)? FindFreeSlot(
        int col, int startRow, ISet<(int Col, int Row)>? extraOccupied = null)
    {
        bool IsFree(int c, int r) =>
            !_occupancy.ContainsKey((c, r)) && extraOccupied?.Contains((c, r)) != true;

        var cap = ColumnCapacity;

        for (var row = startRow; row < cap; row++)
        {
            if (IsFree(col, row))
            {
                return (col, row);
            }
        }

        // 本列已满 → 向右找新列（上限 64 列，防异常数据死循环）
        for (var c = col + 1; c < col + 64; c++)
        {
            for (var row = 0; row < cap; row++)
            {
                if (IsFree(c, row))
                {
                    return (c, row);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 临时让位（不落盘）：每个被拖图标的目标格若坐着非被拖图标，把它**连同下方连续的
    /// 图标一起往下推一格**。落点由占位表算出"往下第一个真正的空格"，因此绝不会推到
    /// 别人身上造成堆叠。每次目标格变化都先回滚到基准再重算，移开时让位自然恢复。
    /// 批量拖动 = 多个目标格，按"从上到下"依次下推。
    /// </summary>
    private void ApplyAvoidance()
    {
        if (_dragCells.Count == 0)
        {
            return;
        }

        var draggedSet = new HashSet<FrameworkElement>(_dragCells);

        // 计算每个被拖图标的吸附目标格（基准快照含被拖集合外的全部图标）
        var targets = new List<(int Col, int Row)>(_dragCells.Count);
        foreach (var fe in _dragCells)
        {
            var (fx, fy) = _livePositions.TryGetValue(fe, out var p)
                ? p
                : (X: Canvas.GetLeft(fe), Y: Canvas.GetTop(fe));
            var (sx, sy) = SnapTarget(fx, fy);
            targets.Add(ToCell(sx, sy));
        }

        // 目标格未变化则跳过，避免每帧回滚+重算造成抖动
        if (_lastAvoidTargets is not null &&
            _lastAvoidTargets.Count == targets.Count &&
            _lastAvoidTargets.SequenceEqual(targets))
        {
            return;
        }

        _lastAvoidTargets = targets;

        // 1) 先撤销上一次让位（回到拖动开始时的排布）
        RestoreBasePositions(draggedSet);

        // 2) 按格索引重建占位表（互斥判定的依据；被拖集合全部排除）
        RebuildOccupancy(draggedSet);

        // 3) 对每个目标格（从上到下、去重）执行下推让位
        foreach (var t in targets
                     .OrderBy(t => t.Row)
                     .ThenBy(t => t.Col)
                     .Distinct())
        {
            // 目标格空 → 无需让位
            if (!_occupancy.ContainsKey(t))
            {
                continue;
            }

            // 往下找第一个真正的空格；本列放不下则溢出到新列（纵向不无限扩展）
            var slot = FindFreeSlot(t.Col, t.Row + 1);
            if (slot is null)
            {
                continue;
            }

            var (sc, sr) = slot.Value;
            var cap = ColumnCapacity;

            // 倒序下推（从最下方开始，避免相互踩踏）；每个图标都落到空出来的格
            var lastRow = sc == t.Col ? sr - 1 : cap - 1;
            for (var row = lastRow; row >= t.Row; row--)
            {
                if (!_occupancy.TryGetValue((t.Col, row), out var fe))
                {
                    continue;
                }

                // 本列内依次下移一格；列尾的溢出到新列空格
                var target = row + 1 < cap
                    ? (Col: t.Col, Row: row + 1)
                    : (Col: sc, Row: sr);
                _occupancy.Remove((t.Col, row));
                _occupancy[target] = fe;

                var (nx, ny) = FromCell(target.Col, target.Row);
                _livePositions[fe] = (nx, ny);
                AnimateTo(fe, nx, ny);
            }
        }
    }

    /// <summary>
    /// 提交前的最终防线：消解所有重叠，保证**一格至多一个图标**。
    /// 按稳定顺序遍历（被拖图标优先占位 → 再按 Y、X），先到先占，
    /// 冲突者往下找最近的空格。即使中途动画或外部历史数据有偏差，
    /// 落盘的坐标也一定是互斥的——这是"绝不堆叠"的兜底保证。
    /// </summary>
    private void ResolveOverlaps(ISet<FrameworkElement> draggedSet)
    {
        var ordered = _livePositions
            // 被拖图标的落点是用户明确意图，优先保留在自己的格
            .OrderBy(kv => draggedSet.Contains(kv.Key) ? 0 : 1)
            .ThenBy(kv => kv.Value.Y)
            .ThenBy(kv => kv.Value.X)
            .ToList();

        var occupied = new HashSet<(int, int)>();
        foreach (var kv in ordered)
        {
            var fe = kv.Key;
            var idx = ToCell(kv.Value.X, kv.Value.Y);

            if (occupied.Add(idx))
            {
                continue; // 该格空闲，占位成功
            }

            // 冲突：找最近的空格；本列放不下则向右溢出新列（纵向不无限扩展）
            var slot = FindFreeSlot(idx.Col, idx.Row + 1, occupied);
            if (slot is null)
            {
                continue; // 64 列内已无处可放（极端异常数据），保持原格
            }

            idx = slot.Value;
            occupied.Add(idx);

            var (nx, ny) = FromCell(idx.Col, idx.Row);
            _livePositions[fe] = (nx, ny);
            AnimateTo(fe, nx, ny);
        }
    }

    /// <summary>松手时一次性提交：全部被拖图标落点 + 所有被让位图标的最终位置。</summary>
    private void CommitLayout(IReadOnlyList<(FrameworkElement Cell, double X, double Y)> drops)
    {
        if (_settings is null)
        {
            return;
        }

        var draggedSet = new HashSet<FrameworkElement>();
        foreach (var d in drops)
        {
            draggedSet.Add(d.Cell);
        }

        // 互斥最终防线：先消解重叠，再落盘，保证存档里也是"一格一个"
        ResolveOverlaps(draggedSet);

        var all = LoadPositions();

        // 被拖集合（ResolveOverlaps 优先让它们占位，位置以消解后的逻辑位置为准）
        foreach (var d in drops)
        {
            if (_livePositions.TryGetValue(d.Cell, out var p) && d.Cell.Tag is string draggedPath)
            {
                all[draggedPath] = new[] { p.X, p.Y };
            }
        }

        // 其余图标（含被让位者）按逻辑位置落盘
        foreach (var kv in _livePositions)
        {
            if (draggedSet.Contains(kv.Key) || kv.Key.Tag is not string path)
            {
                continue;
            }

            all[path] = new[] { kv.Value.X, kv.Value.Y };
        }

        _settings.Set("desktop.iconPositions", all);
    }

    /// <summary>
    /// 紧凑重排（"整理图标"）：按列优先顺序把所有图标重新填入网格。
    /// 调整图标大小/间距等设置后留下的空洞由后续图标补上；
    /// 排序依据 = 图标当前坐标的（列, 行）次序，保持"从左到右、从上到下"的相对排列感。
    /// 落盘前先做重叠消解，保证紧凑结果互斥。
    /// </summary>
    private void CompactLayout()
    {
        if (Content is not Canvas canvas || _settings is null || _disposed)
        {
            return;
        }

        var cells = new List<FrameworkElement>();
        foreach (var child in canvas.Children)
        {
            if (child is FrameworkElement fe &&
                !ReferenceEquals(fe, _dropIndicator) &&
                fe.Tag is string)
            {
                cells.Add(fe);
            }
        }

        if (cells.Count == 0)
        {
            return;
        }

        var ordered = cells
            .Select(fe => (fe, idx: ToCell(Canvas.GetLeft(fe), Canvas.GetTop(fe))))
            .OrderBy(t => t.idx.Col)
            .ThenBy(t => t.idx.Row)
            .ToList();

        var cap = ColumnCapacity;
        var all = LoadPositions();
        var slotIndex = 0;

        // 应用新位置：线性序号 → (列, 行)，动画过渡 + 逻辑位置/落盘同步
        foreach (var (fe, _) in ordered)
        {
            var col = slotIndex / cap;
            var row = slotIndex % cap;
            slotIndex++;

            var (nx, ny) = FromCell(col, row);
            Canvas.SetLeft(fe, nx);
            Canvas.SetTop(fe, ny);
            _livePositions[fe] = (nx, ny);
            AnimateTo(fe, nx, ny);
            if (fe.Tag is string path)
            {
                all[path] = new[] { nx, ny };
            }
        }

        UpdateCanvasExtent();
        _settings.Set("desktop.iconPositions", all);
    }

    /// <summary>清理拖动临时状态。</summary>
    private void ClearDragState()
    {
        _basePositions = null;
        _livePositions.Clear();
        _occupancy.Clear();
        _lastAvoidTargets = null;
        _dragCells.Clear();
        _dragOrigins.Clear();
    }

    /// <summary>平滑位移动画（用于避让）：动画 Canvas.Left/Top 两个附加属性。</summary>
    private static void AnimateTo(FrameworkElement element, double x, double y)
    {
        var curX = (double)element.GetValue(Canvas.LeftProperty);
        var curY = (double)element.GetValue(Canvas.TopProperty);

        // 基值立即指向目标值：让 Canvas.GetLeft/GetTop 与逻辑位置始终一致
        element.SetValue(Canvas.LeftProperty, x);
        element.SetValue(Canvas.TopProperty, y);

        var duration = TimeSpan.FromMilliseconds(160);
        var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

        // ⚠️ 必须用 FillBehavior.Stop：
        //    DoubleAnimation 默认 HoldEnd，动画结束后会**永久锁定** Canvas.Left/Top，
        //    此后 Canvas.SetLeft 完全失效 —— 表现为"被让位过的图标再次拖动时不跟手、
        //    像消失了一样"。改成 Stop 后动画结束即解除锁定，露出上面的基值(=目标值)，
        //    既无跳变也不残留锁定。
        var animX = new System.Windows.Media.Animation.DoubleAnimation(curX, x, duration)
        { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        var animY = new System.Windows.Media.Animation.DoubleAnimation(curY, y, duration)
        { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };

        element.BeginAnimation(Canvas.LeftProperty, animX);
        element.BeginAnimation(Canvas.TopProperty, animY);
    }

    /// <summary>
    /// 按当前所有图标的实际占位重算 Canvas 尺寸。
    /// 必须在拖动中与落位后调用：Canvas 尺寸决定 ScrollViewer 的可滚动范围，
    /// 不更新则拖到右侧/下方的图标会被裁剪而看不见。
    /// </summary>
    private void UpdateCanvasExtent()
    {
        if (Content is not Canvas canvas)
        {
            return;
        }

        var maxRight = CellWidth;
        var maxBottom = CellHeight;

        // 拖动中优先用逻辑位置：让位是靠动画实现的，动画期间 Canvas.GetLeft/GetTop
        // 返回的仍是基值，读它会把内容范围算小 → 下方图标被裁剪。
        if (_livePositions.Count > 0)
        {
            foreach (var kv in _livePositions)
            {
                maxRight = Math.Max(maxRight, kv.Value.X + CellWidth);
                maxBottom = Math.Max(maxBottom, kv.Value.Y + CellHeight);
            }
        }
        else
        {
            foreach (var child in canvas.Children)
            {
                if (child is not FrameworkElement fe || ReferenceEquals(fe, _dropIndicator))
                {
                    continue;
                }

                var x = Canvas.GetLeft(fe);
                var y = Canvas.GetTop(fe);
                var w = fe.ActualWidth > 0 ? fe.ActualWidth : CellWidth - 6;
                var h = fe.ActualHeight > 0 ? fe.ActualHeight : CellHeight - 6;
                maxRight = Math.Max(maxRight, x + w + 8);
                maxBottom = Math.Max(maxBottom, y + h + 8);
            }
        }

        canvas.Width = maxRight;
        canvas.Height = maxBottom;
    }

    /// <summary>拖动视觉：半透明 + 轻微放大 + 投影 + 置顶（拖动期间的即时反馈动画）。</summary>
    private static void BeginDragVisual(FrameworkElement cell)
    {
        // 拖动中必须清晰可见：只用轻微半透明表达"拿起"状态，不能弱到看不清图标内容
        cell.Opacity = 0.95;
        cell.RenderTransformOrigin = new Point(0.5, 0.5);
        cell.RenderTransform = new ScaleTransform(1.06, 1.06);
        cell.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 4,
            Opacity = 0.55
        };
        Panel.SetZIndex(cell, 1000);
    }

    /// <summary>结束拖动视觉，恢复常态。</summary>
    private static void EndDragVisual(FrameworkElement cell)
    {
        cell.Opacity = 1;
        cell.RenderTransform = null;
        cell.Effect = null;
        Panel.SetZIndex(cell, 0);
    }

    // ======== 选中 ========

    private void SelectForClick(string path, bool extend)
    {
        if (extend)
        {
            if (_browser is Services.DesktopBrowser impl) impl.ToggleSelection(path);
        }
        else
        {
            _browser.SetSelection(new[] { path });
        }
    }

    private void SyncSelectionVisual(Border cell, string path)
    {
        var selected = _browser.SelectedPaths.Contains(path);
        cell.Background = selected
            ? new SolidColorBrush(Color.FromArgb(70, 0x00, 0x78, 0xD4))
            : Brushes.Transparent;
        cell.BorderThickness = new Thickness(selected ? 1 : 0);
        cell.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0x00, 0x78, 0xD4));
    }

    // ======== 打开 / 文件操作 ========

    private void Open(BrowserEntry entry)
    {
        // shell 虚拟项（此电脑/回收站等）：explorer.exe ::{CLSID} 打开（与 dock 系统区同款）。
        if (entry.IsShellNamespace)
        {
            StartFileShellNamespace(entry.Path);
        }
        else if (entry.IsDirectory) _browser.Navigate(entry.Path);
        else StartFile(entry.Path);
    }

    /// <summary>经 explorer 打开 shell 命名空间项（"::{CLSID}"）。</summary>
    private static void StartFileShellNamespace(string clsidPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = clsidPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // 启动失败静默（M10）
        }
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

    private static void OpenSettings(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 设置打开失败静默（M10）
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHObjectProperties(IntPtr hwnd, uint stype, string pszObject, string? pszPage);

    /// <summary>打开 Win32 文件属性页（SHObjectProperties，stype=0 SHOP_FILEPATH）。</summary>
    private void ShowProperties(string path)
    {
        try
        {
            var hwnd = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;
            _ = SHObjectProperties(hwnd, 0, path, null);
        }
        catch
        {
            // 属性页打开失败静默（M10）
        }
    }

    // ======== 右键菜单（自绘主题风格，DesktopMenuStyling 工厂） ========

    /// <summary>图标右键菜单（MENU-SPECS §2 实用子集，自绘主题呈现）。</summary>
    private ContextMenu BuildIconMenu(BrowserEntry entry, Border cell, TextBlock label)
    {
        var menu = DesktopMenuStyling.CreateMenu();
        DesktopMenuStyling.AddItem(menu, "打开", () => Open(entry), isDefault: true);

        // shell 虚拟项不是文件系统对象：剪切/复制/重命名/删除一律不适用（对回收站做删除会静默失败甚至误操作）。
        if (entry.IsShellNamespace)
        {
            return menu;
        }

        DesktopMenuStyling.AddSeparator(menu);
        DesktopMenuStyling.AddItem(menu, "剪切", () => { _browser.SetSelection(new[] { entry.Path }); _browser.Cut(); });
        DesktopMenuStyling.AddItem(menu, "复制", () => { _browser.SetSelection(new[] { entry.Path }); _browser.Copy(); });
        DesktopMenuStyling.AddItem(menu, "重命名", () => StartRename(cell, label, entry.Path));
        DesktopMenuStyling.AddItem(menu, "删除", () => { _browser.SetSelection(new[] { entry.Path }); _browser.Delete(); });
        DesktopMenuStyling.AddSeparator(menu);
        DesktopMenuStyling.AddItem(menu, "属性", () => ShowProperties(entry.Path));
        return menu;
    }

    /// <summary>空白处右键菜单（MENU-SPECS §1 实用子集，自绘主题呈现）。粘贴项在菜单打开时按 CanPaste 刷新可用态。</summary>
    private ContextMenu BuildBlankMenu()
    {
        var menu = DesktopMenuStyling.CreateMenu();
        DesktopMenuStyling.AddItem(menu, "新建文件夹", () => _browser.NewFolder());
        _pasteItem = new MenuItem { Header = "粘贴" };
        _pasteItem.Click += (_, _) =>
        {
            try { _browser.Paste(); }
            catch { /* 粘贴失败静默（M10） */ }
        };
        menu.Items.Add(_pasteItem);
        DesktopMenuStyling.AddSeparator(menu);
        DesktopMenuStyling.AddItem(menu, "整理图标", CompactLayout);
        DesktopMenuStyling.AddItem(menu, "刷新", () => _browser.Refresh());
        DesktopMenuStyling.AddSeparator(menu);
        DesktopMenuStyling.AddItem(menu, "显示设置", () => OpenSettings("ms-settings:display"));
        DesktopMenuStyling.AddItem(menu, "个性化", () => OpenSettings("ms-settings:personalization"));
        // ContextMenuOpening 触发在放置目标（本 ScrollViewer）上，而非菜单自身。
        ContextMenuOpening += (_, _) =>
        {
            if (_pasteItem is not null) _pasteItem.IsEnabled = _browser.CanPaste;
        };
        return menu;
    }

    // ======== 内联重命名 ========

    /// <summary>图标标签换成 TextBox 就地编辑：Enter/失焦提交，Esc 取消。与 Rebuild 竞态按 M10 丢弃编辑态。</summary>
    private void StartRename(Border cell, TextBlock label, string path)
    {
        if (_editingPath is not null)
        {
            return; // 已有一个在编辑
        }

        var sp = cell.Child as StackPanel;
        var idx = sp?.Children.IndexOf(label) ?? -1;
        if (sp is null || idx < 0)
        {
            return;
        }

        _editingPath = path;
        _browser.SetSelection(new[] { path }); // 保证 Rename 单选中

        var originalName = label.Text;
        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(path),
            FontSize = label.FontSize,
            MaxWidth = CellWidth - 10,
            MaxLength = 255,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ContextMenu = null // 抑制 WPF 默认剪贴板右键菜单（系统样式，2026-09-02 统一收口）
        };
        sp.Children[idx] = box;
        box.Focus();
        box.SelectAll();

        var committed = false;
        void Commit()
        {
            if (committed || !string.Equals(_editingPath, path, StringComparison.Ordinal))
            {
                return;
            }

            committed = true;
            _editingPath = null;
            sp.Children[idx] = label; // 先还原 label；成功改名后 Rebuild 会整体重建
            var name = box.Text.Trim();
            if (name.Length > 0 && !string.Equals(name, originalName, StringComparison.Ordinal))
            {
                _browser.Rename(name);
            }
        }

        void Cancel()
        {
            if (committed || !string.Equals(_editingPath, path, StringComparison.Ordinal))
            {
                return;
            }

            committed = true;
            _editingPath = null;
            sp.Children[idx] = label;
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
        box.LostFocus += (_, _) => Commit();
    }

    // ======== 拖入（外部文件 → 当前浏览目录） ========

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // Ctrl=复制，否则移动（explorer 惯例）
            e.Effects = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey)
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var move = e.Effects == DragDropEffects.Move;
        _browser.ImportFiles(paths, move);
    }

    // ======== 图标提取 ========

    /// <summary>图标异步提取（ExtraLarge，IconScheduler 调度）；目录失败回退自绘 glyph。</summary>
    private static async System.Threading.Tasks.Task LoadFileIconAsync(string path, bool isDirectory, Image target, Border iconBox)
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
            else if (isDirectory)
            {
                FallbackToGlyph(iconBox);
            }
        }
        catch
        {
            if (isDirectory)
            {
                FallbackToGlyph(iconBox);
            }
            // 文件提取失败留空白（M10）
        }
    }

    private static void FallbackToGlyph(Border iconBox)
    {
        // 仅当图标框仍是 Image 时替换（避免覆盖已提取成功的图标）
        if (iconBox.Child is Image)
        {
            iconBox.Child = FolderGlyph();
        }
    }

    /// <summary>文件夹自绘 glyph（白色描边，随壁纸可读）。仅在真实文件夹图标提取失败时作回退。</summary>
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

    public void Dispose()
    {
        _disposed = true;
        foreach (var handle in _menuHandles)
        {
            try { handle.Dispose(); }
            catch { /* 注销失败不阻断（M10） */ }
        }
        _menuHandles.Clear();
        _cellMenuTargets.Clear();
        Content = null;
    }

    // ===== 统一右键菜单路由（shell-context-menu 新路径；旧自绘路径见 Build*Menu） =====

    private void OnMenuServiceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_menus is null || _disposed)
        {
            return;
        }

        var (entry, cell, label) = FindMenuTarget(e.OriginalSource as DependencyObject);
        if (entry is not null && cell is not null && label is not null)
        {
            if (!_browser.SelectedPaths.Contains(entry.Path))
            {
                // explorer 同款：右键未选中项 → 先单选再弹菜单
                _browser.SetSelection([entry.Path]);
            }
            _ = ShowMenuAsync(new DesktopIconTarget(entry, cell, label), e);
        }
        else
        {
            _ = ShowMenuAsync(null, e);
        }
        e.Handled = true;
    }

    private (BrowserEntry? Entry, Border? Cell, TextBlock? Label) FindMenuTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && _cellMenuTargets.TryGetValue(border, out var mapped))
            {
                return (mapped.Entry, border, mapped.Label);
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return (null, null, null);
    }

    private async Task ShowMenuAsync(DesktopIconTarget? target, MouseButtonEventArgs e)
    {
        if (_menus is null)
        {
            return;
        }

        try
        {
            FileIdentity? identity = null;
            if (target is not null && !target.Entry.IsShellNamespace)
            {
                // 多选（右键项在选中集内）→ 交集能力过滤（ClassifyMany，单文件专属项自动隐藏）；
                // 单选 → 单项分类（审查 P1-1：MenuRequest.SelectedPaths 语义兑现）
                var selected = _browser.SelectedPaths;
                identity = _classifier is null ? null
                    : selected.Count > 1 && selected.Contains(target.Entry.Path)
                        ? _classifier.ClassifyMany([.. selected])
                        : _classifier.Classify(target.Entry.Path);
            }

            // PointToScreen 返回物理像素 → 换算 DIP（弹层窗口 Left/Top 使用逻辑坐标）
            var physical = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var screenPos = new Point(physical.X / dpi, physical.Y / dpi);

            var request = new MenuRequest(
                target is null ? MenuScope.Desktop : MenuScope.DesktopIcon,
                target,
                screenPos,
                File: identity,
                SelectedPaths: [.. _browser.SelectedPaths]);
            await _menus.ShowAsync(request);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"右键菜单展示失败: {ex.Message}");
        }
    }

    // ===== 模板回调包装（DesktopMenuTemplates 经此访问控件能力；保持原 private 方法不动） =====

    internal void InvokeBrowserNewFolder() => _browser.NewFolder();

    internal void InvokeBrowserPaste() => _browser.Paste();

    internal bool InvokeCanPaste() => _browser.CanPaste;

    internal void InvokeBrowserRefresh() => _browser.Refresh();

    // 多选语义（explorer 同款，审查 P1-1）：右键项在选中集内 → 整集操作，**不得重置选中集**；
    // 否则单选该项再操作。（多选身份的能力交集过滤在 ShowMenuAsync 经 ClassifyMany 提供）

    internal void InvokeBrowserCut(string path)
    {
        if (!_browser.SelectedPaths.Contains(path))
        {
            _browser.SetSelection([path]);
        }
        _browser.Cut();
    }

    internal void InvokeBrowserCopy(string path)
    {
        if (!_browser.SelectedPaths.Contains(path))
        {
            _browser.SetSelection([path]);
        }
        _browser.Copy();
    }

    internal void InvokeBrowserDelete(string path)
    {
        if (!_browser.SelectedPaths.Contains(path))
        {
            _browser.SetSelection([path]);
        }
        _browser.Delete();
    }

    internal void InvokeCompactLayout() => CompactLayout();

    internal void InvokeOpenSettings(string uri) => OpenSettings(uri);

    internal void InvokeOpenEntry(BrowserEntry entry) => Open(entry);

    internal string InvokeDesktopPath() => _browser.DesktopPath;

    internal void InvokeStartRename(Border cell, TextBlock label, string path) => StartRename(cell, label, path);

    internal void InvokeShowProperties(string path) => ShowProperties(path);

    internal double InvokeGetDouble(string key, double defaultValue) =>
        _settings?.Get(key, defaultValue) ?? defaultValue;

    internal bool InvokeGetBool(string key, bool defaultValue) =>
        _settings?.Get(key, defaultValue) ?? defaultValue;

    internal void InvokeSetDouble(string key, double value) => _settings?.Set(key, value);

    internal void InvokeSetBool(string key, bool value) => _settings?.Set(key, value);

    internal string? InvokeSortKey() => _browser.SortKey;

    /// <summary>设置排序键：Browser 即时重载 + desktop.sortKey 持久化（下次启动经构造恢复）。</summary>
    internal void InvokeSetSort(string? key)
    {
        _settings?.Set("desktop.sortKey", key ?? string.Empty);
        _browser.SetSort(key);
    }

    internal void InvokeCreateTextFile() => _browser.CreateTextFile();
}

/// <summary>极简 ICommand（双击绑定用）。</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
