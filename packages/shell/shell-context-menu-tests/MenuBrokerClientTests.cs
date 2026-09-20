// 进程外 broker 客户端单测：只测「协议往返 / 崩溃归因 / 降级决策」——真进程启动不属于单测范围，
// 故经 IMenuBrokerTransport 注入假实现（与 IndexIpcClient 的 transport 注入同款）。
// 真实链路由 native 侧 --self-test（build-shellmenu.ps1 会跑）与宿主实跑覆盖。
//
// 注意：HandlerCrashGuard / MenuBrokerClient 的测试缝都是**进程级静态状态**，凡会触碰它们的测试类
// （本类、HandlerCrashGuardTests、走 ShellExMenuPreview 的 MenuManagerServiceTests）必须同属一个
// xUnit Collection ——否则并行执行会互相改写静态目录与 transport 注入，产生假失败。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.ContextMenu.Tests.Support;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

[Collection("shellmenu-static-seams")]
public class MenuBrokerClientTests : IDisposable
{
    private const string ClsidA = "{33333333-3333-3333-3333-333333333333}";
    private const int TimeoutMs = 1000;

    private readonly TempFileScope _temp = new();

    public MenuBrokerClientTests()
    {
        HandlerCrashGuard.OverrideStoreRoot(Path.Combine(_temp.CreateDirectory(), "guard"));
        MenuBrokerClient.OverrideTransport(null);
    }

    public void Dispose()
    {
        MenuBrokerClient.OverrideTransport(null);
        HandlerCrashGuard.OverrideStoreRoot(null);
        _temp.Dispose();
    }

