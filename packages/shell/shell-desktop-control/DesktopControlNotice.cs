// BetterDesktop.DesktopControl — 面向用户的「未能生效」提示
//
// 【为什么需要（2026-09-20 用户实测）】「桌面控制」里属于**宿主内组件**的开关
//（双击隐藏图标 / 菜单栏 / Dock / 热键侧板 / 灵动岛）在宿主缺席时**无法生效** ——
// 它们的效果由宿主的插件消费（图标钩子、菜单栏窗口、Dock 层…），而宿主不在。
//
// 旧行为：只写一行日志（DesktopToggleExecutor 的"切换（免宿主）"）并以退出码 0 结束。
// 用户视角："点了，勾变了（或没变），但桌面什么都没发生" —— 典型**静默失败**：
// **"将来会被应用的意图"与"已经生效"对用户不可区分**。
//
// 为什么宿主内自绘菜单没暴露这个坑：那份菜单会按 hostRunning 把这类项**置灰**（DesktopControlMenu）。
// 但**系统右键菜单**那条路（explorer 原生扩展 → Cli.exe --menu-batch → 本进程 --toggle-key）
// **没有宿主在线探测这一层**，所以同样这些项在系统右键里照常可点。
//
// 措辞纪律：必须把"**已暂存（未生效）**"与"已生效"分开说清楚，不能让用户以为已经生效。
// 提示失败绝不能影响主流程（本进程是短命进程，崩了用户只看到"点了没反应"）。

using System;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>把"未生效"这件事当面告诉用户（而不是只写日志）。</summary>
internal static class DesktopControlNotice
{
    /// <summary>
    /// 提示「此开关已记下，但要等主程序起来才生效」。
    /// </summary>
    /// <param name="displayName">开关的显示名（来自 DesktopToggleCatalog.DisplayName）。</param>
    /// <param name="settingsKey">已写入的设置键（原样展示，便于用户/支持人员核对）。</param>
    /// <param name="value">写入的值。</param>
    public static void ShowPendingUntilHost(string displayName, string settingsKey, bool value)
    {
        try
        {
            System.Windows.MessageBox.Show(
                $"「{displayName}」需要 BetterDesktop 主程序在运行，本次**未能生效**。\n\n" +
                $"您的选择已记下：{settingsKey} = {value.ToString().ToLowerInvariant()}\n" +
                "主程序启动后会自动应用。\n\n" +
                "（启动主程序：托盘图标 → 启动主程序）",
                "BetterDesktop",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // 提示本身失败不能影响主流程：设置已落盘，行为已如日志。
            DesktopControlLog.Warn($"提示用户失败（不影响已落盘的设置）: {ex.Message}");
        }
    }
}
