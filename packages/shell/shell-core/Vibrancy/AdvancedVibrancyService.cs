using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 高级毛玻璃服务实现，支持动态模糊和边缘光效。
/// 通过 Win32 <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c>（见 <see cref="DwmHelper"/>）实现真正生效的
/// 色调叠加：模糊由系统实时采样背景完成，alpha 通道由调用方控制，从而实现"动态强度"与"边缘光效"。
/// </summary>
public sealed class AdvancedVibrancyService : VibrancyService, IAdvancedVibrancyService
{
    private readonly Dictionary<IntPtr, BlurSettings> _blurSettings = new();
    private readonly Dictionary<IntPtr, EdgeGlowSettings> _edgeGlowSettings = new();

    // 动态模糊使用的基色（中性暗色），仅由 blurIntensity 控制其 alpha。
    private const uint DynamicBaseArgb = 0x000000;

    /// <inheritdoc />
    public void ApplyDynamic(IntPtr hWnd, VibrancyStyle style, double blurIntensity, bool roundCorners = true, bool smallRadius = false)
    {
        // 圆角跟随 FrostedGlassDemo 实证基准：DWM 系统默认圆角，由 DwmHelper 设置。
        // 真实效果：alpha 由 intensity 驱动（0=无叠加，1=最强色调）。
        var alpha = (byte)Math.Round(Math.Clamp(blurIntensity, 0.0, 1.0) * 200);
        var argb = PackArgb(alpha, DynamicBaseArgb);

        DwmHelper.EnableAccentTint(hWnd, argb, roundCorners, smallRadius);

        _blurSettings[hWnd] = new BlurSettings
        {
            Style = style,
            BlurIntensity = Math.Clamp(blurIntensity, 0.0, 1.0),
            ColorArgb = argb
        };
    }

    /// <inheritdoc />
    public void ApplyWithEdgeGlow(IntPtr hWnd, VibrancyStyle style, Color edgeGlowColor, double edgeGlowIntensity, bool roundCorners = true, bool smallRadius = false)
    {
        // 圆角跟随 FrostedGlassDemo 实证基准：DWM 系统默认圆角，由 DwmHelper 设置。
        var alpha = (byte)Math.Round(Math.Clamp(edgeGlowIntensity, 0.0, 1.0) * 255);
        var argb = PackArgb(alpha, edgeGlowColor);

        DwmHelper.EnableAccentTint(hWnd, argb, roundCorners, smallRadius);

        _edgeGlowSettings[hWnd] = new EdgeGlowSettings
        {
            Style = style,
            EdgeGlowColor = edgeGlowColor,
            EdgeGlowIntensity = Math.Clamp(edgeGlowIntensity, 0.0, 1.0),
            ColorArgb = argb
        };
    }

    /// <inheritdoc />
    public void UpdateDynamicBlur(IntPtr hWnd, double blurIntensity)
    {
        if (_blurSettings.TryGetValue(hWnd, out var settings))
        {
            settings.BlurIntensity = Math.Clamp(blurIntensity, 0.0, 1.0);
            settings.ColorArgb = PackArgb((byte)Math.Round(settings.BlurIntensity * 200), DynamicBaseArgb);
            DwmHelper.EnableAccentTint(hWnd, settings.ColorArgb, roundCorners: true, smallRadius: false);
        }
    }

    /// <inheritdoc />
    public void UpdateEdgeGlow(IntPtr hWnd, Color edgeGlowColor, double edgeGlowIntensity)
    {
        if (_edgeGlowSettings.TryGetValue(hWnd, out var settings))
        {
            settings.EdgeGlowColor = edgeGlowColor;
            settings.EdgeGlowIntensity = Math.Clamp(edgeGlowIntensity, 0.0, 1.0);
            settings.ColorArgb = PackArgb((byte)Math.Round(settings.EdgeGlowIntensity * 255), edgeGlowColor);
            DwmHelper.EnableAccentTint(hWnd, settings.ColorArgb, roundCorners: true, smallRadius: false);
        }
    }

    /// <inheritdoc />
    public override void Disable(IntPtr hWnd)
    {
        base.Disable(hWnd);
        _blurSettings.Remove(hWnd);
        _edgeGlowSettings.Remove(hWnd);
    }

    /// <summary>将 alpha 与基色打包为 0xAARRGGBB（高字节序：AARRGGBB）。</summary>
    private static uint PackArgb(byte alpha, Color color)
        => ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>将 alpha 与已有 0xRRGGBB 基色打包为 0xAARRGGBB。</summary>
    private static uint PackArgb(byte alpha, uint rgb)
        => (uint)(alpha << 24) | (rgb & 0x00FFFFFF);

    private class BlurSettings
    {
        public VibrancyStyle Style { get; set; }
        public double BlurIntensity { get; set; }
        public uint ColorArgb { get; set; }
    }

    private class EdgeGlowSettings
    {
        public VibrancyStyle Style { get; set; }
        public Color EdgeGlowColor { get; set; }
        public double EdgeGlowIntensity { get; set; }
        public uint ColorArgb { get; set; }
    }
}
