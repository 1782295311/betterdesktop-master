using System.IO.Pipes;
using System.Text;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>
/// core 控制客户端的行为测试。
/// </summary>
/// <remarks>
/// 用<b>真实命名管道</b>起一个测试用服务端，走完"写请求 → 读响应 → 解码"整条路径 ——
/// 而不是 mock 掉管道。理由：本类最容易出错的地方恰恰是**分帧与失败分类**，
/// 那两样都无法用 mock 验证。测试服务端在自己进程内创建，不需要 ACL。
/// </remarks>
public sealed class CoreControlClientTests
{
    /// <summary>唯一的测试管道名（避免与真 core 的管道互相干扰）。</summary>
    private static string TestPipe() => "BetterDesktop.TestControl-" + Guid.NewGuid().ToString("N");

    /// <summary>起一个"收到一行就回一行"的测试服务端。</summary>
    private static (string PipeName, Task<string?> Received, Task Server) StartServer(string reply)
    {
        var pipeName = TestPipe();
        var received = new TaskCompletionSource<string?>();

        var server = Task.Run(() =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
            server.WaitForConnection();
            using var reader = new StreamReader(server, Encoding.UTF8);
            var line = reader.ReadLine();
            received.TrySetResult(line);

            if (reply.Length == 0)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(reply + "\n");
            server.Write(payload, 0, payload.Length);
            server.Flush();
            // 给客户端一点时间读完再拆管道（否则可能读到半截）
            Thread.Sleep(200);
        });

        return (pipeName, received.Task, server);
    }

    [Fact(DisplayName = "服务端回 ok:true → 客户端成功，且发出去的确实是规范控制行")]
    public async Task OkResponseYieldsSuccess()
    {
        var (pipe, received, server) = StartServer(
            """{"ok":true,"verb":"status","data":{"uptime":7}}""");

        var result = CoreControlClient.Send("status", string.Empty, ensureCore: null, pipeName: pipe);

        Assert.True(result.Succeeded);
        Assert.Equal(ControlFailure.None, result.Failure);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(7, result.Response.Data.GetProperty("uptime").GetInt32());

        // ReadLine 会剥掉行终止符 —— 这里断言的是**规范控制行本体**（分帧由编解码器保证）
        var line = await received;
        Assert.Equal("BDMC1|@ctl|status|", line);
        await server;
    }

    [Fact(DisplayName = "服务端回 ok:false → 归类为 RemoteError（是业务错误，不是通信失败）")]
    public async Task RemoteErrorIsNotCommunicationFailure()
    {
        var (pipe, _, server) = StartServer(
            """{"ok":false,"verb":"start","error":"unknown-component","message":"component 'nope' not in components.json"}""");

        var result = CoreControlClient.Send("start", "nope", ensureCore: null, pipeName: pipe);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlFailure.RemoteError, result.Failure);
        Assert.Equal("unknown-component", result.Response.Error);
        await server;
    }

    [Fact(DisplayName = "远端业务错误**不得**触发 ensure core（否则会把「组件不存在」当 core 挂了去反复拉起）")]
    public async Task RemoteErrorDoesNotTriggerEnsure()
    {
        var (pipe, _, server) = StartServer(
            """{"ok":false,"verb":"start","error":"unknown-verb","message":"nope"}""");

        var ensureCalled = false;
        var result = CoreControlClient.Send("start", "x", () => { ensureCalled = true; return true; }, pipe);

        Assert.Equal(ControlFailure.RemoteError, result.Failure);
        Assert.False(ensureCalled, "ensure must not run for a remote business error");
        await server;
    }

    [Fact(DisplayName = "服务端回非 JSON → 归类为 Malformed（对端不是我们的 core）")]
    public async Task GarbageResponseIsMalformed()
    {
        var (pipe, _, server) = StartServer("hello from some other pipe server");

        var result = CoreControlClient.Send("status", string.Empty, ensureCore: null, pipeName: pipe);

        Assert.Equal(ControlFailure.Malformed, result.Failure);
        Assert.Contains("invalid json", result.FailureDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        await server;
    }

    [Fact(DisplayName = "管道不存在 → NotRunning；ensure 失败 → EnsureFailed 且只尝试一次")]
    public void MissingPipeAndFailedEnsure()
    {
        var attempts = 0;
        var result = CoreControlClient.Send("status", string.Empty, () => { attempts++; return false; }, TestPipe());

        Assert.False(result.Succeeded);
        Assert.Equal(ControlFailure.EnsureFailed, result.Failure);
        Assert.Equal(1, attempts);
        Assert.Equal(1, result.Attempts);
    }

    [Fact(DisplayName = "管道不存在且不提供 ensure → NotRunning，不去拉起任何进程")]
    public void MissingPipeWithoutEnsureIsNotRunning()
    {
        var result = CoreControlClient.Send("status", string.Empty, ensureCore: null, pipeName: TestPipe());

        Assert.False(result.Succeeded);
        Assert.Equal(ControlFailure.NotRunning, result.Failure);
        Assert.Equal(1, result.Attempts);
    }

    [Fact(DisplayName = "ensure 成功且 core 变得可连 → 重发真实请求并成功（attempts >= 2）")]
    public async Task EnsureThenRetrySucceeds()
    {
        var pipe = TestPipe();

        // ensure 委托负责"把服务端拉起来"——模拟 core 启动后建管道
        var result = CoreControlClient.Send(
            "status",
            string.Empty,
            ensureCore: () =>
            {
                _ = Task.Run(() =>
                {
                    using var server = new NamedPipeServerStream(
                        pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    reader.ReadLine();
                    var payload = Encoding.UTF8.GetBytes("""{"ok":true,"verb":"status","data":{}}""" + "\n");
                    server.Write(payload, 0, payload.Length);
                    server.Flush();
                    Thread.Sleep(200);
                });
                return true;
            },
            pipeName: pipe);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.True(result.Attempts >= 2, $"expected a retry, got {result.Attempts} attempt(s)");
    }
}
