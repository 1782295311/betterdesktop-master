// BetterDesktop.Shell.ContextMenus — 统一菜单样式工厂
// 全部走 App 级主题令牌（DynamicResource），随主题模式即时切换。
//
// 【实现说明（踩坑记录，禁止复现）】
//   1. 严禁用 FrameworkElementFactory.SetValue(dp, DynamicResourceExtension.ProvideValue(null))
//      构建模板 —— 运行时抛 ArgumentException「ResourceReferenceExpression 不能作为 Background 的
//      有效值」（FrameworkElementFactory.SetValue 不做延迟求值）。正解：XamlReader.Parse 解析
//      XAML 字符串，{DynamicResource} 由 XAML 解析器原生处理。
//   2. 子菜单必须用自建模板（CreateSubmenuStyle）：WPF 默认 MenuItem 模板的下拉 Popup 背板
//      是系统 SystemColors（深色系统=黑块）——在深色壁纸上"点击新建没反应"实为子菜单弹出
//      了但黑底不可辨（2026-09-02 实测）。自建模板的 PART_Popup 内铺 PopupBackground 令牌。
//   3. 顶层普通项同样用自建模板：默认 TopLevelItem 模板无勾选列（IsChecked 不渲染）且
//      悬停是系统色。自建模板带 ✓ 勾选列（Radio/Toggle 用）+ PopupItemHover 悬停。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>统一右键菜单样式工厂（主题令牌驱动，随主题模式即时切换）。</summary>
public static class MenuStyling
{
    /// <summary>顶层普通项（Command/Toggle/Radio）：勾选列 + 悬停令牌 + 禁用降态。</summary>
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
            <Setter Property="MaxWidth" Value="360"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="{x:Type MenuItem}">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="4">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="18"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <Grid Grid.Column="0" Width="18">
                                    <ContentPresenter ContentSource="Icon" Width="16" Height="16"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    <TextBlock x:Name="Check" Text="✓" FontSize="12"
                                               VerticalAlignment="Center" HorizontalAlignment="Center"
                                               Foreground="{DynamicResource ThemeForeground}"
                                               Visibility="Collapsed"/>
                                </Grid>
                                <ContentPresenter Grid.Column="1" ContentSource="Header"
                                                  VerticalAlignment="Center" Margin="10,0,8,0"
                                                  RecognizesAccessKey="True"/>
                                <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}"
                                           VerticalAlignment="Center" Margin="12,0,10,0"
                                           FontSize="11.5" Foreground="{DynamicResource ThemeMutedForeground}"/>
                            </Grid>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PopupItemHover}"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    /// <summary>子菜单宿主项（Submenu）：右侧箭头 + PART_Popup（令牌背板，右弹出）。</summary>
    private const string SubmenuStyleXaml =
        """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type MenuItem}">
            <Setter Property="Height" Value="30"/>
            <Setter Property="Margin" Value="2,1,2,1"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource ThemeForeground}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FontSize" Value="12.5"/>
            <Setter Property="MaxWidth" Value="360"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="{x:Type MenuItem}">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="4">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="18"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ContentPresenter Grid.Column="0" ContentSource="Icon" Width="16" Height="16"
                                                  HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                <ContentPresenter Grid.Column="1" ContentSource="Header"
                                                  VerticalAlignment="Center" Margin="10,0,8,0"
                                                  RecognizesAccessKey="True"/>
                                <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}"
                                           VerticalAlignment="Center" Margin="12,0,6,0"
                                           FontSize="11.5" Foreground="{DynamicResource ThemeMutedForeground}"/>
                                <TextBlock Grid.Column="3" Text="›" FontSize="15"
                                           VerticalAlignment="Center" Margin="4,0,8,0"
                                           Foreground="{DynamicResource ThemeMutedForeground}"/>
                                <Popup x:Name="PART_Popup" Grid.Column="0"
                                       Placement="Right" VerticalOffset="-6"
                                       AllowsTransparency="True" Focusable="False"
                                       PopupAnimation="Fade"
                                       IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}">
                                    <Border Background="{DynamicResource PopupBackground}"
                                            BorderBrush="{DynamicResource PopupBorder}"
                                            BorderThickness="1" CornerRadius="8" Padding="4"
                                            SnapsToDevicePixels="True" UseLayoutRounding="True"
                                            MinWidth="140" MaxWidth="280">
                                        <StackPanel IsItemsHost="True"/>
                                    </Border>
                                </Popup>
                            </Grid>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PopupItemHover}"/>
                            </Trigger>
                            <Trigger Property="IsSubmenuOpen" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PopupItemHover}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    /// <summary>分隔线主题样式。</summary>
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

    /// <summary>菜单项主题样式（普通项：自建模板，勾选列 + 悬停令牌）。</summary>
    public static Style CreateItemStyle() => (Style)XamlReader.Parse(ItemStyleXaml);

    /// <summary>子菜单宿主项样式（自建模板：箭头 + 令牌背板 PART_Popup，右弹出）。</summary>
    public static Style CreateSubmenuStyle() => (Style)XamlReader.Parse(SubmenuStyleXaml);

    /// <summary>分隔线主题样式。</summary>
    public static Style CreateSeparatorStyle() => (Style)XamlReader.Parse(SeparatorStyleXaml);

    /// <summary>创建自绘主题 ContextMenu（旧回退路径专用；根模板 + 菜单项样式 + 分隔线样式）。</summary>
    public static ContextMenu CreateMenu()
    {
        var menu = new ContextMenu
        {
            Template = SubmenuRootTemplate(),
            ItemContainerStyle = CreateItemStyle(),
            HasDropShadow = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        return menu;
    }

    /// <summary>旧路径 ContextMenu 根模板（令牌圆角面板 + ItemsPresenter）。</summary>
    private static ControlTemplate SubmenuRootTemplate() => (ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ContextMenu}">
            <Border Background="{DynamicResource PopupBackground}"
                    BorderBrush="{DynamicResource PopupBorder}"
                    BorderThickness="1" CornerRadius="8" Padding="4"
                    SnapsToDevicePixels="True" UseLayoutRounding="True">
                <ItemsPresenter/>
            </Border>
        </ControlTemplate>
        """);
}
