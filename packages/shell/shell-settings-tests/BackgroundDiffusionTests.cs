using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Settings.Sections;
using BetterDesktop.Shell.Settings.Services;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable CA2000 // SettingsService 自带 ProcessExit 兜底 flush，测试内局部实例无需显式 Dispose
namespace BetterDesktop.Shell.Settings.Tests;

/// <summary>
/// 验证"插件化系统扩散"：用户上传皮肤 / 切换预设 / 调主题色，所有 ShellWindow 子类窗口的背景
/// 立即跟随同一 Brush 引用（ImageBrush 共享实例 / 色调托盘共享实例）。
///
/// 这是 2026-08-23 用户原话"这对设置的影响范围扩大出去，不能只在位置(窗口根背景)内生效"
/// 的核心落地测试：基类 ApplyAppearance 无条件同步 Background = AppearanceService.BackgroundBrush。
/// </summary>
public class BackgroundDiffusionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Application _app;
    private static readonly object _lock = new();

    public BackgroundDiffusionTests(ITestOutputHelper output)
    {
        _output = output;
        // 测试隔离：SettingsService 落盘 %APPDATA%\BetterDesktop\settings.json，多测试共享一个进程会相互污染。
        CleanSettings();
        // 最小 Application 宿主（推资源需要 Application.Current 非空）。
        lock (_lock)
        {
            if (Application.Current is null)
            {
                _app = new Application();
            }
            else
            {
                _app = Application.Current;
            }
        }
    }

    public void Dispose() => CleanSettings();

    private static void CleanSettings()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BetterDesktop");
            var settingsFile = System.IO.Path.Combine(dir, "settings.json");
            if (System.IO.File.Exists(settingsFile))
            {
                System.IO.File.Delete(settingsFile);
            }
            var skinsDir = System.IO.Path.Combine(dir, "skins");
            if (System.IO.Directory.Exists(skinsDir))
            {
                System.IO.Directory.Delete(skinsDir, recursive: true);
            }
        }
        catch
        {
            // 测试环境清理失败忽略
        }
    }

    /// <summary>纯代码 ShellWindow 子类（无 XAML）—— 模拟 Dock/应用提取器/NewAppsNotification 这类
    /// "纯代码构造 + ChromeBorder 手动接入" 的窗口，证明 Background 同步对所有 ShellWindow 路径都生效。</summary>
    private sealed class CodeOnlyWindow : ShellWindow
    {
        public CodeOnlyWindow(IAppearanceService? appearance, IEventBus? events = null)
        {
            AppearanceService = appearance;
            Events = events;
            Width = 200;
            Height = 100;
            var root = new Border { Background = Brushes.Transparent, Padding = new Thickness(8) };
            root.Child = new TextBlock { Text = "code-only window" };
            Content = root;
            ChromeBorder = root;
        }
    }

    [Fact]
    public void Preset_Graphite_AllShellWindows_ShareSameBackgroundBrush()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                var settings = new SettingsService();
                var appearance = new AppearanceService(settings);
                appearance.Initialize();
                appearance.SkinActive = "preset:graphite"; // 配色预设（无图）→ 色调托盘

                var registry = new SettingsSectionRegistry();
                registry.Register(new SystemSection());
                registry.Register(new ThemeSection());
                var xamlWin = new SettingsWindow(registry, settings, appearance);
                var codeWin = new CodeOnlyWindow(appearance);

                // Show + 手动 raise LoadedEvent：基类 OnShellWindowLoaded → ApplyAppearance → Background 同步。
                // 不能用 DispatcherPriority.ContextIdle 等 Loaded 触发（其优先级高于 Loaded，不会自然派发）。
                xamlWin.Show();
                codeWin.Show();
                xamlWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, xamlWin));
                codeWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, codeWin));

                _output.WriteLine($"[diffuse-preset] appearance.BackgroundBrush={appearance.BackgroundBrush.GetType().Name}");
                _output.WriteLine($"[diffuse-preset] xamlWin.Background={xamlWin.Background.GetType().Name}");
                _output.WriteLine($"[diffuse-preset] codeWin.Background={codeWin.Background.GetType().Name}");

                // 核心断言：基类统一从 AppearanceService 同步 Background，所有窗口 Background 都引用同一 Brush。
                Assert.Same(appearance.BackgroundBrush, xamlWin.Background);
                Assert.Same(appearance.BackgroundBrush, codeWin.Background);
                // 两窗口之间也共享同一个 Brush 实例。
                Assert.Same(xamlWin.Background, codeWin.Background);

                xamlWin.Close();
                codeWin.Close();
            }
            catch (Exception ex) { captured = ex; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void NoSkin_Default_AllShellWindows_ShareSameBackgroundBrush()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                var settings = new SettingsService();
                var appearance = new AppearanceService(settings);
                appearance.Initialize();
                // 不激活任何皮肤 → BackgroundBrush 返回默认色调托盘。

                var registry = new SettingsSectionRegistry();
                registry.Register(new SystemSection());
                registry.Register(new ThemeSection());
                var xamlWin = new SettingsWindow(registry, settings, appearance);
                var codeWin = new CodeOnlyWindow(appearance);

                xamlWin.Show();
                codeWin.Show();
                xamlWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, xamlWin));
                codeWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, codeWin));

                _output.WriteLine($"[diffuse-noskin] brush={appearance.BackgroundBrush.GetType().Name}");
                Assert.Same(appearance.BackgroundBrush, xamlWin.Background);
                Assert.Same(appearance.BackgroundBrush, codeWin.Background);

                xamlWin.Close();
                codeWin.Close();
            }
            catch (Exception ex) { captured = ex; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void Accent_And_Tint_PropagateToAppResources_ForPluginCustomRendering()
    {
        // 插件化系统扩散的另一面：插件 XAML / 代码可以用 DynamicResource AccentBrush 取当前主色，
        // 与基类窗口 Background 同步。这是 "修改皮肤颜色也对所有窗口生效" 的连带落地。
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                var settings = new SettingsService();
                var appearance = new AppearanceService(settings);
                appearance.Initialize();
                appearance.Accent = Color.FromRgb(0x10, 0x84, 0xFF);
                appearance.WindowTint = Color.FromRgb(0x1F, 0x1F, 0x22);

                // SyncAppResources 应已把 AccentBrush/WindowTintColor 等同步键推到 App.Resources + GlobalDictionary。
                var app = Application.Current;
                var accentBrush = app.Resources["AccentBrush"] as SolidColorBrush;
                var accentColor = app.Resources["AccentColor"];
                var windowTint = app.Resources["WindowTintColor"];
                Assert.NotNull(accentBrush);
                Assert.Equal(Color.FromRgb(0x10, 0x84, 0xFF), accentBrush!.Color);
                Assert.Equal(Color.FromRgb(0x10, 0x84, 0xFF), (Color)accentColor!);
                Assert.Equal(Color.FromRgb(0x1F, 0x1F, 0x22), (Color)windowTint!);

                // Global 字典（插件宿主共享）也应同步。
                var global = BetterDesktop.Shell.Core.Surface.ThemeResourceProvider.Ensure();
                Assert.NotNull(global["AccentBrush"]);
                Assert.NotNull(global["WindowTintColor"]);

                _output.WriteLine($"[diffuse-accent] AccentBrush={accentBrush.Color}, WindowTint={(Color)windowTint}");
            }
            catch (Exception ex) { captured = ex; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void WindowTint_Changed_PropagatesToAllShellWindows()
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                var context = new CordisContext();
                var settings = new SettingsService(context);
                var appearance = new AppearanceService(settings, context);
                appearance.Initialize();

                var registry = new SettingsSectionRegistry();
                registry.Register(new SystemSection());
                registry.Register(new ThemeSection());
                var xamlWin = new SettingsWindow(registry, settings, appearance, events: context.Events);
                var codeWin = new CodeOnlyWindow(appearance, context.Events);

                xamlWin.Show();
                codeWin.Show();
                xamlWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, xamlWin));
                codeWin.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, codeWin));

                // 基线 Brush（无皮肤时色调托盘）。
                var brushBefore = appearance.BackgroundBrush;
                Assert.Same(brushBefore, xamlWin.Background);
                Assert.Same(brushBefore, codeWin.Background);

                // 改 WindowTint —— 通过 AppearanceService 触发 Changed 事件（任何 flags），
                // 基类 ApplyAppearance 无条件重新同步 Background，所有窗口同步更新。
                appearance.WindowTint = Color.FromRgb(0x33, 0x66, 0x99);

                var brushAfter = appearance.BackgroundBrush;
                _output.WriteLine($"[diffuse-tint] before={brushBefore.GetType().Name} after={brushAfter.GetType().Name}");
                Assert.Same(brushAfter, xamlWin.Background);
                Assert.Same(brushAfter, codeWin.Background);
                Assert.NotSame(brushBefore, brushAfter); // 颜色变了 Brush 实例必然变化

                xamlWin.Close();
                codeWin.Close();
            }
            catch (Exception ex) { captured = ex; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }
}
