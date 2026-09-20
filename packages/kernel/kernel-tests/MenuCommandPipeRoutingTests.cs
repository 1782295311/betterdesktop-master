using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>
/// 管道**路由**契约（2026-09-19，计划 §13.17 + 向量 `_routing`）。
///
/// <para>
/// 背景：`@ctl` 与 legacy 两种形态**曾经同名**（都发往 `BetterDesktop.MenuCmd`），而两侧动词集不相交
/// → 客户端连上谁不确定 → 每个入口调用都是掷硬币；更糟的是 legacy 落到 core 时
/// <c>TrySend</c> 照样返回 <c>true</c> → 调用方以为宿主已处理、**连回退都被短路**。
/// 现在按 head 段分流，两条路各自确定 —— 本文件钉的就是这条分流。
/// </para>
/// </summary>
public sealed class MenuCommandPipeRoutingTests
{
    /// <summary>`@ctl` 形态 → **core** 的控制管道。</summary>
    [Theory]
    [InlineData("@ctl|status")]
    [InlineData("@ctl")]
    [InlineData("@ctl|start|desktop")]
    [InlineData("@ctl|set|k a|b")]
    public void ControlShapeGoesToCore(string action) =>
        Assert.Equal(MenuCommandPipeClient.PipeName, MenuCommandPipeClient.PipeNameFor(action));

    /// <summary>legacy 形态 → **Host** 的管道。</summary>
    [Theory]
    [InlineData("open-settings")]
    [InlineData("notify-error")]
    [InlineData("toggle-key")]
    [InlineData("toggle-desktop")]
    [InlineData("clipboard-history")]
    [InlineData("convert")]
    [InlineData("dock-pin")]
    // 【2026-09-19 真机补钉】`paste-session` 原先**不在**这张表里 —— 而真机上它恰恰是唯一
    // 被观测到落错服务端的动词（core 日志 6 次 `routing error: legacy … paste-session`）。
    // 原因不是路由写错（它是 legacy、本就走 Host ✓），而是**面板加载了旧 Kernel 副本**；
    // 但"没被钉住的用例漏了"这条教训成立：路由表里出现的每个 legacy 动词都该被列在这里。
    [InlineData("paste-session")]
    [InlineData("ctl|status")] // 邻接：缺 `@` → 不得被当成控制形态
    [InlineData("")]           // 空动作：不得抛；归 legacy（Host 会按未知命令告警）
    public void LegacyShapeGoesToTheHost(string action) =>
        Assert.Equal(MenuCommandPipeClient.HostPipeName, MenuCommandPipeClient.PipeNameFor(action));

    /// <summary>
    /// **前缀匹配**的边界：`@ctl` 开头的任何东西都归 core —— 包括 `@ctl-ish` 这种"不是动词"的输入。
    ///
    /// 这条测试的用途不是"证明它能路由"，而是**说明契约里那条约束为什么存在**：
    /// 「legacy 动词集不得含 `@` 开头的动词」—— 因为一旦有，按 head 判据就会被误路由到 core。
    /// </summary>
    [Theory]
    [InlineData("@ctl-ish")]
    [InlineData("@ctlx")]
    public void AnythingStartingWithTheControlHeadGoesToCore(string action) =>
        Assert.Equal(MenuCommandPipeClient.PipeName, MenuCommandPipeClient.PipeNameFor(action));

    /// <summary>两个名字必须**不同** —— 相同就等于把掷硬币原样搬回来。</summary>
    [Fact]
    public void TheTwoPipeNamesMustDiffer() =>
        Assert.NotEqual(MenuCommandPipeClient.PipeName, MenuCommandPipeClient.HostPipeName);

    /// <summary>
    /// 契约字面量（**跨进程跨语言**：Rust core 的 `PIPE_NAME`、Host 的 `PipeName`、向量 `_routing` 必须一致）。
    /// 改名字时若只改一处，这条会红 —— 这正是它存在的理由。
    /// </summary>
    [Fact]
    public void PipeNamesMatchTheSharedContract()
    {
        Assert.Equal("BetterDesktop.MenuCmd", MenuCommandPipeClient.PipeName);
        Assert.Equal("BetterDesktop.HostCmd", MenuCommandPipeClient.HostPipeName);
    }
}
