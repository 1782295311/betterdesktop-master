// BetterDesktop.Shell.MenuBar — 3 个独立面板的 IMenuBarExtension 实现
// 每个扩展：GetVisual() 返回菜单栏右区按钮（图标+文本来自 StatusSnapshot，不硬编码）；
// OpenPopup(anchor) 负责定位并弹自己独有的 ShellWindow；每个面板窗口独立单例。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

// ── 本文件方法级白话索引（3 个菜单栏扩展，每个 = 一个右区图标；统一实现 IMenuBarExtension）──
//   "输入法菜单栏图标（点开标点/输入法面板）" → ImeMenuBarExtension（OnChanged 刷新、UpdateVisual 改外观；B11：回调经 UiDispatch 回 UI 线程）
//   "CPU 菜单栏图标（点开 CPU 面板）"         → CpuMenuBarExtension（B11：OnChanged 经 UiDispatch 回 UI 线程）
//   "日历/时间菜单栏图标（点开日历面板）"     → CalendarMenuBarExtension（DispatcherTimer 在 UI 线程，天然安全）
//   控制中心不占独立菜单栏图标：由通知图标（Notification）双用（见 StatusBarMenuBarExtension.cs 的
//   case Notification：左键=控制中心、右键=通知中心，同一图标空间按使用频率分左右键）。
//   每个扩展的固定套路：GetVisual 出图标、OpenPopup/ClosePopup 控制面板、OnChanged 订阅状态、Dispose 退订。
//   扩展如何被菜单栏收集见 shell-core 的 MenuBarExtensionRegistry；右区整条状态条见 Status/MenuBarStatusStrip.cs。
// ────────────────────────────────────

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

    /// <summary>切换输入法后由 MenuBarWindow 主动调用，强制刷新按钮图标。
    /// 输入法切换（模拟热键）不改变前台窗口，事件泵不触发；500ms 兜底轮询也可能因
    /// 快照文本未变而判定无变化——主动刷新保证点击切换后图标立即跟随。</summary>
    public void RefreshVisual() => UpdateVisual();

    private void OnChanged(object? sender, StatusSnapshot e)
    {
        // B11 修复：IImeMonitor.Changed 由 StatusPoller 后台线程触发，直接写控件会跨线程抛异常
        // （被 UpdateVisual 的 try/catch 吞掉 → 图标永不刷新）。用 UiDispatch 编队回 UI 线程。
        if (_iconImage is null) return;
        UiDispatch.Run(_iconImage, UpdateVisual);
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
        // B11 修复：ICpuMonitor.Changed 来自后台线程，_label.Text 必须回 UI 线程写。
        if (_label is null || string.IsNullOrEmpty(e.ShortText)) return;
        var text = e.ShortText;
        UiDispatch.Run(_label, () => _label.Text = text);
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
    private readonly BetterDesktop.Shell.Calendar.Contracts.ICalendarService? _calendar;
    private CalendarPopupWindow? _popup;
    private TextBlock? _label;
    private readonly System.Windows.Threading.DispatcherTimer _ticker;

    public CalendarMenuBarExtension(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        BetterDesktop.Shell.Calendar.Contracts.ICalendarService? calendar = null)
    {
        _vibrancy = vibrancy; _appearance = appearance; _calendar = calendar;
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
        _popup ??= new CalendarPopupWindow(_calendar, _vibrancy, _appearance);
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
