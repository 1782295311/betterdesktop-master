using System;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.PluginSdk;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 外观模式：统一驱动窗体/内容托盘的色调与透明度（亮色叠加透白，暗色叠加透黑，无色跟随中性协调色）。
/// 取代旧的"不实现深浅色"思路——现在由模式集中推导 WindowTint/WindowOpacity/ContentOpacity/Material，
/// 单窗口或皮肤仍可单独覆盖。
/// </summary>
public enum ThemeMode
{
    /// <summary>无色：跟随统一窗口基类的毛玻璃中性协调色（灰调半透明，即应用提取器现状）。</summary>
    None = 0,
    /// <summary>亮色：窗体叠加透白属性（白底半透明 + 透亮模糊 + 亮文字）。</summary>
    Light = 1,
    /// <summary>暗色：窗体叠加透黑属性（黑底半透明 + 透亮模糊 + 亮文字）。</summary>
    Dark = 2
}

/// <summary>
/// 全局外观服务（单一外观来源）。
/// 一切外壳窗口（继承 ShellWindow（内核宿主窗口基类））经此读取并订阅外观令牌；
/// 任何写入（主题板块控件、未来 ThemeCenter）都会触发 Changed 事件，
/// 已订阅的窗口自动重绘，实现"一处改动、全局生效、无需重启"。
/// </summary>
public interface IAppearanceService
{
    /// <summary>强调/选中色（按钮、选中态、链接）。</summary>
    Color Accent { get; set; }

    /// <summary>窗体半透明托盘的基础色调（RGB，不含 alpha）。</summary>
    Color WindowTint { get; set; }

    /// <summary>窗体外层托盘不透明度（0=全透，1=不透明）。</summary>
    double WindowOpacity { get; set; }

    /// <summary>内容/卡片托盘不透明度（0=全透，1=不透明）。</summary>
    double ContentOpacity { get; set; }

    /// <summary>圆角半径（像素）。</summary>
    double CornerRadius { get; set; }

    /// <summary>间距缩放（1=基准，影响卡片内边距/行距的乘数）。</summary>
    double SpacingScale { get; set; }

    /// <summary>字号缩放（1=基准，作用于窗口根 FontSize）。</summary>
    double FontScale { get; set; }

    /// <summary>毛玻璃材质：透亮模糊（Transparent）或系统亚克力（Acrylic，自带暗色调）。</summary>
    VibrancyStyle Material { get; set; }

    /// <summary>
    /// 毛玻璃是否走"非分层玻璃窗"路径（对应设置键 <c>appearance.material.glass</c>，默认 false）。
    /// <para>
    /// 【为什么需要它】accent 模糊（WCA_ACCENT_POLICY）画在**常规 DWM 重定向位图的透明像素**后面；
    /// 分层窗口（WPF <c>AllowsTransparency=true</c>，per-pixel alpha 自绘）走的是另一条合成路径，
    /// 模糊不被支持/不稳定——这是"配方没错、模糊就是不出现"的结构性原因。
    /// 壳面窗口基类（ShellWindow）据此在**构造期**决定是否放弃分层、改用 <c>DwmExtendFrameIntoClientArea</c> 玻璃区。
    /// </para>
    /// <para>
    /// 【为什么带默认实现】新增的可选能力：默认 false = 现行分层行为，既有实现（含测试替身）
    /// 无需改动即保持零回归；需要毛玻璃的窗口构造期读一次，改动后需重开窗口才生效。
    /// </para>
    /// </summary>
    bool GlassBackdrop => false;

    /// <summary>皮肤背景图路径；null/空表示无皮肤（仅半透明托盘）。</summary>
    string? SkinPath { get; set; }

    /// <summary>当前激活皮肤 ID（""=无 / "img:&lt;path&gt;"=图片 / "preset:&lt;name&gt;"=配色预设）。
    /// 是皮肤系统的唯一激活源，SkinPath 是其 img: 形式的兼容视图。</summary>
    string SkinActive { get; set; }

    /// <summary>当前皮肤种类（None/Image/Preset），供 UI 与插件据以分流渲染策略。</summary>
    SkinKind SkinKind { get; }

    /// <summary>图片皮肤拉伸方式（0=铺满裁切 / 1=完整居中 / 2=拉伸变形 / 3=平铺）。仅图片皮肤生效。</summary>
    int SkinImageStretch { get; set; }

    /// <summary>图片皮肤暗化强度 0–1（叠加半透明黑层，保证亮色文字可读）。</summary>
    double SkinImageDarken { get; set; }

    /// <summary>图片皮肤模糊感强度 0–1（叠加半透明白层降低图存在感；真像素模糊由 VibrancyService 窗口级提供）。</summary>
    double SkinImageBlur { get; set; }

    /// <summary>图片皮肤自身不透明度 0–1（与 WindowOpacity 叠加控制透出）。</summary>
    double SkinImageOpacity { get; set; }

