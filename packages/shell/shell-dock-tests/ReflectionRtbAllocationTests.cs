// BetterDesktop.Shell.Dock.Tests — H3 倒影 RTB 重建的 Gen2/分配画像测量（先测量后决定）。
// 设计方案：docs/design-proposals/2026-09-03-H3-倒影RTB测量与决策.md
// 结论门槛：一次全量 dock 重建（24 图标 @48px 默认档）RTB 分配 < 2MB 且零 Gen2 GC
// → 不引入倒影位图缓存（重建频率已被 250ms 视觉防抖约束），只修 DPD handler 泄漏。

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

public class ReflectionRtbAllocationTests
{
    [Fact]
    public void FullDockRebuild_RtbAllocation_UnderGate()
    {
        Exception? captured = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                const int iconsPerDock = 24; // 现实上界：pinned + running + system + flyout 预览
                const int iconSize = 48;     // 默认图标档

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var beforeAlloc = GC.GetTotalAllocatedBytes(true);
                var beforeGen2 = GC.CollectionCount(2);

                for (var i = 0; i < iconsPerDock; i++)
                {
                    _ = MakeFlippedRtb(MakeSrcBitmap(), iconSize, iconSize);
                }

                var allocBytes = GC.GetTotalAllocatedBytes(true) - beforeAlloc;
                var gen2Delta = GC.CollectionCount(2) - beforeGen2;

                Assert.True(allocBytes < 2 * 1024 * 1024,
                    $"一次全量重建 RTB 分配 {allocBytes / 1024.0:F0}KB 超 2MB 门槛——必须引入倒影位图缓存");
                Assert.Equal(0, gen2Delta);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    private static BitmapSource MakeSrcBitmap()
    {
        // 32×32 不透明源图（模拟真实图标像素源）
        var pixels = new int[32 * 32];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = unchecked((int)0xFF2070C0);
        }
        var src = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Pbgra32, null, pixels, 32 * 4);
        src.Freeze();
        return src;
    }

    /// <summary>与 DockWindow.BuildReflection 完全同构的 RTB 重建路径（翻转 + 按显示尺寸渲染）。</summary>
    private static RenderTargetBitmap MakeFlippedRtb(BitmapSource src, int w, int h)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var brush = new ImageBrush(src) { Stretch = Stretch.Uniform };
            ctx.PushTransform(new ScaleTransform(1, -1));
            ctx.DrawRectangle(brush, null, new Rect(0, -h, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
