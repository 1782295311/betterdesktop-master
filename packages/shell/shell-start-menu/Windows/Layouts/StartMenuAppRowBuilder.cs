using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// 跨布局共用的应用行构建工具：图标 + 名称 + 悬浮底 + 右键菜单 + 左键启动。
/// 供 Win10 主菜单的「所有应用」分组列表与新「所有应用」浏览视图共用，
/// 保证各行视觉与交互完全一致（图标 24px / 行高 32px / 悬浮底 / 圆角 4px / 像素对齐）。
/// </summary>
internal static class StartMenuAppRowBuilder
{
    /// <summary>把应用名归一化为索引字母：拉丁字母取大写首字母；非拉丁首字符统一归入 “#”（避免按字分组导致索引爆炸）。</summary>
    public static string NormalizeGroupLetter(string? name)
    {
        if (name is { Length: > 0 })
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z')
            {
                return c.ToString();
            }
        }

        return "#";
    }

    /// <summary>构建应用行：24px 图标 + 名称，32px 高，悬浮浅底，右键菜单，左键启动并关闭菜单。</summary>
    public static FrameworkElement BuildAppRow(AppItem app, string displayName, StartMenuService service, StartMenuPalette palette)
    {
        var row = new Grid
        {
            Height = 32,
            Cursor = Cursors.Hand,
            Tag = app,
            Margin = new Thickness(4, 1, 4, 1)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconPad = new Grid { Width = 34 };
        iconPad.Children.Add(icon);
        Grid.SetColumn(iconPad, 0);
        row.Children.Add(iconPad);

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = service.ThemeTokens?.FontSizeBody ?? 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = palette.Foreground
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        row.MouseLeftButtonUp += (_, _) =>
        {
            service.ActivateOrLaunch(app);
            service.Hide();
        };
        row.ContextMenu = AppItemActions.BuildContextMenu(app, service);

        var host = new Border { CornerRadius = new CornerRadius(4), Child = row };
        host.MouseEnter += (_, _) => host.Background = palette.RowHover;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;

        _ = LoadIconAsync(app, icon, service);
        return host;
    }

    /// <summary>异步加载应用图标（失败静默退回留空）。</summary>
    private static async System.Threading.Tasks.Task LoadIconAsync(AppItem app, Image target, StartMenuService service)
    {
        try
        {
            var icon = await service.GetIconAsync(app, CancellationToken.None);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标加载失败静默（M10），不阻塞列表渲染。
        }
    }
}