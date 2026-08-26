using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Sections;

/// <summary>
/// 电源栏目（复刻 Open-Shell 电源按钮区）：关机 / 重启 / 睡眠 / 锁定。
/// 显隐由 startmenu.show-power 控制（默认 true），设置分区热更新。
/// </summary>
public sealed class PowerSectionProvider : IStartMenuSectionProvider
{
    /// <inheritdoc />
    public string Name => "power";

    /// <inheritdoc />
    public FrameworkElement BuildSection(StartMenuService service)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (!service.Settings.Get("startmenu.show-power", true))
        {
            panel.Visibility = Visibility.Collapsed;
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = "电源",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        AddItem(panel, "⏻ 关机", () => PowerCommands.Shutdown());
        AddItem(panel, "↻ 重启", () => PowerCommands.Restart());
        AddItem(panel, "◔ 睡眠", () => PowerCommands.Sleep());
        AddItem(panel, "🔒 锁定", () => PowerCommands.Lock());
        return panel;
    }

    private static void AddItem(StackPanel panel, string label, Action action)
    {
        var item = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 3, 0, 3)
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                action();
            }
            catch
            {
                // 电源操作失败静默（M10）。
            }
        };
        panel.Children.Add(item);
    }
}
