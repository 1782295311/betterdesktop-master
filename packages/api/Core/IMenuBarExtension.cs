// BetterDesktop.Shell.Core — 菜单栏扩展点契约（2026-09-07 公共化上提，P0-3）
// 原位于 shell-menu-bar/Contracts/IMenuBarExtension.cs（已 public），上提后供跨包实现/消费。
// 契约语义不变：
//   - GetVisual() 返回右区按钮的 WPF Visual；**允许返回 null** = 非按钮扩展
//     （如左区弹窗提供者 LogoMenu/StacksPopup），MenuBarWindow 装配时跳过 null。
//   - OpenPopup(anchor) 打开独立弹窗（ShellWindow），anchor 为按钮左上角屏幕坐标（逻辑单位）。
//   - 扩展独自持有自己的弹窗窗口；ClosePopup 用于统一收起（如显示器变化时）。

using System.Windows;

namespace BetterDesktop.Shell.Core.Contracts;

/// <summary>
/// 菜单栏扩展点：每个可挂载部件 = 一个独立插件实现。
/// 右区按钮扩展返回按钮 Visual；非按钮扩展（左区弹窗提供者）返回 null（不参与右区装配）。
/// </summary>
public interface IMenuBarExtension
{
    /// <summary>扩展标识（如 "ime" / "calendar" / "control-center" / "wlan" / "volume" / "bluetooth" / "battery" / "display" / "logo-menu" / "stacks-popup"）。</summary>
    string Id { get; }

    /// <summary>右区按钮的可视元素（由 MenuBarWindow 放在右区尾部）；非按钮扩展返回 null。</summary>
    FrameworkElement? GetVisual();

    /// <summary>
    /// 打开该扩展对应的弹窗。
    /// anchor：菜单栏窗口中触发元素的左上角屏幕坐标（弹窗以此为参照，通常铺在正下方）。
    /// 返回的窗口允许被重复调用：若已显示则只做置前；调用方不持有引用。
    /// </summary>
    void OpenPopup(Point anchorScreenTopLeft);

    /// <summary>显式关闭弹窗（若打开中），例如点击菜单别处时统一收起。</summary>
    void ClosePopup();
}
