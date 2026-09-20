// BetterDesktop.DesktopControl —「桌面控制」独立进程入口（2026-09-17 用户需求）
//
// 【需求】主程序（BetterDesktop.Host.exe）**没启动**时，「桌面控制」也必须能正常用（"像剪贴板历史一样的级别"）。
//
// 【为什么不塞进宿主/托盘】
//   · 塞进宿主 = 又变成"要有主程序"，正是本次要解决的依赖；
//   · 塞进托盘进程 = 托盘是**零依赖 WinForms 保活进程**（最后一个还能用的程序），把 WPF 菜单搬进去会让它变重、
//     崩溃面变大（托盘挂了 = 独立功能全挂）。故独立成自己的 exe，与 Clipboard.Panel 同一级别。
//
// 【入口契约】
//   --desktop-controls  在光标处弹出「桌面控制」菜单（默认模式，无参同义）。
//   退出码：0 正常；2 参数不认识（本进程不认的动作不该静默吞掉）。
//
// 【进程形态】**短命**：弹菜单 → 用户点选/关闭 → 退出。因此不进看门狗（进了会被反复拉起，毫无意义）。
//
// 【同时保留】宿主内自绘桌面右键的「桌面控制」仍走进程内渲染（DesktopControlMenu 同一份实现）——
// 两条路径共用同一套内容与动作语义，只是承载进程不同。M2（自绘桌面也搬进来）之后统一。

using System;
using System.Windows;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.DesktopControl;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        DesktopControlLog.Install();
        DesktopControlLog.Trace($"=== 启动：args=[{string.Join(' ', args)}] ===");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // 全局兜底：任何未处理异常都要留痕（短命进程最怕"静默消失"）。
        // 崩溃路径必须同步刷盘：本进程弹完菜单即退，异步队列来不及刷就什么都没了。
        app.DispatcherUnhandledException += (_, e) =>
        {
            DesktopControlLog.Error($"Dispatcher 未处理异常: {e.Exception}");
            DesktopControlLog.Flush();
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            DesktopControlLog.Error($"AppDomain 未处理异常: {e.ExceptionObject}");
            DesktopControlLog.Flush();
        };

        var exitCode = ExitCodes.Ok;
        app.Startup += (_, _) => exitCode = DesktopControlEntry.Run(app, args);
        app.Run();
        DesktopControlLog.Trace($"=== 退出：{exitCode} ===");
        // 短命进程退出前必须刷盘，否则最后几条（退出码 / 失败原因）随进程一起消失。
        DesktopControlLog.Flush();
        return exitCode;
    }
}

/// <summary>退出码（与 CLI 分层约定一致：0 成功 / 2 用法错误，便于原生侧与诊断脚本判读）。</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 2;
    public const int Failed = 5;
}
