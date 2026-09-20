using System;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>帧运算（裁剪/拷贝；纯 CPU，供后端帧→产物帧的零拷贝路径与裁剪）。</summary>
public static class FrameOps
{
    /// <summary>
    /// 从源帧裁剪出物理像素矩形。
    /// <para>若 <paramref name="crop"/> 与源帧矩形重合则返回源帧本身（所有权转移，调用方负责释放）；
    /// 否则分配新帧并逐行拷贝（裁剪矩形会被夹到源帧边界内）。</para>
    /// </summary>
    public static RawFrame Crop(RawFrame source, PixelRect crop)
    {
        var clamped = crop.Intersect(source.Rect);
        if (clamped.IsEmpty)
        {
            throw new ArgumentException($"裁剪矩形 {crop} 与源帧 {source.Rect} 无交集", nameof(crop));
        }

        if (clamped == source.Rect)
        {
            return source;
        }

        var target = RawFrame.Allocate(clamped, source.Format);
        int bpp = RawFrame.BytesPerPixel(source.Format);
        int srcOffsetX = clamped.X - source.Rect.X;
        int srcOffsetY = clamped.Y - source.Rect.Y;
        int rowBytes = clamped.Width * bpp;

        unsafe
        {
            for (int y = 0; y < clamped.Height; y++)
            {
                byte* src = (byte*)source.Pixels + (nint)(srcOffsetY + y) * source.Stride + (nint)srcOffsetX * bpp;
                byte* dst = (byte*)target.Pixels + (nint)y * target.Stride;
                new Span<byte>(src, rowBytes).CopyTo(new Span<byte>(dst, rowBytes));
            }
        }
        return target;
    }

    /// <summary>
    /// 把若干源帧按目标位置拷贝进目标帧（多显示器合成；越界部分自动裁剪）。
    /// 各源帧必须为 BGRA8（合成发生在色调映射之后）。
    /// </summary>
    public static void CompositeBgra(RawFrame target, RawFrame source, int destX, int destY)
    {
        int startX = Math.Max(0, destX);
        int startY = Math.Max(0, destY);
        int endX = Math.Min(target.Rect.Right, destX + source.Width);
        int endY = Math.Min(target.Rect.Bottom, destY + source.Height);
        if (endX <= startX || endY <= startY)
        {
            return;
        }

        int copyWidth = endX - startX;
        int rowBytes = copyWidth * 4;
        int srcStartX = startX - destX;
        int srcStartY = startY - destY;

        unsafe
        {
            for (int y = 0; y < endY - startY; y++)
            {
                byte* src = (byte*)source.Pixels + (nint)(srcStartY + y) * source.Stride + (nint)srcStartX * 4;
                byte* dst = (byte*)target.Pixels + (nint)(startY + y - target.Rect.Y) * target.Stride + (nint)(startX - target.Rect.X) * 4;
                new Span<byte>(src, rowBytes).CopyTo(new Span<byte>(dst, rowBytes));
            }
        }
    }
}
