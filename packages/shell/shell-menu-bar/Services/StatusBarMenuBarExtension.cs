// BetterDesktop.Shell.MenuBar — 状态条扩展（菜单栏右区主体）
// 角色：把移植自 tools/ShellComponentsPlayground 的紧凑状态条（MenuBarStatusStrip）作为菜单栏右区。
// 插件化约定：数据全部来自 Inject 的 shell.status 监控服务（可能为 null → 状态条内图标降级为占位，不抛）；
//             每个状态图标左键/右键点击 → 打开对应独立面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）。
// 面板均为独立 ShellWindow（MenuBarPopupWindow），复用什么都不做的通用弹窗基类；状态条自身不持有面板引用。

using System;
using System.Windows;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>菜单栏右区状态条扩展：GetVisual 返回状态条；各按钮点击打开对应独立面板。</summary>
internal sealed class StatusBarMenuBarExtension : IMenuBarExtension, IDisposable
{
    public string Id => "status-bar";

    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly INetworkMonitor? _net;
    private readonly IVolumeMonitor? _vol;
    private readonly IMicrophoneMonitor? _mic;
    private readonly IBatteryMonitor? _bat;
    private readonly IImeMonitor? _ime;
    private readonly IBrightnessMonitor? _brightness;
    private readonly IMemoryMonitor? _mem;
    private readonly ICpuMonitor? _cpu;
    private readonly ISettingsService? _settings;
    private readonly IStartMenuSearchService? _search;
    private readonly IAppIconService? _appIcon;
    private readonly BetterDesktop.Shell.Calendar.Contracts.ICalendarService? _calendar;
    private readonly BetterDesktop.Shell.Pinning.Contracts.IPinningService? _pinning;
    private readonly BetterDesktop.Shell.Clipboard.Contracts.IClipboardService? _clipboard;

    private MenuBarStatusStrip? _strip;
    private SearchPopupWindow? _searchPopup;
    private ImePopupWindow? _imePopup;
    private ExtensionsCenterWindow? _extensionsCenterPopup;
    private CalendarPopupWindow? _calendarPopup;
    private ControlCenterWindow? _controlCenterPopup;
    private BluetoothPopupWindow? _bluetoothPopup;
    private WifiPopupWindow? _wifiPopup;
    private ThemePopupWindow? _themePopup;
    private PowerPopupWindow? _powerPopup;
    private NetworkPanelWindow? _networkPopup;
    private MemoryPanelWindow? _memoryPopup;
    private CpuPanelWindow? _cpuPopup;
    private MicrophonePanelWindow? _microphonePopup;
    private SoundPanelWindow? _soundPopup;
    private NotificationCenterWindow? _notificationCenterPopup;

    public StatusBarMenuBarExtension(
        IVolumeMonitor? vol,
        IMicrophoneMonitor? mic,
        IBatteryMonitor? bat,
        IImeMonitor? ime,
        IBrightnessMonitor? brightness,
        INetworkMonitor? net,
        IMemoryMonitor? mem,
        ICpuMonitor? cpu,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        ISettingsService? settings = null,
        IStartMenuSearchService? search = null,
        IAppIconService? appIcon = null,
        BetterDesktop.Shell.Calendar.Contracts.ICalendarService? calendar = null,
        BetterDesktop.Shell.Pinning.Contracts.IPinningService? pinning = null,
        BetterDesktop.Shell.Clipboard.Contracts.IClipboardService? clipboard = null)
    {
        _vol = vol;
        _mic = mic;
        _bat = bat;
        _ime = ime;
        _brightness = brightness;
        _net = net;
        _mem = mem;
        _cpu = cpu;
        _vibrancy = vibrancy;
        _appearance = appearance;
        _settings = settings;
        _search = search;
        _appIcon = appIcon;
        _calendar = calendar;
        _pinning = pinning;
        _clipboard = clipboard;
    }

    public FrameworkElement GetVisual()
    {
        if (_strip is null)
        {
            _strip = new MenuBarStatusStrip(_vol, _mic, _bat, _ime, _brightness, _net, _mem, _cpu, _settings);
            _strip.ButtonClicked += OnButtonClicked;
        }
        return _strip;
    }

    public void OpenPopup(Point anchorScreenTopLeft)
    {
        // 状态条为常驻展示，无单一弹窗入口；具体面板由各按钮点击触发。
    }

    public void ClosePopup()
    {
        _searchPopup?.Hide();
        _imePopup?.Hide();
        _extensionsCenterPopup?.Hide();
        _calendarPopup?.Hide();
        _controlCenterPopup?.Hide();
        _bluetoothPopup?.Hide();
        _wifiPopup?.Hide();
        _themePopup?.Hide();
        _powerPopup?.Hide();
        _networkPopup?.Hide();
        _memoryPopup?.Hide();
        _cpuPopup?.Hide();
        _microphonePopup?.Hide();
        _soundPopup?.Hide();
    }

