using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.PluginSdk;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 全局外观服务（实时、可持久化、可观察）。
/// 实现 <see cref="IAppearanceService"/>（一切外壳窗口的外观单一来源）与 <see cref="IThemeTokens"/>
/// （兼容现有设置分区/窗口的令牌消费），从 <see cref="ISettingsService"/> 的 <c>appearance.*</c> 键读写。
/// 任意 setter 变更即写回设置并触发 <see cref="IAppearanceService.Changed"/>，已订阅窗口自动重绘。
/// 不实现深/浅色模式：色调为毛玻璃中性协调色，可被皮肤覆盖。
/// </summary>
public sealed class AppearanceService : IAppearanceService, IThemeTokens
{
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    public AppearanceService(ISettingsService settings)
    {
        _settings = settings;
    }

    // ---- 强调色 / 文字（文字恒亮，保证可读性，不受主题影响） ----
    // IAppearanceService.Accent 暴露 Color（供控件/绘制）；IThemeTokens.Accent 暴露 Brush（兼容旧消费者）。
    public Color Accent
    {
        get => ParseColor(_settings.Get("appearance.accent", "#0A84FF"), Color.FromRgb(0x0A, 0x84, 0xFF));
        set
        {
            SetColor("appearance.accent", value, args: new() { AccentChanged = true });
            // 同步把 AccentBrush/AccentColor 推到 App 资源与全局字典（插件 XAML 用 DynamicResource AccentBrush 即可跟随后续主色变更），
            // 是"修改皮肤颜色也对所有窗口生效"的连锁落地。
            SyncAppResources();
        }
    }

    Brush IThemeTokens.Accent => new SolidColorBrush(Accent);

    // ---- 窗体色调与透明度 ----
    public Color WindowTint
    {
        get => ParseColor(_settings.Get("appearance.windowTint", "#1F1F22"), Color.FromRgb(0x1F, 0x1F, 0x22));
        set
        {
            SetColor("appearance.windowTint", value, args: new() { WindowTintChanged = true });
            // 同步把 WindowTintColor 推到 App 资源与全局字典；BackgroundBrush 的色调托盘缓存虽会自动失效重建
            // （Tint 签名变化），但本键供"非基线窗口"也可用 DynamicResource WindowTintColor 拿到当前色值。
            SyncAppResources();
        }
    }

    public double WindowOpacity
    {
        get => Clamp01(_settings.Get("appearance.windowOpacity", 0.2)); // 默认 = 2026-09-01 用户实测调优值
        set
        {
            // 用户手动调过透明度 → 标记，模式切换不再覆盖（透明度是全局偏好）。
            _settings.Set("appearance.opacityTouched", true);
            SetDouble("appearance.windowOpacity", value, args: new() { WindowOpacityChanged = true });
            // 同步把 WindowOpacityValue 推到 App 资源与全局字典。
            SyncAppResources();
        }
    }

    public double ContentOpacity
    {
        get => Clamp01(_settings.Get("appearance.contentOpacity", 0.502));
        set => SetDouble("appearance.contentOpacity", value, args: new() { ContentOpacityChanged = true });
    }

    // ---- 几何尺度 ----
    public double CornerRadius
    {
        // 默认 8：对齐 FrostedGlassDemo 实证基准（窗口圆角与 DWM 系统默认圆角重合，避免双重窗口错觉）。
        get => _settings.Get("appearance.cornerRadius", 8.0);
        set => SetDouble("appearance.cornerRadius", value, args: new() { CornerRadiusChanged = true });
    }

    public double SpacingScale
    {
        get => _settings.Get("appearance.spacingScale", 1.0);
        set => SetDouble("appearance.spacingScale", value, args: new() { SpacingChanged = true });
    }

    public double FontScale
    {
        get => _settings.Get("appearance.fontScale", 1.0);
        set => SetDouble("appearance.fontScale", value, args: new() { FontScaleChanged = true });
    }

    // ---- IThemeTokens 字号语义令牌（供壳表面统一取用；后续若接入 FontScale 在此整体换算即可） ----
    public double FontSizeInput => 14;
    public double FontSizeBody => 13;
    public double FontSizeCaption => 12;
    public double FontSizeSmall => 11;

    // ---- 毛玻璃材质 ----
    public VibrancyStyle Material
    {
        get => Enum.TryParse<VibrancyStyle>(_settings.Get("appearance.material", "Transparent"), out var m)
            ? m : VibrancyStyle.Transparent;
        set => SetString("appearance.material", value.ToString(), args: new() { MaterialChanged = true });
    }

    // ---- 皮肤 ----
    // 唯一皮肤激活源：appearance.skin.active（""=无 / "img:<path>"=图片 / "preset:<name>"=配色预设）。
    // 旧 SkinPath 属性保留为向后兼容：写时转 img: 前缀、读时从 active 解析，语义不变。
    public string SkinActive
    {
        get => _settings.Get("appearance.skin.active", "") ?? "";
        set
        {
            var v = value ?? "";
            if ((_settings.Get("appearance.skin.active", "") ?? "") == v) return;
            _settings.Set("appearance.skin.active", v);
            // 预设皮肤：激活时整体覆盖对应 appearance.* 键（WindowTint/Accent/描边）。
            // 图片/无皮肤不覆盖（色调由各自逻辑推导）。这是预设应用的唯一执行点（单一真相源）。
            if (v.StartsWith("preset:", StringComparison.Ordinal))
            {
                ApplyPreset(v.Substring(7));
            }
            Raise(new() { SkinChanged = true });
            SyncAppResources();
        }
    }

