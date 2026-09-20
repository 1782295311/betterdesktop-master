using System.Windows.Controls;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>
/// 视觉基元（胶囊按钮模板）必须能被 XAML 解析器真正解析出来——
/// 本仓曾因"代码工厂建模板不做延迟求值"踩过运行时异常，所以这里直接把解析结果断言掉，
/// 不让它拖到真机才暴露。
/// </summary>
public class HotkeyPanelVisualsTests
{
    [Fact]
    public void Pill_button_template_parses()
    {
        var template = HotkeyPanelVisuals.PillButtonTemplate;
        Assert.NotNull(template);
        Assert.Equal(typeof(Button), template.TargetType);
        Assert.True(template.IsSealed, "模板应已封闭（XamlReader 解析产物）");
    }

    [Fact]
    public void Pill_button_style_targets_buttons()
    {
        var style = HotkeyPanelVisuals.PillButtonStyle;
        Assert.NotNull(style);
        Assert.Equal(typeof(Button), style.TargetType);
        Assert.NotEmpty(style.Setters);
    }
}
