using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Services;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// 程序项右键接入（2026-09-05 架构收口）：开始菜单不再自绘菜单——条目右键
/// 直接接系统原生菜单（NativeMenuPopup，explorer 同款渲染）。
/// 【白话】"开始菜单条目的右键菜单" → 本文件 AttachNative（host=布局里的条目行/悬停框）。
/// 有有效路径（.lnk/目标）才挂接；UWP 等无路径项不挂 = 主体未申明菜单，
/// 按拍板「主体没申明就不实现自绘菜单，统一交由系统处理」。
/// </summary>
internal static class AppItemActions
{
    /// <summary>把开始菜单条目的右键接到系统原生菜单（有路径才有菜单）。</summary>
    public static void AttachNative(FrameworkElement host, AppItem app)
    {
        host.MouseRightButtonUp += (_, e) =>
        {
            try
            {
                var paths = NativePaths(app);
                if (paths is not { Count: > 0 })
                {
                    return; // 无路径（UWP 等）：不弹任何自研菜单
                }
                var physical = host.PointToScreen(e.GetPosition(host));
                _ = NativeMenuPopup.TryShowItems(
                    paths, physical,
                    (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
            }
            catch
            {
                // 右键接入失败不拖垮宿主交互（M10）
            }
        };
    }

    /// <summary>
    /// 原生菜单路径源：优先 .lnk（系统菜单含"固定到任务栏/打开文件位置"全套），回退真实目标。
    /// </summary>
    public static IReadOnlyList<string>? NativePaths(AppItem app)
    {
        if (!string.IsNullOrWhiteSpace(app.ShortcutPath) && File.Exists(app.ShortcutPath))
        {
            return [app.ShortcutPath];
        }
        if (!string.IsNullOrWhiteSpace(app.TargetPath)
            && (File.Exists(app.TargetPath) || Directory.Exists(app.TargetPath)))
        {
            return [app.TargetPath];
        }
        return null;
    }
}
