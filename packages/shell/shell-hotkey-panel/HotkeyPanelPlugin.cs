using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.HotkeyPanel.Sections;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 热键侧板插件：宿主内常驻透明窗 + 设置中心"热键"分节（热键计划 P5 §10 界面分工）。
/// <para>依赖 <c>hotkeys</c> 插件（<see cref="IHotkeyRegistryService"/>）；缺失时降级不创建窗口（M10），
/// 不阻塞宿主启动。声明迁移（既有热键进注册表）在窗口创建后一次性执行。</para>
/// <para>界面分工（用户裁定"侧板改键反人类"）：侧板只做**显示与隐藏管理**；
/// 改键在**正常窗口**（设置中心"热键"分节 <see cref="HotkeySettingsSection"/>）。</para>
/// <para>
/// 【2026-09-17 用户需求"增加侧板的启动和关闭"】侧板原先只会随宿主常驻、没有任何关闭入口。
/// 现在显隐由设置键 <see cref="HotkeyPanelSettings.EnabledKey"/> 唯一表达，三条写入口
/// （侧板右键菜单 / 可操作态底部「关闭侧板」/ 设置中心勾选与全局热键）都只写这个键，
/// **落地动作统一在本插件订阅的 <c>ShellEvents.SettingsChanged</c> 里执行**——一个真相源、一个应用点，
/// 不会出现"菜单关了但设置还显示开"的漂移。
/// </para>
/// </summary>
public sealed class HotkeyPanelPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "hotkeys-panel";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => new[] { typeof(IHotkeyRegistryService) };

    private HotkeyPanelWindow? _window;
    private IHotkeyRegistryService? _registry;
    private ISettingsService? _settings;
    private IContext? _context;
    private IDisposable? _settingsSub;

    // 【2026-09-17 用户拍板「显隐不止隐藏，更是不留后台」】窗口改为**按需创建 / 关闭即销毁**，
    // 故构造依赖要留一份：关掉时 Close()（连带停 500ms + 50ms 两个定时器、Dispose 场景监听器），
    // 重开时用这些依赖重建一个干净窗口。
    private IVibrancyService? _vibrancy;
    private IAppearanceService? _appearance;
    private ISettingsWindowService? _settingsWindow;
    private HotkeyScopeTracker? _tracker;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var registry = context.Get<IHotkeyRegistryService>();
        if (registry is null)
        {
            context.Logger.Warn("[hotkeys-panel] 未找到热键注册表（hotkeys 插件未加载），侧板不创建（M10 降级）");
            return Task.CompletedTask;
        }

        var vibrancy = context.Get<IVibrancyService>() ?? NullVibrancyService.Instance;
        var appearance = context.Get<IAppearanceService>();
        var settings = context.Get<ISettingsService>();
        var settingsWindow = context.Get<ISettingsWindowService>(); // 侧板"打开热键设置"入口；缺失则隐藏（M10）
        context.Logger.Info($"[hotkeys-panel] settingsWindow={settingsWindow is not null}");
        var tracker = new HotkeyScopeTracker(registry);
        SurfaceScopeBridge.Attach(tracker); // 表面 show/hide → 作用域聚合（P5 P0-1）；插件卸载前保持挂载

        // 正常窗口改键：设置中心"热键"分节（用户裁定"侧板改键反人类"，改键移出侧板）
        context.Get<ISettingsSectionRegistry>()?.Register(new HotkeySettingsSection(registry));

        _registry = registry;
        _settings = settings;
        _context = context;

        _vibrancy = vibrancy;
        _appearance = appearance;
        _settingsWindow = settingsWindow;
        _tracker = tracker;

        // 【2026-09-17 起窗口按需创建】此处只在"当前为显示态"时建窗（由 ApplyEnabledState 判定并调用）。
        // 声明（LoadDeclarations）挂在窗口上：窗口销毁则声明暂时消失，重开时重新声明；
        // 而**切换热键是"注册"进来的，不随窗口消失** —— 关掉之后必须还有东西能把它叫回来（回家的路）。

        RegisterToggleHotkey(registry, context);

        // 显隐意图的唯一应用点：右键菜单 / 底部入口 / 热键 / 设置勾选都只写设置键，落地在这里
        _settingsSub = context.Events.On<SettingsChangedEventArgs>(
            ShellEvents.SettingsChanged,
            (e, _) =>
            {
                OnSettingsChanged(e);
                return Task.CompletedTask;
            });

        ApplyEnabledState("启动");

        context.Logger.Info("[hotkeys-panel] 热键侧板已就绪（右侧中部；显隐见设置中心「热键」或右键菜单 / "
            + HotkeyPanelSettings.ToggleHotkeyDefault + "）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;

        // 切换热键是"注册"（非声明）进来的：卸载必须释放键位，否则重启后新宿主注册不上（0x581）。
        _registry?.Unregister(HotkeyPanelSettings.ToggleHotkeyId);
        _registry = null;

        _window?.Close();
        _window = null;
        _settings = null;
        _context = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 注册"显示 / 隐藏热键侧板"全局热键。
    /// <para>为什么必须有：侧板一旦关闭，右键菜单就没了 —— 热键是**关掉之后唯一还能把它叫回来**的入口
    /// （另一个是设置中心勾选框）。它注册进注册表，因此会出现在侧板列表与设置中心，可改键 / 可停用，
    /// 冲突时 fail-closed 并如实提示，不会静默失效。</para>
    /// </summary>
    private void RegisterToggleHotkey(IHotkeyRegistryService registry, IContext context)
    {
        var chord = new HotkeyChord(HotkeyPanelSettings.ToggleHotkeyDefault);
        var binding = new HotkeyBinding(
            HotkeyPanelSettings.ToggleHotkeyId,
            chord,
            HotkeyScope.Global, // 面板关掉时也必须能按（作用域不能绑定到面板自身）
            HotkeyPanelSettings.ToggleHotkeyDescription,
            HotkeySource.SystemHotkey,
            chord,
            "hotkeys-panel");

        var result = registry.Register(binding, _ => ToggleByHotkey());
        if (result.Ok)
        {
            context.Logger.Info($"[hotkeys-panel] 切换热键已注册：{chord.Spec}（{HotkeyPanelSettings.ToggleHotkeyDescription}）");
        }
        else
        {
            // fail-visible 红线：注册失败必须在日志与侧板/设置中心可见（注册表已把冲突登记进去）
            context.Logger.Warn($"[hotkeys-panel] 切换热键 {chord.Spec} 注册失败：{result.Reason}"
                + "（可在设置中心「热键」改键；侧板仍可用右键菜单/设置勾选关闭）");
        }
    }

    /// <summary>热键触发（在注册表泵线程/钩子线程）：只翻设置键，UI 落地统一走 <see cref="OnSettingsChanged"/>。</summary>
    private void ToggleByHotkey()
    {
        var settings = _settings;
        if (settings is null)
        {
            return;
        }

        HotkeyPanelSettings.SetEnabled(settings, !HotkeyPanelSettings.IsEnabled(settings));
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (!string.Equals(e.Key, HotkeyPanelSettings.EnabledKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyEnabledState("设置变更");
    }

    /// <summary>
    /// 把显隐意图落到窗口。
    /// <para>
    /// 【2026-09-17 用户拍板："显隐不止隐藏，更是不留后台进程"】
    ///   开 → 需要则新建窗口 + 显示；
    ///   关 → <c>Close()</c> **销毁窗口**（连带停掉 500ms 数据刷新与 50ms 门控轮询两个定时器、
    ///        Dispose 场景监听器、退出作用域与外部点击钩子），只留下"切换热键"的注册
    ///        —— 那是关掉之后唯一还能把侧板叫回来的入口（另一个是设置中心「热键」勾选框）。
    /// 幂等：重复调用只是再 Show / 对已销毁的窗口再 Close 一次（null 短路）。
    /// </para>
    /// </summary>
    private void ApplyEnabledState(string reason)
    {
        var enabled = HotkeyPanelSettings.IsEnabled(_settings);
        Dispatch(() =>
        {
            if (enabled)
            {
                EnsureWindow();
                _window?.ShowPanel();
            }
            else if (_window is { } window)
            {
                window.Close(); // 触发 OnClosed：停两个定时器 + Dispose 场景监听器（真正的"不留后台"）
                _window = null;
            }
        });

        _context?.Logger.Info($"[hotkeys-panel] 侧板{(enabled ? "已显示" : "已关闭并释放窗口")}（{reason}）");
    }

    /// <summary>按需建窗（已存在则直接返回）。声明（热键条目）与显隐无关，但挂在窗口上 → 随窗口重建。</summary>
    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        if (_registry is null || _tracker is null)
        {
            return; // 未完成装配（M10 降级路径）→ 不建窗
        }

        _window = new HotkeyPanelWindow(
            _vibrancy ?? NullVibrancyService.Instance,
            _appearance,
            _registry,
            _settings,
            _settingsWindow,
            _tracker);
        _window.LoadDeclarations();
    }

    /// <summary>设置变更回调来自事件总线线程；窗口操作必须回 UI 线程（无 Dispatcher 时按已在 UI 线程处理）。</summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }
}
