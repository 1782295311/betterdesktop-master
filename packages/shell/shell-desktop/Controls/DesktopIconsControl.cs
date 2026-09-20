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
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Windows;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Controls;

// ── 本文件方法级白话索引（桌面图标画布，2000 行按功能分组找）──
//   "图标网格重建/排列方式"             → Rebuild / RebuildAutoArrange（自动对齐）/ RebuildFreeLayout（自由摆放）；位置持久化 LoadPositions
//   "创建一个图标格子"                  → CreateItem；格子命中框 GetCellRect
//   【左键拖动 = 原生桌面式（2026-09-17 改版 + 四轮）】→ TryStartInteractionDrag / HandleInteractionDrop / MoveDragSelectionToPlace / OpenFilesWith / IsExecutableApp
//       拖到文件夹图标 = 移入（Ctrl=复制）／拖到 exe·快捷方式 = 用该程序打开／拖到空白 = 整组跟着手走并落位（实时跟随动画）／自身·回收站等虚拟项 = 无操作
//   【右键长按拖动 = 自由摆放（2026-09-17 改版）】→ StartRightHoldWatch / ArmRightHold / BeginFreeDrag / FinishFreeDrag / ExitArrangeAndContinueDrag
//   "自由摆放自动排版（避让/找空位/重叠消解/紧凑）" → SnapTarget、FindFreeSlot、RebuildOccupancy、ApplyAvoidance、ResolveOverlaps、CompactLayout、CommitLayout
//   "拖拽过程的快照/复位/动画"          → CaptureLayoutSnapshot、RestoreBasePositions、AnimateTo、BeginDragVisual/EndDragVisual、ShowDropIndicator/HideDropIndicator
//   "拖拽中触发重建后续拖"              → ExitArrangeAndContinueDrag / ResumeDragAfterRebuild；按路径找格子 FindCellByPath/EnumerateCells/ClearDragState
//   "选中/双击打开"                     → SelectForClick/SyncSelectionVisual、Open/StartFile/StartExplorerFolder/StartFileShellNamespace/OpenSettings
//   "回收站"                            → **无任何特殊处理**（2026-09-17 拆除）：它是普通图标，拖上去不删除
//   "右键菜单（统一路由）"              → BuildIconMenuEntries（图标）/ BuildBackgroundMenuEntries（空白），渲染交 DesktopMenuPopup（自绘）
// ────────────────────────────────────

/// <summary>右键路由的图标目标（cell→entry 映射；原 DesktopMenuTemplates 定义，收口后本地化）。</summary>
public sealed record DesktopIconTarget(BrowserEntry Entry, Border Cell, TextBlock Label);

/// <summary>桌面图标网格（透明背景，铺满桌面窗口工作区）。</summary>
public sealed class DesktopIconsControl : ScrollViewer, IDisposable
{
    private readonly IDesktopBrowser _browser;
    private readonly ISettingsService? _settings;
    private readonly BetterDesktop.Shell.Convert.Contracts.IConvertMenuService? _convertMenu;
    private readonly BetterDesktop.Shell.Convert.Contracts.IArchiveService? _archive;
    private readonly Func<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService?>? _clipboardFactory;
    private readonly IEventBus? _events;
    private IDisposable? _settingsSub;

    private readonly Dictionary<Border, (BrowserEntry Entry, TextBlock Label)> _cellMenuTargets = [];
    private bool _disposed;

    // 拖出状态
    private Point _dragStart;
    private string? _dragPath;
    private bool _dragPossible;

    // 当前按住的是哪个键（2026-09-17 改版：左键=图标间文件拖放，右键长按=自由摆放）
    private MouseButton _dragButton = MouseButton.Left;

    // 拖动重排/自由摆放状态（自由布局）
    private bool _dragMoving;
    private double _itemOriginX;
    private double _itemOriginY;
    private FrameworkElement? _draggingCell;

    // 批量拖动：框选（或多选）后按下其中一个图标拖动 → 整个选中集一起移动。
    // _dragCells 与 _dragOrigins 一一对应，保持集合内图标相对位置不变。
    private readonly List<FrameworkElement> _dragCells = new();
    private readonly List<(double X, double Y)> _dragOrigins = new();

    // ===== 左键交互拖放（2026-09-17）状态 =====
    // _internalDragActive：内部拖放进行中 → OnDragOver 返回 None，使自家桌面窗口**拒收**自己的拖放，
    //   于是 DoDragDrop 落回本窗口时返回 None，交由 CompleteInteractionDrop 解析落点语义。
    private bool _internalDragActive;
    private bool _dragCopyModifier;           // 拖动期间 Ctrl 是否按下（GiveFeedback 持续记录）
    private List<string>? _interactionPaths;  // 本次拖放的源路径快照（shell 虚拟项 CLSID 已剔除）
    private HashSet<string>? _interactionSourceSet; // 同上的集合形态（落点判定要排除源自身）
    private Border? _interactionHoverCell;    // 拖动中实时高亮的落点 cell
    private bool _internalDropHandled;        // 自家 OnDrop 已执行动作（避免 DoDragDrop 返回后再做一次）
    private bool _interactionCancelled;       // Esc/右键取消（QueryContinueDrag 上报）→ 兜底不得执行动作
    // 原生风格拖动提示（"移动到 xxx / 用 xxx 打开"，跟随光标；画在自己窗口里的 Popup，见 EnsureInteractionTip）
    private System.Windows.Controls.Primitives.Popup? _interactionTip;
    private Border? _interactionTipBody;
    private TextBlock? _interactionTipGlyph;
    private TextBlock? _interactionTipText;

    // ===== 右键长按（自由摆放入口，2026-09-17）状态 =====
    private const int RightHoldMs = 350;
    private System.Windows.Threading.DispatcherTimer? _rightHoldTimer;
    private Border? _rightHoldCell;
    private bool _rightArmed;                 // 长按就绪：此后拖动 = 自由摆放

    // 落点预览指示器（拖动时高亮显示松手后会落在哪个格，便于确认松手效果）
    private Border? _dropIndicator;

    // 说明（2026-09-17 用户拍板）：**回收站不再有任何特殊拖放处理**——它在图标/入口层级里
    // 就是一个普通项（与"此电脑/控制面板"同级），拖上去既不删除也不高亮；删除走右键菜单 / Delete 键。
    // 原 2026-09-07 的"拖到桌面回收站 / dock 栏回收站 = 移入回收站"整条链路（含 kernel 共享通道
    // DockDropTargets）已按用户要求拆除。

    // ===== 框选（橡皮筋多选） =====
    private bool _rubberActive;
    private bool _rubberCtrl;              // 按下 Ctrl → 框选结果追加到原选中

