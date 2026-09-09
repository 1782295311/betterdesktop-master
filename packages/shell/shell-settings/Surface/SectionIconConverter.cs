using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BetterDesktop.Shell.Settings.Surface;

/// <summary>
/// 设置导航栏图标：把分区标题映射为 24×24 网格上的矢量轮廓（StreamGeometry，描边渲染）。
/// <para>
/// 为什么按标题映射：<see cref="Contracts.ISettingsSection.IconKey"/> 的所有现存实现都返回
/// <c>null</c>，导航栏拿不到可用的图标标识，此前只能给每个分区显示同一个字符齿轮——
/// 既无法区分分区，也把「图标」退化成了字符（字符图标会随字体/系统缺失而变形）。
/// 改为矢量轮廓后：可区分、可随前景色变化、缩放不失真。未知分区统一回落齿轮。
/// </para>
/// </summary>
public sealed class SectionIconConverter : IValueConverter
{
    // 齿轮（默认/兜底）：圆 + 8 根刻度
    private const string Gear =
        "M12,8.5 a3.5,3.5 0 1,0 0,7 a3.5,3.5 0 1,0 0,-7 " +
        "M12,2.6 L12,5 M12,19 L12,21.4 M2.6,12 L5,12 M19,12 L21.4,12 " +
        "M5.3,5.3 L7,7 M17,17 L18.7,18.7 M18.7,5.3 L17,7 M7,17 L5.3,18.7";

    // 水滴（主题/外观）
    private const string Droplet =
        "M12,3.2 C12,3.2 5.2,10.2 5.2,14.4 A6.8,6.8 0 0,0 18.8,14.4 C18.8,10.2 12,3.2 12,3.2 Z";

    // 底部圆角条 + 三个圆点（Dock）
    private const string Dock =
        "M3,9 h18 a2,2 0 0,1 2,2 v4 a2,2 0 0,1 -2,2 h-18 a2,2 0 0,1 -2,-2 v-4 a2,2 0 0,1 2,-2 z " +
        "M4.8,13 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0 " +
        "M10.8,13 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0 " +
        "M16.8,13 a1.2,1.2 0 1,0 2.4,0 a1.2,1.2 0 1,0 -2.4,0";

    // 显示器（桌面）
    private const string Monitor =
        "M2.5,4.5 h19 a1.5,1.5 0 0,1 1.5,1.5 v9 a1.5,1.5 0 0,1 -1.5,1.5 h-19 " +
        "a1.5,1.5 0 0,1 -1.5,-1.5 v-9 a1.5,1.5 0 0,1 1.5,-1.5 z M8.5,20 h7 M12,16.5 v3.5";

    // 顶部条 + 条目（菜单栏）
    private const string MenuBar = "M1,6 h22 M4,12 h7 M14,12 h6 M4,17.5 h10";

    // 2×2 磁贴（开始菜单）
    private const string Tiles =
        "M3.5,3.5 h7 v7 h-7 z M13.5,3.5 h7 v7 h-7 z M3.5,13.5 h7 v7 h-7 z M13.5,13.5 h7 v7 h-7 z";

    // 菜单项列表 + 展开箭头（右键菜单）
    private const string ContextMenu = "M4,6 h16 M4,11 h16 M4,16 h9 M17.5,13.5 l3.5,3.5 -3.5,3.5";

    // 底部任务栏（任务栏外观）
    private const string Taskbar =
        "M1,14 h22 v6 a2,2 0 0,1 -2,2 h-18 a2,2 0 0,1 -2,-2 z M5,17.6 h4 M11,17.6 h4 M17,17.6 h2";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["设置"] = Gear,
        ["主题"] = Droplet,
        ["左侧 Dock"] = Dock,
        ["桌面"] = Monitor,
        ["菜单栏"] = MenuBar,
        ["开始菜单"] = Tiles,
        ["右键菜单"] = ContextMenu,
        ["任务栏外观"] = Taskbar,
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    /// <summary>标题 → 矢量几何。未登记或解析失败一律回落齿轮，绝不抛异常拖垮导航栏渲染。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var title = value as string ?? string.Empty;
        var data = Paths.TryGetValue(title, out var found) ? found : Gear;

        if (Cache.TryGetValue(data, out var cached)) return cached;

        Geometry geometry;
        try
        {
            geometry = Geometry.Parse(data);
        }
        catch (Exception)
        {
            geometry = Geometry.Parse(Gear);
        }

        Cache[data] = geometry;
        return geometry;
    }

    /// <summary>不支持反向转换（图标仅用于展示）。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
