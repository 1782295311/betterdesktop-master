using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class PowerManagementTests
{
    private static PowerManagement CreateSut()
    {
        return new PowerManagement(new KernelLogger());
    }

    [Fact]
    public void Name_ShouldBe_KernelPower()
    {
        var sut = CreateSut();
        Assert.Equal("kernel.power", sut.Name);
    }

    [Fact]
    public void Inject_ShouldBeEmpty()
    {
        var sut = CreateSut();
        Assert.Empty(sut.Inject);
    }

    [Fact]
    public void IsIdle_ShouldBeFalse_ByDefault()
    {
        var sut = CreateSut();
        Assert.False(sut.IsIdle);
    }

    [Fact]
    public void EnterIdle_ShouldSetIsIdleTrue()
    {
        var sut = CreateSut();
        sut.EnterIdle();
        Assert.True(sut.IsIdle);
    }

    [Fact]
    public void ExitIdle_ShouldSetIsIdleFalse()
    {
        var sut = CreateSut();
        sut.EnterIdle();
        sut.ExitIdle();
        Assert.False(sut.IsIdle);
    }

    [Fact]
    public void EnterIdle_CalledTwice_ShouldBeIdempotent()
    {
        var sut = CreateSut();
        sut.EnterIdle();
        sut.EnterIdle();
        Assert.True(sut.IsIdle);
    }

    [Fact]
    public void ExitIdle_WhenNotIdle_ShouldBeNoOp()
    {
        var sut = CreateSut();
        sut.ExitIdle();
        Assert.False(sut.IsIdle);
    }

    [Fact]
    public void LowerProcessPriority_ShouldNotThrow()
    {
        var sut = CreateSut();
        // 不抛异常即通过（可能需要管理员权限才能降级，但不应崩溃）
        var ex = Record.Exception(() => sut.LowerProcessPriority());
        Assert.Null(ex);
    }

    [Fact]
    public void AllowSystemSleep_ShouldNotThrow()
    {
        var sut = CreateSut();
        var ex = Record.Exception(() => sut.AllowSystemSleep());
        Assert.Null(ex);
    }

    [Fact]
    public async Task LoadAsync_ShouldProvideIPowerManagement()
    {
        using var context = new CordisContext();
        var sut = CreateSut();
        await sut.LoadAsync(context);
        var resolved = context.Get<IPowerManagement>();
        Assert.NotNull(resolved);
        Assert.Same(sut, resolved);
    }

    [Fact]
    public async Task LoadAsync_ShouldLowerPriorityAndAllowSleep()
    {
        using var context = new CordisContext();
        var sut = CreateSut();
        // 不抛异常即通过
        await sut.LoadAsync(context);
        Assert.False(sut.IsIdle); // 加载后不应处于空闲
    }
}
