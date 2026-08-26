using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;

namespace BetterDesktop.Shell.Settings.Sections;

/// <summary>
/// 设置主分区（合并原"通用"与"系统管理"）：桌面环境自身（非单个插件）的设置项。
/// 分组：常规与外观 / 启动与自启 / 性能与资源 / 维护与重置 / 组件管理。
/// 维护类操作（自启注册表、重置删配置、导出、清缓存、开日志）已接真实系统调用；
/// 性能类的"真实策略"已接对应组件（缩略图质量→DwmThumbnail、多显示器布局→DockLayoutService）。
/// 原"系统集成"分组已并入此处（接管任务栏/替换 Shell 为重复且破坏性的伪开关，已移除；
/// 多显示器策略归入"性能与资源"）。
/// </summary>
public sealed class SystemSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "设置";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = "设置",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "管理桌面环境的外观、启动、性能与组件。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18)
        });

        // ---- 常规与外观（原"通用"分区内容，合并至此） ----
        var cardAppearance = GroupCard(tokens);
        CardBody(cardAppearance).Children.Add(TitleBlock("常规与外观", tokens));

        CardBody(cardAppearance).Children.Add(SettingRow("界面语言", BuildLanguage(settings), tokens));
        panel.Children.Add(cardAppearance);

        // ---- 启动与自启 ----
        var cardStartup = GroupCard(tokens);
        CardBody(cardStartup).Children.Add(TitleBlock("启动与自启", tokens));

        var autoStart = WithStyle(new CheckBox { Content = "开机自动启动桌面环境", Margin = new Thickness(0, 10, 0, 0) }, "MacToggle");
        // 初始状态以注册表真实值为准（设置项与系统实际保持一致）
        var autoStartOn = SystemManagement.IsAutoStartEnabled();
        autoStart.IsChecked = autoStartOn;
        autoStart.Checked += (_, _) =>
        {
            settings.Set("system.autostart", true);
            SystemManagement.SetAutoStart(true);
        };
        autoStart.Unchecked += (_, _) =>
        {
            settings.Set("system.autostart", false);
            SystemManagement.SetAutoStart(false);
        };
        CardBody(cardStartup).Children.Add(autoStart);

        var watchdogStart = WithStyle(new CheckBox { Content = "看门狗开机自启（崩溃自动拉起桌面）", Margin = new Thickness(0, 10, 0, 0) }, "MacToggle");
        var watchdogStartOn = SystemManagement.IsWatchdogAutoStartEnabled();
        watchdogStart.IsChecked = watchdogStartOn;
        watchdogStart.Checked += (_, _) =>
        {
            settings.Set("watchdog.autostart", true);
            SystemManagement.SetWatchdogAutoStart(true);
        };
        watchdogStart.Unchecked += (_, _) =>
        {
            settings.Set("watchdog.autostart", false);
            SystemManagement.SetWatchdogAutoStart(false);
        };
        CardBody(cardStartup).Children.Add(watchdogStart);

        var launchMinimized = WithStyle(new CheckBox { Content = "启动时最小化到后台", Margin = new Thickness(0, 10, 0, 0) }, "MacToggle");
        launchMinimized.IsChecked = settings.Get("system.launchMinimized", false);
        launchMinimized.Checked += (_, _) => settings.Set("system.launchMinimized", true);
        launchMinimized.Unchecked += (_, _) => settings.Set("system.launchMinimized", false);
        CardBody(cardStartup).Children.Add(launchMinimized);

        CardBody(cardStartup).Children.Add(SettingRow("启动延迟（秒）", BuildStartupDelay(settings, tokens), tokens));
        panel.Children.Add(cardStartup);

        // ---- 性能与资源 ----
        var cardPerf = GroupCard(tokens);
        CardBody(cardPerf).Children.Add(TitleBlock("性能与资源", tokens));

        var animations = WithStyle(new CheckBox { Content = "启用界面动画", Margin = new Thickness(0, 10, 0, 0) }, "MacToggle");
        animations.IsChecked = settings.Get("system.animations", true);
        animations.Checked += (_, _) => settings.Set("system.animations", true);
        animations.Unchecked += (_, _) => settings.Set("system.animations", false);
        CardBody(cardPerf).Children.Add(animations);

        CardBody(cardPerf).Children.Add(SettingRow("缩略图质量", BuildThumbnailQuality(settings), tokens));
        // 多显示器策略（原"系统集成"分组并入）：DockLayoutService 读取决定 Dock 目标屏。
        CardBody(cardPerf).Children.Add(SettingRow("多显示器策略", BuildMultiMonitorStrategy(settings), tokens));
        panel.Children.Add(cardPerf);

        // ---- 维护与重置 ----
        var cardMaint = GroupCard(tokens);
        CardBody(cardMaint).Children.Add(TitleBlock("维护与重置", tokens));
        CardBody(cardMaint).Children.Add(ActionRow(tokens, "重置所有设置",
            "将清空并恢复默认配置（不会卸载程序）。",
            (_, _) => OnReset(settings), tokens.MutedForeground, tokens.Separator));
        CardBody(cardMaint).Children.Add(ActionRow(tokens, "导出配置",
            "把当前 settings.json 复制到桌面备份。",
            (_, _) => OnExportConfig(), tokens.MutedForeground, tokens.Separator));
        CardBody(cardMaint).Children.Add(ActionRow(tokens, "清除缓存",
            "删除缩略图与临时渲染缓存。",
            (_, _) => OnClearCache(), tokens.MutedForeground, tokens.Separator));
        CardBody(cardMaint).Children.Add(ActionRow(tokens, "打开日志目录",
            "查看 debug / crash 日志以排查问题。",
            (_, _) => OnOpenLogDir(), tokens.MutedForeground, tokens.Separator));
        CardBody(cardMaint).Children.Add(ActionRow(tokens, "紧急恢复桌面",
            "若桌面环境异常（任务栏消失/窗口残留），立即复位为原生 Windows 桌面。",
            (_, _) => OnEmergencyRecovery(), tokens.MutedForeground, tokens.Separator));
        panel.Children.Add(cardMaint);

        // ---- 组件管理 ----
        // 注：开关生效时机分两类——
        //   ● 我方组件（dock/menubar/companion/contextmenu）：禁用则下次启动不加载对应组件（LoadAsync 读取各自键）。
        //   ● Windows 原生部件管理（wintaskbar/wincontextmenu）：本环境不实现原生部件，仅"管理"其显隐。
        //     components.wintaskbar 已真实接入（Bootstrap 启动按设置隐藏/显示 Explorer 原生任务栏，退出时恢复）；
        //     components.wincontextmenu 为同类管理开关，待接入注册表方式（NoViewContextMenu 等）后即时生效。
        var cardComponents = GroupCard(tokens);
        CardBody(cardComponents).Children.Add(TitleBlock("组件管理", tokens));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.dock", "Dock 栏（已生效）", true));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.menubar", "菜单栏（下次启动生效）", true));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.companion", "扩展中心（下次启动生效）", true));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.wintaskbar", "Windows 原生任务栏（管理：隐藏/显示，已生效）", true));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.contextmenu", "右键菜单（下次启动生效）", true));
        CardBody(cardComponents).Children.Add(ComponentToggle(settings, "components.wincontextmenu", "Windows 原生右键菜单（管理类，待接入）", true));

        // 看门狗状态 + HMR 熔断概览（只读信息行，实时反映 C3 常驻与热重载健康度）
        var watchdogRunning = SystemManagement.IsWatchdogRunning();
        var watchdogAuto = SystemManagement.IsWatchdogAutoStartEnabled();
        var watchdogStatus = new TextBlock
        {
            Text = $"看门狗：{(watchdogRunning ? "运行中" : "未运行")} · 开机自启{(watchdogAuto ? "已开启" : "未开启")}",
            Foreground = tokens.MutedForeground,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0)
        };
        CardBody(cardComponents).Children.Add(watchdogStatus);

        if (SettingsKernelBridge.TryGetHmrManager(out var hmr))
        {
            var infos = hmr.GetPluginRuntimeInfos();
            var quarantined = infos.Count(i => i.Quarantined);
            var hmrStatus = new TextBlock
            {
                Text = $"热重载保护：监控 {infos.Count} 个插件 · 已熔断隔离 {quarantined} 个",
                Foreground = tokens.MutedForeground,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            };
            CardBody(cardComponents).Children.Add(hmrStatus);
        }
        panel.Children.Add(cardComponents);

        return panel;
    }

    /// <summary>
    /// 分组卡片：圆角 + 主题驱动描边 + 浅色背景 + 主题驱动阴影 + 内边距，营造现代设置面板的分组块。
    /// 描边强度（BorderStrength）与阴影档位（ShadowSize）均由 <see cref="IAppearanceService"/> 实时派生，
    /// 可在主题设置页切换并即时生效；订阅 Changed 后在 Border/Shadow 变更时刷新自身，
    /// 并在 Unloaded（窗口关闭）时退订，避免单例外观服务持有卡片造成内存泄漏。
    /// 返回 Border（圆角容器），其 Child 为内容 StackPanel，用 <see cref="CardBody"/> 取内层。
    /// </summary>
    private static Border GroupCard(IThemeTokens tokens)
    {
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        // 暂停描边尝试：分组卡片不再画任何描边线（含内层 inner 亮线），仅保留圆角透明容器，
        // 与"暂时去掉所有描边和阴影"的总要求一致；待视觉方案重做后再恢复。
        var inner = new Border
        {
            CornerRadius = new CornerRadius(9),
            Child = body
        };
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 16),
            CornerRadius = new CornerRadius(10),
            // 透明：透出统一窗口基类的毛玻璃托盘，不叠加第二层 ContentBackground 色块，也不画描边。
            Background = Brushes.Transparent,
            Child = inner
        };

        // 描边/阴影档位只作用于大窗口根 ChromeBorder，分组卡片不参与；无需订阅 AppearanceService。
        return card;
    }

    /// <summary>取分组卡片内层内容面板（最内层 StackPanel）。</summary>
    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static CheckBox ComponentToggle(ISettingsService settings, string key, string label, bool defaultOn)
    {
        var box = WithStyle(new CheckBox { Content = label, Margin = new Thickness(0, 8, 0, 0) }, "MacToggle");
        box.IsChecked = settings.Get(key, defaultOn);
        box.Checked += (_, _) => settings.Set(key, true);
        box.Unchecked += (_, _) => settings.Set(key, false);
        return box;
    }

    // ---- 各分组内的复合控件构造 ----

    private static UIElement BuildLanguage(ISettingsService settings)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacCombo");
        // 下拉项强制亮色（双保险，覆盖纯字符串项的偶发黑字）
        combo.Items.Add("简体中文 (zh-CN)");
        combo.Items.Add("English (en-US)");
        combo.SelectedIndex = settings.Get("general.language", "zh-CN") == "zh-CN" ? 0 : 1;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex == 0)
            {
                settings.Set("general.language", "zh-CN");
            }
            else if (combo.SelectedIndex == 1)
            {
                settings.Set("general.language", "en-US");
            }
        };
        return combo;
    }

    private static UIElement BuildStartupDelay(ISettingsService settings, IThemeTokens tokens)
    {
        var box = new TextBox
        {
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = tokens.Foreground,
            Background = tokens.InputBackground,
            BorderBrush = tokens.InputBorder,
            Text = settings.Get("system.startupDelay", 0).ToString()
        };
        box.LostFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, out var v) && v >= 0)
            {
                settings.Set("system.startupDelay", v);
            }
            else
            {
                box.Text = settings.Get("system.startupDelay", 0).ToString();
            }
        };
        if (tokens is IAppearanceService appearance)
        {
            EventHandler<AppearanceChangedArgs>? handler = null;
            handler = (_, e) =>
            {
                if (e.ThemeModeChanged || e.WindowTintChanged || e.ContentOpacityChanged)
                {
                    box.Foreground = tokens.Foreground;
                    box.Background = tokens.InputBackground;
                    box.BorderBrush = tokens.InputBorder;
                }
            };
            appearance.Changed += handler;
            box.Unloaded += (_, _) => { if (handler is not null) appearance.Changed -= handler; };
        }
        // TODO: 接入真实启动延迟逻辑（host 启动计时器读取 system.startupDelay）。
        return box;
    }

    private static UIElement BuildThumbnailQuality(ISettingsService settings)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacCombo");
        combo.Items.Add("低（省资源）");
        combo.Items.Add("中（平衡）");
        combo.Items.Add("高（清晰）");
        var current = settings.Get("system.thumbnailQuality", "medium");
        combo.SelectedIndex = current switch
        {
            "low" => 0,
            "high" => 2,
            _ => 1
        };
        combo.SelectionChanged += (_, _) =>
        {
            var value = combo.SelectedIndex switch
            {
                0 => "low",
                2 => "high",
                _ => "medium"
            };
            settings.Set("system.thumbnailQuality", value);
            // 已接真实策略：DockThumbWindow 构造 DwmThumbnail 时读取本项，
            // low→小尺寸+LowQuality缩放+降透明度，high→大尺寸+HighQuality缩放+满透明度。
        };
        return combo;
    }

    private static UIElement BuildMultiMonitorStrategy(ISettingsService settings)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacCombo");
        combo.Items.Add("仅主显示器");
        combo.Items.Add("所有显示器");
        combo.Items.Add("各显示器独立布局");
        var current = settings.Get("system.multiMonitorStrategy", "primary");
        combo.SelectedIndex = current switch
        {
            "all" => 1,
            "independent" => 2,
            _ => 0
        };
        combo.SelectionChanged += (_, _) =>
        {
            var value = combo.SelectedIndex switch
            {
                1 => "all",
                2 => "independent",
                _ => "primary"
            };
            settings.Set("system.multiMonitorStrategy", value);
            // 已接真实策略：DockLayoutService 读取本项决定 Dock 目标屏
            // （primary=仅主屏，all/independent=所有显示器各一个 Dock 底部居中）；
            // DockWindow/DockThumbWindow 定位均走 layout 服务提供的屏幕边界，不再硬编码主屏。
        };
        return combo;
    }

    // ---- 维护操作（按钮，真实逻辑） ----

    private static void OnReset(ISettingsService settings)
    {
        var result = MessageBox.Show(
            "确定要重置所有设置吗？此操作将清空当前配置并恢复默认（不会卸载程序，重启后生效）。",
            "重置设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SystemManagement.ResetSettings();
        MessageBox.Show(
            "设置已重置。部分选项（如组件开关）将在下次启动时生效。",
            "系统管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OnExportConfig()
    {
        var dest = SystemManagement.ExportConfig();
        if (dest is null)
        {
            MessageBox.Show(
                "没有可导出的配置（settings.json 尚不存在）。",
                "系统管理",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            $"配置已导出到：\n{dest}",
            "系统管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OnClearCache()
    {
        SystemManagement.ClearCache();
        MessageBox.Show(
            "缓存已清除（如存在）。",
            "系统管理",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OnOpenLogDir()
    {
        SystemManagement.OpenLogDirectory();
    }

    /// <summary>
    /// 紧急恢复：启动同目录的 BetterDesktop.Recovery.exe（D 层兜底），
    /// 把桌面复位为原生 Windows 模样（显示任务栏、清场残留进程、写恢复日志）。
    /// 该 exe 独立无依赖，即使本设置窗口已假死也能经系统 ShellExecute 拉起。
    /// </summary>
    private static void OnEmergencyRecovery()
    {
        var hostDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(hostDir))
        {
            return;
        }
        var recoveryExe = System.IO.Path.Combine(hostDir, "BetterDesktop.Recovery.exe");
        if (!File.Exists(recoveryExe))
        {
            MessageBox.Show(
                "未找到紧急恢复程序（BetterDesktop.Recovery.exe）。请确认它与桌面环境主程序位于同一目录。",
                "紧急恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(recoveryExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动紧急恢复失败：{ex.Message}",
                "紧急恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---- 共享 UI helper（Section 自包含，不依赖外部文件） ----

    /// <summary>
    /// 套用宿主 App.xaml 中已定义好的 macOS 风 XAML 样式（MacCombo/MacSlider/MacToggle/MacButton/MacAccentButton）。
    /// 这是 WPF 官方推荐的样式方式：外观集中在 XAML 资源字典，代码只取用，不手写 ControlTemplate。
    /// 若资源缺失（极端情况），仅给最小亮色前景兜底（不覆盖模板，避免手写模板引入黑字/点不动/白条）。
    /// </summary>
    private static T WithStyle<T>(T element, string key) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style s)
        {
            element.Style = s;
        }
        else if (element is Control c)
        {
            // 最小兜底：跟随 ThemeForeground 主题令牌（随模式切换），绝不触碰 Template/ItemTemplate。
            if (Application.Current?.TryFindResource("ThemeForeground") is Brush fb)
            {
                c.Foreground = fb;
            }
        }

        return element;
    }

    private static TextBlock TitleBlock(string text, IThemeTokens tokens, Brush? foreground = null)
        => new()
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground ?? tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 6)
        };

    private static Border SectionSeparator(IThemeTokens tokens, Brush? brush = null)
        => new()
        {
            Height = 1,
            Background = brush ?? tokens.Separator,
            Margin = new Thickness(0, 4, 0, 0)
        };

    private static DockPanel SettingRow(string label, UIElement control, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground
        };
        DockPanel.SetDock(text, Dock.Left);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private static Border ActionRow(IThemeTokens tokens, string title, string description, RoutedEventHandler onClick, Brush? mutedForeground = null, Brush? separatorBrush = null)
    {
        var button = WithStyle(new Button
        {
            Content = title,
            Width = 110,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Left
        }, "MacAccentButton");
        button.Click += onClick;

        var desc = new TextBlock
        {
            Text = description,
            Foreground = mutedForeground ?? tokens.MutedForeground,
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        DockPanel.SetDock(button, Dock.Left);
        dock.Children.Add(button);
        dock.Children.Add(desc);

        // 注意：分隔线必须是独立元素，绝不能给整行 Border 设 Opacity ——
        // 否则按钮与描述文字一起被压暗（"蒙一层灰"）。分隔线用自身低 alpha 笔刷即可。
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(dock, Dock.Top);
        row.Children.Add(dock);
        row.Children.Add(SectionSeparator(tokens, separatorBrush));
        return new Border
        {
            Child = row,
            Padding = new Thickness(0, 0, 0, 8)
        };
    }
}
