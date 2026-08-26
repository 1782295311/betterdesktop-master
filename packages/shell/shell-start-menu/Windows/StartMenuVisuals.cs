using System.Windows;
using System.Windows.Media;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.StartMenu.Windows;

/// <summary>
/// 自绘开始菜单的共享视觉助手。
/// 落实"风格底色 + 主题强调"：三套样式（win7 / win10 / win11）各自复刻原生结构、材质、间距与字号，
/// 但面板底色、强调色、文字色一律由主题令牌 <see cref="IThemeTokens"/> 换算而来，保证可整体换肤。
/// 布局不再硬编码颜色；本助手是唯一的取色入口。
/// </summary>
internal sealed class StartMenuPalette
{
    private readonly SolidColorBrush _accentSolid;

    private StartMenuPalette(IThemeTokens tokens)
    {
        // 主题面板底色（菜单内容区背景，随主题统一材质）。
        Surface = First(tokens.PanelBackground, tokens.ContentBackground, tokens.WindowBackground);
        SurfaceAlt = tokens.WindowBackground ?? tokens.PanelBackground ?? Surface;
        Foreground = tokens.Foreground;
        Muted = tokens.MutedForeground;
        Separator = tokens.Separator;
        CornerRadius = tokens.CornerRadius > 0 ? tokens.CornerRadius : 8;

        // 主题强调色：转成 Color 供后续按需叠加透明度生成磁贴/悬浮/选中底。
        _accentSolid = (tokens.Accent as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x33, 0x77, 0xEF));
        AccentColor = _accentSolid.Color;
        Accent = _accentSolid;
    }

    /// <summary>按主题令牌构造调色板（容错：任何令牌缺失都回退中性值）。</summary>
    public static StartMenuPalette From(IThemeTokens? tokens)
        => new(tokens ?? new FallbackTokens());

    // ---- 基础表面 ----

    /// <summary>菜单内容区底色。</summary>
    public Brush Surface { get; }

    /// <summary>次级表面（如 Win11 底部栏 / Win10 底部导航区背景）。</summary>
    public Brush SurfaceAlt { get; }

    /// <summary>主前景（文字）。</summary>
    public Brush Foreground { get; }

    /// <summary>次要前景（副标题 / 提示）。</summary>
    public Brush Muted { get; }

    /// <summary>分隔线。</summary>
    public Brush Separator { get; }

    /// <summary>面板圆角。</summary>
    public double CornerRadius { get; }

    // ---- 强调 ----

    /// <summary>强调色（Color，供叠加透明度）。</summary>
    public Color AccentColor { get; }

    /// <summary>强调画刷。</summary>
    public Brush Accent { get; }

    /// <summary>强调上的文字色（恒亮，与亮色文字主题一致）。</summary>
    public Brush OnAccent => Brushes.White;

    // ---- 由强调/中性叠加出的交互底 ----

    /// <summary>行悬浮底色（白低透明度，叠加在 Surface 上）。</summary>
    public Brush RowHover => WithAlpha(Colors.White, 0x16);

    /// <summary>选中行底色（占位：主题强调低透明）。</summary>
    public Brush RowSelected => WithAlpha(AccentColor, 0x4A);

    /// <summary>Win11 磁贴标底：强调超低透明圆角块，图标居中，下方名称。</summary>
    public Brush Tile11 => WithAlpha(AccentColor, 0x30);

    /// <summary>Win10 扁平磁贴底：强调中透明实心方块。</summary>
    public Brush Tile10 => WithAlpha(AccentColor, 0x6E);

    /// <summary>图标文件夹磁贴底：强调低透明（与 Tile10 区分）。</summary>
    public Brush TileFolder => WithAlpha(AccentColor, 0x46);

    /// <summary>Win7 右栏用户区底：比 Surface 略深一层（强调低透明）。</summary>
    public Brush UserArea => WithAlpha(AccentColor, 0x1A);

    // ---- 工具 ----

    /// <summary>取 32 位 ARGB 编码的 Color 画刷（避免动态资源解引用问题）。</summary>
    private static SolidColorBrush WithAlpha(Color color, byte alpha)
    {
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>首个非空画刷（令牌容错）。</summary>
    private static Brush First(params Brush?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (c is not null)
            {
                return c;
            }
        }

        var fallback = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        fallback.Freeze();
        return fallback;
    }

    /// <summary>主题令牌缺失时的兜底（保证布局仍可构建渲染）。</summary>
    private sealed class FallbackTokens : IThemeTokens
    {
        private static readonly Brush Bg;
        private static readonly Brush Fg;
        private static readonly Brush Muted;
        private static readonly Brush Sep;
        private static readonly Brush Acc;

        static FallbackTokens()
        {
            Bg = FreezeBrush(Color.FromRgb(0x24, 0x26, 0x2B));
            Fg = FreezeBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            Muted = FreezeBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
            Sep = FreezeBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
            Acc = FreezeBrush(Color.FromRgb(0x33, 0x77, 0xEF));
        }

        private static SolidColorBrush FreezeBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public Brush WindowBackground => Bg;
        public Brush PanelBackground => Bg;
        public Brush ContentBackground => Bg;
        public Brush Foreground => Fg;
        public Brush MutedForeground => Muted;
        public Brush Separator => Sep;
        public Brush Accent => Acc;
        public Brush InputBackground => Bg;
        public Brush InputBorder => Sep;
        public Brush CardBorder => Sep;
        public int CardBorderStyle => 0;
        public System.Windows.Media.Effects.Effect? CardShadow => null;
        public double CornerRadius => 8;
        public double FontSizeInput => 14;
        public double FontSizeBody => 13;
        public double FontSizeCaption => 12;
        public double FontSizeSmall => 11;
    }
}