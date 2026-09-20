using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windowing;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>侧板窗口 smoke：构造 + 声明加载 + 渲染刷新 + 初始穿透态（只读态常驻穿透）。</summary>
public class HotkeyPanelWindowSmokeTests
{
    private readonly ITestOutputHelper _out;

    public HotkeyPanelWindowSmokeTests(ITestOutputHelper output) => _out = output;

    private sealed class FakeRegistry : IHotkeyRegistryService
    {
        private readonly Dictionary<string, HotkeyView> _views = new(StringComparer.Ordinal);

        public void Seed(HotkeyView v) => _views[v.Binding.Id] = v;

        public IReadOnlyList<HotkeyView> GetActive() => _views.Values.ToList();
        public IReadOnlyList<HotkeyView> GetAll() => _views.Values.ToList();
        public IReadOnlyList<HotkeyConflict> GetConflicts() => new List<HotkeyConflict>();

        public RegistrationResult Register(HotkeyBinding binding, Action<HotkeyBinding>? onTrigger = null)
            => Declare(binding);

        public RegistrationResult Declare(HotkeyBinding binding)
        {
            if (_views.ContainsKey(binding.Id))
            {
                return new RegistrationResult(false, null, "已存在");
            }

            _views[binding.Id] = new HotkeyView(binding, true, true, false, false);
            return new RegistrationResult(true, null, null);
        }

        public void Unregister(string id) => _views.Remove(id);
        public RegistrationResult Rebind(string id, HotkeyChord chord) => new(false, null, "not used");
        public void SetEnabled(string id, bool enabled)
        {
        }

        public void SetVisible(string id, bool visible)
        {
        }

        public void ResetToDefault(string id)
        {
        }

        public void SetActiveScopes(IReadOnlyList<string> scopeIds)
        {
        }
    }

    private sealed class MemSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _s = new(StringComparer.Ordinal);
        public T? Get<T>(string key, T? defaultValue = default)
            => _s.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _s[key] = value;
    }

    [Fact]
    public void Window_constructs_declares_and_starts_click_through() => RunOnSta(Run);

    /// <summary>
    /// 【2026-09-17 用户需求"增加侧板的启动和关闭"】隐藏 / 重新显示必须成对可用：
    /// 隐藏后 IsVisible=false，重新显示后 IsVisible=true 且回到只读穿透态。
    /// </summary>
    [Fact]
    public void Window_hide_and_show_toggles_visibility() => RunOnSta(() =>
    {
        EnsureApplication();
        var (win, _) = CreateWindow();

        Assert.True(win.IsVisible);

        win.HidePanel();
        Assert.False(win.IsVisible);

        win.ShowPanel();
        Assert.True(win.IsVisible);

        // 重开后必须回到只读穿透态（否则会带着"不穿透"复活，静默吃掉右侧区域的点击）
        var handle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        Assert.True(ClickThroughWindow.IsClickThrough(handle));

        win.Close();
    });

    /// <summary>
    /// 侧板右键菜单必须挂着「显示热键侧板」开关项（= 用户要的"启动 / 关闭这个面板"的入口本体），
    /// 且右侧显示当前切换热键 —— 关掉之后才知道怎么再打开。
    /// </summary>
    [Fact]
    public void Window_context_menu_has_panel_toggle_item() => RunOnSta(() =>
    {
        EnsureApplication();
        var (win, _) = CreateWindow();

        var chrome = Assert.IsType<Border>(win.Content);
        var root = Assert.IsType<Border>(chrome.Child);
        var menu = Assert.IsType<ContextMenu>(root.ContextMenu);

        var toggle = Assert.Single(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "显示热键侧板"));
        Assert.True(toggle.IsCheckable);
        Assert.True(toggle.IsChecked);
        Assert.False(string.IsNullOrWhiteSpace(toggle.InputGestureText));

        win.Close();
    });

    /// <summary>
    /// 同进程内只允许一个 Application，且必须建在**承载窗口的那条 STA 线程**上
    /// （真实宿主同理：UI 线程唯一）。创建动作收口在 <see cref="EnsureStaHost"/>，
    /// 这里只记录"已建过"，避免依赖 <c>Application.Current</c> 的跨线程可见性。
    /// </summary>
    private static Application? _app;

    private static void EnsureApplication()
    {
        if (_app is null && Application.Current is null)
        {
            _app = new Application();
        }
    }

    private (HotkeyPanelWindow Window, MemSettings Settings) CreateWindow()
    {
        var registry = new FakeRegistry();
        var settings = new MemSettings();
        var tracker = new HotkeyScopeTracker(registry);
        var win = new HotkeyPanelWindow(
            NullVibrancyService.Instance, appearance: null, registry, settings, settingsWindow: null, tracker);
        win.ShowAt(new Point(0, 0));
        win.LoadDeclarations();
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        return (win, settings);
    }

    // ── 统一的 STA 测试宿主 ──
    //
    // 【为什么不是"每个用例开一个 STA 线程"】同进程只允许一个 `Application`（WPF 硬约束），而
    // `Application.Current` 属于**第一个**创建它的线程的 Dispatcher。若每个用例各起一个线程，
    // 第二个用例会拿到"活在已退出线程上的 Application"：窗口资源查找 / SetResourceReference 全部
    // 踩到死 Dispatcher（实测症状：Show() 之后句柄仍为 0、点击穿透断言失败）。
    // 改为一条常驻 STA 线程 + 一个 Application + Dispatcher.Run()，所有窗口用例经它串行执行，
    // 与真实宿主"UI 线程唯一"的形态一致。
    private static readonly object StaGate = new();
    private static Dispatcher? _staDispatcher;

    private static void RunOnSta(Action body)
    {
        lock (StaGate)
        {
            EnsureStaHost();
            Exception? captured = null;
            _staDispatcher!.Invoke(new Action(() =>
            {
                try { body(); }
                catch (Exception ex) { captured = ex; }
            }), System.Windows.Threading.DispatcherPriority.Normal);

            if (captured is not null)
            {
                throw captured;
            }
        }
    }

    private static void EnsureStaHost()
    {
        if (_staDispatcher is not null)
        {
            return;
        }

        using var ready = new ManualResetEventSlim();
        Dispatcher? created = null;
        var t = new Thread(() =>
        {
            _app = Application.Current ?? new Application(); // 唯一创建点（持锁 + 单线程）
            // 【必须显式关闭】默认 ShutdownMode=OnLastWindowClose：第一个用例 win.Close() 会触发
            // Application 关停（Dispatcher 开始关闭），后续用例再建窗口就拿不到 HWND（实测句柄恒为 0）。
            _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            created = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "hotkey-panel-tests-sta",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();

        _staDispatcher = created;
    }

    private void Run()
    {
        EnsureApplication();

        var registry = new FakeRegistry();
        var settings = new MemSettings();
        var tracker = new HotkeyScopeTracker(registry);
        var win = new HotkeyPanelWindow(
            NullVibrancyService.Instance, appearance: null, registry, settings, settingsWindow: null, tracker);

        win.ShowAt(new Point(0, 0)); // ShowAt = ApplyContent + Show（对齐真实插件路径）
        win.LoadDeclarations();
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        _out.WriteLine($"[smoke] HotkeyPanelWindow Show OK, clickThrough={ClickThroughWindow.IsClickThrough(new System.Windows.Interop.WindowInteropHelper(win).Handle)}");

        // 只读态初始 = 点击穿透（不挡下方应用操作）
        var handle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(ClickThroughWindow.IsClickThrough(handle));

        win.Close();
    }
}
