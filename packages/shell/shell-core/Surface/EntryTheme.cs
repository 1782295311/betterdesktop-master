using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 「独立入口 exe」的主题引导（**唯一实现**）：不引宿主 shell-settings/kernel 的进程
///（剪贴板面板 <c>BetterDesktop.Clipboard.Panel</c>、截图入口面 <c>BetterDesktop.Capture</c> …）
/// 直接读同一份 <c>%APPDATA%\BetterDesktop\settings.json</c>，把宿主的 <c>appearance.*</c> 扁平键
///（SettingsService 格式）推导成 <c>Application.Resources</c> 主题令牌，
/// 复刻 <c>shell-settings/Services/AppearanceService.SyncAppResources</c> 的令牌全集与推导口径，
/// 保证这些进程里手写的窗口与宿主窗口**视觉同源**。
/// <para>
/// 【为什么集中在这里 · 2026-09-15】此前这套推导只存在于剪贴板面板的 <c>PanelTheme</c> 里；
/// 截图入口面要做同样的视觉时，若各写一遍必然漂移（同一份令牌出现两套色值）——
/// 与仓库既有纪律（"热键解析唯一来源，禁止另写"）同类。故上提到 shell-core，
/// 面板与截图共用同一份实现与同一套设计刻度。
/// </para>
/// <para>
/// 各 exe 约定：启动时 <see cref="Load"/> + <see cref="ApplyToAppResources"/> 各调一次；
/// 运行时不监听设置变更（v1 语义：宿主改主题后，入口 exe 重启跟随 —— 与扩展中心开关同款）。
/// </para>
/// </summary>
public static class EntryTheme
{
    private const string DefaultAccent = "#0A84FF";
    private const string DefaultTint = "#1F1F22";

    private static readonly Dictionary<string, JsonElement> Flat = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置文件路径（可注入，测试用）。</summary>
    public static string SettingsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDesktop", "settings.json");

    /// <summary>解析结果是否来自真实文件（false = 文件缺失，全默认）。</summary>
    public static bool Loaded { get; private set; }

    /// <summary>
    /// 诊断输出（可选）。本类在 shell-core 里，不认识各 exe 的日志实现（PanelLog / CaptureLog），
    /// 由调用方在启动时注入自己的写日志方法 —— 避免"主题读失败"变成静默降级。
    /// </summary>
    public static Action<string>? OnDiagnostic { get; set; }

