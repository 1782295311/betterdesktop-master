using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 热键面板的视觉基元（侧板与设置中心共用）。
/// <para>
/// 【为什么用 XamlReader 而不是代码建模板】`FrameworkElementFactory.SetValue(dp, DynamicResourceExtension…)`
/// 不做延迟求值，会在运行时抛"ResourceReferenceExpression 不能作为有效值"（本仓已踩过一次，
/// 见记忆 39603351）。XAML 解析器原生处理 `{TemplateBinding}` / `{DynamicResource}`，最稳。
/// </para>
/// <para>【设计依据】按钮一律**胶囊**（Pinguo 品牌：pill 而非直角）、hover/pressed 用透明度反馈
/// （UI Pro Max：交互必须有状态反馈，且不做 0ms 突变）；颜色仍全部走主题令牌。</para>
/// </summary>
internal static class HotkeyPanelVisuals
{
    private static ControlTemplate? _pillTemplate;

    /// <summary>胶囊按钮模板（内容居中、1px 描边、hover 85% / pressed 65% / 禁用 40%）。</summary>
    public static ControlTemplate PillButtonTemplate => _pillTemplate ??= BuildPillTemplate();

    /// <summary>隐式按钮样式：挂到容器 Resources 上，子树内所有 Button 自动变胶囊。</summary>
    public static Style PillButtonStyle { get; } = BuildPillStyle();

    private static Style BuildPillStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, PillButtonTemplate));
        return style;
    }

    private static ControlTemplate BuildPillTemplate()
    {
        const string xaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="Button">
          <Border x:Name="bd"
                  CornerRadius="999"
                  Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center"
                              VerticalAlignment="Center"
                              TextElement.Foreground="{TemplateBinding Foreground}" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Opacity" Value="0.85" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="bd" Property="Opacity" Value="0.65" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="bd" Property="Opacity" Value="0.4" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

        var template = (ControlTemplate)XamlReader.Parse(xaml);
        template.Seal(); // 模板不再变化：封闭后省掉每次实例化的校验开销（也便于断言其确实可用）
        return template;
    }
}
