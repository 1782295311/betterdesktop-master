// BetterDesktop.Shell.MenuBar — 控制中心"功能目录"模型与工厂
// 原则：控制中心只是功能的概览舱（简洁行），每个功能在控制中心内只显示一行小结，
// 点击唤起该功能"自己的独立完整面板"（Mac 风格）。本轮落地：网络/蓝牙/电源/屏幕/音量。
// 所有小结都直接从 shell-status 的服务读（单一数据源），因此与独立面板天然同步。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Contracts;
using Windows.Devices.Radios;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>控制中心里一个可唤起独立面板的功能行。</summary>
internal sealed class ControlCenterFeature
{
    public string Title { get; }
    public Func<string> Summary { get; }
    public Action<Point> OnActivate { get; }

    /// <summary>
    /// 若不为 null，表示该功能是一个可即时切换的开关（如 Wi‑Fi/蓝牙无线电）。
    /// 返回切换后的新状态：true=开，false=关；null 表示无适配器或调用失败。
    /// </summary>
    public Func<Task<bool?>>? ToggleAsync { get; }

    public bool IsToggle => ToggleAsync is not null;

    public ControlCenterFeature(string title, Func<string> summary, Action<Point> onActivate, Func<Task<bool?>>? toggleAsync = null)
    {
        Title = title;
        Summary = summary;
        OnActivate = onActivate;
        ToggleAsync = toggleAsync;
    }
}

/// <summary>装配控制中心的功能目录：每项对应一个独立面板，点击时在锚点唤起。</summary>
internal static class ControlCenterFeatureCatalog
{
    public static IReadOnlyList<ControlCenterFeature> Build(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        INetworkMonitor? net,
        IVolumeMonitor? vol,
        IMicrophoneMonitor? mic,
        IBatteryMonitor? bat,
        IBrightnessMonitor? brightness,
        IMediaPlaybackService? media = null)
    {
        var list = new List<ControlCenterFeature>(capacity: 10);

        // —— 顶部开关区：左列（大）——
        // 网络（Wi‑Fi → 独立面板；左键切换无线电，右键打开面板）
        WifiPopupWindow? wifi = null;
        list.Add(new ControlCenterFeature("Wi‑Fi",
            () => SummaryNet(net),
            anchor => { wifi ??= new WifiPopupWindow(vibrancy, appearance); wifi.RefreshContent(); ShowAt(wifi, anchor); },
            async () => await RadioInterop.ToggleAsync(RadioKind.WiFi)));

        // 蓝牙（左键切换无线电，右键打开面板）
        BluetoothPopupWindow? bt = null;
        list.Add(new ControlCenterFeature("蓝牙",
            SummaryBluetooth,
            anchor => { bt ??= new BluetoothPopupWindow(vibrancy, appearance); bt.Refresh(); ShowAt(bt, anchor); },
            async () => await RadioInterop.ToggleAsync(RadioKind.Bluetooth)));

        // 热点（空壳：Windows 可通过 ms-settings:network-mobilehotspot 唤起设置）
        list.Add(new ControlCenterFeature("热点",
            () => "关闭",
            anchor => LaunchSettings("ms-settings:network-mobilehotspot")));

        // —— 顶部开关区：右列（小）——
        // 投影（Win+K：投射到无线显示器 → ms-settings:project）
        list.Add(new ControlCenterFeature("投影",
            () => string.Empty,
            anchor => LaunchSettings("ms-settings:project")));

        // —— 下方卡片区：功能目录保留完整项，便于将来也作为"目录"来源 ——
        // 电源 / 性能（电池）
        PowerPopupWindow? power = null;
        list.Add(new ControlCenterFeature("电源",
            () => SummaryBattery(bat),
            anchor => { power ??= new PowerPopupWindow(vibrancy, appearance); ShowAt(power, anchor); }));

        // 屏幕（亮度 → 独立亮度面板）
        ThemePopupWindow? theme = null;
        list.Add(new ControlCenterFeature("屏幕",
            () => SummaryBrightness(brightness),
            anchor => { theme ??= new ThemePopupWindow(brightness, vibrancy, appearance); ShowAt(theme, anchor); }));

        // 声音（2026-09-14 修复：漏传 media 导致从这里打开的声音面板收不到 SMTC，音乐区永远空态）
        SoundPanelWindow? audio = null;
        list.Add(new ControlCenterFeature("声音",
            () => SummaryAudio(vol, mic),
            anchor => { audio ??= new SoundPanelWindow(vibrancy, appearance, media); ShowAt(audio, anchor); }));

        return list;
    }

