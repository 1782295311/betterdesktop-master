// 5 个独立展开面板（NETWORK / MEM / CPU / 麦克风 / 声音）共享的 UI 工具函数与常量。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 构建用户截图里那种"浅灰玻璃态弹窗"（macOS Ventura 风格）。
/// 面板都以 BuildPreviewContent 返回 FrameworkElement，由 Playground 嵌入卡片容器。
/// 颜色已改为受主题系统驱动：Brush 属性按当前外观设置解析主题令牌（ThemeForeground /
/// ThemeMutedForeground / ThemePanelBackground / ThemeContentBackground / AccentBrush /
/// ThemeSeparator 等），随亮/暗/无色模式自动切换；主题令牌缺失（如 Playground 预览无外观
/// 服务）时回退到原浅灰 macOS 玻璃样式。
/// </summary>
internal static class NativePanelStyles
{
    public const double DefaultWidth = 320;

    // 注意：不能命名为 Freeze，因为 Freezable 自带同名实例方法（void 返回），编译器优先匹配它而不是扩展方法
    private static T MakeFrozen<T>(this T b) where T : Freezable { b.Freeze(); return b; }

    /// <summary>按主题令牌解析当前生效画刷；令牌缺失时回退 fallback（原浅灰样式）。</summary>
    private static Brush ThemeBrush(string key, Brush fallback)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is not null && app.Resources.Contains(key) && app.Resources[key] is Brush b)
            {
                return b;
            }
        }
        catch
        {
            // 资源字典异常时回退
        }
        return fallback;
    }

    public static Brush PanelBack => ThemeBrush("ThemePanelBackground", new SolidColorBrush(Color.FromArgb(240, 234, 234, 237)));
    public static Brush PanelBorder => ThemeBrush("CardBorderBrush", new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)));
    public static Brush BarTrackBack => ThemeBrush("ThemeContentBackground", new SolidColorBrush(Color.FromArgb(110, 60, 60, 67)));
    public static Brush BarFill => ThemeBrush("AccentBrush", new SolidColorBrush(Color.FromRgb(10, 132, 255)));
    public static Brush BarFillBlueLight => ThemeBrush("AccentBrush", new SolidColorBrush(Color.FromRgb(87, 166, 255)));
    public static Brush SeparatorBack => ThemeBrush("ThemeSeparator", new SolidColorBrush(Color.FromArgb(64, 80, 80, 90)));
    public static Brush TextPrimary => ThemeBrush("ThemeForeground", new SolidColorBrush(Color.FromRgb(29, 29, 31)));
    public static Brush TextSecondary => ThemeBrush("ThemeMutedForeground", new SolidColorBrush(Color.FromArgb(200, 60, 60, 67)));
    // 悬停/选中叠加色：中性半透明，不属于主题语义色，保留
    public static Brush RowHover => new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)).MakeFrozen();
    public static Brush AccentBack => ThemeBrush("AccentBrush", new SolidColorBrush(Color.FromRgb(10, 132, 255)));

    public static Border Root(double width = DefaultWidth, Action<StackPanel>? withColumn = null)
    {
        var root = new Border
        {
            Width = width,
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载
            // （Playground 预览不经 ApplyContent 时透出容器背景，属可接受降级）。
            Padding = new Thickness(14, 10, 14, 12),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Effect = null
        };
        var col = new StackPanel { Orientation = Orientation.Vertical };
        withColumn?.Invoke(col);
        root.Child = col;
        return root;
    }

    public static TextBlock Title(string text, double fontSize = 12)
    {
        // 标题继承主题前景色（面板基类自动绑 ThemeForeground）
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 6)
        };
    }

    public static Border Separator(double top = 6, double bottom = 6)
    {
        // 分隔线走主题令牌，随亮/暗/无色模式自动切换。
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(0, top, 0, bottom)
        };
        sep.SetResourceReference(Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    public static FrameworkElement GlyphLabel(string glyph, string text, bool small = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock
        {
            Text = glyph,
            FontSize = small ? 12 : 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        // 图标：次要前景走主题令牌
        g.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMutedForeground");
        row.Children.Add(g);
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = small ? 11 : 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return row;
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        // 2 位小数：累计用量变化较慢时仍可观察到实时更新
        return $"{value:F2} {units[unit]}";
    }

    public static FrameworkElement BuildProgressTrack(double progress01, double heightDp = 14, Brush? fill = null)
    {
        var p = Math.Clamp(progress01, 0, 1);
        var track = new Grid { Height = heightDp };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d - p, GridUnitType.Star) });
        var fillCell = new Border
        {
            CornerRadius = new CornerRadius(heightDp / 2, 0, 0, heightDp / 2)
        };
        if (fill is not null)
        {
            // 显式传入的状态色/语义色（如音量、负载色阶）：保留调用方决定
            fillCell.Background = fill;
        }
        else
        {
            // 默认填充：强调色走主题令牌
            fillCell.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        }
        Grid.SetColumn(fillCell, 0); track.Children.Add(fillCell);
        track.Background = BarTrackBack;
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(heightDp / 2),
            Height = heightDp,
            Clip = new RectangleGeometry(new Rect(0, 0, 1e4, heightDp)) { RadiusX = heightDp / 2, RadiusY = heightDp / 2 },
            Child = track
        };
        return wrap;
    }

    public static (double barWidth, double barHeight) CellSize(int count, double containerWidth, double containerHeight)
    {
        // 简单直方图格子：按列数分 X，每列宽度留间距；Y 统一按 containerHeight 内部的比例绘制
        var gapX = 2;
        var gapY = 2;
        var cols = count;
        var w = containerWidth - (cols + 1) * gapX;
        if (w < 0) w = 0;
        var bw = w / cols;
        return (bw, containerHeight - gapY * 2);
    }

    /// <summary>圆形拇指 Slider 样式（公共复用）：两端半圆轨道 + 强调色已填充段 + 白色圆球拇指带阴影。
    /// 控制中心音量滑杆与声音面板主音量滑杆共用此样式，保证视觉一致。</summary>
    public static Style CreateCircleThumbSliderStyle()
    {
        const string xaml = @"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""Slider"">
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Slider"">
        <Grid VerticalAlignment=""Center"" MinHeight=""24"">
          <!-- 轨道：高度 6，两端半圆（CornerRadius=3） -->
          <Border Height=""6"" CornerRadius=""3"" Background=""#80D0D0D0""
                  VerticalAlignment=""Center"" Margin=""4,0""/>
          <Track x:Name=""PART_Track"">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command=""Slider.DecreaseLarge"">
                <RepeatButton.Template>
                  <ControlTemplate TargetType=""RepeatButton"">
                    <!-- 已填充段：左边圆角，右边平（与未填充段拼接处平整），整体轨道由底层 Border 提供两端半圆胶囊形态 -->
                    <Border Height=""6"" CornerRadius=""3,0,0,3"" Background=""{DynamicResource AccentBrush}""
                            VerticalAlignment=""Center"" Margin=""4,0""/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command=""Slider.IncreaseLarge"">
                <RepeatButton.Template>
                  <ControlTemplate TargetType=""RepeatButton"">
                    <Border Background=""Transparent""/>
                  </ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb x:Name=""Thumb"">
                <Thumb.Template>
                  <ControlTemplate TargetType=""Thumb"">
                    <!-- 白色圆球拇指：直径 18，带阴影 -->
                    <Border Width=""18"" Height=""18"" CornerRadius=""9""
                            Background=""White"" BorderBrush=""#40000000"" BorderThickness=""1"">
                      <Border.Effect>
                        <DropShadowEffect BlurRadius=""4"" ShadowDepth=""1"" Opacity=""0.45""/>
                      </Border.Effect>
                    </Border>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>通用滑杆流畅化配置：关闭刻度吸附、开启点击跳转、拖动过程中只更新 UI 不调系统 API，拖动结束/点击跳转时才提交值。
    /// 解决 IsSnapToTickEnabled=true 导致拖动卡顿、以及 ValueChanged 里频繁调系统 API（音量/亮度）导致的不流畅。</summary>
    /// <param name="slider">目标滑杆</param>
    /// <param name="onValueCommitted">值提交时回调（拖动结束 / 点击跳转），参数为 0-100 的百分比或滑杆原始值</param>
    /// <param name="onValueChanging">拖动过程中回调（仅更新 UI 显示，勿调系统 API），null 则不回调</param>
    public static void ConfigureSmoothSlider(Slider slider, Action<double> onValueCommitted, Action<double>? onValueChanging = null)
    {
        slider.IsMoveToPointEnabled = true;  // 点击任意位置直接跳转
        slider.IsSnapToTickEnabled = false;   // 关闭刻度吸附，拖动流畅

        bool isDragging = false;
        double lastCommitted = slider.Value;

        // 拖动开始：标记拖动中，暂停系统 API 调用
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { isDragging = true; }));

        // 拖动结束：提交最终值到系统 API
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            isDragging = false;
            if (!e.Canceled)
            {
                lastCommitted = slider.Value;
                onValueCommitted(slider.Value);
            }
            else
            {
                // 取消拖动：恢复原值
                slider.Value = lastCommitted;
            }
        }));

        // 值变化：拖动中只更新 UI；非拖动（键盘/点击跳转）直接提交
        slider.ValueChanged += (_, e) =>
        {
            if (isDragging)
            {
                onValueChanging?.Invoke(e.NewValue);
            }
            else
            {
                lastCommitted = e.NewValue;
                onValueCommitted(e.NewValue);
            }
        };
    }
}
