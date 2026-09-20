using BetterDesktop.Shell.MenuBar;
using BetterDesktop.Shell.Pinning.Contracts;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

/// <summary>
/// 【S2 · 2026-09-14】菜单栏插件的依赖声明回归。
/// <para>
/// 为什么值得一条测试：搜索面板的「固定到 Dock / 从 Dock 取消固定」曾通过**可空 <c>Get</c>** 采集
/// <see cref="IPinningService"/>，装配顺序变化（历史坑：dock 先于 context-menu 激活）会让它恒为 null，
/// 菜单项静默消失 —— 用户表现为「右键功能时有时无」。
/// 改为 <c>Inject</c> 声明后顺序由内核保证，缺失会显式停在 <c>Pending</c>，而不是少一个菜单项。
/// </para>
/// </summary>
public class MenuBarPluginDependencyTests
{
    [Fact]
    public void Inject_DeclaresPinningService()
    {
        var plugin = new MenuBarPlugin();

        Assert.Contains(typeof(IPinningService), plugin.Inject);
    }

    /// <summary>Inject 只能列**契约接口**：内核按类型解析服务，写实现类会让插件永远停在 Pending。</summary>
    [Fact]
    public void Inject_OnlyContainsInterfaces()
    {
        var plugin = new MenuBarPlugin();

        foreach (var dependency in plugin.Inject)
        {
            Assert.True(dependency.IsInterface, $"{dependency.FullName} 不是接口：内核按契约类型解析服务");
        }
    }
}