    /// <summary>皮肤层级档：皮肤图相对 DWM 毛玻璃放在哪一层。true=模糊档（皮肤在 DWM 之下，被磨砂压糊）；
    /// false=清晰档（皮肤在 DWM 之上，图锐利，毛玻璃被遮）。切换即时广播 SkinChanged 让所有窗口重绘。</summary>
    bool SkinBlurBehindDwm { get; set; }

    /// <summary>主前景文字色（Color 形式，从 IThemeTokens.Foreground 提升，供快照/插件取色）。</summary>
    System.Windows.Media.Color ForegroundColor { get; }

    /// <summary>当前卡片描边主色（Color 形式，从 CardBorder 提升，供快照/插件取色）。</summary>
    System.Windows.Media.Color CardBorderColor { get; }

    /// <summary>窗口根背景画刷——基类 ShellWindow 把它同时赋给 Window.Background 与
    /// 根 ChromeBorder.Background。有皮肤时返回皮肤 <see cref="System.Windows.Media.ImageBrush"/>
    /// （UniformToFill 拉伸、冻结，可被所有窗口 XAML/代码直接消费），无皮肤时按 Mode + WindowTint +
    /// WindowOpacity 推导色调托盘。一句话：用户选的皮肤图，所有外壳窗口都把它当背景铺上。</summary>
    System.Windows.Media.Brush BackgroundBrush { get; }

    /// <summary>外观模式（无色/亮色/暗色），集中推导窗体色调与透明度。单窗口或皮肤可单独覆盖。</summary>
    ThemeMode Mode { get; set; }

    /// <summary>卡片/窗口描边强度（0=无描边，0.8=最强，默认 0.3）。驱动全局边框（含所有 Shell 窗口外边缘）透明度。</summary>
    double BorderStrength { get; set; }

    /// <summary>阴影档位（0=无，1=轻，2=中，3=强）。驱动全局卡片浮起投影的大小与浓度。</summary>
    int ShadowSize { get; set; }

    /// <summary>描边样式（0=纯色 Solid，1=顶部高光渐变 TopGlow，2=对角渐变 Diagonal，3=内描边 Inner）。</summary>
    int BorderStyle { get; set; }

    /// <summary>卡片/窗口描边画刷（由 BorderStrength + BorderStyle 实时推导：Solid=纯色；TopGlow/Diagonal=渐变；Inner=弱纯色，靠内层 Border 实现内描边）。
    /// 提升自 IThemeTokens，使 ShellWindow 基类可直接把描边应用到所有外壳窗口根 Border，实现全局统一。</summary>
    System.Windows.Media.Brush CardBorder { get; }

    /// <summary>描边厚度（设备无关像素）。默认 1.5，对齐 FrostedGlassDemo 根 Border 的 BorderThickness=1.5
    /// （比 1px 更易在深色毛玻璃上清晰可见，呈现玻璃边缘高光）。由各 Shell 窗口根 ChromeBorder 统一消费。</summary>
    double CardBorderThickness { get; }

    /// <summary>当前描边样式（0=Solid，1=TopGlow，2=Diagonal，3=Inner），供卡片据以决定是否内嵌亮线 Border。</summary>
    int CardBorderStyle { get; }

    /// <summary>卡片/窗口阴影效果（由 ShadowSize 实时推导；无阴影时返回 null）。套用到 Border.Effect。</summary>
    System.Windows.Media.Effects.Effect? CardShadow { get; }

    /// <summary>外观任意令牌变更后经内核事件总线广播（事件名 ShellEvents.AppearanceChanged，含何种令牌变更，供订阅方按需局部重绘）。</summary>
    void NotifyChanged(AppearanceChangedArgs args);
}

/// <summary>外观变更事件载荷：标记哪些维度发生变化。</summary>
public sealed class AppearanceChangedArgs : EventArgs
{
    public bool AccentChanged { get; init; }
    public bool WindowTintChanged { get; init; }
    public bool WindowOpacityChanged { get; init; }
    public bool ContentOpacityChanged { get; init; }
    public bool CornerRadiusChanged { get; init; }
    public bool SpacingChanged { get; init; }
    public bool FontScaleChanged { get; init; }
    public bool MaterialChanged { get; init; }
    public bool SkinChanged { get; init; }
    public bool BorderChanged { get; init; }
    public bool ShadowChanged { get; init; }
    public bool ThemeModeChanged { get; init; }

    public static readonly AppearanceChangedArgs All = new()
    {
        AccentChanged = true,
        WindowTintChanged = true,
        WindowOpacityChanged = true,
        ContentOpacityChanged = true,
        CornerRadiusChanged = true,
        SpacingChanged = true,
        FontScaleChanged = true,
        MaterialChanged = true,
        SkinChanged = true,
        BorderChanged = true,
        ShadowChanged = true,
        ThemeModeChanged = true
    };
}
