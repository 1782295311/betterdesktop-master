using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Taskbar.Contracts;
using BetterDesktop.Shell.Taskbar.Native;

namespace BetterDesktop.Shell.Taskbar.Services;

/// <summary>
/// 任务栏外观引擎：场景化状态机 + 套用 ACCENT 策略。
/// 原样对齐 TranslucentTB 的 GetConfig 优先级：
/// BatterySaver &gt; TaskView &gt; (StartOpened / SearchOpened) &gt; MaximizedWindow &gt; VisibleWindow &gt; Desktop。
/// Win10 直接 SetWindowCompositionAttribute；Win11 走 ExplorerTapBridge 注入的 ITaskbarAppearanceService。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskbarAppearanceEngine : ITaskbarAppearanceService, IDisposable
{
    private readonly object _lock = new();
    private readonly IKernelLogger? _logger;
    private TaskbarAppearanceConfig _config;
    private readonly bool _isWindows11;
    private readonly ExplorerTapBridge? _bridge;
    private readonly AppVisibilityWatcher _appVisibility;
    private readonly WinEventPump _pump = new();
    private readonly Dictionary<IntPtr, bool> _taskbars = new();
    private bool _processDiesRegistered;
    private bool _bridgeUnavailableWarned;
    private bool _disposed;

    // EnumWindows 回调必须以字段强引用持有，否则原生枚举期间委托被 GC 回收会触发 AccessViolation。
    // 不能用内联 lambda（闭包无根）。
    private readonly NativeMethods.EnumWindowsProc _enumSearchProc;
    private readonly NativeMethods.EnumWindowsProc _enumMaxProc;
    private bool _enumFound; // 枚举复用状态，避免闭包捕获

    // 【2026-09-11 崩溃根治 · 幂等去重 + 事件防抖】
    // 病灶：WinEventPump 窗口事件风暴（创建/销毁/前台，用户操作时每秒数十个）→ 回调在泵线程直接
    //   RefreshAll → 对每个任务栏无条件跨进程 COM Apply。实测 scene/accent/color 完全不变仍每秒
    //   20+ 次 SetTaskbarAppearance 轰炸 explorer → explorer 内 comctl32.dll 0xc0000005 崩溃
    //   （事件日志 Explorer.EXE，当日 9 次，宿主无图标操作时段同样触发）+ explorer 侧繁忙导致
    //   桌面图标显隐视觉反馈延迟。
    // 修复：
    //   1) Apply 幂等去重：每 hwnd 记录上次成功套用的 (accent,color,blurRadius)，值未变 → 零跨进程调用；
    //   2) 窗口事件防抖：回调只标记脏，200ms 合并后单次刷新（低频；同时把跨进程 COM 移出泵线程，
    //      遵守 WinEventPump「回调不耗时」纪律）；
    //   3) 失败不记基线：下次值仍变可重试，但值不变不重试（消灭失败重试风暴）。
    private const int RefreshDebounceMs = 200;
    private readonly Dictionary<IntPtr, (TaskbarAccent Accent, uint Color, float BlurRadius)> _lastApplied = new();
    private CancellationTokenSource? _refreshDebounceCts;
    private bool _refreshPending;

    // 场景状态（显式初始化，避免 CS0649 在 -warnaserror 下报错）
    private bool _startOpened = false;
    private bool _searchOpened = false;
    private bool _taskViewOpened = false;
    private bool _batterySaver = false;
    private bool _hasMaximized = false;

    // 电池省电状态：通过 GetSystemPowerStatus 在状态重算时同步探测（无需消息窗口）。

    public TaskbarAppearanceEngine(IKernelLogger? logger = null, string? bridgeDllDir = null)
    {
        _logger = logger;
        _config = new TaskbarAppearanceConfig();
        _isWindows11 = IsWindows11OrGreater();
        _appVisibility = new AppVisibilityWatcher();
        _enumSearchProc = EnumSearchCallback;
        _enumMaxProc = EnumMaxCallback;
        _logger?.Info($"[TaskbarAccent] OS 版本探测：{Environment.OSVersion.Version}（Build≥22000 判为 Win11）。");
        if (_isWindows11)
        {
            _bridge = new ExplorerTapBridge(bridgeDllDir);
            _logger?.Info(_bridge.Available
                ? $"[TaskbarAccent] Win11 桥已就绪：ExplorerTAP.dll 加载成功，可注入 explorer。"
                : $"[TaskbarAccent] Win11 桥不可用：ExplorerTAP.dll 未找到或导出解析失败（仅桌面场景降级）。");
        }
        else
        {
            _logger?.Info($"[TaskbarAccent] 当前为 Win10/旧版，走 SetWindowCompositionAttribute 路径。");
        }
    }

    public TaskbarAppearanceConfig Current => _config.Clone();

    public bool Win11BridgeAvailable => _isWindows11 && _bridge is { Available: true };

    public bool IsWindows11 => _isWindows11;

    /// <summary>启动引擎：枚举任务栏、连接 Win11 桥、订阅事件。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) return;

            var taskbars = TaskbarWindowFinder.FindAll();
            foreach (var h in taskbars)
            {
                _taskbars[h] = true;
                if (_isWindows11 && Win11BridgeAvailable)
                {
                    bool ok = _bridge!.Connect(h);
                    _logger?.Info(ok
                        ? $"[TaskbarAccent] Win11 注入成功：任务栏 hwnd={h:X} 已连接 ITaskbarAppearanceService。"
                        : $"[TaskbarAccent] Win11 注入失败：hwnd={h:X} 的 Connect 返回 false（可能被杀软拦截）。");
                    if (ok && !_processDiesRegistered)
                    {
                        // F1（7436）：进程死亡还原契约，启动必调一次——否则进程崩溃/强杀后外观永久残留。
                        _processDiesRegistered = _bridge.RestoreAllWhenProcessDies();
                        DiagnosticLog.Trace("TaskbarAccent",
                            $"RestoreAllTaskbarsToDefaultWhenProcessDies(pid={Environment.ProcessId}) => {_processDiesRegistered}");
                    }
                }
            }
            if (_taskbars.Count == 0)
            {
                _logger?.Warn($"[TaskbarAccent] 未枚举到任何任务栏窗口（Shell_TrayWnd 未就绪？）。");
            }

            _appVisibility.LauncherVisibilityChanged += OnLauncherVisibility;
            _appVisibility.Start();

            // 监听窗口创建/销毁/前台/重排：触发重新评估外观（7435 收口：统一 WinEventPump 单泵多订阅）。
            // 【2026-09-11】事件回调只标记脏 → ScheduleRefresh 防抖合并（泵线程零耗时，COM 移出泵线程）。
            _pump.Subscribe(0x8000, 0x8001, (_, _) => ScheduleRefresh());
            _pump.Subscribe(0x0003, 0x0003, (_, _) => ScheduleRefresh());
            _pump.Subscribe(0x8008, 0x8008, (_, _) => ScheduleRefresh());

            UpdateSceneState();
            RefreshAll();
        }
    }

    /// <summary>窗口事件防抖刷新：200ms 内多事件合并为一次（泵线程只做标记，跨进程 COM 在工作线程执行）。</summary>
    private void ScheduleRefresh()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _refreshPending = true;
            _refreshDebounceCts?.Cancel();
            _refreshDebounceCts = new CancellationTokenSource();
            var token = _refreshDebounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RefreshDebounceMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // 事件继续到来，由最新一次刷新承接
                }
                lock (_lock)
                {
                    if (_disposed || !_refreshPending) return;
                    _refreshPending = false;
                    try
                    {
                        UpdateSceneState();
                        RefreshAll();
                    }
                    catch
                    {
                        // 刷新失败不阻断（M10）
                    }
                }
            }, CancellationToken.None);
        }
    }

    public void SetConfig(TaskbarAppearanceConfig config)
    {
        lock (_lock)
        {
            _config = config.Clone();
            DiagnosticLog.Trace("TaskbarAccent",
                $"SetConfig: desktop={_config.Desktop.Accent}/{_config.Desktop.Color:X8} " +
                $"max={_config.MaximizedWindow.Accent} start(enabled={_config.StartOpenedEnabled})={_config.StartOpened.Accent} " +
                $"search(enabled={_config.SearchOpenedEnabled})={_config.SearchOpened.Accent} " +
                $"taskview(enabled={_config.TaskViewOpenedEnabled}) battery(enabled={_config.BatterySaverEnabled})");
            RefreshAll();
        }
    }

    public void ReturnToStock()
    {
        lock (_lock)
        {
            foreach (var h in _taskbars.Keys)
            {
                if (_isWindows11 && Win11BridgeAvailable)
                {
                    _bridge!.ReturnToDefault(h);
                }
                else
                {
                    DwmapiHelper.ClearAccent(h);
                }
            }
            // 状态已还原为系统默认，去重基线失效：清空让下次 RefreshAll 重新 Apply。
            _lastApplied.Clear();
            _logger?.Info($"[TaskbarAccent] 已还原任务栏到系统默认外观（{_taskbars.Count} 个窗口）。");
        }
    }

    private void OnLauncherVisibility(object? sender, bool visible)
    {
        lock (_lock)
        {
            _startOpened = visible;
            RefreshAll();
        }
    }

    /// <summary>
    /// 评估场景状态（对齐 TTB GetConfig 的实时探测）：
    /// 最大化窗口 / 搜索浮层 / 任务视图 / 省电，均在状态重算时同步探测。
    /// 开始菜单由 AppVisibility 事件驱动（见 OnLauncherVisibility）。
    /// </summary>
    private void UpdateSceneState()
    {
        _hasMaximized = HasMaximizedWindowOnAnyMonitor();
        _searchOpened = IsSearchOpen();
        _taskViewOpened = IsTaskViewOpen();
        _batterySaver = IsBatterySaver();
    }

    /// <summary>搜索浮层是否打开（Win10: Windows.Shell.Search；Win11: SearchHost 的 CoreWindow）。</summary>
    private bool IsSearchOpen()
    {
        // Win10 搜索是独立的 immersive 窗口类；Win11 搜索宿主为 SearchHost.exe（类名 Windows.UI.Core.CoreWindow）。
        // 用 FindWindow 探测已知类，避免依赖进程名（更稳）。
        if (NativeMethods.FindWindow("Windows.Shell.Search", null) != IntPtr.Zero) return true;
        if (NativeMethods.FindWindow("SearchPane", null) != IntPtr.Zero) return true;
        // Win11：CoreWindow 通用类，需确认是搜索宿主且可见。
        if (SearchHostVisible()) return true;
        return false;
    }

    /// <summary>任务视图是否打开（Win10/Win11 任务切换器类名）。</summary>
    private bool IsTaskViewOpen()
    {
        return NativeMethods.FindWindow("Windows.TaskSwitcher", null) != IntPtr.Zero
            || NativeMethods.FindWindow("TaskViewFrame", null) != IntPtr.Zero;
    }

    /// <summary>省电模式是否开启（Windows 10+ SYSTEM_POWER_STATUS.SystemStatusFlag 第 0 位）。</summary>
    private bool IsBatterySaver()
    {
        var status = new NativeMethods.SYSTEM_POWER_STATUS();
        if (!NativeMethods.GetSystemPowerStatus(ref status)) return false;
        return (status.SystemStatusFlag & 0x1) != 0;
    }

    /// <summary>搜索宿主窗口是否可见（辅助 IsSearchOpen，仅 Win11 CoreWindow 分支用）。</summary>
    private bool SearchHostVisible()
    {
        _enumFound = false;
        NativeMethods.EnumWindows(_enumSearchProc, IntPtr.Zero);
        return _enumFound;
    }

    private bool EnumSearchCallback(IntPtr hwnd, IntPtr _)
    {
        if (_enumFound) return true;
        var sb = new System.Text.StringBuilder(256);
        if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == "Windows.UI.Core.CoreWindow"
            && NativeMethods.IsWindowVisible(hwnd))
        {
            _enumFound = true;
            return false;
        }
        return true;
    }

    private bool HasMaximizedWindowOnAnyMonitor()
    {
        _enumFound = false;
        NativeMethods.EnumWindows(_enumMaxProc, IntPtr.Zero);
        return _enumFound;
    }

    private bool EnumMaxCallback(IntPtr hwnd, IntPtr _)
    {
        if (_enumFound) return true;
        // 仅考虑可见、非最小化的顶层窗口；跳过任务栏/工具条自身。
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true; // 子窗口/owned
        if (NativeMethods.IsZoomed(hwnd)) { _enumFound = true; return false; }
        return true;
    }

    /// <summary>依据当前场景状态选出应套用的外观配置（对齐 TTB 优先级）。</summary>
    private TaskbarAppearance ResolveAppearance()
    {
        if (_config.BatterySaverEnabled && _batterySaver)
            return _config.BatterySaver;
        if (_config.TaskViewOpenedEnabled && _taskViewOpened)
            return _config.TaskViewOpened;
        if (_config.StartOpenedEnabled && _startOpened)
            return _config.StartOpened;
        if (_config.SearchOpenedEnabled && _searchOpened)
            return _config.SearchOpened;
        if (_hasMaximized)
            return _config.MaximizedWindow;
        // 简化：此处不区分 VisibleWindow / Desktop（最大化之外统一用 Desktop）。
        return _config.Desktop;
    }

    private void RefreshAll()
    {
        if (_disposed) return;
        var appearance = ResolveAppearance();
        DiagnosticLog.Trace("TaskbarAccent",
            $"RefreshAll: scene={SceneName()} accent={appearance.Accent} color={appearance.Color:X8} " +
            $"taskbars={_taskbars.Count} bridgeAvail={_isWindows11 && Win11BridgeAvailable}");
        foreach (var h in _taskbars.Keys)
        {
            Apply(h, appearance);
        }
    }

    /// <summary>当前生效场景名（诊断用）。</summary>
    private string SceneName()
    {
        if (_config.BatterySaverEnabled && _batterySaver) return "BatterySaver";
        if (_config.TaskViewOpenedEnabled && _taskViewOpened) return "TaskView";
        if (_config.StartOpenedEnabled && _startOpened) return "Start";
        if (_config.SearchOpenedEnabled && _searchOpened) return "Search";
        if (_hasMaximized) return "Maximized";
        return "Desktop";
    }

    private void Apply(IntPtr hWnd, TaskbarAppearance appearance)
    {
        try
        {
            // 【2026-09-11 崩溃根治核心】幂等去重：值与上次成功套用一致 → 跳过跨进程 COM。
            // 窗口事件风暴时 scene/accent/color 通常未变，此检查消灭 99% 无效轰炸。
            var key = (appearance.Accent, appearance.Color, appearance.BlurRadius);
            if (_lastApplied.TryGetValue(hWnd, out var last) && last == key)
            {
                return;
            }

            // 原生 ITaskbarAppearanceService 的 color 参数为 ABGR（对照 TTB 原版 color.ToABGR()）
            var abgr = DwmapiHelper.ToAbgr(appearance.Color);
            // Blur 模式下若用户没设过 radius，0 在 26200 上视觉上看不出模糊；给个合理默认值
            // （仅当次 Apply 生效，不持久化到 config，避免误改用户数据）
            var blurRadius = appearance.BlurRadius > 0f ? appearance.BlurRadius : 24f;
            if (_isWindows11 && Win11BridgeAvailable)
            {
                // Win11：ITaskbarAppearanceService。Acrylic→brush=0；其余 SolidColor brush=1。
                if (appearance.Accent == TaskbarAccent.Acrylic)
                {
                    bool ok = _bridge!.SetAppearance(hWnd, 0, abgr);
                    DiagnosticLog.Trace("TaskbarAccent", $"Apply Acrylic hwnd={hWnd:X} => {ok}");
                    if (!ok) _logger?.Warn($"[TaskbarAccent] SetTaskbarAppearance(Acrylic) 失败 hwnd={hWnd:X}。");
                    if (ok) _lastApplied[hWnd] = key;
                }
                else if (appearance.Accent == TaskbarAccent.Blur)
                {
                    bool ok = _bridge!.SetBlur(hWnd, abgr, blurRadius / 3f);
                    DiagnosticLog.Trace("TaskbarAccent", $"Apply Blur hwnd={hWnd:X} radius={blurRadius} => {ok}");
                    if (!ok) _logger?.Warn($"[TaskbarAccent] SetTaskbarBlur 失败 hwnd={hWnd:X}。");
                    if (ok) _lastApplied[hWnd] = key;
                }
                else
                {
                    bool ok = _bridge!.SetAppearance(hWnd, 1, abgr);
                    DiagnosticLog.Trace("TaskbarAccent", $"Apply Solid hwnd={hWnd:X} color={abgr:X8} => {ok}");
                    if (!ok) _logger?.Warn($"[TaskbarAccent] SetTaskbarAppearance(SolidColor) 失败 hwnd={hWnd:X}。");
                    if (ok) _lastApplied[hWnd] = key;
                }
            }
            else if (!_isWindows11)
            {
                // Win10：直接 SetWindowCompositionAttribute（合法降级路径）。
                bool ok = DwmapiHelper.SetAccent(hWnd, appearance);
                if (!ok) _logger?.Warn($"[TaskbarAccent] SetWindowCompositionAttribute 失败 hwnd={hWnd:X}。");
                if (ok) _lastApplied[hWnd] = key;
            }
            else
            {
                // F2（7404）：Win11 任务栏是 XAML，WCA 对其无效——桥不可用时静默走 WCA
                // 等于"用户调外观没反应"。改为一次性 Warn + 跳过无效降级；不可用状态
                // 已通过 Win11BridgeAvailable && IsWindows11 对设置页可读。
                if (!_bridgeUnavailableWarned)
                {
                    _bridgeUnavailableWarned = true;
                    _logger?.Warn("[TaskbarAccent] Win11 桥不可用（ExplorerTAP.dll 缺失/注入失败），任务栏外观功能不可用；已跳过对 XAML 任务栏无效的 WCA 降级。");
                    DiagnosticLog.Trace("TaskbarAccent", "Win11BridgeUnavailable: 外观功能不可用（不走无效的 WCA 降级，请补齐 ExplorerTAP.dll）");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"[TaskbarAccent] 套用外观异常 hwnd={hWnd:X}：{ex.Message}");
        }
    }

    private static bool IsWindows11OrGreater()
    {
        try
        {
            // 22000 = Windows 11 RTM
            var v = Environment.OSVersion.Version;
            return v.Major > 10 || (v.Major == 10 && v.Build >= 22000);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshDebounceCts?.Cancel();
            _refreshDebounceCts = null;
            _lastApplied.Clear();
            try { ReturnToStock(); } catch { /* ignore */ }
            _pump.Dispose();
            _appVisibility.Dispose();
            _bridge?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
