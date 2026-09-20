using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>
/// <see cref="CoreComponents"/> 的解析契约测试。
/// </summary>
/// <remarks>
/// 只测**纯函数**部分（把 core 的 status JSON 解成"某组件在不在跑"）—— 这一层是判活的判据本体，
/// 出错的表现极隐蔽：解错时会返回 <c>false</c>，而 <c>false</c> 在调用方看来是"没在跑"，
/// 于是面板会去重启一个本来在跑的引擎。故每个"形状不符"的分支都要有用例钉住。
/// 真实管道往返不在这里测（那属真机验收；单测不依赖真机进程状态）。
/// </remarks>
public sealed class CoreComponentsTests
{
    /// <summary>用真实响应文本造 <see cref="ControlResult"/>，避免手搓 JsonElement。</summary>
    private static ControlResult Result(string json)
    {
        Assert.True(MenuCommandPipeCodec.TryDecodeResponse(json, out var response, out var failure),
            $"夹具本身不合法：{failure}");
        return new ControlResult(response.Ok, ControlFailure.None, response, null, 1);
    }

    private const string StatusOk = """
{"ok":true,"verb":"status","data":{"desired":{"shell":"running","index-engine":"on-demand"},"actual":{"shell":false,"index-engine":true},"state":{"shell":"stopped","index-engine":"alive"},"health":{"shell":"degraded","index-engine":"ok"},"restarts":{"shell":0,"index-engine":0},"uptime":42}}
""";

    [Fact]
    public void ReadsActualFlagForComponent()
    {
        Assert.True(CoreComponents.TryReadRunning(Result(StatusOk), "index-engine", out var engine));
        Assert.True(engine);

        Assert.True(CoreComponents.TryReadRunning(Result(StatusOk), "shell", out var shell));
        Assert.False(shell);
    }

    /// <summary>组件不在 actual 里 → 返回 false 且 <c>running=false</c>（**不猜**，也不抛）。</summary>
    [Fact]
    public void UnknownComponentReturnsFalseNotGuess()
    {
        Assert.False(CoreComponents.TryReadRunning(Result(StatusOk), "no-such-component", out var running));
        Assert.False(running);
    }

    /// <summary>失败的请求（没拿到 ok:true）一律读不出来 —— 不能把"没问到"读成"没在跑"。</summary>
    [Fact]
    public void FailedResultYieldsFalse()
    {
        var failed = new ControlResult(false, ControlFailure.NotRunning, default, "core not running", 1);
        Assert.False(CoreComponents.TryReadRunning(failed, "index-engine", out var running));
        Assert.False(running, "读不出来时必须是 false 且由调用方走降级路径，不编造状态");
    }

    /// <summary>data 形状不对（不是对象 / actual 不是对象 / 值不是布尔）→ 全部返回 false，不得抛。</summary>
    [Theory]
    [InlineData("""{"ok":true,"verb":"status","data":"not-an-object"}""")]
    [InlineData("""{"ok":true,"verb":"status","data":{"actual":"not-an-object"}}""")]
    [InlineData("""{"ok":true,"verb":"status","data":{"actual":{"shell":"running"}}}""")]
    [InlineData("""{"ok":true,"verb":"status","data":{}}""")]
    public void MalformedShapesReturnFalseWithoutThrowing(string json)
    {
        Assert.False(CoreComponents.TryReadRunning(Result(json), "shell", out var running));
        Assert.False(running);
    }

    /// <summary>
    /// 【契约钉子】常量集与 core/components.json 的 name 集合必须一致 ——
    /// 由门禁 verify-boundaries 双向校验，这里再钉一次是为了让"改 component 名"这件事
    /// 在**最近的地方**（改代码的人跑单测时）就炸，而不是等门禁。
    /// </summary>
    [Fact]
    public void ComponentNamesCoverTheTable()
    {
        Assert.Equal(new[]
        {
            "shell", "desktop", "desktop-controls", "clipboard-engine", "clipboard-panel",
            "clipboard-panel-open", "index-engine", "settings", "capture",
        }, CoreComponents.All);

        Assert.Equal("clipboard-panel-open", CoreComponents.ClipboardPanelOpen);
        Assert.Equal("desktop-controls", CoreComponents.DesktopControls);
    }
}
