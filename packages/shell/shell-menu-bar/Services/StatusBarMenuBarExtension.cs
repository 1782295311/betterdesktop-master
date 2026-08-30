// BetterDesktop.Shell.MenuBar — 状态条扩展（菜单栏右区主体）
// 角色：把移植自 tools/ShellComponentsPlayground 的紧凑状态条（MenuBarStatusStrip）作为菜单栏右区。
// 插件化约定：数据全部来自 Inject 的 shell.status 监控服务（可能为 null → 状态条内图标降级为占位，不抛）；
//             每个状态图标左键/右键点击 → 打开对应独立面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）。
// 面板均为独立 ShellWindow（MenuBarPopupWindow），复用什么都不做的通用弹窗基类；状态条自身不持有面板引用。

using System;
using System.Windows;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.MenuBar.Windows;
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

    private MenuBarStatusStrip? _strip;
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
        ISettingsService? settings = null)
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
            // 状态条自身已处理的动作（显示桌面）：不重复干预。
            if (e.Button == MenuBarStatusButtonId.Desktop)
            {
                return;
            }

            var anchor = e.Source.PointToScreen(new Point(0, 0));
            double buttonWidth = Math.Max(e.Source.ActualWidth, 16);

            switch (e.Button)
            {
                case MenuBarStatusButtonId.Notification:
                    // 通知图标入口：打开控制中心（功能目录布局），与加号入口解耦
                    ShowPopup(ref _controlCenterPopup,
                        () => new ControlCenterWindow(
                            ControlCenterFeatureCatalog.Build(_vibrancy, _appearance, _net, _vol, _mic, _bat, _brightness),
                            _vol, _mic, _brightness, _vibrancy, _appearance),
                        anchor, buttonWidth, new Size(360, 420));
                    break;

                case MenuBarStatusButtonId.Ime:
                    // 左键=切换输入法（Win+Space 循环），右键=打开输入法选择面板
                    if (e.IsRightButton)
                    {
                        if (_ime is null)
                        {
                            // 监控服务未注入（null）：面板无数据可展示，静默降级（M10），避免 _ime! 在 null 上抛 NRE。
                            break;
                        }
                        ShowPopup(ref _imePopup, () => new ImePopupWindow(_ime, _vibrancy, _appearance),
                            anchor, buttonWidth, new Size(290, 320));
                    }
                    else
                    {
                        ImeLayoutEnumerator.CycleOnce();
                    }
                    break;

                case MenuBarStatusButtonId.Battery:
                    ShowPopup(ref _powerPopup, () => new PowerPopupWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(290, 420));
                    break;

                case MenuBarStatusButtonId.NetworkTraffic:
                    ShowPopup(ref _networkPopup, () => new NetworkPanelWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(460, 520));
                    break;

                case MenuBarStatusButtonId.Memory:
                    ShowPopup(ref _memoryPopup, () => new MemoryPanelWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(460, 520));
                    break;

                case MenuBarStatusButtonId.Cpu:
                    ShowPopup(ref _cpuPopup, () => new CpuPanelWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(340, 300));
                    break;

                case MenuBarStatusButtonId.Microphone:
                    ShowPopup(ref _microphonePopup, () => new MicrophonePanelWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(320, 360));
                    break;

                case MenuBarStatusButtonId.Volume:
                    ShowPopup(ref _soundPopup, () => new SoundPanelWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(320, 420));
                    break;

                case MenuBarStatusButtonId.Wifi:
                    ShowPopup(ref _wifiPopup, () => new WifiPopupWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(320, 520));
                    break;

                case MenuBarStatusButtonId.Bluetooth:
                    ShowPopup(ref _bluetoothPopup, () => new BluetoothPopupWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(300, 420));
                    break;

                case MenuBarStatusButtonId.Brightness:
                    ShowPopup(ref _themePopup, () => new ThemePopupWindow(_brightness, _vibrancy, _appearance),
                        anchor, buttonWidth, new Size(290, 360));
                    break;

                case MenuBarStatusButtonId.DateTime:
                    ShowPopup(ref _calendarPopup, () => new CalendarPopupWindow(_vibrancy, _appearance),
                        anchor, buttonWidth, new Size(340, 420));
                    break;

                case MenuBarStatusButtonId.Extensions:
                    // "+"（扩展中心）：打开扩展管理面板，管理外部扩展功能插件与菜单栏模块显隐
                    ShowPopup(ref _extensionsCenterPopup,
                        () => new ExtensionsCenterWindow(
                            _settings,
                            (id, on) => _strip?.SetComponentVisible(id, on),
                            _vibrancy, _appearance),
                        anchor, buttonWidth, new Size(320, 460));
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
    private void ShowPopup<T>(ref T? popup, Func<T> factory, Point anchor, double buttonWidth, Size popupSize)
        where T : MenuBarPopupWindow
    {
        popup ??= factory();
        var pos = PopupAnchor.Compute(anchor, buttonWidth, popupSize, MenuBarMetrics.MenuBarHeight);
        popup.ShowAt(pos);
    }

    public void Dispose()
    {
        if (_strip is not null)
        {
            _strip.ButtonClicked -= OnButtonClicked;
            _strip.Dispose();
            _strip = null;
        }
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
    }
}
