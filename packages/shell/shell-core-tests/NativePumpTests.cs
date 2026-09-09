// BetterDesktop.Shell.Core.Tests — Native 互操作层单测（Phase A：WinEventPump / MouseHook / NativeMethods）
// 覆盖：生命周期正常路径、边界（重复订阅/重复卸载/句柄零）、异常路径（Dispose 后调用）。
// 真实事件送达（前台切换/窗口创建）属端到端场景，由 smoke-test + 真机走查覆盖（DoD D1）。

using System;
using System.Threading;
using BetterDesktop.Shell.Core.Native;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

public class WinEventPumpTests : IDisposable
{
    private readonly WinEventPump _pump = new();

    [Fact]
    public void Subscribe_Returns_NonZeroHookHandle()
    {
        var hook = _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, (_, _) => { });
        Assert.NotEqual(IntPtr.Zero, hook);
        _pump.Unsubscribe(hook);
    }

    [Fact]
    public void Subscribe_Tracks_SubscriberCount()
    {
        var h1 = _pump.Subscribe(0x8000, 0x8001, (_, _) => { });
        var h2 = _pump.Subscribe(0x0003, 0x0003, (_, _) => { });
        Assert.Equal(2, _pump.SubscriberCount);
        _pump.Unsubscribe(h1);
        Assert.Equal(1, _pump.SubscriberCount);
        _pump.Unsubscribe(h2);
        Assert.Equal(0, _pump.SubscriberCount);
    }

    [Fact]
    public void Unsubscribe_WithZeroHandle_IsNoOp()
    {
        Assert.False(_pump.Unsubscribe(IntPtr.Zero));
    }

    [Fact]
    public void Unsubscribe_UnknownHook_ReturnsFalse()
    {
        Assert.False(_pump.Unsubscribe(new IntPtr(0xDEAD)));
    }

    [Fact]
    public void SameRange_MultipleCallbacks_Independent()
    {
        var fired = 0;
        var h1 = _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, (_, _) => Interlocked.Increment(ref fired));
        var h2 = _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, (_, _) => Interlocked.Increment(ref fired));
        Assert.NotEqual(IntPtr.Zero, h1);
        Assert.NotEqual(IntPtr.Zero, h2);
        Assert.NotEqual(h1, h2);
        _pump.Unsubscribe(h1);
        _pump.Unsubscribe(h2);
    }

    [Fact]
    public void NullCallback_ReturnsZero()
    {
        Assert.Equal(IntPtr.Zero, _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, null!));
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        _pump.Dispose();
        _pump.Dispose(); // 幂等
    }

    [Fact]
    public void Dispose_Unsubscribes_AllHooks()
    {
        _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, (_, _) => { });
        _pump.Dispose();
        Assert.Equal(0, _pump.SubscriberCount);
    }

    [Fact]
    public void Subscribe_After_Dispose_ReturnsZero()
    {
        _pump.Dispose();
        Assert.Equal(IntPtr.Zero, _pump.Subscribe(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, (_, _) => { }));
    }

    public void Dispose() => _pump.Dispose();
}

public class MouseHookTests : IDisposable
{
    [Fact]
    public void Start_Installs_Hook()
    {
        using var hook = new MouseHook(null);
        Assert.True(hook.Start());
    }

    [Fact]
    public void Start_Twice_IsIdempotent()
    {
        using var hook = new MouseHook(null);
        Assert.True(hook.Start());
        Assert.True(hook.Start());
    }

    [Fact]
    public void Stop_Without_Start_IsSafe()
    {
        using var hook = new MouseHook(null);
        hook.Stop(); // 未安装时停止：无异常
        hook.Stop();
    }

    [Fact]
    public void CallNext_Without_Hook_DoesNotThrow()
    {
        using var hook = new MouseHook(null);
        _ = hook.CallNext(0, IntPtr.Zero, IntPtr.Zero);
    }

    [Fact]
    public void Dispose_Uninstalls_Hook()
    {
        var hook = new MouseHook(null);
        hook.Start();
        hook.Dispose();
        hook.Dispose(); // 幂等
    }

    public void Dispose() { }
}
