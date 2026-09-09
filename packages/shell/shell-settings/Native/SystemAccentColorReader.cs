using System;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace BetterDesktop.Shell.Settings.Native;

/// <summary>
/// 系统强调色读取器。
/// 主路径（7440 变体 A，EarTrumpet Uxtheme.cs 实测移植）：Immersive 颜色家族三段式——
/// #98 GetImmersiveUserColorPreference → #96 GetImmersiveColorTypeFromName（名字必须带
/// <c>Immersive</c> 前缀）→ #95 GetImmersiveColorFromColorSetEx（0xAARRGGBB，含 alpha）；
/// 返回 0xFFFF00FF 洋红哨兵 = 颜色类型无效，必须判。
/// 降级路径（7405 变体 A，cairoshell WindowsThemeColorManager.cs 实测移植）：
/// uxtheme#120 GetUserColorPreference 的 crAccentColor（0x00RRGGBB，BGR 布局）。
/// GetAccentColor 失败返回 null（不抛），由调用方用硬编码兜底；结果按进程缓存一次
/// （动态跟随系统强调色变化为 §12 deferred，本轮不做）。
/// </summary>
internal static class SystemAccentColorReader
{
    /// <summary>#95 无效颜色哨兵（洋红）——把它当真色用会显示洋红（7440 红线 3）。</summary>
    private const uint InvalidColorSentinel = 0xFFFF00FFu;

    private static Color? _cached;
    private static bool _probed;

    /// <summary>读当前用户系统强调色（按进程缓存一次）。失败/无效返回 null。</summary>
    public static Color? GetAccentColor()
    {
        if (_probed)
        {
            return _cached;
        }

        _probed = true;
        _cached = ReadAccentColor();
        return _cached;
    }

    private static Color? ReadAccentColor()
    {
        // ---- 主路径：7440 Immersive 颜色家族（必须按序数调用，无公开导出名——红线 1） ----
        try
        {
            var colorSet = GetImmersiveUserColorPreference(false, false);
            // 真名 = "SystemAccent"（#100 枚举 #1211 实证），#96 入参须拼 "Immersive" 前缀
            //（红线 4）。注意 7440 文档原示例 "ImmersiveSystemAccentColor" 实测不存在（#96 返回
            // 0xFFFFFFFF）——已按本机实测纠正为 "ImmersiveSystemAccent"。
            var colorType = GetImmersiveColorTypeFromName("ImmersiveSystemAccent");
            var raw = GetImmersiveColorFromColorSetEx(colorSet, colorType, ignoreHighContrast: false, 0);
            if (raw != InvalidColorSentinel && raw != 0)
            {
                // 0xAARRGGBB 拆位（红线 5：#95 返回含 alpha，勿按 BGR 解）。
                return Color.FromArgb(
                    (byte)((raw >> 24) & 0xFF),
                    (byte)((raw >> 16) & 0xFF),
                    (byte)((raw >> 8) & 0xFF),
                    (byte)(raw & 0xFF));
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 极老系统无 #94-98 导出 → 走 #120 降级。
        }
        catch (DllNotFoundException)
        {
        }

        // ---- 降级路径：7405 #120（crAccentColor，0x00RRGGBB BGR 布局） ----
        try
        {
            var preference = new ImmersiveColorPreference();
            _ = GetUserColorPreference(ref preference, fForceReload: true);
            var accent = preference.crAccentColor;
            if (accent != 0)
            {
                // BGR 布局：R=c&0xFF, G=(c>>8)&0xFF, B=(c>>16)&0xFF——顺序反了红蓝颠倒（7405 红线 4）。
                return Color.FromRgb(
                    (byte)(accent & 0xFF),
                    (byte)((accent >> 8) & 0xFF),
                    (byte)((accent >> 16) & 0xFF));
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImmersiveColorPreference
    {
        public uint dwColorSetIndex;
        public uint crStartColor;   // 0x00RRGGBB（BGR 布局）
        public uint crAccentColor;
    }

    [DllImport("uxtheme.dll", EntryPoint = "#98")]
    private static extern uint GetImmersiveUserColorPreference(bool forceCheckRegistry, bool skipCheckOnFail);

    [DllImport("uxtheme.dll", EntryPoint = "#96")]
    private static extern uint GetImmersiveColorTypeFromName([MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport("uxtheme.dll", EntryPoint = "#95")]
    private static extern uint GetImmersiveColorFromColorSetEx(uint colorSet, uint colorType, bool ignoreHighContrast, uint highContrastCacheMode);

    [DllImport("uxtheme.dll", EntryPoint = "#120")]
    private static extern IntPtr GetUserColorPreference(ref ImmersiveColorPreference pPreference, bool fForceReload);
}
