namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>底层毛玻璃模式（DwmHelper 的实际调用目标）。</summary>
public enum VibrancyMode
{
    /// <summary>无磨砂。</summary>
    None,

    /// <summary>透亮模糊（blur-behind）。</summary>
    BlurBehind,

    /// <summary>系统亚克力。</summary>
    Acrylic
}

/// <summary>毛玻璃风格 → 底层参数 的纯函数映射。</summary>
public static class VibrancyMapping
{
    /// <summary>
    /// 将高层风格映射为底层模式。
    ///   - Transparent → VibrancyMode.BlurBehind（全局默认毛玻璃：透亮高斯模糊，无色调）
    ///   - Acrylic    → VibrancyMode.Acrylic（DWM 系统亚克力，自带暗色调，偏暗/可读）
    ///   - None       → VibrancyMode.None（关闭一切 DWM 磨砂，普通透明窗——dock「清晰」档专用）
    /// 注意：Transparent 必须保持 BlurBehind 语义，它是 appearance.material 默认值，
    /// 所有走基类 ApplyWindowMaterial 的窗口（设置/AppGrabber/Launchpad 等）都依赖它获得默认毛玻璃；
    /// 把它误映射成 None 会导致全局窗口失去磨砂（回归）。「真·无磨砂」用独立的 None 枚举表达。
    /// （圆角参数不再参与映射，统一走系统默认圆角。）
    /// </summary>
    public static VibrancyMode ToParams(VibrancyStyle style)
    {
        return style switch
        {
            VibrancyStyle.Acrylic => VibrancyMode.Acrylic,
            VibrancyStyle.None => VibrancyMode.None,
            _ => VibrancyMode.BlurBehind // Transparent（默认）及其他未知值 → 透亮模糊
        };
    }
}
