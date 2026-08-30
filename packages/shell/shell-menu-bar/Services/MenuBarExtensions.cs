// BetterDesktop.Shell.MenuBar — 3 个独立面板的 IMenuBarExtension 实现
// 每个扩展：GetVisual() 返回菜单栏右区按钮（图标+文本来自 StatusSnapshot，不硬编码）；
// OpenPopup(anchor) 负责定位并弹自己独有的 ShellWindow；每个面板窗口独立单例。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>IME 菜单栏按钮 + 弹窗（独立面板）。左键=切换一次输入法，右键=打开面板。
/// TSF 输入法显示真实图标；纯键盘布局显示语言代码文本（如 "ENG"），与 Windows 11 语言栏一致。</summary>
internal sealed class ImeMenuBarExtension : IMenuBarExtension, IDisposable
{
    public string Id => "ime";
    private readonly IImeMonitor _ime;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private ImePopupWindow? _popup;
    private Image? _iconImage;
    private TextBlock? _langText;
    private IntPtr _currentIconHandle;

    public ImeMenuBarExtension(IImeMonitor ime, IVibrancyService vibrancy, IAppearanceService? appearance)
    {
        _ime = ime; _vibrancy = vibrancy; _appearance = appearance;
        _ime.Changed += OnChanged;
    }

    public FrameworkElement GetVisual()
    {
        var container = new Grid
        {
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        _iconImage = new Image
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);
        _langText = new TextBlock
        {
            Foreground = MenuBarTheme.Foreground,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Visible
        };
        container.Children.Add(_iconImage);
        container.Children.Add(_langText);
        UpdateVisual();
        return container;
    }

    public void OpenPopup(Point anchorScreenTopLeft)
    {
        _popup ??= new ImePopupWindow(_ime, _vibrancy, _appearance);
        var pos = PopupAnchor.Compute(anchorScreenTopLeft, 48, new Size(_popup.Width, 320), MenuBarMetrics.MenuBarHeight);
        _popup.ShowAt(pos);
    }

    public void ClosePopup()
    {
        if (_popup?.IsVisible == true) { _popup.Hide(); }
    }

    private void OnChanged(object? sender, StatusSnapshot e)
    {
        UpdateVisual();
    }

    /// <summary>根据当前激活的输入法更新显示：TSF 显示图标，纯键盘布局显示语言代码。</summary>
    private void UpdateVisual()
    {
        if (_iconImage is null || _langText is null) return;
        try
        {
            var layouts = KeyboardLayoutInterop.Enumerate();
            var active = layouts.FirstOrDefault(x => x.IsActive);
            if (active is null && layouts.Count > 0) active = layouts[0];
            if (active is null) return;

            var hIcon = KeyboardLayoutInterop.GetLayoutIconHandle(active.KlidHex, active.IsTs);
            if (hIcon != IntPtr.Zero)
            {
                // TSF 输入法：显示图标
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                _iconImage.Source = source;
                _iconImage.Visibility = Visibility.Visible;
                _langText.Visibility = Visibility.Collapsed;
                if (_currentIconHandle != IntPtr.Zero)
                    KeyboardLayoutInterop.ReleaseIcon(_currentIconHandle);
                _currentIconHandle = hIcon;
            }
            else
            {
                // 纯键盘布局：显示语言代码（如 "ENG"）
                _langText.Text = KeyboardLayoutInterop.GetLanguageCode(active.KlidHex);
                _iconImage.Visibility = Visibility.Collapsed;
                _langText.Visibility = Visibility.Visible;
                if (_currentIconHandle != IntPtr.Zero)
                {
                    KeyboardLayoutInterop.ReleaseIcon(_currentIconHandle);
                    _currentIconHandle = IntPtr.Zero;
                }
            }
        }
        catch { /* 更新失败静默，保持上一个状态 */ }
    }

    public void Dispose()
    {
        _ime.Changed -= OnChanged;
        if (_currentIconHandle != IntPtr.Zero)
        {
            KeyboardLayoutInterop.ReleaseIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
        _popup?.Close();
    }
}

/// <summary>CPU 菜单栏按钮 + 弹窗（独立面板）。显示实时 CPU 利用率，点击打开详情面板。</summary>
internal sealed class CpuMenuBarExtension : IMenuBarExtension, IDisposable
{
    public string Id => "cpu";
    private readonly ICpuMonitor _cpu;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private CpuPanelWindow? _popup;
    private TextBlock? _label;

    public CpuMenuBarExtension(ICpuMonitor cpu, IVibrancyService vibrancy, IAppearanceService? appearance)
    {
        _cpu = cpu; _vibrancy = vibrancy; _appearance = appearance;
        _cpu.Changed += OnChanged;
    }

