namespace BetterDesktop.Shell.Taskbar.Contracts;

/// <summary>
/// 任务栏强调（ACCENT）风格。枚举值与 Windows ACCENT_STATE 对齐，
/// 直接映射 TranslucentTB 的 accent 选项（normal/opaque/clear/blur/acrylic）。
/// </summary>
public enum TaskbarAccent
{
    /// <summary>还原系统默认外观（ACCENT_NORMAL）。</summary>
    Normal = 0,

    /// <summary>不透明纯色（ACCENT_ENABLE_GRADIENT，alpha=0xFF）。</summary>
    Opaque = 1,

    /// <summary>完全透明（ACCENT_ENABLE_GRADIENT，alpha=0）。</summary>
    Clear = 2,

    /// <summary>模糊透出背景（ACCENT_ENABLE_BLURBEHIND）。</summary>
    Blur = 3,

    /// <summary>亚克力材质（ACCENT_ENABLE_ACRYLICBLURBEHIND）。</summary>
    Acrylic = 4,
}

/// <summary>
/// 单一场景的任务栏外观配置（对应 TTB 的 TaskbarAppearance）。
/// </summary>
public sealed class TaskbarAppearance
{
    /// <summary>强调风格。</summary>
    public TaskbarAccent Accent { get; set; } = TaskbarAccent.Blur;

    /// <summary>颜色（0xAARRGGBB；alpha 控制透明度，Win10/Win11 均生效）。
    /// 默认中性灰半透明（0xCC808080），避免纯色模式因 alpha=0 全透明。</summary>
    public uint Color { get; set; } = 0xCC808080;

    /// <summary>亚克力/模糊半径（Win11 TaskbarService 用，除以 3 传入；0 表示系统默认）。</summary>
    public float BlurRadius { get; set; } = 0f;

    public TaskbarAppearance Clone() => new()
    {
        Accent = Accent,
        Color = Color,
        BlurRadius = BlurRadius,
    };
}

/// <summary>
/// 整套任务栏外观配置：每个上下文场景一套外观，与 TTB settings.schema.json 对齐。
/// </summary>
public sealed class TaskbarAppearanceConfig
{
    // 场景默认值规范（2026-09-02 用户定稿）：全部场景统一「不透明纯色（Opaque）+ 白色 + 不透明度 0」。
    // 注意：TaskbarAppearance 类字段默认（Blur/0xCC808080）是历史遗留，禁止作为场景初值直接 new；
    // 新增场景必须显式套用下面的规范化初始值。

    /// <summary>桌面空闲（无最大化/无浮层）时的外观。</summary>
    public TaskbarAppearance Desktop { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>有可见窗口（无最大化）时的外观。</summary>
    public TaskbarAppearance VisibleWindow { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>有最大化窗口时的外观。</summary>
    public TaskbarAppearance MaximizedWindow { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>开始菜单打开时的外观（enabled=false 表示不参与，沿用上层）。</summary>
    public bool StartOpenedEnabled { get; set; } = true;
    public TaskbarAppearance StartOpened { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>搜索打开时的外观。</summary>
    public bool SearchOpenedEnabled { get; set; } = true;
    public TaskbarAppearance SearchOpened { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>任务视图打开时的外观。</summary>
    public bool TaskViewOpenedEnabled { get; set; } = false;
    public TaskbarAppearance TaskViewOpened { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>省电模式时的外观。</summary>
    public bool BatterySaverEnabled { get; set; } = false;
    public TaskbarAppearance BatterySaver { get; set; } = new() { Accent = TaskbarAccent.Opaque, Color = 0x00FFFFFF };

    /// <summary>深拷贝，供引擎内部持有、避免外部修改。</summary>
    public TaskbarAppearanceConfig Clone()
    {
        return new TaskbarAppearanceConfig
        {
            Desktop = Desktop.Clone(),
            VisibleWindow = VisibleWindow.Clone(),
            MaximizedWindow = MaximizedWindow.Clone(),
            StartOpenedEnabled = StartOpenedEnabled,
            StartOpened = StartOpened.Clone(),
            SearchOpenedEnabled = SearchOpenedEnabled,
            SearchOpened = SearchOpened.Clone(),
            TaskViewOpenedEnabled = TaskViewOpenedEnabled,
            TaskViewOpened = TaskViewOpened.Clone(),
            BatterySaverEnabled = BatterySaverEnabled,
            BatterySaver = BatterySaver.Clone(),
        };
    }
}
