// BetterDesktop.Shell.ContextMenus — 「右键菜单」设置分区（2026-09-05 更名定稿：菜单来源 + 样式选区 + 场景×扩展×新建 + 备份 + ShellEx 只读预览）
// 纪律（计划红线）：全程零 MessageBox（分区内提示条 / 两段式确认按钮）；新手向术语（显示位置/来源/开关）；
//   专业信息（键名/CLSID/注册表路径/命令）只进详情折叠；ShellEx 预览只读、绝不伪造小功能开关；
//   BetterDesktop 自有快捷功能与第三方扩展统一管理（同一列表、同一套开关/删除/恢复）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.ContextMenus.Sections;

// ── 本文件方法级白话索引（「右键菜单」设置分区 UI，白话 → 方法）──
//   "设置分区总装入口"                → Build
//   "菜单样式卡（Win10/Win11 经典 + 重启资源管理器）" → BuildStyleCard / SetStyleAndHint / RestartExplorerAsync
//   "场景选择卡（桌面/文件夹/驱动器…）" → BuildSceneCard / UpdateScopeHint
//   "按来源分组列出菜单项"            → ReloadGroups / BuildGroupRow / BuildBadge（来源徽标）
//   "单项行（启停/删除/新建按钮）"     → BuildItemRow；项列表 RenderPreview
//   "BetterDesktop / ShellEx 详情折叠" → RenderBetterDeskDetail / RenderShellexDetail；ShellEx 只读预览 LoadPreviewAsync
//   枚举/启停/新建的实际逻辑不在本文件：见 MenuManagerService / Toggle / Create（同包 Services）。
// ────────────────────────────────────

/// <summary>「右键菜单」设置分区（样式 / 菜单来源 / 场景扩展 / 新建 / 备份，全部内嵌无独立窗口）。</summary>
public sealed class MenuManagerSection : ISettingsSection
{
    public string Title => "右键菜单";

    public string? IconKey => null;

    private readonly ComboBox _sceneBox = new();
    private readonly TextBox _searchBox = new();
    private readonly StackPanel _groupsHost = new();
    private readonly TextBlock _statusText = new();
    private readonly IShellExMenuPreview _preview = new ShellExMenuPreview();
    private Brush _statusMuted = Brushes.Gray;

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        // 页面标题统一由窗口标题栏承载，此处不再重复渲染大标题。
        panel.Children.Add(new TextBlock
        {
            Text = "右键菜单长什么样、里面出现什么，都在这里调。所有改动即时生效，改错了随时能恢复。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(BuildStyleCard(tokens));
        panel.Children.Add(BuildSceneCard(tokens));
        panel.Children.Add(BuildNewCard(tokens));
        panel.Children.Add(BuildBackupCard(tokens));
        return panel;
    }

    // ===== 卡片① 右键菜单样式（Win11 单独选区；Win10 隐藏选区） =====

    private UIElement BuildStyleCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("换个菜单样子", tokens));
        body.Children.Add(Desc("两种右键菜单样式，选顺眼的就行。切换后点下面的按钮立刻生效。", tokens));

        if (Environment.OSVersion.Version.Build < 22000)
        {
            body.Children.Add(Desc("当前系统（Windows 10）默认使用经典菜单样式，无需切换。", tokens));
            return card;
        }

        var toggle = WithStyle(new CheckBox
        {
            Content = "使用 Windows 10 经典样式（不用每次点“显示更多选项”）",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
        }, "MacToggle", tokens);
        // 审查修复：编程赋值 IsChecked 也会触发 Checked/Unchecked 事件（WPF 契约），会重复写注册表；
        // 订阅事件前完成初始化赋值并用抑制闸兜底。
        _suppressStyleToggle = true;
        toggle.IsChecked = MenuManagerService.IsWin11ClassicMenuEnabled();
        _suppressStyleToggle = false;
        toggle.Checked += (_, _) => SetStyleAndHint(tokens, toggle, true);
        toggle.Unchecked += (_, _) => SetStyleAndHint(tokens, toggle, false);
        body.Children.Add(toggle);