    public FrameworkElement GetVisual()
    {
        var snap = _cpu.GetSnapshot();
        _label = new TextBlock
        {
            Text = string.IsNullOrEmpty(snap.ShortText) ? "—%" : snap.ShortText,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0, 2, 0),
            SnapsToDevicePixels = true
        };
        return _label;
    }

    public void OpenPopup(Point anchorScreenTopLeft)
    {
        _popup ??= new CpuPanelWindow(_vibrancy, _appearance);
        var pos = PopupAnchor.Compute(anchorScreenTopLeft, 60, new Size(_popup.Width, 220), MenuBarMetrics.MenuBarHeight);
        _popup.ShowAt(pos);
    }

    public void ClosePopup()
    {
        if (_popup?.IsVisible == true) { _popup.Hide(); }
    }

    private void OnChanged(object? sender, StatusSnapshot e)
    {
        if (_label is not null && !string.IsNullOrEmpty(e.ShortText))
        {
            _label.Text = e.ShortText;
        }
    }

    public void Dispose()
    {
        _cpu.Changed -= OnChanged;
        _popup?.Close();
    }
}

/// <summary>日历菜单栏按钮 + 弹窗（独立面板）。</summary>
internal sealed class CalendarMenuBarExtension : IMenuBarExtension, IDisposable
{
    public string Id => "calendar";
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private CalendarPopupWindow? _popup;
    private TextBlock? _label;
    private readonly System.Windows.Threading.DispatcherTimer _ticker;

    public CalendarMenuBarExtension(IVibrancyService vibrancy, IAppearanceService? appearance)
    {
        _vibrancy = vibrancy; _appearance = appearance;
        _ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) => UpdateLabel();
        _ticker.Start();
    }

    public FrameworkElement GetVisual()
    {
        _label = new TextBlock
        {
            Foreground = MenuBarTheme.Foreground,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            SnapsToDevicePixels = true
        };
        UpdateLabel();
        return _label;
    }

    public void OpenPopup(Point anchorScreenTopLeft)
    {
        _popup ??= new CalendarPopupWindow(_vibrancy, _appearance);
        var pos = PopupAnchor.Compute(anchorScreenTopLeft, 150, new Size(_popup.Width, 420), MenuBarMetrics.MenuBarHeight);
        _popup.ShowAt(pos);
    }

    public void ClosePopup() { if (_popup?.IsVisible == true) { _popup.Hide(); } }

    private void UpdateLabel()
    {
        if (_label is null) return;
        // 系统当前时间：不写死
        _label.Text = DateTime.Now.ToString("HH:mm  M/d", System.Globalization.CultureInfo.CurrentUICulture);
    }

    public void Dispose()
    {
        _ticker.Stop();
        _popup?.Close();
    }
}

/// <summary>控制中心（功能托盘）菜单栏按钮 + 收纳面板（独立面板）。</summary>
internal sealed class ControlCenterMenuBarExtension : IMenuBarExtension, IDisposable
{
    public string Id => "control-center";
    private readonly INetworkMonitor? _net;
    private readonly IVolumeMonitor? _vol;
    private readonly IMicrophoneMonitor? _mic;
    private readonly IBatteryMonitor? _bat;
    private readonly IBrightnessMonitor? _brightness;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private ControlCenterWindow? _popup;

    public ControlCenterMenuBarExtension(
        INetworkMonitor? net, IVolumeMonitor? vol, IMicrophoneMonitor? mic, IBatteryMonitor? bat,
        IBrightnessMonitor? brightness,
        IVibrancyService vibrancy, IAppearanceService? appearance)
    {
        _net = net; _vol = vol; _mic = mic; _bat = bat; _brightness = brightness;
        _vibrancy = vibrancy; _appearance = appearance;
    }

    public FrameworkElement GetVisual()
    {
        // 菜单栏图标：双横线"功能托盘"符号。不写死文字；颜色随主题（暂白）
        var icon = new TextBlock
        {
            Text = "◫",
            Foreground = MenuBarTheme.Foreground,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            SnapsToDevicePixels = true
        };
        return icon;
    }

    public void OpenPopup(Point anchorScreenTopLeft)
    {
        if (_popup is null)
        {
            // 控制中心 = 功能目录：每项构造独立面板，点击唤起。共用同一批服务（数据源同步）。
            var features = ControlCenterFeatureCatalog.Build(_vibrancy, _appearance, _net, _vol, _mic, _bat, _brightness);
            _popup = new ControlCenterWindow(features, _vol, _mic, _brightness, _vibrancy, _appearance);
        }
        var pos = PopupAnchor.Compute(anchorScreenTopLeft, 30, new Size(_popup.Width, 560), MenuBarMetrics.MenuBarHeight);
        _popup.ShowAt(pos);
    }

    public void ClosePopup() { if (_popup?.IsVisible == true) { _popup.Hide(); } }

    public void Dispose() { _popup?.Close(); }
}
