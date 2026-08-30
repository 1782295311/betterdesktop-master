// BetterDesktop.Shell.MenuBar — 主题独立弹出面板（零硬编码，真实系统）。
// 本面板聚焦"显示器亮度控制"：通过 C++ 原生层 DisplayCore.dll（dxva2）读写
// 第一台支持亮度调节的物理显示器；不支持时给出明确降级提示。
// UI 结构：
//   - 顶部：标题"显示亮度"
//   - 中部：亮度滑块（Min/Max/当前值全部来自系统，非写死 0-100）
//   - 底部：当前亮度百分比 + "显示设置" 跳转 ms-settings:display

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>主题独立弹出面板（显示器亮度控制，共用的 BrightnessSliderControl）。</summary>
internal sealed class ThemePopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 290;

    private readonly IBrightnessMonitor? _brightness;
    private BrightnessSliderControl? _brightnessControl;

    public ThemePopupWindow(IBrightnessMonitor? brightness, IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _brightness = brightness;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载。
            Padding = new Thickness(6, 6, 6, 8),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 共用亮度滑块（与系统/控制中心同一 IBrightnessMonitor 数据源，跨面板同步）
        if (_brightness is not null)
        {
            _brightnessControl = new BrightnessSliderControl(_brightness, title: "显示亮度", showSettingsLink: true);
            column.Children.Add(_brightnessControl.Root);
        }

        root.Child = column;
        return root;
    }

    protected override void OnClosed(EventArgs e)
    {
        _brightnessControl?.Dispose();
        _brightnessControl = null;
        base.OnClosed(e);
    }
}
