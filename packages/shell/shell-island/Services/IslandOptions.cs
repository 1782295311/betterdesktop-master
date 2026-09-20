// BetterDesktop.Shell.Island — 岛的运行期选项（设置键 → 强类型，唯一收口点）

using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Island.Services;

/// <summary>
/// 灵动岛选项（设置键前缀 <c>island.</c>）。
/// <para>默认值取自动效规格 §7：动效强度默认**尊重系统"允许动画"设置**（远程桌面/关闭动画时自动降档），
/// 位置默认贴菜单栏中置，三个来源默认全开（消息已就绪，开箱即用）。</para>
/// </summary>
public sealed record IslandOptions(
    bool Enabled,
    IslandMotionTier Tier,
    bool HoverExpand,
    bool IdleVisible,
    bool PasteSessionSource,
    bool ConvertSource,
    bool MediaSource,
    double OffsetX,
    double OffsetY)
{
    /// <summary>设置键：总开关。</summary>
    public const string EnabledKey = "island.enabled";

    /// <summary>设置键：动效强度（full / lite / off）。</summary>
    public const string TierKey = "island.motion";

    /// <summary>设置键：悬停展开。</summary>
    public const string HoverExpandKey = "island.hover-expand";

    /// <summary>
    /// 设置键：空闲时是否保留一枚小凸起（**默认关**：不用时完全藏进菜单栏）。
    /// <para>
    /// 2026-09-16 二轮反馈定稿：先前默认留一枚矮胶囊做"存在感"，用户实测觉得那一条挂在菜单栏下沿很碍眼
    /// （"还有一小条灵动岛，是没有完全藏到菜单栏下吗"）→ 默认改为完全收起，需要位置提示的人再自行打开。
    /// </para>
    /// </summary>
    public const string IdleVisibleKey = "island.idle-visible";

    /// <summary>
    /// 设置键：剪贴板「按序粘贴 / 按格粘」会话来源开关。
    /// <para>
    /// 【为什么不是"复制来源"】2026-09-16 用户否决了"复制即上屏"：那会把剪贴板内容暴露在屏幕上。
    /// 岛只呈现用户主动发起的按序粘贴会话进度（第几项/共几项），载荷里没有任何剪贴板内容。
    /// </para>
    /// </summary>
    public const string PasteSessionSourceKey = "island.source.paste-session";

    /// <summary>设置键：格式转换来源开关。</summary>
    public const string ConvertSourceKey = "island.source.convert";

    /// <summary>设置键：媒体播放来源开关。</summary>
    public const string MediaSourceKey = "island.source.media";

    /// <summary>设置键：水平微调（DIP，正数向右）。</summary>
    public const string OffsetXKey = "island.offset-x";

    /// <summary>设置键：纵向微调（DIP，正数向下）。</summary>
    public const string OffsetYKey = "island.offset-y";

    /// <summary>从设置服务读取（缺失键回退默认；系统关动画时默认档位降为"精简"）。</summary>
    public static IslandOptions Read(ISettingsService? settings)
    {
        var defaultTier = SystemParametersFlag() ? IslandMotionTier.Full : IslandMotionTier.Lite;

        var tierText = settings?.Get(TierKey, string.Empty);
        var tier = string.IsNullOrWhiteSpace(tierText) ? defaultTier : ParseTier(tierText, defaultTier);

        return new IslandOptions(
            Enabled: settings?.Get(EnabledKey, true) ?? true,
            Tier: tier,
            HoverExpand: settings?.Get(HoverExpandKey, true) ?? true,
            IdleVisible: settings?.Get(IdleVisibleKey, false) ?? false,
            PasteSessionSource: settings?.Get(PasteSessionSourceKey, true) ?? true,
            ConvertSource: settings?.Get(ConvertSourceKey, true) ?? true,
            MediaSource: settings?.Get(MediaSourceKey, true) ?? true,
            OffsetX: settings?.Get(OffsetXKey, 0d) ?? 0d,
            OffsetY: settings?.Get(OffsetYKey, 0d) ?? 0d);
    }

    /// <summary>解析档位文本（非法值 → 回退默认，绝不抛）。</summary>
    public static IslandMotionTier ParseTier(string? text, IslandMotionTier fallback) => text?.Trim().ToLowerInvariant() switch
    {
        "full" => IslandMotionTier.Full,
        "lite" => IslandMotionTier.Lite,
        "off" => IslandMotionTier.Off,
        _ => fallback,
    };

    /// <summary>档位文本（写入设置用）。</summary>
    public static string TierText(IslandMotionTier tier) => tier switch
    {
        IslandMotionTier.Off => "off",
        IslandMotionTier.Lite => "lite",
        _ => "full",
    };

    /// <summary>系统是否允许播放动画（关闭 → 默认降档为"精简"，不打扰也不炫技）。</summary>
    private static bool SystemParametersFlag()
    {
        try
        {
            return System.Windows.SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return true; // 读不到按允许处理（保守：宁可动，也不静默降级到"没效果"）
        }
    }
}