    private void OnButtonClicked(object? sender, MenuBarStatusButtonClickedEventArgs e)
    {
        try
        {
            // 状态条自身已处理的动作：显示桌面（Desktop，由 strip 内部处理）。
            // 通知图标（Notification）是双用入口，在下方 case 中按左右键分发：
            //   左键 → 控制中心（高频入口）
            //   右键 → 通知中心（低频；当前仅图标开关占位，尚未接系统通知）
            if (e.Button == MenuBarStatusButtonId.Desktop)
            {
                return;
            }

            double buttonWidth = Math.Max(e.Source.ActualWidth, 16);

            switch (e.Button)
            {
                case MenuBarStatusButtonId.Notification:
                    // 通知图标 = 控制中心 + 通知中心双用入口（同一图标空间）：
                    //   左键（高频）= 控制中心（功能目录，Wi-Fi/蓝牙/热点/投影/电源/屏幕/声音等）
                    //   右键（低频）= 通知中心（strip.ToggleNotificationSwitch 只翻图标状态；
                    //                 通知中心尚未接系统通知，见 MenuBarStatusStrip NotificationToggle 注释）
                    if (e.IsRightButton)
                    {
                        // 右键 = 通知中心：图标状态复位（未读标记清掉）+ 弹通知中心面板。
                        // 面板当前为空态占位（未接系统通知源，见 NotificationCenterWindow 头注释）。
                        _strip?.ToggleNotificationSwitch();
                        ShowPopup(ref _notificationCenterPopup,
                            () => new NotificationCenterWindow(_vibrancy, _appearance),
                            e.Source, buttonWidth, new Size(320, 400));
                    }
                    else
                    {
                        ShowPopup(ref _controlCenterPopup,
                            () => new ControlCenterWindow(
                                ControlCenterFeatureCatalog.Build(_vibrancy, _appearance, _net, _vol, _mic, _bat, _brightness),
                                _vol, _mic, _brightness, _vibrancy, _appearance),
                            e.Source, buttonWidth, new Size(400, 420));
                    }
                    break;

                case MenuBarStatusButtonId.Search:
                    // 搜索：弹出搜索面板（程序/设置/文件）。服务缺失时面板内显示"不可用"占位（M10）。
                    ShowPopup(ref _searchPopup,
                        () => new SearchPopupWindow(_search, _appIcon, _vibrancy, _appearance, _pinning, _clipboard),
                        e.Source, buttonWidth, new Size(440, 500));
                    break;

                case MenuBarStatusButtonId.Ime:
                    // 左键=切换输入法（直接切下一个，不弹系统选择器 UI），右键=打开输入法选择面板
                    if (e.IsRightButton)
                    {
                        if (_ime is null)
                        {
                            // 监控服务未注入（null）：面板无数据可展示，静默降级（M10），避免 _ime! 在 null 上抛 NRE。
                            break;
                        }
                        ShowPopup(ref _imePopup, () => new ImePopupWindow(_ime, _vibrancy, _appearance),
                            e.Source, buttonWidth, new Size(290, 320));
                    }
                    else
                    {
                        ImeLayoutEnumerator.CycleOnce();
                        // 切换后立即刷新按钮图标：模拟热键切换不改变前台窗口，事件泵不触发，
                        // 500ms 兜底轮询也可能判定快照无变化——主动刷新保证图标跟随切换。
                        _strip?.RefreshImeIcon();
                    }
                    break;

                case MenuBarStatusButtonId.Battery:
                    ShowPopup(ref _powerPopup, () => new PowerPopupWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(290, 420));
                    break;

                case MenuBarStatusButtonId.NetworkTraffic:
                    ShowPopup(ref _networkPopup, () => new NetworkPanelWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(460, 520));
                    break;

                case MenuBarStatusButtonId.Memory:
                    ShowPopup(ref _memoryPopup, () => new MemoryPanelWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(460, 520));
                    break;

                case MenuBarStatusButtonId.Cpu:
                    ShowPopup(ref _cpuPopup, () => new CpuPanelWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(340, 300));
                    break;

                case MenuBarStatusButtonId.Microphone:
                    ShowPopup(ref _microphonePopup, () => new MicrophonePanelWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(320, 360));
                    break;

                case MenuBarStatusButtonId.Volume:
                    ShowPopup(ref _soundPopup, () => new SoundPanelWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(320, 420));
                    break;

                case MenuBarStatusButtonId.Wifi:
                    ShowPopup(ref _wifiPopup, () => new WifiPopupWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(320, 520));
                    break;

                case MenuBarStatusButtonId.Bluetooth:
                    ShowPopup(ref _bluetoothPopup, () => new BluetoothPopupWindow(_vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(300, 420));
                    break;

                case MenuBarStatusButtonId.Brightness:
                    ShowPopup(ref _themePopup, () => new ThemePopupWindow(_brightness, _vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(290, 360));
                    break;

                case MenuBarStatusButtonId.DateTime:
                    ShowPopup(ref _calendarPopup, () => new CalendarPopupWindow(_calendar, _vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(340, 420));
                    break;

                case MenuBarStatusButtonId.Extensions:
                    // "+"（扩展中心）：打开扩展管理面板，管理外部扩展功能插件与菜单栏模块显隐
                    ShowPopup(ref _extensionsCenterPopup,
                        () => new ExtensionsCenterWindow(
                            _settings,
                            (id, on) => _strip?.SetComponentVisible(id, on),
                            _vibrancy, _appearance),
                        e.Source, buttonWidth, new Size(320, 460));
                    break;

                default:
                    break;
            }
        }
        catch
        {
            // 单个面板打开失败不影响状态条其余按钮（M10 降级）
        }
    }

    /// <summary>懒创建并显示独立面板（首次创建、重复仅置前），锚定在按钮正下方。</summary>
    /// <remarks>
    /// 传 <c>source</c>（按钮本体）而非预先算好的坐标：DPI 换算与"取哪个显示器"都由
    /// <see cref="PopupAnchor"/> 统一处理，调用方不接触物理像素，避免单位混用。
    /// </remarks>
    private void ShowPopup<T>(ref T? popup, Func<T> factory, FrameworkElement source, double buttonWidth, Size popupSize)
        where T : MenuBarPopupWindow
    {
        popup ??= factory();

        // 弹窗互斥：仿 macOS，同一时刻只开一个面板。
        // 原实现每个面板各管各的，点了 CPU 再点声音会叠两层窗口，且旧面板不会自动收起。
        CloseAllExcept(popup);

        var pos = PopupAnchor.Compute(source, buttonWidth, popupSize, MenuBarMetrics.MenuBarHeight);
        popup.ShowAt(pos);
    }

    /// <summary>收起除 <paramref name="except"/> 之外的所有面板（实现弹窗互斥）。</summary>
    private void CloseAllExcept(MenuBarPopupWindow? except)
    {
        HideIfNot(_searchPopup, except);
        HideIfNot(_imePopup, except);
        HideIfNot(_extensionsCenterPopup, except);
        HideIfNot(_calendarPopup, except);
        HideIfNot(_controlCenterPopup, except);
        HideIfNot(_bluetoothPopup, except);
        HideIfNot(_wifiPopup, except);
        HideIfNot(_themePopup, except);
        HideIfNot(_powerPopup, except);
        HideIfNot(_networkPopup, except);
        HideIfNot(_memoryPopup, except);
        HideIfNot(_cpuPopup, except);
        HideIfNot(_microphonePopup, except);
        HideIfNot(_soundPopup, except);
        HideIfNot(_notificationCenterPopup, except);
    }

    private static void HideIfNot(MenuBarPopupWindow? window, MenuBarPopupWindow? except)
    {
        if (window is not null && !ReferenceEquals(window, except))
        {
            window.Hide();
        }
    }

    /// <summary>
    /// 运行时显隐某个菜单栏系统功能按钮。
    /// 供「设置 → 菜单栏」分区（<see cref="Sections.MenuBarSection"/>）在开关切换时即时生效。
    /// 状态条尚未创建时静默忽略——设置会先落盘，下次启动由 MenuBarStatusStrip 构造函数统一应用。
    /// </summary>
    public void SetComponentVisible(MenuBarStatusButtonId id, bool visible)
        => _strip?.SetComponentVisible(id, visible);

    /// <summary>
    /// 设置系统托盘是否隐藏与菜单栏专用按钮重复的系统图标（音量/网络/电源/安全与维护）。
    /// 供「设置 → 菜单栏」分区使用。
    /// </summary>
    public void SetTrayHideSystemIcons(bool hide)
        => _strip?.SetTrayHideSystemIcons(hide);

    public void Dispose()
    {
        if (_strip is not null)
        {
            _strip.ButtonClicked -= OnButtonClicked;
            _strip.Dispose();
            _strip = null;
        }
        _searchPopup?.Close();
        _imePopup?.Close();
        _extensionsCenterPopup?.Close();
        _calendarPopup?.Close();
        _controlCenterPopup?.Close();
        _bluetoothPopup?.Close();
        _wifiPopup?.Close();
        _themePopup?.Close();
        _powerPopup?.Close();
        _networkPopup?.Close();
        _memoryPopup?.Close();
        _cpuPopup?.Close();
        _microphonePopup?.Close();
        _soundPopup?.Close();
        _notificationCenterPopup?.Close();
    }
}
