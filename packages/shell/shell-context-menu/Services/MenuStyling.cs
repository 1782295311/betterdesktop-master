// BetterDesktop.Shell.ContextMenu — 统一菜单样式工厂
// 迁移自 shell-desktop/DesktopMenuStyling（internal → public，供全部表面共用）：
//   - 根模板：PopupBackground 底 + PopupBorder 描边 + 圆角，悬停高亮 PopupItemHover，
//     前景 ThemeForeground，全部走 App 级主题令牌（DynamicResource），随主题模式即时切换。
//   - MenuItem：统一行高 + 悬停高亮（IsHighlighted 触发器）+ 分隔线（ThemeSeparator）。
//   - 保留 WPF ContextMenu 的定位/失焦/Esc 行为，仅换呈现。
//
// 【实现说明（踩坑记录，禁止复现）】
//   严禁用 FrameworkElementFactory.SetValue(dp, DynamicResourceExtension.ProvideValue(null))
//   构建模板 —— 运行时抛 ArgumentException「ResourceReferenceExpression 不能作为 Background 的
//   有效值」（FrameworkElementFactory.SetValue 不做延迟求值）。正解：XamlReader.Parse 解析
//   XAML 字符串，{DynamicResource} 由 XAML 解析器原生处理。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>统一右键菜单样式工厂（主题令牌驱动，随主题模式即时切换）。</summary>
public static class MenuStyling
{
    private const string TemplateXaml =
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ContextMenu}">
            <Border Background="{DynamicResource PopupBackground}"
                    BorderBrush="{DynamicResource PopupBorder}"
                    BorderThickness="1"
                    CornerRadius="8"
                    Padding="4"
                    SnapsToDevicePixels="True"
                    UseLayoutRounding="True">
                <ItemsPresenter/>
            </Border>
        </ControlTemplate>
        """;

    private const string ItemStyleXaml =
        """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type MenuItem}">
            <Setter Property="Height" Value="30"/>
            <Setter Property="Padding" Value="10,0,10,0"/>
            <Setter Property="Margin" Value="2,1,2,1"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource ThemeForeground}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FontSize" Value="12.5"/>
            <Style.Triggers>
                <Trigger Property="IsHighlighted" Value="True">
                    <Setter Property="Background" Value="{DynamicResource PopupItemHover}"/>
                </Trigger>
            </Style.Triggers>
        </Style>
        """;

    private const string SeparatorStyleXaml =
        """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type Separator}">
            <Setter Property="Height" Value="1"/>
            <Setter Property="Margin" Value="8,4,8,4"/>
            <Setter Property="Background" Value="{DynamicResource ThemeSeparator}"/>
        </Style>
        """;

    /// <summary>创建自绘主题 ContextMenu（根模板 + 菜单项样式 + 分隔线样式）。</summary>
    public static ContextMenu CreateMenu()
    {
        var menu = new ContextMenu
        {
            Template = (ControlTemplate)XamlReader.Parse(TemplateXaml),
            ItemContainerStyle = (Style)XamlReader.Parse(ItemStyleXaml),
            HasDropShadow = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Resources =
            {
                [typeof(Separator)] = (Style)XamlReader.Parse(SeparatorStyleXaml)
            }
        };
        return menu;
    }
}
