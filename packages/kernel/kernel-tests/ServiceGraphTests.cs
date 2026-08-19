// BetterDesktop.Kernel.Tests — 服务图契约测试（ADR-002 D1）
// 契约一：父链解析 / 子覆盖遮蔽 / 未注册返回 null

using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class ServiceGraphTests
{
    [Fact(DisplayName = "未注册服务返回 null")]
    public void Get_Unregistered_ReturnsNull()
    {
        using var context = new CordisContext();
        Assert.Null(context.Get<IMarker>());
    }

    [Fact(DisplayName = "父上下文提供，子上下文可见")]
    public void Get_ParentProvided_VisibleInChild()
    {
        using var parent = new CordisContext();
        parent.Provide<IMarker>(new Marker("parent"));
        var child = (CordisContext)parent.Extend();
        Assert.Equal("parent", child.Get<IMarker>()!.Id);
    }

    [Fact(DisplayName = "子上下文覆盖，遮蔽父实现")]
    public void Get_ChildOverride_ShadowsParent()
    {
        using var parent = new CordisContext();
        parent.Provide<IMarker>(new Marker("parent"));
        var child = (CordisContext)parent.Extend();
        child.Provide<IMarker>(new Marker("child"));
        Assert.Equal("child", child.Get<IMarker>()!.Id);
        Assert.Equal("parent", parent.Get<IMarker>()!.Id);
    }

    [Fact(DisplayName = "撤销句柄移除服务后，解析回退父实现")]
    public void Provide_DisposeHandle_FallsBackToParent()
    {
        using var parent = new CordisContext();
        parent.Provide<IMarker>(new Marker("parent"));
        var child = (CordisContext)parent.Extend();
        var handle = child.Provide<IMarker>(new Marker("child"));
        Assert.Equal("child", child.Get<IMarker>()!.Id);

        handle.Dispose();
        Assert.Equal("parent", child.Get<IMarker>()!.Id);
    }

    public interface IMarker
    {
        string Id { get; }
    }

    private sealed class Marker : IMarker
    {
        public Marker(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }
}
