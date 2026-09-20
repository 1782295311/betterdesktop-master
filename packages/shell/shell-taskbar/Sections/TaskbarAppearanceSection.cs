using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;
using BetterDesktop.Shell.Taskbar.Contracts;
using BetterDesktop.Shell.Taskbar.Services;

namespace BetterDesktop.Shell.Taskbar.Sections;

/// <summary>
/// 任务栏外观设置分区（极简版）：等同系统原生"任务栏样式"控制——
/// 一个全局样式下拉 + 一个全局颜色，改动即时生效，退出程序自动还原系统默认。
/// （2026-08-25 按用户要求删去场景切换/透明度/模糊半径等精细控件。）
/// </summary>
public sealed class TaskbarAppearanceSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "任务栏外观";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        // 经桥取回任务栏外观服务（分区接口仅接 ISettingsService，as 转换永远为 null 不可用）。
        var svc = TaskbarServiceBridge.TryGet();
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // 页面标题统一由窗口标题栏承载，此处不再重复渲染大标题。
        panel.Children.Add(new TextBlock
        {
            Text = "选择任务栏样式与颜色，改动即时生效；退出本程序后自动还原为系统默认。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "提示：「不透明纯色」中选择的颜色会在其他样式下同样生效（作为底色）；不想要时把「强弱」拉到最低即可。",
            Foreground = tokens.MutedForeground,
            FontSize = 12,
            Margin = new Thickness(0, -10, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });

        if (svc is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "任务栏外观服务尚未就绪（插件未加载或初始化中），请稍后重新打开本页。",
                Foreground = tokens.MutedForeground,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return panel;
        }

        var config = svc.Current;

        // ---- 任务栏样式（全局，等同系统原生"任务栏样式"设置） ----
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("任务栏样式", tokens));
        CardBody(card).Children.Add(Desc("全局应用到任务栏，任意时刻生效（若有最大化窗口等场景，同样使用此样式）。", tokens));

        // 纯色：色板 + 强弱（仅「不透明纯色」样式显示；选用预设色块，无需手输色号）
        var solidPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var combo = WithStyle(new ComboBox
        {
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "默认：系统原生外观 · 透明：半透明白色 · 模糊：高斯模糊 · 亚克力：毛玻璃质感 · 不透明纯色：实底 · 完全透明：只留边缘"
        }, "MacCombo", tokens);
        combo.Items.Add("默认");
        combo.Items.Add("透明");
        combo.Items.Add("模糊");
        combo.Items.Add("亚克力");
        combo.Items.Add("不透明纯色");
        combo.Items.Add("完全透明");
        combo.SelectedIndex = config.Desktop.Accent switch
        {
            TaskbarAccent.Normal => 0,
            TaskbarAccent.Clear => 1,
            TaskbarAccent.Blur => 2,
            TaskbarAccent.Acrylic => 3,
            TaskbarAccent.Opaque => 4,
            _ => 0
        };
        combo.SelectionChanged += (_, _) =>
        {
            var accent = combo.SelectedIndex switch
            {
                1 => TaskbarAccent.Clear,
                2 => TaskbarAccent.Blur,
                3 => TaskbarAccent.Acrylic,
                4 => TaskbarAccent.Opaque,
                _ => TaskbarAccent.Normal
            };
            ApplyStyleToAll(config, accent);
            svc.SetConfig(config);
            UpdateSolidPanel();
        };
        CardBody(card).Children.Add(combo);

        // ---- 纯色：色板 + 强弱（仅「不透明纯色」样式显示；选用预设色块，无需手输色号） ----
        solidPanel.Children.Add(new TextBlock
        {
            Text = "颜色（预设色板）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var palette = new WrapPanel();
        var swatches = new List<Border>();
        foreach (var c in Palette)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = c
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                var color = (Color)swatch.Tag;
                var a = AlphaOf(config.Desktop.Color);
                ApplyColorToAll(config, PackColor(a, color.R, color.G, color.B));
                svc.SetConfig(config);
                UpdateHighlight();
            };
            swatches.Add(swatch);
            palette.Children.Add(swatch);
        }
        solidPanel.Children.Add(palette);

        // 强弱（不透明度）滑杆：0=全透明，100=全不透明
        var strengthLabel = new TextBlock
        {
            Text = "强弱（不透明度）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 6, 0, 2)
        };
        solidPanel.Children.Add(strengthLabel);
        var strength = WithStyle(new Slider
        {
            Width = 220,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Round(AlphaOf(config.Desktop.Color) / 255.0 * 100.0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "0 = 全透明（等同「完全透明」），100 = 完全不透明（实底）"
        }, "MacSlider", tokens);
        strength.ValueChanged += (_, _) =>
        {
            byte a = (byte)Math.Round(strength.Value / 100.0 * 255.0);
            var (cr, cg, cb) = UnpackRgb(config.Desktop.Color);
            ApplyColorToAll(config, PackColor(a, cr, cg, cb));
            svc.SetConfig(config);
        };
        solidPanel.Children.Add(strength);

        CardBody(card).Children.Add(solidPanel);

        // 仅「不透明纯色」样式显示色板与强弱，其余样式隐藏
        void UpdateSolidPanel() =>
            solidPanel.Visibility = combo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        void UpdateHighlight()
        {
            var current = UnpackRgb(config.Desktop.Color);
            foreach (var s in swatches)
            {
                var c = (Color)s.Tag;
                s.BorderBrush = (c.R == current.R && c.G == current.G && c.B == current.B)
                    ? tokens.Accent
                    : Brushes.Transparent;
            }
        }
        UpdateSolidPanel();
        UpdateHighlight();

        panel.Children.Add(card);

        // 初始套用一次（保证打开设置即生效当前配置）
        svc.SetConfig(config);

        return panel;
    }

    /// <summary>把所选样式应用到全部场景（全局统一，等同系统原生"任务栏样式"）。</summary>
    private static void ApplyStyleToAll(TaskbarAppearanceConfig config, TaskbarAccent accent)
    {
        config.Desktop.Accent = accent;
        config.VisibleWindow.Accent = accent;
        config.MaximizedWindow.Accent = accent;
        config.StartOpened.Accent = accent;
        config.SearchOpened.Accent = accent;
        config.TaskViewOpened.Accent = accent;
        config.BatterySaver.Accent = accent;
    }

    /// <summary>把所选颜色应用到全部场景（全局统一）。</summary>
    private static void ApplyColorToAll(TaskbarAppearanceConfig config, uint color)
    {
        config.Desktop.Color = color;
        config.VisibleWindow.Color = color;
        config.MaximizedWindow.Color = color;
        config.StartOpened.Color = color;
        config.SearchOpened.Color = color;
        config.TaskViewOpened.Color = color;
        config.BatterySaver.Color = color;
    }

    // 预设色板（Windows 强调色 + 常用中性色，供纯色模式点选）
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x00, 0x00, 0x00), // 黑
        Color.FromRgb(0x40, 0x40, 0x40), // 深灰
        Color.FromRgb(0x80, 0x80, 0x80), // 中灰
        Color.FromRgb(0xC0, 0xC0, 0xC0), // 银灰
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白
        Color.FromRgb(0xE8, 0x11, 0x23), // 红
        Color.FromRgb(0xF7, 0x63, 0x0C), // 橙
        Color.FromRgb(0xFF, 0xB9, 0x00), // 黄
        Color.FromRgb(0x10, 0x89, 0x3E), // 绿
        Color.FromRgb(0x00, 0xB2, 0x94), // 青
        Color.FromRgb(0x00, 0x78, 0xD7), // 蓝
        Color.FromRgb(0x5C, 0x2D, 0x91), // 紫
        Color.FromRgb(0xE3, 0x00, 0x8C), // 粉
        Color.FromRgb(0x8E, 0x56, 0x2E), // 棕
    };

    // ---- 颜色辅助（0xAARRGGBB 与 RGB/Alpha 互转） ----
    private static byte AlphaOf(uint c) => (byte)((c >> 24) & 0xFF);
    private static uint PackColor(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    private static (byte R, byte G, byte B) UnpackRgb(uint c) => ((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));

    // ===== 共享 helper（自包含，沿用 ThemeSection 风格） =====
    private static UIElement Desc(string text, IThemeTokens tokens) => new TextBlock
    {
        Text = text,
        Foreground = tokens.MutedForeground,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Border GroupCard(IThemeTokens tokens) => SettingsUi.CreateCard();

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style style)
        {
            element.Style = style;
        }
        else
        {
            // 最小兜底：仅前景色，绝不碰 Template/ItemTemplate（Mac 模板统一负责外观）
            if (element is Control control)
            {
                control.Foreground = ThemeBrushes.Get("ControlForeground");
            }
        }
        return element;
    }
}
