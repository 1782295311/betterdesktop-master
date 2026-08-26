using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 系统级毛玻璃。
///  - EnableBlurBehind：Win32 经典「背后模糊」(ACCENT_ENABLE_BLURBEHIND)，只做高斯模糊、不叠任何色调 → 透亮无色。
///  - EnableAcrylic   ：Win11 DWM 系统亚克力(DWMSBT_TRANSIENTWINDOW)，自带一层暗色调（用于可读），偏暗。
/// 二者都真实采样窗口背后的桌面/其它 App，零抓屏、零闪烁。
/// 本文件原样移植自 FrostedGlassDemo/DwmHelper.cs 实证基准：圆角由 DwmSetWindowAttribute
/// (DWMWA_WINDOW_CORNER_PREFERENCE) 的 DWM 系统默认圆角实现，与 XAML 内容层 CornerRadius 重合。
/// </summary>
internal static class DwmHelper
{
    // DWMWA_SYSTEMBACKDROP_TYPE
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3;     // Acrylic

    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    // WCA_ACCENT_POLICY
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;   // 纯透亮模糊，无色调
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4; // 带色调的亚克力（nColor = 0xAARRGGBB）

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int nAccentState;
        public int nFlags;
        public int nColor;
        public int nAnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int nAttribute;
        public IntPtr pData;
        public int ulSize;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WINCOMPATTRDATA pAttrData);

    /// <summary>按入参给窗口设置 DWM 系统默认圆角偏好（实证基准 FrostedGlassDemo 原样）。</summary>
    private static void ApplyCornerPreference(IntPtr hWnd, bool roundCorners, bool smallRadius)
    {
        if (!roundCorners) return;
        int corner = smallRadius ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// 透亮模糊（默认推荐）：背后真实内容做高斯模糊，无色调、不压暗。
    /// roundCorners:true 让系统模糊区也圆角，与 XAML 内容层 CornerRadius 重合，避免直角溢出。
    /// </summary>
    public static void EnableBlurBehind(IntPtr hWnd, bool roundCorners, bool smallRadius)
    {
        var accent = new ACCENT_POLICY
        {
            nAccentState = ACCENT_ENABLE_BLURBEHIND,
            nFlags = 0,
            nColor = 0,
            nAnimationId = 0
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, p, false);
        try
        {
            var data = new WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            SetWindowCompositionAttribute(hWnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
    }

    /// <summary>
    /// 系统亚克力（带暗色调，偏暗）：采样背后所有内容做实时模糊。
    /// roundCorners:true 让系统模糊区也圆角，与 XAML 内容层 CornerRadius 重合。
    /// </summary>
    public static void EnableAcrylic(IntPtr hWnd, bool roundCorners, bool smallRadius)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        DwmExtendFrameIntoClientArea(hWnd, ref margins);

        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
    }

    /// <summary>
    /// 带色调的亚克力：在真实模糊之上叠加一层 tint，alpha 由调用方控制（动态强度/边缘光效）。
    /// nColor 为 0xAARRGGBB；alpha 越大色调越浓。模糊本身由系统实时采样背景完成。
    /// roundCorners:true 让系统模糊区也圆角，与 XAML 内容层 CornerRadius 重合。
    /// </summary>
    public static void EnableAccentTint(IntPtr hWnd, uint argb, bool roundCorners, bool smallRadius)
    {
        var accent = new ACCENT_POLICY
        {
            nAccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            nFlags = 0,
            nColor = unchecked((int)argb),
            nAnimationId = 0
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, p, false);
        try
        {
            var data = new WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            SetWindowCompositionAttribute(hWnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
    }

    /// <summary>
    /// 关闭磨砂（普通透明窗）。
    /// </summary>
    public static void Disable(IntPtr hWnd)
    {
        var accent = new ACCENT_POLICY
        {
            nAccentState = ACCENT_DISABLED,
            nFlags = 0,
            nColor = 0,
            nAnimationId = 0
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, p, false);
        try
        {
            var data = new WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            SetWindowCompositionAttribute(hWnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        int backdrop = DWMSBT_NONE;
        DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }
}
