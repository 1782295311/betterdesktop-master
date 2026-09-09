using System.Windows;

namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>菜单请求（一次右键触发的完整上下文）。</summary>
/// <param name="Scope">触发作用域。</param>
/// <param name="Target">目标对象（场景自定义：BrowserEntry/Dock 项/窗口…，贡献者按需解析）。</param>
/// <param name="ScreenPosition">屏幕坐标（DIP 逻辑坐标；独立弹层窗口 Left/Top 直接使用）。</param>
/// <param name="AnchorRect">锚定矩形（可选，图标右键时为图标矩形）。</param>
/// <param name="File">文件身份（文件场景必填；空白/系统场景为 null）。</param>
/// <param name="SelectedPaths">选中路径集（多选时能力过滤取交集）。</param>
/// <param name="ShiftPressed">是否按住了 Shift（Shift 扩展项显示的判据；由调用方在右键事件中传入）。</param>
public sealed record MenuRequest(
    MenuScope Scope,
    object? Target,
    Point ScreenPosition,
    Rect? AnchorRect = null,
    FileIdentity? File = null,
    IReadOnlyList<string>? SelectedPaths = null,
    bool ShiftPressed = false);

/// <summary>菜单关闭结果。</summary>
public enum MenuResultKind
{
    /// <summary>用户点了某命令项。</summary>
    CommandExecuted,

    /// <summary>Esc/外部点击/失焦关闭。</summary>
    Cancelled,

    /// <summary>未展示（构建失败等）。</summary>
    None,
}

/// <param name="Kind">结果种类。</param>
/// <param name="ExecutedCommandId">被执行项的 MenuItemDef.Id（Kind=CommandExecuted 时非空）。</param>
public sealed record MenuResult(MenuResultKind Kind, string? ExecutedCommandId = null);
