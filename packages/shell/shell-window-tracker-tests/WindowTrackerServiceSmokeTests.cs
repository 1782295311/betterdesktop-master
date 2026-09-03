// BetterDesktop.Shell.WindowTracker.Tests — 构造冒烟（W3-2）。
// WindowTrackerService 构造路径：SynchronizationContext 捕获 + 防抖 Timer + WinEventHook 装配，
// 无 WPF Application 依赖，headless 可构造；再走一条异常安全的纯读路径。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.WindowTracker.Services;
using Xunit;

namespace BetterDesktop.Shell.WindowTracker.Tests;

public class WindowTrackerServiceSmokeTests
{
    private sealed class StubLogger : IKernelLogger
    {
        public void Log(LogLevel level, string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    [Fact]
    public void Ctor_WithNullAppSource_ConstructsAndDisposes()
    {
        var svc = new WindowTrackerService(appSource: null!, logger: new StubLogger());
        svc.Dispose();
    }

    [Fact]
    public void GetWindowTitle_ZeroHwnd_ReturnsEmpty_NotThrow()
    {
        using var svc = new WindowTrackerService(appSource: null!, logger: new StubLogger());
        Assert.Equal(string.Empty, svc.GetWindowTitle(IntPtr.Zero));
    }
}
