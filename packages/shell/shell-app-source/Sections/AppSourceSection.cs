using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.AppSource.Sections;

/// <summary>
/// 设置中心「应用来源」分区：口袋目录（应用扫描目录）+ 桌面快捷方式纳入。
/// <para>由 <c>AppSourcePlugin</c> 自贡献（各功能域自己注册分区，不在 shell-settings 里硬编码本包设置）。</para>
/// <para><b>UI 自包含</b>：本包未引用 shell-settings，故卡片与行控件就地实现
/// （与 <c>DesktopSection</c> / <c>MenuBarSection</c> 同一风格：各自内置 helper，互不依赖）。</para>
/// </summary>
internal sealed class AppSourceSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "应用来源";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(BuildScanRootsCard(settings, tokens));
        panel.Children.Add(BuildDesktopCard(settings, tokens));
        return panel;
    }

    // ===== 卡片：应用扫描目录（口袋目录） =====

    private static Border BuildScanRootsCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = CreateCard(tokens);
        var body = CardBody(card);

        body.Children.Add(TitleBlock("应用扫描目录", tokens));
        body.Children.Add(HintBlock(
            "解压即用、不安装的便携工具（MAA / OneDragon 一类）不在任何标准位置，默认扫不到；"
            + "把它们所在的目录加到这里，就会出现在「全程序模式」列表中。留空 = 不扫描额外目录。",
            tokens));

        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        // 落盘：从行容器还原成路径列表（行内 TextBox 是唯一状态源，见 BuildRootRow）
        void Save()
        {
            var paths = new List<string>();
            foreach (var child in rows.Children)
            {
                if (child is DockPanel { Tag: TextBox box } && !string.IsNullOrWhiteSpace(box.Text))
                {
                    paths.Add(box.Text.Trim());
                }
            }

            settings.Set(AppSourceSettings.ExtraRootsKey, AppSourceSettings.SerializeRoots(paths));
        }

        void AddRow(string path) => rows.Children.Add(BuildRootRow(path, rows, Save, tokens));

        foreach (var root in AppSourceSettings.ParseRoots(settings.Get<string>(AppSourceSettings.ExtraRootsKey)))
        {
            AddRow(root);
        }

        body.Children.Add(rows);

        var add = new Button
        {
            Content = "＋ 添加目录…",
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "选择一个目录中的任意文件，取该文件所在目录作为新的扫描目录",
        };
        add.Click += (_, _) =>
        {
            // WPF 无原生目录选择框（既有先例：LeftDockSection 同样「选文件取目录」）
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择该目录下的任意文件（取它所在目录）",
                Filter = "任意文件|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(directory))
            {
                AddRow(directory);
                Save();
            }
        };

        body.Children.Add(add);
        return card;
    }

    /// <summary>单行目录：可编辑路径 + 移除（失焦/移除/新增即落盘，避免逐键写设置轰炸订阅方）。</summary>
    private static DockPanel BuildRootRow(string path, StackPanel host, Action save, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };

        var remove = new Button
        {
            Content = "✕",
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "移除该目录",
        };
        // 全限定：`Dock` 会被解析成 BetterDesktop.Shell.Dock 命名空间
        DockPanel.SetDock(remove, System.Windows.Controls.Dock.Right);

        var box = new TextBox
        {
            Text = path,
            FontSize = tokens.FontSizeCaption,
            Padding = new Thickness(6, 4, 6, 4),
            Background = tokens.InputBackground,
            Foreground = tokens.Foreground,
            BorderBrush = tokens.InputBorder,
            ToolTip = "目录路径（可直接编辑，失焦即保存）",
        };
        box.LostFocus += (_, _) => save();

        row.Tag = box;
        row.Children.Add(remove);
        row.Children.Add(box);

        remove.Click += (_, _) =>
        {
            host.Children.Remove(row);
            save();
        };

        return row;
    }

    // ===== 卡片：桌面快捷方式 =====

    private static Border BuildDesktopCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = CreateCard(tokens);
        var body = CardBody(card);

        body.Children.Add(TitleBlock("桌面快捷方式", tokens));

        var toggle = new CheckBox
        {
            Content = "把桌面快捷方式纳入应用列表",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13,
            Foreground = tokens.Foreground,
            IsChecked = settings.Get(AppSourceSettings.ScanDesktopShortcutsKey, true),
        };
        toggle.Checked += (_, _) => settings.Set(AppSourceSettings.ScanDesktopShortcutsKey, true);
        toggle.Unchecked += (_, _) => settings.Set(AppSourceSettings.ScanDesktopShortcutsKey, false);
        body.Children.Add(toggle);

        body.Children.Add(HintBlock(
            "桌面是手动启动应用的主要入口，默认纳入。下载目录默认不纳入（噪音太大）。"
            + "关掉它不影响开始菜单与已安装程序的扫描结果。",
            tokens));

        return card;
    }

    // ===== UI helper（Section 自包含，不依赖 shell-settings 的实现程序集） =====

    private static Border CreateCard(IThemeTokens tokens) => new()
    {
        Background = tokens.PanelBackground,
        BorderBrush = tokens.CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(tokens.CornerRadius),
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 10),
        Effect = tokens.CardShadow,
        Child = new Border
        {
            Padding = new Thickness(8),
            Child = new StackPanel(),
        },
    };

    private static StackPanel CardBody(Border card) => (StackPanel)((Border)card.Child!).Child!;

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };
}
