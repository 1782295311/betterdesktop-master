using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.PluginSdk;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;

namespace BetterDesktop.Shell.Settings.Sections;

/// <summary>
/// 主题分区：统一管控程序一切外观。
/// 所有改动实时写入 <see cref="IAppearanceService"/>（经其持久化到 settings 并广播 Changed），
/// 已订阅的 ShellWindow（设置/Dock/菜单栏等）即时重绘，无需重启。
/// 不实现深/浅色模式：色调为毛玻璃中性协调色，可被皮肤覆盖。
/// </summary>
public sealed class ThemeSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "主题";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        // tokens 实际为 AppearanceService 实例（SettingsPlugin 同时 Provide 两者），借此实时改外观。
        var appearance = tokens as IAppearanceService;

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = "主题",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var descBlock = new TextBlock
        {
            Text = "统一管控程序的外观模式、强调色、透明度、毛玻璃、字号与皮肤。改动即时生效于所有窗口。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(descBlock);

        // ---- 外观模式（无色/亮色/暗色） ----
        var cardMode = GroupCard(tokens);
        CardBody(cardMode).Children.Add(TitleBlock("外观模式", tokens));
        CardBody(cardMode).Children.Add(ModeRow(appearance, tokens, descBlock));
        panel.Children.Add(cardMode);

        // ---- 窗体透明度与毛玻璃 ----
        var cardWin = GroupCard(tokens);
        CardBody(cardWin).Children.Add(TitleBlock("窗体透明度与毛玻璃", tokens));
        CardBody(cardWin).Children.Add(SliderRow("窗体不透明度", appearance, tokens,
            () => appearance?.WindowOpacity ?? 0.667,
            v => { if (appearance is not null) appearance.WindowOpacity = v; },
            0.2, 1.0));
        CardBody(cardWin).Children.Add(MaterialToggle(appearance, tokens));
        panel.Children.Add(cardWin);

        // ---- 字号缩放 ----
        var cardFont = GroupCard(tokens);
        CardBody(cardFont).Children.Add(TitleBlock("字号缩放", tokens));
        CardBody(cardFont).Children.Add(FontScaleRow(appearance, tokens));
        panel.Children.Add(cardFont);

        // ---- 间距缩放（圆角半径设置已撤除：窗口圆角方案待重做） ----
        var cardGeo = GroupCard(tokens);
        CardBody(cardGeo).Children.Add(TitleBlock("间距缩放", tokens));
        CardBody(cardGeo).Children.Add(SliderRow("间距缩放", appearance, tokens,
            () => appearance?.SpacingScale ?? 1,
            v => { if (appearance is not null) appearance.SpacingScale = v; },
            0.8, 1.3));
        panel.Children.Add(cardGeo);

        // ---- 描边（全局卡片视觉规范，可实时切换；阴影效果已按需求移除，见 ShadowSize 固定为 0） ----
        var cardStroke = GroupCard(tokens);
        CardBody(cardStroke).Children.Add(TitleBlock("描边", tokens));
        CardBody(cardStroke).Children.Add(BorderStyleRow(appearance, tokens));
        CardBody(cardStroke).Children.Add(SliderRow("描边强度", appearance, tokens,
            () => appearance?.BorderStrength ?? 0.3,
            v => { if (appearance is not null) appearance.BorderStrength = v; },
            0, 0.8));
        panel.Children.Add(cardStroke);

        // ---- 皮肤（图片参数化 + 配色预设 + 管理器） ----
        var cardSkin = GroupCard(tokens);
        CardBody(cardSkin).Children.Add(TitleBlock("皮肤", tokens));
        CardBody(cardSkin).Children.Add(SkinCard(appearance, tokens));
        panel.Children.Add(cardSkin);

        return panel;
    }

    // ===== 各分组控件 =====

    private static UIElement SliderRow(string label, IAppearanceService? appearance, IThemeTokens tokens,
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

    private static UIElement ModeRow(IAppearanceService? appearance, IThemeTokens tokens, TextBlock? descBlock = null)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        }, "MacCombo", tokens);
        combo.Items.Add("无色（跟随系统）");
        combo.Items.Add("亮色（叠加透白）");
        combo.Items.Add("暗色（叠加透黑）");
        // 下拉项亮色与渲染由 App.xaml 的 MacCombo 模板统一负责（ItemsPresenter + 深色弹层）。
        var cur = appearance?.Mode ?? ThemeMode.None;
        combo.SelectedIndex = (int)cur;
        combo.SelectionChanged += (_, _) =>
        {
            if (appearance is null) return;
            appearance.Mode = (ThemeMode)combo.SelectedIndex;
            // 模式切换后，本分区说明文字颜色（前景/次要前景随模式推导）即时刷新。
            if (descBlock is not null) descBlock.Foreground = tokens.MutedForeground;
        };
        return combo;
    }

    private static UIElement MaterialToggle(IAppearanceService? appearance, IThemeTokens tokens)
    {
        var box = WithStyle(new CheckBox
        {
            Content = "使用系统亚克力材质（Acrylic，自带暗色调，偏暗）",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 13
        }, "MacToggle", tokens);
        box.IsChecked = appearance is not null && appearance.Material == VibrancyStyle.Acrylic;
        box.Checked += (_, _) => { if (appearance is not null) appearance.Material = VibrancyStyle.Acrylic; };
        box.Unchecked += (_, _) => { if (appearance is not null) appearance.Material = VibrancyStyle.Transparent; };
        return box;
    }

    private static UIElement FontScaleRow(IAppearanceService? appearance, IThemeTokens tokens)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacCombo", tokens);
        combo.Items.Add("小");
        combo.Items.Add("中");
        combo.Items.Add("大");
        // 下拉项亮色与渲染均由 App.xaml 的 MacCombo 模板统一负责（ItemsPresenter + 深色弹层），
        // 此处不再额外设 ItemTemplate，避免与模板冲突。
        var cur = appearance?.FontScale ?? 1.0;
        combo.SelectedIndex = cur switch
        {
            <= 0.95 => 0,
            >= 1.1 => 2,
            _ => 1
        };
        combo.SelectionChanged += (_, _) =>
        {
            var scale = combo.SelectedIndex switch
            {
                0 => 0.9,
                2 => 1.15,
                _ => 1.0
            };
            if (appearance is not null) appearance.FontScale = scale;
        };
        return combo;
    }

    private static UIElement BorderStyleRow(IAppearanceService? appearance, IThemeTokens tokens)
    {
        var combo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        }, "MacCombo", tokens);
        combo.Items.Add("纯色");
        combo.Items.Add("顶部高光");
        combo.Items.Add("对角渐变");
        combo.Items.Add("内描边");
        // 下拉项亮色与渲染由 App.xaml 的 MacCombo 模板统一负责（ItemsPresenter + 深色弹层）。
        var cur = appearance?.BorderStyle ?? 0;
        combo.SelectedIndex = cur is < 0 or > 3 ? 0 : cur;
        combo.SelectionChanged += (_, _) =>
        {
            if (appearance is not null) appearance.BorderStyle = combo.SelectedIndex;
        };
        return combo;
    }

    private static UIElement SkinCard(IAppearanceService? appearance, IThemeTokens tokens)
    {
        var manager = appearance is not null ? new SkinManager(appearance) : null;
        var wrap = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        // 当前皮肤名
        var current = new TextBlock
        {
            Text = CurrentSkinName(appearance),
            Foreground = tokens.MutedForeground,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };

        // 皮肤列表（网格缩略图）：预设色块 + 用户图片缩略图，点击即激活。
        // 参数面板提前声明（闭包捕获需要词法先声明）。
        var paramPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        const double cell = 84;
        const double cellH = 64;
        if (manager is not null)
        {
            foreach (var entry in manager.List())
            {
                var btn = WithStyle(new Button
                {
                    Width = cell,
                    Height = cellH,
                    Margin = new Thickness(0, 0, 8, 8),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = entry.Name
                }, entry.Kind == SkinKind.Image ? "MacButton" : "MacButton", tokens);

                // 缩略图内容：图片用 Image；预设用色块文本。
                if (entry.ThumbnailPath is { } imgPath && File.Exists(imgPath))
                {
                    try
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(imgPath, UriKind.Absolute);
                        bi.DecodePixelWidth = (int)cell;
                        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        btn.Content = new Image { Source = bi, Stretch = Stretch.UniformToFill };
                    }
                    catch { btn.Content = entry.Name; }
                }
                else
                {
                    btn.Content = entry.Name;
                }

                var id = entry.Id;
                btn.Click += (_, _) =>
                {
                    if (appearance is not null)
                    {
                        manager.Apply(id);
                        current.Text = CurrentSkinName(appearance);
                        SyncParamPanelVisibility(paramPanel, appearance);
                    }
                };
                grid.Children.Add(btn);
            }
        }

        // 参数面板（仅图片皮肤显示）：拉伸/暗化/模糊/不透明度
        BuildSkinParams(paramPanel, appearance, tokens);
        SyncParamPanelVisibility(paramPanel, appearance);

        // 选择背景图片 / 清除皮肤
        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
        var pick = WithStyle(new Button
        {
            Content = "选择背景图片",
            Width = 130,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left
        }, "MacAccentButton", tokens);
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                Title = "选择窗口皮肤背景"
            };
            if (dlg.ShowDialog() == true && appearance is not null && manager is not null)
            {
                // 复制到 skins 目录便于管理器枚举 + 持久化路径稳定。
                var saved = CopyToSkinsDir(dlg.FileName);
                var id = "img:" + saved;
                manager.Apply(id);
                current.Text = CurrentSkinName(appearance);
                SyncParamPanelVisibility(paramPanel, appearance);
            }
        };
        var clear = WithStyle(new Button
        {
            Content = "清除皮肤",
            Width = 100,
            Height = 32,
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        }, "MacButton", tokens);
        clear.Click += (_, _) =>
        {
            if (appearance is not null && manager is not null)
            {
                manager.Apply("");
                current.Text = CurrentSkinName(appearance);
                SyncParamPanelVisibility(paramPanel, appearance);
            }
        };
        DockPanel.SetDock(pick, Dock.Left);
        actions.Children.Add(pick);
        DockPanel.SetDock(clear, Dock.Left);
        actions.Children.Add(clear);

        wrap.Children.Add(current);
        wrap.Children.Add(grid);
        wrap.Children.Add(paramPanel);
        wrap.Children.Add(actions);
        return wrap;
    }

    private static void BuildSkinParams(StackPanel panel, IAppearanceService? appearance, IThemeTokens tokens)
    {
        // 皮肤层级（模糊 / 清晰）：决定皮肤图本身是否做模糊处理。
        // 模糊档对图套 BlurEffect，图发糊且底层 DWM 毛玻璃透出形成磨砂感；清晰档图原样锐利。
        var layerCombo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        }, "MacCombo", tokens);
        layerCombo.Items.Add("模糊（磨砂皮肤）");
        layerCombo.Items.Add("清晰（锐利皮肤）");
        // 下拉项亮色与渲染由 App.xaml 的 MacCombo 模板统一负责（ItemsPresenter + 深色弹层）。
        layerCombo.SelectedIndex = appearance is not null && !appearance.SkinBlurBehindDwm ? 1 : 0;
        layerCombo.SelectionChanged += (_, _) =>
        {
            if (appearance is not null) appearance.SkinBlurBehindDwm = layerCombo.SelectedIndex == 0;
        };
        panel.Children.Add(Labeled("皮肤层级", layerCombo, tokens));

        // 拉伸方式
        var stretchCombo = WithStyle(new ComboBox
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        }, "MacCombo", tokens);
        stretchCombo.Items.Add("铺满裁切");
        stretchCombo.Items.Add("完整居中");
        stretchCombo.Items.Add("拉伸变形");
        stretchCombo.Items.Add("平铺");
        stretchCombo.SelectedIndex = appearance is not null ? appearance.SkinImageStretch : 0;
        stretchCombo.SelectionChanged += (_, _) =>
        {
            if (appearance is not null) appearance.SkinImageStretch = stretchCombo.SelectedIndex;
        };
        panel.Children.Add(Labeled("拉伸方式", stretchCombo, tokens));

        // 暗化 / 不透明度 滑块（暗化让皮肤图下文字更可读；不透明度控制皮肤图自身透明度）
        panel.Children.Add(SliderRow("暗化", appearance, tokens,
            () => appearance?.SkinImageDarken ?? 0.25,
            v => { if (appearance is not null) appearance.SkinImageDarken = v; }, 0, 0.8));
        panel.Children.Add(SliderRow("不透明度", appearance, tokens,
            () => appearance?.SkinImageOpacity ?? 1,
            v => { if (appearance is not null) appearance.SkinImageOpacity = v; }, 0.3, 1));
    }

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

    private static void SyncParamPanelVisibility(StackPanel paramPanel, IAppearanceService? appearance)
    {
        // 参数面板仅图片皮肤显示；配色预设/无皮肤隐藏。
        paramPanel.Visibility = appearance is not null && appearance.SkinKind == SkinKind.Image
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string CurrentSkinName(IAppearanceService? appearance)
    {
        if (appearance is null) return "（无皮肤，使用半透明托盘）";
        return appearance.SkinKind switch
        {
            SkinKind.Image => "图片皮肤：" + (appearance.SkinPath ?? ""),
            SkinKind.Preset => "配色预设：" + appearance.SkinActive,
            _ => "（无皮肤，使用半透明托盘）"
        };
    }

    private static string CopyToSkinsDir(string srcPath)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = System.IO.Path.Combine(appData, "BetterDesktop", "skins");
            Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(srcPath));
            // 避免覆盖同名：加时间戳。
            if (File.Exists(dest))
            {
                dest = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(srcPath)}_{DateTime.Now:yyyyMMddHHmmss}{System.IO.Path.GetExtension(srcPath)}");
            }
            File.Copy(srcPath, dest, overwrite: false);
            return dest;
        }
        catch
        {
            // 复制失败则直接用原路径（仍可用，只是不进管理器枚举）。
            return srcPath;
        }
    }

    // ===== 共享 UI helper（Section 自包含） =====

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

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens)
        => new()
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 6)
        };

    /// <summary>
    /// 套用宿主 App.xaml 中已定义好的 macOS 风 XAML 样式（MacCombo/MacSlider/MacToggle/MacButton/MacAccentButton）。
    /// 这是 WPF 官方推荐的样式方式：外观集中在 XAML 资源字典，代码只取用，不手写 ControlTemplate。
    /// 若资源缺失（极端情况），仅给最小亮色前景兜底（不覆盖模板，避免手写模板引入黑字/点不动/白条）。
    /// </summary>
    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style s)
        {
            element.Style = s;
        }
        else if (element is Control c)
        {
            // 最小兜底：跟随主题令牌前景色（随模式切换），绝不触碰 Template/ItemTemplate。
            c.Foreground = tokens.Foreground;
        }

        return element;
    }
}
