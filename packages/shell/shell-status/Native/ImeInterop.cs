// BetterDesktop.Shell.Status — 输入法状态 P/Invoke 收口：
//   - GetKeyboardLayoutNameW 读取当前键盘布局名（如 "00000804"=中文）
//   - ImmGetDefaultIMEWnd 取默认 IME 窗口（验证是否有激活的 IME 能力）

using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>
/// 输入法/键盘布局采集的 Interop 封装。
/// 失败/异常时返回可用的降级结果（UnknownLayout），不抛异常。
/// </summary>
internal static class ImeInterop
{
    private const int KlNameMaxLength = 9; // KL_NAMELENGTH（含结尾 NUL）

    /// <summary>布局名取不到时的降级值。</summary>
    public const string UnknownLayout = "00000000";

    /// <summary>布局名取不到时显示的文本。</summary>
    public const string UnknownText = "未知输入法";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyboardLayoutNameW(StringBuilder pwszKLID);

    /// <summary>读取当前键盘布局 KLID，返回形如 "00000804" 的八位字符串；失败返回 UnknownLayout。</summary>
    public static string GetKeyboardLayoutId()
    {
        try
        {
            var sb = new StringBuilder(KlNameMaxLength);
            if (GetKeyboardLayoutNameW(sb) > 0 && sb.Length > 0)
            {
                return sb.ToString();
            }
            return UnknownLayout;
        }
        catch
        {
            return UnknownLayout;
        }
    }
}

/// <summary>
/// 常见 KLID → 显示名映射（最小验证用；完整映射应在正式实现走注册表/系统 API）。
/// </summary>
internal static class CultureLayoutMapper
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00000804"] = "中文",
        ["00000409"] = "ENG",
        ["00000404"] = "中文（繁）",
        ["00000411"] = "日本語",
        ["00000412"] = "한국어",
    };

    /// <summary>布局 KLID → 简短显示名；未知返回 UnknownText 对应键位由调用方处理。</summary>
    public static string GetShortName(string? id)
    {
        if (id is not null && Known.TryGetValue(id, out var name))
        {
            return name;
        }
        return id ?? string.Empty;
    }

    /// <summary>将 KLID 尾部四位语言子码映射为可读语言名；未知返回 null。</summary>
    public static string? ToLanguageName(string? id)
    {
        if (id is null)
        {
            return null;
        }
        return Known.TryGetValue(id, out var name) ? name : null;
    }
}