    public static void Load()
    {
        Flat.Clear();
        Loaded = false;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    Flat[p.Name] = p.Value.Clone();
                }
            }
            Loaded = true;
        }
        catch (Exception e)
        {
            OnDiagnostic?.Invoke($"settings.json 解析失败，使用默认主题: {e.Message}");
        }
    }

    // ---- 设置读取（对外公开：各 exe 还要读自己的扩展键，共用同一份扁平表） ----

    public static JsonElement? Raw(string key)
        => Flat.TryGetValue(key, out var v) ? v : (JsonElement?)null;

    public static string Str(string key, string dflt)
    {
        var r = Raw(key);
        return r is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? dflt : dflt;
    }

    public static int Int(string key, int dflt)
    {
        var r = Raw(key);
        return r is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : dflt;
    }

    public static double Dbl(string key, double dflt)
    {
        var r = Raw(key);
        return r is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var v) ? v : dflt;
    }

    public static bool Bool(string key, bool dflt)
    {
        var r = Raw(key);
        return r is { ValueKind: JsonValueKind.True } || r is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) && v != 0
            ? true
            : r is { ValueKind: JsonValueKind.False } ? false : dflt;
    }

    public static Color ParseColor(string? s, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s) && s.StartsWith('#'))
            {
                var hex = s[1..];
                return hex.Length switch
                {
                    // 【坑】必须写 System.Convert：本文件命名空间在 BetterDesktop.Shell 之下，
                    // 而 BetterDesktop.Shell.Convert（转换包）这个**命名空间**会遮蔽全局 Convert → CS0234。
                    6 => Color.FromRgb(
                        System.Convert.ToByte(hex[..2], 16),
                        System.Convert.ToByte(hex[2..4], 16),
                        System.Convert.ToByte(hex[4..6], 16)),
                    8 => Color.FromArgb(
                        System.Convert.ToByte(hex[..2], 16),
                        System.Convert.ToByte(hex[2..4], 16),
                        System.Convert.ToByte(hex[4..6], 16),
                        System.Convert.ToByte(hex[6..8], 16)),
                    _ => fallback
                };
            }
        }
        catch
        {
            // 回退
        }
        return fallback;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // ---- 读取键（与 AppearanceService 默认值逐一对齐） ----
    public static int ThemeMode => Math.Clamp(Int("appearance.themeMode", 0), 0, 2);
    public static Color Accent => ParseColor(Str("appearance.accent", DefaultAccent), Color.FromRgb(0x0A, 0x84, 0xFF));
    public static Color WindowTint => ParseColor(Str("appearance.windowTint", DefaultTint), Color.FromRgb(0x1F, 0x1F, 0x22));
    public static double WindowOpacity => Clamp01(Dbl("appearance.windowOpacity", 0.2));
    public static double ContentOpacity => Clamp01(Dbl("appearance.contentOpacity", 0.502));
    public static double CornerRadius => Dbl("appearance.cornerRadius", 8.0);
    public static int BorderStyle => Math.Clamp(Int("appearance.borderStyle", 2), 0, 3);
    public static double BorderStrength => Clamp01(Dbl("appearance.borderStrength", 0.5));
    public static double BorderThickness => Dbl("appearance.borderThickness", 1.5);
    public static string SkinActive => Str("appearance.skin.active", "");
    public static string Material => Str("appearance.material", "Transparent");

    /// <summary>
    /// 入口 exe 的界面设计刻度（**唯一来源**，面板与截图共用）。
    /// <para>
    /// 【为什么必须集中 · 2026-09-12 UI 收敛】多轮追加功能时，字号/间距/圆角/强调色透明度各自就地取值，
    /// 结果是：FontSize 10/11/12/13/15 混用、水平内缩 14 与 10 并存、圆角出现 4/6/7/8/10/12 六种、
    /// 同一语义的强调色透明度散成 5 档（0.16/0.18/0.28/0.85/0.9）。
    /// 这些值本身都不算错，错在**没有一个共同来源** —— 于是"看起来不像一个产品"。
    /// 今后 UI 代码里再出现野生数值，一律视为回归。
    /// </para>
    /// </summary>
    public static class Scale
    {
        // 字号四档：caption（徽标）/ small（次要信息）/ body（正文）/ title（标题）
        // 【2026-09-15 视觉收敛】原 10/11/12/16 里 10-11px 低于可读下限（技能规则：正文不得 <12px，
        // 字号须成模数级差）。现为 11/12/13/17，相邻档差 ≥1px 且保持"徽标 < 次要 < 正文 < 标题"的层级。
        public const double FontCaption = 11;
        public const double FontSmall = 12;
        public const double FontBody = 13;
        public const double FontTitle = 17;

        // 间距四档（4 的倍数）
        public const double SpaceXS = 4;
        public const double SpaceS = 8;
        public const double SpaceM = 12;
        public const double SpaceL = 16;

        // 圆角三档：S 徽标 / M 卡片与状态卡 / L 行与浮层
        public const double RadiusS = 4;
        public const double RadiusM = 10;
        public const double RadiusL = 14;

        /// <summary>胶囊圆角（chip / 按钮）：= <see cref="TapTarget"/> 的一半，两端口为半圆。</summary>
        public const double RadiusPill = 16;

        /// <summary>
        /// 小徽标（分类 / 敏感 / 标签 / 计数）的水平内边距。
        /// 【为什么单独一档】徽标字号是 FontCaption（11px），用 SpaceS(8) 会显得臃肿、
        /// 用 SpaceXS(4) 又贴字；此前代码里就地写 4/5 混用 —— 收敛为唯一值。
        /// </summary>
        public const double BadgePadX = 6;

        // 强调色透明度三档（语义化；禁止在 UI 代码里再写裸小数）
        public const double AccentSubtle = 0.16;   // 极轻底：分类徽标
        public const double AccentMedium = 0.28;   // 选中/激活底：chip、序号徽标
        public const double AccentStrong = 1.0;    // 强调字/图标

        /// <summary>标准点击目标（技能规则：可点击元素 ≥32px，触控友好）。</summary>
        public const double TapTarget = 32;

        /// <summary>面板水平内缩：header / 搜索 / 筛选 / 状态槽 / 列表 / 底部栏**一律同值**。</summary>
        public const double GutterX = 16;

        // ---- 动效刻度（技能规则：hover/状态切换须有 150-300ms 过渡，禁止 0ms 瞬变）----
        /// <summary>微交互（hover / 按压）过渡时长。</summary>
        public const int MotionFastMs = 150;

        /// <summary>区块级过渡（面板内浮层出现等）时长。</summary>
        public const int MotionBaseMs = 250;
    }

    // ---- 令牌推导（对齐 SyncAppResources 口径） ----
    public static void ApplyToAppResources()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }
        var mode = ThemeMode;
        var isLight = mode == 1;

        var fg = isLight ? "#FF1A1A1C" : "#FFECECEC";
        var bg = isLight ? "#FFF3F3F5" : "#FF2C2C2E";
        var bgHover = isLight ? "#FFE3E3E6" : "#FF48484A";
        var bgPressed = isLight ? "#FFD8D8DC" : "#FF343436";
        var border = isLight ? "#FFC8C8CC" : "#FF48484A";
        var track = isLight ? "#FFD2D2D6" : "#FF48484A";
        var popupBg = isLight ? "#FFF6F6F8" : "#FF222226";
        var popupHover = isLight ? "#FFE3E3E6" : "#FF333338";

        SetBrush(app, "ControlForeground", fg);
        SetBrush(app, "ControlBackground", bg);
        SetBrush(app, "ControlBackgroundHover", bgHover);
        SetBrush(app, "ControlBackgroundPressed", bgPressed);
        SetBrush(app, "ControlBorder", border);
        SetBrush(app, "ControlTrack", track);
        SetBrush(app, "PopupBackground", popupBg);
        SetBrush(app, "PopupBorder", border);
        SetBrush(app, "PopupItemHover", popupHover);

        var winBg = isLight ? "#E8E8EA" : mode == 2 ? "#000000" : "#01000000";
        var panelBg = isLight ? "#F2F2F4" : mode == 2 ? "#1A1A1C" : "#1D1D20";
        var contentBg = isLight ? "#F6F6F8" : mode == 2 ? "#222224" : "#222226";
        var fore = isLight ? "#1A1A1C" : "#F2F2F2";
        // 【2026-09-15 视觉收敛】次要前景从 #E2E2E6 降到 #A0A0A6：
        // 原值几乎与正文前景（#F2F2F2）同亮 → 元信息（大小/来源/次数/时间）与预览正文
        // 之间没有层级，"满屏都是白字"。新值在 #222226 底上对比度 ≈ 6:1（仍远超 4.5:1 门槛），
        // 且与正文拉开一档 —— 这正是 Apple 二级标签（systemGray）的做法。
        var muted = isLight ? "#6E6E73" : "#A0A0A6";
        var sepColor = isLight ? "#000000" : "#B0B0B0";
        var sepOpacity = isLight ? 0.12 : 0.25;

        ContentBackgroundColor = (Color)ColorConverter.ConvertFromString(contentBg)!;

        SetBrush(app, "ThemeWindowBackground", winBg);
        SetBrush(app, "ThemePanelBackground", panelBg, WindowOpacity);
        SetBrush(app, "ThemeContentBackground", contentBg);
        SetBrush(app, "ThemeForeground", fore);
        SetBrush(app, "ThemeMutedForeground", muted);
        SetBrush(app, "ThemeSeparator", sepColor, sepOpacity);

        SetAppResource(app, "CardBorderBrush", BuildCardBorder());
        SetAppResource(app, "CardShadowEffect", null);
        SetAppResource(app, "AccentBrush", new SolidColorBrush(Accent));
        SetAppResource(app, "AccentColor", Accent);
        SetAppResource(app, "WindowTintColor", WindowTint);
        SetAppResource(app, "WindowOpacityValue", WindowOpacity);
        SetAppResource(app, "BackgroundBrush", BuildBackgroundBrush());
        SetAppResource(app, "SkinBackgroundBrush", BuildBackgroundBrush());
        SetAppResource(app, "SkinIsActive", !string.IsNullOrWhiteSpace(SkinActive));
        SetAppResource(app, "SkinDarkenOpacity", Dbl("appearance.skin.imgDarken", 0.25));
        SetAppResource(app, "SkinAccentFromSkin", new SolidColorBrush(Accent));

        // ---- 语义状态色 + 描边 ----
        // 【为什么必须显式定义】入口 exe 只复刻了宿主 AppearanceService 推送的令牌子集；
        // 而界面代码引用了 StatusSuccess / StatusWarning / StatusDanger / BorderStroke* ——
        // 这些键若未定义，`SetResourceReference` 解析到 null 是**静默**的。实测后果：
        //   · 操作反馈文字（toast）前景解析为 null → 用默认黑字 → 深色底上等于看不见；
        //   · 卡片与操作条描边全部丢失，看起来"没画完"。
        // 取值与 host/App.xaml 的 Status*/BorderStroke* 逐一对齐（描边在浅色模式下改黑，否则白描边不可见）。
        var strokeBase = isLight ? Colors.Black : Colors.White;
        SetAppResource(app, "StatusSuccess", new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)));
        SetAppResource(app, "StatusWarning", new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
        SetAppResource(app, "StatusDanger", new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57)));
        SetAppResource(app, "BorderStroke", new SolidColorBrush(WithAlpha(strokeBase, 0.14)));
        SetAppResource(app, "BorderStrokeSubtle", new SolidColorBrush(WithAlpha(strokeBase, 0.08)));
        SetAppResource(app, "BorderStrokeAccent", new SolidColorBrush(WithAlpha(Accent, 0.9)));

        // 细滚动条拇指（SlimScrollBar 用）：取次要前景色的两档透明 —— 比描边明显、比正文轻。
        // 不定义这两个键的话，细滚动条模板里的 DynamicResource 会解析成 null（拇指不可见）。
        // 透明度取"淡"档：滚动条是**辅助**元素，抢眼就等于脏（0.55 在深色面板上已相当亮，
        // 用户实测"不如系统默认好看"）。hover 才明显加深。
        var mutedColor = (Color)ColorConverter.ConvertFromString(muted)!;
        SetAppResource(app, "ScrollThumb", new SolidColorBrush(WithAlpha(mutedColor, 0.35)));
        SetAppResource(app, "ScrollThumbHover", new SolidColorBrush(WithAlpha(mutedColor, 0.7)));

        // 全局主题字典（PopupWindowBase 的动态资源解析也可经 ThemeResourceProvider 兜底）
        var global = ThemeResourceProvider.Ensure();
        foreach (var key in new[]
                 {
                     "ControlForeground", "ControlBackground", "ControlBackgroundHover", "ControlBackgroundPressed",
                     "ControlBorder", "ControlTrack", "PopupBackground", "PopupBorder", "PopupItemHover",
                     "ThemeWindowBackground", "ThemePanelBackground", "ThemeContentBackground",
                     "ThemeForeground", "ThemeMutedForeground", "ThemeSeparator",
                     "CardBorderBrush", "CardShadowEffect", "BackgroundBrush",
                     "SkinBackgroundBrush", "SkinIsActive", "SkinDarkenOpacity", "SkinAccentFromSkin",
                     "AccentBrush", "AccentColor", "WindowTintColor", "WindowOpacityValue",
                     "StatusSuccess", "StatusWarning", "StatusDanger",
                     "BorderStroke", "BorderStrokeSubtle", "BorderStrokeAccent",
                     "ScrollThumb", "ScrollThumbHover"
                 })
        {
            if (app.Resources[key] is { } v)
            {
                global[key] = v;
            }
        }
    }

    /// <summary>
    /// 叠加色：把 <paramref name="overlay"/> 按 <paramref name="alpha"/> 混到 <paramref name="under"/> 上，
    /// 返回**不透明**结果色。
    /// <para>
    /// 【为什么需要它 · 2026-09-15】行/按钮的 hover 与选中不是"整块换画刷"而是**颜色过渡动画**，
    /// 而 WPF 只能给不透明的 <see cref="SolidColorBrush.Color"/> 做插值；两段都是半透明色时会插值出
    /// 中途变淡的假象。先混成不透明色再动画，观感才是"底色淡入"。
    /// </para>
    /// </summary>
    public static Color Blend(Color under, Color overlay, double alpha)
    {
        var a = Clamp01(alpha);
        byte Mix(byte u, byte o) => (byte)Math.Round(u * (1 - a) + o * a);
        return Color.FromRgb(Mix(under.R, overlay.R), Mix(under.G, overlay.G), Mix(under.B, overlay.B));
    }

    /// <summary>强调色（令牌 AccentBrush 的色值；动画插值用）。</summary>
    public static Color AccentColor => Accent;

    /// <summary>
    /// 卡片/内容区静止底色（<c>ThemeContentBackground</c> 的色值；动画插值用）。
    /// 由 <see cref="ApplyToAppResources"/> 在解析令牌时**同源**记下 —— 不二次硬编码色值，
    /// 避免"令牌改了、动画起点没改"的漂移。
    /// </summary>
    public static Color ContentBackgroundColor { get; private set; } = Color.FromRgb(0x22, 0x22, 0x26);

    private static Brush BuildCardBorder()
    {
        var s = BorderStrength;
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        return BorderStyle switch
        {
            1 => new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(WithAlpha(white, Math.Min(1, s * 1.6)), 0),
                    new GradientStop(WithAlpha(white, s * 0.35), 1)
                }
            },
            2 => new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 0.35),
                    new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.70),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
                }
            },
            _ => new SolidColorBrush(white) { Opacity = Math.Max(s, 0.31) }
        };
    }

    private static Brush BuildBackgroundBrush()
    {
        var skin = SkinActive;
        if (skin.StartsWith("img:", StringComparison.Ordinal) && File.Exists(skin[4..]))
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(skin[4..], UriKind.Absolute);
                img.DecodePixelWidth = 2560;
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                var brush = new ImageBrush(img)
                {
                    Stretch = Stretch.Uniform,
                    Opacity = Clamp01(Dbl("appearance.skin.imgOpacity", 1.0))
                };
                brush.Freeze();
                return brush;
            }
            catch
            {
                // 皮肤加载失败回退色调
            }
        }
        var tint = MakeBrush(WindowTint, WindowOpacity);
        tint.Freeze();
        return tint;
    }

    private static SolidColorBrush MakeBrush(Color c, double alpha)
        => new(Color.FromArgb((byte)Math.Round(Clamp01(alpha) * 255), c.R, c.G, c.B));

    private static Color WithAlpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Round(Clamp01(alpha) * 255), c.R, c.G, c.B);

    private static void SetBrush(Application app, string key, string color, double? opacity = null)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        if (opacity is { } o)
        {
            brush.Opacity = o;
        }
        SetAppResource(app, key, brush);
    }

    private static void SetAppResource(Application app, string key, object? value)
    {
        // 只读合并字典坑：先 Remove 本地同名项再 Add（对齐 SettingsService.SetAppResource 纪律）
        app.Resources.Remove(key);
        app.Resources.Add(key, value);
    }
}
