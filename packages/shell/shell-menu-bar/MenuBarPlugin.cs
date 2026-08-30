// BetterDesktop.Shell.MenuBar — 插件入口（IPlugin）
// 角色：`shell.menu-bar` — 顶部菜单栏（左区导航入口 + 右区状态条扩展）。
// 左区 = MenuBarLeftZone：程序菜单（复用 IStartMenuService）+ 位置/下载/文档。
// 右区 = StatusBarMenuBarExtension（移植自 tools/ShellComponentsPlayground 的紧凑状态条）：
//   系统托盘/FPS/CPU/内存/WiFi/实时网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面，
//   各图标左键/右键打开对应独立面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Sections;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar;

/// <summary>菜单栏插件：注册扩展点 + 构造 MenuBarWindow 并显示。</summary>
public sealed class MenuBarPlugin : IPlugin
{
    public string Name => "shell.menu-bar";
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private MenuBarWindow? _window;
    private StatusBarMenuBarExtension? _statusBar;
    private IAppearanceService? _appearance;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // DI：从内核注入的上下文 Get 采集服务（全部可能为 null，插件按 M10 降级）
        var ime = context.Get<IImeMonitor>();
        var net = context.Get<INetworkMonitor>();
        var vol = context.Get<IVolumeMonitor>();
        var mic = context.Get<IMicrophoneMonitor>();
        var bat = context.Get<IBatteryMonitor>();
        var brightness = context.Get<IBrightnessMonitor>();
        var mem = context.Get<IMemoryMonitor>();
        var cpu = context.Get<ICpuMonitor>();
        var vibrancy = context.Get<IVibrancyService>();
        var appearance = context.Get<IAppearanceService>();
        var settings = context.Get<ISettingsService>();
        // 左区程序菜单复用开始菜单插件；未注册时为 null，MenuBarLeftZone 会自动不呈现该入口。
        var startMenu = context.Get<IStartMenuService>();

        // Vibrancy 必要：窗口需毛玻璃；降级为 NullVibrancy 保证不抛
        vibrancy ??= new NullVibrancy();

        // 主题接线：菜单栏自绘图标此前 55 处硬编码 Brushes.White，切亮色主题会白字白底不可读。
        // MenuBarTheme 持有单一共享画刷，这里只需把外观服务的当前前景色同步进去并订阅变更，
        // 全部图标/文字即随主题实时换色（SyncFrom 内部已做 UI 线程兜底）。
        if (appearance is not null)
        {
            _appearance = appearance;
            MenuBarTheme.SyncFrom(appearance);
            appearance.Changed += OnAppearanceChanged;
        }

        // 右区 = 紧凑状态条（系统托盘/FPS/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
        _statusBar = new StatusBarMenuBarExtension(vol, mic, bat, ime, brightness, net, mem, cpu, vibrancy, appearance, settings);
        var extensions = new List<Contracts.IMenuBarExtension>(capacity: 1)
        {
            _statusBar
        };

        // 构造并显示菜单栏主窗口
        _window = new MenuBarWindow(extensions, vibrancy, appearance, context.Logger, startMenu);
        _window.Show();

        // 设置 → 菜单栏：系统功能（系统托盘/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
        // 的显隐统一由设置分区管理；「+」扩展中心只管外部扩展功能插件。
        // 这里把「切换即时生效」的回调接到运行状态条上（注册表由 SettingsPlugin 在 6.0 节提前 Provide）。
        context.Get<ISettingsSectionRegistry>()?.Register(
            new MenuBarSection(
                (id, on) => _statusBar?.SetComponentVisible(id, on),
                hide => _statusBar?.SetTrayHideSystemIcons(hide)));

        context.Logger.Info($"{Name} 已加载：已显示菜单栏主窗口，左区程序菜单{(startMenu is null ? "（服务缺失，已降级隐藏）" : string.Empty)}，右区状态条（{extensions.Count} 个扩展）就绪");
        return Task.CompletedTask;
    }

    private void OnAppearanceChanged(object? sender, BetterDesktop.Shell.Core.Surface.AppearanceChangedArgs e)
    {
        MenuBarTheme.SyncFrom(_appearance);
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
            _appearance = null;
        }
        _window?.Close();
        _window = null;
        _statusBar?.Dispose();
        _statusBar = null;
        return Task.CompletedTask;
    }
}

/// <summary>兜底：IVibrancyService 不存在时，降级为空壳（窗口不变毛玻璃，但不崩溃）。</summary>
internal sealed class NullVibrancy : IVibrancyService
{
    public void Apply(IntPtr hwnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }
    public void Disable(IntPtr hwnd) { }
}
