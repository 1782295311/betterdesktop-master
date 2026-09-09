// BetterDesktop.Shell.Status.Tests — 语义层输出测试（内存/电量/音量必测，另覆麦克风/网络/输入法）
// 验收点：把裸系统值翻译为用户可读的语言与严重级别。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;
using BetterDesktop.Shell.Status.Services;
using Xunit;

namespace BetterDesktop.Shell.Status.Tests;

/// <summary>内存监控语义测试。</summary>
public class MemoryMonitorTests
{
    [Fact]
    public void NormalUsage_IsNormalSeverity()
    {
        var source = new FakeSystemSource { Memory = FakeSystemSource.MakeMemory(50) };
        var snap = new MemoryMonitor(source).GetSnapshot();

        Assert.Equal("50%", snap.ShortText);
        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Equal(50, snap.Progress);
        Assert.Contains("50%", snap.HumanText);
    }

    [Theory]
    [InlineData(72, StatusSeverity.Warning, "建议关闭部分后台程序")]
    [InlineData(95, StatusSeverity.Critical, "内存已近饱和")]
    public void HighUsage_ReflectsSeverityAndHint(byte load, StatusSeverity expected, string hint)
    {
        var source = new FakeSystemSource { Memory = FakeSystemSource.MakeMemory(load) };
        var snap = new MemoryMonitor(source).GetSnapshot();

        Assert.Equal(expected, snap.Severity);
        Assert.Contains(hint, snap.HumanText);
    }

    [Fact]
    public void ReadFailure_DegradesToInfo()
    {
        var source = new FakeSystemSource { Memory = null };
        var snap = new MemoryMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Info, snap.Severity);
        Assert.Contains("读取失败", snap.HumanText);
    }
}

/// <summary>电池/电源监控语义测试。</summary>
public class BatteryMonitorTests
{
    [Fact]
    public void NoBattery_Hidden()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(0, 128, 100) };
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Contains("未检测到电池", snap.HumanText);
    }

    [Fact]
    public void Charging_ShowsChargingState()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(1, 8, 65) }; // AC + 充电标志
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Contains("正在充电，65%", snap.HumanText);
        Assert.Equal(StatusSeverity.Info, snap.Severity);
    }

    [Fact]
    public void OnBattery_RemainingTimeIsHuman()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(0, 2, 60, 4800) }; // 80 分钟
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Contains("剩余电量 60%", snap.HumanText);
        Assert.Contains("1 小时 20 分", snap.HumanText);
    }

    [Fact]
    public void LowBattery_Warning()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(0, 0, 15) };
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Warning, snap.Severity);
        Assert.Contains("尽快充电", snap.HumanText);
    }

    [Fact]
    public void CriticalBattery_CriticalSeverity()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(0, 0, 5) };
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Critical, snap.Severity);
        Assert.Contains("立即接通电源", snap.HumanText);
    }

    [Fact]
    public void UnknownRemainingTime_ShowsUnknown()
    {
        var source = new FakeSystemSource { Power = FakeSystemSource.MakePower(0, 0, 40, 0xFFFFFFFF) };
        var snap = new BatteryMonitor(source).GetSnapshot();

        Assert.Contains("剩余时长未知", snap.HumanText);
    }

    [Fact]
    public void ReadFailure_Info()
    {
        var source = new FakeSystemSource { Power = null };
        Assert.Equal(StatusSeverity.Info, new BatteryMonitor(source).GetSnapshot().Severity);
    }
}

/// <summary>音量监控语义测试。</summary>
public class VolumeMonitorTests
{
    [Fact]
    public void Muted_Warning()
    {
        var source = new FakeSystemSource { Volume = new AudioEndpointStatus(0.5f, true, true) };
        var snap = new VolumeMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Warning, snap.Severity);
        Assert.Contains("已静音", snap.HumanText);
        Assert.Contains("已静音", snap.ShortText);
    }

    [Fact]
    public void Normal_ShowsPercent()
    {
        var source = new FakeSystemSource { Volume = new AudioEndpointStatus(0.45f, false, true) };
        var snap = new VolumeMonitor(source).GetSnapshot();

        Assert.Equal("45%", snap.ShortText);
        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Equal(45, snap.Progress);
    }

    [Fact]
    public void NoDevice_Info()
    {
        var source = new FakeSystemSource { Volume = new AudioEndpointStatus(0, false, false) };
        Assert.Equal(StatusSeverity.Info, new VolumeMonitor(source).GetSnapshot().Severity);
    }
}

/// <summary>麦克风监控语义测试。</summary>
public class MicrophoneMonitorTests
{
    [Fact]
    public void Muted_Warning()
    {
        var source = new FakeSystemSource { Microphone = new AudioEndpointStatus(0, true, true) };
        Assert.Equal(StatusSeverity.Warning, new MicrophoneMonitor(source).GetSnapshot().Severity);
    }

    [Fact]
    public void Active_Normal()
    {
        var source = new FakeSystemSource { Microphone = new AudioEndpointStatus(0, false, true) };
        var snap = new MicrophoneMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Contains("正常", snap.HumanText);
    }
}

/// <summary>网络监控语义测试。</summary>
public class NetworkMonitorTests
{
    [Fact]
    public void Offline_Warning()
    {
        var source = new FakeSystemSource { HasConnection = false };
        var snap = new NetworkMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Warning, snap.Severity);
        Assert.Contains("未连接到网络", snap.HumanText);
    }

    [Fact]
    public void WifiConnected_Normal()
    {
        var source = new FakeSystemSource
        {
            HasConnection = true,
            WirelessAdapters = new[] { new WirelessAdapterStatus("Wi-Fi 6E", "Connected", true) }
        };
        var snap = new NetworkMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Contains("已连接 Wi-Fi", snap.HumanText);
    }

    [Fact]
    public void WiredOnly_Ethernet()
    {
        var source = new FakeSystemSource { HasConnection = true, WirelessAdapters = Array.Empty<WirelessAdapterStatus>() };
        var snap = new NetworkMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Contains("有线", snap.HumanText);
    }
}

/// <summary>输入法监控语义测试。</summary>
public class ImeMonitorTests
{
    [Fact]
    public void ChineseLayout_ShowsShortName()
    {
        // 注册布局注入 Fake（空枚举 → 走 KLID 兜底路径），保证单测不依赖真实机器输入法/注册表状态。
        var source = new FakeSystemSource
        {
            LayoutId = "00000804",
            RegisteredLayouts = new[] { new KeyboardLayoutItem("00000804", "中文(简体，中国)", "kbdus.dll", IsIme: false, IsActive: false) }
        };
        var snap = new ImeMonitor(source).GetSnapshot();

        Assert.Equal(StatusSeverity.Normal, snap.Severity);
        Assert.Contains("当前布局：中文", snap.HumanText);
    }

    [Fact]
    public void Unknown_Info()
    {
        var source = new FakeSystemSource { LayoutId = string.Empty };
        Assert.Equal(StatusSeverity.Info, new ImeMonitor(source).GetSnapshot().Severity);
    }
}
