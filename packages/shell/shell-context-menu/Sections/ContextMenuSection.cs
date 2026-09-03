// BetterDesktop.Shell.ContextMenus — 右键菜单设置分区
// 三块能力（用户 2026-09-02 定版）：
//   1. 外观：菜单不透明度用户自调（context-menu.opacity，透出 vibrancy 毛玻璃的程度）。
//   2. 快捷工具管理：第三方工具（VS Code/压缩软件/WPS…）一键检测添加 + 手动添加 + 编辑/启停/删除，
//      存 context-menu.custom.items（与 UserMenuContributor 同键，右键即时生效）。
//   3. 功能状态清单：已实现/计划 roadmap 用户可见。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Sections;

/// <summary>右键菜单设置分区（外观透明度 + 快捷工具管理 + 功能状态）。</summary>
public sealed class ContextMenuSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "右键菜单";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        panel.Children.Add(new TextBlock
        {
            Text = "右键菜单",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "全项目统一右键菜单：桌面图标 / 桌面空白 / Dock / 开始菜单 / 应用提取器全部经统一弹层" +
                   "（ShellWindow 窗口 + 主题令牌样式）渲染，随「设置 → 外观」主题即时换肤。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(BuildOpacityCard(settings, tokens));
        panel.Children.Add(BuildExtendedCard(settings, tokens));
        panel.Children.Add(BuildThirdPartyCard(settings, tokens));
        panel.Children.Add(BuildToolsCard(settings, tokens));
        panel.Children.Add(BuildFeaturesCard(tokens));
        panel.Children.Add(BuildControlCard(settings, tokens));

        return panel;
    }

    // ===== 外观：不透明度 =====

    private UIElement BuildOpacityCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("外观", tokens));
        CardBody(card).Children.Add(Desc(
            "菜单面板不透明度：越低越透出毛玻璃与桌面，越高越清晰易读。改动后下一次右键生效。",
            tokens));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = "不透明度",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 12, 0)
        });
        var valueText = new TextBlock
        {
            Text = $"{(int)Math.Round(Math.Clamp(settings.Get(MenuService.OpacityKey, 0.82), 0.3, 1.0) * 100)}%",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = tokens.MutedForeground,
            MinWidth = 44
        };
        var slider = WithStyle(new Slider
        {
            Minimum = 30,
            Maximum = 100,
            TickFrequency = 5,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            Value = Math.Clamp(settings.Get(MenuService.OpacityKey, 0.82), 0.3, 1.0) * 100
        }, "MacSlider", tokens);
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            valueText.Text = $"{v}%";
            settings.Set(MenuService.OpacityKey, v / 100.0);
        };
        row.Children.Add(slider);
        row.Children.Add(valueText);
        CardBody(card).Children.Add(row);
        return card;
    }

    // ===== Shift 扩展项（计划 H1：Win10 式扩展机制开关） =====

    private UIElement BuildExtendedCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("扩展项", tokens));
        CardBody(card).Children.Add(Desc(
            "Win10 式 Shift 扩展机制：永久删除、以其他用户身份运行、复制到文件夹…等低频/危险项，" +
            "默认仅在按住 Shift 右键时出现（不进二级收纳）。开启下方开关后全部常驻显示。",
            tokens));

        var check = new CheckBox
        {
            Content = "扩展项常驻（不按 Shift 也显示）",
            FontSize = 13,
            Foreground = tokens.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            IsChecked = settings.Get(MenuService.ExtendedAlwaysKey, false),
        };
        check.Checked += (_, _) => settings.Set(MenuService.ExtendedAlwaysKey, true);
        check.Unchecked += (_, _) => settings.Set(MenuService.ExtendedAlwaysKey, false);
        CardBody(card).Children.Add(check);
        return card;
    }

    // ===== 第三方菜单项展示策略（M2：注册表 verb + COM 透传） =====

    private UIElement BuildThirdPartyCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("第三方菜单项", tokens));
        CardBody(card).Children.Add(Desc(
            "第三方软件（7-Zip/WinRAR 等）注册的右键命令默认收纳为子菜单（与资源管理器一致，保持第一层清爽）。" +
            "开启展开后，厂商顶层命令打散平铺到第一级（更深层命令仍是子菜单）。改动下一次右键生效。",
            tokens));

        var check = new CheckBox
        {
            Content = "展开到第一级（不收纳为子菜单）",
            FontSize = 13,
            Foreground = tokens.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            IsChecked = settings.Get(ShellMenuContributor.FlattenKey, false),
        };
        check.Checked += (_, _) => settings.Set(ShellMenuContributor.FlattenKey, true);
        check.Unchecked += (_, _) => settings.Set(ShellMenuContributor.FlattenKey, false);
        CardBody(card).Children.Add(check);
        return card;
    }

    // ===== 快捷工具管理 =====

    private UIElement BuildToolsCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("检测到的工具（默认全开）", tokens));
        body.Children.Add(Desc(
            "本机检测到的第三方工具已自动进入右键菜单（带图标、按功能分组，作用于文件/文件夹右键）。" +
            "不需要的把开关关掉即可从菜单移除；重新打开即恢复。改动即时生效。",
            tokens));

        // 排除名单（排除法：不在名单里 = 开）
        var disabledList = settings.Get<List<string>>(ToolCatalog.DisabledKey, new List<string>())
            ?? new List<string>();
        void SaveDisabled() => settings.Set(ToolCatalog.DisabledKey, disabledList);

        foreach (var group in ToolCatalog.Detect()
                     .GroupBy(d => d.Cat, StringComparer.OrdinalIgnoreCase))
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{group.Key}（{group.Count()}）",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = tokens.MutedForeground,
                Margin = new Thickness(0, 8, 0, 2)
            });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var d in group)
            {
                var norm = ToolCatalog.NormalizePath(d.Path);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5, 10, 5),
                    Margin = new Thickness(0, 0, 8, 4),
                    Background = Brushes.Transparent
                };
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
                if (MenuIconCache.Get(d.Path) is { } icon)
                {
                    var img = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(img, 0);
                    grid.Children.Add(img);
                }
                var nameText = new TextBlock { Text = d.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(nameText, 1);
                grid.Children.Add(nameText);
                var toggle = WithStyle(new CheckBox { VerticalAlignment = VerticalAlignment.Center }, "MacToggle", tokens);
                toggle.IsChecked = !disabledList.Any(n => ToolCatalog.NormalizePath(n) == norm);
                toggle.Unchecked += (_, _) => { disabledList.Add(norm); SaveDisabled(); };
                toggle.Checked += (_, _) =>
                {
                    disabledList.RemoveAll(n => ToolCatalog.NormalizePath(n) == norm);
                    SaveDisabled();
                };
                Grid.SetColumn(toggle, 2);
                grid.Children.Add(toggle);
                row.Child = grid;
                wrap.Children.Add(row);
            }
            body.Children.Add(wrap);
        }

        // 自定义添加（检测不到的程序；在下方列表中可编辑/删除）
        body.Children.Add(new TextBlock
        {
            Text = "自定义添加",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.MutedForeground,
            Margin = new Thickness(0, 10, 0, 2)
        });
        var toolsHost = new StackPanel();
        body.Children.Add(toolsHost);
        RenderTools(settings, tokens, toolsHost);

        // 手动添加
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var addManualBtn = WithStyle(new Button
        {
            Content = "添加工具…（选择程序）",
            FontSize = 12.5,
            Padding = new Thickness(10, 4, 10, 4)
        }, "MacButton", tokens);
        addManualBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择工具程序",
                Filter = "程序|*.exe;*.bat;*.cmd|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
            {
                return;
            }

            var list = LoadTools(settings);
            list.Add(new UserMenuSpec
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Path.GetFileNameWithoutExtension(dlg.FileName),
                Command = dlg.FileName,
                Arguments = "\"%file%\"",
                Scope = nameof(MenuScope.DesktopIcon),
                Enabled = true,
            });
            SaveAll(settings, list);
            RenderTools(settings, tokens, toolsHost);
        };
        addRow.Children.Add(addManualBtn);
        body.Children.Add(addRow);
        return card;
    }

    /// <summary>重渲染工具列表（增删改后调用）：按功能分类分组展示。</summary>
    private void RenderTools(ISettingsService settings, IThemeTokens tokens, StackPanel host)
    {
        host.Children.Clear();
        var tools = LoadTools(settings);
        if (tools.Count == 0)
        {
            host.Children.Add(Desc("（暂无快捷工具）", tokens));
            return;
        }

        foreach (var group in tools.GroupBy(
                     t => string.IsNullOrWhiteSpace(t.Category) ? "工具" : t.Category!.Trim(),
                     StringComparer.OrdinalIgnoreCase))
        {
            host.Children.Add(new TextBlock
            {
                Text = $"{group.Key}（{group.Count()}）",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = tokens.MutedForeground,
                Margin = new Thickness(0, 8, 0, 2)
            });
            foreach (var spec in group)
            {
                host.Children.Add(BuildToolRow(settings, tokens, spec, tools, host));
            }
        }
    }

    private UIElement BuildToolRow(ISettingsService settings, IThemeTokens tokens,
        UserMenuSpec spec, List<UserMenuSpec> all, StackPanel host)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 工具图标（exe 提取，进程级缓存）
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = MenuIconCache.Get(spec.Command)
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // 启用
        var enable = WithStyle(new CheckBox { VerticalAlignment = VerticalAlignment.Center }, "MacToggle", tokens);
        enable.IsChecked = spec.Enabled;
        enable.Checked += (_, _) => { spec.Enabled = true; SaveAll(settings, all); };
        enable.Unchecked += (_, _) => { spec.Enabled = false; SaveAll(settings, all); };
        Grid.SetColumn(enable, 1);
        grid.Children.Add(enable);

        // 名称 + 参数（LostFocus 保存，避免每次击键写盘）
        var name = new TextBox
        {
            Text = spec.Name,
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        name.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(name.Text) && name.Text != spec.Name)
            {
                spec.Name = name.Text.Trim();
                SaveAll(settings, all);
            }
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);

        var args = new TextBox
        {
            Text = spec.Arguments,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        args.LostFocus += (_, _) =>
        {
            if (args.Text != spec.Arguments)
            {
                spec.Arguments = args.Text;
                SaveAll(settings, all);
            }
        };
        Grid.SetColumn(args, 3);
        grid.Children.Add(args);

        // 作用域
        var scope = WithStyle(new ComboBox { Width = 96, Height = 26, FontSize = 12 }, "MacCombo", tokens);
        scope.Items.Add("文件右键");
        scope.Items.Add("桌面空白");
        scope.SelectedIndex = string.Equals(spec.Scope, nameof(MenuScope.Desktop), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        scope.SelectionChanged += (_, _) =>
        {
            spec.Scope = scope.SelectedIndex == 1 ? nameof(MenuScope.Desktop) : nameof(MenuScope.DesktopIcon);
            SaveAll(settings, all);
        };
        Grid.SetColumn(scope, 4);
        grid.Children.Add(scope);

        // 删除
        var del = new Button
        {
            Content = "删",
            FontSize = 12,
            Width = 30,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        del.Click += (_, _) =>
        {
            all.Remove(spec);
            SaveAll(settings, all);
            RenderTools(settings, tokens, host);
        };
        Grid.SetColumn(del, 5);
        grid.Children.Add(del);

        row.Child = grid;
        return row;
    }

    private static List<UserMenuSpec> LoadTools(ISettingsService settings)
        => settings.Get<List<UserMenuSpec>>(UserMenuContributor.SettingsKey) ?? [];

    private static void SaveAll(ISettingsService settings, List<UserMenuSpec> tools)
        => settings.Set(UserMenuContributor.SettingsKey, tools);


    // ===== 功能状态 roadmap =====

    private UIElement BuildFeaturesCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("功能状态", tokens));
        foreach (var (state, text) in Features)
        {
            CardBody(card).Children.Add(Desc($"{state} {text}", tokens));
        }
        return card;
    }

    /// <summary>功能清单（✅ = 已实现并可用；🔧 = 已计划未实现）。</summary>
    private static readonly (string, string)[] Features =
    [
        ("✅", "统一弹层渲染（ShellWindow + vibrancy 毛玻璃 + 主题令牌，全表面同一样式）"),
        ("✅", "桌面空白菜单：新建 / 粘贴 / 刷新 / 查看 / 排序 / 整理图标 / 显示设置 / 个性化 / 终端"),
        ("✅", "桌面图标菜单：打开 / 打开方式 / 管理员运行 / 文件位置 / 新窗口打开 / 剪切 / 复制 / 删除 / 重命名 / 复制文件地址 / 发送到 / 属性"),
        ("✅", "子菜单悬停自动展开/收回 + 主题令牌弹层（右侧弹出）"),
        ("✅", "文件属性识别（FileKind/FileCapabilities）驱动菜单项显隐；多选整集操作（能力取交集）"),
        ("✅", "原生菜单抑制（WM_CONTEXTMENU 吞没）/ 多显示器按所在屏定位 / 未激活自愈"),
        ("✅", "快捷工具管理（检测一键添加 / 手动添加 / 启停 / 编辑 / 删除）"),
        ("✅", "菜单不透明度用户自调"),
        ("🔧", "Shell 扩展 verb 透传（IContextMenu COM 集成：第三方右键项如杀毒/编辑器子菜单）"),
        ("🔧", "菜单项图标渲染（MenuItemDef.IconKey 字段已预留）"),
        ("🔧", "回收站「还原」；压缩包「解压到…」"),
        ("🔧", "文件关联精判（HKCU UserChoice / Assoc API）"),
        ("🔧", "快捷工具按文件类型过滤显示（当前对所有文件/文件夹显示）"),
    ];

    // ===== 控制开关 =====

    private UIElement BuildControlCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        CardBody(card).Children.Add(TitleBlock("控制", tokens));
        CardBody(card).Children.Add(ToggleRow(
            settings, "context-menu.migrated", true,
            "使用统一菜单服务（关闭 = 回退旧自绘路径，仅桌面生效）", tokens));
        CardBody(card).Children.Add(Desc(
            "本页所有改动即时生效（快捷工具/透明度写入 settings.json），无需重启。",
            tokens));
        return card;
    }

    // ===== 共享 helper（对齐 StartMenuSection 风格，自包含） =====

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

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        if (Application.Current?.Resources[key] is Style style)
        {
            element.Style = style;
        }
        else if (element is Control control)
        {
            control.Foreground = tokens.Foreground;
        }

        return element;
    }

    private static UIElement ToggleRow(
        ISettingsService settings, string key, bool defaultValue, string label, IThemeTokens tokens)
    {
        var toggle = new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13
        };
        if (Application.Current?.Resources["MacToggle"] is Style style)
        {
            toggle.Style = style;
        }
        else
        {
            toggle.Foreground = tokens.Foreground;
        }

        toggle.IsChecked = settings.Get(key, defaultValue);
        toggle.Checked += (_, _) => settings.Set(key, true);
        toggle.Unchecked += (_, _) => settings.Set(key, false);
        return toggle;
    }
}
