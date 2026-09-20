using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 条目行控件（面板完整窗口与悬浮球 RecentStrip 共用）。
/// 视觉基线 = 已删除的宿主内旧面板 `ClipboardHistoryWindow.CreateEntryRow`（2026-09-14 随 legacy 一并移除）：
/// 类型瓦片（图片=缩略图、文件=🖿、其余=glyph）+ 3 行预览（代码条目等宽 Consolas）+ meta 行
/// （类型 chip · 来源 · 次数 · 收藏 · HasImages/HasTable 徽标）+ hover 操作（📌/⧉/🗑）。
/// 图片缩略图永不解码原图：引擎已在捕获时生成 thumb 存 ImagePath（thumb-width=480），此处 DecodePixelWidth 再限幅。
/// </summary>
public sealed class RecentStrip : Border
{
    /// <summary>
    /// 预览最大显示高度（6 行 × 15px）。
    /// 【2026-09-12 用户要求】长文本"要显示更多的文字，以避免重复"—— 原先限高 44（3 行 ≈ 80 字），
    /// 开头相同的两条长文本在列表里长得一模一样、无法区分。现放宽到 6 行（≈170 字）；
    /// 短文本按内容自适应、不会多占高度。
    /// </summary>
    private const double PreviewMaxHeight = 90;

    /// <summary>预览行高（与 90/6 ≈ 15 对齐，保证恰好 6 行）。</summary>
    private const double PreviewLineHeight = 15;

    public ClipboardEntry Entry { get; }
    public int Index { get; }

    /// <summary>
    /// 多选状态（2026-09-12 新增「按序粘贴 / 批量删除」）。
    /// <para>
    /// <b>顺序语义</b>：<see cref="SelectionOrder"/> = 用户**勾选的先后顺序**，而不是列表显示顺序 ——
    /// 用户要的是"我按心里那个次序勾，就按那个次序粘"。面板用有序 `List` 维护（不用 `HashSet`：
    /// legacy 面板正是用 HashSet，顺序只能靠 .NET 内部插入序碰巧成立，属脆弱实现）。
    /// </para>
    /// </summary>
    public bool IsMultiSelectMode { get; set; }

    /// <summary>本行是否已勾选。</summary>
    public bool IsSelected { get; set; }

    /// <summary>勾选序号（1 起；0 = 未勾选）。画在勾选圈里，让"按序"看得见。</summary>
    public int SelectionOrder { get; set; }

    /// <summary>勾选圈被点击（面板据此切换选中并维护顺序）。</summary>
    public event Action<ClipboardEntry>? SelectionToggled;

    private Border _selectDot = null!;

    public event Action<ClipboardEntry>? Clicked;

    /// <summary>复制为纯文本（右键菜单入口 · 2026-09-15；代码条目/文件路径场景，与 Ctrl+单击等价）。</summary>
    public event Action<ClipboardEntry>? PlainCopyRequested;

    public event Action<ClipboardEntry>? PinRequested;

    /// <summary>切换「表情包」标记（与收藏同级的独立标记 · 2026-09-13；任何条目都可标记）。</summary>
    public event Action<ClipboardEntry>? StickerRequested;

    /// <summary>
    /// 复制该条并**粘贴回"打开面板时那个窗口"**（2026-09-13）。
    /// 触发器：**鼠标中键**（单手一个动作）/ 列表聚焦时的 **Shift+Enter**。
    /// </summary>
    public event Action<ClipboardEntry>? PasteRequested;

    /// <summary>
    /// 【P2-3 临时粘贴】复制并粘贴该条，随后**还原**原剪贴板（Ctrl+中键）。
    /// 与 <see cref="PasteRequested"/> 的区别：粘完不留痕（剪贴板回到用户原来的内容）。
    /// </summary>
    public event Action<ClipboardEntry>? TemporaryPasteRequested;

    /// <summary>【P2-4】点击标签 chip（面板据此把搜索切到 `tag:标签`）。</summary>
    public event Action<string>? TagClicked;

    /// <summary>
    /// 【按格粘 · 2026-09-13】把该**表格条目**逐格粘到业务系统（每次 Ctrl+V 粘一格）。
    /// 仅表格类条目（<c>ClipboardTableCells.IsTabular</c>）会触发/显示入口。
    /// </summary>
    public event Action<ClipboardEntry>? CellPasteRequested;

    public event Action<ClipboardEntry>? DeleteRequested;

    // 图片条目的懒解码槽位（仅图片条目非空）：见 DecodeImageIfNeeded/ReleaseImage。
    private Image? _previewImage;
    private TextBlock? _imageSizeText;

    /// <summary>
    /// 行表面画刷（**逐行自带**，故可做颜色过渡）。
    /// <para>
    /// 【为什么不是 SetResourceReference 共享令牌画刷 · 2026-09-15】状态切换靠给
    /// <c>Background.Color</c> 打 ColorAnimation 实现淡入淡出；若背景是应用级共享画刷，
    /// 一次动画会改到**所有行**、并永久改写主题令牌。逐行一份画刷既隔离又正确
    ///（面板 v1 只在启动时读一次主题，静态起点色安全）。
    /// </para>
    /// </summary>
    private readonly SolidColorBrush _surface = new(EntryTheme.ContentBackgroundColor);

