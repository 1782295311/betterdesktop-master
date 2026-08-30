// Shell 组件独立验证工作台（大容器）
// 画面：自绘标题栏 + 左侧导航 + 右侧滚动分区（Hero 菜单栏预览 · 核心独立面板 · 真实功能面板）
// 数据：全部来自系统真值，零硬编码假数据。
//   - 菜单栏按钮 & 独立面板：PlaygroundPreviewFactory.Build → IImeMonitor / INetworkMonitor / IVolumeMonitor ...
//   - 真实功能面板（⑧-⑫）：NetworkCore / MemoryCore / CpuCore / AudioCore 原生 DLL 封装

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Timer;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;
using BetterDesktop.Shell.Status;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Tools.ShellComponentsPlayground;

public partial class PlaygroundWindow : Window
{
    private MenuBarStatusStrip? _rightButtons;

    public PlaygroundWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _rightButtons?.Dispose();
        _rightButtons = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ========= 最小内核装配（复现 Bootstrap 的关键 Provide/Plugin，不引其他无关插件） =========
        // 日志同时写文件（status-events.log），便于后台观测事件驱动是否即时触发。
        var logPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "status-events.log");
        void WriteLog(string line)
        {
            Console.WriteLine(line);
            try { System.IO.File.AppendAllText(logPath, line + Environment.NewLine); }
            catch { /* 日志文件写入失败不影响运行 */ }
        }

        try
        {
#pragma warning disable CA2000
            var context = new CordisContext(logSink: (level, msg) => WriteLog($"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}"));
#pragma warning restore CA2000

#pragma warning disable CA2000
            var settings = new SettingsService();
#pragma warning restore CA2000
            context.Provide<ISettingsService>(settings);
            context.Provide<ISettingsSectionRegistry>(new SettingsSectionRegistry());
            var appearance = new AppearanceService(settings);
            context.Provide<IAppearanceService>(appearance);

            // 单个插件装配失败不应清空整个工作台：逐项保护，失败仅记录。
            TryPlugin(context, new PowerManagement(context.Logger), WriteLog);
            TryPlugin(context, new TimerService(), WriteLog);
            TryPlugin(context, new VibrancyService(), WriteLog);
            TryPlugin(context, new StatusPlugin(), WriteLog);

            // 获取服务（Monitor 缺失时工厂会降级为占位文案，不抛异常）
            var vibrancy = context.Get<IVibrancyService>() ?? new NullVibrancyPlay();
            var bat = context.Get<IBatteryMonitor>();
            var vol = context.Get<IVolumeMonitor>();
            var mic = context.Get<IMicrophoneMonitor>();
            var net = context.Get<INetworkMonitor>();
            var ime = context.Get<IImeMonitor>();
            var cpu = context.Get<ICpuMonitor>();
            var mem = context.Get<IMemoryMonitor>();
            var brightness = context.Get<IBrightnessMonitor>();

            // ========= Hero 卡：顶部菜单栏 + 三列核心独立面板 =========
            var preview = PlaygroundPreviewFactory.Build(vibrancy, appearance, ime, net, vol, mic, bat, cpu, brightness,
                refreshPowerPanel: fe => PowerHost.Content = fe);
            MenuBarHost.Children.Clear();
            MenuBarHost.Children.Add(preview.MenuBarStrip);

            // ========= 菜单栏右半侧功能按钮组（从右到左：桌面 | 时间 | 扩展(+) | 通知(双胶囊) | 音量 | 麦克风 | 电池）=========
            // 注：MenuBarStatusStrip 已由 tools 移植进主项目 packages/shell/shell-menu-bar/Status（单一真源），此处直接复用包内实现。
            _rightButtons = new MenuBarStatusStrip(vol, mic, bat, ime, brightness, net, mem, cpu);
            // 找到菜单栏右区 StackPanel（Grid 的 Column=1），替换原有按钮
            if (preview.MenuBarStrip is Grid menuGrid)
            {
                foreach (var child in menuGrid.Children)
                {
                    if (child is StackPanel sp && Grid.GetColumn(sp) == 1)
                    {
                        sp.Children.Clear();
                        sp.Children.Add(_rightButtons);
                        break;
                    }
                }
            }

            ImeHost.Content = preview.ImePanel;
            CalendarHost.Content = preview.CalendarPanel;
            ControlCenterHost.Content = preview.ControlCenterPanel;
            BluetoothHost.Content = preview.BluetoothPanel;
            WifiHost.Content = preview.WifiPanel;
            ThemeHost.Content = preview.ThemePanel;
            PowerHost.Content = preview.PowerPanel;
            NetworkHost.Content = preview.NetworkPanel;
            MemoryHost.Content = preview.MemoryPanel;
            CpuHost.Content = preview.CpuPanel;
            MicrophoneHost.Content = preview.MicrophonePanel;
            SoundHost.Content = preview.SoundPanel;
        }
        catch (Exception ex)
        {
            WriteLog($"{DateTime.Now:HH:mm:ss.fff} [FATAL] 工作台装配失败：{ex}");
            // 异常会冒泡到 App.DispatcherUnhandledException 弹窗；已装配的部分尽量保留。
        }

        // ========= 自绘窗口控制按钮视觉 =========
        StyleFlatTitleButton(BtnMin);
        StyleFlatTitleButton(BtnMax);
    }

    private static void TryPlugin(IContext context, IPlugin plugin, Action<string> log)
    {
        try { context.Plugin(plugin); }
        catch (Exception ex) { log($"插件 {plugin.Name} 装配失败：{ex.Message}"); }
    }

    // ---------- 窗口控制 ----------
    private void StyleFlatTitleButton(Button b)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3E))));
        style.Triggers.Add(hover);
        b.Style = style;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (WindowState == WindowState.Maximized)
        {
            // 最大化为了避免 WindowChrome CornerRadius 被裁切，把外层 Chrome 的 CornerRadius 置 0
            Chrome.CornerRadius = new CornerRadius(0);
        }
        else
        {
            Chrome.CornerRadius = new CornerRadius(10);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>兜底：IVibrancyService 缺失时的空实现。</summary>
file sealed class NullVibrancyPlay : IVibrancyService
{
    public void Apply(IntPtr hWnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }
    public void Disable(IntPtr hWnd) { }
}
