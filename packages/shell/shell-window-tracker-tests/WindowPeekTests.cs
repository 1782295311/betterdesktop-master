// WindowPeek 单测：Z 序裁决是纯函数（不调 Win32），headless 可确定性覆盖。
// 覆盖点对应计划 2026-09-05-dock-thumbnail-hover-peek.md §4 红线 2：
// 还原锚点必须是「抬起前紧邻其上的**非置顶**窗口」，置顶窗口不得当锚点（会把目标带进置顶层）。

using System;
using System.Collections.Generic;
using BetterDesktop.Shell.WindowTracker.Thumbnail;
using Xunit;

namespace BetterDesktop.Shell.WindowTracker.Tests;

public class WindowPeekTests
{
    private static ZOrderEntry Normal(int id) => new(new IntPtr(id), false);
    private static ZOrderEntry Top(int id) => new(new IntPtr(id), true);

    private static List<ZOrderEntry> Order(params ZOrderEntry[] entries) => new(entries);

    [Fact]
    public void Plan_TargetInMiddle_AnchorIsNearestNonTopmostAbove_SkippingTopmost()
    {
        // 自顶向底：置顶(dock) / 置顶(浮层) / 普通A / 普通B / 目标 / 普通C
        var target = new IntPtr(50);
        var order = Order(Top(10), Top(11), Normal(20), Normal(21), new ZOrderEntry(target, false), Normal(30));

        var plan = WindowPeek.Plan(order, target);

        Assert.True(plan.CanPeek);
        // 紧邻其上的非置顶窗口 = 21（置顶的 10/11 必须被跳过）
        Assert.Equal(new IntPtr(21), plan.InsertAfterOnRestore);
    }

    [Fact]
    public void Plan_OnlyTopmostAbove_AnchorIsZero_MeaningHwndTop()
    {
        // 目标本就是普通层最前：上方只有置顶窗口 → 还原时回到 HWND_TOP
        var target = new IntPtr(50);
        var order = Order(Top(10), Top(11), new ZOrderEntry(target, false), Normal(30));

        var plan = WindowPeek.Plan(order, target);

        Assert.True(plan.CanPeek);
        Assert.Equal(IntPtr.Zero, plan.InsertAfterOnRestore);
    }

    [Fact]
    public void Plan_TargetIsBottomMost_AnchorIsImmediateAbove()
    {
        var target = new IntPtr(99);
        var order = Order(Top(10), Normal(21), Normal(22), new ZOrderEntry(target, false));

        var plan = WindowPeek.Plan(order, target);

        Assert.True(plan.CanPeek);
        Assert.Equal(new IntPtr(22), plan.InsertAfterOnRestore);
    }

    [Fact]
    public void Plan_TargetAlreadyTopmost_CannotPeek()
    {
        // 目标本身在置顶层：已经在最上，重排它毫无意义且会打乱置顶层
        var target = new IntPtr(10);
        var order = Order(new ZOrderEntry(target, true), Normal(20), Normal(21));

        var plan = WindowPeek.Plan(order, target);

        Assert.False(plan.CanPeek);
        Assert.Equal(IntPtr.Zero, plan.InsertAfterOnRestore);
    }

    [Fact]
    public void Plan_TargetNotInSnapshot_CannotPeek()
    {
        var order = Order(Normal(20), Normal(21));

        var plan = WindowPeek.Plan(order, new IntPtr(777));

        Assert.False(plan.CanPeek);
    }

    [Fact]
    public void Plan_ZeroTarget_CannotPeek()
    {
        var plan = WindowPeek.Plan(Order(Normal(20)), IntPtr.Zero);

        Assert.False(plan.CanPeek);
    }

    [Fact]
    public void Begin_ZeroHandle_ReturnsFalse_AndStaysInactive()
    {
        using var peek = new WindowPeek();

        Assert.False(peek.Begin(IntPtr.Zero));
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
    public void Plan_MinimizedWindow_IsStillPeekableAndAnchorable()
    {
        // 契约（用户要求 peek 必须覆盖最小化窗口）：最小化窗口的 WS_VISIBLE 仍在、
        // IsWindowVisible 恒为 true，所以它照样留在 EnumWindows 快照里——
        // 既能当 peek 目标，也能当别人的还原锚点。Z 序层不因最小化而特殊化。
        var target = new IntPtr(50);
        var order = Order(Top(10), Normal(20), new ZOrderEntry(target, false), Normal(30));

        var plan = WindowPeek.Plan(order, target);

        Assert.True(plan.CanPeek);
        Assert.Equal(new IntPtr(20), plan.InsertAfterOnRestore);
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
