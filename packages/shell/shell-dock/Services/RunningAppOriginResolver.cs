// BetterDesktop.Shell.Dock — 运行项「溯源到应用本体」的判定（T3，纯函数）
//
// 【要解决的问题】运行区右键「固定到 Dock」时，直接拿**运行中那个进程的 exe** 去固定，
// 有可能是"程序此刻的状态"而不是用户认知里的那个应用：
//   · Electron 类应用的主窗口可能挂在子进程/helper 上；
//   · 启动器拉起的本体（launcher.exe → 游戏本体）在索引里没有登记。
//
// 【为什么不用"路径相似/同安装根"去猜】那正是 2026-09-14 真机误绑的成因：
//   S8 重绑梯子第 ④ 级「同安装根」把 WorkBuddy 错指到同根下的 CodeBuddy CN.exe
//   （%LocalAppData%\Programs 是**共享容器**）。**路径相似 ≠ 同一个应用**，猜错的代价是改坏固定库。
//
// 【本方案】只用**事实**：谁启动了谁。沿进程父子链由近及远，取**第一个"应用索引里已知"的祖先**——
//   · 运行 exe 本身就是已知应用 → 取它（= 现状行为，不变）；
//   · 它是未登记的 helper/子进程 → 向上一层取到已知的父应用（= 溯源到本体）；
//   · 一路都未知（例如游戏本体是启动器拉起的、而启动器也未登记）→ 返回 null，
//     调用方**退回当前 exe 并记日志** —— 绝不猜。
//
// 本类只做"链上挑谁"的判定（纯函数、可单测）；进程链的获取是原生部分（Native/ProcessGenealogy）。

using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Dock.Services;

internal static class RunningAppOriginResolver
{
    /// <summary>
    /// 从进程祖先链里挑出应当固定的路径。
    /// </summary>
    /// <param name="ancestors">
    /// 祖先链的 exe 路径，**由近到远**（<c>[0]</c> = 运行窗口自身的进程，
    /// 其后依次是父、祖父……）。空串/空白项按"未知"跳过。
    /// </param>
    /// <param name="isKnownApp">
    /// 该 exe 路径是否为应用索引里已知的应用（调用方注入，通常包 <c>IAppSourceService.ResolveFromPath</c>）。
    /// </param>
    /// <returns>应当固定的路径；链上没有任何已知应用时返回 <c>null</c>（调用方退回原路径并记日志）。</returns>
    public static string? Resolve(IReadOnlyList<string> ancestors, Func<string, bool> isKnownApp)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(isKnownApp);

        // 由近及远取**第一个**已知应用（不是最外层）：
        // 对「helper → 主应用」取到主应用（本体）；对「游戏本体 → 启动器」取到游戏本体本身
        // （用户此刻看的就是它），与用户"我点是哪个就固定哪个"的直觉一致。
        foreach (var path in ancestors)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (isKnownApp(path))
            {
                return path;
            }
        }

        return null;
    }
}
