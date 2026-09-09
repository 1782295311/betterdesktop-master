// BetterDesktop.Host — 菜单服务（--menu-service <x> <y> <mode> <path...>）
//
// 背景（2026-09-07 用户拍板"直接复用文件管理器的逻辑"）：自绘桌面本质是类文件管理器，
// 右键菜单应像 explorer 资源管理器窗口一样用本进程 IContextMenu 构建（文件管理器逻辑）。
// 但第三方 shell 扩展（WinRAR/杀毒/云盘等）在 BetterDesktop.Host.exe 内 GetUIObjectOf 加载
// 会崩溃（日志实锤：IShellFolder RCW ok 后中断）——因此菜单构建移到独立子进程：
// 扩展在子进程加载，崩溃只崩子进程，不拖垮宿主与 explorer；菜单逻辑与文件管理器完全一致。
//
// 生命周期：宿主右键 → 启动本模式实例（传坐标+路径）→ 本实例弹菜单 → 用户选择执行 → 退出。

using System;
using System.Linq;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Services;

namespace BetterDesktop.Host;

/// <summary>菜单服务：独立子进程构建并显示系统原生右键菜单（文件管理器逻辑）。</summary>
public static class MenuService
{
    /// <summary>解析参数并同步弹菜单（在 OnStartup 内 STA UI 线程同步执行，弹完即退出）。</summary>
    public static void Run(string[] args)
    {
        // args: --menu-service <x> <y> <mode> <path1> <path2> ...
        try
        {
            if (args.Length < 4 ||
                !int.TryParse(args[1], out var x) ||
                !int.TryParse(args[2], out var y))
            {
                DiagnosticLog.Trace("menu-service", "参数无效，退出");
                return;
            }

            var mode = args[3];
            var pos = new System.Windows.Point(x, y);

            if (string.Equals(mode, "background", StringComparison.Ordinal))
            {
                DiagnosticLog.Trace("menu-service", $"背景菜单 ({x},{y})");
                NativeMenuPopup.ShowBackgroundSync(pos);
            }
            else
            {
                var paths = args.Skip(4).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                DiagnosticLog.Trace("menu-service", $"图标菜单 n={paths.Count} first={paths.FirstOrDefault()}");
                NativeMenuPopup.ShowItemsSync(paths, pos);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-service", $"菜单服务异常: {ex.Message}");
        }
    }
}