        // 【诚实告知 2026-09-05】25H2 实证：经典样式本质 = 常驻「显示更多选项」那套老菜单，
        // 只加载传统注册表扩展；新式注册（MSIX 稀疏包 + IExplorerCommand）的菜单项只在
        // Win11 新菜单里存在——切过去后这些软件的右键项会消失（用户实测：新式压缩软件不见、只剩 7-Zip）。
        body.Children.Add(Desc(
            "注意：经典样式只认「传统方式注册」的软件（如 7-Zip）。" +
            "通过微软商店/新方式注册菜单的软件（部分新版压缩软件、终端等）在这套老菜单里不会出现——" +
            "如果切过去后发现某些软件的右键项不见了，把开关切回来即可找回。",
            tokens));

        body.Children.Add(TwoStepButton(tokens, "立刻生效（桌面会闪一下，正常现象）", "确认重启桌面？", RestartExplorerAsync));
        return card;
    }

    private bool _suppressStyleToggle;

    private void SetStyleAndHint(IThemeTokens tokens, CheckBox toggle, bool classic)
    {
        if (_suppressStyleToggle)
        {
            return; // 编程赋值触发的假事件（WPF Checked/Unchecked 在代码置 IsChecked 时同步引发）
        }
        try
        {
            MenuManagerService.SetWin11ClassicMenu(classic);
            ShowStatus(classic ? "已换成经典样式，点下面按钮立刻生效。" : "已换回新样式，点下面按钮立刻生效。", false);
        }
        catch (Exception ex)
        {
            // 审查修复：写失败必须回滚开关显示态，否则开关显示"已开启"而注册表未写 = "开启没有成功"
            _suppressStyleToggle = true;
            toggle.IsChecked = !classic;
            _suppressStyleToggle = false;
            ShowStatus($"切换失败：{ex.Message}", true);
        }
    }

    /// <summary>重启 explorer（注册表改键不重启永远不生效——"切换没成功"的第一根因）。异步化避免 UI 线程阻塞。</summary>
    private async Task RestartExplorerAsync()
    {
        using (Process.Start(new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }))
        {
        }
        // 等 explorer 真正退出再启动，避免半死实例顶住 shell 角色
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                break;
            }
        }
        _ = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        ShowStatus("好了！现在右键就是新样式了。", false);
    }

    // ===== 卡片② 场景中的扩展（主卡片，统一列表） =====

    private UIElement BuildSceneCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("右键菜单里出现的软件", tokens));
        body.Children.Add(Desc("先选一个位置（比如桌面、文件夹），再把不想看到的关掉、想用的打开。BetterDesk 自己的快捷功能也在这里一起管。", tokens));

        // 行1：显示位置下拉 + 搜索
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var scene in MenuManagerService.Scenes)
        {
            _sceneBox.Items.Add(new ComboBoxItem { Content = scene.Display, Tag = scene.Key });
        }
        _sceneBox.SelectedIndex = 0;
        _sceneBox.SelectionChanged += (_, _) => ReloadGroups(tokens);
        WithStyle(_sceneBox, "MacCombo", tokens);
        _sceneBox.Width = 150;
        _sceneBox.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_sceneBox);

        // 敏感位提示：「所有文件/所有对象」影响几乎所有右键菜单，防止误关波及面过大
        var scopeHint = new TextBlock
        {
            FontSize = 11.5,
            Foreground = tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        UpdateScopeHint(scopeHint);
        _sceneBox.SelectionChanged += (_, _) => UpdateScopeHint(scopeHint);
        row.Children.Add(scopeHint);

        var searchBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = tokens.InputBackground,
            BorderBrush = tokens.InputBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _searchBox.Width = 180;
        _searchBox.FontSize = 12.5;
        _searchBox.Background = Brushes.Transparent;
        _searchBox.BorderThickness = new Thickness(0);
        _searchBox.Foreground = tokens.Foreground;
        _searchBox.TextChanged += (_, _) => ReloadGroups(tokens);
        searchBorder.Child = _searchBox;
        row.Children.Add(searchBorder);
        body.Children.Add(row);

        // 左右双栏：左=开关列表，右=右键菜单预览（用户直观看到每个开关控制了菜单里的哪一项）
        _sceneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sceneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        _leftColumn.Children.Add(_groupsHost);
        Grid.SetColumn(_leftColumn, 0);
        _sceneGrid.Children.Add(_leftColumn);

        var previewInner = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        previewInner.Children.Add(new TextBlock
        {
            Text = "受管理项预览",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Margin = new Thickness(0, 0, 0, 2),
        });
        previewInner.Children.Add(new TextBlock
        {
            Text = "真实菜单 = 系统固定项（打开/剪切/复制/删除/发送到/属性…由 Windows 决定，不在此管理范围）+ 下面这些受管理项，顺序由 Windows 决定。灰显带「已关」= 被开关关掉的。",
            FontSize = 11.5,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        previewInner.Children.Add(_previewHost);
        var previewBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = tokens.InputBackground,
            BorderBrush = tokens.InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 4, 4, 4),
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ScrollViewer
            {
                Content = previewInner,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460,
            },
        };
        Grid.SetColumn(previewBorder, 1);
        _sceneGrid.Children.Add(previewBorder);
        body.Children.Add(_sceneGrid);

        _statusText.Margin = new Thickness(0, 10, 0, 0);
        _statusText.FontSize = 12.5;
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusMuted = tokens.MutedForeground;
        _leftColumn.Children.Add(_statusText);

        ReloadGroups(tokens);
        return card;
    }

    private readonly Grid _sceneGrid = new();
    private readonly StackPanel _leftColumn = new();
    private readonly StackPanel _previewHost = new();

    private string CurrentSceneKey =>
        (_sceneBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "DesktopBackground";

    /// <summary>敏感位提示：「所有文件/所有对象」波及所有类型，管理前先让用户知道影响面。</summary>
    private void UpdateScopeHint(TextBlock hint)
    {
        hint.Text = CurrentSceneKey switch
        {
            "AllFiles" => "⚠ 影响所有类型的文件",
            "AllObjects" => "⚠ 影响文件和文件夹（几乎所有右键）",
            "Folder" => "影响所有文件夹",
            _ => string.Empty,
        };
    }

    private void ReloadGroups(IThemeTokens tokens)
    {
        var sceneKey = CurrentSceneKey;
        var keyword = _searchBox.Text.Trim();
        _groupsHost.Children.Clear();

        List<MenuItemInfo> items;
        try
        {
            items = MenuManagerService.Enumerate(sceneKey);
        }
        catch (Exception ex)
        {
            _groupsHost.Children.Add(Desc($"读取失败：{ex.Message}", tokens));
            return;
        }

        var groups = MenuManagerService.GroupExtensions(items);
        var shown = 0;
        foreach (var g in groups)
        {
            var hit = keyword.Length == 0
                || g.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || g.Items.Any(i => i.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                continue;
            }
            _groupsHost.Children.Add(BuildGroupRow(g, tokens));
            shown++;
        }
        if (shown == 0)
        {
            _groupsHost.Children.Add(Desc(
                keyword.Length > 0 ? "没有匹配的扩展或功能。" : "该位置没有可管理的扩展项。", tokens));
        }

        RenderPreview(items, tokens);
    }

    // ===== 右键菜单预览（该位置真实会出现的项；开=正常，关=灰显带「已关」） =====

    private void RenderPreview(List<MenuItemInfo> items, IThemeTokens tokens)
    {
        _previewHost.Children.Clear();
        var groups = MenuManagerService.GroupExtensions(items);
        foreach (var g in groups)
        {
            foreach (var item in g.Items)
            {
                var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };
                var icon = new Image
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Source = MenuItemIconCache.GetForItem(item),
                    Opacity = item.Enabled ? 1.0 : 0.35,
                };
                DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
                row.Children.Add(icon);

                var text = new TextBlock
                {
                    Text = item.Enabled ? item.DisplayName : item.DisplayName + "（已关）",
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = item.Enabled ? tokens.Foreground : tokens.MutedForeground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                row.Children.Add(text);
                _previewHost.Children.Add(row);
            }
        }
        if (_previewHost.Children.Count == 0)
        {
            _previewHost.Children.Add(Desc("（该位置没有菜单项）", tokens));
        }
    }

    // ===== 扩展组行（整体开关 + 来源徽标 + 展开区） =====

    private UIElement BuildGroupRow(MenuExtensionGroup g, IThemeTokens tokens)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Background = Brushes.Transparent,
            BorderBrush = tokens.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        var dock = new StackPanel();

        var header = new DockPanel();
        var expandBtn = new Button
        {
            Content = "▸",
            Width = 26,
            FontSize = 11,
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        DockPanel.SetDock(expandBtn, System.Windows.Controls.Dock.Left);
        header.Children.Add(expandBtn);

        // 「命令项」组保留开关：Git/MobaXterm 等第三方入口 + Windows 部分自带入口都以静态命令注册，
        // 可逆且有备份；命名已从「系统自带项」更正为「命令项」避免用户误判为 Windows 核心而不敢管。
        var groupToggle = WithStyle(new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = MenuManagerService.IsGroupEnabled(g),
        }, "MacToggle", tokens);
        DockPanel.SetDock(groupToggle, System.Windows.Controls.Dock.Right);
        header.Children.Add(groupToggle);

        var badge = BuildBadge(g.Source, tokens);
        DockPanel.SetDock(badge, System.Windows.Controls.Dock.Right);
        header.Children.Add(badge);

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = MenuItemIconCache.GetForGroup(g),
        };
        header.Children.Add(icon);

        var name = new TextBlock
        {
            Text = g.Name,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
        };
        header.Children.Add(name);
        dock.Children.Add(header);

        var detail = new StackPanel { Margin = new Thickness(32, 6, 0, 0), Visibility = Visibility.Collapsed };
        dock.Children.Add(detail);

        var expanded = false;
        expandBtn.Click += (_, _) =>
        {
            expanded = !expanded;
            expandBtn.Content = expanded ? "▾" : "▸";
            detail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (!expanded)
            {
                return;
            }
            switch (g.Source)
            {
                case "BetterDesktop":
                    RenderBetterDeskDetail(detail, g, tokens, showRestore: true);
                    break;
                case "自定义":
                    RenderBetterDeskDetail(detail, g, tokens, showRestore: false);
                    break;
                case "扩展程序":
                    RenderShellexDetail(detail, g, tokens);
                    break;
                default:
                    RenderSystemDetail(detail, g, tokens);
                    break;
            }
        };
        groupToggle.Checked += (_, _) => ToggleGroupAndRefresh(g, true, tokens);
        groupToggle.Unchecked += (_, _) => ToggleGroupAndRefresh(g, false, tokens);

        row.Child = dock;
        return row;
    }

    private static UIElement BuildBadge(string source, IThemeTokens tokens)
    {
        var text = source switch
        {
            "BetterDesktop" => "BetterDesktop",
            "自定义" => "我创建的",
            "扩展程序" => "扩展程序",
            _ => "系统",
        };
        var accent = source == "BetterDesktop" ? tokens.Accent : Brushes.Transparent;
        return new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = accent,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = source == "BetterDesktop" ? tokens.WindowBackground : tokens.MutedForeground,
            },
        };
    }

    // ===== 展开区：BetterDesktop / 自定义（子项 + 单独开关 + 详情 + 删除 + 恢复） =====

    private void RenderBetterDeskDetail(StackPanel host, MenuExtensionGroup g, IThemeTokens tokens, bool showRestore)
    {
        host.Children.Clear();
        foreach (var item in g.Items)
        {
            host.Children.Add(BuildItemRow(item, tokens));
        }
        if (showRestore)
        {
            host.Children.Add(Desc("BetterDesktop 的快捷功能列在上方，可单独开关；删除后可一键恢复默认。", tokens));
            var restore = WithStyle(new Button
            {
                Content = "恢复默认快捷功能",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12.5,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand,
            }, "MacButton", tokens);
            restore.Click += (_, _) =>
            {
                try
                {
                    MenuManagerService.InjectBetterDeskItems();
                    ShowStatus("BetterDesktop 快捷功能已恢复（幂等，不会重复添加）", false);
                }
                catch (Exception ex)
                {
                    ShowStatus($"恢复失败：{ex.Message}", true);
                }
                ReloadGroups(tokens);
            };
            host.Children.Add(restore);
        }
    }

    /// <summary>单项行：单独开关 + 显示名 + 详情（键名/CLSID/路径/命令 + 两段式删除）。</summary>
    private UIElement BuildItemRow(MenuItemInfo item, IThemeTokens tokens)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var row = new DockPanel();

        var toggle = WithStyle(new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = item.Enabled,
        }, "MacToggle", tokens);
        DockPanel.SetDock(toggle, System.Windows.Controls.Dock.Left);
        toggle.Checked += (_, _) => ToggleItemAndRefresh(item, tokens);
        toggle.Unchecked += (_, _) => ToggleItemAndRefresh(item, tokens);
        row.Children.Add(toggle);

        var detailBtn = new Button
        {
            Content = "详情",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(detailBtn, System.Windows.Controls.Dock.Right);
        row.Children.Add(detailBtn);

        var name = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 12.5,
            Foreground = tokens.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        row.Children.Add(name);
        wrap.Children.Add(row);

        // 详情（专业信息折叠；删除为两段式确认，零 MessageBox）
        var detail = new StackPanel { Margin = new Thickness(28, 4, 0, 6), Visibility = Visibility.Collapsed };
        detail.Children.Add(new TextBlock
        {
            Text = $"所在位置：{(item.Origin == "HKCU" ? "当前用户" : "系统（需管理员）")}",
            FontSize = 11.5,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(item.Clsid))
        {
            detail.Children.Add(new TextBlock { Text = $"扩展 ID：{item.Clsid}", FontSize = 11.5, Foreground = tokens.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }
        if (!string.IsNullOrEmpty(item.Command))
        {
            detail.Children.Add(new TextBlock { Text = $"命令：{item.Command}", FontSize = 11.5, Foreground = tokens.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }
        detail.Children.Add(new TextBlock { Text = $"注册表路径：{item.WritePath}", FontSize = 11, Foreground = tokens.MutedForeground, TextWrapping = TextWrapping.Wrap });
        detail.Children.Add(TwoStepButton(tokens, "移除此项", "确认移除？", () =>
        {
            try
            {
                ShowStatus(MenuManagerService.Delete(item), false);
            }
            catch (Exception ex)
            {
                ShowStatus($"移除失败：{ex.Message}", true);
            }
            ReloadGroups(tokens);
            return Task.CompletedTask;
        }));
        wrap.Children.Add(detail);

        detailBtn.Click += (_, _) =>
            detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        return wrap;
    }

    // ===== 展开区：ShellEx（只读预览 + 详情） =====

    private void RenderShellexDetail(StackPanel host, MenuExtensionGroup g, IThemeTokens tokens)
    {
        host.Children.Clear();
        host.Children.Add(Desc("正在读取此扩展的功能清单…（模拟资源管理器调用，仅查看不触发操作）", tokens));
        var clsid = g.Key;
        var sceneKey = g.Items[0].SceneKey;
        _ = LoadPreviewAsync(host, g, clsid, sceneKey, tokens);
    }

    private async System.Threading.Tasks.Task LoadPreviewAsync(
        StackPanel host, MenuExtensionGroup g, string clsid, string sceneKey, IThemeTokens tokens)
    {
        IReadOnlyList<MenuPreviewItem> items;
        try
        {
            items = await _preview.QueryAsync(clsid, sceneKey, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            items = [new MenuPreviewItem($"无法预览：{ex.Message}", 0, false)];
        }

        host.Children.Clear();
        if (items.Count == 0)
        {
            host.Children.Add(Desc("该扩展没有可预览的菜单内容（可能仅支持新式菜单接口）。", tokens));
        }
        else
        {
            foreach (var it in items)
            {
                host.Children.Add(new TextBlock
                {
                    Text = new string('　', Math.Min(it.Depth, 4)) + it.Text,
                    FontSize = 12.5,
                    Foreground = it.IsGroupHeader ? tokens.MutedForeground : tokens.Foreground,
                    Margin = new Thickness(0, 1, 0, 1),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        host.Children.Add(Desc("以上功能由软件动态提供，仅可查看；关闭请使用上方开关。", tokens));
        host.Children.Add(Desc("移除：会删除该扩展的注册信息（自动备份可恢复）。", tokens));
        host.Children.Add(TwoStepButton(tokens, "移除此扩展", "确认移除？", () =>
        {
            var first = g.Items[0];
            try
            {
                ShowStatus(MenuManagerService.Delete(first), false);
            }
            catch (Exception ex)
            {
                ShowStatus($"移除失败：{ex.Message}", true);
            }
            ReloadGroups(tokens);
            return Task.CompletedTask;
        }));
    }

    // ===== 展开区：系统（只读说明） =====

    private void RenderSystemDetail(StackPanel host, MenuExtensionGroup g, IThemeTokens tokens)
    {
        host.Children.Clear();
        host.Children.Add(Desc("以「命令」方式注册的菜单项入口（如 Git Bash here、MobaXterm、PowerShell 窗口等，Windows 部分自带入口也在其中）。每项可单独开关，操作可逆且有备份；不确定来源的项，展开「详情」看命令路径再决定。", tokens));
        foreach (var item in g.Items)
        {
            host.Children.Add(new TextBlock
            {
                Text = $"· {item.DisplayName}",
                FontSize = 12,
                Foreground = tokens.MutedForeground,
                Margin = new Thickness(4, 1, 0, 1),
            });
        }
    }

    // ===== 卡片③ 新建菜单项（高级，默认折叠） =====

    private UIElement BuildNewCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);

        var expandBtn = new Button
        {
            Content = "新建菜单项  ▸",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = tokens.Foreground,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(0),
        };
        body.Children.Add(expandBtn);

        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        content.Children.Add(Desc("高级功能：往系统右键菜单添加自己的命令。", tokens));

        content.Children.Add(Desc("显示位置（右键哪里出现）：", tokens));
        var newScene = new ComboBox();
        foreach (var s in MenuManagerService.Scenes)
        {
            newScene.Items.Add(new ComboBoxItem { Content = s.Display, Tag = s.Key });
        }
        newScene.SelectedIndex = 6; // 所有文件
        WithStyle(newScene, "MacCombo", tokens);
        newScene.Width = 160;
        newScene.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(newScene);

        content.Children.Add(Desc("显示名称：", tokens));
        var nameBox = new TextBox
        {
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12.5,
            Foreground = tokens.Foreground,
            Background = tokens.InputBackground,
            BorderBrush = tokens.InputBorder,
            Margin = new Thickness(0, 0, 0, 8),
        };
        content.Children.Add(nameBox);

        content.Children.Add(Desc("命令（点击菜单项后执行的程序或命令）：", tokens));
        var cmdRow = new DockPanel();
        var cmdBox = new TextBox
        {
            FontSize = 12.5,
            Foreground = tokens.Foreground,
            Background = tokens.InputBackground,
            BorderBrush = tokens.InputBorder,
        };
        cmdRow.Children.Add(cmdBox);
        var pick = WithStyle(new Button
        {
            Content = "选择程序…",
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        }, "MacButton", tokens);
        DockPanel.SetDock(pick, System.Windows.Controls.Dock.Right);
        cmdRow.Children.Add(pick);
        content.Children.Add(cmdRow);

        var create = WithStyle(new Button
        {
            Content = "创建",
            FontSize = 12.5,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
        }, "MacAccentButton", tokens);
        content.Children.Add(create);

        content.Children.Add(Desc("%1 = 右键的那个文件，%V = 右键的那个文件夹。", tokens));

        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择程序",
                Filter = "程序|*.exe;*.bat;*.cmd|所有文件|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                cmdBox.Text = $"\"{dlg.FileName}\" %1";
            }
        };
        create.Click += (_, _) =>
        {
            var sceneKey = (newScene.SelectedItem as ComboBoxItem)?.Tag as string ?? "AllFiles";
            var display = nameBox.Text.Trim();
            var command = cmdBox.Text.Trim();
            if (display.Length == 0 || command.Length == 0)
            {
                ShowStatus("请填写显示名称和命令。", true);
                return;
            }
            try
            {
                var keyName = NextUserMenuKey(sceneKey);
                var created = MenuManagerService.CreateStaticVerb(sceneKey, keyName, display, command);
                ShowStatus($"已创建「{display}」，在右键菜单中立即可见（位置：{(created.Origin == "HKCU" ? "当前用户" : "系统")}）。", false);
                nameBox.Text = string.Empty;
                cmdBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                ShowStatus($"创建失败：{ex.Message}", true);
            }
        };

        expandBtn.Click += (_, _) =>
        {
            var expanded = content.Visibility == Visibility.Visible;
            content.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            expandBtn.Content = expanded ? "新建菜单项  ▸" : "新建菜单项  ▾";
        };
        body.Children.Add(content);
        return card;
    }

    /// <summary>用户新建项键名：UserMenu 前缀 + 递增避让（同场景不覆盖；前缀是「自定义」来源识别锚点）。</summary>
    private static string NextUserMenuKey(string sceneKey)
    {
        var scene = MenuManagerService.Scenes.First(s => s.Key == sceneKey);
        for (var i = 0; ; i++)
        {
            var name = i == 0 ? "UserMenu" : $"UserMenu{i}";
            var check = $@"{MenuManagerService.UserClassesRoot}\{scene.RegPath}\shell\{name}";
            if (!MenuManagerService.KeyExists(check))
            {
                return name;
            }
        }
    }

    // ===== 卡片④ 备份与恢复 =====

    private UIElement BuildBackupCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("备份与恢复", tokens));
        body.Children.Add(Desc("所有启停 / 删除 / 新建操作都会先自动备份。可随时恢复最近一次备份，或打开备份文件夹查看。", tokens));

        body.Children.Add(TwoStepButton(tokens, "恢复最近备份", "确认恢复？", () =>
        {
            var latest = RegTreeBackup.ListBackups().FirstOrDefault();
            if (latest is null)
            {
                ShowStatus("还没有备份记录。", true);
                return Task.CompletedTask;
            }
            try
            {
                var ok = RegTreeBackup.Restore(latest);
                ShowStatus(ok ? $"已恢复备份：{Path.GetFileName(latest)}" : "恢复失败（备份文件可能损坏）。", !ok);
            }
            catch (Exception ex)
            {
                ShowStatus($"恢复失败：{ex.Message}", true);
            }
            ReloadGroups(tokens);
            return Task.CompletedTask;
        }));
        body.Children.Add(TwoStepButton(tokens, "打开备份文件夹", "打开？", () =>
        {
            Directory.CreateDirectory(RegTreeBackup.BackupDirectory);
            Process.Start(new ProcessStartInfo(RegTreeBackup.BackupDirectory) { UseShellExecute = true });
            return Task.CompletedTask;
        }));
        return card;
    }

    // ===== 两段式确认按钮（零 MessageBox） =====

    private Button TwoStepButton(IThemeTokens tokens, string label, string confirm, Func<Task> action)
    {
        var btn = WithStyle(new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 8, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
        }, "MacButton", tokens);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        EventHandler tick = null!;
        tick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= tick;
            btn.Content = label;
        };
        timer.Tick += tick;

        btn.Click += async (_, _) =>
        {
            if (Equals(btn.Content, confirm))
            {
                timer.Stop();
                timer.Tick -= tick;
                btn.Content = label;
                btn.IsEnabled = false;
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    ShowStatus($"操作失败：{ex.Message}", true);
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
            else
            {
                btn.Content = confirm;
                timer.Stop();
                timer.Start();
            }
        };
        return btn;
    }

    // ===== 操作反馈与状态 =====

    private void ToggleGroupAndRefresh(MenuExtensionGroup g, bool enable, IThemeTokens tokens)
    {
        try
        {
            var result = MenuManagerService.ToggleGroup(g, enable);
            ShowStatus(result, result.Contains("失败", StringComparison.Ordinal) || result.Contains("权限", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            ShowStatus($"操作失败：{ex.Message}", true);
        }
        ReloadGroups(tokens);
    }

    private void ToggleItemAndRefresh(MenuItemInfo item, IThemeTokens tokens)
    {
        try
        {
            var result = MenuManagerService.Toggle(item);
            ShowStatus(result, false);
        }
        catch (Exception ex)
        {
            ShowStatus($"操作失败：{ex.Message}", true);
        }
        ReloadGroups(tokens);
    }

    private void ShowStatus(string message, bool isError)
    {
        // 权限自检：系统区（HKLM）项目需要管理员权限；非管理员时给用户明确指引而不是裸报错
        if (isError && !Environment.IsPrivilegedProcess)
        {
            message += "（当前 BetterDesktop 未以管理员身份运行——涉及系统区的项目请右键 BetterDesktop 选「以管理员身份运行」后再试。）";
        }
        _statusText.Text = message;
        _statusText.Foreground = isError
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0x66, 0x68))
            : _statusMuted;
    }

    // ===== 共享 UI helper（分区自包含，不依赖外部样式工厂） =====

    private static UIElement Desc(string text, IThemeTokens tokens) => new TextBlock
    {
        Text = text,
        Foreground = tokens.MutedForeground,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static Border GroupCard(IThemeTokens tokens) => SettingsUi.CreateCard();

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
}
