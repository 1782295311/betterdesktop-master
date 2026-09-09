using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Sections;

/// <summary>
/// 固定位置栏目（复刻 Open-Shell "Special Items" 的用户文件区）：
/// 文档 / 下载 / 图片 经 explorer 直达，设置打开 BetterDesktop 设置窗口。
/// 显隐由 startmenu.show-places 控制（默认 true），设置分区热更新。
/// </summary>
public sealed class PlacesSectionProvider : IStartMenuSectionProvider
{
    /// <inheritdoc />
    public string Name => "places";

    /// <inheritdoc />
    public FrameworkElement BuildSection(IStartMenuDataService service)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (!service.Settings.Get("startmenu.show-places", true))
        {
            panel.Visibility = Visibility.Collapsed;
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = "位置",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        AddItem(panel, "📄 文档", OpenMyDocuments);
        AddItem(panel, "⬇ 下载", OpenDownloads);
        AddItem(panel, "🖼 图片", OpenMyPictures);
        AddItem(panel, "⚙ 设置", service.OpenSettings);
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
                // 打开失败静默（M10）。
            }
        };
        panel.Children.Add(item);
    }

    private static void OpenMyDocuments()
        => Process.Start("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    private static void OpenDownloads()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Process.Start("explorer.exe", Directory.Exists(downloads) ? downloads : "shell:Downloads");
    }

    private static void OpenMyPictures()
        => Process.Start("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
}
