using System;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 高级毛玻璃服务接口，支持动态模糊和边缘光效。
/// </summary>
public interface IAdvancedVibrancyService : IVibrancyService
{
    /// <summary>
    /// 对指定窗口应用动态模糊毛玻璃。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="style">毛玻璃风格。</param>
    /// <param name="blurIntensity">模糊强度（0.0-1.0）。</param>
    /// <param name="roundCorners">是否启用圆角。</param>
    /// <param name="smallRadius">圆角半径是否为小号。</param>
    void ApplyDynamic(IntPtr hWnd, VibrancyStyle style, double blurIntensity, bool roundCorners = true, bool smallRadius = false);

    /// <summary>
    /// 对指定窗口应用带边缘光效的毛玻璃。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="style">毛玻璃风格。</param>
    /// <param name="edgeGlowColor">边缘光效颜色。</param>
    /// <param name="edgeGlowIntensity">边缘光效强度（0.0-1.0）。</param>
    /// <param name="roundCorners">是否启用圆角。</param>
    /// <param name="smallRadius">圆角半径是否为小号。</param>
    void ApplyWithEdgeGlow(IntPtr hWnd, VibrancyStyle style, System.Windows.Media.Color edgeGlowColor, double edgeGlowIntensity, bool roundCorners = true, bool smallRadius = false);

    /// <summary>
    /// 更新动态模糊强度。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="blurIntensity">新的模糊强度（0.0-1.0）。</param>
    void UpdateDynamicBlur(IntPtr hWnd, double blurIntensity);

    /// <summary>
    /// 更新边缘光效。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="edgeGlowColor">新的边缘光效颜色。</param>
    /// <param name="edgeGlowIntensity">新的边缘光效强度（0.0-1.0）。</param>
    void UpdateEdgeGlow(IntPtr hWnd, System.Windows.Media.Color edgeGlowColor, double edgeGlowIntensity);
}
