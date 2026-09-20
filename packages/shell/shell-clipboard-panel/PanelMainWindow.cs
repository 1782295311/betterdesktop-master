using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 完整面板（v1）：PopupWindowBase 子类，主题令牌外观与宿主一致。
/// 数据面全部走 IPC 分页（GetFilteredEntriesPage，每页 100），列表 VirtualizingStackPanel 虚拟化——
/// 对齐计划 A 档"面板虚拟化 + 分页提升 v1 强制"（万条历史不再全量重建 UI 线程）。
/// 交互：单击=复制到剪贴板、双击=粘贴到活动窗口（代码条目强制纯文本）、📌/🗑、搜索防抖 300ms、
/// 类型筛选 chips、暂停横幅（PauseTemporarily 60s + 剩余秒数）、空状态、引擎断线横幅。
/// </summary>
public sealed class PanelMainWindow : PopupWindowBase
{
    private const int PageSize = 100;
    private const double PanelWidth = 420;
    private const double PanelHeight = 640;

    // 面板含搜索 TextBox，必须可被激活以接收键盘输入；基类默认 true(WS_EX_NOACTIVATE) 会让搜索框无法键入
    // （602 纪律：含键盘输入的弹窗必须重写为 false）。改为可激活后，点面板外由 Deactivated + 鼠标钩子双保险收起。
    protected override bool UseNoActivateWindowStyle => false;

