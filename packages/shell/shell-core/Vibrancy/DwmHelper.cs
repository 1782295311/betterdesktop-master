using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

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
    private const int DWMSBT_MAINWINDOW = 2;          // Mica：采样壁纸、无亚克力噪声
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


    /// <summary>
    /// F11/O3（7437 纪律 1）：WCA/DWM 调用返回值必须检查——毛玻璃失效静默降级透明窗时
    /// 无从排查，失败必须带错误码记日志（降级照常，不崩溃）。
    /// </summary>
    private static void TraceFailure(string api, IntPtr hWnd, int hr)
        => DiagnosticLog.Trace("Vibrancy", $"{api} 失败 hwnd={hWnd:X} hr=0x{hr:X8}（毛玻璃可能降级为透明窗）");

    /// <summary>按入参给窗口设置 DWM 系统默认圆角偏好（实证基准 FrostedGlassDemo 原样）。</summary>
    private static void ApplyCornerPreference(IntPtr hWnd, bool roundCorners, bool smallRadius)
    {
        if (!roundCorners) return;
        int corner = smallRadius ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        int hr = NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (hr != 0) TraceFailure("NativeMethods.DwmSetWindowAttribute(CornerPreference)", hWnd, hr);
    }

    /// <summary>实际生效的模糊路径（供日志/诊断；用于区分"这档没出图"与"环境不允许"）。</summary>
    public enum BlurPath
    {
        /// <summary>未尝试。</summary>
        None,

        /// <summary>经典无色毛玻璃（ACCENT_ENABLE_BLURBEHIND，零色调）——"无色纯模糊"正解。</summary>
        BlurBehind,

        /// <summary>
        /// 亚克力通道 + 零色调（ACCENT_ENABLE_ACRYLICBLURBEHIND + nFlags=2 + alpha=0）。
        /// <para>
        /// ⚠️ **实测并非无色**：BlurProbe 在 Win11 build 26200 上测得它把窗口刷成**不透明白**（100% 白）。
        /// 因此**不在默认链里**，仅作为排障对比档保留（<c>appearance.material.blur=acrylic0</c>）。
        /// </para>
        /// </summary>
        AcrylicZeroTint,

        /// <summary>DWM 系统材质 Mica（自带系统色调，严格说不是"无色"，仅作保底避免整窗无背景）。</summary>
        Mica,

        /// <summary>三档全部失败（极老系统/被策略禁用）。</summary>
        Failed,
    }

    /// <summary>
    /// 第一优先档（可在设置里切换，用于按机器 A/B"到底哪一档能出图"——虚拟机/不同 build 上并不一致）。
    /// </summary>
    public enum BlurPreference
    {
        /// <summary>按内置顺序自动尝试（BlurBehind → AcrylicZeroTint → Mica）。</summary>
        Auto,

        /// <summary>强制先试经典无色毛玻璃。</summary>
        BlurBehind,

        /// <summary>强制先试亚克力零色调。</summary>
        AcrylicZeroTint,

        /// <summary>强制先试 Mica（有色调）。</summary>
        Mica,
    }

    /// <summary>
    /// 透亮模糊（默认推荐）：背后真实内容做高斯模糊，无色调、不压暗。
    /// roundCorners:true 让系统模糊区也圆角，与 XAML 内容层 CornerRadius 重合，避免直角溢出。
    /// </summary>
    public static void EnableBlurBehind(IntPtr hWnd, bool roundCorners, bool smallRadius)
        => _ = EnableColorlessBlur(hWnd, roundCorners, smallRadius);

    /// <summary>
    /// 【2026-09-18 · 无色纯模糊主路径】按"零色调优先"的顺序尝试，返回实际用的那一档。
    /// <para>
    /// 为什么不是"看返回值决定降级"：<c>SetWindowCompositionAttribute</c> 返回 BOOL 时
    /// **失败不设 GetLastError、且返回成功也不代表 DWM 真的画了模糊**。所以本方法做的是
    /// "按优先级尝试 + 每档都留可诊断痕迹"，真正的"这一档到底出没出图"由
    /// <see cref="BlurCapability"/>（环境前提）与 tools/BlurProbe（实拍像素比对）共同判定。
    /// </para>
    /// <para>
    /// 顺序（<paramref name="preference"/> 指定的档先试，其余按内置顺序补位）：
    ///   ① BLURBEHIND + GradientColor=0（标准无色纯模糊）
    ///   ② ACRYLICBLURBEHIND + nFlags=2 + GradientColor=0x00FFFFFF（alpha=0 ⇒ 零色调；走亚克力通道拿模糊不留色）
    ///   ③ Mica（自带系统色调，保底，避免"整窗没有任何背景"）
    /// </para>
    /// <para>
    /// 无论走哪一档都会先铺满玻璃区并强制 DWM 重算——**模糊只画在窗口的透明像素后面**，
    /// 不铺玻璃区即使 accent 生效也看不见（"调了没反应"的第二大原因）。
    /// </para>
    /// </summary>
    public static BlurPath EnableColorlessBlur(
        IntPtr hWnd,
        bool roundCorners,
        bool smallRadius,
        BlurPreference preference = BlurPreference.Auto)
    {
        EnsureGlassArea(hWnd);

        foreach (var candidate in OrderCandidates(preference))
        {
            switch (candidate)
            {
                case BlurPath.BlurBehind:
                    if (TrySetAccent(hWnd, ACCENT_ENABLE_BLURBEHIND, nFlags: 0, nColor: 0, "BlurBehind"))
                    {
                        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
                        ForceDwmRecompute(hWnd);
                        return BlurPath.BlurBehind;
                    }

                    break;

                case BlurPath.AcrylicZeroTint:
                    // nFlags=2 告知 GradientColor 被使用（漏设则色调被忽略/或退化为不透明白）；
                    // alpha=0 是关键——亚克力自带色调，只有把 alpha 清零才是"无色"。
                    if (TrySetAccent(
                            hWnd,
                            ACCENT_ENABLE_ACRYLICBLURBEHIND,
                            nFlags: 2,
                            nColor: unchecked((int)0x00FFFFFF),
                            "AcrylicZeroTint"))
                    {
                        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
                        ForceDwmRecompute(hWnd);
                        return BlurPath.AcrylicZeroTint;
                    }

                    break;

                case BlurPath.Mica:
                    // EnableMica 内部已含圆角处理与边框铺展，勿重复 ApplyCornerPreference。
                    EnableMica(hWnd, roundCorners, smallRadius);
                    ForceDwmRecompute(hWnd);
                    return BlurPath.Mica;
            }
        }

        TraceFailure("EnableColorlessBlur(全部失败)", hWnd, Marshal.GetLastWin32Error());
        return BlurPath.Failed;
    }

    private static BlurPath[] OrderCandidates(BlurPreference preference)
    {
        // 默认顺序：BLURBEHIND → Mica。
        // 【2026-09-18 实拍结论（tools/BlurProbe · Win11 build 26200，黑白细条纹 + 真实抓屏像素分类）】
        //   · 分层 + BLURBEHIND     → 中间灰 100%   ✅ 真正的"无色纯模糊"
        //   · 分层 + ACRYLIC(α=0)   → 不透明白 100% ❌ "零色调亚克力"并不无色，反而糊上一层白
        //   · 非分层 + BLURBEHIND   → 中间灰 100%   ✅（玻璃窗路径同样可用，但非必需）
        //   · 非分层 + ACRYLIC(α=0) → 不透明黑 100% ❌
        //   · 对照组（不设 accent）  → 黑白各 ~46% / 灰 7% = 测量基线（透明且看到清晰背景）
        // 故 ACRYLIC 档**不进默认链**：它把窗口刷成白/黑，比"没有模糊"更糟。
        // 保留 BlurPreference.AcrylicZeroTint 仅供排障对比（换机器/换 build 复核该结论）。
        var defaultOrder = new[] { BlurPath.BlurBehind, BlurPath.Mica };
        var preferred = preference switch
        {
            BlurPreference.AcrylicZeroTint => BlurPath.AcrylicZeroTint,
            BlurPreference.Mica => BlurPath.Mica,
            _ => BlurPath.BlurBehind,
        };

        if (preferred == BlurPath.BlurBehind)
        {
            return defaultOrder;
        }

        var reordered = new BlurPath[defaultOrder.Length];
        reordered[0] = preferred;
        var index = 1;
        foreach (var path in defaultOrder)
        {
            if (path != preferred)
            {
                reordered[index++] = path;
            }
        }

        return reordered;
    }

    /// <summary>
    /// 把整窗铺成"玻璃区"（DwmExtendFrameIntoClientArea 全 -1）：客户区里未被内容覆盖的透明像素
    /// 才会由 DWM 用 backdrop/模糊填充。
    /// <para>
    /// 对**分层窗口**（WPF AllowsTransparency=true）DWM 会忽略本调用，因此这里对现有窗口零副作用；
    /// 它对"非分层玻璃窗"路径（<c>appearance.material.glass</c>）才是生效前提。
    /// </para>
    /// </summary>
    private static void EnsureGlassArea(IntPtr hWnd)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        int hr = DwmExtendFrameIntoClientArea(hWnd, ref margins);
        if (hr != 0)
        {
            TraceFailure("DwmExtendFrameIntoClientArea(玻璃区)", hWnd, hr);
        }
    }

    /// <summary>写一份 ACCENT_POLICY；失败带错误码记日志（失败也返回 false 供上层换档）。</summary>
    private static bool TrySetAccent(IntPtr hWnd, int accentState, int nFlags, int nColor, string what)
    {
        var accent = new ACCENT_POLICY
        {
            nAccentState = accentState,
            nFlags = nFlags,
            nColor = nColor,
            nAnimationId = 0
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, p, false);
        try
        {
            var data = new NativeMethods.WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            bool ok = NativeMethods.SetWindowCompositionAttribute(hWnd, ref data);
            if (!ok)
            {
                TraceFailure($"SetWindowCompositionAttribute({what})", hWnd, Marshal.GetLastWin32Error());
            }

            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// 应用 accent 之后强制 DWM 重算一次（不改位置/尺寸/Z 序、不激活）。
    /// <para>
    /// 【为什么必须有】Win11 实测：设置 accent 后不补这一步，材质常常不刷新——首次显示、
    /// 从最小化恢复、切虚拟桌面回来、系统「透明效果」开关刚变动，都会出现"设置成功但没画出来"。
    /// </para>
    /// </summary>
    private static void ForceDwmRecompute(IntPtr hWnd)
    {
        _ = NativeMethods.SetWindowPos(
            hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
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
        int extendHr = DwmExtendFrameIntoClientArea(hWnd, ref margins);
        if (extendHr != 0) TraceFailure("DwmExtendFrameIntoClientArea", hWnd, extendHr);

        int backdrop = DWMSBT_TRANSIENTWINDOW;
        int backdropHr = NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        if (backdropHr != 0) TraceFailure("NativeMethods.DwmSetWindowAttribute(SystemBackdrop)", hWnd, backdropHr);

        ApplyCornerPreference(hWnd, roundCorners, smallRadius);
    }

    /// <summary>
    /// Win11 DWM 系统材质 **Mica**（DWMSBT_MAINWINDOW）：采样桌面壁纸、无亚克力噪声。
    /// <para>
    /// 用途：<see cref="EnableBlurBehind"/> **真失败**时的降级材质（2026-09-14 起；
    /// 2026-09-12 用户口径"不要亚克力"）。注意该材质要求**非分层窗口**：分层窗口
    ///（<c>AllowsTransparency=true</c>）会被 DWM 忽略 backdrop，此时保持透明窗，绝不退化为纯黑。
    /// </para>
    /// </summary>
    public static void EnableMica(IntPtr hWnd, bool roundCorners, bool smallRadius)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        int extendHr = DwmExtendFrameIntoClientArea(hWnd, ref margins);
        if (extendHr != 0) TraceFailure("DwmExtendFrameIntoClientArea", hWnd, extendHr);

        int backdrop = DWMSBT_MAINWINDOW;
        int backdropHr = NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        if (backdropHr != 0) TraceFailure("NativeMethods.DwmSetWindowAttribute(SystemBackdrop=Mica)", hWnd, backdropHr);

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
            // P0（7404 红线 1 [verified]）：AccentFlags=2 告知 GradientColor 被使用——
            // 漏设则亚克力色调被忽略（BlurBehind 无色调不适用此规则，保持 0）。
            nFlags = 2,
            nColor = unchecked((int)argb),
            nAnimationId = 0
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(accent, p, false);
        try
        {
            var data = new NativeMethods.WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            if (!NativeMethods.SetWindowCompositionAttribute(hWnd, ref data))
            {
                TraceFailure("SetWindowCompositionAttribute(AccentTint)", hWnd, Marshal.GetLastWin32Error());
            }
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
            var data = new NativeMethods.WINCOMPATTRDATA { nAttribute = WCA_ACCENT_POLICY, pData = p, ulSize = size };
            if (!NativeMethods.SetWindowCompositionAttribute(hWnd, ref data))
            {
                TraceFailure("SetWindowCompositionAttribute(Disable)", hWnd, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        int backdrop = DWMSBT_NONE;
        int backdropHr = NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        if (backdropHr != 0) TraceFailure("NativeMethods.DwmSetWindowAttribute(SystemBackdrop=None)", hWnd, backdropHr);
    }
}