    [Fact]
    public void RespondedItemsAreMappedToVerbTree()
    {
        // broker 应答：一个普通项 + 一个带子项的子菜单 + 一个分隔线
        const string response = """
        {"ok":true,"count":3,"items":[
          {"text":"用记事本打开","verb":"open","sep":false,"sub":false,"enabled":true,"checked":false,"children":[]},
          {"text":"发送到","verb":"","sep":false,"sub":true,"enabled":true,"checked":false,"children":[
             {"text":"桌面快捷方式","verb":"sendto","sep":false,"sub":false,"enabled":true,"checked":false,"children":[]}]},
          {"text":"","verb":"","sep":true,"sub":false,"enabled":true,"checked":false,"children":[]}
        ]}
        """;
        var transport = new FakeTransport(_ => (true, response, 0, string.Empty));
        MenuBrokerClient.OverrideTransport(transport);

        var ok = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], background: false,
            extendedVerbs: false, handlerNames: null, TimeoutMs, out var items);

        Assert.True(ok);
        Assert.Equal(3, items.Count);
        Assert.Equal("用记事本打开", items[0].Text);
        Assert.Null(items[0].Invoke); // broker 项的 Invoke 留空：当前无生产消费方（见 MenuBrokerClient.MapItems）
        Assert.True(items[1].IsSubMenu);
        Assert.Single(items[1].Children);
        Assert.Equal("桌面快捷方式", items[1].Children[0].Text);
        Assert.True(items[2].IsSeparator);
        Assert.Equal(1, transport.Calls);
        Assert.Contains("\"op\":\"menu\"", transport.LastRequest, StringComparison.Ordinal);
        Assert.Contains(ClsidA, transport.LastRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerCrashIsAttributedAndNotRetriedInProcess()
    {
        // 非 0 退出 == 这个 CLSID 把 broker 干掉了（协议契约）。绝不能在宿主内重试它。
        var transport = new FakeTransport(_ => (true, string.Empty, unchecked((int)0xC0000005), string.Empty));
        MenuBrokerClient.OverrideTransport(transport);

        var ok = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], background: false,
            extendedVerbs: false, handlerNames: null, TimeoutMs, out var items);

        Assert.True(ok);          // 已有完整答案（"该扩展读不到"）——不能返回 false 去触发 in-proc 重试
        Assert.Equal(1, HandlerCrashGuard.ConsecutiveCount(ClsidA));

        // 降级必须可见：返回一条说明项，而不是空列表（空列表会被 UI 显示成"该扩展没有内容"）
        var warning = Assert.Single(items);
        Assert.Contains("崩溃", warning.Text, StringComparison.Ordinal);
        Assert.False(warning.IsSeparator);
    }

    [Fact]
    public void ThirdCrashTriggersAutoDisableAndResetsCounter()
    {
        var transport = new FakeTransport(_ => (true, string.Empty, unchecked((int)0xC0000005), string.Empty));
        MenuBrokerClient.OverrideTransport(transport);

        for (var i = 1; i <= 2; i++)
        {
            _ = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _);
            Assert.Equal(i, HandlerCrashGuard.ConsecutiveCount(ClsidA));
        }

        _ = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _);

        // 达阈值 → HandlerCrashBreaker 处置（本机没有这个假 CLSID 的注册项，故只走"清零"分支）
        Assert.Equal(0, HandlerCrashGuard.ConsecutiveCount(ClsidA));
        Assert.Empty(HandlerCrashGuard.Pending());
    }

    [Fact]
    public void SuccessClearsSuspectCounter()
    {
        var crash = new FakeTransport(_ => (true, string.Empty, unchecked((int)0xC0000005), string.Empty));
        MenuBrokerClient.OverrideTransport(crash);
        _ = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _);
        _ = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _);
        Assert.Equal(2, HandlerCrashGuard.ConsecutiveCount(ClsidA));

        // 一次正常应答 → 洗清嫌疑（只有"连续"崩溃才熔断）
        const string okResponse = """{"ok":true,"count":0,"items":[]}""";
        MenuBrokerClient.OverrideTransport(new FakeTransport(_ => (true, okResponse, 0, string.Empty)));
        _ = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _);
        Assert.Equal(0, HandlerCrashGuard.ConsecutiveCount(ClsidA));
    }

    [Fact]
    public void HandlerRefusalIsNotACrash()
    {
        // broker 正常应答但 handler 没产出（未注册 / 拒绝 Initialize）：跳过，不计账
        const string response = """{"ok":false,"error":"CoCreateInstance 失败","items":[]}""";
        MenuBrokerClient.OverrideTransport(new FakeTransport(_ => (true, response, 0, string.Empty)));

        var ok = MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out var items);

        Assert.True(ok);
        Assert.Empty(items);
        Assert.Equal(0, HandlerCrashGuard.ConsecutiveCount(ClsidA));
    }

    [Fact]
    public void UnavailableBrokerReportsFalseForVisibleDegradation()
    {
        var transport = new FakeTransport(_ => (false, string.Empty, 0, "broker 进程启动失败"));
        MenuBrokerClient.OverrideTransport(transport);

        // false = 调用方须给出可见降级文案（宿主内已无第二份实现可回退）。不做冷却：用户重试即重试。
        Assert.False(MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _));
        Assert.False(MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public void MalformedResponseReportsUnavailable()
    {
        MenuBrokerClient.OverrideTransport(new FakeTransport(_ => (true, "this is not json", 0, string.Empty)));

        Assert.False(MenuBrokerClient.TryQuery([ClsidA], ["C:\\a.txt"], false, false, null, TimeoutMs, out _));
    }

    [Fact]
    public void EmptyClsidListShortCircuitsWithoutSpawning()
    {
        var transport = new FakeTransport(_ => (true, "{}", 0, string.Empty));
        MenuBrokerClient.OverrideTransport(transport);

        var ok = MenuBrokerClient.TryQuery([], ["C:\\a.txt"], false, false, null, TimeoutMs, out var items);

        Assert.True(ok);
        Assert.Empty(items);
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>假 transport：调用即返回预置应答，并记录调用次数与最后一次请求（断言协议用）。</summary>
    private sealed class FakeTransport : IMenuBrokerTransport
    {
        private readonly Func<string, (bool IsResponse, string Response, int ExitCode, string Error)> _respond;

        public FakeTransport(Func<string, (bool, string, int, string)> respond) => _respond = respond;

        public int Calls { get; private set; }

        public string LastRequest { get; private set; } = string.Empty;

        public bool TryRun(string requestJson, int timeoutMs, out string responseJson, out int exitCode, out string error)
        {
            Calls++;
            LastRequest = requestJson;
            var (isResponse, response, code, message) = _respond(requestJson);
            responseJson = response;
            exitCode = code;
            error = message;
            return isResponse;
        }
    }
}
