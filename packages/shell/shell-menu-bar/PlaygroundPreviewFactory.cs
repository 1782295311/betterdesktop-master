// shell-menu-bar 对外公开的"独立预览工厂"
// 供 tools/ShellComponentsPlayground 之类验证工具一次性取到：菜单条右区按钮视觉 + 3 个独立面板 UI
// 不引 ShellWindow（不启动独立窗口），仅返回 FrameworkElement 让验证工具嵌入大容器。
// 所有 internal 访问集中在此处理，Playground 不关心任何 internal 类。

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar;

/// <summary>大容器验证工作台使用的预览数据（菜单条 + 十三块独立面板内容，全部是系统真值，零硬编码假数据）。</summary>
public sealed class PlaygroundPreview
{
    /// <summary>菜单条顶部预览（左区占位 + 弹簧 + 右区 3 个按钮视觉）。</summary>
    public FrameworkElement MenuBarStrip { get; }

    /// <summary>独立面板 ① IME 输入法切换面板的内容。</summary>
    public FrameworkElement ImePanel { get; }

    /// <summary>独立面板 ② 日历 / 农历面板的内容。</summary>
    public FrameworkElement CalendarPanel { get; }

    /// <summary>独立面板 ③ 控制中心（收纳式）面板的内容。</summary>
    public FrameworkElement ControlCenterPanel { get; }

    /// <summary>独立面板 ④ 蓝牙面板。</summary>
    public FrameworkElement BluetoothPanel { get; }

    /// <summary>独立面板 ⑤ Wi‑Fi 连接&附近网络面板。</summary>
    public FrameworkElement WifiPanel { get; }

    /// <summary>独立面板 ⑥ 主题（深色/浅色）面板。</summary>
    public FrameworkElement ThemePanel { get; }

    /// <summary>独立面板 ⑦ 电池 & 性能模式面板。</summary>
    public FrameworkElement PowerPanel { get; }

    /// <summary>独立面板 ⑨ NETWORK 详情面板（公网IP+地理 + 本地IP + 实时流量图 + 上下行速度/累计）。</summary>
    public FrameworkElement NetworkPanel { get; }

    /// <summary>独立面板 ⑩ MEM 详情面板（内存占用条 + TOP 进程 + 物理内存条规格）。</summary>
    public FrameworkElement MemoryPanel { get; }

    /// <summary>独立面板 ⑪ CPU 详情面板（真实 CPU 占用柱状图）。</summary>
    public FrameworkElement CpuPanel { get; }

    /// <summary>独立面板 ⑫ 麦克风面板（输入音量整数滑块 + 输入设备枚举）。</summary>
    public FrameworkElement MicrophonePanel { get; }

    /// <summary>独立面板 ⑬ 声音面板（输出音量整数化 + 真实音频会话 + SMTC 播放控制）。</summary>
    public FrameworkElement SoundPanel { get; }

    public PlaygroundPreview(
        FrameworkElement menuBarStrip,
        FrameworkElement imePanel,
        FrameworkElement calendarPanel,
        FrameworkElement controlCenterPanel,
        FrameworkElement bluetoothPanel,
        FrameworkElement wifiPanel,
        FrameworkElement themePanel,
        FrameworkElement powerPanel,
        FrameworkElement networkPanel,
        FrameworkElement memoryPanel,
        FrameworkElement cpuPanel,
        FrameworkElement microphonePanel,
        FrameworkElement soundPanel)
    {
        MenuBarStrip = menuBarStrip;
        ImePanel = imePanel;
        CalendarPanel = calendarPanel;
        ControlCenterPanel = controlCenterPanel;
        BluetoothPanel = bluetoothPanel;
        WifiPanel = wifiPanel;
        ThemePanel = themePanel;
        PowerPanel = powerPanel;
        NetworkPanel = networkPanel;
        MemoryPanel = memoryPanel;
        CpuPanel = cpuPanel;
        MicrophonePanel = microphonePanel;
        SoundPanel = soundPanel;
    }
}

