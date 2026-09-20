using System;
using System.Collections.Generic;
using System.Drawing;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Capture.Native;
using Xunit;

namespace BetterDesktop.Shell.Capture.Tests;

/// <summary>坐标/DPI 换算纯函数（计划 T2）：跨屏不同缩放的选区必须按显示器切片换算。</summary>
public sealed class DisplaySpaceTests
{
    // 显示器 A：主屏 100% 缩放，1920×1080 @(0,0)；显示器 B：副屏 150% 缩放，1280×1024 @(1920,0)。
    private static readonly List<MonitorEntry> Monitors = new()
    {
        new MonitorEntry
        {
            Handle = new IntPtr(0x10001),
            DeviceName = "TEST-A",
            Bounds = new PixelRect(0, 0, 1920, 1080),
            DpiX = 96,
            DpiY = 96,
            IsPrimary = true,
        },
        new MonitorEntry
        {
            Handle = new IntPtr(0x10002),
            DeviceName = "TEST-B",
            Bounds = new PixelRect(1920, 0, 1280, 1024),
            DpiX = 144,
            DpiY = 144,
            IsPrimary = false,
        },
    };

    private static void Near(double expected, double actual, double epsilon = 0.01)
        => Assert.True(Math.Abs(expected - actual) <= epsilon, $"expected {expected}, got {actual}");

    [Fact]
    public void MonitorDipBounds_UsesOwnDpi()
    {
        var a = DisplaySpace.MonitorDipBounds(Monitors[0]);
        Assert.Equal(new System.Windows.Rect(0, 0, 1920, 1080), a);

        var b = DisplaySpace.MonitorDipBounds(Monitors[1]);
        Near(1920.0 / 1.5, b.Left);
        Near(1280.0 / 1.5, b.Width);
    }

    [Fact]
    public void MonitorAtPhysical_PicksCorrectMonitor()
    {
        Assert.Equal(Monitors[0], DisplaySpace.MonitorAtPhysical(100, 100, Monitors));
        Assert.Equal(Monitors[1], DisplaySpace.MonitorAtPhysical(2000, 100, Monitors));
        // 负坐标/屏外 → 回退第一个
        Assert.Equal(Monitors[0], DisplaySpace.MonitorAtPhysical(-50, -50, Monitors));
    }

    // 注：跨屏「物理↔DIP 整矩形」换算在混合 DPI 下无单一 DIP 坐标系（数学上无定义），
    // 已于实现中移除（DoD 偏离项，原因见核销表）；生产路径均为单显示器内各自 DPI 换算。
}

/// <summary>选区状态机（计划 T2 同族）：任意方向拖动归一化 + 夹取虚拟屏边界。</summary>
public sealed class SelectionControllerTests
{
    private static readonly PixelRect Virtual = new(0, 0, 2560, 1440);

    [Fact]
    public void DragTopLeftToBottomRight_Normalizes()
    {
        var sel = new SelectionController(Virtual);
        sel.Begin(new Point(100, 100));
        sel.Update(new Point(400, 300));
        Assert.Equal(new PixelRect(100, 100, 300, 200), sel.Current);
        Assert.True(sel.IsMeaningful);
    }

    [Fact]
    public void DragBottomRightToTopLeft_NormalizesToTopLeftOrigin()
    {
        var sel = new SelectionController(Virtual);
        sel.Begin(new Point(500, 400));
        sel.Update(new Point(200, 150));
        Assert.Equal(new PixelRect(200, 150, 300, 250), sel.Current);
    }

    [Fact]
    public void DragBeyondVirtualBounds_Clamps()
    {
        var sel = new SelectionController(Virtual);
        sel.Begin(new Point(-100, -100));
        sel.Update(new Point(3000, 2000));
        Assert.Equal(Virtual, sel.Current);
    }

    [Fact]
    public void ClickWithoutDrag_NotMeaningful()
    {
        var sel = new SelectionController(Virtual);
        sel.Begin(new Point(100, 100));
        sel.Update(new Point(101, 101));
        Assert.False(sel.IsMeaningful);
    }

    [Fact]
    public void Reset_ClearsSelection()
    {
        var sel = new SelectionController(Virtual);
        sel.Begin(new Point(0, 0));
        sel.Update(new Point(10, 10));
        sel.Reset();
        Assert.Null(sel.Current);
        Assert.False(sel.IsActive);
    }
}

/// <summary>PixelRect 核心语义（T2 共用）。</summary>
public sealed class PixelRectTests
{
    [Fact]
    public void Intersect_EmptyWhenDisjoint()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(200, 200, 50, 50);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Intersect_Partial()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 200, 200);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(b));
    }

    [Fact]
    public void NegativeCoordinates_Supported()
    {
        var r = new PixelRect(-1920, -100, 1920, 1080);
        Assert.Equal(0, r.Right);
        Assert.False(r.Contains(1, 0));
        Assert.True(r.Contains(-100, 0));
    }
}
