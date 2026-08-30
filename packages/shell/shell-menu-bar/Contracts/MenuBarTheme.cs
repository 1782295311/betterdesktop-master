// BetterDesktop.Shell.MenuBar — 菜单栏前景色单一真相源
//
// 【为什么存在这个文件】
// 右区状态条是**纯代码自绘**（Rectangle/Path/Ellipse/TextBlock 直接赋 Fill/Stroke/Foreground），
// 历史上 55 处直接写 Brushes.White。这带来一个功能级缺陷：主题切到亮色 / 浅色皮肤时，
// 菜单栏仍是白字白图标 → 整条菜单栏不可读。
//
// 【为什么不用 DynamicResource / SetResourceReference】
// AppearanceService 确实会往 Application.Current.Resources 推 "ThemeForeground"，
// 但把它接进自绘控件意味着把 30+ 个对象初始化器拆成「new → 赋值 → SetResourceReference」三段，
// 改动面大、易漏。这里改用 WPF 的一条特性：
//   SolidColorBrush **未冻结**时，修改它的 Color 会自动通知所有引用它的元素重绘。
// 于是只要对外暴露**同一个共享实例**，改一次 Color 就等价于"全菜单栏换色"，
// 与 DynamicResource 效果一致，但对调用方零结构改动（Brushes.White → MenuBarTheme.Foreground）。
//
// 【约束】
// - 画刷刻意不 Freeze（冻结后改 Color 会抛 InvalidOperationException）。
// - 改 Color 是 DispatcherObject 操作，必须 UI 线程；SyncFrom 内部已做 Dispatcher 兜底，
//   因为 PluginHandle.LoadAsync 用 ConfigureAwait(false)，调用方可能不在 UI 线程。

using System.Windows;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>
/// 菜单栏前景令牌：所有自绘图标/文字都应引用 <see cref="Foreground"/> 而非 Brushes.White，
/// 主题变化时由 <see cref="SyncFrom"/> 统一改色，全菜单栏同步刷新。
/// </summary>
internal static class MenuBarTheme
{
    /// <summary>默认前景（暗色毛玻璃上白字）——AppearanceService 缺失时的兜底，与历史行为一致。</summary>
    private static readonly SolidColorBrush ForegroundBrush = new(Colors.White);

    /// <summary>按钮悬停底色（20% 白）。冻结后共享，避免每次 MouseEnter 都 new 一个画刷。</summary>
    public static readonly Brush Hover = CreateFrozen(51);

    /// <summary>按钮按下底色（33% 白）。同上，冻结共享。</summary>
    public static readonly Brush Pressed = CreateFrozen(85);

    private static Brush CreateFrozen(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, byte.MaxValue, byte.MaxValue, byte.MaxValue));
        brush.Freeze(); // 只读共享画刷：冻结后 WPF 可直接在渲染线程复用，省一次克隆
        return brush;
    }

    /// <summary>
    /// 给菜单栏按钮挂上统一的悬停/按下反馈（左区与右区共用同一套手感）。
    /// 反馈色固定为半透明白（叠加在毛玻璃上，深浅主题下都成立），因此不随前景色变化。
    /// </summary>
    public static void AttachHoverFeedback(System.Windows.Controls.Border button)
    {
        button.Background = System.Windows.Media.Brushes.Transparent;
        button.MouseEnter += (_, _) => button.Background = Hover;
        button.MouseLeave += (_, _) => button.Background = System.Windows.Media.Brushes.Transparent;
        button.MouseLeftButtonDown += (_, _) => button.Background = Pressed;
    }

    /// <summary>
    /// 菜单栏目共前景画刷（共享可变实例）。
    /// 引用它而不是 Brushes.White；改色由 SyncFrom 统一驱动，调用方不要自行 Freeze 或改它的 Color。
    /// </summary>
    public static Brush Foreground => ForegroundBrush;

    /// <summary>
    /// 从外观服务同步前景色。内部做 UI 线程兜底，可安全地在插件 LoadAsync（非 UI 线程）中调用。
    /// appearance 为 null 时保持默认白（降级，不抛）。
    /// </summary>
    public static void SyncFrom(IAppearanceService? appearance)
    {
        if (appearance is null)
        {
            return;
        }

        var color = appearance.ForegroundColor;
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            ApplyCore(color);
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            ApplyCore(color);
        }
        else
        {
            // 非 UI 线程：排队到 UI 线程执行（SolidColorBrush 是 DispatcherObject，跨线程改会抛）
            app.Dispatcher.BeginInvoke(() => ApplyCore(color));
        }
    }

    private static void ApplyCore(Color color)
    {
        // 同色短路：避免主题 Changed 频繁广播时产生无谓的重绘通知
        if (ForegroundBrush.Color == color)
        {
            return;
        }

        ForegroundBrush.Color = color;
    }
}
