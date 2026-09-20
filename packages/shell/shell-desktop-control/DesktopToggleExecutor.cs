// BetterDesktop.DesktopControl —「桌面控制」动作执行路由（独立进程侧）
//
// 【为什么不能只写 settings.json】宿主读的是**自己内存里的设置快照**：外部进程改盘它不会自动重载。
// 所以宿主在线时必须走命令桥（宿主进程内 Set → 事件总线 → 组件即时反应），这与 CLI/tray 的既有路由一致。
//
// 【宿主缺席时为什么要额外"原生生效"】用户 2026-09-17 实测原话："点了没反应"——
// 旧路径只写盘，而写盘的消费者（宿主）根本不在。故宿主缺席时：
//   ① 直写 settings.json（下次宿主上线即为目标状态）；
//   ② 能被 explorer 原生层直接生效的（图标 / 任务栏）**当场生效**（DesktopControlNative）。
// 只属于宿主的组件（菜单栏 / Dock / 热键侧板 / 双击钩子）在菜单层就已被置灰，不会走到这里。

using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.DesktopControl;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>一次「桌面控制」翻转的执行。</summary>
internal static class DesktopToggleExecutor
{
    /// <summary>命令桥动作名（宿主 Bootstrap 的 <c>case "toggle-key"</c> 消费）。</summary>
    private const string PipeAction = "toggle-key";

    public static void Apply(string name, ISettingsService settings, bool hostAlive)
    {
        if (!DesktopToggleCatalog.TryGet(name, out var spec))
        {
            DesktopControlLog.Trace($"未知开关：{name}（目录里没有，忽略点击）");
            return;
        }

        // ① 宿主在线：命令桥热切（成败以管道送达为准；送达失败继续走 ② 的直写，不丢用户点击）。
        if (hostAlive && MenuCommandPipeClient.TrySend(PipeAction, name))
        {
            DesktopControlLog.Trace($"切换（命令桥热切）：{name} → 宿主在线处理");
            return;
        }

        // ② 宿主缺席 / 管道不可达：直写 + 原生层立即生效。
        var current = settings.Get(spec.SettingsKey, spec.Default);
        var next = !current;
        settings.Set(spec.SettingsKey, next);

        // 显式留痕：让「桌面控制」的选择压过其它功能的默认隐藏（如 dock 默认隐藏原生任务栏）。
        foreach (var (overrideKey, overrideValue) in DesktopToggleCatalog.ExplicitOverrides(spec, next))
        {
            settings.Set(overrideKey, overrideValue);
        }

        if (spec.NativeEffective)
        {
            var applied = DesktopControlNative.Apply(spec.Name, next);
            DesktopControlLog.Trace($"切换（免宿主）：{spec.SettingsKey}={next} 原生层生效={applied}");
            return;
        }

        // 走到这里说明该开关既无原生效果、宿主又不在。
        //
        // 【2026-09-20 用户实测 —— 这里是典型静默失败，原注释的前提是错的】
        // 原注释写"正常不会发生：菜单已置灰"，但那个前提**只对宿主内自绘菜单成立**：
        //   · 自绘右键菜单（DesktopControlMenu.Build）会按 hostRunning 把宿主内组件项置灰；
        //   · 但**系统右键菜单**那条路不经过它 —— explorer 原生扩展 → Cli.exe --menu-batch
        //     → 拉起本进程 `--toggle-key <name>`，**全程没有"宿主在线探测"这一层**，
        //     所以这些项在系统右键里**照常可点**，点了就走到这里。
        // 原文案只写一行日志并以退出码 0 结束 ⇒ 用户以为生效了。
        // 这属于本项目 defensive-patterns 反复批判的形态：**"将来会被应用的意图"与"已经生效"
        // 对用户不可区分**。
        //
        // 处置：仍然写盘（下一轮宿主上线即为目标状态，这是有价值的部分 —— 用户要的是"别丢了我的选择"），
        // 但**必须让用户当场看见**。措辞刻意区分"已暂存（未生效）"与"已生效"，不让二者混同。
        DesktopControlLog.Trace($"切换（免宿主·未生效）：{spec.SettingsKey}={next}（该开关需要主程序在运行）");
        DesktopControlNotice.ShowPendingUntilHost(
            spec.DisplayName.Length > 0 ? spec.DisplayName : spec.Name,
            spec.SettingsKey,
            next);
    }
}