    public RecentStrip(ClipboardEntry entry, int index)
    {
        Entry = entry;
        Index = index;
        // 【2026-09-15 视觉收敛】内边距改为左右对称（此前左 12 / 右 8，再叠加内容列 10/6 的偏移，
        // 卡片的左右留白其实差 8px —— 单看每一行都不明显，整列表看下来就是"没对齐"）。
        Padding = new Thickness(
            EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS + 2,
            EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS + 2);
        Margin = new Thickness(0, EntryTheme.Scale.SpaceXS, 0, EntryTheme.Scale.SpaceXS);
        CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusM);
        BorderThickness = new Thickness(0);
        Cursor = System.Windows.Input.Cursors.Hand;
        SnapsToDevicePixels = true;

        Background = _surface;
        SetResourceReference(BorderBrushProperty, "CardBorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 勾选圈（常驻，见 RefreshSelectDotVisibility）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 内容

        // 各列必须显式 SetColumn：不写 SetColumn 默认全部落 col0 叠加——
        // 类型瓦片会垂直居中压在预览文字上（2026-09-12 截图「图标和文字叠在一起」根因）。
        _selectDot = BuildSelectDot();
        Grid.SetColumn(_selectDot, 0);
        grid.Children.Add(_selectDot);
        var contentPanel = BuildContentPanel();
        Grid.SetColumn(contentPanel, 1);
        grid.Children.Add(contentPanel);
        // 【2026-09-15 交互收敛（用户定稿）】
        // ① 删图标瓦片列（34-44px 类型图标）→ 内容列全宽，预览显示更多；
        // ② 删行内悬浮操作层 → 功能项统一放面板底部常驻操作栏（所有条目共用）+ 行右键菜单；
        // ③ 勾选圈常驻显示 → 多选入口不再依赖 hover。
        Child = grid;

        // 右键菜单：行内不再有操作按钮，全部操作入口收进菜单（用户定稿 2026-09-15）。
        ContextMenu = BuildRowContextMenu();

        ToolTip = BuildToolTip();

        // 行的可访问名：屏幕阅读器至少能念出"第几条 / 什么分类 / 内容摘要"，
        // 而不是面对一堆 TextBlock 沉默（收敛前全目录 0 处可访问名）。
        PanelUi.Name(this, BuildAccessibleName());

        // 活动态（悬停 / 键盘焦点）统一驱动行外观：边框高亮 + 勾选圈 + 行内操作。
        // 【2026-09-12 UI 收敛】此前 hover 逻辑散在本构造器与 BuildRightColumn 两处、各写各的可见性，
        // 且完全不认键盘焦点 —— 技能规则把"只在 hover 揭示唯一操作"判为 Critical 反模式。
        MouseEnter += (_, _) => RefreshActiveChrome();
        MouseLeave += (_, _) => RefreshActiveChrome();
        IsKeyboardFocusWithinChanged += (_, _) => RefreshActiveChrome();
        RefreshSelectionVisual();
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left || e.Handled)
            {
                // 【P2-4】标签 chip 自己处理了点击（Handled=true）→ 不触发行级复制。
                return;
            }

            // 点击行内操作按钮（📌收藏/⧉复制/🗑删除）时不触发行级复制——
            // 否则点"删除"会同时把内容复制回剪贴板（2026-09-12 真机：删除超时 + 复制成功，
            // 引擎日志 copy_count 被改写，用户感知"删不到"）。
            if (FindAncestor<ButtonBase>((DependencyObject)e.OriginalSource) is not null)
            {
                return;
            }

            // 落在勾选热区（行左侧条带）→ 切换勾选（不复制）。
            // 用坐标判定而不是给圈挂 handler：Border + Transparent 背景的命中测试在真机上不可靠
            //（见 BuildSelectDot 注释）；条带判定顺带解决了"20px 圆心太难命中"。
            if (IsPointOnSelectDot(e.GetPosition(this)))
            {
                SelectionToggled?.Invoke(Entry);
                e.Handled = true;
                return;
            }

