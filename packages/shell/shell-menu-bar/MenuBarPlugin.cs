// BetterDesktop.Shell.MenuBar — 插件入口（IPlugin）
// 角色：`shell.menu-bar` — 顶部菜单栏（左区程序菜单占位 + 右区状态条扩展）。
// 右区 = StatusBarMenuBarExtension（移植自 tools/ShellComponentsPlayground 的紧凑状态条）：
//   系统托盘/FPS/CPU/内存/WiFi/实时网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面，
//   各图标左键/右键打开对应独立面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar;

/// <summary>菜单栏插件：注册扩展点 + 构造 MenuBarWindow 并显示。</summary>
public sealed class MenuBarPlugin : IPlugin
{
    public string Name => "shell.menu-bar";
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private MenuBarWindow? _window;
    private StatusBarMenuBarExtension? _statusBar;

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

        // Vibrancy 必要：窗口需毛玻璃；降级为 NullVibrancy 保证不抛
        vibrancy ??= new NullVibrancy();

        // 右区 = 紧凑状态条（系统托盘/FPS/CPU/内存/WiFi/网速/亮度/输入法/蓝牙/音量/麦克风/电池/通知/时间/桌面）
        _statusBar = new StatusBarMenuBarExtension(vol, mic, bat, ime, brightness, net, mem, cpu, vibrancy, appearance, settings);
        var extensions = new List<Contracts.IMenuBarExtension>(capacity: 1)
        {
            _statusBar
        };

        // 构造并显示菜单栏主窗口
        _window = new MenuBarWindow(extensions, vibrancy, appearance, context.Logger);
        _window.Show();

        context.Logger.Info($"{Name} 已加载：已显示菜单栏主窗口，右区状态条（{extensions.Count} 个扩展）就绪");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
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
