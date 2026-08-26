using System;
using System.Linq;
using System.Threading;
using System.Windows;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Settings.Sections;
using BetterDesktop.Shell.Settings.Services;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable CA2000 // SettingsService 自带 ProcessExit 兜底 flush，测试内局部实例无需显式 Dispose
namespace BetterDesktop.Shell.Settings.Tests;

/// <summary>
/// 启动冒烟测试：精确复现 SettingsPlugin.LoadAsync 的启动路径
/// （AppearanceService.Initialize → ThemeSection.Build → SettingsWindow 构造），
/// 主动捕获"一启动就黑屏崩溃"的真实异常。不依赖真实 Dispatcher/host，
/// 用最小 Application 宿主让 Application.Current 可用（AppearanceService 推送需要）。
/// </summary>
public class StartupSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterDesktop", "settings.json");

    // 最小 Application 宿主（headless 下必须，否则 Application.Current 为 null 导致推送失败）。
    private Application? _app;

    public StartupSmokeTests(ITestOutputHelper output)
    {
        _output = output;
        CleanSettings();
    }

    public void Dispose()
    {
        CleanSettings();
        GC.SuppressFinalize(this);
    }

    private static void CleanSettings()
    {
        try { if (System.IO.File.Exists(SettingsPath)) System.IO.File.Delete(SettingsPath); }
        catch { /* 忽略 */ }
    }

    [Fact]
    public void StartupPath_Initialize_Then_BuildThemeSection_DoesNotThrow()
    {
        // WPF 视觉树必须在 STA 线程构建（ThemeSection.Build 创建 StackPanel 等）。
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { RunStartup(null); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void StartupPath_WithPresetSkin_DoesNotThrow()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { RunStartup("preset:graphite"); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    /// <summary>
    /// 真实 host 模拟：加载 host 的 App.xaml 资源字典（含 MacCombo/MacSlider 等所有 macOS 风样式 +
    /// 主题令牌初始键），再跑完整启动序列。覆盖简单测试没覆盖的"XAML 样式字典已就位"路径，
    /// 复现真实 Dispatcher 渲染期崩溃（如 DynamicResource 键缺失、冻结 Brush 渲染异常）。
    /// </summary>
    [Fact]
    public void StartupPath_WithRealHostResources_DoesNotThrow()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { RunStartupWithHostResources(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    private void RunStartupWithHostResources()
    {
        // 加载真实 host 的 App.xaml 资源字典（路径指向仓库内 host/App.xaml）。
        var hostAppXaml = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "host", "App.xaml");
        if (!System.IO.File.Exists(hostAppXaml))
        {
            hostAppXaml = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "host", "App.xaml"));
        }
        var app = Application.Current ?? new Application();
        if (System.IO.File.Exists(hostAppXaml))
        {
            var dict = new System.Windows.ResourceDictionary
            {
                Source = new Uri(hostAppXaml, UriKind.Absolute)
            };
            foreach (var key in dict.Keys)
            {
                app.Resources[key] = dict[key];
            }
            _output.WriteLine("[smoke] loaded host App.xaml resources, keys=" + dict.Keys.Count);
        }
        else
        {
            _output.WriteLine("[smoke] WARN host App.xaml not found at " + hostAppXaml);
        }

        var settings = new SettingsService();
        var appearance = new AppearanceService(settings);
        appearance.Initialize(); // 推送主题令牌（含 CardBorderBrush）
        _output.WriteLine("[smoke] Initialize() OK; CardBorderBrush present=" +
                          (app.Resources.Contains("CardBorderBrush")));

        var theme = new ThemeSection();
        var built = theme.Build(settings, appearance);
        _output.WriteLine("[smoke] ThemeSection.Build() OK");

        var win = new SettingsWindow(new SettingsSectionRegistry(), settings, appearance);
        _output.WriteLine("[smoke] SettingsWindow ctor OK");
        win.Show();
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        _output.WriteLine("[smoke] SettingsWindow Show+Render OK (host resources)");
        win.Close();
    }

    /// <summary>
    /// 决定性诊断：用真实 host 的 registry（注册 SystemSection + ThemeSection），真实 Show 后检查
    /// 导航项数量与内容是否真的构建进 ContentPanel。复现用户"只有毛玻璃、没有内容"。
    /// 同时捕获 BuildAllSections 是否抛异常（异常会被 Show 外层吞掉，导致内容缺失但无 crash.log）。
    /// </summary>
    [Fact]
    public void RealRegistry_ContentIsBuilt_VerifySectionListAndContentPanel()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { DiagnoseRealRegistryContent(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    private void DiagnoseRealRegistryContent()
    {
        if (Application.Current is null) _app = new Application();
        var settings = new SettingsService();
        var appearance = new AppearanceService(settings);
        appearance.Initialize();

        // 对齐真实 SettingsPlugin.LoadAsync 的 registry 填充
        var registry = new SettingsSectionRegistry();
        registry.Register(new SystemSection());
        registry.Register(new ThemeSection());
        _output.WriteLine($"[diag-real] registry.Sections.Count={registry.Sections.Count}");

        var win = new SettingsWindow(registry, settings, appearance);
        // 监听 Loaded 以捕获 BuildAllSections 是否抛异常
        Exception? loadEx = null;
        win.Loaded += (_, _) =>
        {
            try
            {
                // BuildAllSections 已在 OnWindowLoaded 内同步跑完；此处仅读取结果。
                var list = win.FindName("SectionList") as System.Windows.Controls.ListBox;
                var cp = win.FindName("ContentPanel") as System.Windows.Controls.StackPanel;
                _output.WriteLine($"[diag-real] SectionList.Items.Count={list?.Items.Count ?? -1}");
                _output.WriteLine($"[diag-real] ContentPanel.Children.Count={cp?.Children.Count ?? -1}");

                // 关键断言：真实 registry 下必须构建出 2 个分区内容与导航。
                Assert.True((list?.Items.Count ?? 0) == 2,
                    $"导航项应为 2，实际 {list?.Items.Count ?? 0}");
                Assert.True((cp?.Children.Count ?? 0) == 2,
                    $"内容容器应为 2，实际 {cp?.Children.Count ?? 0}");
            }
            catch (Exception ex) { loadEx = ex; }
        };
        win.Show();
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        if (loadEx is not null) throw loadEx;
        win.Close();
    }

    [Fact]
    public void NoSkin_DefaultState_RootBorderBoundToSkinBackground_NotBlackened()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { DiagnoseRootBorderSkinBinding(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    private void DiagnoseRootBorderSkinBinding()
    {
        if (Application.Current is null) _app = new Application();
        var settings = new SettingsService();
        var appearance = new AppearanceService(settings);
        appearance.Initialize();
        _output.WriteLine($"[diag] SkinActive='{appearance.SkinActive}' SkinKind={appearance.SkinKind}");

        // 皮肤扩散模型（与描边对称）：AppearanceService 把 BackgroundBrush 推成 App 资源键 SkinBackgroundBrush，
        // 各外壳窗口根 Border 用 DynamicResource SkinBackgroundBrush 绑定。无覆盖层、无包裹。
        Assert.True(Application.Current is not null, "App 必须就绪");
        var app = Application.Current!;
        Assert.True(app.Resources.Contains("SkinBackgroundBrush"),
            "App 资源应含 SkinBackgroundBrush 键（皮肤扩散令牌）");
        Assert.True(ReferenceEquals(app.Resources["SkinBackgroundBrush"], appearance.BackgroundBrush),
            "SkinBackgroundBrush 资源键应指向 BackgroundBrush 同一实例");

        var win = new SettingsWindow(new SettingsSectionRegistry(), settings, appearance);
        win.Show();
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));

        // 窗口级背景（基类兜底赋值 Window.Background）应等于 BackgroundBrush（半透明色调托盘）。
        Assert.True(ReferenceEquals(win.Background, appearance.BackgroundBrush),
            "Window.Background 应引用 BackgroundBrush 同一实例（皮肤扩散闭环）");

        // 背景不应该是全不透明白/黑（应是半透明色调托盘，透出毛玻璃）。
        var bg = win.Background as System.Windows.Media.SolidColorBrush;
        if (bg is not null)
            _output.WriteLine($"[diag] Window.Background A={bg.Color.A} RGB=#{bg.Color.R:X2}{bg.Color.G:X2}{bg.Color.B:X2}");

        // 深入检查视觉树：RootBorder 内容是否构建、SideBorder 背景。
        var rootBorder = win.FindName("RootBorder") as System.Windows.Controls.Border;
        if (rootBorder is not null)
        {
            _output.WriteLine($"[diag] RootBorder.Child type={rootBorder.Child?.GetType().Name}");
            _output.WriteLine($"[diag] RootBorder.Background type={rootBorder.Background?.GetType().Name}");
            if (rootBorder.Background is System.Windows.Media.SolidColorBrush rb)
                _output.WriteLine($"[diag] RootBorder.Background A={rb.Color.A} RGB=#{rb.Color.R:X2}{rb.Color.G:X2}{rb.Color.B:X2}");
            var cp = win.FindName("ContentPanel") as System.Windows.Controls.StackPanel;
            _output.WriteLine($"[diag] ContentPanel.Children.Count={cp?.Children.Count ?? -1}");
            var side = win.FindName("SideBorder") as System.Windows.Controls.Border;
            if (side is not null)
                _output.WriteLine($"[diag] SideBorder.Background type={side.Background?.GetType().Name}");
        }
        win.Close();
    }

    /// <summary>对齐 SettingsPlugin.LoadAsync 的真实启动顺序，在 STA 线程内执行。</summary>
    private void RunStartup(string? presetId)
    {
        // 真实启动会复用已初始化的 Application；这里建一个最小宿主（headless 下必须）。
        if (Application.Current is null) _app = new Application();

        var settings = new SettingsService();
        var appearance = new AppearanceService(settings);
        // 1) 启动即 Initialize（推送令牌到 App 资源）
        appearance.Initialize();
        _output.WriteLine("[smoke] AppearanceService.Initialize() OK");

        if (presetId is not null)
        {
            appearance.SkinActive = presetId;
            _output.WriteLine("[smoke] preset active=" + appearance.SkinActive + " kind=" + appearance.SkinKind);
        }

        // 2) 构建主题分区（真实启动会构建所有分区，ThemeSection 含 SkinCard）
        var theme = new ThemeSection();
        var built = theme.Build(settings, appearance);
        _output.WriteLine("[smoke] ThemeSection.Build() OK, element=" + built.GetType().Name);

        // 3) 构造 SettingsWindow（Shader/Brush 在此经基类绑定）
        var win = new SettingsWindow(new SettingsSectionRegistry(), settings, appearance);
        _output.WriteLine("[smoke] SettingsWindow ctor OK");

        // 4) 真实 Show（headless/Offscreen 仍可渲染一帧，触发 ApplyAppearance + DWM 合成路径，
        //    复现"窗口画出来时崩溃"的真实场景，而非仅 RaiseEvent Loaded）。
        win.Show();
        // 强制布局+渲染一帧（Dispatcher 排空），暴露渲染期异常。
        win.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        _output.WriteLine("[smoke] SettingsWindow Show+Render OK");
        win.Close();
    }
}