    /// <summary>
    /// 框选自愈看门狗：只在框选进行中运行。自绘桌面嵌在 explorer DefView 里，
    /// 某些路径下 MouseLeftButtonUp 会丢（2026-09-16 真机：1px 细线永久留在桌面、还会跟着鼠标变形），
    /// 这里用"物理左键是否还按着"兜底——松开即清理并停表（空闲期零开销）。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _rubberWatchdog;
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
        BetterDesktop.Shell.Convert.Contracts.IConvertMenuService? convertMenu = null,
        BetterDesktop.Shell.Convert.Contracts.IArchiveService? archive = null,
        IEventBus? events = null,
        Func<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService?>? clipboardFactory = null)
    {
        _browser = browser;
        _settings = settings;
        _convertMenu = convertMenu;
        _archive = archive;
        _clipboardFactory = clipboardFactory;
        _events = events;
        Background = Brushes.Transparent; // Transparent 可 HitTest：整个桌面区域接收鼠标事件
        // 对齐 cairoshell DesktopFolderViewStyle：横向滚动（纵向禁用），先填满一列再横向开新列
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // 【回归修复 2026-09-07】必须 Stretch 填满父容器：Left/Top 只占图标排列长方形区域，
        // 导致图标之外的桌面空白处右键事件收不到（OnMenuServiceMouseUp 在此控件内）→ 空白无菜单。
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Margin = new Thickness(7, 13, 0, 0); // cairoshell DesktopIcons.setPosition 同值

        // 拖入支持：外部文件/文件夹拖到桌面 → 复制/移动进当前浏览目录（始终启用）
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        // 2026-09-07 回归自绘：桌面空白/图标条目右键一律自绘菜单（ShowMenu → DesktopMenuPopup）。
        MouseRightButtonUp += OnMenuServiceMouseUp;

        // 计划 G1 全键盘：F2/Del/Shift+Del/Ctrl+C·X·V·A/Ctrl+Shift+C/Alt+Enter 真实响应——
        // 模板快捷键列写出的键全部在此兑现（反假提示红线，计划 §5-3）。
        PreviewKeyDown += OnControlPreviewKeyDown;

        // F10/O2（7438 纪律）：订阅必须可退订——匿名 lambda 无法 -=，改命名方法；Dispose 中配对退订。
        _browser.ItemsChanged += OnBrowserItemsChanged;

        // 恢复持久化排序（desktop.sortKey；菜单改排序时同步写此键；空串=默认）
        if (settings is not null)
        {
            _browser.SetSort(settings.Get("desktop.sortKey", string.Empty), settings.Get("desktop.sortDesc", false));
        }

        // 图标大小/间距变化 → 网格格子变了 → 自动紧凑重排（违规1修复：裸 event → IEventBus）
        if (settings is not null && _events is not null)
        {
            _settingsSub = _events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                });
        }

        // 首次加载必须显式触发：DesktopBrowser 构造只设 Location 不枚举，
        // 不调 Refresh 则 Items 永远为空（此前"桌面无图标"的根因）。
        _browser.Refresh();

        // 右键菜单候选预热（2026-09-07）：ToolCatalog 首次注册表枚举约几百 ms，
        // Background 优先级后台预热避免首次右键卡 UI 无响应。
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                try { _ = ToolCatalog.Detect(); }
                catch { /* 预热失败静默（M10） */ }
            }));
    }

    /// <summary>
    /// 空白处双击（图标之外的自由布局空白区）。供 DesktopWindow 实现「双击切换隐藏桌面图标」。
    /// 图标自身的双击（打开文件）在 cell 层已处理并标记 Handled，不会触发本事件。
    /// 【2026-09-11】参数=落点（本控件坐标系）：订阅方需按**落点几何**再确认一次"确实在空白处"
    /// （cell 层放行/事件源判定在格边缘与列间空隙上会漏判，见 DesktopWindow.TryToggleOnBlankDoubleClick）。
    /// </summary>
    public event EventHandler<Point>? BlankAreaDoubleClick;

    /// <summary>落点（本控件坐标系）是否命中任一图标格（含容差，覆盖列间空隙与格子边缘）。
    /// 两种布局通用：自动排列 WrapPanel 与自由布局 Canvas 都按可视化树里的 cell（<c>Border.Tag</c>=路径）判定。
    /// 隐藏态（Content=null，无 cell）自然返回 false = "空白"，双击即恢复通道。</summary>
    public bool IsPointOverIcon(Point pInControl, double tolerance = 4)
    {
        foreach (var cell in EnumerateAllCells())
        {
            if (cell.ActualWidth <= 0 || cell.ActualHeight <= 0)
            {
                continue;
            }

            Rect r;
            try
            {
                // cell 左上角 + 自身尺寸，换算到本控件坐标系（两种布局的父容器不同，交给 WPF 换算）
                r = cell.TransformToAncestor(this)
                    .TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue; // 尚未接入可视化树（重建中）：跳过本次判定
            }

            r.Inflate(tolerance, tolerance);
            if (r.Contains(pInControl))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>枚举两种布局下的全部图标 cell（深度优先；命中即不再深入其子树）。</summary>
    private IEnumerable<Border> EnumerateAllCells()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is Border b && b.Tag is string { Length: > 0 })
            {
                yield return b;
                continue; // 不深入 cell 内部（内部 Border 无 Tag，且没必要）
            }

            var count = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++)
            {
                stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }
    }

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

    /// <summary>自动排列：true（默认）=瀑布列自动排列（左键拖动不改位置，仍是"拖到文件夹/程序上投递"）；
    /// false=自由布局，右键长按拖动可自由摆放（让位/吸附/框选在该模式下生效）。</summary>
    private bool AutoArrange => _settings?.Get("desktop.autoArrange", true) ?? true;

    /// <summary>对齐网格：自由摆放（右键长按拖动）松手后吸附到网格（默认 true）。</summary>
    private bool SnapToGrid => _settings?.Get("desktop.snapToGrid", true) ?? true;

    /// <summary>【2026-09-17 语义迁移】自动排列下**右键长按自由摆放**时，先固化瀑布布局并退出自动排列再续拖
    /// （desktop.autoExitArrangeOnDrag）。旧语义为"左键拖动 → 退出自动排列"，左键已不再改变图标位置。</summary>
    private bool AutoExitArrangeOnDrag => _settings?.Get("desktop.autoExitArrangeOnDrag", true) ?? true;

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

        // 【2026-09-07 诊断】图标加载链路落点：items 数 / 布局模式 / 隐藏态
        DiagnosticLog.Trace("shell.desktop",
            $"Rebuild: items={_browser.Items.Count} autoArrange={AutoArrange} iconsHidden={_settings?.Get("desktop.iconsHidden", false) ?? false}");

        // 【回归修复 2026-09-06 / P2-9】隐藏图标态：清空网格但保留控件交互
        //（空白右键=背景菜单）——Collapsed 会吞整棵子树事件，隐藏图标后右键完全失效
        //（用户实测：仅剩窗口层诊断日志、无任何菜单）。
        // 【2026-09-11 修复】隐藏态双击恢复不走本控件：Content=null 后 canvas/WrapPanel
        // 双击链全部断开（左键冒泡收不到），恢复由 DesktopWindow 的 PreviewMouseLeftButtonDown
        // 隧道兜底（窗口层先收到，与右键同机制）。此处保持 Content=null——不留空布局容器，
        // 避免空 canvas Stretch 与显示态 Left/Top 反复切换导致布局抖动（2026-09-11 用户实测崩溃）。
        if (_settings?.Get("desktop.iconsHidden", false) ?? false)
        {
            Content = null;
            return;
        }

        // 竖向滚动条：自动排列是"填满一列再换列"（无需竖向滚动）；
        // 自由布局图标可摆到任意 Y（含视口下方），故需要竖向滚动。
        VerticalScrollBarVisibility = AutoArrange ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        DiagnosticLog.Trace("shell.desktop", "Rebuild: VSB=" + VerticalScrollBarVisibility);

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
        DiagnosticLog.Trace("shell.desktop", $"RebuildAutoArrange: cells={panel.Children.Count}");
    }

    /// <summary>
    /// 自由布局：Canvas + 每个图标保存的坐标（desktop.iconPositions）；
    /// 无保存坐标的条目按「列优先」网格自动分配初始位置（列满换列）。
    /// 用户可拖动图标任意摆放，松手保存（可选吸附网格）。
    /// </summary>
    private void RebuildFreeLayout()
    {
        // 【2026-09-07 根因修复】Canvas 默认 HorizontalAlignment/VerticalAlignment=Stretch：
        // 显式 Width/Height(420x1064) 与 Stretch 并存 → 布局槽拉伸、内容按固定尺寸居中
        // → 整个画布（含全部图标）渲染到屏幕中央（LayoutCheck 实测 screen=(1022,78)）。
        // 自动排列 WrapPanel 无显式尺寸、Stretch 填满 → 从左上角排布 → 正常。
        // 修：显式 Left/Top，画布回到桌面左上角（Margin 7,13 由控件本身承载）。
        var canvas = new Canvas
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
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
            BorderBrush = ThemeBrushes.AccentTint(0.59),
            Background = ThemeBrushes.AccentTint(0.13),
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
            BorderBrush = ThemeBrushes.AccentTint(0.86),
            Background = ThemeBrushes.AccentTint(0.15),
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
            // 【2026-09-11】精确 ==2：三击（ClickCount=3）不重复切换（曾导致图标闪回）。
            if (e.ClickCount == 2)
            {
                _rubberActive = false;
                canvas.ReleaseMouseCapture();
                if (_rubberBand is not null)
                {
                    _rubberBand.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
                BlankAreaDoubleClick?.Invoke(this, e.GetPosition(this));
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
            StartRubberWatchdog(canvas);
            e.Handled = true;
        };

        canvas.MouseMove += (_, e) =>
        {
            // 【2026-09-16 真机事故 · 桌面残留"细小线框"的真凶】
            // 橡皮筋的收尾**只有** MouseLeftButtonUp 一条路；自绘桌面是 explorer DefView 里的子窗口，
            // 某些路径下那次 Up 会丢（捕获被夺/消息没送到）→ _rubberActive 永远为真、矩形永久可见，
            // 而且**跟着鼠标变形**（用户实测："包裹图标区域的细线，被我拉到屏幕最右边拉变形了"）。
            // 兜底判据用**物理左键状态**：左键已松开却还在框选态 = Up 丢了 → 立即收干净。
            // 这样用户只要再动一下鼠标就自愈，不必重启宿主或切壁纸。
            if (e.LeftButton == MouseButtonState.Released)
            {
                ClearTransientOverlays(canvas);
                return;
            }

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
            DiagnosticLog.Trace("shell.desktop",
                $"框选诊断: 结束后 selCnt={_browser.SelectedPaths.Count}");
        };

        // 【2026-09-16 修复 · 桌面残留"细小黑色线框"】框选/拖动指示器的**唯一**清理点是 MouseLeftButtonUp；
        // 一旦这次 Up 没送到（捕获被系统/其他窗口夺走、拖动中被强制取消、explorer 重建桌面时丢了消息），
        // 橡皮筋或落点指示器就会**永久留在桌面上**（用户实测：一条细小黑色线框，切壁纸/重建桌面后才消失）。
        // 因此补上两条与交互无关的兜底清理：① 丢失鼠标捕获；② 尺寸/可见性变化（壁纸切换、显示设置变更都会走这里）。
        canvas.LostMouseCapture += (_, _) => ClearTransientOverlays(canvas);
        SizeChanged += (_, _) => ClearTransientOverlays(Content as Canvas);

        // 每列容量：按 ScrollViewer 内容区高度 ViewportHeight（与自动排列 WrapPanel 同基准，
        // 扣除水平滚动条；首帧未布局时回退主屏工作区高度）。用 ActualHeight 会在水平滚动条
        // 出现时偏大 → 自由布局每列行数偏多 → 列数偏少（开关切换行列不一致，用户实测）。
        var available = ViewportHeight > CellHeight * 2
            ? ViewportHeight
            : SystemParameters.WorkArea.Height - 80;
        var rowsPerColumn = Math.Max(1, (int)Math.Floor(available / CellHeight));

        // 【回归修复 2026-09-06】旧错误布局迁移：旧版本 ActualHeight 瞬态值导致 ExitArrange
        // 固化出"所有图标 row=0"的烂布局并持久化到 desktop.iconPositions，用户一进自由布局
        // 就全重叠（用户实测"一进去就是重叠的样子"）。检测：存量坐标全部挤在第一行（y<CellHeight）
        // 且图标数 > 每列容量 → 视为旧错误布局，清除坐标并恢复默认自动排列（用户期望默认开着）。
        if (positions.Count > 0 && _settings is not null)
        {
            var allRowZero = positions.Values.All(p => p is { Length: >= 2 } && p[1] < CellHeight);
            if (allRowZero && _browser.Items.Count > rowsPerColumn)
            {
                _settings.Set("desktop.iconPositions",
                    new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));
                _settings.Set("desktop.autoArrange", true);
                return; // 设置变更触发 Rebuild，走自动排列分支
            }
        }

        // 【2026-09-07 重叠根因修复】新条目（无保存坐标）不得用列表序号直接映射列/行：
        // 序号对应的格位几乎必然已被「有保存坐标的图标」占用（转换生成新文件时尤其如此），
        // 直接落位 = 叠在已有图标上。改为扫描「首个空闲格」（列优先，跳过已占用格位）。
        var occupied = new HashSet<(int Col, int Row)>();
        foreach (var pos in positions.Values)
        {
            if (pos is { Length: >= 2 } && pos[0] >= 0 && pos[1] >= 0)
            {
                occupied.Add(((int)Math.Round(pos[0] / CellWidth), (int)Math.Round(pos[1] / CellHeight)));
            }
        }

        var index = 0;
        var maxRight = CellWidth;
        var maxBottom = CellHeight;
        var freeCol = 0;
        var freeRow = 0;
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
                // 新条目：列优先扫描第一个未被已存坐标占用的格位；用完即标记，
                // 后续新条目顺延到下一个空闲格（不回头，避免 O(n²)）。
                while (occupied.Contains((freeCol, freeRow)))
                {
                    freeRow++;
                    if (freeRow >= rowsPerColumn)
                    {
                        freeRow = 0;
                        freeCol++;
                    }
                }

                var col = freeCol;
                var row = freeRow;
                occupied.Add((col, row));
                freeRow++;
                if (freeRow >= rowsPerColumn)
                {
                    freeRow = 0;
                    freeCol++;
                }

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
        DiagnosticLog.Trace("shell.desktop",
            $"RebuildFreeLayout: cells={canvas.Children.Count} canvas=({canvas.Width:F0}x{canvas.Height:F0})");
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
            TextWrapping = TextWrapping.NoWrap,
            // 【回归修复 2026-09-06】单行截断：旧 Wrap+MaxHeight(30)+CharacterEllipsis 组合下，
            // Wrap 使 Trimming 失效，长文本换行后被 MaxHeight 裁成半行 → 标签两行重叠/乱码
            // （用户截图实锤）。改为 NoWrap+CharacterEllipsis：长名单行末尾省略号，绝不换行。
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
            Tag = entry.Path,
            // 【2026-09-17】左键拖放必须能落到图标上：WPF 只在光标下元素链上存在 AllowDrop 元素时
            // 才把 OLE 的 DragOver/Drop 路由上来 → 不给 cell 打开就会"拖到图标上恒显示禁止符号"（用户实测）。
            // 事件处理器仍在本控件（ScrollViewer）层，cell 只作为"可接收"的命中元素。
            AllowDrop = true
        };

        // 图标真实化：目录与文件统一 IconHelper 提取；目录失败回退自绘 glyph；
        // shell 虚拟项（::CLSID）IconHelper 按文件路径取必失败，改走 shell PIDL 提取（同步，项少开销可忽略）。
        if (icon.Child is Image img)
        {
            if (entry.IsShellNamespace)
            {
                img.Source = ShellItemIcon.GetByParsingName(entry.Path);
            }
            else
            {
                _ = LoadFileIconAsync(entry.Path, entry.IsDirectory, img, icon);
            }
        }

        // 选中态跟随
        SyncSelectionVisual(cell, entry.Path);
        _browser.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncSelectionVisual(cell, entry.Path));

        // ===== 手势总览（2026-09-17 用户拍板改版 + 四轮补齐）=====
        //   左键按住拖动 → **原生桌面式**（整组实时跟随，与右键同一套群体动画）：拖到文件夹图标 = 移入（Ctrl=复制）、
        //                  拖到 exe/快捷方式 = 用该程序打开（此时整组退回原位）、
        //                  拖到空白 = 整组跟着手走、松手落在这里、拖到自身/回收站等虚拟项 = 无操作、
        //                  拖到外部程序 = 交给系统（OLE FileDrop，"拖出"能力保留）。
        //   右键长按后拖动 → **自由摆放**（原左键语义：图标跟随鼠标 + 占用者让位 + 松手落盘）；
        //                  松手仍弹右键菜单（用户拍板：自由摆放与菜单都要）。
        //   右键短按 → 右键菜单（不变）；左键空白拖动 → 框选（不变）。
        // 单击选中 / 右键选中（右键让当前项入选中集，explorer 同款）
        // CaptureMouse 保证鼠标移出图标单元格仍能收到 MouseMove/Up。
        cell.MouseLeftButtonDown += (_, e) =>
        {
            // 【2026-09-07 修复】批量选中被破坏：无条件 SelectForClick 会把框选/Ctrl 点选
            // 得到的多选集合单选化（SetSelection(new[]{path})），随后 _dragCells 的批量收集
            // 条件 SelectedPaths.Count>1 立即失效 → "视觉是批量，实际还是单选"、只拖一个。
            // 改为：仅当「不在选中集」或「按 Ctrl」时才走 SelectForClick（explorer 惯例：
            // 无 Ctrl 点击已选中项保持多选，用于整组拖动；Ctrl 点击切换该项）。
            var _ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var _inSel = _browser.SelectedPaths.Contains(entry.Path);
            DiagnosticLog.Trace("shell.desktop",
                $"点击诊断: selCnt={_browser.SelectedPaths.Count} inSel={_inSel} ctrl={_ctrl} parent={cell.Parent?.GetType().Name}");
            // 【2026-09-07 加固】点击已在选中集内的图标：无论是否按 Ctrl 都不改动选中集。
            // 原因：Ctrl 点击已选中项 = toggle 移除 → 拖动集收集条件 Contains(entry.Path) 失效 →
            // "视觉多选、点住一个拖动" 立即塌缩成单选（用户实测失败）。桌面场景下点住已选中项
            // 的心智就是"整组拖动"（macOS 桌面同款），Ctrl 取消单项让位于拖动稳定性。
            if (!_inSel)
            {
                SelectForClick(entry.Path, extend: _ctrl);
            }
            _dragStart = e.GetPosition(this);
            _dragPath = entry.Path;
            _itemOriginX = Canvas.GetLeft(cell);
            _itemOriginY = Canvas.GetTop(cell);
            _dragPossible = true;
            _dragMoving = false;
            _draggingCell = cell;
            _dragButton = MouseButton.Left;
            StopRightHoldWatch(); // 换键：右键长按态作废

            CollectFreeDragCells(cell, entry.Path);
            DiagnosticLog.Trace("shell.desktop", $"点击诊断: 收集后 dragCells={_dragCells.Count}");

            cell.CaptureMouse();
            e.Handled = true;
        };

        // 右键按下（2026-09-17 改版）：选中该图标 + 启动**长按计时**（长按后拖动 = 自由摆放）；
        // 短按 / 长按未拖动 → 松手时照常弹自绘右键菜单（控件层 OnMenuServiceMouseUp）。
        cell.MouseRightButtonDown += (_, e) =>
        {
            if (!_browser.SelectedPaths.Contains(entry.Path))
            {
                _browser.SetSelection(new[] { entry.Path });
            }

            _dragStart = e.GetPosition(this);
            _dragPath = entry.Path;
            _itemOriginX = Canvas.GetLeft(cell);
            _itemOriginY = Canvas.GetTop(cell);
            _dragPossible = true;
            _dragMoving = false;
            _draggingCell = cell;
            _dragButton = MouseButton.Right;

            CollectFreeDragCells(cell, entry.Path);
            cell.CaptureMouse();
            StartRightHoldWatch(cell);
        };

        // 双击：目录导航 / 文件启动
        cell.InputBindings.Clear();
        var dbl = new MouseBinding(
            new RelayCommand(() => Open(entry)),
            new MouseGesture(MouseAction.LeftDoubleClick));
        cell.InputBindings.Add(dbl);

        // ===== 左键抬起：点击 / 左键交互拖放的收尾 =====
        //   交互拖放的落点语义已在 CompleteInteractionDrop 里处理完（DoDragDrop 返回后），
        //   这里只做临时状态清理（普通点击，或 OLE 拖放结束后补发的那次 Up 都走这里）。
        cell.MouseLeftButtonUp += (_, _) =>
        {
            HideDropIndicator();
            ClearDragState();
            _dragMoving = false;
            _dragPossible = false;
            _dragPath = null;
            _draggingCell = null;
            cell.ReleaseMouseCapture();
        };

        // ===== 右键抬起：自由摆放提交 → 松手仍弹菜单（2026-09-17 用户拍板：自由摆放与菜单都要）=====
        cell.MouseRightButtonUp += (_, e) =>
        {
            var wasDrag = _dragMoving && _dragButton == MouseButton.Right && ReferenceEquals(_draggingCell, cell);
            StopRightHoldWatch();
            FinishFreeDrag(cell);
            cell.ReleaseMouseCapture();

            if (wasDrag)
            {
                // 捕获态下 e.OriginalSource 恒为被捕获的 cell（拖动起点）→ 控件层据此弹菜单会弹错图标，
                // 故按**释放点**命中一次并吃掉冒泡，避免弹出两个菜单。
                e.Handled = true;
                ShowMenuAtControlPoint(e.GetPosition(this));
            }
        };

        // ===== 鼠标移动统一入口（2026-09-17 改版）=====
        //   ① 自由摆放进行中（右键）→ 续拖；
        //   ② 左键 = 图标之间的文件拖放（超阈值起手）；
        //   ③ 右键长按就绪（≥350ms）→ 自由摆放下手。
        cell.MouseMove += (_, e) =>
        {
            // ① 自由摆放进行中：续拖
            if (_dragMoving && _dragButton == MouseButton.Right && e.RightButton == MouseButtonState.Pressed)
            {
                UpdateFreeDrag(e.GetPosition(this));
                return;
            }

            if (!_dragPossible || !ReferenceEquals(_draggingCell, cell))
            {
                return;
            }

            var pos = e.GetPosition(this);

            // ② 左键：图标之间的文件拖放（拖到文件夹=移入 / 拖到软件=用其打开）
            if (_dragButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            {
                if (Math.Abs(pos.X - _dragStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _dragPossible = false;       // 一次按下只起手一次
                cell.ReleaseMouseCapture();  // 交给 OLE 拖放接管鼠标
                TryStartInteractionDrag(cell, pos);
                return;
            }

            // ③ 右键长按已就绪 → 自由摆放下手
            if (_dragButton == MouseButton.Right &&
                e.RightButton == MouseButtonState.Pressed &&
                _rightArmed &&
                (Math.Abs(pos.X - _dragStart.X) > 3 || Math.Abs(pos.Y - _dragStart.Y) > 3))
            {
                BeginFreeDrag(cell, pos);
            }
        };

        // 图标 cell→entry 映射：右键路由（ShowMenu）与重命名定位共用。
        _cellMenuTargets[cell] = (entry, label);

        return cell;
    }

    // ======== 手势辅助（2026-09-17 改版：左键=图标间拖放，右键长按=自由摆放） ========

    /// <summary>
    /// 收集自由摆放（右键长按拖动）的被拖集合：按下时该图标已在多选集合里（框选/Ctrl 点选的结果）
    /// → 整个选中集一起拖；仅自由布局（Canvas 父容器）可批量（需要各自的原点做相对位移）。
    /// </summary>
    private void CollectFreeDragCells(Border cell, string path)
    {
        _dragCells.Clear();
        _dragOrigins.Clear();

        if (cell.Parent is Canvas canvasParent &&
            _browser.SelectedPaths.Count > 1 &&
            _browser.SelectedPaths.Contains(path))
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
            _dragOrigins.Add((Canvas.GetLeft(cell), Canvas.GetTop(cell)));
        }
    }

    /// <summary>右键按下后起长按计时（≥350ms 且仍按住 → 进入「可自由摆放」态并给视觉提示）。</summary>
    private void StartRightHoldWatch(Border cell)
    {
        StopRightHoldWatch();
        _rightHoldCell = cell;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RightHoldMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_rightHoldTimer, timer))
            {
                _rightHoldTimer = null;
            }

            if (!ReferenceEquals(_rightHoldCell, cell) ||
                System.Windows.Input.Mouse.RightButton != MouseButtonState.Pressed)
            {
                return;
            }

            ArmRightHold(cell, armed: true);
            DiagnosticLog.Trace("shell.desktop", $"右键长按就绪（{RightHoldMs}ms）：{cell.Tag} 现可拖动自由摆放");
        };
        _rightHoldTimer = timer;
        timer.Start();
    }

    /// <summary>停掉长按计时并复位「可自由摆放」态（右键抬起 / 换键按下 / 拖动开始后）。</summary>
    private void StopRightHoldWatch()
    {
        _rightHoldTimer?.Stop();
        _rightHoldTimer = null;

        if (_rightArmed && _rightHoldCell is { } held)
        {
            ArmRightHold(held, armed: false);
        }

        _rightHoldCell = null;
        _rightArmed = false;
    }

    /// <summary>长按就绪的视觉提示（强调描边）：让用户知道"现在拖动 = 自由摆放"。</summary>
    private void ArmRightHold(Border cell, bool armed)
    {
        _rightArmed = armed;
        if (cell.Tag is not string path)
        {
            return;
        }

        SyncSelectionVisual(cell, path);
        if (armed)
        {
            cell.BorderThickness = new Thickness(1.5);
            cell.BorderBrush = ThemeBrushes.AccentTint(0.78);
        }
    }

    /// <summary>
    /// 自由摆放下手（右键长按后首次越过阈值）。自动排列下先固化瀑布布局、持久化退出自动排列，
    /// 再续接本次拖动（复用 ExitArrangeAndContinueDrag → ResumeDragAfterRebuild 的既有链路）。
    /// </summary>
    private void BeginFreeDrag(Border cell, Point pos)
    {
        if (AutoArrange)
        {
            if (!AutoExitArrangeOnDrag || _dragPath is null)
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"右键长按自由摆放：自动排列下未开启 autoExitArrangeOnDrag，忽略（path={_dragPath}）");
                _dragPossible = false;
                return;
            }

            _dragPossible = false;
            DiagnosticLog.Trace("shell.desktop", "右键长按自由摆放：自动排列 → 固化布局退出自动排列并续接拖动");
            ExitArrangeAndContinueDrag(cell, _dragPath, pos);
            return;
        }

        _dragMoving = true;
        // 记录基准快照：让位全程只是预览，移开即回滚到这里
        CaptureLayoutSnapshot();
        foreach (var fe in _dragCells)
        {
            BeginDragVisual(fe);
        }

        UpdateFreeDrag(pos);
    }

    /// <summary>自由摆放进行中：批量跟随 + 落点预览 + 让位 + 画布范围。</summary>
    private void UpdateFreeDrag(Point pos)
    {
        if (_dragCells.Count == 0)
        {
            return;
        }

        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;

        // 批量跟随：整个被拖集合保持相对位置一起移动
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
        var nx = Math.Max(0, _itemOriginX + dx);
        var ny = Math.Max(0, _itemOriginY + dy);
        var (tx, ty) = SnapTarget(nx, ny);
        ShowDropIndicator(tx, ty);
        // 临时让位：目标格上的图标往下推（未松手，不落盘）
        ApplyAvoidance();

        // ⚠️ 拖动会扩大内容范围：必须同步更新 Canvas 尺寸，
        //    否则 ScrollViewer 仍按旧内容范围裁剪，拖到右侧/下方的图标会被截断不显示。
        UpdateCanvasExtent();
    }

    /// <summary>
    /// 结束自由摆放（右键抬起）：吸附落位 + 全量落盘（含被让位图标）。
    /// 未进入拖动（纯右键点击）时只清理临时状态。
    /// 【2026-09-17】不再有"松手在回收站上 = 删除"的特判——回收站是普通图标（让位照旧、落位照旧）。
    /// </summary>
    private void FinishFreeDrag(Border? cell)
    {
        var isDrag = _dragMoving && cell is not null && ReferenceEquals(_draggingCell, cell);

        if (isDrag)
        {
            // 主 cell 吸附落位；把吸附偏移量应用到整个被拖集合，保持相对位置不变
            var cur = _livePositions.TryGetValue(cell!, out var cp)
                ? cp
                : (X: Canvas.GetLeft(cell!), Y: Canvas.GetTop(cell!));
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
    }

    /// <summary>命中控件坐标下的图标 cell（两种布局通用）；<paramref name="excludePaths"/> 排除的路径跳过。</summary>
    private Border? FindCellAt(Point pInControl, ISet<string>? excludePaths)
    {
        foreach (var cell in EnumerateAllCells())
        {
            if (cell.ActualWidth <= 0 || cell.ActualHeight <= 0)
            {
                continue;
            }

            if (excludePaths is not null && cell.Tag is string tagged && excludePaths.Contains(tagged))
            {
                continue;
            }

            Rect r;
            try
            {
                r = cell.TransformToAncestor(this)
                    .TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue; // 尚未接入可视化树（重建中）
            }

            if (r.Contains(pInControl))
            {
                return cell;
            }
        }

        return null;
    }

    /// <summary>按**控件坐标**命中的图标弹自绘菜单（自由摆放松手用：捕获态下事件源不可信）。</summary>
    private void ShowMenuAtControlPoint(Point pInControl)
    {
        var cell = FindCellAt(pInControl, excludePaths: null);
        if (cell is not null && _cellMenuTargets.TryGetValue(cell, out var mapped))
        {
            if (!_browser.SelectedPaths.Contains(mapped.Entry.Path))
            {
                _browser.SetSelection(new[] { mapped.Entry.Path });
            }

            ShowMenu(new DesktopIconTarget(mapped.Entry, cell, mapped.Label), pInControl);
            return;
        }

        ShowMenu(null, pInControl);
    }

    private BrowserEntry? FindEntryByPath(string path)
    {
        foreach (var item in _browser.Items)
        {
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    // ======== 左键拖动 = 原生桌面式（2026-09-17 改版 + 四轮补齐） ========
    // 语义（对齐原生桌面）：
    //   拖到文件夹图标  → 移入（Ctrl=复制；SHFileOperation，系统进度框 + 长路径）
    //   拖到 exe/快捷方式 → 用该程序打开这些文件（目标是文件夹快捷方式则按移入处理）
    //   拖到空白        → **把被拖图标（整组）挪到这里**：拖动期间整组**实时跟随鼠标**（与右键同一套群体动画：
    //                     跟随 + 占用者让位 + 落点指示器），松手吸附落格 + 互斥消解 + 落盘
    //   拖到自身/其它 shell 虚拟项（此电脑/回收站/网络…）→ 无操作
    //   拖到外部程序/资源管理器 → 由系统处理（OLE FileDrop，原「拖出」能力原样保留）
    // 【2026-09-17 用户拍板】回收站**没有**特殊处理：它就是普通图标，拖上去不删除（删除走右键菜单 / Delete）。
    // 与「右键长按自由摆放」的分工：左键带**投递语义**（落点是文件夹/程序时移入/打开，此时整组退回原位）；
    // 右键长按 = 纯摆放（不看落点，始终跟随）。
    // 实现：走 OLE DoDragDrop（自带系统拖拽影像）；本窗口内一律返回 Move/Copy（空白也是有效落点），
    //       松手在 OnDrop 执行 HandleInteractionDrop；DoDragDrop 返回后的兜底仅在"外部没接、自家也没接"时跑。

    private static readonly HashSet<string> AppDropExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".bat", ".cmd", ".com"
    };

    private static bool IsExecutableApp(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && AppDropExtensions.Contains(ext);
    }

    /// <summary>左键拖放起手：快照源文件 → OLE 拖放 → 落点解析（见 CompleteInteractionDrop）。</summary>
    private void TryStartInteractionDrag(Border cell, Point pos)
    {
        var paths = CollectInteractionPaths(_dragPath);
        if (paths.Count == 0)
        {
            DiagnosticLog.Trace("shell.desktop",
                $"左键拖放：源无可投递的真实文件（{_dragPath}），忽略本次拖动");
            return;
        }

        _interactionPaths = paths;
        _interactionSourceSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        _dragCopyModifier = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _internalDragActive = true;
        _internalDropHandled = false;
        _interactionCancelled = false;

        // 【2026-09-17 五轮 · 用户实测："左键视觉上是一个，但操作逻辑是群体"】
        // 左键拖动与右键自由摆放共用同一套**群体动画**：拖动期间被拖整组实时跟随鼠标 + 占用者让位 +
        // 落点指示器；悬停到可投递目标（文件夹/程序）时整组退回原位（见 UpdateInteractionHover）。
        if (Content is Canvas)
        {
            _dragMoving = true;
            CaptureLayoutSnapshot();
            foreach (var fe in _dragCells)
            {
                BeginDragVisual(fe);
            }
        }
        else
        {
            DiagnosticLog.Trace("shell.desktop",
                "左键拖放：自动排列（无画布）→ 无群体跟随动画，仅做文件投递");
        }

        DiagnosticLog.Trace("shell.desktop",
            $"左键拖放起手: files={paths.Count} first={paths[0]} from=({pos.X:F0},{pos.Y:F0}) cells={_dragCells.Count}");

        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            DragDrop.AddGiveFeedbackHandler(cell, OnInteractionGiveFeedback);
            DragDrop.AddQueryContinueDragHandler(cell, OnInteractionQueryContinueDrag);
            DragDropEffects effect;
            try
            {
                effect = DragDrop.DoDragDrop(cell, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            finally
            {
                DragDrop.RemoveGiveFeedbackHandler(cell, OnInteractionGiveFeedback);
                DragDrop.RemoveQueryContinueDragHandler(cell, OnInteractionQueryContinueDrag);
            }

            CompleteInteractionDrop(effect);
        }
        catch (Exception ex)
        {
            // 拖放失败绝不能打断桌面渲染（M10 降级 + 日志）
            DiagnosticLog.Trace("shell.desktop", $"左键拖放失败（已隔离）: {ex.Message}");
        }
        finally
        {
            _internalDragActive = false;
            _internalDropHandled = false;
            _interactionCancelled = false;
            _interactionPaths = null;
            _interactionSourceSet = null;
            SetInteractionHoverCell(null);
            HideInteractionTip();

            // 群体动画收尾（2026-09-17 五轮）：未落位就结束（外部程序接受 / Esc 取消 / 异常）→
            // 整组必须先回原位再收视觉，否则图标会"停在半空"（未落盘的下落位置会一直显示到下次重建）。
            if (_dragMoving)
            {
                ResetDraggedCellsToBase();
                foreach (var fe in _dragCells)
                {
                    EndDragVisual(fe);
                }
            }

            HideDropIndicator();
            ClearDragState();
            _dragMoving = false;
            cell.Opacity = 1;
        }
    }

    /// <summary>OLE 拖放回馈（光标移动）：实时高亮可落点。</summary>
    private void OnInteractionGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        try
        {
            if (GetCursorInControl() is { } p)
            {
                UpdateInteractionHover(p);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"左键拖放回馈失败（已隔离）: {ex.Message}");
        }
    }

    /// <summary>OLE 拖放修饰键回馈：记录 Ctrl（复制语义）——WPF 的 GiveFeedbackEventArgs 不带键状态，
    /// 键状态只在 QueryContinueDrag 上（每次修饰键/按键变化都会触发）；顺带记录 Esc 取消，
    /// 否则 Esc 取消后 DoDragDrop 返回 None，兜底路径会误判成"落在无效目标"而照做（动作仍会执行）。</summary>
    private void OnInteractionQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        _dragCopyModifier = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (e.Action == DragAction.Cancel)
        {
            _interactionCancelled = true;
            HideInteractionTip();
        }
    }

    /// <summary>
    /// 物理光标位置 → 本控件坐标。
    /// ⚠️【2026-09-17 真机实锤】OLE 拖放期间/结束后**不能**用 <c>Mouse.GetPosition</c>：
    /// WPF 的鼠标位置靠 WM_MOUSEMOVE 更新，而拖放的模态循环把消息吃掉 → 拿到的常是**拖动起点**，
    /// 于是落点恒判定在源图标附近（真机日志：三次拖放到文件夹图标都记为"落在空白处，无操作"）。
    /// 走 GetCursorPos + PointFromScreen 才是真实释放点。
    /// </summary>
    private Point? GetCursorInControl()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt))
            {
                return null;
            }

            return PointFromScreen(new Point(pt.X, pt.Y));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"光标位置换算失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>落点动作（hover 高亮 / 拖动提示 / 松手执行三处共用同一解析结果，避免漂移）。</summary>
    private enum DesktopDropAction
    {
        MoveIntoFolder,
        CopyIntoFolder,
        OpenWithApp,
    }

    /// <summary>解析结果：动作 + 目标路径（文件夹/程序）+ 原生风格提示文案。</summary>
    private sealed record DesktopDropTarget(DesktopDropAction Action, string Destination, string TipGlyph, string TipText);

    /// <summary>Ctrl = 复制语义（拖动中 GiveFeedback 持续记录 + 当前键盘兜底）。</summary>
    private bool IsCopyModifier => _dragCopyModifier || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    /// <summary>
    /// 拖动中刷新反馈（左键交互拖放）：落点高亮 + 原生风格提示浮层 + **群体跟随动画**；返回解析出的落点动作。
    /// 群体动画与右键自由摆放共用同一套实现（`UpdateFreeDrag` = 跟随 + 让位 + 落点指示器 + 画布范围）——
    /// 空白处跟随；一旦悬停到可投递目标（文件夹/程序）→ 整组退回原位（文件将被移入/打开，不该留摆放预览）。
    /// </summary>
    private DesktopDropTarget? UpdateInteractionHover(Point pInControl)
    {
        var target = ResolveInteractionDropTarget(pInControl, out var hoverCell);
        SetInteractionHoverCell(target is null ? null : hoverCell);
        UpdateInteractionTip(target, pInControl);

        if (_dragMoving)
        {
            if (target is null)
            {
                UpdateFreeDrag(pInControl);
            }
            else
            {
                ResetDraggedCellsToBase();
                HideDropIndicator();
            }
        }

        return target;
    }

    /// <summary>
    /// 把被拖整组瞬移回基准位（并把被让位者一并复位）。
    /// 用途：① 悬停到可投递目标时撤回摆放预览；② 拖放结束时未落位（外部接受 / Esc / 异常）的收尾。
    /// </summary>
    private void ResetDraggedCellsToBase()
    {
        if (_basePositions is null)
        {
            return;
        }

        foreach (var fe in _dragCells)
        {
            if (!_basePositions.TryGetValue(fe, out var bp))
            {
                continue;
            }

            Canvas.SetLeft(fe, bp.X);
            Canvas.SetTop(fe, bp.Y);
            _livePositions[fe] = bp;
        }

        // 被让位者一并回位（RestoreBasePositions 只处理"被拖集合之外"的图标）
        RestoreBasePositions(new HashSet<FrameworkElement>(_dragCells), animate: false);
        _lastAvoidTargets = null; // 让位目标记忆作废：回到同一格时才会重新让位
    }

    /// <summary>
    /// 按控件坐标解析"左键拖放到这里会做什么"（对齐原生语义）：
    /// 文件夹 → 移入（Ctrl=复制）；文件夹快捷方式 → 移入其目标；exe/快捷方式 → 用该程序打开；
    /// 其余（空白 / 自身 / 非目标文件 / 其它 shell 虚拟项如回收站·此电脑）→ null（不接收 = 光标"禁止"）。
    /// 【2026-09-17 用户拍板】回收站不再特判：它走普通图标判断链，拖上去无动作。
    /// </summary>
    /// <param name="hoverCell">命中的图标 cell（空白为 null），供高亮复用。</param>
    private DesktopDropTarget? ResolveInteractionDropTarget(Point pInControl, out Border? hoverCell)
    {
        hoverCell = null;

        // 自绘桌面上的图标落点（排除拖放源自身/被拖集合）
        var cell = FindCellAt(pInControl, _interactionSourceSet);
        if (cell?.Tag is not string targetPath)
        {
            return null;
        }

        hoverCell = cell;

        var entry = FindEntryByPath(targetPath);
        if (entry is null)
        {
            return null;
        }

        var copy = IsCopyModifier;

        // 文件夹 → 移入
        if (entry.IsDirectory && !entry.IsShellNamespace)
        {
            return FolderDropTarget(entry.Path, entry.Name, copy);
        }

        // 快捷方式：目标是文件夹 → 移入（原生：拖到"文件夹快捷方式"上是移动进去，不是启动它）
        var lnkTarget = ResolveLnkTarget(entry.Path);
        if (lnkTarget is not null && Directory.Exists(lnkTarget))
        {
            return FolderDropTarget(lnkTarget, entry.Name, copy);
        }

        // 可执行/快捷方式 → 用该程序打开
        if (IsExecutableApp(entry.Path))
        {
            var appName = Path.GetFileNameWithoutExtension(entry.Name);
            return new DesktopDropTarget(DesktopDropAction.OpenWithApp, entry.Path, "+", $"用 {appName} 打开");
        }

        return null;
    }

    private static DesktopDropTarget FolderDropTarget(string folder, string displayName, bool copy)
        => new(copy ? DesktopDropAction.CopyIntoFolder : DesktopDropAction.MoveIntoFolder,
               folder,
               "→",
               $"{(copy ? "复制到" : "移动到")} {Path.GetFileNameWithoutExtension(displayName)}");

    /// <summary>
    /// 左键拖放松手的统一分派（OnDrop 与 DoDragDrop 返回后的兜底共用）：
    /// ① 落点是文件夹 / 程序图标（含文件夹快捷方式）→ 交互（移入 / 用其打开），并先把群体动画撤回原位；
    /// ② 落点是空白 → **整组就落在跟随动画停下的位置**（原生桌面手感；2026-09-17 四轮补：用户实测
    ///   "左键没有成功"——按住整组拖到旁边撒手时原来什么都不发生，因为落到源图标/空白被一律判"无操作"）；
    /// ③ 落点是自身 / 回收站等非目标虚拟项 → 同 ②（跟随停在哪就落在哪，位移为 0 时不写盘）。
    /// </summary>
    private void HandleInteractionDrop(Point pInControl, IReadOnlyList<string> paths)
    {
        var target = ResolveInteractionDropTarget(pInControl, out _);
        if (target is not null)
        {
            // 投递（移入/打开）不是摆放：先把群体动画撤回原位——"用程序打开"时文件本身不动，位置必须还原；
            // "移入文件夹"时文件被移走会触发刷新重建，也以原位为基准最稳（避免残留半空位置）。
            ResetDraggedCellsToBase();
            ExecuteInteractionDrop(target, paths);
            return;
        }

        MoveDragSelectionToPlace(pInControl);
    }

    /// <summary>
    /// 左键拖到空白处松手 = 落位：拖动期间被拖整组已实时跟随（与右键自由摆放**同一套群体动画**），
    /// 故这里直接复用自由摆放的收尾：主拖 cell 吸附落格 → 整组按同偏移平移 → 互斥消解 → 一次性落盘。
    /// 无画布（自动排列）时既无跟随动画也无从落位 → 只记日志。
    /// </summary>
    private void MoveDragSelectionToPlace(Point pInControl)
    {
        _ = pInControl; // 落点位置已在跟随过程中确定（与右键自由摆放一致），此处不再需要坐标

        if (_dragMoving && _draggingCell is Border draggedCell)
        {
            FinishFreeDrag(draggedCell);
            return;
        }

        DiagnosticLog.Trace("shell.desktop",
            "左键拖放：无跟随画布（自动排列）或未进入拖动态 → 不做落位");
    }

    /// <summary>执行落点动作（OnDrop 与 DoDragDrop 返回后的兜底共用同一实现）。</summary>
    private void ExecuteInteractionDrop(DesktopDropTarget target, IReadOnlyList<string> paths)
    {
        DiagnosticLog.Trace("shell.desktop",
            $"左键拖放 → {target.TipText}: files={paths.Count} dst={target.Destination}");

        switch (target.Action)
        {
            case DesktopDropAction.CopyIntoFolder:
                MoveIntoFolder(paths, target.Destination, copy: true);
                break;
            case DesktopDropAction.MoveIntoFolder:
                MoveIntoFolder(paths, target.Destination, copy: false);
                break;
            case DesktopDropAction.OpenWithApp:
                OpenFilesWith(target.Destination, paths);
                break;
        }
    }

    // ===== 原生风格拖动提示（跟随光标的"移动到 xxx / 用 xxx 打开"浮层；2026-09-17 用户要求对齐原生）=====
    // 关键：画在**自己的窗口**里的 Popup（不是新开一个顶层 HWND），因此不会成为 OLE 的落点窗口、
    //       不会把光标在提示上变成"禁止"；IsHitTestVisible=false 不参与命中。

    private void UpdateInteractionTip(DesktopDropTarget? target, Point pInControl)
    {
        if (target is null)
        {
            HideInteractionTip();
            return;
        }

        EnsureInteractionTip();
        if (_interactionTip is null || _interactionTipGlyph is null || _interactionTipText is null)
        {
            return;
        }

        if (_interactionTipGlyph.Text != target.TipGlyph)
        {
            _interactionTipGlyph.Text = target.TipGlyph;
        }

        if (_interactionTipText.Text != target.TipText)
        {
            _interactionTipText.Text = target.TipText;
        }

        // 原生位置：光标右下方一点（避免压住光标本身，也避免挡住落点图标）
        _interactionTip.HorizontalOffset = pInControl.X + 18;
        _interactionTip.VerticalOffset = pInControl.Y + 20;
        if (!_interactionTip.IsOpen)
        {
            ApplyInteractionTipTheme(); // 每次弹出重取主题令牌（跟随外观模式切换）
            _interactionTip.IsOpen = true;
            MakeInteractionTipClickThrough();
        }
    }

    /// <summary>提示浮层窗口置为 OS 级点击穿透（WS_EX_TRANSPARENT）：鼠标扫过它时 OLE 仍判定落点在下方
    /// （本桌面窗口），不会出现"光标划过提示 → 一瞬间变禁止符号"的抖动。</summary>
    private void MakeInteractionTipClickThrough()
    {
        try
        {
            if (_interactionTipBody is not null &&
                PresentationSource.FromVisual(_interactionTipBody) is HwndSource source)
            {
                BetterDesktop.Shell.Core.Windowing.ClickThroughWindow.SetClickThrough(
                    source.Handle, clickThrough: true, keepNoActivate: true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"拖动提示点击穿透设置失败（已隔离）: {ex.Message}");
        }
    }

    private void HideInteractionTip()
    {
        if (_interactionTip is not null && _interactionTip.IsOpen)
        {
            _interactionTip.IsOpen = false;
        }
    }

    private void EnsureInteractionTip()
    {
        if (_interactionTip is not null)
        {
            return;
        }

        _interactionTipGlyph = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _interactionTipText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 320,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_interactionTipGlyph);
        row.Children.Add(_interactionTipText);

        _interactionTipBody = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 6, 13, 6),
            BorderThickness = new Thickness(1),
            Child = row,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.45,
            },
        };

        _interactionTip = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None,
            Child = _interactionTipBody,
        };

        ApplyInteractionTipTheme();
    }

    /// <summary>提示浮层取色（全走主题令牌：PopupBackground/PopupBorder/ThemeForeground + 强调色字形）。</summary>
    private void ApplyInteractionTipTheme()
    {
        if (_interactionTipBody is null || _interactionTipGlyph is null || _interactionTipText is null)
        {
            return;
        }

        _interactionTipBody.Background = ThemeBrushes.Get("PopupBackground", new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
        _interactionTipBody.BorderBrush = ThemeBrushes.Get("PopupBorder", new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)));
        _interactionTipText.Foreground = ThemeBrushes.Get("ThemeForeground", Brushes.White);
        _interactionTipGlyph.Foreground = ThemeBrushes.AccentTint(0.95);
    }

    private void SetInteractionHoverCell(Border? cell)
    {
        if (ReferenceEquals(_interactionHoverCell, cell))
        {
            return;
        }

        if (_interactionHoverCell is { } prev && prev.Tag is string prevPath)
        {
            SyncSelectionVisual(prev, prevPath); // 还原常态/选中态
        }

        _interactionHoverCell = cell;
        if (cell is not null)
        {
            cell.BorderThickness = new Thickness(1.5);
            cell.BorderBrush = ThemeBrushes.AccentTint(0.86);
            cell.Background = ThemeBrushes.AccentTint(0.22);
        }
    }

    /// <summary>
    /// DoDragDrop 返回后的兜底（自家 OnDrop 未触达时才走到这里）：
    /// ① 自家 OnDrop 已执行 → 结束；② 外部程序/资源管理器接受了拖放（effect != None）→ 交给它，不重复处理；
    /// ③ 否则按**物理光标**兜底解析落点并执行（dock 回收站 / 图标落点；见 GetCursorInControl 的真机说明）。
    /// </summary>
    private void CompleteInteractionDrop(DragDropEffects effect)
    {
        var paths = _interactionPaths;
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (_internalDropHandled)
        {
            return; // 自家 OnDrop 已处理（不重复执行）
        }

        if (_interactionCancelled)
        {
            DiagnosticLog.Trace("shell.desktop", "左键拖放：已取消（Esc），无操作");
            return;
        }


        if (effect != DragDropEffects.None)
        {
            DiagnosticLog.Trace("shell.desktop",
                $"左键拖放：{effect} 由外部目标处理（不重复执行内部动作）");
            return;
        }

        // 遮挡闸（2026-09-17）：光标不在本控件窗口上（落在 dock / 普通窗口 / 任务栏等）→ 兜底不执行，
        // 否则会按坐标投递到被遮住、用户根本看不见的图标上（`_internalDragActive` 期间本窗口不会被 OLE 命中，
        // 但坐标空间仍然有效，属"看不见的目标"风险面）。
        if (!IsCursorOverSelf())
        {
            DiagnosticLog.Trace("shell.desktop", "左键拖放：光标不在桌面层（被其它窗口遮挡），无操作");
            return;
        }

        var cursor = GetCursorInControl();
        if (cursor is null)
        {
            DiagnosticLog.Trace("shell.desktop", "左键拖放：取不到光标位置，无操作");
            return;
        }

        HandleInteractionDrop(cursor.Value, paths);
    }

    /// <summary>左键拖放的源集合：多选（且按下项在选中集内）→ 整集，否则只拖按下这一个；
    /// 只保留**磁盘上真实存在**的路径（shell 虚拟项 "::{CLSID}" 参与 FileDrop 会投毒）。</summary>
    private List<string> CollectInteractionPaths(string? pressedPath)
    {
        if (string.IsNullOrWhiteSpace(pressedPath))
        {
            return new List<string>();
        }

        IEnumerable<string> raw = _browser.SelectedPaths.Count > 1 && _browser.SelectedPaths.Contains(pressedPath)
            ? _browser.SelectedPaths
            : new[] { pressedPath };

        return raw
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把文件移入目标文件夹（<paramref name="copy"/>=true 走复制）：SHFileOperation，完成后刷新网格。</summary>
    private void MoveIntoFolder(IReadOnlyList<string> paths, string folder, bool copy)
    {
        // 护栏（2026-09-17 真机修正：首版把"源的父目录"当成"源目录"，导致桌面上任何文件夹都被误判为自嵌套）：
        //   ① 目标 == 源所在目录 → 文件已经在这里，无操作（原生同款）；
        //   ② 源是文件夹且「目标 == 源」或「目标在源之内」→ 自嵌套，拒绝（否则会把文件夹搬进自己）。
        var sep = Path.DirectorySeparatorChar;
        var folderTrim = Path.GetFullPath(folder).TrimEnd(sep);
        foreach (var src in paths)
        {
            string srcFull;
            try
            {
                srcFull = Path.GetFullPath(src).TrimEnd(sep);
            }
            catch
            {
                continue;
            }

            var srcParent = Path.GetDirectoryName(srcFull)?.TrimEnd(sep);
            if (string.Equals(srcParent, folderTrim, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"左键拖放：{src} 已位于目标目录 {folder}，无操作");
                return;
            }

            if (Directory.Exists(srcFull) &&
                (string.Equals(srcFull, folderTrim, StringComparison.OrdinalIgnoreCase) ||
                 folderTrim.StartsWith(srcFull + sep, StringComparison.OrdinalIgnoreCase)))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"左键拖放：目标 {folder} 为源文件夹 {src} 的自身/子目录，已跳过");
                return;
            }
        }

        DiagnosticLog.Trace("shell.desktop",
            $"左键拖放 → 文件夹: files={paths.Count} copy={copy} dst={folder}");

        _ = Task.Run(() =>
        {
            try
            {
                if (copy)
                {
                    FileClipboard.Copy(paths, folder);
                }
                else
                {
                    FileClipboard.Move(paths, folder);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.desktop", $"左键拖放到文件夹失败: {ex.Message}");
            }

            _ = Dispatcher.BeginInvoke(() => _browser.Refresh());
        });
    }

    /// <summary>解析 .lnk 的真实目标路径；非 .lnk / 解析失败返回 null（调用方自行回落）。</summary>
    private static string? ResolveLnkTarget(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var resolved = BetterDesktop.Shell.AppSource.Services.ShellLinkResolver.Resolve(path).TargetPath;
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"快捷方式解析失败（回落直接启动 .lnk）: {ex.Message}");
            return null;
        }
    }

    /// <summary>用指定程序打开这些文件（拖到软件图标上松手）；.lnk 先经 ShellLinkResolver 解析真实目标。</summary>
    private void OpenFilesWith(string appPath, IReadOnlyList<string> files)
    {
        var exe = ResolveLnkTarget(appPath) ?? appPath;
        var args = string.Join(" ", files.Select(f => "\"" + f + "\""));
        DiagnosticLog.Trace("shell.desktop",
            $"左键拖放 → 用程序打开: app={exe} files={files.Count}");

        _ = Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                };
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    psi.WorkingDirectory = dir;
                }

                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.desktop", $"用程序打开失败: app={exe} err={ex.Message}");
            }
        });
    }

    /// <summary>
    /// 物理光标当前是否落在**本控件自己的窗口**上（用于兜底路径的安全闸）。
    /// 自绘桌面窗口是 DefView 的子窗口 → 任何普通窗口（含 dock）都盖在它上面；此时光标坐标在控件空间里
    /// 可能正好压着某个**被遮住**的图标，兜底若照做就会"投递到看不见的图标"（真机隐患）。
    /// 判法：WindowFromPoint 拿到光标下的窗口，沿父链上溯看是否命中本控件所在的 HWND。
    /// </summary>
    private bool IsCursorOverSelf()
    {
        try
        {
            var self = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (self == IntPtr.Zero || !NativeMethods.GetCursorPos(out var pt))
            {
                return false;
            }

            var hwnd = WindowFromPoint(new NativePoint { X = pt.X, Y = pt.Y });
            for (var i = 0; i < 16 && hwnd != IntPtr.Zero; i++)
            {
                if (hwnd == self)
                {
                    return true;
                }

                hwnd = GetParent(hwnd);
            }

            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"落点遮挡判定失败（保守放行）: {ex.Message}");
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

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
            // 【2026-09-07 修复】必须同时排除 _rubberBand（框选橡皮筋）：
            // 它平时从未 SetLeft/SetTop → GetLeft=UnsetValue(转 double=NaN)。
            // 若混入快照，RestoreBasePositions → AnimateTo(fe, NaN, NaN) 抛
            // ArgumentException(""NaN"不是属性"From"的有效值")，连续 Dispatcher
            // 异常把自绘层打挂（实测框选后拖动 → 桌面图标消失、explorer 透出）。
            if (child is not FrameworkElement fe ||
                ReferenceEquals(fe, _dropIndicator) ||
                ReferenceEquals(fe, _rubberBand))
            {
                continue;
            }

            var pos = (X: Canvas.GetLeft(fe), Y: Canvas.GetTop(fe));
            if (double.IsNaN(pos.X) || double.IsNaN(pos.Y))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"CaptureLayoutSnapshot 跳过 NaN: el={fe.Tag ?? "(null)"}");
                continue;
            }
            _basePositions[fe] = pos;
            _livePositions[fe] = pos;
        }
    }

    /// <summary>把所有被让位的图标回滚到基准位置（被拖集合不回滚，它们跟随鼠标）。</summary>
    /// <param name="animate">true=平滑回滚（松手场景）；false=瞬移（拖动中每跨一格回滚，
    /// 动画会让 76 个图标高频重起 DoubleAnimation → 卡顿）。</param>
    private void RestoreBasePositions(ISet<FrameworkElement> draggedSet, bool animate = true)
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

            // 【2026-09-07 保险】快照目标为 NaN（理论已被 CaptureLayoutSnapshot 拦截）：
            // 跳过，避免 AnimateTo(NaN) 抛异常
            if (double.IsNaN(kv.Value.X) || double.IsNaN(kv.Value.Y))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"RestoreBasePositions 跳过 NaN 目标: el={fe.Tag ?? "(null)"}");
                continue;
            }

            // 位置没变就不必重起动画：否则每次目标格变化都给全部图标刷新动画，既浪费又抖
            if (Math.Abs(cur.X - kv.Value.X) < 1 && Math.Abs(cur.Y - kv.Value.Y) < 1)
            {
                continue;
            }

            if (animate)
            {
                AnimateTo(fe, kv.Value.X, kv.Value.Y, _dragMoving ? 80 : 160);
            }
            else
            {
                Canvas.SetLeft(fe, kv.Value.X);
                Canvas.SetTop(fe, kv.Value.Y);
            }
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

        // 1) 先撤销上一次让位（回到拖动开始时的排布）。
        //    拖动中瞬移：每跨一格就回滚重推，动画会让位视觉上是"整排重排"，卡且跳。
        RestoreBasePositions(draggedSet, animate: !_dragMoving);

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
                AnimateTo(fe, nx, ny, _dragMoving ? 80 : 160);
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

    /// <summary>
    /// 【2026-09-06 功能】自动排列模式下拖动图标 → 固化当前瀑布布局到 desktop.iconPositions、
    /// 持久化退出自动排列（desktop.autoArrange=false）、重建为自由布局（位置不变 → 视觉不变），
    /// 并续接本次拖动（explorer 同款：拖一个图标即切到手动排布，布局保持不变）。
    /// </summary>
    private void ExitArrangeAndContinueDrag(Border cell, string dragPath, System.Windows.Point pos)
    {
        if (_settings is null || _disposed)
        {
            return;
        }

        // 1. 固化当前自动排列布局（与 RebuildFreeLayout 同基准的列优先分配）→ desktop.iconPositions
        // 【回归修复 2026-09-06】用 ViewportHeight 而非 ActualHeight：自动排列 WrapPanel 的
        // 实际可用高度是 ScrollViewer 内容区（扣除水平滚动条），ActualHeight 在水平滚动条
        // 出现时偏大 → 固化坐标时每列行数偏多 → 列数偏少（用户实测关闭自动排列后列数跳变）。
        // 低于两行高度视为不可信，回退到屏幕工作区高度。
        var available = ViewportHeight > CellHeight * 2 ? ViewportHeight : SystemParameters.WorkArea.Height - 80;
        var rowsPerColumn = Math.Max(1, (int)Math.Floor(available / CellHeight));
        var all = new Dictionary<string, double[]>();
        var i = 0;
        foreach (var entry in _browser.Items)
        {
            var col = i / rowsPerColumn;
            var row = i % rowsPerColumn;
            i++;
            all[entry.Path] = new[] { col * CellWidth, row * CellHeight };
        }
        _settings.Set("desktop.iconPositions", all);

        // 2. 退出自动排列（持久化）→ DesktopWindow.OnSettingsChanged 对 desktop.* 键
        //    Dispatcher.BeginInvoke(Rebuild) 排队重建为自由布局。
        //    ⚠️【2026-09-07 竞态修复】不得再同步 Rebuild()：若此处同步重建并续接拖动，
        //    随后排队的异步 Rebuild 的 ClearDragState 会把续接状态清掉 → 拖动中断/状态错乱
        //    （"拖动图标时桌面崩溃/闪回"的关联因素）。续接同样 BeginInvoke 排队：
        //    顺序保证 Rebuild 先执行、ResumeDragAfterRebuild 后执行。
        _settings.Set("desktop.autoArrange", false);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
            {
                return;
            }
            ResumeDragAfterRebuild(dragPath, pos);
        }));
    }

    /// <summary>自动排列 → 自由布局重建完成后续接本次拖动（Rebuild 的 ClearDragState 已清旧态，
    /// 此处在重建后的新网格上恢复拖动上下文；与 MouseLeftButtonUp 的清理互斥由 Dispatcher 队列保证）。</summary>
    private void ResumeDragAfterRebuild(string dragPath, System.Windows.Point pos)
    {
        // 续接本次拖动：定位被拖图标的新 cell，恢复拖动上下文（自由布局 MouseMove 续接）
        var newCell = FindCellByPath(dragPath);
        if (newCell is null)
        {
            return;
        }

        _dragStart = pos;
        _dragPath = dragPath;
        _itemOriginX = Canvas.GetLeft(newCell);
        _itemOriginY = Canvas.GetTop(newCell);
        _dragPossible = true;
        _dragMoving = false;
        _draggingCell = newCell;

        _dragCells.Clear();
        _dragOrigins.Clear();
        if (_browser.SelectedPaths.Count > 1 && _browser.SelectedPaths.Contains(dragPath))
        {
            foreach (var fe in EnumerateCells())
            {
                if (fe.Tag is string p && _browser.SelectedPaths.Contains(p))
                {
                    _dragCells.Add(fe);
                    _dragOrigins.Add((Canvas.GetLeft(fe), Canvas.GetTop(fe)));
                }
            }
        }
        if (_dragCells.Count == 0)
        {
            _dragCells.Add(newCell);
            _dragOrigins.Add((_itemOriginX, _itemOriginY));
        }

        // 重建后旧 cell 已销毁，鼠标捕获需在新 cell 上重建（保持后续 Move/Up 到达）
        newCell.CaptureMouse();
    }

    /// <summary>在自由布局 Canvas 中按完整路径查找图标 cell。</summary>
    private Border? FindCellByPath(string path)
    {
        if (Content is not Canvas canvas)
        {
            return null;
        }

        foreach (var child in canvas.Children)
        {
            if (child is Border fe && fe.Tag is string p &&
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            {
                return fe;
            }
        }

        return null;
    }

    /// <summary>
    /// 清掉"只在交互期间存在"的覆盖层：框选橡皮筋 + 拖动落点指示器。
    /// 见框选段末注释——这两个覆盖层过去只有 MouseLeftButtonUp 一个清理点，
    /// 一旦 Up 丢失就会在桌面留下细小线框（2026-09-16 用户实测）。
    /// </summary>
    /// <summary>框选看门狗当前作用的橡皮筋画布（Tick 处理器构造期只挂一次，故画布经字段传递）。</summary>
    private Canvas? _rubberWatchdogCanvas;

    /// <summary>框选期间起看门狗（500 ms 检查物理左键；松开即自愈），空闲期不占表。</summary>
    private void StartRubberWatchdog(Canvas canvas)
    {
        _rubberWatchdogCanvas = canvas;

        if (_rubberWatchdog is null)
        {
            _rubberWatchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            // 【2026-09-18】处理器**只挂一次**：原实现在每次 Start 里 `Tick += lambda`，
            // 而 Tick 是事件、Stop 不会减订阅 → "每框选一次就多挂一个回调、永不释放"，
            // 长会话下同一次 Tick 会重复执行 N 次（越用越重）。改为命名方法 + 幂等挂载。
            _rubberWatchdog.Tick += OnRubberWatchdogTick;
        }

        if (!_rubberWatchdog.IsEnabled)
        {
            _rubberWatchdog.Start();
        }
    }

    private void OnRubberWatchdogTick(object? sender, EventArgs e)
    {
        if (_rubberActive && Mouse.LeftButton == MouseButtonState.Released)
        {
            ClearTransientOverlays(_rubberWatchdogCanvas);
            DiagnosticLog.Trace("shell.desktop", "框选橡皮筋自愈：Up 丢失，已清理（2026-09-16 细线残留事故的兜底）");
        }
    }

    private void ClearTransientOverlays(Canvas? canvas)
    {
        try
        {
            _rubberActive = false;
            _rubberWatchdog?.Stop();
            if (_rubberBand is not null)
            {
                _rubberBand.Visibility = Visibility.Collapsed;
            }

            if (_dropIndicator is not null)
            {
                _dropIndicator.Visibility = Visibility.Collapsed;
            }

            if (canvas is not null && canvas.IsMouseCaptured)
            {
                canvas.ReleaseMouseCapture();
            }
        }
        catch (Exception ex)
        {
            // 清理失败绝不能打断桌面渲染（M10 降级）
            DiagnosticLog.Trace("shell.desktop", $"临时覆盖层清理失败（已隔离）：{ex.Message}");
        }
    }

    /// <summary>枚举自由布局 Canvas 中的图标 cell（不含指示器/橡皮筋）。</summary>
    private IEnumerable<Border> EnumerateCells()
    {
        if (Content is not Canvas canvas)
        {
            yield break;
        }

        foreach (var child in canvas.Children)
        {
            if (child is Border fe &&
                !ReferenceEquals(fe, _dropIndicator) &&
                !ReferenceEquals(fe, _rubberBand) &&
                fe.Tag is string)
            {
                yield return fe;
            }
        }
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
        // 交互拖放的高亮 cell 可能已被重建销毁：直接弃引用（重挂到新网格后由下次 hover 重建）
        _interactionHoverCell = null;
    }

    /// <summary>平滑位移动画（用于避让）：动画 Canvas.Left/Top 两个附加属性。</summary>
    /// <param name="durationMs">动画时长。拖动中让位用短时长（80ms）保持跟手，
    /// 松手后落位/消解用 160ms 平滑。</param>
    private static void AnimateTo(FrameworkElement element, double x, double y, int durationMs = 160)
    {
        var curX = (double)element.GetValue(Canvas.LeftProperty);
        var curY = (double)element.GetValue(Canvas.TopProperty);

        // 【2026-09-07 修复】cell 基值可能为 UnsetValue(转 double = NaN)：
        // DoubleAnimation(NaN, x) 抛 ArgumentException(""NaN"不是属性"From"的有效值")，
        // DispatcherUnhandledException 连续触发会把自绘层打挂（实测拖动/框选后桌面图标消失）。
        // 防御：NaN → 直接用目标值（跳过动画），并记录来源便于排查。
        if (double.IsNaN(curX) || double.IsNaN(curY))
        {
            DiagnosticLog.Trace("shell.desktop",
                $"AnimateTo NaN 防御: el={element.Tag} cur=({curX},{curY}) dst=({x},{y})");
            if (double.IsNaN(curX)) curX = x;
            if (double.IsNaN(curY)) curY = y;
        }

        // 基值立即指向目标值：让 Canvas.GetLeft/GetTop 与逻辑位置始终一致
        element.SetValue(Canvas.LeftProperty, x);
        element.SetValue(Canvas.TopProperty, y);

        var duration = TimeSpan.FromMilliseconds(durationMs);
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
            ? ThemeBrushes.AccentTint(0.27)
            : Brushes.Transparent;
        cell.BorderThickness = new Thickness(selected ? 1 : 0);
        cell.BorderBrush = ThemeBrushes.AccentTint(0.47);
    }

    // ======== 打开 / 文件操作 ========

    private void Open(BrowserEntry entry)
    {
        // 【2026-09-07 用户拍板】自绘桌面本质是类文件管理器 → "打开"统一定位为
        // 系统文件管理器（explorer.exe）：文件夹与 shell 虚拟项（此电脑/回收站等）
        // 都用 explorer 打开（与 dock 系统区同款）；文件用默认关联程序打开。
        if (entry.IsShellNamespace)
        {
            StartFileShellNamespace(entry.Path);
        }
        else if (entry.IsDirectory) StartExplorerFolder(entry.Path);
        else StartFile(entry.Path);
    }

    /// <summary>经系统文件管理器（explorer.exe）打开文件夹。</summary>
    private static void StartExplorerFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // 启动失败静默（M10）
        }
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
            LaunchedWindowPresenter.GrantForegroundToNextApp();
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            // 交接给"已运行的隐藏实例"的应用（WPS 实证）会把文档开在不可见窗口里 → 叫出来
            LaunchedWindowPresenter.EnsurePresented(appPathHint: null, path);
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

    /// <summary>
    /// SHOP_FILEPATH = 0x2（本机 SDK 实证：Windows Kits 10.0.26100.0\um\ShlObj_core.h:2378
    /// <c>#define SHOP_FILEPATH 0x00000002</c>；同处定义 SHOP_PRINTERNAME=1 / SHOP_VOLUMEGUID=4）。
    /// <para>
    /// 【2026-09-17 用户实测"点属性没反应"的根因】此前这里传的是 <c>0</c>——0 不是任何合法
    /// shopObjectType，SHObjectProperties 不做任何事直接返回 FALSE（不抛异常、不弹窗），
    /// 被 M10 静默 catch 吞掉后用户侧就是"点了完全没反应"。
    /// </para>
    /// </summary>
    private const uint ShopFilepath = 0x00000002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHObjectProperties(IntPtr hwnd, uint stype, string pszObject, string? pszPage);

    /// <summary>
    /// 打开文件/文件夹属性对话框。
    /// <para>
    /// 主路径 = shell 原生 <c>properties</c> 动词（与 explorer 右键「属性」同源，
    /// 也与应用提取器 <c>AppEntryActions.ShowProperties</c> 同一实现口径：文件/目录/虚拟项通吃）；
    /// 失败才退回 SHObjectProperties（本进程内直调，需正确的 SHOP_FILEPATH）。
    /// </para>
    /// </summary>
    private void ShowProperties(string path)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "properties"
            });
            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"属性（shell properties 动词）失败 {path}: {ex.Message} → 回退 SHObjectProperties");
        }

        try
        {
            var hwnd = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (SHObjectProperties(hwnd, ShopFilepath, path, null) == 0)
            {
                DiagnosticLog.Trace("shell.desktop", $"属性（SHObjectProperties）返回失败 {path}（stype=SHOP_FILEPATH）");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"属性页打开失败 {path}: {ex.Message}");
        }
    }

    // 2026-09-05 曾随中央菜单管线退役旧自绘右键菜单（BuildIconMenu/BuildBlankMenu，DesktopMenuStyling 工厂）；
    // 2026-09-07 用户拍板回归自绘（ShowMenu → DesktopMenuPopup），此前的退役说明作废。

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
        // 【2026-09-17 用户反馈修正】左键交互拖放：按落点解析语义并**给出效果**——
        // 一律返回 None 会让光标恒为"禁止"符号（用户实测："移到另一个上面都是禁止的符号，十分不对"）。
        // 现在：有效落点（文件夹/程序/回收站）→ Move（Ctrl=Copy）→ 原生移动/复制光标 + 提示浮层；
        //       空白/自身/非目标 → None（此处"禁止"才是诚实的）。
        // 注意：返回非 None 意味着松手会在本窗口触发 OnDrop，由它执行内部动作（见 OnDrop 的 _internalDragActive 分支）。
        if (_internalDragActive)
        {
            var target = UpdateInteractionHover(e.GetPosition(this));
            // 【2026-09-17 四轮】空白也是有效落点（= 原生式"把图标挪到这里"），故本窗口内一律 Move；
            // 落点是文件夹且按住 Ctrl 时给 Copy。禁止符号只应出现在窗口外的无效目标上。
            e.Effects = target is not null && target.Action == DesktopDropAction.CopyIntoFolder
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

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
        // 内部左键拖放：在本窗口内松手 → 执行解析出的动作（移入 / Ctrl 复制 / 用程序打开 / 回收站）
        if (_internalDragActive)
        {
            e.Handled = true;
            var sourcePaths = _interactionPaths;
            var dropPoint = e.GetPosition(this);
            HideInteractionTip();
            if (sourcePaths is null || sourcePaths.Count == 0)
            {
                return;
            }

            _internalDropHandled = true;
            HandleInteractionDrop(dropPoint, sourcePaths);
            return;
        }

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

    /// <summary>F10/O2：命名方法订阅（可退订），ItemsChanged → 重建图标网格。</summary>
    private void OnBrowserItemsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(Rebuild);

    /// <summary>F10/O2：命名方法订阅（可退订），图标大小/间距变化 → 自动紧凑重排补位。</summary>
    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (e.Key is "desktop.iconSize" or "desktop.itemSpacingX" or "desktop.itemSpacingY")
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, CompactLayout);
        }
    }

    public void Dispose()
    {
        // 7438 纪律 4：Dispose 幂等守卫 + 订阅退订配对（长生命周期服务持住控件 → 旧实例无法 GC）。
        if (_disposed) return;
        _disposed = true;
        _browser.ItemsChanged -= OnBrowserItemsChanged;
        _settingsSub?.Dispose();
        _cellMenuTargets.Clear();
        HideInteractionTip();
        _interactionTip = null;
        Content = null;
    }

    // ===== 统一右键菜单路由（2026-09-05 收口后唯一路径）：
    //   本区域只构建菜单项定义（BuildIconMenuEntries 图标 / BuildBackgroundMenuEntries 空白），
    //   渲染交给 shell-desktop 的 DesktopMenuPopup（WPF 自绘菜单；2026-09-07 回归自绘）。 =====

    private void OnMenuServiceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_disposed)
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
            ShowMenu(new DesktopIconTarget(entry, cell, label), e.GetPosition(this));
        }
        else
        {
            ShowMenu(null, e.GetPosition(this));
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

    /// <summary>弹自绘菜单。<paramref name="pInControl"/> = 控件坐标下的弹出点
    /// （普通右键取事件位置；自由摆放松手取鼠标位置——捕获态下事件源不可信）。</summary>
    private void ShowMenu(DesktopIconTarget? target, Point pInControl)
    {
        try
        {
            // 【2026-09-07 用户拍板】回归自绘右键菜单：桌面空白与图标条目一律自绘菜单
            //（WPF ContextMenu，零 IContextMenu 依赖）。系统原生菜单接入（跨进程委托 explorer /
            // DefView 转发）退役——类型虽正确但稳定性不达标（拖动关联崩溃、菜单类型错乱等），
            // 且本机任何非 explorer 进程 GetUIObjectOf 聚合第三方扩展必然崩溃（0xC0000005 实锤）。
            // 自绘菜单仅基础操作（打开/剪切/复制/删除/重命名/属性/新建/刷新/排序），
            // 不含第三方 shell 扩展项——这是回归自绘的明确代价（用户已确认接受）。
            var physical = PointToScreen(pInControl);
            var entries = target is null ? BuildBackgroundMenuEntries() : BuildIconMenuEntries(target);
            if (entries.Count == 0)
            {
                DiagnosticLog.Trace("shell.desktop", "右键菜单：无可用项");
                return;
            }
            DiagnosticLog.Trace("shell.desktop",
                $"右键菜单：{(target is null ? "空白" : target.Entry.Path)} 项数={entries.Count} pos=({physical.X:F0},{physical.Y:F0}) 自绘");
            // this 作 DPI 参考：贴边收敛要把物理工作区换算成逻辑单位（PerMonitorV2 下以本窗口 DPI 为准）
            DesktopMenuPopup.Show(entries, PopupPositioningService.ToScreenDipFromPhysical(physical, this), this);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"右键菜单展示失败: {ex.Message}");
        }
    }

    // ===== 自绘右键菜单内容（2026-09-07 回归） =====

    /// <summary>该文件是否适用「打开方式」：普通文件且非可执行/快捷方式/系统文件。</summary>
    private static bool CanOpenWith(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not (".exe" or ".bat" or ".cmd" or ".com" or ".msc"
            or ".lnk" or ".url" or ".dll" or ".sys" or ".msi");
    }

    /// <summary>用系统默认关联打开文件（回退路径：指定应用不可用/启动失败时的保底）。</summary>
    private static void OpenWithDefaultApp(string filePath)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"默认关联打开失败 {filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// 弹系统「打开方式」选择框（shell 的 <c>openas</c> 动词）。
    /// 这是「用 xx 打开」的**保底入口**：候选列表是自绘的（只认识注册表里登记过的应用），
    /// 用户想用的程序不在列表里时必须有条路走到系统的完整选择框（与 dock 的「打开方式…」同实现）。
    /// </summary>
    private static void OpenWithDialog(string filePath)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
            {
                UseShellExecute = true,
                Verb = "openas"
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开方式对话框失败 {filePath}: {ex.Message} → 回退默认关联");
            OpenWithDefaultApp(filePath);
        }
    }

    /// <summary>
    /// 用指定应用打开文件。
    /// <para>
    /// 【2026-09-17 修复】此前失败是**完全静默**的（裸 catch{}），用户侧只见"点了没反应"、
    /// 日志里无迹可查。现在：① 目标程序不存在 → 记日志并回退系统默认关联；② 启动抛异常 → 记日志
    /// （含异常文本）并回退默认关联；③ 成功也记一条（文件名 + 实际命令行），便于比对参数是否正确。
    /// </para>
    /// </summary>
    private static void LaunchWithApp(string appPath, string argsTemplate, string filePath)
    {
        // 模板与替换同源：模板由 ToolCatalog.SplitCommandLine 从注册表产出，替换也归 ToolCatalog
        var args = ToolCatalog.BuildLaunchArgs(argsTemplate, filePath);
        try
        {
            if (!File.Exists(appPath))
            {
                DiagnosticLog.Trace("shell.desktop", $"打开方式：目标程序不存在（{appPath}）→ 回退系统默认关联");
                OpenWithDefaultApp(filePath);
                return;
            }

            var workDir = Path.GetDirectoryName(filePath);
            LaunchedWindowPresenter.GrantForegroundToNextApp();
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = appPath,
                Arguments = args,
                // 工作目录 = 文件所在目录：部分应用（压缩工具/编辑器）以相对路径解析 %w、或用它做"另存为"起点
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? string.Empty : workDir,
                UseShellExecute = true
            });
            DiagnosticLog.Trace("shell.desktop", $"打开方式：{Path.GetFileName(appPath)} {args}");

            // 【WPS 专属救济】WPS 的 /prometheus 是单实例交接：新文档落到它**已运行的隐藏实例**里，
            // 文档真开了但窗口 isVisible=False（真机实证五条启动路径全部如此，含 explorer 自己）→
            // 这里短时观察并把未显示的文档窗口叫出来（其余应用零影响，见该类文件头）。
            LaunchedWindowPresenter.EnsurePresented(appPath, filePath);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开方式失败（{appPath}，args={args}）: {ex.Message}");

            // MSIX 打包应用（装在 WindowsApps 下）不能像普通 exe 那样直接 CreateProcess 唤起
            // （需要包标识/APPX 激活通道）——此时给系统「打开方式」框，让 shell 走正确的激活路径；
            // 直接回退"默认关联"会把用户明确指定要用的那个应用悄悄换掉，比失败更误导。
            if (appPath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Trace("shell.desktop", "目标为打包应用（WindowsApps）→ 改走系统「打开方式」选择框");
                OpenWithDialog(filePath);
                return;
            }

            DiagnosticLog.Trace("shell.desktop", "→ 回退系统默认关联");
            OpenWithDefaultApp(filePath);
        }
    }

    /// <summary>图标右键菜单（多选感知：右键项在选中集内 → 整集操作；explorer 同款）。</summary>
    private IReadOnlyList<MenuItemDef> BuildIconMenuEntries(DesktopIconTarget target)
    {
        var primary = target.Entry.Path;
        var multi = _browser.SelectedPaths.Contains(primary) && _browser.SelectedPaths.Count > 1;
        var items = new List<MenuItemDef>
        {
            // 图标 = 该条目自身（文件/夹）的 16px shell 图标（与 explorer 右键菜单一致）
            new MenuItemDef
            {
                Id = "open", Text = "打开", Kind = MenuItemKind.Command, IsDefault = true,
                Icon = MenuIconProvider.Get(primary),
                Command = () => Open(target.Entry)
            },
        };

        // 打开方式（2026-09-07：自绘「用 xx 打开」，候选 = 系统真实关联 + 本机常用软件类别匹配）
        // 【2026-09-17】①每个候选显示**应用自身图标**（用户实测"只有名称文字，认不出是哪个软件"）；
        //              ②末尾常驻「选择其他应用…」→ 系统 openas 选择框（候选表覆盖不到时的保底入口）。
        if (!multi && CanOpenWith(primary))
        {
            var candidates = ToolCatalog.MatchOpenWith(primary);
            var openWithChildren = new List<MenuItemDef>();
            var iconHits = 0;
            foreach (var (name, path, args) in candidates)
            {
                var icon = MenuIconProvider.Get(path);
                if (icon is not null)
                {
                    iconHits++;
                }
                openWithChildren.Add(new MenuItemDef
                {
                    Id = "ow-" + path,
                    Text = name,
                    Kind = MenuItemKind.Command,
                    Icon = icon,
                    // 参数模板来自注册表原始命令行（--single-argument %1 / /pdf "%1" …），
                    // 不是写死的裸路径——否则 Edge/WPS/夸克 这类"必须带参数"的应用点了没反应。
                    Command = () => LaunchWithApp(path, args, primary)
                });
            }
            // 诊断（用户报"图标没显示"时一眼看出是候选没图标还是渲染没画）
            DiagnosticLog.Trace("shell.desktop", $"打开方式候选 {candidates.Count} 项，图标命中 {iconHits} 项");
            if (openWithChildren.Count > 0)
            {
                openWithChildren.Add(new MenuItemDef { Id = "owSep", Text = "", Kind = MenuItemKind.Separator });
            }
            openWithChildren.Add(new MenuItemDef
            {
                Id = "owMore", Text = "选择其他应用…", Kind = MenuItemKind.Command,
                Command = () => OpenWithDialog(primary)
            });
            items.Add(new MenuItemDef { Id = "openWith", Text = "打开方式", Kind = MenuItemKind.Submenu, Children = openWithChildren });
        }
        items.Add(new MenuItemDef { Id = "sep1", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef { Id = "cut", Text = "剪切", Kind = MenuItemKind.Command, GestureText = "Ctrl+X", Command = () => InvokeBrowserCut(primary) });
        items.Add(new MenuItemDef { Id = "copy", Text = "复制", Kind = MenuItemKind.Command, GestureText = "Ctrl+C", Command = () => InvokeBrowserCopy(primary) });
        if (_browser.CanPaste)
        {
            items.Add(new MenuItemDef { Id = "paste", Text = "粘贴", Kind = MenuItemKind.Command, GestureText = "Ctrl+V", Command = () => InvokeBrowserPaste() });
        }
        items.Add(new MenuItemDef { Id = "copyPath", Text = "复制路径", Kind = MenuItemKind.Command, GestureText = "Ctrl+Shift+C", Command = () => InvokeCopyPaths() });
        items.Add(new MenuItemDef { Id = "sep2", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef { Id = "delete", Text = "删除", Kind = MenuItemKind.Command, GestureText = "Delete", Command = () => InvokeBrowserDelete(primary) });
        // 【2026-09-07 用户拍板】永久删除 → 卸载：对应用快捷方式（能解析到卸载注册表入口）显示"卸载"，
        // 唤起应用本体卸载器；非应用 / 无卸载入口仍保留"永久删除"（Shift 扩展）。
        if (!multi && UninstallResolver.TryResolve(primary, out var uninstallCmd))
        {
            items.Add(new MenuItemDef { Id = "uninstall", Text = "卸载", Kind = MenuItemKind.Command, Command = () => UninstallResolver.RunUninstaller(uninstallCmd ?? string.Empty) });
        }
        else
        {
            items.Add(new MenuItemDef { Id = "deletePermanent", Text = "永久删除", Kind = MenuItemKind.Command, GestureText = "Shift+Delete", Extended = true, Command = () => InvokePermanentDelete() });
        }
        if (!multi)
        {
            var mapped = _cellMenuTargets.FirstOrDefault(kv =>
                string.Equals(kv.Value.Entry.Path, primary, StringComparison.OrdinalIgnoreCase));
            if (mapped.Value.Entry is not null)
            {
                items.Add(new MenuItemDef { Id = "rename", Text = "重命名", Kind = MenuItemKind.Command, GestureText = "F2", Command = () => InvokeStartRename(mapped.Key, mapped.Value.Label, primary) });
            }
        }
        // 【2026-09-07 恢复转换挂点】格式转换：矩阵全部目标（引擎缺失置灰），
        // 单文件/同格式批量共用 shell-convert 的 ConvertMenuService 构建。
        if (_convertMenu is not null)
        {
            var paths = multi
                ? _browser.SelectedPaths.ToList()
                : new List<string> { primary };
            var convertItems = _convertMenu.BuildMenuItems(paths);
            if (convertItems.Count > 0)
            {
                items.Add(new MenuItemDef { Id = "sepConvert", Text = "", Kind = MenuItemKind.Separator });
                items.AddRange(convertItems);
            }
        }
        // 【2026-09-07 压缩/解压】zip 内置永远可用；rar/7z 解压需 WinRAR 引擎（置灰不隐藏，与转换区同原则）。
        if (_archive is not null)
        {
            var archivePaths = multi ? _browser.SelectedPaths.ToList() : new List<string> { primary };
            // 「压缩到 ▸」：zip 内置永远可用；7z/rar 按引擎探测置灰（不隐藏）。
            var compressChildren = new List<MenuItemDef>
            {
                new MenuItemDef
                {
                    Id = "compressZip", Text = "压缩为 .zip", Kind = MenuItemKind.Command,
                    Command = () => RunArchiveNotify(() => _archive.CompressZipAsync(archivePaths)),
                },
                new MenuItemDef
                {
                    Id = "compress7z", Text = "压缩为 .7z", Kind = MenuItemKind.Command,
                    IsEnabled = _archive.SevenZipAvailable,
                    Command = () => RunArchiveNotify(() => _archive.Compress7zAsync(archivePaths)),
                },
                new MenuItemDef
                {
                    Id = "compressRar", Text = "压缩为 .rar", Kind = MenuItemKind.Command,
                    IsEnabled = _archive.RarAvailable,
                    Command = () => RunArchiveNotify(() => _archive.CompressRarAsync(archivePaths)),
                },
            };
            items.Add(new MenuItemDef { Id = "compress", Text = "压缩到", Kind = MenuItemKind.Submenu, Children = compressChildren });
            if (!multi && IsArchiveFile(primary))
            {
                var isZip = string.Equals(System.IO.Path.GetExtension(primary), ".zip", StringComparison.OrdinalIgnoreCase);
                var named = System.IO.Path.GetFileNameWithoutExtension(primary);
                var children = new List<MenuItemDef>
                {
                    new MenuItemDef
                    {
                        Id = "unzipHere", Text = "解压到当前文件夹", Kind = MenuItemKind.Command,
                        IsEnabled = isZip || _archive.WinRarAvailable,
                        Command = () => RunArchiveNotify(() => _archive.ExtractAsync(primary, false)),
                    },
                    new MenuItemDef
                    {
                        Id = "unzipTo", Text = "解压到 " + named + "\\", Kind = MenuItemKind.Command,
                        IsEnabled = isZip || _archive.WinRarAvailable,
                        Command = () => RunArchiveNotify(() => _archive.ExtractAsync(primary, true)),
                    },
                };
                items.Add(new MenuItemDef { Id = "unzip", Text = "解压到", Kind = MenuItemKind.Submenu, Children = children });
            }
        }

        items.Add(new MenuItemDef { Id = "sep3", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef { Id = "properties", Text = "属性", Kind = MenuItemKind.Command, GestureText = "Alt+Enter", Command = () => InvokeShowProperties(primary) });
        return items;
    }

    /// <summary>压缩/解压异步执行 + UI 线程 MessageBox 反馈（与转换区 RunAndNotify 同模式，标题独立）。</summary>
    private void RunArchiveNotify(Func<System.Threading.Tasks.Task<BetterDesktop.Shell.Convert.Contracts.ArchiveResult>> op)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            string message;
            try
            {
                var r = await op();
                message = r.Success ? r.Message : r.Message;
            }
            catch (Exception ex)
            {
                message = $"操作异常：{ex.Message}";
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                MessageBox.Show(message, "压缩与解压");
                return;
            }

            _ = dispatcher.BeginInvoke(() => MessageBox.Show(message, "压缩与解压"));
        });
    }

    /// <summary>归档扩展名判定（.zip/.7z/.rar）。</summary>
    private static bool IsArchiveFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>桌面空白右键菜单。</summary>
    private IReadOnlyList<MenuItemDef> BuildBackgroundMenuEntries()
    {
        var sort = InvokeSortKey();
        var iconsHidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
        var items = new List<MenuItemDef>
        {
            // 【2026-09-17 用户拍板】「后退」已移除：桌面文件夹现在是**直接交给 explorer 打开**（双击目录 =
            // StartExplorerFolder），自绘桌面不再做站内导航 → 历史栈恒空、「后退」永远是灰的（2026-09-11
            // 引入它是因为当时桌面会站内导航进子目录；该前提已不存在）。菜单栏左区工具条的 ← → ↑ 同步移除。
            new MenuItemDef { Id = "refresh", Text = "刷新", Kind = MenuItemKind.Command, Command = () => InvokeBrowserRefresh() },
            new MenuItemDef { Id = "sep1", Text = "", Kind = MenuItemKind.Separator },
            new MenuItemDef
            {
                Id = "sortBy", Text = "排序方式", Kind = MenuItemKind.Submenu,
                Children =
                [
                    new MenuItemDef { Id = "sortSmart", Text = "智能", Kind = MenuItemKind.Radio, IsChecked = string.IsNullOrEmpty(sort), Command = () => InvokeSetSort(string.Empty) },
                    new MenuItemDef { Id = "sortName", Text = "名称", Kind = MenuItemKind.Radio, IsChecked = sort == "name", Command = () => InvokeSetSort("name") },
                    new MenuItemDef { Id = "sortModified", Text = "修改日期", Kind = MenuItemKind.Radio, IsChecked = sort == "modified", Command = () => InvokeSetSort("modified") },
                    new MenuItemDef { Id = "sortType", Text = "类型", Kind = MenuItemKind.Radio, IsChecked = sort == "type", Command = () => InvokeSetSort("type") },
                    new MenuItemDef { Id = "sortSize", Text = "大小", Kind = MenuItemKind.Radio, IsChecked = sort == "size", Command = () => InvokeSetSort("size") },
                ]
            },
            new MenuItemDef { Id = "autoArrange", Text = "自动排列图标", Kind = MenuItemKind.Toggle, IsChecked = AutoArrange, Command = () => _settings?.Set("desktop.autoArrange", !AutoArrange) },
            new MenuItemDef { Id = "snapGrid", Text = "将图标与网格对齐", Kind = MenuItemKind.Toggle, IsChecked = SnapToGrid, Command = () => _settings?.Set("desktop.snapToGrid", !SnapToGrid) },
            new MenuItemDef { Id = "sep2", Text = "", Kind = MenuItemKind.Separator },
            new MenuItemDef
            {
                Id = "new", Text = "新建", Kind = MenuItemKind.Submenu,
                Children =
                [
                    new MenuItemDef { Id = "newFolder", Text = "文件夹", Kind = MenuItemKind.Command, Command = () => InvokeBrowserNewFolder() },
                    new MenuItemDef { Id = "newSep1", Text = "", Kind = MenuItemKind.Separator },
                    new MenuItemDef { Id = "newText", Text = "文本文档", Kind = MenuItemKind.Command, Command = () => InvokeCreateTextFile() },
                    new MenuItemDef { Id = "newWord", Text = "Word 文档", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 Microsoft Word 文档", "docx", NewFileTemplates.Docx) },
                    new MenuItemDef { Id = "newExcel", Text = "Excel 工作表", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 Microsoft Excel 工作表", "xlsx", NewFileTemplates.Xlsx) },
                    new MenuItemDef { Id = "newPowerPoint", Text = "PowerPoint 演示文稿", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 Microsoft PowerPoint 演示文稿", "pptx", NewFileTemplates.Pptx) },
                    new MenuItemDef { Id = "newZip", Text = "压缩(zipped)文件夹", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建压缩(zipped)文件夹", "zip", NewFileTemplates.Zip) },
                    new MenuItemDef { Id = "newBmp", Text = "位图图像", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建位图图像", "bmp", NewFileTemplates.Bmp) },
                    new MenuItemDef { Id = "newSep2", Text = "", Kind = MenuItemKind.Separator },
                    new MenuItemDef { Id = "newMarkdown", Text = "Markdown 文档", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 Markdown 文档", "md", NewFileTemplates.Markdown) },
                    new MenuItemDef { Id = "newJson", Text = "JSON 文件", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 JSON 文件", "json", NewFileTemplates.Json) },
                    new MenuItemDef { Id = "newXml", Text = "XML 文档", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 XML 文档", "xml", NewFileTemplates.Xml) },
                    new MenuItemDef { Id = "newHtml", Text = "HTML 文档", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 HTML 文档", "html", NewFileTemplates.Html) },
                    new MenuItemDef { Id = "newCsv", Text = "CSV 文件", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 CSV 文件", "csv", NewFileTemplates.Csv) },
                    new MenuItemDef { Id = "newBat", Text = "批处理文件", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建批处理文件", "bat", NewFileTemplates.Bat) },
                    new MenuItemDef { Id = "newPs1", Text = "PowerShell 脚本", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 PowerShell 脚本", "ps1", NewFileTemplates.Ps1) },
                    new MenuItemDef { Id = "newPy", Text = "Python 脚本", Kind = MenuItemKind.Command, Command = () => _browser.CreateFile("新建 Python 脚本", "py", NewFileTemplates.Py) },
                ]
            },
        };
        if (_browser.CanPaste)
        {
            items.Add(new MenuItemDef { Id = "paste", Text = "粘贴", Kind = MenuItemKind.Command, GestureText = "Ctrl+V", Command = () => InvokeBrowserPaste() });
        }
        // 剪贴板历史（2026-09-09 自绘菜单补全）：Ctrl+Shift+V 同款全局热键，任何桌面位置直达历史面板。
        // 延迟解析：插件加载并行，桌面窗口创建时 clipboard 可能未注册（Func 构建菜单时解析，必然已注册）。
        var clipboard = _clipboardFactory?.Invoke();
        if (clipboard is not null)
        {
            items.Add(new MenuItemDef
            {
                Id = "clipboardHistory",
                Text = "剪贴板历史…",
                Kind = MenuItemKind.Command,
                GestureText = "Ctrl+Shift+V",
                // 【2026-09-12】engine 后端下 OpenHistoryWindow 是同步 IPC（最长 5s 超时）：
                // 直接在菜单 Click（UI 线程）里调会冻界面，失败还会被上层 catch 静默 →
                // 用户侧表现为"点了没反应"。故放后台线程执行，失败留诊断日志。
                Command = () => System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        clipboard.OpenHistoryWindow();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Trace("shell.desktop", $"剪贴板历史入口唤起失败: {ex.Message}");
                    }
                }),
            });
        }
        items.Add(new MenuItemDef { Id = "sep3", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef
        {
            Id = "showIcons",
            Text = "显示桌面图标",
            Kind = MenuItemKind.Toggle,
            IsChecked = !iconsHidden,
            Command = () => _settings?.Set("desktop.iconsHidden", !iconsHidden)
        });
        items.Add(new MenuItemDef { Id = "displaySettings", Text = "显示设置", Kind = MenuItemKind.Command, Command = () => OpenSettings("ms-settings:display") });
        items.Add(new MenuItemDef { Id = "personalize", Text = "个性化", Kind = MenuItemKind.Command, Command = () => OpenSettings("ms-settings:personalization") });
        // 【2026-09-07 用户拍板】功能管理：BetterDesktop 功能总开关（自绘桌面 / 系统右键·格式转换），
        // 开关状态持久化 = 注册表/设置真实状态（IsChecked 每次构建菜单时读，无缓存漂移）。
        // 【2026-09-11 用户拍板 · 悬停展开的二级菜单】自绘桌面右键里的「桌面控制」是**真子菜单**
        //（WPF ContextMenu 原生行为：鼠标移到父项即自动展开二级）——这正是系统右键做不到的：
        // Win11 新菜单不渲染注册表级联，非打包第三方程序无法提供"悬停子菜单"（只能靠 MSIX + COM）。
        // 二级内容与系统菜单项共用 DesktopControlMenu.Build（同一份实现，避免两处漂移）。
        items.Add(new MenuItemDef
        {
            Id = "desktopControls",
            Text = "桌面控制",
            Kind = MenuItemKind.Submenu,
            Children = DesktopControlMenu.Build(_settings,
                clipboard is null ? null : clipboard.OpenHistoryWindow,
                // 【2026-09-17 修复"菜单里点了没反应"】本包不能反向引用 shell-desktop-control，
                // 故"宿主是否在线"与"跨进程翻转路由"由**宿主进程注入**（桌面服务装配时设置）：
                //   · 桌面服务：Probe=宿主进程探测、Router=DesktopToggleExecutor.Apply
                //     → 菜单栏 / Dock / 热键侧板这些**宿主内组件**的翻转才会真正送到宿主；
                //   · 两者为 null（宿主内跑桌面插件的老形态）：退回进程内直接翻设置 —— 原有行为。
                hostRunning: DesktopControlMenu.HostRunningProbe?.Invoke() ?? true,
                toggle: DesktopControlMenu.ToggleRouter),
        });

        items.Add(new MenuItemDef { Id = "sepFeatures", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef
        {
            Id = "features",
            Text = "功能管理",
            Kind = MenuItemKind.Submenu,
            // 【2026-09-20 用户反馈："桌面控制与功能管理重复了，或许是功能管理的设计出错了" —— 确实出错了】
            //
            // 原先这里有 5 项：自绘桌面 / 系统右键·格式转换 / **隐藏菜单栏** / **隐藏 Dock** / **隐藏任务栏**。
            // 后三项与同一份菜单里的「桌面控制」子菜单（DesktopControlMenu.Build 的
            // ctlMenuBar / ctlDock / ctlTaskbar）**管的是同一个设置键**，而且**语义相反**：
            //   · 桌面控制：`菜单栏显隐`，勾选 = 显示；Dock 显隐，勾选 = 显示；隐藏任务栏，勾选 = 已隐藏（取实际可见性）
            //   · 功能管理：`隐藏菜单栏`/`隐藏 Dock`/`隐藏任务栏`，勾选 = 隐藏（即 `!值`）
            // 于是同一个开关在两处呈现相反的勾选态 —— 用户点哪边都像"另一个地方变了"。
            //
            // 分工应当按"**这个能力存不存在**" vs "**这个部件此刻显不显示**"切：
            //   · 功能管理 = 功能总开关（能力是否存在）：自绘桌面、系统右键·格式转换；
            //   · 桌面控制 = 桌面部件的即时显隐（含勾选态取真值、优先级裁决）。
            // 故后三项**删除**（不是隐藏）—— 保留它们只会让两处继续漂移。
            Children =
            [
                new MenuItemDef
                {
                    Id = "featDesktop", Text = "自绘桌面", Kind = MenuItemKind.Toggle,
                    IsChecked = _settings?.Get("components.desktop", true) ?? true,
                    Command = () => _settings?.Set("components.desktop",
                        !(_settings?.Get("components.desktop", true) ?? true)),
                },
                new MenuItemDef
                {
                    // 【2026-09-11】开关状态与落地统一走设置键 shellmenu.convert（唯一真相），
                    // 由 DesktopPlugin.ApplyShellMenuRegistration 负责注册/注销——避免"菜单看注册表、
                    // 设置看设置键"两套状态互相漂移。
                    Id = "featConvert", Text = "系统右键 · 格式转换", Kind = MenuItemKind.Toggle,
                    IsChecked = _settings?.Get("shellmenu.convert", true) ?? true,
                    Command = () => _settings?.Set("shellmenu.convert",
                        !(_settings?.Get("shellmenu.convert", true) ?? true)),
                },
                new MenuItemDef { Id = "featHint", Text = "这里只管“功能是否存在”；桌面部件的显隐在上一项「桌面控制」里", Kind = MenuItemKind.Command, IsEnabled = false },
            ],
        });
        // 【2026-09-07 用户拍板】关闭自绘桌面：切回 explorer 原生桌面。
        // 反向"开启"入口在系统桌面右键菜单「切换自绘桌面」（DesktopSystemMenuRegistrar 注册）——
        // 关闭后自绘菜单本身消失，只能从系统菜单/设置中心重新开启。
        items.Add(new MenuItemDef { Id = "sepToggleDesktop", Text = "", Kind = MenuItemKind.Separator });
        items.Add(new MenuItemDef { Id = "toggleDesktop", Text = "关闭自绘桌面", Kind = MenuItemKind.Command, Command = () => _settings?.Set("components.desktop", false) });
        return items;
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

    /// <summary>永久删除选中集（Shift+Del；不带 FOF_ALLOWUNDO，保留系统确认框）。</summary>
    internal void InvokePermanentDelete()
    {
        var paths = _browser.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }
        FileClipboard.DeletePermanent(paths);
    }

    /// <summary>复制路径到文本剪贴板（OQ8：多选每行一条，不包裹引号）。</summary>
    internal void InvokeCopyPaths()
    {
        var paths = _browser.SelectedPaths;
        if (paths.Count == 0)
        {
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"复制路径失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 控件级键盘快捷键（计划 G1）。菜单弹层打开期间让位给菜单自身的键盘处理；
    /// 无选中项的组合键不消费（不劫持纯导航键）。
    /// </summary>
    private void OnControlPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 2026-09-05 收口：MenuHost 自绘弹层退役，不再需要按键豁免判断。

        var primary = _browser.SelectedPaths.FirstOrDefault();
        var mod = Keyboard.Modifiers;

        switch (e.Key, mod)
        {
            // 组合键按"精确修饰键集合"判定；Ctrl+Shift+C 必须先于 Ctrl+C 判定
            case (Key.C, _) when mod == (ModifierKeys.Control | ModifierKeys.Shift) && primary is not null:
                InvokeCopyPaths();
                e.Handled = true;
                break;
            case (Key.C, _) when mod == ModifierKeys.Control && primary is not null:
                InvokeBrowserCopy(primary);
                e.Handled = true;
                break;
            case (Key.X, _) when mod == ModifierKeys.Control && primary is not null:
                InvokeBrowserCut(primary);
                e.Handled = true;
                break;
            case (Key.V, _) when mod == ModifierKeys.Control:
                if (_browser.CanPaste)
                {
                    _browser.Paste();
                    e.Handled = true;
                }
                break;
            case (Key.A, _) when mod == ModifierKeys.Control:
                _browser.SetSelection(_browser.Items.Select(i => i.Path));
                e.Handled = true;
                break;
            case (Key.F2, _) when mod == ModifierKeys.None && primary is not null:
                if (_cellMenuTargets.FirstOrDefault(kv => kv.Value.Entry.Path == primary) is { } pair)
                {
                    StartRename(pair.Key, pair.Value.Label, primary);
                    e.Handled = true;
                }
                break;
            case (Key.Delete, _) when mod == ModifierKeys.Shift && primary is not null:
                InvokePermanentDelete();
                e.Handled = true;
                break;
            case (Key.Delete, _) when mod == ModifierKeys.None && primary is not null:
                InvokeBrowserDelete(primary);
                e.Handled = true;
                break;
            case (Key.Enter, _) when mod == ModifierKeys.Alt && primary is not null:
                InvokeShowProperties(primary);
                e.Handled = true;
                break;
        }
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
        var desc = _settings?.Get("desktop.sortDesc", false) ?? false;
        _settings?.Set("desktop.sortKey", key ?? string.Empty);
        _browser.SetSort(key, desc);
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
