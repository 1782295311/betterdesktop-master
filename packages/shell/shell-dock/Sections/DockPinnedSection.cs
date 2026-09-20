using System;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.Dock.Sections;

/// <summary>
/// 设置中心「Dock 固定项」分区：固定项健康态的**可观测**出口。
/// <para>
/// 为什么要有这一页：此前固定项失效**完全不可见**——渲染层静默让位（不显示），
/// 用户只感知「图标没了」，既不知道原因也做不了什么。这里把三态摆出来，并给
/// 「重新绑定…」（手动指认）与「清理失效项」（删除持久化数据）两个动作。
/// </para>
/// <para>由 DockPlugin 自贡献（各功能域自己注册分区）。服务经 <see cref="DockAppsServiceBridge"/> 取。</para>
/// </summary>
internal sealed class DockPinnedSection : ISettingsSection
{
    /// <inheritdoc />
    public string Title => "Dock 固定项";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var card = SettingsUi.CreateCard();
        var body = (StackPanel)((Border)card.Child!).Child!;

        body.Children.Add(TitleBlock("固定项健康状态", tokens));
        body.Children.Add(HintBlock(
            "「已失效」= 原路径不存在且各级重绑都没找到，视为已卸载（快照会保留，重装回原路径即自动回来）。"
            + "「已自愈」= 原路径失效但已自动改指到新路径（如 Edge/Chrome 更新换了版本目录）。",
            tokens));

        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(rows);

        var summary = new TextBlock
        {
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(summary);

        void Rebuild()
        {
            rows.Children.Clear();

            var service = DockAppsServiceBridge.Current;
            if (service is null)
            {
                summary.Text = "Dock 组件当前未启用，无法读取固定项。";
                return;
            }

            var health = service.GetPinnedHealth();
            var orphaned = 0;
            foreach (var item in health)
            {
                if (item.State == PinnedHealthState.Orphaned)
                {
                    orphaned++;
                }

                rows.Children.Add(BuildRow(item, Rebuild, tokens));
            }

            summary.Text = health.Count == 0
                ? "还没有固定任何应用。"
                : $"共 {health.Count} 项，其中已失效 {orphaned} 项。";
        }

        var refresh = new Button
        {
            Content = "刷新",
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 10, 8, 0),
        };
        refresh.Click += (_, _) => Rebuild();

        var purge = new Button
        {
            Content = "清理失效项",
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "删除所有「已失效」固定项的持久化数据（不只是让位）。重新安装后不会再自动回来。",
        };
        purge.Click += (_, _) =>
        {
            DockAppsServiceBridge.Current?.PurgeOrphaned();
            Rebuild();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(purge);
        actions.Children.Add(refresh);
        body.Children.Add(actions);

        Rebuild();
        panel.Children.Add(card);
        return panel;
    }

    /// <summary>单行：名称 + 健康态 + 当前路径/原因；失效与自愈项给「重新绑定…」。</summary>
    private static UIElement BuildRow(PinnedHealth health, Action rebuild, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };

        if (health.State != PinnedHealthState.Healthy)
        {
            var rebind = new Button
            {
                Content = "重新绑定…",
                Padding = new Thickness(10, 2, 10, 2),
                ToolTip = "手动指认该应用现在的可执行文件",
            };
            DockPanel.SetDock(rebind, System.Windows.Controls.Dock.Right);
            rebind.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"重新绑定「{health.Name}」",
                    CheckFileExists = true,
                    Filter = "应用与快捷方式|*.exe;*.bat;*.cmd;*.com;*.msc;*.lnk;*.url;*.appref-ms|所有文件|*.*",
                };

                if (dialog.ShowDialog() == true)
                {
                    DockAppsServiceBridge.Current?.RebindTo(health.Id, dialog.FileName);
                    rebuild();
                }
            };
            row.Children.Add(rebind);
        }

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = $"{health.Name}　【{StateText(health.State)}】",
            FontSize = 13,
            Foreground = tokens.Foreground,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = health.CurrentPath ?? health.Detail,
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(text);

        return row;
    }

    private static string StateText(PinnedHealthState state) => state switch
    {
        PinnedHealthState.Healthy => "正常",
        PinnedHealthState.Healable => "已自愈",
        _ => "已失效",
    };

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