/// <summary>构造 Playground 预览：装配依赖 + 统一把 internal 面板的 BuildPreviewContent 暴露出来。</summary>
public static class PlaygroundPreviewFactory
{
    public static PlaygroundPreview Build(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IImeMonitor? ime,
        INetworkMonitor? net,
        IVolumeMonitor? vol,
        IMicrophoneMonitor? mic,
        IBatteryMonitor? bat,
        ICpuMonitor? cpu,
        IBrightnessMonitor? brightness = null,
        System.Action<FrameworkElement>? refreshPowerPanel = null)
    {
        vibrancy ??= new NullVibrancyForPreview();

        // -------- 菜单条右区按钮（使用 internal MenuBarExtensions 构造） --------
        var extensions = new List<Contracts.IMenuBarExtension>(capacity: 4);
        extensions.Add(new Services.ControlCenterMenuBarExtension(net, vol, mic, bat, brightness, vibrancy, appearance));
        if (ime is not null) extensions.Add(new Services.ImeMenuBarExtension(ime, vibrancy, appearance));
        if (cpu is not null) extensions.Add(new Services.CpuMenuBarExtension(cpu, vibrancy, appearance));
        extensions.Add(new Services.CalendarMenuBarExtension(vibrancy, appearance));

        var root = new Grid
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Height = 14
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftZone = new TextBlock
        {
            Text = "  BetterDesktop 菜单项（占位）",
            Foreground = MenuBarTheme.Foreground,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(leftZone, 0);
        root.Children.Add(leftZone);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        foreach (var ext in extensions)
        {
            var v = ext.GetVisual();
            if (v is null) continue;
            v.Margin = new Thickness(10, 0, 0, 0);
            v.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(v);
        }
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        // -------- 三个独立面板 UI（直接嵌内容，不启动 ShellWindow） --------
        FrameworkElement imePanel;
        if (ime is null)
        {
            imePanel = new TextBlock
            {
                Text = "未检测到 IImeMonitor（shell.status 未注册 IME 监控）",
                Foreground = Brushes.DarkGray
            };
        }
        else
        {
            var w = new Windows.ImePopupWindow(ime, vibrancy, appearance);
            imePanel = w.BuildPreviewContent();
        }

        var cal = new Windows.CalendarPopupWindow(vibrancy, appearance);
        var ccFeatures = Services.ControlCenterFeatureCatalog.Build(vibrancy, appearance, net, vol, mic, bat, brightness);
        var cc = new Windows.ControlCenterWindow(ccFeatures, vol, mic, brightness, vibrancy, appearance);

        // -------- 四个新面板 UI（蓝牙 / Wi‑Fi / 主题 / 电池&性能） --------
        var blue = new Windows.BluetoothPopupWindow(vibrancy, appearance);
        var wifi = new Windows.WifiPopupWindow(vibrancy, appearance);
        var theme = new Windows.ThemePopupWindow(brightness, vibrancy, appearance);
        var power = new Windows.PowerPopupWindow(vibrancy, appearance, refreshPowerPanel);

        // -------- C++ 原生层驱动的五个独立详情面板 --------
        var netPanel = new Windows.NetworkPanelWindow(vibrancy, appearance);
        var memPanel = new Windows.MemoryPanelWindow(vibrancy, appearance);
        var cpuPanel = new Windows.CpuPanelWindow(vibrancy, appearance);
        var micPanel = new Windows.MicrophonePanelWindow(vibrancy, appearance);
        var soundPanel = new Windows.SoundPanelWindow(vibrancy, appearance);

        return new PlaygroundPreview(
            menuBarStrip: root,
            imePanel: imePanel,
            calendarPanel: cal.BuildPreviewContent(),
            controlCenterPanel: cc.BuildPreviewContent(),
            bluetoothPanel: blue.BuildPreviewContent(),
            wifiPanel: wifi.BuildPreviewContent(),
            themePanel: theme.BuildPreviewContent(),
            powerPanel: power.BuildPreviewContent(),
            networkPanel: netPanel.BuildPreviewContent(),
            memoryPanel: memPanel.BuildPreviewContent(),
            cpuPanel: cpuPanel.BuildPreviewContent(),
            microphonePanel: micPanel.BuildPreviewContent(),
            soundPanel: soundPanel.BuildPreviewContent());
    }

    /// <summary>兜底：IVibrancyService 为空时的空壳（与 MenuBarPlugin.NullVibrancy 独立，避免相互 internal 引用）。</summary>
    private sealed class NullVibrancyForPreview : IVibrancyService
    {
        public void Apply(IntPtr hWnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }
        public void Disable(IntPtr hWnd) { }
    }
}
