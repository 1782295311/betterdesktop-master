using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using Xunit;

namespace BetterDesktop.Shell.Capture.Tests;

/// <summary>后端选择纯函数（计划 T1）：版本/远程会话/HDR/偏好 → 后端序列与降级原因。</summary>
public sealed class BackendSelectorTests
{
    private static readonly SelectorInput LocalWgc = new(true, false, false, null, true);
    private static readonly SelectorInput LocalNoWgc = new(false, false, false, null, true);
    private static readonly SelectorInput RemoteWgc = new(true, true, false, null, true);
    private static readonly SelectorInput RemoteNoWgcHdr = new(false, true, true, null, true);

    [Fact]
    public void WgcLocal_OrdersWgcDxgiBitBlt_NoReasons()
    {
        var plan = CaptureBackendSelector.Select(LocalWgc);
        Assert.Equal(
            new[] { CaptureBackend.Wgc, CaptureBackend.Dxgi, CaptureBackend.BitBlt },
            plan.Order);
        Assert.Empty(plan.DegradeReasons);
    }

    [Fact]
    public void NoWgc_DropsWgc_AddsVisibleReason()
    {
        var plan = CaptureBackendSelector.Select(LocalNoWgc);
        Assert.DoesNotContain(CaptureBackend.Wgc, plan.Order);
        Assert.Single(plan.DegradeReasons, r => r.Contains("WGC", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteSession_SkipsDxgi_AddsVisibleReason()
    {
        var plan = CaptureBackendSelector.Select(RemoteWgc);
        Assert.Equal(new[] { CaptureBackend.Wgc, CaptureBackend.BitBlt }, plan.Order);
        Assert.Contains(plan.DegradeReasons, r => r.Contains("DXGI", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteNoWgcHdr_OnlyBitBlt_HdrDistortionVisible()
    {
        var plan = CaptureBackendSelector.Select(RemoteNoWgcHdr);
        Assert.Equal(new[] { CaptureBackend.BitBlt }, plan.Order);
        Assert.Contains(plan.DegradeReasons, r => r.Contains("HDR", StringComparison.Ordinal));
    }

    [Fact]
    public void PreferredNoDegrade_SingleBackend()
    {
        var plan = CaptureBackendSelector.Select(new SelectorInput(true, false, false, CaptureBackend.Dxgi, false));
        Assert.Equal(new[] { CaptureBackend.Dxgi }, plan.Order);
        Assert.Empty(plan.DegradeReasons);
    }

    [Fact]
    public void PreferredWithDegrade_MovesPreferredToFront()
    {
        var plan = CaptureBackendSelector.Select(new SelectorInput(true, false, false, CaptureBackend.Dxgi, true));
        Assert.Equal(new[] { CaptureBackend.Dxgi, CaptureBackend.Wgc, CaptureBackend.BitBlt }, plan.Order);
    }
}
