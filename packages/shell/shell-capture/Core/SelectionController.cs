using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 选区状态机（纯逻辑，T2 同族可单测）：虚拟屏物理像素坐标。
/// 任意方向拖动都归一化为「左上→右下」矩形，并夹到虚拟屏边界。
/// </summary>
public sealed class SelectionController
{
    public PixelRect VirtualBounds { get; }

    public PixelRect? Current { get; private set; }

    public bool IsActive => Current is not null;

    /// <summary>选区是否达到最小尺寸（0 = 点击未拖动也算，用于「点击即全屏」判定）。</summary>
    public bool IsMeaningful => Current is { } r && r.Width >= 2 && r.Height >= 2;

    public SelectionController(PixelRect virtualBounds)
    {
        VirtualBounds = virtualBounds;
    }

    /// <summary>按下：记录锚点并初始化选区（物理像素）。</summary>
    public void Begin(System.Drawing.Point p)
    {
        _anchor = p;
        Current = Normalize(p, p);
    }

    /// <summary>拖动更新（相对锚点）。</summary>
    public void Update(System.Drawing.Point p)
    {
        if (Current is null)
        {
            return;
        }
        Current = Normalize(_anchor, p);
    }

    private System.Drawing.Point _anchor;

    /// <summary>结束拖动（保持当前选区）。</summary>
    public void End() { }

    public void Reset() => Current = null;

    private PixelRect Normalize(System.Drawing.Point a, System.Drawing.Point b)
    {
        int x1 = Math.Clamp(Math.Min(a.X, b.X), VirtualBounds.X, VirtualBounds.Right);
        int y1 = Math.Clamp(Math.Min(a.Y, b.Y), VirtualBounds.Y, VirtualBounds.Bottom);
        int x2 = Math.Clamp(Math.Max(a.X, b.X), VirtualBounds.X, VirtualBounds.Right);
        int y2 = Math.Clamp(Math.Max(a.Y, b.Y), VirtualBounds.Y, VirtualBounds.Bottom);
        return PixelRect.FromLTRB(x1, y1, x2, y2);
    }
}
