// BetterDesktop.Shell.Core —「桌面服务」命令通道（客户端）
//
// 【为什么需要】桌面层搬进独立进程（BetterDesktop.DesktopControl.exe）后，托盘 / CLI / 宿主都要能"叫它做事"：
//   · 弹「桌面控制」菜单（原生右键扩展那条链）；
//   · 翻转桌面自有的开关键（desktop.iconsHidden / desktop.doubleClickHideIcons / components.desktop）——
//     **必须由服务进程自己翻**：宿主读的是自己内存里的设置快照，外部进程写盘它不会重载；服务同理。
//   · 优雅停止。
//
// 协议刻意与宿主的 MenuCmd 同形（单行文本 `<magic><action>|<path>`，服务端按 `|` 拆）：
// 两个通道各自独立（不同管道名），但形状一致 = 心智负担与排查方式一致。
// ⚠️ 服务端实现在 packages/shell/shell-desktop-control/DesktopControlEntry.cs（常驻管道循环）。

using System;
using System.IO;
using System.IO.Pipes;

namespace BetterDesktop.Shell.Core.DesktopControl;

/// <summary>「桌面服务」命令通道客户端（一次性转发；失败返回 false 由调用方决定降级）。</summary>
public static class DesktopControlPipe
{
    /// <summary>管道名（服务端同名；与宿主 MenuCmd 通道相互独立）。</summary>
    public const string PipeName = "BetterDesktop.DesktopCmd";

    /// <summary>协议 magic 前缀（Desktop Command v1），服务端校验后才派发。</summary>
    public const string MagicPrefix = "BDDC1|";

    // ===== 动作名（跨进程契约，两端同步） =====

    /// <summary>在光标处弹出「桌面控制」菜单（由服务进程渲染，勾选态/置灰按当前真值）。</summary>
    public const string ActionShowMenu = "desktop-controls";

    /// <summary>翻转一个开关键（path = 命令名，如 icons / doubleclick / menubar）。</summary>
    public const string ActionToggleKey = "toggle-key";

    /// <summary>翻转"自绘桌面"总开关（components.desktop）。</summary>
    public const string ActionToggleDesktop = "toggle-desktop";

    /// <summary>优雅停止桌面服务。</summary>
    public const string ActionStop = "stop";

    /// <summary>
    /// 「桌面服务」是否在运行 —— 判据 = **它长期监听的命令管道存在**，不是进程名。
    /// <para>
    /// 【为什么不能用进程名判活】「桌面控制菜单」是**短命进程、与常驻服务同名**
    ///（都是 <c>BetterDesktop.DesktopControl.exe</c>，见 shell-desktop-control 的 <c>--desktop-controls</c> 分支）。
    /// 按进程名判活会让"正在弹菜单"被误判成"服务在运行"，后果有两条：
    ///   ① 服务真的死了却不被拉起（自绘桌面消失且不自愈）；
    ///   ② 用户每弹一次菜单就制造一次"在场 → 离场"抖动，而 Agent 的在场探测依此反复装/摘
    ///      桌面图标钩子与任务栏外观（用户观感："双击切换要按两次 / 任务栏外观闪动"）。
    /// </para>
    /// <para>托盘一直用的是管道判据（tray/PipeProbe.cs），此处把它收口成**全仓唯一实现**，供 Agent / 宿主 / CLI 共用。</para>
    /// </summary>
    public static bool IsServiceUp()
    {
        try
        {
            foreach (var name in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (string.Equals(Path.GetFileName(name), PipeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell.desktop", $"桌面服务管道探测失败: {ex.Message}");
        }

        return false;
    }

    /// <summary>转发一条命令给运行中的桌面服务。返回 false = 无运行实例（调用方走本地降级）。</summary>
    public static bool TrySend(string action, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
            writer.WriteLine($"{MagicPrefix}{action}|{path}");
            return true;
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell.desktop",
                $"桌面服务转发失败（服务未运行?）: {ex.Message}");
            return false;
        }
    }
}
