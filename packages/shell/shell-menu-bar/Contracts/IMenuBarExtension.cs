// BetterDesktop.Shell.MenuBar — 菜单栏右区按钮扩展点契约
// 设计原则：每个右区按钮 = 一个独立的 IMenuBarExtension：
//   - GetVisual() 返回按钮的 WPF Visual（用于显示在 MenuBar 右区）
//   - OpenPopup(anchor) 打开独立的 ShellWindow（弹窗，锚定在按钮下方）
//   - 所有按钮的数据全部走 Inject 的服务 + Binding，禁止硬编码假数据。
// 这样 IME / 日历 / WLAN / 音量 / 蓝牙 / 控制中心 都是独立实现，互不耦合。

using System.Windows;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>
/// 菜单栏右区按钮扩展点：每个按钮 = 一个独立插件实现。
/// 每个扩展独自持有自己的独立弹窗 ShellWindow（点击按钮 → 打开弹窗），弹窗也可被其他入口直接调起。
/// </summary>
public interface IMenuBarExtension
{
    /// <summary>扩展标识（如 "ime" / "calendar" / "control-center" / "wlan" / "volume" / "bluetooth" / "battery" / "display"）。</summary>
    string Id { get; }

    /// <summary>右区按钮的可视元素（由 MenuBarWindow 放在右区尾部）。</summary>
    FrameworkElement GetVisual();

    /// <summary>
    /// 打开该按钮对应的独立弹窗。
    /// anchor：菜单栏窗口中按钮的左上角屏幕坐标（弹窗以此为参照，通常铺在按钮正下方）。
    /// 返回的窗口允许被重复调用：若已显示则只做置前；调用方不持有引用。
    /// </summary>
    void OpenPopup(Point anchorScreenTopLeft);

    /// <summary>显式关闭弹窗（若打开中），例如点击菜单别处时统一收起。</summary>
    void ClosePopup();
}
