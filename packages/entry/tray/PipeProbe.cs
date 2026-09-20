using System.Diagnostics;

namespace BetterDesktop.Tray;

/// <summary>
/// 宿主管道探测（只探测存在性，不实现协议——协议唯一实现在 CLI/kernel，避免两处漂移）。
///
/// 为什么需要它：CLI 在"无宿主"时对 <c>--menu-cmd &lt;需宿主动作&gt;</c> 会弹**原生 MessageBox**，
/// 那是**模态阻塞、没有超时**的——会一直停到用户手动关闭为止（自检里实测过一次约 9.8s，
/// 但那只是"当时有人点掉了它"，不是固定时长）。托盘若在宿主未就绪时贸然发命令，
/// 用户就会看到一个莫名弹窗。因此发命令前先等管道出现；管道在 = 宿主服务已装配完成。
/// </summary>
internal static class PipeProbe
{
    /// <summary>
    /// 与 <c>kernel/MenuCommandPipeClient.HostPipeName</c> 保持一致（该常量是契约的一部分）。
    ///
    /// 【2026-09-19 换名】探测的是"**宿主**在不在"（本文件的全部用途），而 legacy 服务端
    /// 已从 `BetterDesktop.MenuCmd` 改为 `BetterDesktop.HostCmd` —— 名字分开后，
    /// `MenuCmd` 归 core（控制面），不再是"宿主在线"的信号。见计划 §13.17。
    /// </summary>
    private const string HostCmdPipeName = "BetterDesktop.HostCmd";

    /// <summary>与 <c>shell-core/DesktopControl/DesktopControlPipe.PipeName</c> 保持一致（跨进程契约）。</summary>
    private const string DesktopCmdPipeName = "BetterDesktop.DesktopCmd";

    public static bool IsMenuPipeUp() => IsPipeUp(HostCmdPipeName);

    /// <summary>
    /// 桌面服务**命令管道**是否存在 = 常驻服务模式正在运行。
    /// <para>
    /// 【为什么用管道而不是进程名】「桌面控制菜单」是短命进程、与常驻服务**同名**
    /// （都是 BetterDesktop.DesktopControl.exe，见 DesktopControlEntry 的 --desktop-controls 分支），
    /// 托盘按进程名判定会把"正在弹菜单"误报成"服务在运行" → 用户点「启动桌面服务」被置灰、
    /// 点「停止」误杀菜单进程。服务模式独有特征是它监听的命名管道，故用它判定。
    /// </para>
    /// </summary>
    public static bool IsDesktopServicePipeUp() => IsPipeUp(DesktopCmdPipeName);

    /// <summary>枚举 \\.\pipe\ 判断指定管道是否存在（精确比较，避免 "FooXXX" 被误判就绪）。</summary>
    private static bool IsPipeUp(string pipeName)
    {
        try
        {
            foreach (var name in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (string.Equals(Path.GetFileName(name), pipeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"管道探测失败（{pipeName}）: {ex.Message}");
        }

        return false;
    }

    /// <summary>轮询等待宿主管道就绪（宿主冷启动含 splash + 插件装配，实测约 20s）。</summary>
    public static bool WaitForMenuPipe(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsMenuPipeUp())
            {
                TrayLog.Write($"宿主管道已就绪（等待 {sw.ElapsedMilliseconds}ms）");
                return true;
            }

            Thread.Sleep(300);
        }

        TrayLog.Write($"等待宿主管道超时（{timeoutMs}ms）");
        return false;
    }
}
