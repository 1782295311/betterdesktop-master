// BetterDesktop.Shell.Island — 抑制判定（纯函数，可单测）
//
// 判据与 Windows 系统通知同源（SHQueryUserNotificationState）：全屏应用/游戏/演示模式/忙碌时，
// 岛不弹出（活动进队列，解除后按优先级补播）。**保守原则**：读不到状态时按"可通知"处理，
// 绝不因为一次查询失败就把用户的消息吞掉（失败可见 > 静默丢失）。

using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Island.Services;

/// <summary>用户通知状态 → 是否抑制岛的呈现。</summary>
public static class SuppressionPolicy
{
    /// <summary>该通知状态是否应抑制岛弹出。</summary>
    public static bool ShouldSuppress(int userNotificationState) => userNotificationState switch
    {
        NativeMethods.QUNS_BUSY => true,
        NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN => true,
        NativeMethods.QUNS_PRESENTATION_MODE => true,
        // ACCEPTS_NOTIFICATIONS / QUIET_TIME / NOT_PRESENT / APP 以及任何未知值：不抑制
        _ => false,
    };

    /// <summary>查询当前用户通知状态（失败返回 false，调用方按"不抑制"处理）。</summary>
    public static bool TryQuery(out int userNotificationState)
    {
        var hr = NativeMethods.SHQueryUserNotificationState(out var state);
        userNotificationState = state;
        return hr == 0;
    }

    /// <summary>一步到位：查询 + 判定（查询失败 → 不抑制）。</summary>
    public static bool IsSuppressedNow()
        => TryQuery(out var state) && ShouldSuppress(state);
}