            Clicked?.Invoke(Entry);
        };

        // 【中键 = 复制并粘贴回原窗口 · 用户裁定 2026-09-13（最终）】
        // 入口迭代了四轮（Ctrl+Enter → Shift+Enter → Enter 单键 → 双击 → 中键），最终用户裁定：
        // "**鼠标中间就可以了**，如果用户想要热键，让用户自己设置就行了，我们提供入口在设置中。"
        // 故定为：
        //  · **鼠标中键** = 唯一的默认入口（不必配置、不必记）；
        //  · 键盘侧**不绑任何默认组合键** —— 想要键盘入口的用户，去**设置 → 剪贴板**自行配
        //    （见 ClipboardSection 的「粘贴回原窗口」热键项；由引擎注册、经命名事件回到本面板）。
        // 教训：默认入口只留**一个**，且必须是"一次动作"；键盘组合交给用户按自己的手型去配 ——
        // 我们猜一侧（左手/右手、单键/组合）就错一次。
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
            {
                return;
            }

            // 与左键同规：点在行内操作按钮上时不做行级动作（否则点"删除"会连带粘贴）
            if (FindAncestor<ButtonBase>((DependencyObject)e.OriginalSource) is not null)
            {
                return;
            }

            // 【P2-3】Ctrl+中键 = 临时粘贴（粘完还原剪贴板）；中键 = 粘贴回原窗口。
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                TemporaryPasteRequested?.Invoke(Entry);
            }
            else
            {
                PasteRequested?.Invoke(Entry);
            }
            e.Handled = true;
        };

        // 淡入（2026-09-12 用户反馈"从一行跳到下一行、太生硬、不连续"）：
        // 新进入视口的行由透明渐显，与面板的逐帧补间滚动配合，消除"硬跳"的观感；
        // 虚拟化复用时 Loaded 会再次触发，形成内容掠过时的柔和过渡。
        Opacity = 0;
        Loaded += (_, _) =>
        {
            // 进入视口（含虚拟化回收后滚回）才解码缩略图 —— 视口外的行不占位图内存。
            DecodeImageIfNeeded();
            // 容器复用后底色会停在"上一行的状态"，进场时重算一次（同样带过渡，不会闪）。
            RefreshActiveChrome();
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(EntryTheme.Scale.MotionFastMs))
            {
                EasingFunction = PanelUi.MotionEase(),
            };
            BeginAnimation(OpacityProperty, fade);
        };
        // 虚拟化回收（滚出视口）：释放位图引用。行数可无限增长，位图必须随视口进出而建/销，
        // 否则万条滚动会累计数百 MB 解码位图（虚拟化只回收容器，不回收内容对象持有的资源）。
        Unloaded += (_, _) => ReleaseImage();
    }

    /// <summary>
    /// 行右键菜单（2026-09-15 用户定稿：功能项移出条目，统一操作栏 + 右键菜单承载）。
    /// 菜单项直接复用行事件 → 面板侧现有处理器零改动即可响应。
    /// </summary>
    private ContextMenu BuildRowContextMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "PopupBackground");
        menu.SetResourceReference(Control.BorderBrushProperty, "PopupBorder");
        menu.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        // 菜单容器圆角 + 轻阴影（Apple 风浮层；替代系统默认直角菜单）
        menu.Template = BuildMenuContainerTemplate();

        menu.Items.Add(MenuCommand("复制", () => Clicked?.Invoke(Entry)));
        menu.Items.Add(MenuCommand("复制为纯文本", () => PlainCopyRequested?.Invoke(Entry)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("粘贴回原窗口", () => PasteRequested?.Invoke(Entry)));
        menu.Items.Add(MenuCommand("临时粘贴（粘完还原剪贴板）", () => TemporaryPasteRequested?.Invoke(Entry)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand(Entry.IsPinned ? "取消收藏" : "收藏", () => PinRequested?.Invoke(Entry)));
        menu.Items.Add(MenuCommand(Entry.IsSticker ? "取消表情包标记" : "标记为表情包", () => StickerRequested?.Invoke(Entry)));
        if (ClipboardTableCells.IsTabular(Entry))
        {
            menu.Items.Add(MenuCommand("按格粘（逐格粘到业务系统）", () => CellPasteRequested?.Invoke(Entry)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("删除", () => DeleteRequested?.Invoke(Entry)));
        return menu;
    }

    private static MenuItem MenuCommand(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            // 系统默认 MenuItem 模板带一列图标占位（无 Icon 时留白）——用户反馈"前面有一列白色空白"。
            // 用精简模板（无图标列）替换，菜单项左侧不再有多余空白。
            Template = BuildRowMenuTemplate(),
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// 菜单容器模板：圆角 Border + 轻阴影（Apple 浮层风格）。
    /// 圆角由 Popup 分层透明承载；阴影用极轻的静态效果（菜单短暂存在，开销可忽略）。
    /// </summary>
    private static ControlTemplate BuildMenuContainerTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusL));
        root.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("PopupBackground"));
        root.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("PopupBorder"));
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        root.SetValue(Border.PaddingProperty, new Thickness(EntryTheme.Scale.SpaceXS));
        root.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Opacity = 0.25,
            Color = Color.FromRgb(0, 0, 0),
        });
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        root.AppendChild(items);

        var template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = root };
        return template;
    }

    /// <summary>
    /// 行右键菜单项模板：去 WPF 默认的图标列（MinWidth≈26 留白），内容直接左起；
    /// 高亮 = 强调色淡底（带圆角），禁用 = 弱化前景。本菜单无子菜单/快捷键，模板可安全精简。
    /// </summary>
    private static ControlTemplate BuildRowMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 16, 7));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(EntryTheme.Scale.RadiusM));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };

        var highlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlight.Setters.Add(new Setter(Border.BackgroundProperty, ThemeBrushes.AccentTint(EntryTheme.Scale.AccentSubtle)) { TargetName = "Bd" });
        template.Triggers.Add(highlight);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("ThemeMutedForeground")));
        template.Triggers.Add(disabled);

        return template;
    }

    /// <summary>
    /// 勾选圈：未勾选 = 空心圈；已勾选 = 强调色实心圈 + **白色序号**。
    /// 用序号而不是对勾 —— 让"按勾选顺序粘贴"的次序**看得见**（用户能确认自己第 2 个勾的到底排第几）。
    /// </summary>
    private Border BuildSelectDot()
    {
        var dot = new Border
        {
            // 18px（原 20）+ 更疏的右间距 —— 每行左侧都常驻一个圈，尺寸大一点整列就显得很重。
            // 圈心仍是"往左点"的热区（见 IsPointOnSelectDot），缩小不影响命中。
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed
        };
        // 命中判定**不在圈上**做，而是在行的 MouseLeftButtonUp 里按坐标判定（见构造函数）。
        // 原因（2026-09-12 真机）：Border 的 Background=Transparent 在部分命中测试路径下
        // 收不到 MouseLeftButtonUp（表现为"点勾选圈毫无反应"）；坐标判定不依赖子元素命中，稳定得多。
        return dot;
    }

    /// <summary>
    /// 点击点（行内坐标）是否落在"勾选热区"。
    /// <para>
    /// 【2026-09-12 真机实测】按圆圈精确判定（20px + ±4 容错）**命中率太低** ——
    /// 鼠标必须同时对准水平与垂直两个方向（诊断日志显示连续三次试探只命中一次），
    /// 用户手动点击同样难用。改为**行左侧 40px 条带、垂直方向整行有效**：
    /// 只需"往左边点"即可，不必瞄圆心。
    /// </para>
    /// <para>
    /// 安全性：**仅当勾选圈可见时**生效（多选模式 / 已勾选 / 悬停三选一），
    /// 且条带宽度 40px 不越过图标瓦片（其右是正文区，点那里仍是"整行=复制"）。
    /// </para>
    /// </summary>
    private bool IsPointOnSelectDot(Point p)
    {
        if (_selectDot is null || _selectDot.Visibility != Visibility.Visible)
        {
            return false;
        }
        return p.X <= 40;
    }

    /// <summary>刷新勾选圈外观（选中态 / 序号 / 配色）。面板切换选中后调用。</summary>
    public void RefreshSelectionVisual()
    {
        if (_selectDot is null)
        {
            return;
        }

        if (IsSelected)
        {
            _selectDot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            _selectDot.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            _selectDot.Child = new TextBlock
            {
                Text = SelectionOrder.ToString(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            _selectDot.Background = Brushes.Transparent;
            _selectDot.SetResourceReference(Border.BorderBrushProperty, "ThemeMutedForeground");
            _selectDot.Child = null;
        }

        RefreshSelectDotVisibility();
    }

    /// <summary>
    /// 勾选圈可见性：**常驻显示**（2026-09-15 用户定稿：多选入口不依赖 hover，每行左侧始终有圈）。
    /// 未勾选 = 空心圈，已勾选 = 实心 + 序号。
    /// </summary>
    private void RefreshSelectDotVisibility()
    {
        if (_selectDot is null)
        {
            return;
        }
        _selectDot.Visibility = Visibility.Visible;
    }

    /// <summary>本行是否处于"活动态"（悬停或持有键盘焦点）。</summary>
    private bool IsActiveRow => IsMouseOver || IsKeyboardFocusWithin;

    /// <summary>
    /// 本行是否为**当前操作目标**（底部常驻操作区的作用对象）。
    /// <para>
    /// 【为什么直接问 ListBoxItem · 2026-09-15】不用面板下发的"标志位"：行容器在虚拟化下会被**复用**，
    /// 标志位会随容器回收而变成"上一行的状态"。而 <c>ListBoxItem.IsSelected</c> 由 WPF 选择器自己维护，
    /// 容器复用时同步刷新 —— 状态永远与数据一致。
    /// </para>
    /// </summary>
    private bool IsCurrentTarget => FindAncestor<ListBoxItem>(this)?.IsSelected == true;

    /// <summary>
    /// 行外观的**唯一刷新点**（悬停 / 键盘焦点 / 当前操作目标 → 三档底色 + 勾选圈）。
    /// <para>
    /// 【2026-09-15 视觉收敛】① 底色切换改为 **150ms 颜色过渡**（技能规则：状态切换禁止 0ms 瞬变，
    /// hover 须有 150-300ms 反馈）；② 新增"当前操作目标"档 —— 此前单击某行只是把它复制了，
    /// **行本身没有任何视觉反馈**，用户无从得知底部操作区那几个按钮此刻作用于谁。
    /// </para>
    /// </summary>
    private void RefreshActiveChrome()
    {
        if (_selectDot is null)
        {
            return;
        }

        // 三档底色：静止 = 内容底色；悬停/焦点 = 强调色极轻底；当前操作目标 = 强调色淡底。
        // 用 Blend 先混成**不透明**色再动画 —— 半透明色之间插值会插出"中途变淡"的假象。
        var accent = EntryTheme.AccentColor;
        var rest = EntryTheme.ContentBackgroundColor;
        var target = IsCurrentTarget
            ? EntryTheme.Blend(rest, accent, EntryTheme.Scale.AccentMedium)
            : IsActiveRow
                ? EntryTheme.Blend(rest, accent, EntryTheme.Scale.AccentSubtle)
                : rest;

        if (_surface.Color != target)
        {
            _surface.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(EntryTheme.Scale.MotionFastMs))
                {
                    EasingFunction = PanelUi.MotionEase(),
                });
        }

        RefreshSelectDotVisibility();
    }

    /// <summary>
    /// 外部（面板）在选中项变化后重算本行外观。面板只对**已实现的行**调用 ——
    /// 虚拟化下视口外没有容器，也就没有外观需要刷新。
    /// </summary>
    public void RefreshChrome() => RefreshActiveChrome();

    private FrameworkElement BuildContentPanel()
    {
        // 内缩统一由行 Padding 承担（此前这里再叠 10/6，与 Padding 的 12/8 相加后左右差 8px）
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 【2026-09-13】表情包改为**标记**后，条件要看"内容形态"而不是标记本身：
        //  · 图片条目 → 缩略图预览；
        //  · 文件条目 + 表情包标记 → 也是缩略图预览（导入的动图/图片）；
        //  · **文字表情包**（颜文字）→ 仍走文本预览（它本来就没有图）。
        if (Entry.ContentType == ClipboardItemKind.Image
            || (Entry.IsSticker && Entry.ContentType == ClipboardItemKind.Files))
        {
            // 图片条目：整张缩略图为主（设计预期 2026-09-11 确认），meta 行保留尺寸/来源/次数。
            // 表情包同此呈现 —— 首帧缩略图 + 尺寸，方便一眼选中要发的那张。
            panel.Children.Add(BuildImagePreview());
        }
        else
        {
            // 预览行：序号徽章（1-9 快捷键）+ 预览文本。
            // 必须用 Grid（Auto + Star）而非水平 StackPanel——水平 StackPanel 在排列方向上给子元素
            // 无限宽度，TextBlock 的 TextWrapping.Wrap 拿到无限宽就永不换行，结果只显示 1 行并横向溢出
            // （顶出列表水平滚动条、三条同开头文本无法区分，2026-09-12 截图根因）。Star 列给出有限宽，
            // 文本才能正确折成 3 行。
            var previewRow = new Grid();
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (Index < 9)
            {
                var badge = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
                    Background = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentSubtle),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, EntryTheme.Scale.SpaceS, 0)
                };
                badge.Child = new TextBlock
                {
                    Text = (Index + 1).ToString(),
                    FontSize = EntryTheme.Scale.FontCaption,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong)
                };
                Grid.SetColumn(badge, 0);
                previewRow.Children.Add(badge);
            }

            var preview = BuildPreviewText();
            Grid.SetColumn(preview, 1);
            previewRow.Children.Add(preview);
            panel.Children.Add(previewRow);
        }

        // meta 行：左侧 = 类型 chip · 大小 · 来源 · 次数 · 徽标（可伸缩，超长裁切）；
        // 右侧 = 时间戳（固定列，永不被挤掉）。
        // 【2026-09-15】时间戳从"信息组最后一项"改为**独立右对齐列**：它留在组里时，
        // 一旦来源名较长（如 BetterDesktop.Capture）整行就溢出卡片、被列表裁成"26 分…"的半截字。
        // 独立成列后，左侧再长也只挤压自己（ClipToBounds），时间永远完整 —— 同时符合
        // "元信息靠左、时间戳靠右"的列表惯例（Finder / 邮件列表都是这个读法）。
        var meta = new Grid
        {
            Margin = new Thickness(0, EntryTheme.Scale.SpaceXS, 0, 0),
            ClipToBounds = true,
        };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var metaLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            ClipToBounds = true,
        };
        // 【P2-2】敏感标记放在最前 —— 被遮罩的条目必须一眼看出"是它被遮了，而不是内容本来就这样"。
        if (Entry.IsSensitive)
        {
            metaLeft.Children.Add(CreateSensitiveBadge());
        }
        metaLeft.Children.Add(BuildCategoryChip());
        // 【2026-09-13 用户要求】文件大小 —— 文本条目是 UTF-8 字节数，图片/文件是落盘字节数。
        // 源码里 size_bytes 一直有，只是从未显示（用户看不到"这条占了多少"）。
        var size = PanelUi.FormatSize(Entry.SizeBytes);
        if (size.Length > 0)
        {
            metaLeft.Children.Add(CreateMetaText(size));
        }
        if (!string.IsNullOrEmpty(Entry.SourceAppDisplayName))
        {
            metaLeft.Children.Add(CreateMetaText(Entry.SourceAppDisplayName));
        }
        // 【2026-09-15】只在 ≥2 次时才显示次数：每条都印"1 次复制"等于没印 ——
        // 它既占掉本来就紧张的一行宽度，又把"重复复制过"这个真正有意义的信息稀释掉。
        if (Entry.CopyCount > 1)
        {
            metaLeft.Children.Add(CreateMetaText($"{Entry.CopyCount} 次复制"));
        }
        if (Entry.IsPinned)
        {
            metaLeft.Children.Add(CreateMetaGlyph("\uE718", "已收藏"));
        }
        // 【2026-09-13】表情包标识：与「已收藏」对称 —— 标记必须**看得见**，
        // 否则"文字颜文字已是表情包"这件事没有任何界面反馈（旧模型靠分类 chip 顺带体现，现在没有分类了）。
        if (Entry.IsSticker)
        {
            metaLeft.Children.Add(CreateMetaGlyph("\uE90E", "表情包"));
        }
        if (Entry.HasImages)
        {
            metaLeft.Children.Add(CreateBadge("图"));
        }
        if (Entry.HasTable)
        {
            metaLeft.Children.Add(CreateBadge("表"));
        }
        // 【P2-4 标签体系】标签 chip（`#标签`，点击即按标签搜索）—— 此前标签只藏在 ToolTip 里，等于没有入口。
        foreach (var tag in SplitTags(Entry.Tags))
        {
            metaLeft.Children.Add(BuildTagChip(tag));
        }

        Grid.SetColumn(metaLeft, 0);
        meta.Children.Add(metaLeft);

        // 时间戳靠右独立成列（见上）。悬停/选中/非活动任何状态都可见。
        var time = CreateMetaText(GetRelativeTime(Entry.Timestamp));
        time.Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0);
        Grid.SetColumn(time, 1);
        meta.Children.Add(time);

        panel.Children.Add(meta);
        return panel;
    }

    /// <summary>
    /// 图片条目整张缩略图：引擎 thumb（480px）DecodePixelWidth 限幅解码，永不解码原图；
    /// paths-only 模式无缩略图副本时回退尺寸文本。
    /// 【懒解码】此处只搭槽位（尺寸文本 + 空 Image），真实解码推迟到 <c>Loaded</c>（见 DecodeImageIfNeeded）——
    /// 视口外的行即便已构造也不持有位图；滚出视口由 ReleaseImage 释放。
    /// </summary>
    private FrameworkElement BuildImagePreview()
    {
        var slot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4)
        };

        _imageSizeText = new TextBlock
        {
            // 尺寸 + **文件大小**（这块文本同时是"缩略图未就绪/缺失"时的占位，故两个信息都要有）
            Text = BuildImageMetaText(),
            FontSize = EntryTheme.Scale.FontBody,
            Foreground = ThemeBrushes.Get("ThemeMutedForeground")
        };
        slot.Children.Add(_imageSizeText);

        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 150,
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        slot.Children.Add(_previewImage);

        return slot;
    }

    /// <summary>进入视口时解码（幂等：已解码则直接返回）。资源缺失/解码失败保持尺寸文本回退。</summary>
    private void DecodeImageIfNeeded()
    {
        var isSticker = Entry.IsSticker;
        if (_previewImage is null || _previewImage.Source is not null
            || (Entry.ContentType != ClipboardItemKind.Image && !isSticker))
        {
            return;
        }

        var path = ClipboardImagePaths.BestDisplayImage(Entry);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 480;      // 限幅解码（原图可能远大于显示尺寸）
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读入并释放文件句柄，避免锁住缩略图文件
            bmp.EndInit();
            bmp.Freeze();
            _previewImage.Source = bmp;
            _previewImage.Visibility = Visibility.Visible;
            if (_imageSizeText is not null)
            {
                _imageSizeText.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // 解码失败 → 保持尺寸文本回退
        }
    }

    /// <summary>滚出视口时释放位图（滚回时 Loaded 会重新解码）。</summary>
    private void ReleaseImage()
    {
        if (_previewImage?.Source is null)
        {
            return;
        }

        _previewImage.Source = null;
        _previewImage.Visibility = Visibility.Collapsed;
        if (_imageSizeText is not null)
        {
            _imageSizeText.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement BuildPreviewText()
    {
        // 【P2-2】敏感条目预览遮罩（只影响显示；复制/粘贴仍是完整内容）。
        var preview = MaskedPreview();
        var isCode = Entry.Category == ContentCategory.Code;
        var isImage = Entry.ContentType == ClipboardItemKind.Image;
        var isFile = Entry.ContentType == ClipboardItemKind.Files;

        var tb = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontBody,
            FontWeight = Entry.IsPinned ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get("ThemeForeground"),
            MaxHeight = isImage || isFile ? 20 : PreviewMaxHeight
        };
        if (isImage)
        {
            tb.Text = $"{Entry.ImageWidth}×{Entry.ImageHeight}";
            tb.FontSize = EntryTheme.Scale.FontSmall;
            tb.Foreground = ThemeBrushes.Get("ThemeMutedForeground");
        }
        else if (isFile)
        {
            tb.Text = string.Join("  ", Entry.FilePaths ?? Array.Empty<string>());
            tb.Foreground = ThemeBrushes.Get("ThemeMutedForeground");
        }
        else if (isCode)
        {
            tb.Text = preview;
            tb.FontFamily = PanelUi.MonoFont;
        }
        else
        {
            // 文本：多行预览（短文本自然少于上限，长文本显示更多，见 PreviewMaxHeight）
            tb.Text = preview;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.MaxHeight = PreviewMaxHeight;
            tb.LineHeight = PreviewLineHeight;
        }

        if (isCode)
        {
            // 代码：等宽多行预览（引擎 Preview 已按行拼接，这里仅限高 + 换行）
            tb.TextWrapping = TextWrapping.Wrap;
            tb.MaxHeight = PreviewMaxHeight;
            tb.LineHeight = PreviewLineHeight;
        }
        return tb;
    }

    /// <summary>
    /// 条目标签（显示**语义分类**：文字/代码/富文本/图片/文件）。
    /// 【2026-09-12】此前显示剪贴板**格式**名（HTML/RTF/文本）—— 格式名对用户没有意义，
    /// 还与筛选 chips 的维度不一致（用户实测困惑："从 md 复制的全文为什么显示 HTML？"）。
    /// 格式信息仍可看：ToolTip 首行是「类型 · 分类」。
    /// </summary>
    private FrameworkElement BuildCategoryChip()
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
            Background = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentSubtle),
            Padding = new Thickness(EntryTheme.Scale.BadgePadX, 1, EntryTheme.Scale.BadgePadX, 1),
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        chip.Child = new TextBlock
        {
            Text = Entry.CategoryLabel,
            FontSize = EntryTheme.Scale.FontCaption,
            Foreground = ThemeBrushes.AccentTint(EntryTheme.Scale.AccentStrong),
            VerticalAlignment = VerticalAlignment.Center
        };
        return chip;
    }

    /// <summary>
    /// 图片条目的尺寸/大小文案：`300×300 · 1.2 MB`（任一缺失则只显示另一方）。
    /// 【2026-09-13】此前只有像素尺寸 —— 用户看不到"这张图占了多少空间"。
    /// </summary>
    private string BuildImageMetaText()
    {
        var dims = Entry.ImageWidth > 0 && Entry.ImageHeight > 0
            ? $"{Entry.ImageWidth}×{Entry.ImageHeight}"
            : string.Empty;
        var size = PanelUi.FormatSize(Entry.SizeBytes);
        return dims.Length == 0 ? size : size.Length == 0 ? dims : $"{dims} · {size}";
    }

    private static TextBlock CreateMetaText(string text) => new()
    {
        Text = text,
        FontSize = EntryTheme.Scale.FontSmall,
        // 【2026-09-15】项间距 4 → 8：元信息是"一串并列的小事实"，挤在一起会读成一个长句；
        // 8px 才让"分类 · 大小 · 来源 · 次数 · 时间"各自成词（技能规则：相邻目标/信息块 ≥8px）。
        Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = ThemeBrushes.Get("ThemeMutedForeground")
    };

    /// <summary>
    /// 元信息「图标 + 文案」项（收藏 / 表情包标记）。
    /// <para>
    /// 【为什么不用 emoji · 2026-09-15】此前标记写作 <c>"★ 已收藏"</c> / <c>"😀 表情包"</c>：
    /// ① 与全面板的 Segoe MDL2 图标体系混用（同一行里既有矢量图标又有彩色 emoji 字形）；
    /// ② emoji 是**彩色**字形，在不同系统字体回退下样子不可控（技能规则：禁止用 emoji 当图标）。
    /// 现改用与底部操作区**同一个**字形（收藏 = E718、表情包 = E90E），行内标记与按钮自洽。
    /// </para>
    /// </summary>
    private static FrameworkElement CreateMetaGlyph(string glyph, string text)
    {
        var label = CreateMetaText(text);
        label.Margin = new Thickness(0);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = PanelUi.IconFont,
            FontSize = EntryTheme.Scale.FontCaption,
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get("ThemeMutedForeground"),
        });
        row.Children.Add(label);
        return row;
    }

    /// <summary>沿可视树向上查找指定类型祖先（按钮内容 TextBlock 等场景：OriginalSource 可能是按钮内部元素）。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>【P2-2】预览文本（敏感条目按设置遮罩；无敏感或关闭识别时原样返回）。</summary>
    private string MaskedPreview() =>
        Entry.IsSensitive
            ? Entry.BuildMaskedPreview(PanelTheme.SensitiveMaskLeading, PanelTheme.SensitiveMaskTrailing)
            : Entry.Preview;

    /// <summary>【P2-2】「敏感」标记（危险色，与遮罩预览呼应）。</summary>
    private static Border CreateSensitiveBadge() => new()
    {
        CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
        Background = ThemeBrushes.Tint("StatusDanger", EntryTheme.Scale.AccentSubtle),
        Padding = new Thickness(EntryTheme.Scale.BadgePadX, 1, EntryTheme.Scale.BadgePadX, 1),
        Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "敏感",
            FontSize = EntryTheme.Scale.FontCaption,
            Foreground = ThemeBrushes.Get("StatusDanger"),
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>
    /// 【P2-4】标签 chip：`#标签`，点击即按该标签筛选（标签即导航）。
    /// <para>
    /// 【2026-09-15 视觉收敛】它**是可点控件**（点一下就切搜索），却长成了"静态色块"——
    /// 无 hover、无按下反馈，用户不会想到能点。现改用与按钮同一套表面语言（静止 = ControlBackground、
    /// hover = ControlBackgroundHover），派生的好处是文字对比度从 ≈3.7:1 提到 ≈5.3:1（技能 Critical：
    /// 11px 小字也必须 ≥4.5:1）。
    /// </para>
    /// </summary>
    private Border BuildTagChip(string tag)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
            Background = ThemeBrushes.Get("ControlBackground"),
            Padding = new Thickness(EntryTheme.Scale.BadgePadX, 1, EntryTheme.Scale.BadgePadX, 1),
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"按标签「{tag}」筛选",
        };
        chip.Child = new TextBlock
        {
            Text = "#" + tag,
            FontSize = EntryTheme.Scale.FontCaption,
            Foreground = ThemeBrushes.Get("ThemeMutedForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.MouseEnter += (_, _) => chip.Background = ThemeBrushes.Get("ControlBackgroundHover");
        chip.MouseLeave += (_, _) => chip.Background = ThemeBrushes.Get("ControlBackground");
        chip.MouseLeftButtonUp += (_, e) =>
        {
            TagClicked?.Invoke(tag);
            e.Handled = true; // 阻止冒泡到行级"点击=复制"
        };
        return chip;
    }

    /// <summary>标签串按空格 / 逗号拆分（引擎侧 tags 为空格分隔）。</summary>
    private static IEnumerable<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? Array.Empty<string>()
            : tags.Split(new[] { ' ', '\t', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);

    private static Border CreateBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
        Background = ThemeBrushes.Tint("StatusWarning", EntryTheme.Scale.AccentSubtle),
        Padding = new Thickness(EntryTheme.Scale.BadgePadX, 1, EntryTheme.Scale.BadgePadX, 1),
        Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = EntryTheme.Scale.FontCaption,
            Foreground = ThemeBrushes.Get("StatusWarning"),
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static string GetRelativeTime(DateTime ts)
    {
        var delta = DateTime.Now - ts;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} 天前";
        return ts.ToString("MM-dd");
    }

    /// <summary>
    /// 行的可访问名（屏幕阅读器读这一句）：序号 + 语义分类 + 预览摘要。
    /// 【2026-09-12 UI 收敛】收敛前全目录 0 处 `AutomationProperties.Name` —— 读屏用户在列表里
    /// 只能听到一串没有标签的 TextBlock；图标按钮更是只念出 emoji。
    /// </summary>
    private string BuildAccessibleName()
    {
        var preview = Entry.Preview ?? string.Empty;
        if (preview.Length > 80)
        {
            preview = preview[..80];
        }

        // 【2026-09-13】标记也要读出来（与界面上的「★ 已收藏」「😀 表情包」对称）：
        // 否则读屏用户无从得知某条已被标记为表情包 —— 而"标记要看得见"正是这次改造的要点。
        var marks = (Entry.IsPinned, Entry.IsSticker) switch
        {
            (true, true) => "已收藏且表情包、",
            (true, false) => "已收藏、",
            (false, true) => "表情包、",
            _ => string.Empty,
        };
        return $"{Index + 1}. {marks}{Entry.CategoryLabel}：{preview}";
    }

    /// <summary>
    /// 供容器自动化读取。
    /// 【为什么必须覆盖 · 2026-09-12 真机 UIA 实测】WPF 的 `ListBoxItem` 默认拿 `Content.ToString()`
    /// 当自己的自动化名 —— 不覆盖时读屏软件念出的是**类型全名**（`BetterDesktop.Clipboard.Panel.RecentStrip`），
    /// 而不是条目内容。给 RecentStrip 本身设 `AutomationProperties.Name` 解决不了这一点（读的是容器）。
    /// </summary>
    public override string ToString() => BuildAccessibleName();

    private object BuildToolTip()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(new TextBlock
        {
            Text = $"{Entry.ContentTypeLabel} · {Entry.CategoryLabel}",
            FontSize = EntryTheme.Scale.FontBody,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(Entry.SourceAppDisplayName))
        {
            p.Children.Add(new TextBlock { Text = $"来源: {Entry.SourceAppDisplayName}", FontSize = EntryTheme.Scale.FontSmall });
        }
        p.Children.Add(new TextBlock { Text = $"时间: {Entry.Timestamp:yyyy-MM-dd HH:mm:ss}", FontSize = EntryTheme.Scale.FontSmall });
        if (!string.IsNullOrEmpty(Entry.Tags))
        {
            p.Children.Add(new TextBlock { Text = $"标签: {Entry.Tags}", FontSize = EntryTheme.Scale.FontSmall });
        }
        // 【2026-09-13】尺寸与大小也在 ToolTip 留一份：缩略图显示出来后行内不再显示这两项，
        // 但"这张图多大、多少像素"仍是用户挑图时要看的信息。
        if (Entry.ImageWidth > 0 && Entry.ImageHeight > 0)
        {
            p.Children.Add(new TextBlock { Text = $"尺寸: {Entry.ImageWidth}×{Entry.ImageHeight}", FontSize = EntryTheme.Scale.FontSmall });
        }
        var size = PanelUi.FormatSize(Entry.SizeBytes);
        if (size.Length > 0)
        {
            p.Children.Add(new TextBlock { Text = $"大小: {size}", FontSize = EntryTheme.Scale.FontSmall });
        }

        // 操作提示（可发现性）：Ctrl+单击的"纯文本复制"没有任何可见入口，不写在这里用户永远不会知道 ——
        // 而它正是"在聊天框里贴出文件夹路径而不是一堆子项"的答案。
        p.Children.Add(new TextBlock
        {
            Text = "单击 = 复制　·　Ctrl+单击 = 复制为纯文本（文件夹 → 路径文字）\n"
                 + "中键单击 = 复制并直接粘贴回「打开面板时那个窗口」（键盘热键可在设置中自定义）\n"
                 + "Ctrl+中键 = 临时粘贴（粘完把剪贴板还原成你原来的内容）\n"
                 + "T 键 = 编辑标签（点击 #标签 可直接按标签筛选）",
            FontSize = EntryTheme.Scale.FontSmall,
            Margin = new Thickness(0, EntryTheme.Scale.SpaceXS, 0, 0),
            Foreground = ThemeBrushes.Get("ThemeMutedForeground"),
        });
        return p;
    }
}
