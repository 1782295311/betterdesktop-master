// BetterDesktop.Shell.Dock — 自动隐藏轮询的 tick 档位策略（纯函数，可单测）。
//
// H2 治理（2026-09-03，A/B 数据见 docs/design-proposals/2026-09-03-H2-Dock轮询降频.md）：
// 原实现 _autoHideTimer 固定 60ms 常驻（构造即 Start，永不停表），UI 线程每秒被唤醒 ≈16.7 次，
// 每 tick 做 GetCursorPos/布局查询/GetLastInputInfo 等 P/Invoke；而其中绝大多数 tick 的裁决结果是
// "维持现状"（SetDockVisible 状态机短路）。改为【双档自适应】：
//  - 快档 60ms： dock 隐藏（等待唤出）或光标贴近 dock 预唤出带——
//    这些状态下 tick 裁决可能在下一拍改变，需要快速响应；
//  - 慢档 250ms：其余（dock 常驻显示、光标远离、用户活跃/全屏稳定态）——纯"确认维持现状"的空转轮询。
// UX 契约：唤出延迟（贴底热区 → 显示）最坏 = 一次慢档 + 一次快档 ≈ 310ms，实测可感知门槛
// 一般在 400ms+；全屏隐藏/恢复延迟最坏 ≈ 250ms（视频/游戏场景无感知）。
// A/B 门槛（DockTickPolicyTests 守护）：空闲 1h tick 数 60_000 → ≤ 14_400（-76%+），
// 且贴边/悬停/隐藏/全屏四场景全部按契约落档。

namespace BetterDesktop.Shell.Dock;

public static class DockTickPolicy
{
    /// <summary>快档间隔：需要下一拍就可能改变裁决的状态。</summary>
    public const int FastMs = 60;

    /// <summary>慢档间隔：纯维持现状的空转确认轮询。</summary>
    public const int SlowMs = 250;

    /// <summary>下一拍 tick 间隔（毫秒）。纯函数：由 tick 裁决输入决定档位。</summary>
    /// <param name="statePending">dock 处于隐藏态（等待贴边/悬停唤出）——下一拍可能转为显示。</param>
    /// <param name="cursorNearDock">光标在 dock 上 / 贴底预唤出带内（含 dock 隐藏时的几何带判定）。</param>
    public static int NextIntervalMs(bool statePending, bool cursorNearDock)
    {
        return (statePending || cursorNearDock) ? FastMs : SlowMs;
    }
}
