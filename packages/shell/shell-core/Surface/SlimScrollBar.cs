using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 细滚动条样式（**入口 exe 共用实现**）。
/// <para>
/// 【为什么需要 · 2026-09-15 用户实测】WPF 默认滚动条是 Aero2 的系统样式：两端带**上下箭头按钮**、
/// 方头轨道、浅灰色 —— 摆在深色面板里非常割裂（截图 OCR 面板的列表尤其明显）。
/// 这里换成：无箭头、10px 细轨道、圆角拇指、随主题（深/浅模式自动跟随，透明轨道不画底）。
/// </para>
/// <para>
/// 令牌：<c>ScrollThumb</c> / <c>ScrollThumbHover</c>（由 <see cref="EntryTheme.ApplyToAppResources"/> 定义）。
/// 之所以用 XAML 字符串而非 <c>FrameworkElementFactory</c>：<c>Track</c> 的 Thumb 是**属性元素**
/// （<c>Track.Thumb</c>），工厂 API 无法表达；XAML 解析器原生支持（与仓库既有做法一致）。
/// </para>
/// </summary>
public static class SlimScrollBar
{
    /// <summary>拇指宽度（与系统默认同宽）。</summary>
    private const double ThumbWidth = EntryTheme.Scale.BadgePadX;

    /// <summary>滚动条槽厚度（= 拇指 + 两侧留白）。</summary>
    private const double Thickness = EntryTheme.Scale.BadgePadX * 2;

    /// <summary>
    /// 拇指在槽内的留白（**左右相等**）。
    /// <para>
    /// 【为什么改成对称 · 2026-09-15 用户实测】上一版让拇指"内容侧留 4px、边缘侧留 2px"（靠向窗口边缘，
    /// macOS 覆盖式滚动条的做法），但实测观感是**右侧像被切掉一块**——2px 与 4px 的差在细条上足以被读成
    /// "不对称/被遮挡"。细滚动条本就只有 6px 宽，任何左右不等都会被放大，故直接居中。
    /// </para>
    /// </summary>
    private const double ThumbInset = (Thickness - ThumbWidth) / 2;

    /// <summary>拇指最短长度：太短会变成"小方块"，拖不准。</summary>
    private const double MinThumbLength = 24;

    /// <summary>构造样式（隐式样式：TargetType = ScrollBar）。</summary>
    public static Style Create()
    {
        var radius = EntryTheme.Scale.SpaceXS;
        var marginV = $"{ThumbInset},0,{ThumbInset},0";   // 竖向：左右等距（居中）
        var marginH = $"0,{ThumbInset},0,{ThumbInset}";   // 横向：上下等距（居中）

        var xaml = $$"""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="ScrollBar">
  <Grid Background="Transparent">
    <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True">
      <Track.Thumb>
        <Thumb x:Name="ThumbEl" Margin="{{marginV}}"
               MinHeight="{{MinThumbLength}}" MinWidth="{{MinThumbLength}}">
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Border x:Name="Th" CornerRadius="{{radius}}"
                      Background="{DynamicResource ScrollThumb}"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Th" Property="Background" Value="{DynamicResource ScrollThumbHover}"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="Orientation" Value="Horizontal">
      <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
      <Setter TargetName="ThumbEl" Property="Margin" Value="{{marginH}}"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";

        var template = (ControlTemplate)XamlReader.Parse(xaml);

        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, Thickness));       // 竖向
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, Thickness));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));

        // 横向滚动条：厚度落在高度上，宽高互换
        var horizontal = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
        horizontal.Setters.Add(new Setter(FrameworkElement.HeightProperty, Thickness));
        horizontal.Setters.Add(new Setter(FrameworkElement.WidthProperty, double.NaN));
        horizontal.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        horizontal.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom));
        style.Triggers.Add(horizontal);

        return style;
    }

    /// <summary>
    /// 装进应用资源（隐式样式：本进程内所有 <see cref="ScrollBar"/> 生效，无需逐处指定）。
    /// 约定在各入口 exe 的启动装配里、主题引导（ApplyToAppResources）之后调用一次。
    /// </summary>
    public static void Install(Application app)
    {
        app.Resources.Remove(typeof(ScrollBar));
        app.Resources.Add(typeof(ScrollBar), Create());
    }
}
