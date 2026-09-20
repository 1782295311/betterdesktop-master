// BetterDesktop.Shell.ContextMenus — 「系统集成」设置分区（2026-09-17 安装器级落地）
//
// 【为什么单开一页】"注册到系统"此前散落在多处（右键菜单页只管 menu 内容、系统页只管两项自启），
//   用户遇到"右键项不见了 / 开机没起来"时没有一个地方能一眼看到全貌：
//   装在哪、右键扩展注册没有、Win11 新菜单包在不在、哪些自启真的会生效。
//   本页把 SystemIntegrationRegistrar 采到的状态原样摊开，并把可逆动作（重新注册 / 修复 / 注销 / 重启桌面）
//   放在同一处 —— 与托盘「系统集成」菜单、安装脚本共用同一个 CLI 实现，三处永远说同一件事。
//
// 纪律（沿用本包既有分区约定）：**零 MessageBox**（用内嵌状态行 + 两段式确认按钮）。

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.ContextMenus.Sections;

/// <summary>「系统集成」分区：安装位置 / 右键两路注册状态 / 开机自启 / 快照 + 注册类动作。</summary>
public sealed class SystemIntegrationSection : ISettingsSection
{
    public string Title => "系统集成";

    public string? IconKey => null;

    private readonly TextBlock _statusText = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private readonly TextBlock _actionHint = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = "这里看的是「本程序在系统里注册成什么样了」：装在哪、右键菜单扩展有没有注册、"
                 + "开机自启会不会真的生效。出问题时先点一次「修复注册」。",
            Foreground = tokens.MutedForeground,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(BuildStatusCard(tokens));
        panel.Children.Add(BuildActionCard(tokens));
        return panel;
    }

    // ===== 卡片① 当前状态 =====

    private UIElement BuildStatusCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("当前状态", tokens));
        body.Children.Add(Desc("「右键扩展」= 经典菜单里的入口；「新菜单包」= Windows 11 新版右键菜单第一层的入口。", tokens));
        body.Children.Add(_statusText);

        var refresh = new Button
        {
            Content = "刷新状态",
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 10, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        ApplyStyle(refresh, "MacButton", tokens);
        refresh.Click += (_, _) => RefreshStatus(tokens);
        body.Children.Add(refresh);

        RefreshStatus(tokens);
        return card;
    }

    private void RefreshStatus(IThemeTokens tokens)
    {
        var status = SystemIntegrationRegistrar.GetStatus();
        _statusText.Foreground = tokens.Foreground;

        var msix = status.MsixRegistered switch
        {
            true => $"已注册（{status.MsixVersion}）",
            false => "未注册（Windows 11 新菜单第一层看不到入口）",
            _ => "未知（查询不可用，不影响经典菜单）",
        };

        var lines = new[]
        {
            $"安装位置：{status.InstallRoot ?? "未用安装器安装（当前是开发/便携形态）"}",
            $"版本：{status.Version ?? "?"}（构建 {status.Build ?? "?"}）",
            $"右键扩展：{(status.ComRegistered ? "已注册" : "未注册")}"
                + (status.ComPathDrifted ? " · 路径与当前版本不一致（建议修复）" : string.Empty),
            $"  注册指向：{status.ComRegisteredPath ?? "(空)"}",
            $"  当前部署：{status.ComDllPath ?? "(未部署原生扩展)"}",
            $"新菜单包：{msix}",
            $"  包位置：{status.MsixLocation ?? "(未知)"}",
            $"菜单快照：{(status.SnapshotPresent ? "存在" : "缺失（右键里不会出现我们的项）")}",
            $"开机自启：托盘 {(status.TrayAutostart ? "✓" : "✗")} · 看门狗 {(status.WatchdogAutostart ? "✓" : "✗")} · 主程序 {(status.ShellAutostart ? "✓" : "✗")}",
        };

        _statusText.Text = string.Join(Environment.NewLine, lines);
    }

    // ===== 卡片② 可逆动作 =====

    private UIElement BuildActionCard(IThemeTokens tokens)
    {
        var card = GroupCard(tokens);
        var body = CardBody(card);
        body.Children.Add(TitleBlock("注册 / 修复 / 注销", tokens));
        body.Children.Add(Desc("改动即时写注册表；右键菜单需要重启桌面才会刷新（下面有按钮）。", tokens));

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(TwoStepButton(tokens, "注册到系统", "确认注册？", () =>
        {
            var ok = SystemIntegrationRegistrar.Register(out var error);
            Report(tokens, ok, ok ? "已注册：右键扩展 + 托盘开机自启。" : error ?? "注册失败");
            return Task.CompletedTask;
        }));

        row.Children.Add(TwoStepButton(tokens, "修复注册", "确认修复？", () =>
        {
            var ok = SystemIntegrationRegistrar.Repair(out var error);
            Report(tokens, ok, ok ? "已按当前状态补缺（幂等，可反复点）。" : error ?? "修复失败");
            return Task.CompletedTask;
        }));

        row.Children.Add(TwoStepButton(tokens, "注销右键扩展", "确认注销？", () =>
        {
            // 刻意**不动开机自启**：注销菜单 ≠ 卸载程序，自启归"系统"页与卸载脚本管。
            var ok = SystemIntegrationRegistrar.Unregister(includeAutostart: false, out var error);
            Report(tokens, ok, ok ? "已注销右键扩展并删除菜单快照（自启保留）。" : error ?? "注销失败");
            return Task.CompletedTask;
        }));

        row.Children.Add(TwoStepButton(tokens, "重启桌面", "确认重启？", RestartExplorerAsync));
        body.Children.Add(row);

        _actionHint.Foreground = tokens.MutedForeground;
        _actionHint.Text = "提示：注销后 Windows 11 的「显示更多选项」里也不再有我们的项，但程序本身和开机自启都还在。";
        body.Children.Add(_actionHint);
        return card;
    }

    private void Report(IThemeTokens tokens, bool ok, string message)
    {
        _actionHint.Text = message;
        _actionHint.Foreground = ok ? tokens.MutedForeground : ThemeBrushes.Get("StatusDanger");
        RefreshStatus(tokens);
    }

    /// <summary>重启 explorer：注册表改了不重启永远不生效（"改了没反应"的第一根因）。</summary>
    private async Task RestartExplorerAsync()
    {
        using (Process.Start(new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }))
        {
        }

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                break;
            }
        }

        _ = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        _actionHint.Text = "桌面已重启，现在右键就是最新状态。";
    }

    // ===== UI helper（分区自包含，与 MenuManagerSection 同风格） =====

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

    private static void ApplyStyle(FrameworkElement element, string key, IThemeTokens tokens)
    {
        if (Application.Current?.Resources[key] is Style style)
        {
            element.Style = style;
        }
        else if (element is Control control)
        {
            control.Foreground = ThemeBrushes.Get("ControlForeground");
        }
    }

    /// <summary>两段式确认按钮（零 MessageBox）：第一次点变"确认"，3 秒内再点才执行。</summary>
    private static Button TwoStepButton(IThemeTokens tokens, string label, string confirm, Func<Task> action)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        ApplyStyle(btn, "MacButton", tokens);

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
                    // 失败必须可见（不能静默）：按钮区无状态行，故回退到按钮文案上
                    btn.Content = "失败：" + ex.Message;
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
}
