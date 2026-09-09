// BetterDesktop.Kernel — 跨插件共享的"拖放目标"屏幕矩形
// 用途：桌面自由布局拖动图标时，需要知道"dock 栏回收站"在屏幕上的物理像素矩形
//      （与 GetCursorPos 同域），以支持"拖到回收站松手 = 移入回收站"。
//      shell-dock 的 DockWindow 在系统功能区布局后/窗口移动后写入；
//      shell-desktop 的桌面拖动逻辑读取。放 kernel 公共层避免 shell-desktop ↔ shell-dock 环依赖。
// 生命周期：dock 未加载 / 回收站被隐藏 → 值为 null（桌面侧视为 dock 回收站不在场）。

using System;

namespace BetterDesktop.Kernel.Core;

/// <summary>跨插件共享的"拖放目标"屏幕矩形（物理像素，与 GetCursorPos 同域）。</summary>
public static class DockDropTargets
{
    private static System.Drawing.Rectangle? _dockRecycleBinScreenRect;

    /// <summary>
    /// dock 栏回收站图标在屏幕上的物理像素矩形；dock 未加载 / 回收站隐藏 / dock 关闭时为 null。
    /// </summary>
    public static System.Drawing.Rectangle? DockRecycleBinScreenRect
    {
        get => _dockRecycleBinScreenRect;
        set => _dockRecycleBinScreenRect = value;
    }

    /// <summary>dock 关闭 / 插件卸载时清空，避免残留过期矩形误命中。</summary>
    public static void ClearDockRecycleBinRect() => _dockRecycleBinScreenRect = null;

    // ===== 回收站悬停高亮（桌面拖动 → dock 回收站高亮） =====
    // 桌面拖动中命中 dock 回收站矩形时置 true，dock 侧 150ms 轮询读取并高亮回收站图标。
    private static bool _isRecycleBinHovered;

    /// <summary>桌面拖动当前是否悬停在 dock 栏回收站上（dock 侧据此高亮回收站图标）。</summary>
    public static bool IsRecycleBinHovered
    {
        get => _isRecycleBinHovered;
        set => _isRecycleBinHovered = value;
    }
}