    /// <summary>内置配色预设表（无图，驱动 WindowTint/Accent/描边）。皮肤系统的预设单一真相源。</summary>
    private static readonly IReadOnlyDictionary<string, (string Tint, string Accent, int BorderStyle, double BorderStrength)> Presets = new Dictionary<string, (string, string, int, double)>
    {
        ["graphite"] = ("#1F1F22", "#0A84FF", 2, 0.5),
        ["midnight"] = ("#0B1020", "#5E5CE6", 1, 0.5),
        ["sakura"]   = ("#2A1A22", "#FF375F", 2, 0.5),
        ["mint"]     = ("#10231C", "#30D158", 0, 0.5)
    };

    private void ApplyPreset(string key)
    {
        if (!Presets.TryGetValue(key, out var p)) return;
        WindowTint = ParseColor(p.Tint, Colors.Transparent);
        Accent = ParseColor(p.Accent, Colors.Transparent);
        BorderStyle = p.BorderStyle;
        BorderStrength = p.BorderStrength;
    }

    /// <summary>向后兼容：旧调用写 SkinPath 即激活图片皮肤（img:<path>）；null/空清除。
    /// 读从 skin.active 解析 img: 前缀得到图片路径。</summary>
    public string? SkinPath
    {
        get
        {
            var a = SkinActive;
            return a.StartsWith("img:", StringComparison.Ordinal)
                ? a.Substring(4)
                : null;
        }
        set => SkinActive = string.IsNullOrWhiteSpace(value) ? "" : "img:" + value;
    }

    /// <summary>当前皮肤种类：None=无 / Image=图片 / Preset=配色预设。供 UI/插件据以分流。</summary>
    public SkinKind SkinKind
    {
        get
        {
            var a = SkinActive;
            if (string.IsNullOrWhiteSpace(a)) return SkinKind.None;
            return a.StartsWith("img:", StringComparison.Ordinal) ? SkinKind.Image : SkinKind.Preset;
        }
    }

    // ---- 图片皮肤参数（appearance.skin.* 子键，仅图片皮肤生效） ----
    public int SkinImageStretch
    {
        // 默认 1（Uniform 完整居中）：皮肤按原比例完整显示、居中、留边、不变形；
        // 窗口可自由拉伸去适应皮肤，而非皮肤被拉伸变形去塞满窗口（与"dock 除外、其余窗口可缩放"一致）。
        get => ClampInt(_settings.Get("appearance.skin.imgStretch", 1), 0, 3);
        set
        {
            SetInt("appearance.skin.imgStretch", value, args: new() { SkinChanged = true });
            // 拉伸变化必须重推皮肤 Brush：BackgroundBrush 缓存 key 只看 path/blur，不感知 stretch，
            // 不刷键则 DynamicResource 绑定继续指旧 Brush（旧拉伸）。
            SyncSkinResources();
        }
    }

    public double SkinImageDarken
    {
        get => Clamp01(_settings.Get("appearance.skin.imgDarken", 0.25));
        set
        {
            SetDouble("appearance.skin.imgDarken", value, args: new() { SkinChanged = true });
            SyncSkinResources();
        }
    }

    public double SkinImageBlur
    {
        get => Clamp01(_settings.Get("appearance.skin.imgBlur", 0.0));
        set => SetDouble("appearance.skin.imgBlur", value, args: new() { SkinChanged = true });
    }

    public double SkinImageOpacity
    {
        get => Clamp01(_settings.Get("appearance.skin.imgOpacity", 1.0));
        set
        {
            SetDouble("appearance.skin.imgOpacity", value, args: new() { SkinChanged = true });
            // 不透明度变化必须重推皮肤 Brush（缓存 key 不感知 opacity，不刷则旧 Brush 沿用旧不透明度）。
            SyncSkinResources();
        }
    }

    /// <summary>
    /// 皮肤层级档：皮肤图本身是否做模糊处理（WPF+DWM 模型下，背景永远在 DWM 合成之下，
    /// 无法用 Z-order 区分清晰/模糊，只能对图本身做模糊）。
    /// <list type="bullet">
    ///   <item><description><c>true</c>（默认，模糊档）：皮肤图套 <see cref="System.Windows.Media.Effects.BlurEffect"/>，
    ///     图在合成层即发糊，底层 DWM 毛玻璃透过半透明图透出，形成磨砂皮肤观感。</description></item>
    ///   <item><description><c>false</c>（清晰档）：皮肤图原样锐利显示，不模糊。</description></item>
    /// </list>
    /// 切换即时经 SkinChanged 广播，<see cref="BackgroundBrush"/> 返回不同 Brush 实例，所有外壳窗口重绘。
    /// </summary>
    public bool SkinBlurBehindDwm
    {
        get => _settings.Get("appearance.skin.blurBehindDwm", 1) == 1;
        set
        {
            SetInt("appearance.skin.blurBehindDwm", value ? 1 : 0, args: new() { SkinChanged = true });
            // 档位变化必须重推 App.Resources["SkinBackgroundBrush"]：所有外壳窗口的根 Border 用
            // DynamicResource 绑这个键（与 CardBorderBrush 对称），不刷键资源永远指切档前的旧 Brush。
            // 走轻量 SyncSkinResources（只刷皮肤键，不重推整张模式表），避免切档抖动。
            SyncSkinResources();
        }
    }

