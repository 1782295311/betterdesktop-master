// WindowPeek 单测：DWM Live Preview 方案（2026-09-11，替代旧 SetWindowPos 抬窗）。
// 旧版 Plan/SnapshotZOrder（Z 序裁决纯函数）已随 Z 序机制整体移除——DwmActivateLivePreview
// 不触碰 Z 序，无锚点裁决需求（见计划 2026-09-11-host-elevation-dock-peek.md）。
// 保留的边界契约：无效句柄拒绝、End/Cancel 幂等、Begin 前置校验失败不改动状态。

using System;
using BetterDesktop.Shell.WindowTracker.Thumbnail;
using Xunit;

namespace BetterDesktop.Shell.WindowTracker.Tests;

public class WindowPeekTests
{
    [Fact]
    public void Begin_ZeroHandle_ReturnsFalse_AndStaysInactive()
    {
        using var peek = new WindowPeek();

        Assert.False(peek.Begin(IntPtr.Zero, IntPtr.Zero));
        Assert.False(peek.IsActive);
        Assert.Equal(IntPtr.Zero, peek.Target);
    }

    [Fact]
    public void End_WithoutBegin_IsIdempotentNoThrow()
    {
        using var peek = new WindowPeek();

        peek.End();
        peek.End();

        Assert.False(peek.IsActive);
    }

    [Fact]
    public void Cancel_ClearsPendingRestore_EndBecomesNoOp()
    {
        // 点选缩略图走 Cancel：放弃还原（由 ActivateWindow 接管），
        // 否则 End 会先把窗口收回最小化、激活再还原，闪一下。
        using var peek = new WindowPeek();

        peek.Cancel();
        peek.End();

        Assert.False(peek.IsActive);
        Assert.Equal(IntPtr.Zero, peek.Target);
    }
}
