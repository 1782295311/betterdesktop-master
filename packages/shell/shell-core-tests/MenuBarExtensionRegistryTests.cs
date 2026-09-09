// BetterDesktop.Shell.Core.Tests — 菜单栏扩展注册表单测（711 纪律）
// 覆盖：登记/解析/幂等替换/反注册/线程安全冒烟。装配容错（单扩展失败不拖垮宿主）属消费方职责，
// 由 MenuBarWindow/MenuBarPlugin 场景走查覆盖（DoD D4）。

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Services;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

public class MenuBarExtensionRegistryTests
{
    private sealed class StubExtension(string id) : IMenuBarExtension
    {
        public string Id { get; } = id;
        public FrameworkElement? GetVisual() => null;
        public void OpenPopup(Point anchor) { }
        public void ClosePopup() { }
    }

    [Fact]
    public void Register_Then_GetAll_ReturnsInRegistrationOrder()
    {
        var registry = new MenuBarExtensionRegistry();
        registry.Register(new StubExtension("a"));
        registry.Register(new StubExtension("b"));
        registry.Register(new StubExtension("c"));

        var all = registry.GetAll();
        Assert.Equal(new[] { "a", "b", "c" }, all.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Register_SameId_ReplacesOld()
    {
        var registry = new MenuBarExtensionRegistry();
        registry.Register(new StubExtension("a"));
        registry.Register(new StubExtension("a"));

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("a", all[0].Id);
    }

    [Fact]
    public void Get_ReturnsRegistered_OrNull()
    {
        var registry = new MenuBarExtensionRegistry();
        registry.Register(new StubExtension("ime"));

        Assert.NotNull(registry.Get("ime"));
        Assert.Null(registry.Get("missing"));
        Assert.Null(registry.Get(null!));
    }

    [Fact]
    public void Unregister_RemovesAndReports()
    {
        var registry = new MenuBarExtensionRegistry();
        registry.Register(new StubExtension("a"));

        Assert.True(registry.Unregister("a"));
        Assert.False(registry.Unregister("a")); // 已移除：再次移除返回 false
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task ConcurrentRegisterAndGet_IsSafe()
    {
        var registry = new MenuBarExtensionRegistry();
        var tasks = Enumerable.Range(0, 64)
            .Select(i => Task.Run(() =>
            {
                registry.Register(new StubExtension($"e{i}"));
                _ = registry.Get($"e{i}");
                _ = registry.GetAll();
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        // 全部成功注册（无异常/无丢项）
        Assert.Equal(64, registry.GetAll().Count);
    }
}
