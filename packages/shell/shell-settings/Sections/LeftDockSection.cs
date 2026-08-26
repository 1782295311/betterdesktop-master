using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterDesktop.Shell.Settings.Contracts;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Settings.Sections;

/// <summary>
/// 左侧 dock 栏设置分区：统一管理 dock 视觉参数。
/// 直接经 <see cref="ISettingsService"/> 读写 <c>dock.*</c> 键（与 DockVisualSettings 同约定），
/// 不依赖 shell-dock 程序集，保持设置面板与渲染层解耦。
/// 涵盖：图标大小/间距、距底部高度、名称显示、倒影（开关+强度/透明度/距离/渐变距离）、
/// dock 清晰/模糊、用户图标文件夹替换、图标顺序（在 dock 上拖拽调整）提示。
/// </summary>
public sealed class LeftDockSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "左侧 Dock";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 图标布局 ----
        var sizeCard = GroupCard(tokens);
        var sizeBody = CardBody(sizeCard);
        sizeBody.Children.Add(TitleBlock("图标与布局", tokens));

        sizeBody.Children.Add(SliderRow("图标大小", settings, tokens,
            () => Clamp(settings.Get("dock.iconSize", 44d), 24, 96),
            v => settings.Set("dock.iconSize", v), 24, 96));
        sizeBody.Children.Add(SliderRow("图标间距", settings, tokens,
            () => Clamp(settings.Get("dock.iconSpacing", 12d), 0, 48),
            v => settings.Set("dock.iconSpacing", v), 0, 48));
        sizeBody.Children.Add(SliderRow("距底部高度", settings, tokens,
            () => Clamp(settings.Get("dock.bottomMargin", 10d), 0, 200),
            v => settings.Set("dock.bottomMargin", v), 0, 200));
        sizeBody.Children.Add(ToggleRow("显示软件名称", settings, tokens,
            "dock.showLabel", true));

        panel.Children.Add(sizeCard);

        // ---- 倒影 ----
        var refCard = GroupCard(tokens);
        var refBody = CardBody(refCard);
        refBody.Children.Add(TitleBlock("图标倒影", tokens));

        var refEnabled = ToggleRow("开启倒影", settings, tokens, "dock.reflection.enabled", false);
        refBody.Children.Add(refEnabled);

        // 倒影参数仅在启用时显示（嵌套 StackPanel，按开关切换可见性）。
        var refParams = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        refParams.Children.Add(SliderRow("倒影强度", settings, tokens,
            () => Clamp(settings.Get("dock.reflection.intensity", 0.6d), 0, 1),
            v => settings.Set("dock.reflection.intensity", v), 0, 1));
        refParams.Children.Add(SliderRow("倒影透明度", settings, tokens,
            () => Clamp(settings.Get("dock.reflection.opacity", 0.4d), 0, 1),
            v => settings.Set("dock.reflection.opacity", v), 0, 1));
        refParams.Children.Add(SliderRow("倒影距离", settings, tokens,
            () => Clamp(settings.Get("dock.reflection.distance", 4d), 0, 40),
            v => settings.Set("dock.reflection.distance", v), 0, 40));
        refParams.Children.Add(SliderRow("渐变距离", settings, tokens,
            () => Clamp(settings.Get("dock.reflection.gradientDistance", 28d), 4, 120),
            v => settings.Set("dock.reflection.gradientDistance", v), 4, 120));
        refParams.Children.Add(SliderRow("倾斜角度（负=左倾,正=右倾）", settings, tokens,
            () => Clamp(settings.Get("dock.reflection.skew", 0d), -30, 30),
            v => settings.Set("dock.reflection.skew", v), -30, 30));
        // 太阳日升日落模拟：开启后倾斜角度/方向随时间自动变化（6:00 右倾→12:00 垂直→18:00 左倾→24:00 回正→衔接次日日出），忽略手动倾斜。
        refParams.Children.Add(ToggleRow("模拟太阳轨迹（日升日落）", settings, tokens,
            "dock.reflection.sunSync", false));
        refParams.Visibility = settings.Get("dock.reflection.enabled", false) ? Visibility.Visible : Visibility.Collapsed;
        refBody.Children.Add(refParams);

        // 开关联动：开启时展开参数。
        if (refEnabled is CheckBox cb)
        {
            cb.Checked += (_, _) => refParams.Visibility = Visibility.Visible;
            cb.Unchecked += (_, _) => refParams.Visibility = Visibility.Collapsed;
        }

        panel.Children.Add(refCard);

        // ---- 材质与图标替换 ----
        var matCard = GroupCard(tokens);
        var matBody = CardBody(matCard);
        matBody.Children.Add(TitleBlock("外观与图标替换", tokens));

        // dock 清晰/模糊
        var matCombo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        }, "MacCombo", tokens);
        matCombo.Items.Add("模糊（毛玻璃）");
        matCombo.Items.Add("清晰（无模糊）");
        matCombo.SelectedIndex = settings.Get("dock.material", "blur") == "clear" ? 1 : 0;
        matCombo.SelectionChanged += (_, _) =>
            settings.Set("dock.material", matCombo.SelectedIndex == 1 ? "clear" : "blur");
        matBody.Children.Add(Labeled("Dock 材质", matCombo, tokens));

        // 运行区开关:关闭后 dock 退化为单区(仅左半 fixed 应用),开启则保持 running+分隔线(空时显示「无运行窗口」占位)。
        matBody.Children.Add(ToggleRow("显示运行区", settings, tokens,
            "dock.showRunning", true));

        // 用户图标文件夹选择
        var folderPath = settings.Get("dock.customIconFolder", "") ?? "";
        var pathText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(folderPath) ? "（未设置，使用系统默认图标）" : folderText(folderPath),
            Foreground = tokens.MutedForeground,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        };
        var pickBtn = WithStyle(new Button
        {
            Content = "选择图标文件夹",
            Width = 160,
            Margin = new Thickness(0, 4, 0, 0)
        }, "MacButton", tokens);
        pickBtn.Click += (_, _) =>
        {
            // WPF 无原生文件夹选择框，用 OpenFileDialog 引导用户进入目标文件夹后选任一文件，取其目录作为图标根目录。
            // 文件名提示用户"进入文件夹后选中任意文件即可"。
            var dlg = new OpenFileDialog
            {
                Title = "进入图标文件夹后选中其中任一文件即可（取其所在目录）",
                CheckFileExists = true,
                FileName = ""
            };
            var existing = settings.Get("dock.customIconFolder", "") ?? "";
            if (!string.IsNullOrWhiteSpace(existing) && System.IO.Directory.Exists(existing))
            {
                dlg.InitialDirectory = existing;
            }
            if (dlg.ShowDialog() == true)
            {
                var dir = System.IO.Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    settings.Set("dock.customIconFolder", dir);
                    pathText.Text = folderText(dir);
                }
            }
        };
        matBody.Children.Add(Labeled("自定义图标", pickBtn, tokens));
        matBody.Children.Add(pathText);
        matBody.Children.Add(new TextBlock
        {
            Text = "将文件夹中与程序 exe 同名（去扩展名）的 .png/.ico 放入即可替换对应图标。",
            Foreground = tokens.MutedForeground,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        });

        // 图标顺序提示
        matBody.Children.Add(new TextBlock
        {
            Text = "提示：在 Dock 栏上按住左键拖动图标即可调整顺序（自动保存）。",
            Foreground = tokens.MutedForeground,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        panel.Children.Add(matCard);

        // ---- 系统功能（运行区右侧：此电脑/网络/回收站/控制面板）----
        // 每个开关独立控制 Dock 运行区右侧对应系统图标的显示/隐藏，即时生效。
        var sysCard = GroupCard(tokens);
        var sysBody = CardBody(sysCard);
        sysBody.Children.Add(TitleBlock("系统功能", tokens));
        sysBody.Children.Add(ToggleRow("显示「此电脑」", settings, tokens, "dock.systemComputer", true));
        sysBody.Children.Add(ToggleRow("显示「网络」", settings, tokens, "dock.systemNetwork", true));
        sysBody.Children.Add(ToggleRow("显示「回收站」", settings, tokens, "dock.systemRecycleBin", true));
        sysBody.Children.Add(ToggleRow("显示「控制面板」", settings, tokens, "dock.systemControlPanel", true));
        panel.Children.Add(sysCard);

        return panel;
    }

    private static string folderText(string dir) => "当前：" + dir;

    // ===== 共享 UI helper（与 ThemeSection 同风格，Section 自包含） =====

    private static Border GroupCard(IThemeTokens tokens)
    {
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        var inner = new Border { CornerRadius = new CornerRadius(9), Child = body };
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 16),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = inner
        };
    }

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static UIElement Labeled(string label, UIElement control, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        DockPanel.SetDock(control, Dock.Left);
        row.Children.Add(control);
        return row;
    }

    private static UIElement SliderRow(string label, ISettingsService settings, IThemeTokens tokens,
        Func<double> getVal, Action<double> setVal, double min, double max)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 13,
            Foreground = tokens.Foreground
        };
        DockPanel.SetDock(text, Dock.Left);

        var valueText = new TextBlock
        {
            Text = getVal().ToString("0.00"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Foreground = tokens.MutedForeground,
            MinWidth = 36
        };

        var slider = WithStyle(new Slider
        {
            Minimum = min,
            Maximum = max,
            Width = 220,
            Value = getVal(),
            VerticalAlignment = VerticalAlignment.Center,
            AutoToolTipPlacement = AutoToolTipPlacement.None
        }, "MacSlider", tokens);
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue, 2);
            setVal(v);
            valueText.Text = v.ToString("0.00");
        };

        var right = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(valueText, Dock.Right);
        right.Children.Add(valueText);
        DockPanel.SetDock(slider, Dock.Left);
        right.Children.Add(slider);

        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(text);
        row.Children.Add(right);
        return row;
    }

    private static UIElement ToggleRow(string label, ISettingsService settings, IThemeTokens tokens,
        string key, bool def)
    {
        var box = WithStyle(new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 13,
            IsChecked = settings.Get(key, def)
        }, "MacToggle", tokens);
        box.Checked += (_, _) => settings.Set(key, true);
        box.Unchecked += (_, _) => settings.Set(key, false);
        return box;
    }

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style s)
        {
            element.Style = s;
        }
        else if (element is Control c)
        {
            c.Foreground = tokens.Foreground;
        }
        return element;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}
