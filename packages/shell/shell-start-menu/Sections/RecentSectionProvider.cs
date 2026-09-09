using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Sections;

/// <summary>
/// 最近程序栏目（右栏扩展点示例）：展示最近使用的程序，点击即启动/激活。
/// 数据来自 IRecentItemsService（经 StartMenuService.GetRecentPrograms）。
/// </summary>
public sealed class RecentSectionProvider : IStartMenuSectionProvider
{
    /// <inheritdoc />
    public string Name => "recent";

    /// <inheritdoc />
    public FrameworkElement BuildSection(IStartMenuDataService service)
    {
        // 栏目显隐开关（startmenu.show-recent-programs），设置分区热更新。
        if (!service.Settings.Get("startmenu.show-recent-programs", true))
        {
            return new StackPanel { Visibility = Visibility.Collapsed };
        }

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock
        {
            Text = "最近",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var items = service.GetRecentPrograms(service.GetRecentCount());
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（暂无最近程序）",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        foreach (var item in items)
        {
            var app = item.AppItem;
            var button = new Button
            {
                Content = item.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 3, 6, 3)
            };
            button.Click += (_, _) =>
            {
                if (app is not null)
                {
                    service.ActivateOrLaunch(app);
                    service.Hide();
                }
            };
            panel.Children.Add(button);
        }

        return panel;
    }
}
