// BetterDesktop.Shell.MenuBar.Tests — SoundPanelViewModel.AggregateSessions 聚合测试。
// 背景：原生 Audio_EnumerateSessions 平铺返回全部会话（同一 App 多窗口=多行，103 文档红线禁止），
// UI 层按进程聚合为一行；无进程会话（系统声音）归并为一行。

using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Status.Native;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

public class AggregateSessionsTests
{
    private static AudioSessionNative Session(string name, float volume, bool muted, int pid)
        => new(name, volume, muted, pid);

    [Fact]
    public void SamePid_MultipleSessions_CollapseToOneRow()
    {
        var raw = new List<AudioSessionNative>
        {
            Session("Edge", 0.5f, false, 100),
            Session("Edge", 0.8f, true, 100),   // 同一进程第二个会话
            Session("Spotify", 0.6f, false, 200)
        };

        var result = SoundPanelViewModel.AggregateSessions(raw);

        Assert.Equal(2, result.Count);
        // 保留首个会话的值（显示口径），设置侧按 PID 覆盖全会话
        Assert.Equal(100, result[0].ProcessId);
        Assert.Equal(0.5f, result[0].VolumeFloat);
        Assert.False(result[0].IsMuted);
        Assert.Equal(200, result[1].ProcessId);
    }

    [Fact]
    public void NoProcessSessions_CollapseIntoSingleSystemRow()
    {
        var raw = new List<AudioSessionNative>
        {
            Session("System Sounds", 0.7f, false, 0),
            Session("", 0.3f, true, -1)          // 另一个无进程会话
        };

        var result = SoundPanelViewModel.AggregateSessions(raw);

        var row = Assert.Single(result);
        Assert.Equal(0, row.ProcessId);
        Assert.Equal("System Sounds", row.Name); // 保留首个非空名称
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SoundPanelViewModel.AggregateSessions(Array.Empty<AudioSessionNative>()));
        Assert.Empty(SoundPanelViewModel.AggregateSessions(null!));
    }

    [Fact]
    public void Mixed_AppRowsThenSystemRow()
    {
        var raw = new List<AudioSessionNative>
        {
            Session("A", 1f, false, 1),
            Session("B", 0.2f, true, 2),
            Session("System", 0.5f, false, 0)
        };

        var result = SoundPanelViewModel.AggregateSessions(raw);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { 1, 2, 0 }, result.Select(r => r.ProcessId).ToArray());
    }

    [Fact]
    public void DuplicateProcessIds_KeepFirstSeenRow()
    {
        var raw = new List<AudioSessionNative>
        {
            Session("", 0.2f, false, 42),        // 首会话名为空
            Session("Chrome", 0.9f, true, 42)    // 后续会话有名字
        };

        var result = SoundPanelViewModel.AggregateSessions(raw);

        var row = Assert.Single(result);
        Assert.Equal(42, row.ProcessId);
        // 聚合行显示名由 UI 层 ProcessAppInfo.GetName 兜底（进程名），会话名仅作回退，保留首个即可
        Assert.Equal(0.2f, row.VolumeFloat);
    }
}
