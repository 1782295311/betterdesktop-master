// BetterDesktop.Shell.MenuBar — 控制中心"功能目录"模型与工厂
// 原则：控制中心只是功能的概览舱（简洁行），每个功能在控制中心内只显示一行小结，
// 点击唤起该功能"自己的独立完整面板"（Mac 风格）。本轮落地：网络/蓝牙/电源/屏幕/音量。
// 所有小结都直接从 shell-status 的服务读（单一数据源），因此与独立面板天然同步。

using System;
using System.Collections.Generic;
using System.Windows;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>控制中心里一个可唤起独立面板的功能行。</summary>
internal sealed class ControlCenterFeature
{
    public string Title { get; }
    public Func<string> Summary { get; }
    public Action<Point> OnActivate { get; }

    public ControlCenterFeature(string title, Func<string> summary, Action<Point> onActivate)
    {
        Title = title;
        Summary = summary;
        OnActivate = onActivate;
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
        IBrightnessMonitor? brightness)
    {
        var list = new List<ControlCenterFeature>(capacity: 10);

        // —— 顶部开关区：左列（大）——
        // 网络（Wi‑Fi → 独立面板）
        WifiPopupWindow? wifi = null;
        list.Add(new ControlCenterFeature("Wi‑Fi",
            () => SummaryNet(net),
            anchor => { wifi ??= new WifiPopupWindow(vibrancy, appearance); wifi.RefreshContent(); ShowAt(wifi, anchor); }));

        // 蓝牙
        BluetoothPopupWindow? bt = null;
        list.Add(new ControlCenterFeature("蓝牙",
            SummaryBluetooth,
            anchor => { bt ??= new BluetoothPopupWindow(vibrancy, appearance); bt.Refresh(); ShowAt(bt, anchor); }));

        // 热点（空壳：Windows 可通过 ms-settings:network-mobilehotspot 唤起设置）
        list.Add(new ControlCenterFeature("热点",
            () => "关闭",
            anchor => LaunchSettings("ms-settings:network-mobilehotspot")));

        // —— 顶部开关区：右列（小）——
        // 专注助手（空壳：Win11 专注会话由 ms-settings:quietmoments 管理）
        list.Add(new ControlCenterFeature("专注助手",
            () => string.Empty,
            anchor => LaunchSettings("ms-settings:quietmoments")));

        // 台前调度（空壳：Windows 无原生等价项；唤起多任务视图 Win+Tab 通过 User32 发送 Win 组合键后续接入）
        list.Add(new ControlCenterFeature("台前调度",
            () => string.Empty,
            anchor => LaunchSettings("ms-settings:multitasking")));

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

        // 声音
        SoundPanelWindow? audio = null;
        list.Add(new ControlCenterFeature("声音",
            () => SummaryAudio(vol, mic),
            anchor => { audio ??= new SoundPanelWindow(vibrancy, appearance); ShowAt(audio, anchor); }));

        return list;
    }

    /// <summary>通过 Shell 打开 Windows 设置页（失败时静默降级）。</summary>
    private static void LaunchSettings(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static void ShowAt(MenuBarPopupWindow window, Point anchor)
    {
        // 以触发瓦片的屏幕坐标作为锚点展开独立面板；越界时回拉以保证可见。
        double x = anchor.X, y = anchor.Y;
        x = Math.Clamp(x, 0, SystemParameters.WorkArea.Right - 320);
        y = Math.Clamp(y, 0, SystemParameters.WorkArea.Bottom - 120);
        window.ShowAt(new Point(x, y));
    }

    private static string SummaryNet(INetworkMonitor? net)
    {
        if (net is null) return "—";
        var snap = net.GetSnapshot();
        var text = string.IsNullOrEmpty(snap.ShortText) ? snap.HumanText : snap.ShortText;
        return string.IsNullOrEmpty(text) ? "—" : text;
    }

    private static string SummaryBluetooth()
    {
        try
        {
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