    /// <summary>通过 Shell 打开 Windows 设置页（失败时静默降级）。
    /// 唯一实现收敛到 NativePanelStyles.OpenSystemSettings——历史上三处各写一份 Process.Start，
    /// 其中两份漏挂点击事件，表现为"文字在那儿但点了没反应"。</summary>
    private static void LaunchSettings(string uri) => NativePanelStyles.OpenSystemSettings(uri);



    /// <summary>
    /// 在锚点展开独立面板，越界时回拉以保证可见。
    /// 【单位纪律】anchor 由调用方经 <see cref="Contracts.MenuBarScreen.ToLogical"/> 换算过，
    /// 是**逻辑单位**；钳制边界也必须取锚点所在显示器的工作区（同样是逻辑单位）。
    /// 此前用 SystemParameters.WorkArea：它只描述主屏，多显示器下"在副屏点控制中心、面板弹到主屏边上"。
    /// </summary>
    private static void ShowAt(MenuBarPopupWindow window, Point anchor)
    {
        var area = MenuBarScreen.GetWorkArea(anchor);
        // 面板宽度/高度未知时按保守常数钳制（与历史行为一致），只保证不越出屏幕外。
        double x = Math.Clamp(anchor.X, area.Left, Math.Max(area.Left, area.Right - 320));
        double y = Math.Clamp(anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - 120));
        window.ShowAt(new Point(x, y));
    }

    private static string SummaryNet(INetworkMonitor? net)
    {
        // 优先读真实 Wi‑Fi 连接：有 SSID 时直接显示，比 network monitor 的“Wi‑Fi”文本更具体。
        try
        {
            var wifi = WifiEnumerator.ReadCurrentConnection();
            if (wifi.IsConnected && !string.IsNullOrEmpty(wifi.Ssid))
                return wifi.Ssid;
            var state = WifiEnumerator.GetInterfaceState();
            if (state < 0) return "无适配器";
            if (state == 0) return "未连接";
        }
        catch { }

        if (net is null) return "—";
        var snap = net.GetSnapshot();
        var text = string.IsNullOrEmpty(snap.ShortText) ? snap.HumanText : snap.ShortText;
        return string.IsNullOrEmpty(text) ? "—" : text;
    }

    private static string SummaryBluetooth()
    {
        try
        {
            // 先看是否有已连接设备，这比“已开启”更有信息价值。
            var devices = BluetoothEnumerator.Enumerate();
            var connected = devices.FirstOrDefault(d => d.IsConnected);
            if (connected is not null && !string.IsNullOrEmpty(connected.Name))
                return connected.Name;

            var state = BluetoothEnumerator.GetRadioState();
            return state switch
            {
                BluetoothRadioState.On => "已开启",
                BluetoothRadioState.Off => "已关闭",
                _ => "无适配器"
            };
        }
        catch { return "—"; }
    }

    private static string SummaryBattery(IBatteryMonitor? bat)
    {
        if (bat is null) return "—";
        var snap = bat.GetSnapshot();
        return string.IsNullOrEmpty(snap.ShortText) ? snap.HumanText : snap.ShortText;
    }

    private static string SummaryBrightness(IBrightnessMonitor? brightness)
    {
        if (brightness is null || !brightness.IsBrightnessSupported) return "—";
        var snap = brightness.GetSnapshot();
        return snap.ShortText;
    }

    private static string SummaryAudio(IVolumeMonitor? vol, IMicrophoneMonitor? mic)
    {
        string volume = "—";
        if (vol is not null)
        {
            var vs = vol.GetSnapshot();
            volume = string.IsNullOrEmpty(vs.ShortText) ? vs.HumanText : vs.ShortText;
        }
        string micState = mic?.GetSnapshot().IconKey == "mic-muted" ? " · 静音" : string.Empty;
        return string.IsNullOrWhiteSpace(volume) ? micState.Trim() : volume + micState;
    }
}