    /// <summary>
    /// 窗口根背景画刷：所有外壳窗口都把它当背景铺上。
    /// 有图片皮肤时返回用户选的那张图（冻结的 ImageBrush，按 skin.imgStretch 拉伸、按 skin.imgOpacity 调不透明度）；
    /// 按 <see cref="SkinBlurBehindDwm"/> 决定清晰还是模糊：
    /// <list type="bullet">
    ///   <item><description>清晰档（false）：原图 ImageBrush 直接返回，图锐利。</description></item>
  ///   <item><description>模糊档（true，默认）：用 <see cref="System.Windows.Media.VisualBrush"/> 包一个带
  ///     <see cref="System.Windows.Media.Effects.BlurEffect"/> 的 Image（源=同一张冻结图，不预渲染、不做透明边放大，
  ///     与清晰档拉伸语义完全一致，不会放大裁切）——底层 DWM 毛玻璃透过半透明图透出，形成磨砂感。</description></item>
    /// </list>
    /// 这是 WPF+DWM 模型下能产生真实视觉差异的做法（WPF 背景永远在 DWM 合成之下，无法用 Z-order 区分清晰/模糊，
    /// 只能对图本身做模糊处理）。
    /// 有配色预设时按预设推导的色调托盘（WindowTint/WindowOpacity 已被预设整体覆盖，此处与无皮肤同路）；
    /// 无皮肤时按 Mode + WindowTint + WindowOpacity 推导色调托盘。
    /// 用缓存：皮肤图只在路径/档位变化时重建，避免每次读取重复解码；解码限制尺寸上限防超大图爆显存。
    /// </summary>
    public Brush BackgroundBrush
    {
        get
        {
            var path = SkinPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var blur = SkinBlurBehindDwm;
                // 缓存键加入档位：清晰/模糊返回的是不同 Brush 实例，切档必须失效重建。
                if (_bgBrushForPath == path && _bgBrushCache is not null && _bgBrushBlur == blur)
                {
                    return _bgBrushCache;
                }
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        // 源图单独解码并 Freeze 缓存一次（DecodePixelWidth=2560 防大图爆显存），
                        // 清晰/模糊档复用同一张冻结源图。
                        var img = GetCachedBitmap(path);
                        if (img is null) return FallbackTintBrush();

                        Brush brush;
                        if (blur)
                        {
                            // 模糊档：用 VisualBrush 包一个带 BlurEffect 的 Image（Image 源=冻结源图）。
                            // VisualBrush 按目标区域映射、不存在"放大+裁边"问题（与清晰档 ImageBrush 同一拉伸语义）。
                            // 同一 VisualBrush 实例跨窗口共享（缓存），无需每窗重建；BlurEffect 在合成时实时应用。
                            var visual = new System.Windows.Controls.Image
                            {
                                Source = img,
                                Stretch = Stretch.Fill // 充满 VisualBrush 的 1×1 视口，由 VisualBrush.Stretch 决定最终铺法
                            };
                            visual.Effect = new System.Windows.Media.Effects.BlurEffect
                            {
                                Radius = 28,
                                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
                            };
                            var vb = new System.Windows.Media.VisualBrush(visual)
                            {
                                Stretch = MapStretch(SkinImageStretch),
                                Opacity = SkinImageOpacity
                            };
                            if (SkinImageStretch == 3) // Tile 平铺
                            {
                                vb.TileMode = TileMode.Tile;
                                vb.Viewport = new System.Windows.Rect(0, 0, 0.25, 0.25);
                                vb.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
                            }
                            brush = vb;
                        }
                        else
                        {
                            // 清晰档：原图 ImageBrush 直接返回，图锐利、可 Freeze 跨窗口共享。
                            var ib = new ImageBrush(img)
                            {
                                Stretch = MapStretch(SkinImageStretch),
                                Opacity = SkinImageOpacity
                            };
                            if (SkinImageStretch == 3) // Tile 平铺
                            {
                                ib.TileMode = TileMode.Tile;
                                ib.Viewport = new System.Windows.Rect(0, 0, 0.25, 0.25);
                                ib.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
                            }
                            ib.Freeze();
                            brush = ib;
                        }
                        _bgBrushCache = brush;
                        _bgBrushForPath = path;
                        _bgBrushBlur = blur;
                        return brush;
                    }
                }
                catch
                {
                    // 皮肤加载失败回退色调托盘
                }
            }

            return FallbackTintBrush();
        }
    }

    /// <summary>色调托盘回退 Brush：按 (Tint, Opacity) 签名缓存，避免重复创建。</summary>
    private Brush FallbackTintBrush()
    {
        var curTint = WindowTint;
        var curOpacity = WindowOpacity;
        if (_tintBrushCache is not null
            && curTint == _cachedTint
            && System.Math.Abs(curOpacity - _cachedOpacity) < 0.001)
        {
            return _tintBrushCache;
        }
        var tintBrush = MakeBrush(curTint, curOpacity);
        tintBrush.Freeze();
        _tintBrushCache = tintBrush;
        _cachedTint = curTint;
        _cachedOpacity = curOpacity;
        return tintBrush;
    }

    /// <summary>皮肤位图缓存：同一路径只解码一次并 Freeze，清晰/模糊档复用，避免切档反复重解码。
    /// 路径变化（换皮肤）自动失效重建。</summary>
    private BitmapSource? GetCachedBitmap(string path)
    {
        if (_bgBitmap is not null && _bgBitmapForPath == path) return _bgBitmap;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path, UriKind.Absolute);
            // 解码尺寸上限：避免 8K+ 大图撑爆显存（视觉铺满无需原分辨率）。
            img.DecodePixelWidth = 2560;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            _bgBitmap = img;
            _bgBitmapForPath = path;
            return img;
        }
        catch
        {
            _bgBitmap = null;
            _bgBitmapForPath = null;
            return null;
        }
    }

    // 色调托盘分支缓存字段（紧跟 BackgroundBrush 之后便于关联阅读）。
    private Brush? _tintBrushCache;
    private Color _cachedTint;
    private double _cachedOpacity;

    private static Stretch MapStretch(int v) => v switch
    {
        1 => Stretch.Uniform,     // 完整居中留边（默认档：皮肤不变形）
        2 => Stretch.Fill,        // 拉伸变形
        3 => Stretch.Uniform,     // Tile 由 TileMode 处理，Stretch 用 Uniform 即可
        _ => Stretch.UniformToFill // 0 铺满裁切：用户仍可在设置里手动选，仅不再是默认
    };

    private Brush? _bgBrushCache;
    private string? _bgBrushForPath;
    private bool _bgBrushBlur; // 缓存时对应的 SkinBlurBehindDwm 档位，切档必须失效重建

    private BitmapSource? _bgBitmap;   // 皮肤位图单独缓存（Freeze），清晰/模糊档复用，避免切档反复重解码
    private string? _bgBitmapForPath;


    // ---- 外观模式：集中推导窗体/内容托盘的色调与透明度 ----
    // 无色(None)=应用提取器现状，亮色(Light)=叠加透白，暗色(Dark)=叠加透黑。
    public ThemeMode Mode
    {
        get
        {
            var v = (int)_settings.Get("appearance.themeMode", 0);
            return v is < 0 or > 2 ? ThemeMode.None : (ThemeMode)v;
        }
        set
        {
            var v = (int)value;
            if (v == (int)Mode) return;
            _settings.Set("appearance.themeMode", v);
            // 模式切换即按模式重推窗体/内容托盘的色调与透明度，并广播对应维度变更。
            ApplyModeDefaults(value);
            Raise(new() { ThemeModeChanged = true, WindowTintChanged = true, WindowOpacityChanged = true, ContentOpacityChanged = true });
        }
    }

    /// <summary>按模式把窗体色调/内容透明度/材质写到底层键（供 WindowBackground 等令牌读取）。
    /// 模式默认透明度只在用户未手动调过（appearance.opacityTouched=false）时写入——透明度是用户全局偏好，
    /// 手动调过后切模式不得覆盖（否则用户调到最低后切模式会被重置）。
    /// 三种模式刻意拉开区分度：None=无色（窗口背景近透明、毛玻璃透出，不铺任何托盘）；Light=柔和透白；
    /// Dark=纯黑透黑。</summary>
    private void ApplyModeDefaults(ThemeMode mode)
    {
        // 用户是否手动调过透明度（持久化标志；首次/默认 false）。
        var opacityTouched = _settings.Get("appearance.opacityTouched", false);
        switch (mode)
        {
            case ThemeMode.Light:
                _settings.Set("appearance.windowTint", "#E8E8EA");   // 透白
                if (!opacityTouched) _settings.Set("appearance.windowOpacity", 0.55); // 柔和，不抢戏
                _settings.Set("appearance.contentOpacity", 0.82);
                _settings.Set("appearance.material", "Transparent");
                break;
            case ThemeMode.Dark:
                _settings.Set("appearance.windowTint", "#000000");   // 纯黑透黑（与 None 拉开明度差距）
                if (!opacityTouched) _settings.Set("appearance.windowOpacity", 0.80);
                _settings.Set("appearance.contentOpacity", 0.62);
                _settings.Set("appearance.material", "Transparent");
                break;
            default: // None：无色——窗口背景近透明通透（不铺厚灰层，避免"无色却偏灰"）。
                _settings.Set("appearance.windowTint", "#1F1F22");   // 仅占位（内容层中性底用），窗口层不使用
                if (!opacityTouched) _settings.Set("appearance.windowOpacity", 0.35); // 比 Dark(0.8) 明显更透，无色=通透而非灰层
                _settings.Set("appearance.contentOpacity", 0.30);
                _settings.Set("appearance.material", "Transparent");
                break;
        }
        SyncAppResources();
    }

    /// <summary>把 App.xaml 中的 DynamicResource 键（macOS 风控件画刷 + 主题令牌画刷）按当前 <see cref="Mode"/>
    /// 推入 <see cref="System.Windows.Application.Current.Resources"/>，使亮色/暗色/无色模式一键切换控件外观
    /// 与窗口/面板/文字颜色。亮色=浅底深字；暗/无色=深底亮字。
    /// 在模式切换（<see cref="ApplyModeDefaults"/>）与启动（<see cref="Initialize"/>）时调用。
    /// 仅当 Application 已就绪时推送——保证设置窗口等 XAML 在 App 资源就位后能正确解析 DynamicResource。</summary>
    private void SyncAppResources()
    {
        if (System.Windows.Application.Current is not { } app) return;
        var mode = Mode;
        // 亮色模式：浅底 + 深色字；其余（暗/无色）：深底 + 亮色字。
        var fg = mode == ThemeMode.Light ? "#FF1A1A1C" : "#FFECECEC";
        var bg = mode == ThemeMode.Light ? "#FFF3F3F5" : "#FF2C2C2E";
        var bgHover = mode == ThemeMode.Light ? "#FFE3E3E6" : "#FF48484A";
        var bgPressed = mode == ThemeMode.Light ? "#FFD8D8DC" : "#FF343436";
        var border = mode == ThemeMode.Light ? "#FFC8C8CC" : "#FF48484A";
        var track = mode == ThemeMode.Light ? "#FFD2D2D6" : "#FF48484A";
        var popupBg = mode == ThemeMode.Light ? "#FFF6F6F8" : "#FF2C2C2E";
        var popupBorder = mode == ThemeMode.Light ? "#FFC8C8CC" : "#FF48484A";
        var popupHover = mode == ThemeMode.Light ? "#FFE3E3E6" : "#FF3A3A3C";

        // macOS 风控件画刷
        SetBrush(app, "ControlForeground", fg);
        SetBrush(app, "ControlBackground", bg);
        SetBrush(app, "ControlBackgroundHover", bgHover);
        SetBrush(app, "ControlBackgroundPressed", bgPressed);
        SetBrush(app, "ControlBorder", border);
        SetBrush(app, "ControlTrack", track);
        SetBrush(app, "PopupBackground", popupBg);
        SetBrush(app, "PopupBorder", popupBorder);
        SetBrush(app, "PopupItemHover", popupHover);

        // 主题令牌画刷（供设置窗口等 XAML 直接 DynamicResource 引用，随模式一键切换，消除"特例独行"）。
        // 窗口根背景（ThemeWindowBackground）随模式：亮色=柔和透白，暗色=纯黑透黑，无色=None=近透明。
        // 注意：有皮肤时窗口根由基类直接用 BackgroundBrush（=皮肤图 ImageBrush），并不读这里；
        // 这里只驱动面板/内容层与无皮肤时的窗体托盘色。
        // 面板/内容层：None 保持中性深（亮字可读）；文字恒亮（亮色模式转深）。
        var winBg = mode == ThemeMode.Light ? "#E8E8EA" : mode == ThemeMode.Dark ? "#000000" : "#01000000";
        var panelBg = mode == ThemeMode.Light ? "#F2F2F4" : mode == ThemeMode.Dark ? "#1A1A1C" : "#262629";
        var contentBg = mode == ThemeMode.Light ? "#F6F6F8" : mode == ThemeMode.Dark ? "#222224" : "#2A2A2E";
        var fore = mode == ThemeMode.Light ? "#1A1A1C" : "#F2F2F2";
        // 解释性/次要文字：此前 #C4C4C4(≈77% 亮) 在深色毛玻璃上显灰、可读性差。
        // 提亮到 #E2E2E6(≈89% 亮)，与主文字 #F2F2F2 拉开但仍清晰可辨；亮色模式同步提亮到 #6E6E73。
        var muted = mode == ThemeMode.Light ? "#6E6E73" : "#E2E2E6";
        var sepColor = mode == ThemeMode.Light ? "#000000" : "#B0B0B0";
        var sepOpacity = mode == ThemeMode.Light ? 0.12 : 0.25;

        SetBrush(app, "ThemeWindowBackground", winBg);
        // 面板背景透明度绑定"窗体不透明度"滑块（WindowOpacity）：面板/弹层根背景随滑块实时半透明，
        // 透出 DWM 毛玻璃与桌面，消除"固定深色死黑、调透明度无反应"的问题。
        // （模式仅决定面板基调色深浅；内容层 ThemeContentBackground 保持稳定不透明，避免内容装饰闪烁。）
        SetBrush(app, "ThemePanelBackground", panelBg, WindowOpacity);
        SetBrush(app, "ThemeContentBackground", contentBg);
        SetBrush(app, "ThemeForeground", fore);
        SetBrush(app, "ThemeMutedForeground", muted);
        SetBrush(app, "ThemeSeparator", sepColor, sepOpacity);

        // 卡片级描边/阴影推成 App 动态资源（供 Dock/AppGrabber/Launchpad 卡片 Border 直接
        // DynamicResource 绑定），使描边强度/阴影档位变化时所有窗口卡片自动刷新，
        // 淘汰"OnAppearanceContentChanged 重建面板"模式（仅结构变化才需重建）。
        SetAppResource(app, "CardBorderBrush", CardBorder);
        SetAppResource(app, "CardShadowEffect", CardShadow); // 档位 0 时为 null（无阴影）

        // 窗口根背景画刷：有皮肤时即用户选的皮肤图（ImageBrush），所有外壳窗口通过基类 BackgroundBrush
        // 统一铺上。这里也推一份到 App 资源，供需要 DynamicResource 绑定的场景（如设置面板根 Border）。
        SetAppResource(app, "BackgroundBrush", BackgroundBrush);

        // ===== 皮肤扩散令牌（影响范围不困在窗口根背景内） =====
        // 让插件/未来功能界面经 DynamicResource 引用即可跟随皮肤，无需逐个适配：
        // - SkinBackgroundBrush：与根背景完全一致的 Brush（皮肤图或色调托盘），插件面板可透出同一张皮肤图；
        // - SkinIsActive：是否处于皮肤模式（图片/配色），插件据以决定"透出皮肤"还是"中性托盘"；
        // - SkinDarkenOpacity：皮肤暗化强度，插件内部覆盖层可复用同一值保持视觉一致；
        // - SkinAccentFromSkin：当前主色（配色皮肤=预设 Accent；否则=Accent），插件自定义绘制取色用。
        SyncSkinResources(app);

        // 同步写入插件宿主共享的全局主题字典：插件 XAML 的 DynamicResource 令牌与内置窗口一致。
        // 把 App 资源中所有主题令牌（含皮肤扩散键）复制到全局字典（供 PluginHostWindow 合并）。
        var global = BetterDesktop.Shell.Core.Surface.ThemeResourceProvider.Ensure();
        foreach (var key in new[]
                 {
                     "ControlForeground", "ControlBackground", "ControlBackgroundHover", "ControlBackgroundPressed",
                     "ControlBorder", "ControlTrack", "PopupBackground", "PopupBorder", "PopupItemHover",
                     "ThemeWindowBackground", "ThemePanelBackground", "ThemeContentBackground",
                     "ThemeForeground", "ThemeMutedForeground", "ThemeSeparator",
                     "CardBorderBrush", "CardShadowEffect", "BackgroundBrush",
                     "SkinBackgroundBrush", "SkinIsActive", "SkinDarkenOpacity", "SkinAccentFromSkin",
                     "AccentBrush", "AccentColor", "WindowTintColor", "WindowOpacityValue"
                 })
        {
            if (app.Resources[key] is { } v) global[key] = v;
        }
    }

    /// <summary>
    /// 只刷皮肤相关资源键（<see cref="BackgroundBrush"/> / <see cref="SkinBackgroundBrush"/> 等），
    /// 不重推整张模式表（控件画刷/主题令牌/描边）。供切皮肤档位（清晰/模糊）、换皮肤、皮肤参数微调时调用——
    /// 避免 <see cref="SyncAppResources"/> 全量重建约 30 个资源键带来的抖动（切档延迟的根因之一）。
    /// </summary>
    private void SyncSkinResources(System.Windows.Application app)
    {
        // 窗口根背景画刷：有皮肤时即用户选的皮肤图（ImageBrush，模糊档为预渲染模糊位图 ImageBrush），
        // 所有外壳窗口通过基类 BackgroundBrush
        // 统一铺上；也推一份到 App 资源，供需要 DynamicResource 绑定的场景（如设置面板根 Border）。
        SetAppResource(app, "BackgroundBrush", BackgroundBrush);
        SetAppResource(app, "SkinBackgroundBrush", BackgroundBrush);
        SetAppResource(app, "SkinIsActive", SkinKind != SkinKind.None);
        SetAppResource(app, "SkinDarkenOpacity", SkinImageDarken);
        // 皮肤层级档：true=模糊档(图被模糊处理/磨砂感)、false=清晰档(图锐利)。供插件/子窗口引用。
        SetAppResource(app, "SkinBlurBehindDwm", SkinBlurBehindDwm);
        SetAppResource(app, "SkinAccentFromSkin", new SolidColorBrush(Accent));

        // 同步写入插件宿主共享的全局字典（皮肤相关键），供 PluginHostWindow 合并。
        var global = BetterDesktop.Shell.Core.Surface.ThemeResourceProvider.Ensure();
        foreach (var key in new[] { "BackgroundBrush", "SkinBackgroundBrush", "SkinIsActive", "SkinDarkenOpacity", "SkinAccentFromSkin" })
        {
            if (app.Resources[key] is { } v) global[key] = v;
        }
    }

    /// <summary>启动入口：把当前已保存的外观模式（含控件画刷与主题令牌画刷）推入 App 资源，
    /// 使首屏即应用上次保存的亮色/暗色/无色模式，无需等待任何窗口加载或模式切换。</summary>
    public void Initialize()
    {
        SyncAppResources();
    }

    /// <summary>只刷皮肤相关资源键（轻量，不重推整张模式表）。供皮肤档位/参数变更调用，消除切档抖动。</summary>
    private void SyncSkinResources()
    {
        if (System.Windows.Application.Current is not { } app) return;
        SyncSkinResources(app);
    }

    private static void SetBrush(System.Windows.Application app, string key, string color, double? opacity = null)
    {
        // 直接替换资源条目（而非原地改 Color），确保 DynamicResource 重新解析并刷新所有引用控件。
        // App.xaml 资源字典里的 SolidColorBrush 在应用启动后会被 Frozen，原地改 Color 会静默失效。
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        if (opacity is { } o) brush.Opacity = o;
        SetAppResource(app, key, brush);
    }

    /// <summary>
    /// 安全写入 App 级资源：若 key 已存在于某个合并字典（MergedDictionary，如 ThemeResourceProvider.GlobalDictionary
    /// 经 PluginHostWindow 合并进窗口资源后，App.xaml 也可能合并主题字典）中，WPF 会将其标记为只读，
    /// 直接 <c>app.Resources[key] = x</c> 会抛 InvalidOperationException（"无法在只读对象上设置属性"），
    /// 导致设置窗口 Dispatcher 崩溃→黑屏/点击无反应。
    /// 先 Remove 本地同名项（仅移除本地条目，不触碰只读合并项），再 Add 写入本地新条目——
    /// 本地项优先于合并字典被 DynamicResource 解析，且不修改只读条目。
    /// </summary>
    private static void SetAppResource(System.Windows.Application app, string key, object? value)
    {
        app.Resources.Remove(key);
        app.Resources.Add(key, value);
    }

    // ---- 描边强度 ----
    public double BorderStrength
    {
        // 默认 0.5：对齐 FrostedGlassDemo 对角渐变观感（左上纯白→右下透明，等效约 0.5 强度跨度）；
        // UI 上限 0.8，渐变样式在高位自动截断为不透明。
        get => Clamp01(_settings.Get("appearance.borderStrength", 0.5));
        set
        {
            SetDouble("appearance.borderStrength", value, args: new() { BorderChanged = true });
            // 描边强度变化必须同步把新推导的 CardBorder 推回 App 资源（CardBorderBrush），
            // 否则 XAML/代码窗口的 DynamicResource 绑定永远指向启动时的旧 Brush，
            // 设置面板拖动"描边强度"窗口无反应（此前断链根因）。
            SyncAppResources();
        }
    }

    // ---- 阴影档位：已按需求移除阴影效果，固定为 0（无阴影），setter 不持久化、不广播，仅保留契约兼容。 ----
    public int ShadowSize
    {
        get => 0;
        set
        {
            // 阴影效果已移除：忽略任何设置写入，强制保持 0，使 CardShadow 始终返回 null（无阴影）。
            // 保留 setter 以满足 IAppearanceService 契约（插件/旧消费者仍可赋值而不抛异常）。
        }
    }

    // ---- 描边样式（0 Solid / 1 TopGlow / 2 Diagonal / 3 Inner） ----
    public int BorderStyle
    {
        get
        {
            // 默认 2 对角渐变：对齐 FrostedGlassDemo 受光面体积感描边（左上白→右下透明）。
            var v = (int)_settings.Get("appearance.borderStyle", 2);
            return v < 0 ? 0 : v > 3 ? 3 : v;
        }
        set
        {
            var v = value < 0 ? 0 : value > 3 ? 3 : value;
            SetDouble("appearance.borderStyle", v, args: new() { BorderChanged = true });
            // 描边样式变化必须同步把新推导的 CardBorder 推回 App 资源（CardBorderBrush），
            // 否则窗口 DynamicResource 绑定不刷新、设置面板拖动"描边样式"窗口无反应（此前断链根因）。
            SyncAppResources();
        }
    }

    // ===== IThemeTokens（背景由 BackgroundBrush 统一驱动，前景/输入随模式统一） =====
    // 皮肤模式下 BackgroundBrush 直接返回用户选的皮肤图（ImageBrush），所有外壳窗口根 Border 都铺它；
    // 面板/内容层（SideBorder、卡片）仍用半透明黑叠层，保证在皮肤图上文字可读、层次分明。
    private bool HasSkin => !string.IsNullOrWhiteSpace(SkinPath);

    Brush IThemeTokens.WindowBackground => BackgroundBrush;

    public Brush PanelBackground => HasSkin
        ? new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00))   // 半透明黑叠层，皮肤透出
        : MakeBrush(Lighten(WindowTint, 0.06), Math.Min(1, WindowOpacity + 0.05));

    public Brush ContentBackground => HasSkin
        ? new SolidColorBrush(Color.FromArgb(0x0F, 0x00, 0x00, 0x00))   // 更浅叠层，内容区更透
        : MakeBrush(WindowTint, ContentOpacity);

    // 前景/输入统一从模式推导：亮色模式文字转深、输入底转浅；暗/无色模式恒亮文字 + 中性暗底。
    public Brush Foreground => Mode == ThemeMode.Light
        ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16))
        : new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    public Brush MutedForeground => Mode == ThemeMode.Light
        ? new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))
        : new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE6));
    public Brush Separator => Mode == ThemeMode.Light
        ? new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)) { Opacity = 0.12 }
        : new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)) { Opacity = 0.25 };

    public Brush InputBackground => Mode == ThemeMode.Light
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) { Opacity = 0.85 }
        : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)) { Opacity = 0.9 };
    public Brush InputBorder => Mode == ThemeMode.Light
        ? new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)) { Opacity = 0.18 }
        : new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));

    /// <summary>卡片描边画刷：由 BorderStrength + BorderStyle 实时推导。
    /// 基准对齐 FrostedGlassDemo 受光面体积感描边（受光面=左上纯亮、背光面=右下透明）：
    /// 0 Solid=纯色半透明白；1 TopGlow=垂直渐变（上亮下暗）；2 Diagonal=对角渐变（对齐 demo 四色标
    /// #FFFFFFFF→#CCFFFFFF→#33FFFFFF→#00FFFFFF）；3 Inner=弱纯色（内描边亮线由卡片内层 Border 承担）。</summary>
    public Brush CardBorder
    {
        get
        {
            var s = BorderStrength;
            var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
            return BorderStyle switch
            {
                // 顶部高光：垂直渐变，上亮下暗（模拟玻璃顶部受光），强度随 BorderStrength。
                1 => new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(white, 0) { Color = WithAlpha(white, Math.Min(1, s * 1.6)) },
                        new GradientStop(white, 1) { Color = WithAlpha(white, s * 0.35) }
                    }
                },
                // 对角渐变：严格对齐 FrostedGlassDemo 四色标（左上纯白→右下全透明），呈现玻璃受光面体积感。
                2 => new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(white, 0) { Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
                        new GradientStop(white, 0.35) { Color = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) },
                        new GradientStop(white, 0.70) { Color = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) },
                        new GradientStop(white, 1) { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) }
                    }
                },
                // 3 Inner 与 0 Solid 都用纯色弱描边（Inner 的"内"由卡片内层 Border 实现）。
                // Solid 基准锚定 cairoshell 原版 dock 高光描边 #4FFFFFFF（白 31%）：
                // 下限不低于 0.31，保证"大窗口本身"边缘高光至少与原版 dock 一致，仍可随 BorderStrength 调强。
                _ => new SolidColorBrush(white) { Opacity = Math.Max(s, 0.31) }
            };
        }
    }

    /// <summary>描边厚度（设备无关像素）。默认 1.5，对齐 FrostedGlassDemo 根 Border 的 BorderThickness=1.5
    /// （比 1px 更易在深色毛玻璃上清晰可见，呈现玻璃边缘高光）。由各 Shell 窗口根 ChromeBorder 统一消费。</summary>
    public double CardBorderThickness => _settings.Get("appearance.borderThickness", 1.5);

    /// <summary>当前描边样式枚举值（0/1/2/3），供卡片据以决定是否内嵌亮线 Border。</summary>
    public int CardBorderStyle => BorderStyle;

    /// <summary>主前景文字色（Color）。从 <see cref="Foreground"/> 提取，供快照/插件取色。</summary>
    public Color ForegroundColor => Foreground is SolidColorBrush fb ? fb.Color
        : Mode == ThemeMode.Light ? Color.FromRgb(0x14, 0x14, 0x16) : Color.FromRgb(0xF2, 0xF2, 0xF2);

    /// <summary>当前卡片描边主色（Color）。从 <see cref="CardBorder"/> 提取首色，供快照/插件取色。</summary>
    public Color CardBorderColor => CardBorder is SolidColorBrush cb ? cb.Color : Colors.White;

    private static Color WithAlpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Round(Clamp01(alpha) * 255), c.R, c.G, c.B);

    /// <summary>窗口外边缘阴影：已按需求移除阴影效果，始终返回 null（无阴影）。
    /// 之前按 ShadowSize 档位推导 DropShadowEffect（cairoshell 原版 dock 的 ShadowDepth=0 纯漫射投影），
    /// 现 ShadowSize 固定为 0，故此处恒返回 null。令牌仍保留以满足 IAppearanceService 契约（插件可读取，
    /// 恒为 null = 不套阴影）。</summary>
    public System.Windows.Media.Effects.Effect? CardShadow => null;

    // ===== 事件 =====
    public event EventHandler<AppearanceChangedArgs>? Changed;

    public void NotifyChanged(AppearanceChangedArgs args) => Changed?.Invoke(this, args);

    // ===== 内部 =====
    private void SetColor(string key, Color value, AppearanceChangedArgs args)
    {
        // 键未设置（空白）视为首次写入，直接落盘并广播，避免 fallback==value 导致跳过。
        var stored = _settings.Get(key, "");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var cur = ParseColor(stored, value);
            if (cur == value) return;
        }
        _settings.Set(key, FormatColor(value));
        Raise(args);
    }

    private void SetDouble(string key, double value, AppearanceChangedArgs args)
    {
        var v = Math.Round(value, 3);
        // 键未设置视为首次写入（避免默认 0 与未设键误判相等而跳过）。
        if (_settings.Get(key, double.NaN) is double existing && !double.IsNaN(existing)
            && Math.Abs(existing - v) < 0.0005) return;
        _settings.Set(key, v);
        Raise(args);
    }

    private void SetString(string key, string value, AppearanceChangedArgs args)
    {
        // 键未设置视为首次写入（避免空串默认值与未设键误判相等而跳过）。
        var stored = _settings.Get(key, (string?)null);
        if (stored is not null && stored == value) return;
        _settings.Set(key, value);
        Raise(args);
    }

    private void Raise(AppearanceChangedArgs args)
    {
        // 逐个通知并隔离异常：一个窗口的刷新处理器抛异常不得中断整条事件链
        // （否则后续窗口收不到外观变更，表现为"模式改了只有部分窗口生效"）。
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (EventHandler<AppearanceChangedArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // 单个订阅者失败不阻断其余窗口刷新
                }
            }
        }
    }

    private static Brush MakeBrush(Color c, double alpha)
        => new SolidColorBrush(Color.FromArgb((byte)Math.Round(Clamp01(alpha) * 255), c.R, c.G, c.B));

    private static Color Lighten(Color c, double amt)
        => Color.FromRgb(
            (byte)Math.Min(255, c.R + 255 * amt),
            (byte)Math.Min(255, c.G + 255 * amt),
            (byte)Math.Min(255, c.B + 255 * amt));

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static string FormatColor(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try
        {
            if (s.StartsWith("#"))
            {
                var hex = s[1..];
                if (hex.Length == 6)
                    return Color.FromRgb(
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16));
                if (hex.Length == 8)
                    return Color.FromArgb(
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16),
                        Convert.ToByte(hex[6..8], 16));
            }
        }
        catch
        {
            // 解析失败回退
        }
        return fallback;
    }

    private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private void SetInt(string key, int value, AppearanceChangedArgs args)
    {
        if (_settings.Get(key, 0) == value) return;
        _settings.Set(key, value);
        Raise(args);
    }
}
