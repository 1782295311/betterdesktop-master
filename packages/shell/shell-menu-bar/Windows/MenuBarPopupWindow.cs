// BetterDesktop.Shell.MenuBar — 菜单栏弹出面板基类（薄壳）
// 2026-09-07 弹窗体系上提（P0-2）：通用弹窗行为已上提 shell-core/Windows/PopupWindowBase；
// 本类仅保留菜单栏特有语义：
//   - 屏幕顶部菜单栏条带判定（点击条带 = 操作菜单栏，不触发"点击窗口外收起"）
//   - 条带高度引用 MenuBarMetrics 单一真相源（此前硬编码 20 与 MenuBarHeight 16 不同源，存在漂移隐患）
// 所有独立面板（IME菜单/日历/控制中心/WLAN/音量/…）继续继承本类，无需改动。

using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 菜单栏独立弹出面板的基类。子类实现 BuildContent() 负责真实 UI + Binding。
/// 面板外观由 PopupWindowBase 统一挂载（对齐 ShellWindow 窗口属性，走主题令牌），子类内容根容器须透明。
/// </summary>
internal abstract class MenuBarPopupWindow : PopupWindowBase
{
    /// <summary>屏幕顶部菜单栏条带高度：菜单栏固定在主屏顶部，
    /// 点击该条带视为"操作菜单栏"（打开/切换面板），不触发"点击窗口外收起"。</summary>
    private static readonly double MenuBarStripHeight = Contracts.MenuBarMetrics.MenuBarHeight;

    protected MenuBarPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
    }

    /// <summary>点击点是否落在屏幕顶部菜单栏条带内（菜单栏固定在主屏顶部，全宽）。</summary>
    protected override bool IsPointInMenuBarStrip(NativeMethods.POINT pt)
    {
        return pt.Y >= 0 && pt.Y < MenuBarStripHeight;
    }
}
