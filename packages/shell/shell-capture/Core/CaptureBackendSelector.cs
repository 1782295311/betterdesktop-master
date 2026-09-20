using System;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>后端选择器的输入（纯值对象，便于单测构造）。</summary>
public sealed record SelectorInput(
    bool WgcAvailable,
    bool IsRemoteSession,
    bool AnyAdvancedColor,
    CaptureBackend? Preferred,
    bool AllowDegrade)
{
    public static SelectorInput Detect(bool wgcAvailable, CaptureBackend? preferred = null, bool allowDegrade = true)
    {
        var screen = VirtualScreenInfo.Query();
        return new SelectorInput(wgcAvailable, screen.IsRemoteSession, screen.AnyAdvancedColor, preferred, allowDegrade);
    }
}

/// <summary>后端计划：按序尝试的后端序列 + 每个被跳过/降级的可读原因（fail-visible）。</summary>
public sealed record BackendPlan(CaptureBackend[] Order, string[] DegradeReasons)
{
    public static BackendPlan Single(CaptureBackend backend) => new(new[] { backend }, Array.Empty<string>());
}

/// <summary>
/// 采集后端选择纯函数（T1 对象）：系统版本 / 远程会话 / HDR / 偏好 → 期望后端序列与降级原因。
/// 不探测运行时成败——运行时失败由降级链（ScreenCaptureService）捕获后并入结果。
/// </summary>
public static class CaptureBackendSelector
{
    /// <summary>Win11 22H2（22621）起 WGC 支持逐显示器非交互采集且可关黄框。</summary>
    public const int WgcMinBuild = 22621;

    public static BackendPlan Select(SelectorInput input)
    {
        if (input.Preferred is { } preferred && !input.AllowDegrade)
        {
            return BackendPlan.Single(preferred);
        }

        var order = new System.Collections.Generic.List<CaptureBackend>();
        var reasons = new System.Collections.Generic.List<string>();

        // WGC
        if (input.WgcAvailable)
        {
            order.Add(CaptureBackend.Wgc);
        }
        else
        {
            reasons.Add("WGC 不可用（系统版本低于 Win11 22H2），跳过");
        }

        // DXGI：远程会话（RDP/虚拟机）下 Desktop Duplication 常见 DXGI_ERROR_NOT_CURRENTLY_AVAILABLE。
        if (!input.IsRemoteSession)
        {
            order.Add(CaptureBackend.Dxgi);
        }
        else
        {
            reasons.Add("远程会话下 DXGI Desktop Duplication 不可靠，跳过");
        }

        // BitBlt：最后兜底；HDR 屏会失真，命中必须可见标注。
        order.Add(CaptureBackend.BitBlt);
        if (input.AnyAdvancedColor && order.Count == 1 && order[^1] == CaptureBackend.BitBlt)
        {
            reasons.Add("HDR 屏降级到 BitBlt，颜色将失真");
        }

        // 偏好后端（允许降级时）：把偏好排到最前。
        if (input.Preferred is { } p && input.AllowDegrade && order.Count > 1)
        {
            int idx = Array.IndexOf(order.ToArray(), p);
            if (idx > 0)
            {
                var arr = order.ToArray();
                (arr[0], arr[idx]) = (arr[idx], arr[0]);
                order = new System.Collections.Generic.List<CaptureBackend>(arr);
            }
        }

        return new BackendPlan(order.ToArray(), reasons.ToArray());
    }
}