    /// <summary>
    /// 收起方式（2026-09-12 新增，设置里可选，见 ClipboardSection「收起方式」）：
    ///   auto（默认）= 点面板外 / 失焦即收起（原行为，仿 macOS 面板）；
    ///   manual = 只有点「✕」或按 Esc 才收起 —— 适合"边看历史边在其他窗口操作"。
    /// 基类两处收起路径（外点钩子 + Deactivated 双保险）由该属性统一受控，不会只关一半。
    /// </summary>
    protected override bool AutoHideOnOutsideClick
        => !string.Equals(_dismissMode, "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 收起**前**把前台还给用户原本的窗口（见基类 <c>OnBeforeHide</c> 注释）。
    /// <para>
    /// 【为什么必须做 · 2026-09-12 真机，用户实测"还是漏了第一个"】WPF `Hide()` 会把焦点
    /// 移交给**同进程的另一个可见窗口**（这里是侧边栏手柄，`WS_EX_NOACTIVATE` 拦不住），
    /// 于是面板收起后前台落在自家窗口上 —— 紧接着 `SendPaste` 发出的 Ctrl+V 打在自己身上，
    /// **第一条凭空消失**。因是时序竞态，表现为**偶发**（固定延时有时够、有时不够）。
    /// </para>
    /// <para>
    /// 必须在此刻归还：只有当前台进程调用时 `SetForegroundWindow` 才被系统接受，
    /// `Hide()` 之后再调会被静默忽略。
    /// </para>
    /// </summary>
    protected override void OnBeforeHide()
    {
        // 收起即释放用户热键 —— 它只在面板可见期间才有意义（此刻才有"选中项"与"目标窗口"）。
        // 放在最前面：即使下面因缺少目标窗口而提前 return，也不会漏掉注销。
        UnregisterPasteBackHotkey();

        var target = EdgeHandleWindow.ForegroundBeforeOpen;
        if (target == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // 【2026-09-13】改用 WindowActivator（AttachThreadInput 范式 + ALT 抖动兜底），
            // 不再依赖"裸 SetForegroundWindow 恰好被接受"——前台锁下裸调会静默失败，
            // 而那正是按序粘贴"第一条偶发丢失"的根源。
            PanelLog.Trace(WindowActivator.Activate(target)
                ? "收起面板：已把前台归还给用户窗口（按序粘贴第一条可正常粘出）"
                : "收起面板：归还前台被系统拒绝 —— 将依赖延时，第一条可能需手动按一次 Ctrl+V");
        }
        catch (Exception e)
        {
            PanelLog.Trace($"归还前台失败（不影响功能）: {e.Message}");
        }
    }

    private readonly EntryHost _host;
    private readonly ClipboardIpcClient _client;
    /// <summary>收起方式：每次打开面板时从设置重读（改设置无需重启面板，2026-09-12 用户要求"实时生效"）。</summary>
    private string _dismissMode;

    // ---- 用户自定义热键：「复制并粘贴回原窗口」（2026-09-13 用户裁定）----
    //
    // 【为什么由面板注册、而不是丢给引擎】用户裁定："鼠标中键就可以了，如果用户想要热键，
    // 让用户自己设置就行了，我们提供入口在设置中。" 该动作只在**面板打开且有选中项**时才有意义，
    // 故热键也只在**面板可见期间**存在：
    //   ① 面板收起即注销 → 不长期占用全局热键、不与其它程序长期冲突；
    //   ② 无需为"引擎 → 已运行的面板"新建跨进程事件通道（引擎的全局热键只能 spawn exe，
    //      驱动不了已运行的常驻面板 —— 见 docs 探查结论）。
    // 引擎的内置热键（Ctrl+Shift+V/P/Backspace）不受影响，二者互不干涉。

    /// <summary>热键 id（面板内唯一即可；用固定值，避免每次注册换 id 导致上次的漏注销）。</summary>
    private const int PasteBackHotkeyId = 0xB1A5;

    private bool _pasteBackHotkeyRegistered;

    /// <summary>窗口消息钩子来源（WM_HOTKEY 经此回到 <see cref="OnHotkeyMessage"/>）。</summary>
    private HwndSource? _hotkeySource;

    private Border _storageBanner = null!;
    private TextBlock _storageText = null!;

    // ---- 【P2-4 标签体系】标签编辑浮层（面板内叠加，不新开窗口）----
    private Border? _tagEditorHost;
    private TextBox? _tagEditorBox;
    private ClipboardEntry? _tagEditorEntry;

    // ---- 【按格粘 · 2026-09-13】表格逐格粘（启动浮层选"粘完自动按键"）----
    private Border? _cellPasteHost;
    private ClipboardEntry? _cellPasteEntry;
    private CellPasteAutoKey _cellPasteChoice = CellPasteAutoKey.Tab;
    private readonly List<(RadioButton Button, CellPasteAutoKey Key)> _cellPasteOptions = new();
    private TextBlock? _cellPasteSummary;

    // ---- 多选 / 按序粘贴（2026-09-12 新增「按序粘贴 + 批量删除」）----

    /// <summary>
    /// 已勾选条目：**按勾选先后排队的数组**。
    /// <para>
    /// `List` 保序 = **FIFO（先进先出，队列语义）**，这个顺序就是粘贴顺序 ——
    /// 用户要的是"我按这个次序勾，就按这个次序粘"。
    /// </para>
    /// <para>
    /// 刻意不用 `HashSet`：legacy 面板用 HashSet 存选中项，顺序只能靠 .NET 内部插入序
    /// 碰巧成立，属脆弱实现（换运行时版本就可能变）。这里把顺序做成**数据结构保证**的语义。
    /// </para>
    /// <para>
    /// 配套 <see cref="_selectedOrder"/> 做 id → 序号 的 **O(1)** 反查：否则
    /// <see cref="RefreshSelectionUi"/> 要对每一个已实现的行做一次线性查找
    ///（复杂度退化成「行数 × 选中数」）。
    /// </para>
    /// </summary>
    private readonly List<ClipboardEntry> _selected = new();

    /// <summary>id → 勾选序号（1 起）。仅在选中集合变化时整体重建（见 <see cref="ReindexSelection"/>）。</summary>
    private readonly Dictionary<string, int> _selectedOrder = new(StringComparer.Ordinal);

    private bool _multiSelectMode;
    private Border _multiSelectBar = null!;
    private TextBlock _multiSelectCount = null!;

    /// <summary>多选条的批量标记按钮：文案随选中项状态在「设为表情包 / 取消表情包」间切换（2026-09-13）。</summary>
    private Button _stickerBatchBtn = null!;
    private Border _sequentialBar = null!;
    private TextBlock _sequentialText = null!;

    // ---- 【统一操作区 · 2026-09-15 用户定稿】----
    // 行内不放操作按钮（去图标瓦片 + 去悬浮操作层），功能项统一放进**底部页脚单行**（条目外、
    // 所有条目共用）；目标 = 当前 ListBox 选中条目。多选激活时按钮禁用（批量操作走多选浮层条）。
    private TextBlock _actionSummary = null!;
    private Button _actionCopyBtn = null!;
    private Button _actionPinBtn = null!;
    private Button _actionStickerBtn = null!;
    private Button _actionCellPasteBtn = null!;
    private Button _actionDeleteBtn = null!;
    private ClipboardEntry? _actionTarget;

    /// <summary>
    /// 按序粘贴过程提示（条目失效 / 已用快照降级 / 彻底失败）。
    /// 【为什么留着它 · 2026-09-12 用户实测"选了按序粘贴后它会自己释放"】旧实现失效即静默跳过，
    /// 用户只看到"内容凭空少了"。提示必须能回看：面板收起期间产生，重开面板时仍在状态条上。
    /// </summary>
    private string? _sequentialNotice;

    /// <summary>本次按序会话的总项数（客户端只暴露"剩余"，总项数由面板在会话开始时记住）。</summary>
    private int _sequentialTotal;

    /// <summary>本次会话是否「按格粘」（只影响上报给灵动岛的标题文案）。</summary>
    private bool _sequentialIsCell;

    /// <summary>本次会话按**粘贴顺序**排好的内容预览（供灵动岛显示"下一条要粘什么"）。</summary>
    private System.Collections.Generic.List<string> _sequentialPreviews = new();

    // ---- 按序粘贴的 Ctrl+V 重定向（2026-09-12 用户口径修正）----
    //
    // 【为什么是 Ctrl+V 而不是 Ctrl+Shift+V】用户明确要求："不应该使用 Ctrl+Shift+V 的方式，
    // 而是直接 Ctrl+V 就触发就行了" —— Ctrl+V 是通用粘贴键，不用额外记忆组合键。
    //
    // 做法：低级键盘钩子，**仅在按序会话进行中**把用户的 Ctrl+V 吞掉并改为"粘下一条"；
    // 会话未激活时一律放行 —— 绝不干扰系统正常的 Ctrl+V。
    private KeyboardHook? _pasteHook;
    private bool _ctrlDown;

    /// <summary>
    /// 自注入期间**整体放行**钩子（不吞任何 V 键）。
    /// <para>
    /// 【为什么不用 dwExtraInfo 标记 · 2026-09-12 真机实测】标记方案在理论上更精确，实测却**拿不到**：
    /// 注入实际发生在**宿主进程**（面板经 IPC 请求），低级键盘钩子收到的
    /// <c>KBDLLHOOKSTRUCT.dwExtraInfo</c> 恒为 0（诊断日志 <c>inj=0</c>），
    /// 于是自注入的 Ctrl+V 又被当作用户按键吞掉 → 再消费 → 再注入 → **自激**
    ///（用户原话"后面又被一股脑卸出来了"；日志实证三次消费挤在 250ms 内）。
    /// </para>
    /// <para>
    /// 【为什么不担心多粘】窗口内**只放行、不消费** —— 最坏结果是用户这一按被漏过（再按一次即可），
    /// 而**绝不会**出现"一次按键粘出好几条"。窗口 500ms 覆盖"注入 + 分发 + 回钩"全程。
    /// </para>
    /// </summary>
    private bool _hookPaused;

    private ListBox _entryList = null!;
    private TextBox _searchBox = null!;
    private TextBlock _searchPlaceholder = null!;
    private WrapPanel _chipHost = null!;
    private TextBlock _searchTagHint = null!;
    private Border _pauseBanner = null!;
    private TextBlock _pauseText = null!;
    private ToggleButton _pauseBtn = null!;
    private TextBlock _countLabel = null!;
    private Border _engineBanner = null!;
    private TextBlock _engineText = null!;

    // ---- 状态槽（唯一状态区；2026-09-12 UI 收敛） ----

    /// <summary>
    /// 状态通道。新增一类状态只需加枚举值 + 在 <see cref="LaneOrder"/> 里排位，
    /// **不必再动布局行号**（旧实现正是"每加一个提示就要重排 Grid 行"）。
    /// </summary>
    private enum StatusLane
    {
        /// <summary>引擎断线：阻断级（列表不可用），优先级最高。</summary>
        Engine,

        /// <summary>暂停捕获。</summary>
        Pause,

        /// <summary>按序粘贴会话与过程提示。</summary>
        Sequential,

        /// <summary>存储将满（软提醒，最低）。</summary>
        Storage,
    }

    /// <summary>状态槽优先级顺序（高 → 低）：同一时刻只呈现最高的那条，其余折叠为 `+N 条提醒`。</summary>
    private static readonly StatusLane[] LaneOrder =
    {
        StatusLane.Engine, StatusLane.Pause, StatusLane.Sequential, StatusLane.Storage,
    };

    private readonly Dictionary<StatusLane, bool> _statusWanted = new();
    private Dictionary<StatusLane, Border>? _statusCards;

    /// <summary>状态槽容器（整体可见性由 <see cref="UpdateStatusSlot"/> 裁决）。</summary>
    private FrameworkElement _statusSlot = null!;

    /// <summary>`+N 条提醒` 折叠开关（被压制的提醒不丢弃，可展开查看）。</summary>
    private Button _statusMore = null!;
    private bool _statusExpanded;

    /// <summary>引擎是否处于断开态 —— 用于**抑制"暂无历史"空状态**，避免与"引擎未连接"两条提示互相矛盾。</summary>
    private bool _engineDown;

    private StackPanel _emptyState = null!;
    private TextBlock _emptyTitle = null!;
    private TextBlock _emptyHint = null!;

    /// <summary>
    /// 操作反馈条（复制 / 删除 / 收藏 / 合并 / 清理的成功与失败），3 秒后自动隐去。
    /// 【为什么需要 · 2026-09-12 审计】此前这些操作失败只写 panel.log —— 用户看到的是
    /// "点了没反应"或"删掉了但其实还在"，失败被静默吞掉。
    /// </summary>
    private TextBlock _toast = null!;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private string _currentFilter = "all";   // all|text|code|rich|image|files|sticker|pinned

    /// <summary>
    /// 外部入口（引擎热键 Ctrl+Shift+P「收藏视图」→ 命名事件）登记的目标筛选。
    /// <para>
    /// 为什么"先登记、显示后再应用"：首次显示前筛选 chip 还不存在（BuildContent 才建），
    /// 直接 SwitchFilter 会 NRE；登记后由 <see cref="ShowRightAligned"/> 收尾统一应用。
    /// </para>
    /// </summary>
    private string? _pendingFilter;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _pauseTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _pauseRemaining;
    private int _offset;
    private int _total;
    private bool _loading;
    private bool _allLoaded;

    public PanelMainWindow(EntryHost host, IVibrancyService vibrancy)
        : base(vibrancy)
    {
        _host = host;
        _client = host.Client;
        _dismissMode = PanelTheme.ExtensionConfig().DismissMode;
        Width = PanelWidth;
        Height = PanelHeight;
        MinWidth = PanelWidth;
        MinHeight = 480;
        Title = "剪贴板历史";

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); Reload(); };
        _pauseTick.Tick += (_, _) => UpdatePauseRemaining();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_toast is not null)
            {
                _toast.Visibility = Visibility.Collapsed;
            }
        };

        _client.HistoryChanged += _ => Dispatcher.BeginInvoke(new Action(Reload));
        _client.PauseStateChanged += paused => Dispatcher.BeginInvoke(new Action(() => OnPauseState(paused)));
        // 【2026-09-12 修复】重连后除了收起横幅，还要**重新加载数据** ——
        // 断线期间列表可能停在空态/旧数据（panel.log 实测 "分页加载失败: engine not connected"），
        // 只隐藏横幅会让用户面对一个永远空着的面板。
        _client.Reconnected += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            HideEngineBanner();
            Reload();
        }));

        // 按序粘贴过程提示（条目失效/降级/失败）→ 状态条 + 面板日志。
        // 【为什么必须接线 · 2026-09-12】此前失效完全静默（只在 ipc-client.log 里留痕），
        // 用户看到的现象就是"点了按序粘贴它自己把列表释放了"——过程必须可见。
        _client.SequentialIssue += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            PanelLog.Trace(message);
            _sequentialNotice = message;
            UpdateSequentialBar();
        }));

        // 按序粘贴的 Ctrl+V 重定向钩子（常驻安装，仅会话激活时生效 —— 未激活时零干扰）
        _pasteHook = new KeyboardHook(OnPasteHotkeyHook);
        if (!_pasteHook.Start())
        {
            PanelLog.Trace("键盘钩子安装失败：按序粘贴将只能靠面板上的按钮逐条粘");
        }
    }

    /// <summary>
    /// 低级键盘钩子：**仅在按序粘贴会话进行中**把 Ctrl+V 重定向为"粘下一条"。
    /// <para>
    /// 【回调内必须立刻返回】低级钩子有 LowLevelHooksTimeout（默认 300ms），超时会被系统摘钩。
    /// 所以这里只做判断 + <c>BeginInvoke</c> 异步派发，绝不在此同步调用 IPC。
    /// </para>
    /// <para>
    /// 【按下与抬起都要吞】只吞 KEYDOWN 会让 V 的 KEYUP 漏给系统，造成按键状态错位
    ///（某些应用会以为 V 一直被按住）。
    /// </para>
    /// </summary>
    private bool OnPasteHotkeyHook(int msg, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        var isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        // 维护 Ctrl 自身状态（用修饰键的按下/抬起判断，比 GetAsyncKeyState 稳）
        if (info.vkCode is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL)
        {
            if (isDown)
            {
                _ctrlDown = true;
            }
            else if (isUp)
            {
                _ctrlDown = false;
            }
            return false; // Ctrl 本身照常放行
        }

        if (info.vkCode != NativeMethods.VK_V || (!isDown && !isUp))
        {
            return false;
        }

        // 【自注入期间整体放行】见 _hookPaused 注释：标记方案实测拿不到（inj=0），
        // 只能"注入期间不吞"。注意 Ctrl 状态在上面已更新，这里直接放行不会让 _ctrlDown 卡死。
        if (_hookPaused)
        {
            return false;
        }

        if (!_ctrlDown)
        {
            return false; // 非 Ctrl+V → 放行
        }

        if (!_client.IsSequentialPasteActive)
        {
            return false; // 会话未激活 → 完全放行（不干扰系统粘贴）
        }

        if (isDown)
        {
            // 吞掉用户的 Ctrl+V，异步改为"粘下一条"
            Dispatcher.BeginInvoke(new Action(PasteNextSequential));
        }

        return true; // KEYDOWN / KEYUP 都吞
    }

    /// <summary>置顶常驻（引擎 open_panel / 热键唤起时 ShowAt 屏幕中心）。</summary>
    public void ShowCentered()
    {
        RefreshRuntimeConfig();
        var wa = SystemParameters.WorkArea;
        var x = wa.Left + (wa.Width - Width) / 2;
        var y = wa.Top + (wa.Height - Height) / 2;
        ShowAt(new Point(x, y));
        EnsureLoaded();
        RefreshStorageBanner(); // ShowAt 之后：此时 _storageBanner 已由 BuildContent 创建
    }

    /// <summary>
    /// 侧边栏滑出（默认入口）：贴屏幕右缘垂直居中显示。
    /// v1 不做滑入动画——先 Show 窗口再对内容做 RenderTransform 位移动画，会造成「窗口先完整出现
    /// → 内容瞬移 → 再滑入」的两段观感（用户实测「界面和内容陆续显示，像两次窗口」，2026-09-12）。
    /// 收起由 PopupWindowBase 全局鼠标钩子（外点/Esc）承担。
    /// </summary>
    public void ShowRightAligned()
    {
        RefreshRuntimeConfig();
        var wa = SystemParameters.WorkArea;
        var targetX = wa.Right - Width;
        var targetY = wa.Top + (wa.Height - Height) / 2;
        ShowAt(new Point(targetX, targetY));
        EnsureLoaded();
        RefreshStorageBanner(); // ShowAt 之后：此时 _storageBanner 已由 BuildContent 创建
        ApplyPendingFilter();   // 外部登记的筛选（引擎热键「收藏视图」）在控件就绪后生效
    }

    /// <summary>登记"下次显示时切到该筛选"（幂等；供引擎全局热键通路调用）。</summary>
    public void ShowWithFilter(string filterKey) => _pendingFilter = filterKey;

    /// <summary>应用外部登记的筛选（无登记 = 无操作；<see cref="SwitchFilter"/> 自带同值早退）。</summary>
    private void ApplyPendingFilter()
    {
        if (_pendingFilter is not { } key)
        {
            return;
        }

        _pendingFilter = null;
        SwitchFilter(key);
    }

    /// <summary>外部（悬浮球/单实例信号）关闭面板。</summary>
    public void HidePanel() => HidePopup();

    /// <summary>
    /// 面板每次显示后刷新数据。
    /// 【2026-09-12 审计】旧实现只在"列表为空"时才 Reload —— 上次留下的旧数据会一直显示到
    /// 下一次 `history_changed` 才更新（用户看到的是过期列表：删过的条目还在、新复制的看不到）。
    /// 面板打开即最新才符合预期（查询下沉引擎，一页 100 条代价很小）。
    /// </summary>
    private void EnsureLoaded()
    {
        if (_entryList is null)
        {
            return; // BuildContent 尚未执行（首次 Show 之前的异步事件回调可能走到这里）
        }

        Reload();

        // 用户热键在此注册：必须等**窗口显示之后**（HWND 已创建、消息循环在跑）。
        // RefreshRuntimeConfig 里只能读配置，注册放这里（见 ApplyPasteBackHotkey 注释）。
        ApplyPasteBackHotkey();
    }

    /// <summary>
    /// 布局行的**唯一真相源**。
    /// 【2026-09-12 UI 收敛】此前是 7 个魔法数字散落在各处 `SetRow` 调用里，代码自己都写着
    /// "本文件行号是硬编码的…新增行会牵动后续所有 SetRow"——为了躲开它，4 个状态横幅被硬塞进同一个
    /// StackPanel（结果可以同时全可见、垂直堆叠把列表下推）。现在改成具名常量：新增区块只改这里。
    /// </summary>
    private static class Row
    {
        public const int Header = 0;
        public const int Search = 1;
        public const int Filters = 2;

        /// <summary>状态槽：引擎/暂停/按序/存储 —— **同一时刻只呈现一条**。</summary>
        public const int Status = 3;

        /// <summary>列表：多选操作条与空状态是它的**内部浮层**，不占布局行（因此不会推挤列表）。</summary>
        public const int List = 4;

        public const int Footer = 5;

        public const int Count = 6;
    }

    protected override FrameworkElement BuildContent()
    {
        var panel = new Grid();
        for (var i = 0; i < Row.Count; i++)
        {
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = i == Row.List ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        AddRow(panel, BuildHeader(), Row.Header);
        AddRow(panel, BuildSearchBox(), Row.Search);
        AddRow(panel, BuildFilters(), Row.Filters);
        AddRow(panel, BuildStatusSlot(), Row.Status);
        // 注意：BuildList/BuildFooter 只允许调用一次——重复调用会创建第二份控件（2026-09-12 用户截图 UI 挤在一起根因）。
        AddRow(panel, BuildList(), Row.List);
        AddRow(panel, BuildFooter(), Row.Footer);

        // 【P2-4】标签编辑浮层：叠加在所有行之上（初始折叠）——
        // 不新开窗口，避免与 PopupWindowBase 的"外点收起"语义打架；复用面板键盘焦点，行为可预测。
        var tagEditor = BuildTagEditor();
        Grid.SetRow(tagEditor, 0);
        Grid.SetRowSpan(tagEditor, Row.Count);
        panel.Children.Add(tagEditor);

        // 【按格粘】表格逐格粘的选项浮层（同规叠加，初始折叠）
        var cellPaste = BuildCellPasteEditor();
        Grid.SetRow(cellPaste, 0);
        Grid.SetRowSpan(cellPaste, Row.Count);
        panel.Children.Add(cellPaste);
        return panel;

        static void AddRow(Grid grid, UIElement child, int row)
        {
            Grid.SetRow(child, row);
            grid.Children.Add(child);
        }
    }

    /// <summary>
    /// 状态槽（唯一状态区）：把此前"各自为政"的提示收敛为**一个槽 + 优先级裁决**。
    /// <para>
    /// 【依据 · 技能规则 "Contextual Live Badge Updates"（High）】异步状态变化应给出**一条恰当的原子状态消息**，
    /// 而不是让每处提示都变成竞争性区域。旧实现恰好相反：引擎/存储/按序/多选四条挤进同一个 StackPanel，
    /// 可同时全部可见 —— 垂直堆叠把列表整体下推，甚至"引擎未连接"与"暂无剪贴板历史"两条互相矛盾的提示同时显示。
    /// </para>
    /// 被压制的高优先级以下提醒不丢弃：右侧给 `+N 条提醒`，点击展开全部（信息不丢，只是不抢位置）。
    /// </summary>
    private FrameworkElement BuildStatusSlot()
    {
        var host = new StackPanel();

        _statusCards = new Dictionary<StatusLane, Border>
        {
            [StatusLane.Engine] = _engineBanner = BuildEngineBanner(),
            [StatusLane.Pause] = _pauseBanner = BuildPauseBanner(),
            [StatusLane.Sequential] = _sequentialBar = BuildSequentialBar(),
            [StatusLane.Storage] = _storageBanner = BuildStorageBanner(),
        };
        foreach (var lane in LaneOrder)
        {
            host.Children.Add(_statusCards[lane]);
        }

        // 折叠开关：被压制的提醒数量（≥32px 点击目标，与动作按钮同高）
        _statusMore = new Button
        {
            Content = string.Empty,
            FontSize = EntryTheme.Scale.FontSmall,
            MinHeight = EntryTheme.Scale.TapTarget,
            Padding = new Thickness(EntryTheme.Scale.SpaceM, 0, EntryTheme.Scale.SpaceM, 0),
            Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS, EntryTheme.Scale.GutterX, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };
        _statusMore.SetResourceReference(Control.ForegroundProperty, "ThemeMutedForeground");
        PanelUi.Name(_statusMore, "展开被折叠的提醒");
        _statusMore.Click += (_, _) =>
        {
            _statusExpanded = !_statusExpanded;
            UpdateStatusSlot();
        };
        host.Children.Add(_statusMore);

        _statusSlot = host;
        _statusSlot.Visibility = Visibility.Collapsed;
        return _statusSlot;
    }

    /// <summary>
    /// 登记某状态"想不想显示"。各处**不再直接改 Visibility** —— 那正是旧实现互相打架的根因。
    /// </summary>
    private void SetStatusLane(StatusLane lane, bool wanted)
    {
        _statusWanted[lane] = wanted;
        UpdateStatusSlot();
    }

    /// <summary>状态槽裁决：谁上屏、谁折叠、折叠几个。</summary>
    private void UpdateStatusSlot()
    {
        if (_statusCards is null || _statusSlot is null || _statusMore is null)
        {
            return; // BuildContent 之前（构造期事件先到）→ 不能直接访问
        }

        var active = new List<StatusLane>();
        foreach (var lane in LaneOrder)
        {
            if (_statusWanted.TryGetValue(lane, out var wanted) && wanted)
            {
                active.Add(lane);
            }
        }

        if (active.Count == 0)
        {
            _statusSlot.Visibility = Visibility.Collapsed;
            _statusExpanded = false;
            return;
        }

        _statusSlot.Visibility = Visibility.Visible;

        // 默认只呈现**最高优先级**那一条；用户展开后按优先级全部列出
        var showAll = _statusExpanded || active.Count == 1;
        foreach (var lane in LaneOrder)
        {
            _statusCards[lane].Visibility = showAll || lane == active[0]
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (active.Count <= 1)
        {
            _statusMore.Visibility = Visibility.Collapsed;
        }
        else
        {
            _statusMore.Content = _statusExpanded ? "收起提醒" : $"+{active.Count - 1} 条提醒";
            _statusMore.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement BuildHeader()
    {
        // 水平内缩统一走 GutterX（此前 header 右 10、其余 14，两种内缩让边缘看起来"没对齐"）
        var header = new Grid { Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceL, EntryTheme.Scale.GutterX, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "剪贴板历史",
            FontSize = EntryTheme.Scale.FontTitle,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextElement.ForegroundProperty, "ThemeForeground");
        header.Children.Add(title);

        // 必须存字段：图标选中态要由 OnPauseState（引擎事件）驱动，而非只在点击时自动取反
        _pauseBtn = new ToggleButton
        {
            Name = "PauseBtn",
            Content = "\uE769",
            FontFamily = PanelUi.IconFont,
            Width = EntryTheme.Scale.TapTarget,
            Height = EntryTheme.Scale.TapTarget,
            FontSize = EntryTheme.Scale.FontBody,
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceXS, 0),
            // 【2026-09-12 UI 收敛】文案此前写的是 Ctrl+Shift+P —— 那是**收藏视图**的全局热键，
            // 用户照做会打开收藏而不是暂停（暂停是 Ctrl+Shift+Backspace）。悬空/错指的热键提示比没有更糟。
            ToolTip = "暂停捕获 60 秒（全局热键 Ctrl+Shift+Backspace）",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        _pauseBtn.SetResourceReference(Control.ForegroundProperty, "ThemeMutedForeground");
        _pauseBtn.Click += (_, _) => TogglePause();
        header.Children.Add(_pauseBtn);
        Grid.SetColumn(_pauseBtn, 1);

        var close = CreateIconButton("\uE711", "关闭（Esc）", (_, _) => HidePopup());
        header.Children.Add(close);
        Grid.SetColumn(close, 2);
        return header;
    }

    private FrameworkElement BuildSearchBox()
    {
        var outer = new Border
        {
            Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceM, EntryTheme.Scale.GutterX, 0),
            // 胶囊外形（= 高度的一半）：搜索框是"输入条"，与筛选 chip 同族；原先的 14px 圆角
            // 在 32px 高度上是"圆角矩形"，跟旁边的胶囊 chip 不是一套语言。
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusPill),
            Padding = new Thickness(EntryTheme.Scale.SpaceM, 0, EntryTheme.Scale.SpaceM, 0),
            Height = EntryTheme.Scale.TapTarget
        };
        outer.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        outer.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        outer.BorderThickness = new Thickness(1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = "\uE721",
            FontFamily = PanelUi.IconFont,
            FontSize = EntryTheme.Scale.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
            Opacity = 0.7,
        };
        glyph.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        grid.Children.Add(glyph);

        _searchBox = new TextBox
        {
            Name = "SearchBox",
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = EntryTheme.Scale.FontBody,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = ThemeBrushes.Get("ThemeForeground")
        };
        _searchBox.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        _searchBox.TextChanged += (_, _) =>
        {
            var empty = string.IsNullOrEmpty(_searchBox.Text);
            _searchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _searchTagHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        grid.Children.Add(_searchBox);
        Grid.SetColumn(_searchBox, 1);

        _searchPlaceholder = new TextBlock
        {
            // 【2026-09-15】占位文案改短：原为"搜索历史…　（tag:标签 = 只搜该标签）"——
            // 一整句解释塞在输入框里，既把"这里能打字"的提示淹掉，又直接顶到右边缘。
            // 语法提示独立成右侧的 tag:标签 小标签（见下），信息不丢、层级清楚。
            Text = "搜索历史…",
            FontSize = EntryTheme.Scale.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _searchPlaceholder.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(_searchPlaceholder, 1);
        grid.Children.Add(_searchPlaceholder);

        // 【P2-4】把 `tag:` 语法暴露出来（引擎侧解析）—— 否则标签筛选是"存在但没人知道"的功能。
        // 停靠在输入框右端（Spotlight 式提示位），空输入时才显示，不参与命中测试（不抢焦点/不挡选字）。
        _searchTagHint = new TextBlock
        {
            Text = "tag:标签",
            FontSize = EntryTheme.Scale.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0),
            IsHitTestVisible = false,
            Opacity = 0.75,
        };
        _searchTagHint.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(_searchTagHint, 2);
        grid.Children.Add(_searchTagHint);

        outer.Child = grid;
        return outer;
    }

    private FrameworkElement BuildFilters()
    {
        var host = new StackPanel { Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceM, EntryTheme.Scale.GutterX, 0) };
        // 【2026-09-15 修复筛选条溢出】容器由水平 StackPanel 改为 **WrapPanel**：
        // 8 个筛选 chip 在 420px 面板里的合计宽度已超过可用宽度（388px），水平 StackPanel **不换行也不裁剪**，
        // 末尾的「收藏」被顶到面板外——用户根本点不到（技能规则：内容必须完整落在视口内，禁止横向溢出）。
        // WrapPanel 保证：宽度够 → 单行；不够 → 换行，**永远不会有点不到的 chip**。
        _chipHost = new WrapPanel { Orientation = Orientation.Horizontal };
        // 【筛选维度统一为「语义分类」category（2026-09-12）】此前 "文本/图片/文件" 按剪贴板**格式**(kind)、
        // "代码"按**语义**(category) —— 两种维度混用，且 RichText 根本没有 chip：
        // 从 IDE 复制的代码（带 HTML 格式 → contentType=Html）既不在"文本"（kind≠Text）、
        // 也不在"代码"（当时被判 RichText）→ 任何 chip 都点不到（用户实测的"真空地带"）。
        // 现全部按 category 划分（Text/Code/RichText/Image/File 完备覆盖，不存在无归属条目），
        // 与条目行内显示的 CategoryLabel（文字/代码/富文本/图片/文件）一一对应。
        foreach (var (key, label) in new (string, string)[]
                 {
                     ("all", "全部"), ("text", "文字"), ("code", "代码"), ("rich", "富文本"),
                     ("image", "图片"), ("files", "文件"),
                     // 表情包（2026-09-12）：用户主动填入的动图，独立成一类便于集中取用
                     ("sticker", "表情包"), ("pinned", "收藏")
                 })
        {
            var chip = CreateChip(key, label);
            chip.MouseLeftButtonUp += (_, _) => SwitchFilter(key);
            _chipHost.Children.Add(chip);
        }
        host.Children.Add(_chipHost);
        return host;
    }

    private void SwitchFilter(string key)
    {
        if (_currentFilter == key) return;
        _currentFilter = key;
        UpdateFilterChips();
        Reload();
    }

    private void UpdateFilterChips()
    {
        for (var i = 0; i < _chipHost.Children.Count; i++)
        {
            ApplyChipVisual((Border)_chipHost.Children[i]);
        }
    }

    /// <summary>
    /// 筛选 chip 的**唯一外观刷新点**（选中 / 悬停 / 键盘焦点 → 底色 + 字色）。
    /// <para>
    /// 【为什么必须收口 · 2026-09-15】此前"选中态"由 <see cref="UpdateFilterChips"/> 改、
    /// 悬停/焦点由 <see cref="CreateChip"/> 内的内联闭包改，两处各写一套半透明值 ——
    /// 同一个 chip 在"被选中"和"被悬停"时会呈现两种不同的强调色，看起来像两个控件。
    /// </para>
    /// <para>
    /// 未选中态用弱化前景、选中态用强调色前景：这是"当前在哪一档"的主信号，
    /// 底色只是辅助（技能规则：不得仅靠颜色传达状态 —— 字色 + 底色双通道）。
    /// </para>
    /// </summary>
    private void ApplyChipVisual(Border chip)
    {
        var active = (string?)chip.Tag == _currentFilter;
        var hot = active || chip.IsMouseOver || chip.IsKeyboardFocusWithin;
        chip.Background = hot
            ? ThemeBrushes.AccentTint(active ? EntryTheme.Scale.AccentMedium : EntryTheme.Scale.AccentSubtle)
            : Brushes.Transparent;

        if (chip.Child is TextBlock label)
        {
            // 选中态用**正文前景**而非强调色本身：强调色文字叠在强调色淡底上只有 ≈3:1，
            // 12px 小字达不到 4.5:1（技能 Critical 规则）。正文前景在深色/浅色两种模式下
            // 与强调淡底的对比度分别约 10:1 / 12:1 —— 这也是系统分段控件的做法。
            label.Foreground = active
                ? ThemeBrushes.Get("ThemeForeground")
                : ThemeBrushes.Get("ThemeMutedForeground");
        }
    }

    /// <summary>
    /// 筛选 chip。key 为筛选键（单一真相源）—— 早前由标签反推 key，label/key 两份真相源必然漂移
    ///（2026-09-12 分类改造时正是踩到这个：新增分类忘记同步反推表就会静默失效）。
    /// <para>
    /// 【2026-09-12 UI 收敛】补齐三件此前缺失的事：① 尺寸令牌化 + `MinHeight` 达标准点击目标；
    /// ② 可访问名（读屏只念"全部/文字/…"而无上下文）；③ **键盘可达**（技能规则 Critical：
    /// 紧凑控件需可聚焦 + 键盘操作 + 可见焦点，此前 chip 只能鼠标点）。
    /// </para>
    /// </summary>
    private Border CreateChip(string key, string label)
    {
        // 【2026-09-15】内边距 12 → 8、字号 body → small、圆角 → 胶囊：
        // 8 个 chip 的合计宽度必须落回 388px 的可用宽度内（原 12px 内边距时合计 ≈ 440px，
        // 必然把末尾的「收藏」顶出面板）。字体同时从 13 降到 12 —— chip 是**导航控件**，
        // 与列表正文同级会抢视觉重心；降一档后筛选条整行也才"读得出是一层控件"。
        var tb = new TextBlock
        {
            Text = label,
            FontSize = EntryTheme.Scale.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var chip = new Border
        {
            Tag = key,
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusPill),
            Padding = new Thickness(EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceXS, EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceXS),
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceXS, EntryTheme.Scale.SpaceXS),
            MinHeight = EntryTheme.Scale.TapTarget,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = tb,
        };

        PanelUi.Name(chip, $"筛选：{label}");
        ApplyChipVisual(chip);

        // 键盘：Enter/Space 切换筛选；获得焦点时给一层极轻的强调底作为**可见焦点**
        //（不改变"当前筛选"的视觉，避免与选中态混淆）
        chip.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                SwitchFilter(key);
                e.Handled = true;
            }
        };
        chip.IsKeyboardFocusWithinChanged += (_, _) => ApplyChipVisual(chip);
        chip.MouseEnter += (_, _) => ApplyChipVisual(chip);
        chip.MouseLeave += (_, _) => ApplyChipVisual(chip);
        return chip;
    }

    /// <summary>暂停提醒（状态槽 · Warning 级）。</summary>
    private Border BuildPauseBanner()
        => PanelUi.CreateStatusCard(
            StatusKind.Warning, "\uE769", out _pauseText,
            ("恢复", (_, _) => Resume()));

    /// <summary>引擎断线提醒（状态槽 · Danger 级，优先级最高）。文案在 <see cref="ShowEngineBanner"/> 里设置。</summary>
    private Border BuildEngineBanner()
        => PanelUi.CreateStatusCard(
            StatusKind.Danger, "\uE945", out _engineText,
            ("启动引擎", (_, _) => StartEngineManually()));

    /// <summary>
    /// 「存储已达上限」提醒（状态槽 · Warning 级，优先级最低）。
    /// 【2026-09-12 用户口径】总存储预算是**软限制** —— 达到/超过预算时引擎**不删任何数据**，
    /// 只由这条提醒催促用户清理（"历史数据达到存储上面也可以存储，但我们要发消息提醒用户及时清理"）；
    /// 带一键「清理未收藏」（收藏与表情包永不参与清理）。
    /// </summary>
    private Border BuildStorageBanner()
        => PanelUi.CreateStatusCard(
            StatusKind.Warning, "\uE74E", out _storageText,
            ("清理未收藏", (_, _) => ClearUnpinnedFromStorageBanner()));

    /// <summary>请 core 启动引擎（状态槽「启动引擎」按钮）。core 不可达或被 gate 拒绝时必须说出来，不能点了没反应。</summary>
    private void StartEngineManually()
    {
        if (!EngineProber.EnsureEngine())
        {
            PanelLog.Trace("请 core 启动引擎未成功（core 不可达，或扩展开关已关闭）");
            ShowToast("无法启动引擎：core 未响应或该功能已在设置中关闭", isError: true);
        }
    }

    private FrameworkElement BuildList()
    {
        _entryList = new ListBox
        {
            Name = "EntryList",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(10, 10, 10, 0),
            Padding = new Thickness(4, 0, 0, 8),
            ItemContainerStyle = CreateListContainerStyle()
        };
        // 附加属性不能在对象初始化器里赋值：代码设置虚拟化（万条历史只物化可视行）
        VirtualizingPanel.SetIsVirtualizing(_entryList, true);
        VirtualizingPanel.SetVirtualizationMode(_entryList, VirtualizationMode.Recycling);
        // 禁掉自带模板内 ScrollViewer 的横条，防长 token 顶出底部水平滚动条。
        ScrollViewer.SetHorizontalScrollBarVisibility(_entryList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_entryList, ScrollBarVisibility.Auto);
        // 滚动条视觉统一定义在 App.xaml 应用级资源（细胶囊、无箭头、hover 提亮）。
        // 【滚轮策略】像素滚动 + 逐帧补间 + 虚拟化回收 —— 三者同时成立：
        //   CanContentScroll=true 且 VirtualizingPanel.ScrollUnit=Pixel 时，VerticalOffset 单位是**像素**
        //   （不是项），因此可以逐帧插值（平滑）；同时 VirtualizingStackPanel 照常回收视野外的行容器，
        //   滚出即卸、滚回重建 —— 用户感知不到边界，可一直滚到最旧。
        //   2026-09-12 更正：此前判定"像素滚动必须放弃虚拟化，改用 600 行上限"，是错的（会撞硬墙=边界感）；
        //   ScrollUnit=Pixel 让平滑与回收兼得，行数上限已撤除。
        VirtualizingPanel.SetIsVirtualizing(_entryList, true);
        VirtualizingPanel.SetVirtualizationMode(_entryList, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_entryList, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(_entryList, true);

        // 【2026-09-12 修复"鼠标滚不动"】此前在 ListBox 外面**又套了一层 ScrollViewer**，
        // 且外层设 CanContentScroll=true —— 等于让外层把「整个 ListBox」当成 1 个 item：
        // 内容高度永远不超出视口 → 无滚动条、滚轮完全无效。
        // ListBox 自身模板里已有 ScrollViewer，直接用它；分页判定改听它的 ScrollChanged
        //（ScrollChangedEvent 是冒泡路由事件，从内层 ScrollViewer 冒泡到 ListBox，可在此挂）。
        _entryList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
        // 【2026-09-15 统一操作栏】选中目标变化 → 刷新底部操作栏（单击行=复制并选中，目标随之激活）。
        _entryList.SelectionChanged += (_, _) => RefreshActionBar();

        // ---- 键盘可达性（2026-09-12 UI 收敛；技能规则：全功能键盘可达 · High）----
        // 此前列表整块没有键盘路径：ListBoxItem 不可聚焦、只有 Esc 生效，而 UI 却印着"1-9"序号徽标。
        _entryList.Focusable = true;
        // Tab 只把整个列表当一个站点（否则 Tab 会逐项遍历上百条）；方向键在列表内部导航
        KeyboardNavigation.SetTabNavigation(_entryList, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(_entryList, KeyboardNavigationMode.Contained);
        // 【用 PreviewKeyDown 而非 KeyDown · 2026-09-13 真机实测】KeyDown 是**冒泡**阶段 ——
        // ListBox/ListBoxItem 会先消费部分按键（Space 尤其）并置 `Handled`，处理函数收不到，
        // 表现就是"空格勾选没反应"（UIA 探针实测：多选条始终不出现）。
        // PreviewKeyDown 是**隧道**阶段（从根到叶，早于控件自身处理），故必须用后者。
        _entryList.PreviewKeyDown += OnListKeyDown;
        // 滚轮接管：逐帧补间（CompositionTarget.Rendering），让滚动连续而非"跳一整段"
        _entryList.PreviewMouseWheel += OnListPreviewMouseWheel;
        _entryList.Loaded += (_, _) => _listScroll = FindVisualChild<ScrollViewer>(_entryList);

        // ---- 列表区浮层（2026-09-12 UI 收敛）----
        // 空状态与多选条都作为**列表区内的浮层**，不参与布局：
        //  · 空状态此前被"塞进 ListBox.Items" → 列表里混进一个假 item，污染虚拟化集合与计数语义；
        //  · 多选条此前靠"常驻占位"规避列表位移 —— 两者都是"把非列表内容塞进列表"换来的。
        // 浮层化后：列表永不被推挤，空状态也不再是列表项。
        var host = new Grid
        {
            Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS, EntryTheme.Scale.GutterX, 0),
        };
        _entryList.Margin = new Thickness(0); // 内缩统一由 host 承担（消除此前 14 与 10 两种水平内缩）
        host.Children.Add(_entryList);
        host.Children.Add(_emptyState = BuildEmptyState());
        host.Children.Add(_multiSelectBar = BuildMultiSelectBar());
        return host;
    }

    /// <summary>
    /// 空状态（列表区浮层，**不再作为 ListBox item**）。
    /// 【2026-09-12 UI 收敛】此前它被当成一个 item 加进 `_entryList.Items`；更糟的是引擎断线时列表必然为空，
    /// 于是"引擎未连接"（横幅）与"暂无剪贴板历史"（空状态）**同时显示、互相矛盾**。
    /// 现在改为常驻浮层 + 可见性裁决，断线时一律不显示（见 <see cref="UpdateFooter"/>）。
    /// </summary>
    private StackPanel BuildEmptyState()
    {
        var host = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false, // 浮层不遮挡列表交互
            Visibility = Visibility.Collapsed,
        };

        // 【2026-09-15】空状态补一个**大图标**：此前只有两行小字浮在列表区中央，
        // 在大片空白里几乎"看不见"，用户会以为列表加载失败。图标给空白一个锚点（Apple 空状态惯例）。
        var glyph = new TextBlock
        {
            Text = "\uE81C", // History
            FontFamily = PanelUi.IconFont,
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS),
            Opacity = 0.45,
        };
        glyph.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        host.Children.Add(glyph);

        _emptyTitle = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontBody,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceXS),
        };
        _emptyTitle.SetResourceReference(TextElement.ForegroundProperty, "ThemeForeground");

        _emptyHint = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontSmall,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _emptyHint.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");

        host.Children.Add(_emptyTitle);
        host.Children.Add(_emptyHint);
        return host;
    }

    /// <summary>
    /// 列表键盘操作（技能规则：全功能键盘可达）。↑/↓ 交给 ListBox 原生选择，这里补 Enter/Delete/Space/1-9。
    /// <para>
    /// 【为什么挂在 ListBox 上而不是窗口级 InputBindings】数字键 1-9 在搜索框里是**正常输入**，
    /// 窗口级绑定会把它抢走。技能文档给的 `InputBindings` 建议适用于窗口级命令；
    /// **上下文相关的快捷键必须挂在持有焦点的控件上**。
    /// </para>
    /// </summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var entry = CurrentListEntry();

        switch (e.Key)
        {
            // 【2026-09-13 用户裁定】键盘侧**不绑任何默认组合键** —— 这里只保留列表的**标准浏览键**
            //（Enter=激活/复制、Space=勾选、Delete=删除，都是无修饰键的列表惯例，不是"热键"）。
            // "复制并粘贴回原窗口"的键盘入口**不在此硬编码**：想要的用户去**设置**里自行配
            //（用户原话："如果用户想要热键，让用户自己设置就行了，我们提供入口在设置中"）。
            // 鼠标侧默认入口 = **行中键**（见 RecentStrip 的 PasteRequested）。
            case Key.Enter when entry is not null:
                OnRowClicked(entry); // 只复制：Enter 是列表"激活"惯例，保持单键本义
                e.Handled = true;
                return;
            case Key.Enter when entry is not null:
                OnRowClicked(entry); // 复制（与单击一致）
                e.Handled = true;
                return;
            case Key.Delete when entry is not null:
                OnRowDelete(entry);
                e.Handled = true;
                return;
            case Key.Space when entry is not null:
                ToggleSelection(entry); // 勾选（多选）
                e.Handled = true;
                return;
            // 【P2-4】T = 编辑标签（无修饰键列表惯例；标签此前只有 ToolTip 展示、无编辑入口）。
            case Key.T when entry is not null:
                BeginTagEdit(entry);
                e.Handled = true;
                return;
        }

        var nth = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0,
        };
        if (nth > 0)
        {
            CopyNthFromList(nth);
            e.Handled = true;
        }
    }

    /// <summary>当前键盘光标所在条目（ListBox 原生选择项）。</summary>
    private ClipboardEntry? CurrentListEntry()
        => (_entryList?.SelectedItem as RecentStrip)?.Entry;

    /// <summary>
    /// 选中并复制列表第 N 条（兑现行内"1-9"序号徽标 —— 收敛前它是个悬空承诺：界面上写着快捷键，代码里没有）。
    /// 【为什么只复制、不自动粘贴】面板自身可激活（为搜索框能键入），注入 Ctrl+V 的焦点归还时机不可靠 ——
    /// "双击粘贴"正是因此被移除（2026-09-12 用户裁定）。所以 1-9 = 选中并复制，贴到哪由用户决定。
    /// </summary>
    private void CopyNthFromList(int nth)
    {
        if (_entryList is null || nth < 1 || nth > _entryList.Items.Count)
        {
            ShowToast($"列表里没有第 {nth} 条", isError: true);
            return;
        }

        if (_entryList.Items[nth - 1] is not RecentStrip strip)
        {
            return;
        }

        _entryList.SelectedIndex = nth - 1;
        OnRowClicked(strip.Entry); // 内部已有成功/失败反馈
    }

    /// <summary>列表滚动（ListBox 内层 ScrollViewer 冒泡）：接近底部时加载下一页。像素滚动，单位=px。</summary>
    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0)
        {
            return;
        }
        // 像素滚动：提前 320px 预加载下一页（约 4 行）
        if (!_loading && !_allLoaded && e.ExtentHeight > 0
            && e.VerticalOffset >= e.ExtentHeight - 320)
        {
            LoadNextPage();
        }
    }

    // ---- 平滑滚动（2026-09-12 用户反馈：一次滚轮"从头跳到下一个"，无过渡、不连续）----

    /// <summary>每格滚轮滚动的像素数（约 1.5 行；WPF 默认按 3「项」跳，观感生硬）。</summary>
    private const double WheelStepPixels = 120;

    /// <summary>补间时长（ms）；CubicEaseOut 收尾，避免匀速的机械感。</summary>
    private const double ScrollAnimationMs = 220;

    private ScrollViewer? _listScroll;
    private double _scrollFrom;
    private double _scrollTarget;
    private DateTime _scrollStartedAt;
    private bool _scrollAnimating;

    private void OnListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_listScroll is null || e.Delta == 0)
        {
            return;
        }
        // 以**当前实际偏移**为基准累加：连滚时目标自然叠加，不会因动画中途值而丢失位移。
        double target = _listScroll.VerticalOffset - Math.Sign(e.Delta) * WheelStepPixels;
        StartSmoothScroll(target);
        e.Handled = true;
    }

    /// <summary>从当前位置补间到目标偏移（若动画在跑则只更新目标，由帧回调自然收敛）。</summary>
    private void StartSmoothScroll(double target)
    {
        var sv = _listScroll;
        if (sv is null)
        {
            return;
        }
        _scrollFrom = sv.VerticalOffset;
        _scrollTarget = Math.Clamp(target, 0, Math.Max(0, sv.ScrollableHeight));
        _scrollStartedAt = DateTime.UtcNow;
        if (_scrollAnimating)
        {
            return;
        }
        _scrollAnimating = true;
        CompositionTarget.Rendering += OnSmoothScrollFrame;
    }

    private void OnSmoothScrollFrame(object? sender, EventArgs e)
    {
        var sv = _listScroll;
        if (sv is null)
        {
            EndSmoothScroll();
            return;
        }
        double t = (DateTime.UtcNow - _scrollStartedAt).TotalMilliseconds / ScrollAnimationMs;
        if (t >= 1)
        {
            sv.ScrollToVerticalOffset(_scrollTarget);
            EndSmoothScroll();
            return;
        }
        double eased = 1 - Math.Pow(1 - t, 3); // CubicEaseOut
        sv.ScrollToVerticalOffset(_scrollFrom + ((_scrollTarget - _scrollFrom) * eased));
    }

    private void EndSmoothScroll()
    {
        if (!_scrollAnimating)
        {
            return;
        }
        _scrollAnimating = false;
        CompositionTarget.Rendering -= OnSmoothScrollFrame;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var found = FindVisualChild<T>(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static Style CreateListContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        // 【2026-09-12 UI 收敛】此前是 false → 列表项不可聚焦，方向键/Enter/Delete 全都到不了
        //（面板却印着"1-9 快捷键"徽标 = 悬空承诺）。改为可聚焦后，↑/↓ 走 ListBox 原生选择、
        // Enter/Delete/1-9 由 OnListKeyDown 处理；Tab 只把整个列表当一个站点（见 BuildList 的 TabNavigation=Once）。
        style.Setters.Add(new Setter(FocusableProperty, true));
        // 焦点视觉由 RecentStrip 自绘（Accent 边框）：系统虚线框与圆角深色主题不搭且几乎不可见
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        // 关键：ListBoxItem 默认 HorizontalContentAlignment=Left（按内容自适应宽度），会导致整条
        // RecentStrip 被最长内容撑开，内部 Star 文本列拿到的是「自然宽度」而非受限宽度——TextBlock 的
        // Wrap 永不换行，长文本只显示一行并省略（2026-09-12「预览更多/3 行」未生效根因）。Stretch 让行
        // 填满列表宽度，Star 列才真正受限、文本折成 3 行。
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));
        // 禁用选中/hover 高亮（行自身处理视觉）：模板 = Border + ContentPresenter 呈现条目内容——
        // 只放空 Border 会导致条目内容完全不渲染（2026-09-12 列表空白根因，分页 total=3 但列表区空白）。
        var visualTree = new FrameworkElementFactory(typeof(Border));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        visualTree.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = visualTree
        }));
        return style;
    }

    private FrameworkElement BuildFooter()
    {
        // 页脚两行（2026-09-15 用户裁定）：
        //  行 0 = 计数 + 功能按钮（＋表情包 + ⧉📌🎴⊞🗑，操作区主行）；
        //  行 1 = 解释性文字（目标摘要/操作提示）——不挤进主行，避免"共 N 条 + 提示"截断。
        var footer = new Grid { Margin = new Thickness(EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceXS, EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceM) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 行 0：计数（左）+ 功能按钮（右）----
        var row0 = new Grid { Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS) };
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _countLabel = new TextBlock { FontSize = EntryTheme.Scale.FontSmall, VerticalAlignment = VerticalAlignment.Center };
        _countLabel.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");

        // 反馈条与计数**同列并排**：不复用 _countLabel —— 旧实现把"收藏失败"写进计数标签，
        // 覆盖掉"共 N 条"，信息不自洽。独立控件可各自表达、互不干扰。
        _toast = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontSmall,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _toast.SetResourceReference(TextElement.ForegroundProperty, "StatusSuccess");

        var left0 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left0.Children.Add(_countLabel);
        left0.Children.Add(_toast);
        row0.Children.Add(left0);

        // 【2026-09-15 统一操作区】功能项从条目行移到这里：页脚主行右侧一条操作按钮（所有条目共用），
        // 目标 = 当前选中条目。图标用强调色（整体形态：深色底 + 强调色操作区，灰白不符）。
        var right0 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addSticker = CreateSmallButton("＋ 表情包", (_, _) => ImportStickersFromFiles());
        addSticker.Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0); // 覆盖 CreateSmallButton 的默认左边距
        addSticker.ToolTip = "把本地动图（GIF/WebP/APNG…）存进表情包，之后可跨应用粘贴使用";
        right0.Children.Add(addSticker);
        _actionCopyBtn = PanelUi.CreateIconButton("\uE8C8", "复制到剪贴板", (_, _) => ActionOnTarget(OnRowClicked));
        _actionPinBtn = PanelUi.CreateIconButton("\uE718", "收藏", (_, _) => ActionOnTarget(OnRowPin));
        _actionStickerBtn = PanelUi.CreateIconButton("\uE90E", "标记为表情包", (_, _) => ActionOnTarget(OnRowSticker));
        _actionCellPasteBtn = PanelUi.CreateIconButton("\uE80A", "按格粘（逐格粘到业务系统）", (_, _) => ActionOnTarget(OnCellPasteRequested));
        _actionDeleteBtn = PanelUi.CreateIconButton("\uE74D", "删除", (_, _) => ActionOnTarget(OnRowDelete));
        foreach (var b in new[] { _actionCopyBtn, _actionPinBtn, _actionStickerBtn, _actionCellPasteBtn, _actionDeleteBtn })
        {
            b.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        }
        right0.Children.Add(_actionCopyBtn);
        right0.Children.Add(_actionPinBtn);
        right0.Children.Add(_actionStickerBtn);
        right0.Children.Add(_actionCellPasteBtn);
        right0.Children.Add(_actionDeleteBtn);
        Grid.SetColumn(right0, 1);
        row0.Children.Add(right0);
        Grid.SetRow(row0, 0);
        footer.Children.Add(row0);

        // ---- 行 1：解释性文字（左 = 目标摘要）+ 收起提示（右）----
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 【2026-09-15 统一操作区】目标条目摘要放解释行左侧（弹性截断）：
        // 未选中 → "点击条目以操作（也可右键条目）"；选中 → 「分类 · 预览首行」；多选 → 已选 N 项。
        _actionSummary = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _actionSummary.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        row1.Children.Add(_actionSummary);

        var hint = new TextBlock
        {
            Text = AutoHideOnOutsideClick
                ? "单击复制 · Esc 关闭"
                : "单击复制 · 手动收起（✕ / Esc）",
            FontSize = EntryTheme.Scale.FontSmall,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        hint.SetResourceReference(TextElement.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(hint, 1);
        row1.Children.Add(hint);
        Grid.SetRow(row1, 1);
        footer.Children.Add(row1);

        _actionSummary.ToolTip = "中键 = 粘贴回原窗口 · 更多操作见右键菜单";

        // 页脚与列表区之间一条细分隔线（现代化层次；颜色随主题，不额外占空间）
        // 【2026-09-15】改用 BorderStrokeSubtle 而非 ControlBorder：后者是"控件描边"的强度（≈#48484A），
        // 用作整面板宽的横向分割线时过亮，会把页脚变成"一块被框住的区域"；
        // 系统级分割线应当比控件描边更轻（Apple 用 separator ≈ 8% 白）。
        var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        wrap.SetResourceReference(Border.BorderBrushProperty, "BorderStrokeSubtle");
        return wrap;
    }

    /// <summary>
    /// 导入表情包：选本地动图文件（可多选）→ 引擎做「原文件字节级复制 + 内容哈希去重 + Sticker 分类入库」。
    /// <para>
    /// 结果三态反馈（新增 / 重复跳过 / 失败原因）—— 失败必须可见（不静默）；成功后自动切到「表情包」
    /// chip，否则用户可能因为当前筛选而"看不到刚加进去的图"，误以为没成功。
    /// </para>
    /// </summary>
    private void ImportStickersFromFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加表情包（动图）",
            Multiselect = true,
            Filter = "动图与图片|*.gif;*.webp;*.apng;*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        try
        {
            var result = _client.AddStickers(dialog.FileNames);
            PanelLog.Trace($"表情包导入：新增 {result.Added}、重复 {result.Skipped}、失败 {result.Errors.Count}");
            foreach (var err in result.Errors)
            {
                PanelLog.Trace($"表情包导入失败项：{err}");
            }

            ShowToast(
                BuildStickerImportMessage(result),
                isError: result.Added == 0 && result.Errors.Count > 0);

            // 升级也要刷新并切到表情包分类：用户就是想看到"我刚选的图在表情包里"。
            if (result.Added > 0 || result.Upgraded > 0)
            {
                if (_currentFilter != "sticker")
                {
                    _currentFilter = "sticker";
                    UpdateFilterChips();
                }
                Reload();
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"表情包导入失败: {e.Message}");
            ShowToast($"表情包导入失败：{e.Message}", isError: true);
        }
    }

    /// <summary>导入结果文案（新增 / 已在历史中→升级 / 已是表情包跳过 / 失败 四态；失败只列首条，完整清单在 panel.log）。</summary>
    private static string BuildStickerImportMessage(StickerImportResult result)
    {
        var parts = new List<string>();
        if (result.Added > 0)
        {
            parts.Add($"已添加 {result.Added} 个");
        }
        // 【2026-09-13】复制过这张图 → 历史里已有一条，我们把它**原地升级**为表情包（没有新增条目）。
        // 必须说出来：否则用户选了 3 张、看到"已添加 0 个"会以为导入失败。
        if (result.Upgraded > 0)
        {
            parts.Add($"已在历史中 {result.Upgraded} 个，已直接标为表情包");
        }
        if (result.Skipped > 0)
        {
            parts.Add($"已是表情包跳过 {result.Skipped} 个");
        }
        if (result.Errors.Count > 0)
        {
            parts.Add($"失败 {result.Errors.Count} 个：{result.Errors[0]}");
            if (result.Errors.Count > 1)
            {
                parts.Add("（其余见日志）");
            }
        }
        return parts.Count == 0 ? "没有可导入的文件" : string.Join(" · ", parts);
    }

    // 两个按钮工厂已迁到 PanelUi（尺寸 ≥32px、可访问名、语义色统一由那里保证）。
    // 这里保留薄封装，避免为"统一视觉"而在调用点做大面积改名 —— 收敛的目标是**唯一实现**，不是唯一调用写法。
    private static Button CreateIconButton(string glyph, string tooltip, RoutedEventHandler click)
        => PanelUi.CreateIconButton(glyph, tooltip, click);

    private static Button CreateSmallButton(string text, RoutedEventHandler click)
        => PanelUi.CreateActionButton(text, click);

    // ---- 数据 ----

    private void Reload()
    {
        if (_entryList is null) return; // BuildContent 未调用（窗口未首显）时事件先到：跳过，ShowCentered 会补 Reload
        _offset = 0;
        _allLoaded = false;
        _entryList.Items.Clear();
        LoadNextPage();
    }

    private void LoadNextPage()
    {
        if (_loading) return;
        _loading = true;

        var keyword = _searchBox?.Text?.Trim();
        var filter = _currentFilter;

        // 引擎分页直通：kind/category 映射到引擎 query 参数（null=不过滤）
        var client = _client;
        try
        {
            var page = client.GetFilteredEntriesPage(
                // 不再按剪贴板格式(kind)筛选 —— 分类维度统一走 category（见 BuildFilters 说明）
                kind: null,
                category: filter switch
                {
                    "text" => ContentCategory.Text,
                    "code" => ContentCategory.Code,
                    "rich" => ContentCategory.RichText,
                    "image" => ContentCategory.Image,
                    "files" => ContentCategory.File,
                    // 【2026-09-13】表情包不再是分类 —— 它走下方 `sticker` 参数按**标记**筛选
                    //（这样文字颜文字也能出现在表情包列表里）。
                    _ => (ContentCategory?)null
                },
                keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                sourceApp: null,
                offset: _offset,
                limit: PageSize,
                // 「收藏」chip 此前漏传参数 → 引擎收到全 null = 不过滤，点它显示的是全部历史（2026-09-12 修复）
                pinned: filter == "pinned" ? true : null,
                // 【2026-09-13】表情包按**标记**筛选：任意类型条目（含文字颜文字）都能被标记并筛出
                sticker: filter == "sticker" ? true : null);

            _total = page.Total;
            foreach (var entry in page.Items)
            {
                var row = new RecentStrip(entry, _offset + _entryList.Items.Count)
                {
                    // 序号徽章基于当前页内序号（悬浮球/面板一致性：前 9 条快捷键）
                };
                row.Clicked += OnRowClicked;
                row.PlainCopyRequested += OnPlainCopy; // 右键菜单「复制为纯文本」（2026-09-15）
                row.PinRequested += OnRowPin;
                row.StickerRequested += OnRowSticker; // 表情包标记（与收藏同级的独立标记 · 2026-09-13）
                row.PasteRequested += e => PasteEntryToTargetWindow(e); // 中键 = 复制并粘贴回原窗口（单手可达）
                row.TemporaryPasteRequested += PasteEntryTemporarilyToTargetWindow; // Ctrl+中键 = 临时粘贴（P2-3）
                row.TagClicked += OnTagClicked; // 点击 #标签 → 按标签筛选（P2-4）
                row.CellPasteRequested += OnCellPasteRequested; // ⊞ 按格粘（表格逐格粘到业务系统）
                row.DeleteRequested += OnRowDelete;
                row.SelectionToggled += ToggleSelection; // 多选勾选（2026-09-12）
                _entryList.Items.Add(row);
            }

            _offset += page.Items.Count;
            _allLoaded = page.Items.Count < PageSize;
            UpdateFooter();
            RefreshSelectionUi(); // 重建行后把多选状态（含序号）同步到新行
            // 【2026-09-14 修复】一次成功的分页就是"连接可用"的直接证据 → 收起可能残留的误报横幅。
            // 此前 HideEngineBanner 只在 Reconnected 里调用：若横幅是被**非连接类**失败误挂的，
            // 连接从未断开 → 永远不会重连 → 横幅**永久挂着**（而它优先级最高，会把"已暂停捕获"
            // 折叠成"+1 条提醒"）——正是用户看到的"暂停记录时显示与引擎断开连接"。
            if (_engineDown)
            {
                HideEngineBanner();
            }
            PanelLog.Trace($"分页加载成功: total={_total} count={page.Items.Count} offset={_offset} kw=[{keyword}]");
        }
        catch (Exception e)
        {
            PanelLog.Trace($"分页加载失败 offset={_offset}: {e.Message}");
            // 【2026-09-14 修复】只有**真的连不上**才报"引擎未连接"。
            // 此前任何异常（RPC 报错 / 单次超时 / 解析失败）都挂 Danger 横幅 +「启动引擎」按钮，
            // 而文案写着"引擎未连接，正在重试…" —— 引擎明明活着，属于**误报**
            //（用户实测：暂停记录时看到"与引擎断开连接"，实际连接正常）。
            // 非连接类失败改为瞬时提示：不占最高优先级状态位、不压制暂停/存储提醒、不谎报引擎状态。
            if (client.IsConnected)
            {
                ShowToast($"加载失败：{e.Message}", isError: true);
            }
            else
            {
                ShowEngineBanner();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 复制该条目并**直接粘贴回"打开面板时那个窗口"**（2026-09-13）。
    /// <para>
    /// 默认入口只有**一个**：**行中键**（鼠标单手、一次动作）。键盘入口不硬编码 ——
    /// 用户可在**设置 → 剪贴板 → 粘贴回原窗口**自行配一个热键（用户裁定 2026-09-13：
    /// "鼠标中间就可以了，如果用户想要热键，让用户自己设置就行了，我们提供入口在设置中"）。
    /// </para>
    ///
    /// <para>
    /// 【为什么现在能可靠 · 对比 2026-09-12 的移除】当时删掉"双击粘贴"是因为：面板自身可激活
    ///（为搜索框），`Hide()` 之后焦点归属不可靠 → 注入的 Ctrl+V 有时打在自己身上（偶发丢内容）。
    /// 现在两个前提都补齐了：
    /// ① **目标窗口是确定的** —— 在手柄 MouseDown 那一刻就记下（那时前台还是用户的应用）；
    /// ② **激活是显式的** —— <see cref="WindowActivator.Activate"/> 用 AttachThreadInput 范式
    ///    + ALT 抖动兜底，不再赌"Hide 之后前台恰好落在谁身上"这种时序运气。
    /// </para>
    /// <para>
    /// 【顺序为什么是"先收起 → 再激活 → 最后注入"】面板隐藏时 WPF 会把焦点移交给同进程的手柄，
    /// 若先注入就会打进自家窗口；故先收起（<see cref="OnBeforeHide"/> 已归还前台），
    /// 再显式激活目标兜底，最后**延迟**注入 —— 激活是异步生效的，立刻注入仍会打到旧前台。
    /// </para>
    /// </summary>
    private void PasteEntryToTargetWindow(ClipboardEntry entry, bool temporarily = false)
    {
        var target = EdgeHandleWindow.ForegroundBeforeOpen;
        _hookPaused = true; // 自注入的 Ctrl+V 会回到我们自己的键盘钩子 → 注入期间整体放行（见 _hookPaused 注释）

        HidePopup();
        var activated = WindowActivator.Activate(target);
        PanelLog.Trace($"粘贴回原窗口：target=0x{target:X} activated={activated}");

        // 激活异步生效（窗口管理器要完成 Z 序与焦点转移），立刻注入会打进旧前台。
        // 120ms 足够且无感（同类实现用 150ms）。
        var inject = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        inject.Tick += (_, _) =>
        {
            inject.Stop();
            try
            {
                if (temporarily)
                {
                    // 【P2-3】临时粘贴：写回 + 注入后，客户端会把剪贴板**还原**成用户原来的内容。
                    _client.PasteEntryTemporarilyToActiveWindow(entry);
                }
                else
                {
                    _client.PasteEntryToActiveWindow(entry); // 写回剪贴板（引擎侧登记回环指纹）+ 注入 Ctrl+V
                }
                // 记录**实际按下的键**：终端走 Shift+Insert、其余走 Ctrl+V，由注入方式决定。
                // 写死"Ctrl+V"会在排障时误导（2026-09-13 自查发现），所以读客户端回报的实际键。
                PanelLog.Trace($"粘贴回原窗口：已注入粘贴按键（{_client.LastInjectKey ?? "未知"}）");
            }
            catch (Exception e)
            {
                PanelLog.Trace($"粘贴回原窗口失败: {e.Message}");
            }

            // 与按序粘贴同构：注入后 500ms 内放行钩子（覆盖"注入 + 分发 + 回钩"全程）
            var resume = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            resume.Tick += (_, _) =>
            {
                resume.Stop();
                _hookPaused = false;
            };
            resume.Start();
        };
        inject.Start();
    }

    /// <summary>
    /// 【P2-3 临时粘贴 · 2026-09-13】与 <see cref="PasteEntryToTargetWindow"/> 完全同序，
    /// 区别只在注入后由客户端把剪贴板**还原**成用户原来的内容（入口：Ctrl+中键）。
    /// </summary>
    private void PasteEntryTemporarilyToTargetWindow(ClipboardEntry entry) =>
        PasteEntryToTargetWindow(entry, temporarily: true);

    // ---------------- 【P2-4 标签体系】 ----------------

    /// <summary>
    /// 点击标签 chip → 搜索框切到 `tag:标签`（由**引擎**解析 tag 语义，面板只透传，避免两处漂移）。
    /// </summary>
    private void OnTagClicked(string tag)
    {
        if (_searchBox is null || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        _searchBox.Text = $"tag:{tag}";
        _searchDebounce.Stop(); // 赋值会启动 300ms 防抖 → 立即 Reload 一次，点击即见结果
        Reload();
        ShowToast($"已按标签「{tag}」筛选");
    }

    /// <summary>打开标签编辑浮层（列表键盘 `T`），预填当前标签并聚焦。</summary>
    private void BeginTagEdit(ClipboardEntry entry)
    {
        if (_tagEditorHost is null || _tagEditorBox is null)
        {
            return;
        }

        _tagEditorEntry = entry;
        _tagEditorBox.Text = entry.Tags ?? string.Empty;
        _tagEditorHost.Visibility = Visibility.Visible;
        // 用 Input 优先级聚焦：面板已可见，直接 Focus 足够（不 Activate，避免抢走"目标窗口"前台）。
        _tagEditorBox.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _tagEditorBox.Focus();
                _tagEditorBox.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    private void HideTagEditor()
    {
        if (_tagEditorHost is null)
        {
            return;
        }

        _tagEditorHost.Visibility = Visibility.Collapsed;
        _tagEditorEntry = null;
    }

    /// <summary>保存标签（走既有 `set_tags`；空串 = 清除）。引擎不广播该变更 → 本地同步 + 重建行。</summary>
    private void CommitTagEdit()
    {
        var entry = _tagEditorEntry;
        var box = _tagEditorBox;
        if (entry is null || box is null)
        {
            HideTagEditor();
            return;
        }

        var tags = box.Text?.Trim() ?? string.Empty;
        try
        {
            _client.SetEntryTags(entry, tags);
            entry.Tags = tags;
            RefreshRow(entry);
            ShowToast(tags.Length == 0 ? "已清除标签" : $"已设置标签：{tags}");
        }
        catch (Exception e)
        {
            PanelLog.Trace($"标签保存失败: {e.Message}");
            ShowToast("标签保存失败", isError: true);
        }

        HideTagEditor();
    }

    /// <summary>标签编辑浮层（初始折叠；Enter 保存 / Esc 取消）。</summary>
    private FrameworkElement BuildTagEditor()
    {
        _tagEditorBox = new TextBox
        {
            FontSize = EntryTheme.Scale.FontBody,
            Padding = new Thickness(EntryTheme.Scale.SpaceS, 6, EntryTheme.Scale.SpaceS, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = ThemeBrushes.Get("ThemeForeground"),
        };
        _tagEditorBox.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        _tagEditorBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitTagEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideTagEditor();
                e.Handled = true;
            }
        };

        var boxBorder = new Border
        {
            Child = _tagEditorBox,
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusS),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS),
        };
        boxBorder.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        boxBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(PanelUi.CreateActionButton("取消", (_, _) => HideTagEditor()));
        buttons.Children.Add(PanelUi.CreateActionButton("保存", (_, _) => CommitTagEdit()));

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(new TextBlock
        {
            Text = "编辑标签（空格分隔多个；留空 = 清除）",
            FontSize = EntryTheme.Scale.FontSmall,
            Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS),
            Foreground = ThemeBrushes.Get("ThemeMutedForeground"),
        });
        content.Children.Add(boxBorder);
        content.Children.Add(buttons);

        var card = new Border
        {
            Child = content,
            Padding = new Thickness(EntryTheme.Scale.SpaceM),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusL),
            BorderThickness = new Thickness(1),
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        PanelUi.Name(card, "编辑标签");

        _tagEditorHost = card;
        return card;
    }

    // ---------------- 【按格粘 · 2026-09-13】表格逐格粘到业务系统 ----------------

    /// <summary>
    /// 点击行内 `⊞` → 打开按格粘选项浮层（先让用户确认"解读对不对 + 粘完按什么键"）。
    /// <para>
    /// 【为什么要浮层而不是直接开粘】表格形态与目标系统千差万别（用户口径："我们固定的形式无法应对"），
    /// 所以：① 先亮出"几行几列几格"让用户确认我们拆对了；② 自动按键由用户当场选。
    /// </para>
    /// </summary>
    private void OnCellPasteRequested(ClipboardEntry entry)
    {
        if (_cellPasteHost is null || _cellPasteSummary is null)
        {
            return;
        }

        if (!ClipboardTableCells.IsTabular(entry, out var rows, out var cols, out var count))
        {
            ShowToast("这条不是可逐格粘贴的表格（需要多列 Tab 结构）", isError: true);
            return;
        }

        if (count > ClipboardTableCells.MaxCells)
        {
            ShowToast(
                $"表格过大：{rows} 行 × {cols} 列 = {count} 格（上限 {ClipboardTableCells.MaxCells} 格）",
                isError: true);
            return;
        }

        _cellPasteEntry = entry;
        _cellPasteSummary.Text = $"本表：{rows} 行 × {cols} 列 = {count} 格（顺序＝行优先）";

        // 默认档 = 设置里的默认值（面板无写权，浮层选择只对本次会话生效）
        _cellPasteChoice = PanelTheme.CellPasteAutoKeySetting() switch
        {
            "none" => CellPasteAutoKey.None,
            "enter" => CellPasteAutoKey.Enter,
            _ => CellPasteAutoKey.Tab,
        };
        foreach (var (button, key) in _cellPasteOptions)
        {
            button.IsChecked = key == _cellPasteChoice;
        }

        _cellPasteHost.Visibility = Visibility.Visible;
        PanelLog.Trace($"按格粘：打开选项浮层 rows={rows} cols={cols} count={count}");
    }

    /// <summary>确认 → 入队 → 收起面板（Ctrl+V 必须打到目标窗口，与按序粘贴同因）。</summary>
    private void CommitCellPaste()
    {
        var entry = _cellPasteEntry;
        if (entry is null)
        {
            HideCellPasteEditor();
            return;
        }

        try
        {
            _client.BeginCellSequentialPaste(entry, _cellPasteChoice);
        }
        catch (Exception e)
        {
            // 启动失败保持浮层开着，让用户能改选择或取消（不静默关闭）
            PanelLog.Trace($"按格粘启动失败: {e.Message}");
            ShowToast($"按格粘启动失败：{e.Message}", isError: true);
            return;
        }

        var remaining = _client.SequentialRemaining;
        _sequentialTotal = remaining;
        _sequentialIsCell = true;
        // 按格粘的队列是"单元格"而非条目：用整段表格文本做预览（后续可细化为"下一格"）
        _sequentialPreviews = new System.Collections.Generic.List<string> { entry.Content ?? string.Empty };
        HideCellPasteEditor();
        _sequentialNotice = $"按格粘：共 {remaining} 格，去目标窗口每按一次 Ctrl+V 粘一格";
        UpdateSequentialBar();
        ShowToast($"按格粘就绪：{remaining} 格");
        PanelLog.Trace($"按格粘开始：{remaining} 格 autoKey={_cellPasteChoice}");
        HidePopup(); // 必须收起：面板自己是前台时 Ctrl+V 会打在自己身上
    }

    private void HideCellPasteEditor()
    {
        if (_cellPasteHost is null)
        {
            return;
        }

        _cellPasteHost.Visibility = Visibility.Collapsed;
        _cellPasteEntry = null;
    }

    /// <summary>按格粘选项浮层（初始折叠；Esc 取消）。</summary>
    private FrameworkElement BuildCellPasteEditor()
    {
        _cellPasteSummary = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontBody,
            Foreground = ThemeBrushes.Get("ThemeForeground"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, EntryTheme.Scale.SpaceS),
        };

        var options = new StackPanel { Orientation = Orientation.Vertical };
        _cellPasteOptions.Clear();
        foreach (var (label, key) in new[]
                 {
                     ("Tab —— 粘完跳到下一格（最常见的表单形态）", CellPasteAutoKey.Tab),
                     ("Enter —— 粘完换行 / 确认（部分系统用回车）", CellPasteAutoKey.Enter),
                     ("不自动 —— 只粘贴，按键我自己来", CellPasteAutoKey.None),
                 })
        {
            var captured = key;
            var radio = new RadioButton
            {
                Content = label,
                FontSize = EntryTheme.Scale.FontBody,
                GroupName = "CellPasteAutoKey",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = ThemeBrushes.Get("ThemeForeground"),
            };
            radio.Checked += (_, _) => _cellPasteChoice = captured;
            _cellPasteOptions.Add((radio, key));
            options.Children.Add(radio);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(PanelUi.CreateActionButton("取消", (_, _) => HideCellPasteEditor()));
        buttons.Children.Add(PanelUi.CreateActionButton("开始按格粘", (_, _) => CommitCellPaste()));

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(new TextBlock
        {
            Text = "按格粘：每次 Ctrl+V 粘一个格子",
            FontSize = EntryTheme.Scale.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.Get("ThemeForeground"),
        });
        content.Children.Add(_cellPasteSummary);
        content.Children.Add(new TextBlock
        {
            Text = "粘完自动：",
            FontSize = EntryTheme.Scale.FontSmall,
            Foreground = ThemeBrushes.Get("ThemeMutedForeground"),
        });
        content.Children.Add(options);
        content.Children.Add(buttons);

        var card = new Border
        {
            Child = content,
            Padding = new Thickness(EntryTheme.Scale.SpaceM),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusL),
            BorderThickness = new Thickness(1),
            MaxWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        PanelUi.Name(card, "按格粘选项");

        _cellPasteHost = card;
        return card;
    }

    /// <summary>
    /// 切换「表情包」标记（2026-09-13）。
    /// <para>
    /// 用户口径："跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" —— 所以这里**不做任何内容类型判断**：
    /// 文字颜文字、静态图、动图、文件都能标记。标记只影响两件事：① 出现在「表情包」筛选里；
    /// ② 与收藏一样豁免驱逐与「清理未收藏」。
    /// </para>
    /// 与 <see cref="OnRowPin"/> 同构（引擎侧 set_sticker 也不广播 → 本地状态与行内视觉必须自更新）。
    /// </summary>
    private void OnRowSticker(ClipboardEntry entry)
    {
        try
        {
            var marking = !entry.IsSticker;
            _client.SetSticker(entry, marking);
            entry.IsSticker = marking;
            ShowToast(marking ? "已标记为表情包" : "已取消表情包标记");

            if (_currentFilter == "sticker" && !entry.IsSticker)
            {
                // 取消标记 → 该条不再属于「表情包」结果集，整页重载
                Reload();
            }
            else
            {
                RefreshRow(entry);
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"切换表情包标记失败: {e.Message}");
            ShowToast($"操作失败：{e.Message}", isError: true);
        }
    }

    private void OnRowClicked(ClipboardEntry entry)
    {
        // 单击 = 复制到剪贴板（不自动粘贴，避免误贴）。
        //
        // 【Ctrl+点击 = 复制为纯文本 · 2026-09-13 用户要求"两全"】这是"文件夹适配"的答案：
        //  · 默认复制带 `CF_HDROP` → 文件管理器粘贴得到**完整的文件夹** ✓；
        //  · 但网页聊天框会把目录**展开成一堆子项**（浏览器按"上传文件夹"处理）——
        //    想在那里贴出**路径文字**，就用纯文本复制：剪贴板里**只有文本**，任何输入框都只能得到它 ✓。
        // 两条通道各自完整，不互相牺牲（不是"二选一"）。
        // 代码条目本来就强制纯文本（粘到编辑器不该带 HTML/RTF）。
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        try
        {
            if (ctrl || entry.Category == ContentCategory.Code)
            {
                _client.CopyEntryAsPlainText(entry);
                ShowToast(ctrl ? "已复制为纯文本（文件夹/文件 → 路径文字）" : "已复制到剪贴板");
            }
            else
            {
                _client.CopyEntryToClipboard(entry);
                ShowToast("已复制到剪贴板");
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"复制失败: {e.Message}");
            ShowToast($"复制失败：{e.Message}", isError: true);
        }
    }

    /// <summary>
    /// 收藏 / 取消收藏。
    /// 【2026-09-12 修复】引擎 `pin`/`unpin` **不广播** history_changed（有意为之：若广播，面板的
    /// HistoryChanged→Reload 会把列表整页重建、滚动位置丢失）。因此本地状态与行内视觉必须自更新 ——
    /// 此前两者都没做：再点一次仍是 Pin（取消不了收藏）、★ 标记也不动（用户实测"收藏的问题"）。
    /// </summary>
    private void OnRowPin(ClipboardEntry entry)
    {
        try
        {
            if (entry.IsPinned)
            {
                _client.UnpinEntry(entry);
                entry.IsPinned = false;
            }
            else
            {
                _client.PinEntry(entry);
                entry.IsPinned = true;
            }

            if (_currentFilter == "pinned" && !entry.IsPinned)
            {
                // 取消收藏 → 该条不再属于「收藏」结果集，整页重载
                Reload();
            }
            else
            {
                RefreshRow(entry);
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"收藏失败: {e.Message}");
            // 失败要让用户看见（如收藏数达 pinned-limit），否则表现为"点了没反应"。
            // 走独立反馈条而非改写 _countLabel —— 后者会覆盖"共 N 条"造成信息不自洽（2026-09-12 审计）。
            ShowToast("收藏失败（可能已达收藏上限）", isError: true);
        }
    }

    /// <summary>重建单个条目的行以刷新视觉（如 pin 状态），保持列表顺序与滚动位置不变。</summary>
    private void RefreshRow(ClipboardEntry entry)
    {
        for (var i = 0; i < _entryList.Items.Count; i++)
        {
            if (_entryList.Items[i] is RecentStrip strip && strip.Entry.Id == entry.Id)
            {
                var row = new RecentStrip(entry, strip.Index);
                row.Clicked += OnRowClicked;
                row.PlainCopyRequested += OnPlainCopy; // 右键菜单「复制为纯文本」（2026-09-15）
                row.PinRequested += OnRowPin;
                row.StickerRequested += OnRowSticker; // 表情包标记（与收藏同级的独立标记 · 2026-09-13）
                row.PasteRequested += e => PasteEntryToTargetWindow(e); // 中键 = 复制并粘贴回原窗口（单手可达）
                row.TemporaryPasteRequested += PasteEntryTemporarilyToTargetWindow; // Ctrl+中键 = 临时粘贴（P2-3）
                row.TagClicked += OnTagClicked; // 点击 #标签 → 按标签筛选（P2-4）
                row.CellPasteRequested += OnCellPasteRequested; // ⊞ 按格粘（表格逐格粘到业务系统）
                row.DeleteRequested += OnRowDelete;
                // 【2026-09-12 修复】重建行必须把四个交互全接上 —— 漏掉 SelectionToggled 会让
                // 该行"多选勾选"永久失效（pin 一次后点勾选圈毫无反应），且新行也需按 _selectedOrder 回填视觉。
                row.SelectionToggled += ToggleSelection;
                // 【2026-09-13】替换选中项会让 WPF `Selector` 清空 `SelectedItem` →
                // 之后 Enter/Delete/Space（都走 CurrentListEntry）会**静默失效**，用户感觉"键盘忽然不灵了"。
                // 故替换后把键盘光标恢复回来。本次改造新增了第三个调用方（批量标记），更容易撞上。
                var wasSelected = _entryList.SelectedIndex == i;
                _entryList.Items[i] = row;
                if (wasSelected)
                {
                    _entryList.SelectedIndex = i;
                }
                RefreshSelectionUi();
                return;
            }
        }
    }

    private void OnRowDelete(ClipboardEntry entry)
    {
        try
        {
            _client.DeleteEntry(entry);
        }
        catch (Exception e)
        {
            PanelLog.Trace($"删除失败: {e.Message}");
            ShowToast($"删除失败：{e.Message}", isError: true);
            return;
        }

        ShowToast("已删除该条");

        // 【2026-09-12 修复】删除后必须同时收口两处：
        // ① 把该条移出勾选队列 —— 否则 `_selected` 残留死条目，一点「按序粘贴」就失效；
        // ② 从列表移除该行（**原地移除而非整页 Reload** —— 后者会把滚动位置弹回顶部）。
        DropSelection(entry.Id);
        RemoveRow(entry.Id);
    }

    /// <summary>
    /// 从列表**原地**移除一行（不整页 Reload —— 后者会把滚动位置弹回顶部、观感是"列表跳了一下"）。
    /// <para>
    /// `_offset` 必须同步 -1：它是"已加载条数"游标（分页 offset），不减会让触底加载**漏掉一条**；
    /// `_total` 与底部计数同步 -1。
    /// </para>
    /// </summary>
    private void RemoveRow(string id)
    {
        for (var i = 0; i < _entryList.Items.Count; i++)
        {
            if (_entryList.Items[i] is RecentStrip strip && strip.Entry.Id == id)
            {
                _entryList.Items.RemoveAt(i);
                break;
            }
        }

        _offset = Math.Max(0, _offset - 1);
        _total = Math.Max(0, _total - 1);
        UpdateFooter();
    }

    /// <summary>
    /// 把某条从勾选队列剔除（条目已被删除时调用），并重算序号、刷新两条操作条。
    /// 与 <see cref="ClearSelection"/> 的区别：只动一条，其余勾选与顺序保持不变。
    /// </summary>
    private void DropSelection(string id)
    {
        if (!_selectedOrder.ContainsKey(id))
        {
            return;
        }

        _selected.RemoveAll(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (_selected.Count == 0)
        {
            _multiSelectMode = false;
        }

        ReindexSelection();
        RefreshSelectionUi();
    }

    // 【双击粘贴已于 2026-09-12 移除】用户裁定"没用，而且我们也不用这个功能"。
    // 它原本是"复制 + 直接贴回原窗口"（SendPaste 发 Ctrl+V 给当前前台窗口），
    // 但面板自身可激活（UseNoActivateWindowStyle=false，为搜索框），焦点归还时机不可靠
    // → 实测无效。既然产品不需要，代码与提示文案一并撤除，避免留一个点不动的死交互。
    // 现在双击等价于两次单击（复制两次，幂等无害）。

    /// <summary>
    /// 在底部栏显示一条操作反馈（3 秒后自动隐去）。
    /// 失败路径必须调用它 —— 静默失败在用户侧等价于"点了没反应/状态没变"。
    /// </summary>
    private void ShowToast(string message, bool isError = false)
    {
        if (_toast is null)
        {
            return;
        }

        _toast.SetResourceReference(TextElement.ForegroundProperty, isError ? "StatusDanger" : "StatusSuccess");
        _toast.Text = message;
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// 底部计数 + 空状态裁决。
    /// 【2026-09-12 UI 收敛】**引擎断开时抑制空状态** —— 此时列表必然为空，但真正该说的是"引擎未连接"
    ///（状态槽已显示）；再挂一条"暂无剪贴板历史，复制后将自动记录"就是自相矛盾（旧实现两条同时出现）。
    /// </summary>
    private void UpdateFooter()
    {
        _countLabel.Text = _total > 0 ? $"共 {_total} 条" : "";

        var searching = !string.IsNullOrWhiteSpace(_searchBox?.Text);
        var showEmpty = _total == 0 && !_engineDown;

        if (_emptyState is not null)
        {
            _emptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
        if (showEmpty && _emptyTitle is not null)
        {
            _emptyTitle.Text = searching ? "没有匹配的结果" : "暂无剪贴板历史";
            _emptyHint.Text = searching ? "换个关键词试试" : "复制任意内容后将自动记录";
        }
    }

    // ---- 暂停 ----

    private void TogglePause()
    {
        try
        {
            if (_client.IsTemporarilyPaused)
            {
                Resume();
            }
            else
            {
                _client.PauseTemporarily(60);
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"暂停失败: {e.Message}");
            // ToggleButton 已自行取反 → 失败时按引擎真实状态回正，避免图标与状态不一致
            if (_pauseBtn is not null)
            {
                _pauseBtn.IsChecked = _client.IsTemporarilyPaused;
            }
            // 【2026-09-14】失败必须说出来：此处此前只落日志 —— 用户看到的是"点了没反应"
            //（图标回正 + 没有任何横幅），与 ShowToast 的既定纪律（失败路径必须提示）不符。
            ShowToast($"暂停失败：{e.Message}", isError: true);
        }
    }

    private void Resume()
    {
        try
        {
            _client.Resume();
        }
        catch (Exception e)
        {
            PanelLog.Trace($"恢复失败: {e.Message}");
            ShowToast($"恢复失败：{e.Message}", isError: true);
        }
    }

    /// <summary>
    /// 暂停状态**唯一**的 UI 真源（由引擎事件驱动）。
    /// 【2026-09-12 修复】此前 ToggleButton 的 IsChecked 只在点击时自动取反，从**别的路径**恢复
    /// （点横幅「恢复」、60s 自动到期、外部 Resume）时没有任何代码复位它 → 图标一直保持选中态（用户实测）。
    /// 现在所有路径最终都落到这里，按引擎的真实状态同步。
    /// </summary>
    private void OnPauseState(bool paused)
    {
        if (_pauseBtn is not null)
        {
            _pauseBtn.IsChecked = paused;
        }

        // 【2026-09-12 修复】构造函数里就订阅了 PauseStateChanged —— BuildContent 之前事件先到的话，
        // _pauseBanner/_pauseText 还是 null，直接访问会 NRE（被 App 的 DispatcherUnhandledException
        // 兜住不崩，但事件被吞、横幅状态错乱 = 静默失败）。
        if (_pauseBanner is null || _pauseText is null)
        {
            return;
        }

        if (paused)
        {
            _pauseRemaining = 60;
            _pauseText.Text = $"已暂停捕获（剩余 {_pauseRemaining}s）";
            SetStatusLane(StatusLane.Pause, true);
            _pauseTick.Start();
        }
        else
        {
            _pauseTick.Stop();
            SetStatusLane(StatusLane.Pause, false);
        }
    }

    private void UpdatePauseRemaining()
    {
        if (_pauseText is null || _pauseBanner is null)
        {
            return; // BuildContent 前（定时器理论上不会跑到这里，防御性判空）
        }

        _pauseRemaining = Math.Max(0, _pauseRemaining - 1);
        _pauseText.Text = $"已暂停捕获（剩余 {_pauseRemaining}s）";
        if (_pauseRemaining <= 0)
        {
            _pauseTick.Stop();
            SetStatusLane(StatusLane.Pause, false);
        }
    }

    // ---- 引擎状态 ----

    /// <summary>
    /// 引擎断线：登记最高优先级状态，并**抑制空状态** ——
    /// "引擎未连接"与"暂无剪贴板历史，复制后将自动记录"语义互斥，不能同时出现（旧实现两条一起显示）。
    /// </summary>
    private void ShowEngineBanner()
    {
        if (_engineBanner is null)
        {
            return; // BuildContent 之前（如 ctor 订阅的 Reconnected 先到）→ 不能直接访问
        }

        _engineDown = true;
        if (_engineText is not null)
        {
            _engineText.Text = "引擎未连接，正在重试…";
        }
        SetStatusLane(StatusLane.Engine, true);
        UpdateFooter(); // 空状态裁决依赖 _engineDown
    }

    private void HideEngineBanner()
    {
        if (_engineBanner is null)
        {
            return; // 同上
        }

        _engineDown = false;
        SetStatusLane(StatusLane.Engine, false);
        UpdateFooter(); // 恢复"暂无历史"提示的资格
    }

    // ---- 存储提醒（软限制）与运行期配置刷新 ----

    /// <summary>
    /// 重读运行期配置 —— 2026-09-12 用户要求"修改实时生效"。
    /// 面板是独立进程、不接宿主设置事件总线，故在**每次打开面板**时重读 settings.json。
    /// 覆盖 dismiss-mode（收起方式）与 entry-style（入口显隐）；引擎侧参数本就由宿主经
    /// apply_settings 热推送，无需面板干预。
    /// </summary>
    private void RefreshRuntimeConfig()
    {
        PanelTheme.Load(); // 此前只在进程启动时读一次 → 改完设置必须重启面板才生效
        _dismissMode = PanelTheme.ExtensionConfig().DismissMode;

        // 【2026-09-13】粘贴按键注入方式。
        // 默认 **Ctrl+V**（= 旧行为）：cmd / PowerShell / Windows Terminal 都支持 Ctrl+V（用户实测），
        // 只有 mintty / PuTTY 这类少数终端才需要 Shift+Insert —— 由用户按需切换，我们**不擅自换**。
        _client.InjectMode = PanelTheme.PasteInjectModeSetting().Trim().ToLowerInvariant() switch
        {
            "auto" => PasteInjectMode.Auto,
            "shift-insert" or "shiftinsert" => PasteInjectMode.ShiftInsert,
            _ => PasteInjectMode.CtrlV,
        };
        try
        {
            _host.RefreshEntryStyle(); // entry-style=off 立即隐藏手柄，改回立即出现
        }
        catch (Exception e)
        {
            PanelLog.Trace($"刷新入口形态失败（忽略）: {e.Message}");
        }

        // 【2026-09-13 缺口修复】"打开面板时那个窗口"此前**只在点侧边栏手柄**时记录
        //（EdgeHandleWindow.OnHandleMouseDown），于是经**全局热键 / 右键命令**唤起的面板没有目标窗口，
        // "粘贴回原窗口"（中键 / 自定义热键）无处可贴 —— 表现是"按了没反应"。
        // 此处统一补记：本方法在 ShowAt() **之前**调用，此刻前台仍是用户窗口。
        // 只记**本进程之外**的窗口（否则可能把自家手柄/面板当目标）。
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsOwnProcessWindow(fg))
            {
                EdgeHandleWindow.ForegroundBeforeOpen = fg;
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"记录目标窗口失败（忽略）: {e.Message}");
        }

        // 注意：**不要**在这里查存储横幅 —— 本方法在 ShowAt() 之前调用，那时 BuildContent()
        // 还没执行、_storageBanner 仍为 null（会被判空直接 return，表现为"超限也不提醒"）。
        // 存储横幅由 Show* 在 ShowAt() 之后单独刷新（2026-09-12 实测踩坑）。
    }

    // ---- 用户自定义热键：「复制并粘贴回原窗口」----

    /// <summary>
    /// 按当前配置（重新）注册用户热键。
    /// <para>
    /// **调用时机**：面板每次显示之后（见 <see cref="EnsureLoaded"/>）—— `RegisterHotKey` 需要
    /// 窗口 HWND 已创建且消息循环在跑，故不能在 <see cref="RefreshRuntimeConfig"/> 里做
    ///（那时 `ShowAt()` 还没调用、句柄为空）。未配置/非法/冲突时一律不注册（默认就是未配置）。
    /// </para>
    /// </summary>
    private void ApplyPasteBackHotkey()
    {
        UnregisterPasteBackHotkey(); // 先释放旧键 —— 用户可能刚改过组合，旧键必须跟着换

        var spec = PanelTheme.PasteBackHotkey();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return; // 未配置 = 不启用（默认状态；鼠标中键才是默认入口）
        }

        if (!HotkeySpec.TryParse(spec, out var mods, out var mainKey, out var canonical))
        {
            PanelLog.Trace($"用户热键「{spec}」格式非法（需至少一个修饰键 + 一个主键），已忽略");
            return;
        }

        if (HotkeySpec.ConflictsWithBuiltIn(canonical))
        {
            PanelLog.Trace($"用户热键「{canonical}」与内置全局热键冲突，已忽略（请在设置里换一个）");
            return;
        }

        // 主键名（WPF Key 枚举名）→ 虚拟键码。两侧约定用同一书写形式，见 HotkeySpec 类注释。
        if (!Enum.TryParse<Key>(mainKey, ignoreCase: true, out var key) || key == Key.None)
        {
            PanelLog.Trace($"用户热键主键「{mainKey}」无法识别，已忽略");
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            PanelLog.Trace($"用户热键主键「{mainKey}」无对应虚拟键码，已忽略");
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            PanelLog.Trace("面板句柄尚未就绪，本次不注册用户热键（下次打开面板再试）");
            return;
        }

        if (_hotkeySource is null)
        {
            _hotkeySource = HwndSource.FromHwnd(handle);
            _hotkeySource?.AddHook(OnHotkeyMessage);
        }

        if (NativeMethods.RegisterHotKey(handle, PasteBackHotkeyId, mods, (uint)vk))
        {
            _pasteBackHotkeyRegistered = true;
            PanelLog.Trace(
                $"用户热键已注册：{HotkeySpec.Pretty(canonical)} = 复制并粘贴回原窗口（仅面板打开期间有效）");
        }
        else
        {
            // 最常见原因是组合已被别的程序占用 → 必须说出来，否则用户只会觉得"设置没生效"
            PanelLog.Trace($"用户热键注册失败（组合可能已被其它程序占用）：{canonical}");
            ShowToast($"{HotkeySpec.Pretty(canonical)} 已被占用，热键未生效", isError: true);
        }
    }

    /// <summary>注销用户热键（面板收起时调用；幂等）。</summary>
    private void UnregisterPasteBackHotkey()
    {
        if (!_pasteBackHotkeyRegistered)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(handle, PasteBackHotkeyId);
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"注销用户热键失败（忽略）: {e.Message}");
        }

        _pasteBackHotkeyRegistered = false;
    }

    /// <summary>
    /// 窗口消息钩子：`WM_HOTKEY` → 执行"复制并粘贴回原窗口"。
    /// 与**行中键单击**走同一个方法，行为完全一致 —— 用户自配的热键只是它的键盘等价入口。
    /// </summary>
    private IntPtr OnHotkeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey || wParam.ToInt32() != PasteBackHotkeyId)
        {
            return IntPtr.Zero;
        }

        handled = true;
        // 记一行"热键被触发"：这是排障的**分水岭** —— 用户报"热键没反应"时，
        // 有这行就能立刻分清是"热键根本没生效"（未配置/被占用/面板已收起）
        // 还是"生效了但当下没有选中项"（后者会紧跟下面那条日志）。
        PanelLog.Trace("用户热键触发：复制并粘贴回原窗口");

        var entry = CurrentListEntry();
        if (entry is null)
        {
            // 没有选中项时不能静默：用户按了键却没动静，会以为热键坏了
            PanelLog.Trace("用户热键：列表无选中项 → 提示用户先选中");
            ShowToast("先在列表里选中一条，再按热键", isError: true);
            return IntPtr.Zero;
        }

        PasteEntryToTargetWindow(entry);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 该窗口是否属于本进程 —— 记录"打开面板前的目标窗口"时用它排除自家手柄/面板
    ///（否则会把目标记成自己，粘贴打进空气）。
    /// </summary>
    private static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 查询引擎存储状态并刷新提醒横幅。总预算为**软限制**：超限不删数据，仅提醒。
    /// 引擎不可达时静默收起（断线由引擎横幅负责，不叠加噪音）。
    /// </summary>
    private void RefreshStorageBanner()
    {
        if (_storageBanner is null || _storageText is null)
        {
            return; // BuildContent 之前
        }

        try
        {
            var s = _client.GetStorageStatus();
            if (s.OverBudget)
            {
                _storageText.Text = $"存储已用 {s.UsedMb} MB / 预算 {s.BudgetMb} MB（共 {s.Entries} 条）· 建议清理";
                SetStatusLane(StatusLane.Storage, true);
            }
            else
            {
                SetStatusLane(StatusLane.Storage, false);
            }
        }
        catch (Exception e)
        {
            PanelLog.Trace($"查询存储状态失败（忽略）: {e.Message}");
            SetStatusLane(StatusLane.Storage, false);
        }
    }

    /// <summary>横幅「清理未收藏」：删除全部未收藏条目（收藏条目受保护，永不参与）。</summary>
    private void ClearUnpinnedFromStorageBanner()
    {
        try
        {
            _client.ClearAllUnpinned();
            PanelLog.Trace("用户从存储提醒横幅清理了未收藏条目");
            ShowToast("已清理未收藏条目");
        }
        catch (Exception e)
        {
            // 失败不得继续刷新（否则横幅消失、界面呈现"已清空"的假象）
            PanelLog.Trace($"清理未收藏失败: {e.Message}");
            ShowToast($"清理失败：{e.Message}", isError: true);
            return;
        }

        // 整批未收藏条目已被删除 → 勾选队列必然失效，必须一起清掉
        //（否则按序粘贴时逐条 entry not found，表现正是"列表自己释放"）。
        ClearSelection();
        RefreshStorageBanner();
        Reload();
    }

    // ---- 多选 / 按序粘贴 / 批量删除（2026-09-12 新增）----

    /// <summary>
    /// 按序粘贴提醒（状态槽 · Action 级）：显示"剩余 N 条"与按键提示 + 取消；会话结束后继续承载
    /// **过程提示**（哪几条失效、哪几条走了快照降级），用户重开面板仍能看到，点「取消」清空。
    /// </summary>
    private Border BuildSequentialBar()
        => PanelUi.CreateStatusCard(
            StatusKind.Action, "⏯", out _sequentialText,
            ("取消", (_, _) => CancelSequentialPaste()));

    /// <summary>
    /// 多选操作条（列表底部浮层）。
    /// <para>
    /// 【2026-09-12 UI 收敛】① 它是**中性操作**，不再套警告色；② 从"顶部常驻占位的一行"改为**列表底部浮层**
    /// （不参与布局 → 显隐不再推挤列表，也就不必为规避位移而永远占着一行）。
    /// </para>
    /// <para>
    /// 【2026-09-13 改两行 · 为「批量设为表情包」腾空间】单行要放 5 个按钮：面板宽 420，浮层可用宽度约 368px，
    /// 实测 4 个中文按钮 + 状态标签已接近上限，再加一个必然溢出。改为：
    /// ① 状态行 `已选 N 项` … ✕（清空选择）；② 操作行 4 个主操作。
    /// 「清空选择」本是**退出多选**而非"对选中项的操作"，提到状态行右侧作图标按钮，信息层级反而更正确。
    /// </para>
    /// </summary>
    private Border BuildMultiSelectBar()
    {
        var bar = new Border
        {
            Margin = new Thickness(EntryTheme.Scale.GutterX, 0, EntryTheme.Scale.GutterX, EntryTheme.Scale.SpaceS),
            Padding = new Thickness(EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusL),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "PopupBackground");
        bar.SetResourceReference(Border.BorderBrushProperty, "BorderStrokeAccent");
        PanelUi.Name(bar, "多选操作条");

        _multiSelectCount = new TextBlock
        {
            FontSize = EntryTheme.Scale.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _multiSelectCount.SetResourceReference(TextElement.ForegroundProperty, "ThemeForeground");

        // ── 状态行：已选 N 项 … ✕ 清空选择 ──
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_multiSelectCount);
        var clear = PanelUi.CreateIconButton("\uE894", "清空选择", (_, _) => ClearSelection());
        clear.Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0);
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);

        // ── 操作行：4 个主操作（文字保持完整语义，不缩写） ──
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, EntryTheme.Scale.SpaceS, 0, 0),
        };
        actions.Children.Add(PanelUi.CreateActionButton("按序粘贴", (_, _) => StartSequentialPaste()));
        actions.Children.Add(PanelUi.CreateActionButton("合并粘贴", (_, _) => MergePasteSelected()));
        actions.Children.Add(PanelUi.CreateActionButton("删除所选", (_, _) => DeleteSelected()));
        _stickerBatchBtn = PanelUi.CreateActionButton("设为表情包", (_, _) => MarkSelectedAsSticker());
        actions.Children.Add(_stickerBatchBtn);

        var host = new StackPanel();
        host.Children.Add(header);
        host.Children.Add(actions);
        bar.Child = host;
        return bar;
    }

    /// <summary>
    /// 操作栏动作统一入口：无目标不执行（按钮已禁用，双保险）。
    /// </summary>
    private void ActionOnTarget(Action<ClipboardEntry> action)
    {
        if (_actionTarget is { } t)
        {
            action(t);
        }
    }

    /// <summary>刷新页脚统一操作区：目标（ListBox 选中行）、摘要、按钮可用性与状态图标。</summary>
    private void RefreshActionBar()
    {
        _actionTarget = (_entryList?.SelectedItem as RecentStrip)?.Entry;
        var has = _actionTarget is not null;
        var multi = _selected.Count > 0;

        if (multi)
        {
            // 多选激活：页脚操作区让位（批量操作走多选浮层条），按钮禁用、摘要显示勾选数。
            _actionSummary.Text = $"已选 {_selected.Count} 项（批量操作见底部浮层）";
        }
        else if (has)
        {
            _actionSummary.Text = $"{_actionTarget!.CategoryLabel} · {BuildActionPreview(_actionTarget)}";
        }
        else
        {
            _actionSummary.Text = "点击条目以操作（也可右键条目）";
        }
        // 摘要是解释性文字（辅助信息），统一弱化色——不随选中切亮白，避免与功能按钮抢层级。

        _actionCopyBtn.IsEnabled = has && !multi;
        _actionPinBtn.IsEnabled = has && !multi;
        _actionStickerBtn.IsEnabled = has && !multi;
        _actionDeleteBtn.IsEnabled = has && !multi;

        // 表格条目才显示「按格粘」；其余收起，不给用不上的入口。
        var tabular = has && !multi && ClipboardTableCells.IsTabular(_actionTarget!);
        _actionCellPasteBtn.IsEnabled = tabular;
        _actionCellPasteBtn.Visibility = tabular ? Visibility.Visible : Visibility.Collapsed;

        if (has)
        {
            _actionPinBtn.Content = _actionTarget!.IsPinned ? "\uE77A" : "\uE718";
            _actionStickerBtn.Content = "\uE90E";
            _actionStickerBtn.ToolTip = _actionTarget!.IsSticker ? "取消表情包标记" : "标记为表情包";
        }

        // 选中项变了 → 让"当前操作目标"在**行**上看得见（2026-09-15）。
        // 此前目标只体现在这一行摘要文字里，列表本身毫无提示：用户点完一条复制，
        // 不知道底部那五个按钮现在作用在谁身上。
        RefreshRealizedRowChrome();
    }

    /// <summary>
    /// 重算**已实现的行**外观（当前操作目标 / 悬停 / 焦点）。
    /// <para>
    /// 【为什么沿可视树收集，而不是遍历 Items】列表是虚拟化的：Items 可达上万条，
    /// 但只有视口内的十几行真正存在容器树。逐项 <c>ItemContainerFromIndex</c> 在大数据量下是白跑全量；
    /// 沿可视树遍历只访问真实存在的节点（≈ 视口行数）。
    /// </para>
    /// </summary>
    private void RefreshRealizedRowChrome()
    {
        if (_entryList is null)
        {
            return;
        }

        var strips = new List<RecentStrip>();
        CollectStrips(_entryList, strips);
        foreach (var strip in strips)
        {
            strip.RefreshChrome();
        }
    }

    private static void CollectStrips(DependencyObject node, List<RecentStrip> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is RecentStrip strip)
            {
                into.Add(strip);
            }
            else
            {
                CollectStrips(child, into);
            }
        }
    }

    /// <summary>操作栏目标摘要：预览首行拍平为单行（去换行、截断由 TextTrimming 处理）。</summary>
    private static string BuildActionPreview(ClipboardEntry e)
    {
        var raw = string.IsNullOrWhiteSpace(e.Preview) ? e.Content : e.Preview;
        var flat = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 0 ? flat : e.CategoryLabel;
    }

    /// <summary>复制为纯文本（右键菜单「复制为纯文本」· 2026-09-15；与 Ctrl+单击等价）。</summary>
    private void OnPlainCopy(ClipboardEntry entry)
    {
        try
        {
            _client.CopyEntryAsPlainText(entry);
            ShowToast("已复制为纯文本");
        }
        catch (Exception e)
        {
            PanelLog.Trace($"复制纯文本失败: {e.Message}");
            ShowToast($"复制失败：{e.Message}", isError: true);
        }
    }

    /// <summary>
    /// 批量设置/取消「表情包」标记（2026-09-13 用户要求）。
    /// <para>
    /// 方向由选中项状态**自动判定**：只要还有未标记的 → 统一标记；若全部已标记 → 统一取消。
    /// 按钮文案与之同步（见 <see cref="RefreshSelectionUi"/>），所以用户看到什么就会发生什么。
    /// </para>
    /// 逐条调用而非批量 IPC：单条失败不应影响其余条目（失败计数回报，明细进面板日志）。
    /// </summary>
    private void MarkSelectedAsSticker()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var marking = _selected.Any(e => !e.IsSticker);
        var targets = _selected.ToList();
        var ok = 0;
        var failed = 0;

        foreach (var entry in targets)
        {
            try
            {
                _client.SetSticker(entry, marking);
                entry.IsSticker = marking;
                ok++;
            }
            catch (Exception e)
            {
                failed++;
                PanelLog.Trace($"批量表情包标记失败 id={entry.Id}: {e.Message}");
            }
        }

        ShowToast(
            failed == 0
                ? marking ? $"已把 {ok} 项设为表情包" : $"已取消 {ok} 项的表情包标记"
                : $"{ok} 项成功、{failed} 项失败（明细见面板日志）",
            isError: failed > 0);

        if (ok == 0)
        {
            return;
        }

        // 取消标记且当前正看「表情包」结果集 → 这些条目已不属于该结果集：清空选择并整页重载
        if (_currentFilter == "sticker" && !marking)
        {
            ClearSelection();
            Reload();
            return;
        }

        // 否则原地刷新受影响的行（不整页重建 → 不丢滚动位置），与 OnRowPin/OnRowSticker 一致
        foreach (var entry in targets.Where(e => e.IsSticker == marking))
        {
            RefreshRow(entry);
        }
        RefreshSelectionUi();
    }

    /// <summary>
    /// 切换某条勾选。勾上 = 追加到队尾（FIFO）；取消 = 移除，**其后各条的序号自动前移**
    ///（因为序号是在 <see cref="ReindexSelection"/> 里按数组下标统一重算的）。
    /// </summary>
    private void ToggleSelection(ClipboardEntry entry)
    {
        if (_selectedOrder.TryGetValue(entry.Id, out var order))
        {
            _selected.RemoveAt(order - 1); // 取消勾选
        }
        else
        {
            _selected.Add(entry); // 入队（勾选顺序 = 粘贴顺序）
            _multiSelectMode = true;
        }

        if (_selected.Count == 0)
        {
            _multiSelectMode = false; // 全部取消 → 退出多选，勾选圈收起
        }

        ReindexSelection();
        RefreshSelectionUi();
    }

    private void ClearSelection()
    {
        _selected.Clear();
        _multiSelectMode = false;
        ReindexSelection();
        RefreshSelectionUi();
    }

    /// <summary>
    /// 重建 `id → 序号` 索引（1 起）。**只在选中集合变化时调用一次** ——
    /// 序号必须按数组下标重算，这样取消中间某条后，其余条目的序号会自动前移（不留空洞）。
    /// </summary>
    private void ReindexSelection()
    {
        _selectedOrder.Clear();
        for (var i = 0; i < _selected.Count; i++)
        {
            _selectedOrder[_selected[i].Id] = i + 1;
        }
    }

    /// <summary>把选中状态推给已实现的行（含序号）并刷新两条操作条。</summary>
    private void RefreshSelectionUi()
    {
        if (_entryList is not null)
        {
            foreach (var item in _entryList.Items)
            {
                if (item is not RecentStrip strip)
                {
                    continue;
                }
                // O(1) 反查（此前是 FindIndex 线性扫描，且对每一行都扫一次）
                var order = _selectedOrder.TryGetValue(strip.Entry.Id, out var o) ? o : 0;
                strip.IsMultiSelectMode = _multiSelectMode;
                strip.IsSelected = order > 0;
                strip.SelectionOrder = order;
                strip.RefreshSelectionVisual();
            }
        }

        if (_multiSelectBar is not null)
        {
            // 【2026-09-12 UI 收敛】不再"常驻占位"：多选条已改为**列表底部浮层**（不参与布局），
            // 于是"显示它会推挤列表、勾第 2 条点到移动后的行"这个错位问题从根上消失；
            // 同时也不必为占位而在列表为空时还教用户"点左侧圆圈勾选"（那时根本没有圆圈可点）。
            _multiSelectBar.Visibility = _selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _multiSelectCount.Text = _selected.Count > 0
                ? $"已选 {_selected.Count} 项"
                : "多选：点条目左侧圆圈勾选（按勾选先后顺序粘贴）";

            // 批量标记的**方向提示**（2026-09-13）：只要还有未标记的 → "设为表情包"；
            // 全部已标记 → "取消表情包"。让用户看到什么就会发生什么。
            if (_stickerBatchBtn is not null && _selected.Count > 0)
            {
                var allMarked = _selected.All(e => e.IsSticker);
                _stickerBatchBtn.Content = allMarked ? "取消表情包" : "设为表情包";
                PanelUi.Name(
                    _stickerBatchBtn,
                    allMarked ? $"取消这 {_selected.Count} 项的表情包标记" : $"把选中的 {_selected.Count} 项设为表情包");
            }
        }

        // 【2026-09-15 统一操作区】页脚常驻；多选时按钮禁用、摘要显示勾选数（见 RefreshActionBar）。
        RefreshActionBar();

        UpdateSequentialBar();
    }

    /// <summary>
    /// 开始按序粘贴（**分步**）：**抓内容快照入队** → 收起面板 → 之后用户在目标窗口每按一次 Ctrl+V 粘下一条。
    /// 收起面板是必须的：SendPaste 把 Ctrl+V 发给当前前台窗口，而面板自己就是前台（同双击粘贴的教训）。
    /// 队列自包含内容，条目后来被删/被清理也不影响已排好的序（见 ClipboardIpcClient.SequentialItem 注释）。
    /// </summary>
    private void StartSequentialPaste()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var entries = _selected.ToList();
        _sequentialNotice = null; // 新一轮会话：清掉上一轮的失效/降级提示
        try
        {
            _client.BeginSequentialPaste(entries);
        }
        catch (Exception e)
        {
            PanelLog.Trace($"按序粘贴启动失败: {e.Message}");
            _sequentialNotice = $"按序粘贴启动失败：{e.Message}";
            UpdateSequentialBar();
            return;
        }

        _sequentialTotal = entries.Count;
        _sequentialIsCell = false;
        _sequentialPreviews = entries.ConvertAll(e => e.Content ?? string.Empty);
        PanelLog.Trace($"按序粘贴开始：{entries.Count} 条（勾选顺序）");
        ClearSelection();
        HidePopup();
        // 【不再自动粘贴 · 2026-09-12 用户口径定稿】模式启动后**只收起面板、等用户按 Ctrl+V**。
        // 曾试过"自动粘第一条"（时序不可靠 → 偶发丢第一条）与"哨兵"（粘零宽占位 → 零宽字符会入库成空条目）。
        // 两者都在替用户做他不需要的事：用户要的就是"按一次 Ctrl+V 粘一条"，把首发的时机交还给他，
        // 风险源头即消失 —— 无需哨兵、无需自注入、无需任何时间窗口猜测。
        UpdateSequentialBar();
        PanelLog.Trace($"按序粘贴就绪：{entries.Count} 条，等待用户在目标窗口按 Ctrl+V 粘第 1 条");
    }

    /// <summary>
    /// 粘贴下一条（由 Ctrl+Shift+V 经单实例信号触发，或状态条手动触发）。
    /// 未激活/已完成时引擎客户端内部为空操作。
    /// </summary>
    public void PasteNextSequential()
    {
        // 注入期间放行钩子（见 _hookPaused 注释）：我们随后注入的 Ctrl+V 会回到同一个钩子，
        // 若不暂停就会被当成用户按键**再消费一条** → 自激（真机实测：一次按键粘出数条）。
        _hookPaused = true;
        try
        {
            _client.PasteNextSequential();
        }
        catch (Exception e)
        {
            // 引擎与快照两条路都不通才会到这里（客户端已把详情经 SequentialIssue 推到状态条）
            PanelLog.Trace($"按序粘贴失败: {e.Message}");
        }
        finally
        {
            // 500ms 覆盖"注入 + 分发 + 回钩"全程；窗口内只放行不消费，最坏是漏过一次按键。
            var resume = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            resume.Tick += (_, _) =>
            {
                resume.Stop();
                _hookPaused = false;
            };
            resume.Start();
        }

        PanelLog.Trace($"按序粘贴：已粘一条，剩余 {_client.SequentialRemaining}");
        UpdateSequentialBar();
    }

    private void CancelSequentialPaste()
    {
        try
        {
            _client.CancelSequentialPaste();
        }
        catch (Exception e)
        {
            PanelLog.Trace($"取消按序粘贴失败: {e.Message}");
        }

        _sequentialNotice = null; // 用户主动结束会话 → 一并清掉过程提示
        UpdateSequentialBar();
    }

    /// <summary>按序会话是否进行中（单实例信号据此决定"弹面板"还是"粘下一条"）。</summary>
    public bool IsSequentialPasteActive => _client.IsSequentialPasteActive;

    private void UpdateSequentialBar()
    {
        if (_sequentialBar is null)
        {
            return;
        }

        var active = _client.IsSequentialPasteActive;
        // 会话结束后提示仍保留（用户重开面板能看到"哪几条失效/降级"），点「取消」才清掉。
        SetStatusLane(StatusLane.Sequential, active || _sequentialNotice is not null);

        if (active)
        {
            var hint = $"按序粘贴中：剩余 {_client.SequentialRemaining} 条 · 在目标窗口按 Ctrl+V 粘下一条";
            _sequentialText.Text = _sequentialNotice is null ? hint : $"{hint} · {_sequentialNotice}";
        }
        else if (_sequentialNotice is not null)
        {
            _sequentialText.Text = _sequentialNotice;
        }

        // 会话进度上报给壳（灵动岛上屏）：**只发进度，不发内容**（隐私红线，见 PasteSessionReporter）。
        // 这里同时也是"会话结束/取消"的唯一上报点——Inactive 由 index<=0 表达。
        var remainingCount = active ? _client.SequentialRemaining : 0;
        var index = active && _sequentialTotal > 0 ? Math.Max(1, _sequentialTotal - remainingCount + 1) : 0;

        // 索引是"下一条要粘的第几项"（1 起）→ 预览取队列表的同位元素
        var preview = index > 0 && index <= _sequentialPreviews.Count ? _sequentialPreviews[index - 1] : null;
        PasteSessionReporter.Publish(index, _sequentialTotal, _sequentialIsCell, preview);
    }

    /// <summary>一次性把所选合并成一段文本贴出（默认以空行分隔）。</summary>
    private void MergePasteSelected()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var entries = _selected.ToList();
        PanelLog.Trace($"合并粘贴：{entries.Count} 条");
        ClearSelection();
        HidePopup(); // 先让出焦点（同双击粘贴）

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                _client.MergePasteToActiveWindow(entries);
            }
            catch (Exception e)
            {
                PanelLog.Trace($"合并粘贴失败: {e.Message}");
                // 面板此刻已收起，反馈条多数情况下看不到；但下次打开面板若仍在 3 秒窗口内即可见，
                // 且日志一定有痕迹 —— 绝不再静默。
                ShowToast($"合并粘贴失败：{e.Message}", isError: true);
            }
        };
        timer.Start();
    }

    /// <summary>批量删除所选（收藏条目同样可删 —— 用户显式勾选即意图明确）。</summary>
    private void DeleteSelected()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var entries = _selected.ToList();
        try
        {
            _client.DeleteEntries(entries);
            PanelLog.Trace($"批量删除 {entries.Count} 条");
            ShowToast($"已删除 {entries.Count} 条");
        }
        catch (Exception e)
        {
            // 失败不得继续刷新（否则界面呈现"删成功"的假象）
            PanelLog.Trace($"批量删除失败: {e.Message}");
            ShowToast($"批量删除失败：{e.Message}", isError: true);
            return;
        }

        ClearSelection();
        Reload();
    }

    /// <summary>
    /// 剪贴板面板是**常驻服务**（L2 工具常驻，不随主程序关闭）——其主窗口在进程存活期间
    /// **不允许被外部销毁**（宿主退出 / WM_CLOSE / 未知关闭路径都会让后续唤起
    /// "关闭窗口后无法 Show"而永远打不开，2026-09-16 事故根因）。
    /// 拦截规则：
    /// ① 非进程退出（<see cref="App.IsExiting"/> 未置位）→ 取消关闭 + 转隐藏，窗口对象保持可用；
    /// ② 进程真正退出（托盘停止常驻服务 / 系统注销）→ 放行。
    /// 来源堆栈落盘（popup-trace 同文件），供真机复现时定位关闭方。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            PanelLog.Trace("面板主窗口收到关闭请求（非进程退出 → 转隐藏，窗口保留）:\n" + Environment.StackTrace);
            e.Cancel = true;
            HidePopup();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// 窗口销毁时收干净非托管/长生命周期资源（2026-09-12 审计）：
    /// ① **低级键盘钩子必须卸载** —— 否则系统会继续回调一个已销毁的面板实例（也违反 603/7438 配对释放纪律）；
    /// ② 三个 `DispatcherTimer` 停止；
    /// ③ `CompositionTarget.Rendering` 是**静态事件**，动画中途关窗不退订会强引用本窗口。
    /// 事件订阅（client 的 HistoryChanged 等）刻意不退订：面板单例与 client 同进程同生共死，
    /// 退订需把 lambda 提升为字段，收益不抵改动风险。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _pasteHook?.Dispose();
        }
        catch (Exception ex)
        {
            PanelLog.Trace($"卸载键盘钩子失败（不影响退出）: {ex.Message}");
        }

        EndSmoothScroll();
        _searchDebounce.Stop();
        _pauseTick.Stop();
        _toastTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 【P2-4】Esc 先关标签编辑浮层（若开着），再收起面板 —— 否则用户想取消编辑却把面板关了。
            if (_tagEditorHost?.Visibility == Visibility.Visible)
            {
                HideTagEditor();
                e.Handled = true;
                return;
            }

            // 【按格粘】同规：先关选项浮层，再收起面板。
            if (_cellPasteHost?.Visibility == Visibility.Visible)
            {
                HideCellPasteEditor();
                e.Handled = true;
                return;
            }

            HidePopup();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
