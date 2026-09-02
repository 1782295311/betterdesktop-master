// BetterDesktop.Shell.MenuBar — 菜单栏度量常量（单一真相源）
// 菜单栏固定在主屏顶部、全宽、高度固定。所有需要锚定菜单栏下沿、或判定"是否点中菜单栏条带"的代码都应引用此处，避免各处散落魔法数彼此漂移。

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>菜单栏固定度量（逻辑像素）。</summary>
internal static class MenuBarMetrics
{
    /// <summary>菜单栏条带高度（逻辑像素）。与 MenuBarWindow.Height / MinHeight / MaxHeight 同源。
    /// PopupAnchor 用它把弹窗锚在菜单栏正下方；任何新增的菜单栏高度引用都应走这里。</summary>
    public const double MenuBarHeight = 20;
}
