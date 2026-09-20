using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace BetterDesktop.Kernel.Core;

/// <summary>控制请求的失败分类。**分类必须精确**：不同的失败要触发不同的补救动作。</summary>
public enum ControlFailure
{
    /// <summary>成功。</summary>
    None,

    /// <summary>连不上（core 没在跑，或刚启动还没建管道）→ **值得 ensure core + 重试一次**。</summary>
    NotRunning,

    /// <summary>被拒绝（ACL 或调用者校验）→ **不得重试**：重试只会重复被拒，还会掩盖真问题。</summary>
    AccessDenied,

    /// <summary>连上了但服务端超时未回包 → 可重试一次（core 可能正在忙）。</summary>
    Timeout,

    /// <summary>回了包但不是合法响应 JSON → 说明对端不是我们的 core（或版本不匹配）。</summary>
    Malformed,

    /// <summary>ensure core 之后仍然连不上。</summary>
    EnsureFailed,

    /// <summary>响应本身是结构化错误（<c>ok:false</c>）——注意这**不是**通信失败。</summary>
    RemoteError,
}

/// <summary>控制请求结果。</summary>
public readonly struct ControlResult
{
    /// <summary>构造。</summary>
    public ControlResult(
        bool succeeded,
        ControlFailure failure,
        MenuCommandPipeCodec.Response response,
        string? failureDetail,
        int attempts)
    {
        Succeeded = succeeded;
        Failure = failure;
        Response = response;
        FailureDetail = failureDetail;
        Attempts = attempts;
    }

    /// <summary>是否拿到 <c>ok:true</c> 的响应。</summary>
    public bool Succeeded { get; }

    /// <summary>失败分类（成功时 <see cref="ControlFailure.None"/>）。</summary>
    public ControlFailure Failure { get; }

    /// <summary>已解码的响应（未拿到时为 default）。</summary>
    public MenuCommandPipeCodec.Response Response { get; }

    /// <summary>失败细节（供日志；**面向开发者，不面向用户**）。</summary>
    public string? FailureDetail { get; }

    /// <summary>实际尝试次数（1 = 未触发重试）。</summary>
    public int Attempts { get; }
}

/// <summary>
/// BDMC1 控制管道客户端（core 的 C# 侧入口）：发一条 control 请求、读一条 JSON 响应。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="MenuCommandPipeClient"/> 的分工：那个是 <b>legacy</b>（写后即忘，转发给宿主），
/// 本类是 <b>control</b>（请求/应答，指 core）。两者共用 <see cref="MenuCommandPipeCodec"/> 的编解码。
/// </para>
/// <para>
/// <b>ensure core 由调用方注入</b>（<c>ensureCore</c> 委托），而不是在这里 <c>Process.Start</c>：
/// kernel 包是零依赖的契约层，让它去拉起进程既违反分层、也会让"谁有权拉起进程"这条边界
/// 从门禁视野里消失（门禁按文件判定，委托注入让拉起点留在被登记的那一层）。
/// </para>
/// <para>
/// <b>关于"刚从睡眠醒来"的区分</b>（计划 §6.3 要求客户端区分"core 没跑"与"刚唤醒"）：
/// 一次性客户端（CLI / 右键降级路径）**无法**知道这一点 —— 它自身也是刚被拉起的，
/// 没有任何历史状态可比较。该区分只对<b>长驻客户端</b>（壳 / 面板）有意义，将在 S4 随它们落地。
/// 这里如实标注为未覆盖，而不是假装实现了。
/// </para>
/// </remarks>
public static class CoreControlClient
{
    /// <summary>首次连接超时。比 legacy 的 1500ms 短：连本机管道不需要那么久，快速失败才能快速补救。</summary>
    public const int ConnectTimeoutMs = 600;

    /// <summary>等待响应的上限（core 的 `status` 会枚举进程，给它余量）。</summary>
    public const int ResponseTimeoutMs = 3000;

    /// <summary>ensure core 之后等待 core 变得可连的上限。</summary>
    public const int EnsureWaitMs = 5000;

    /// <summary>补救阶段的重试间隔。</summary>
    public const int RetryIntervalMs = 150;

    /// <summary>
    /// 发一条控制请求，失败时按分类决定是否补救，**最多重试一次**。
    /// </summary>
    /// <param name="verb">命令词（status / start / stop / toggle / get …）。</param>
    /// <param name="arg">参数（可为空串；组件名 / 配置键）。</param>
    /// <param name="ensureCore">
    /// 补救动作：拉起 core。返回 false 表示找不到或拉不起来（调用方已记日志）。
    /// 传 null 表示调用方不愿/不能拉起 core（例如只读探活场景）。
    /// </param>
    /// <param name="pipeName">管道名；<c>null</c> = 用契约名。**仅供单测注入**（起一个测试用服务端）。</param>
    public static ControlResult Send(string verb, string arg, Func<bool>? ensureCore, string? pipeName = null)
    {
        var target = pipeName ?? MenuCommandPipeClient.PipeName;
        var first = Attempt(verb, arg, target);

        // 只有"连不上"和"回包超时"值得补救。AccessDenied / Malformed 重试只是浪费一次超时。
        var retryable = first.Failure is ControlFailure.NotRunning or ControlFailure.Timeout;
        if (first.Failure == ControlFailure.None || first.Response.Ok || !retryable)
        {
            return FinishFirst(first);
        }

        if (ensureCore is null)
        {
            return new ControlResult(false, first.Failure, first.Response, first.Detail, 1);
        }

        if (!ensureCore())
        {
            return new ControlResult(
                false,
                ControlFailure.EnsureFailed,
                first.Response,
                $"{first.Detail}; ensure core failed (core executable not found or launch failed)",
                1);
        }

        // 【为什么是"到期循环重发真实请求"而不是"先探测管道就绪、再重试一次"】
        // 判断管道是否就绪的唯一手段就是**连一下**，而连一下会被服务端当成一个真实客户端接走：
        // core 的实例线程会为这次探测走完整的"校验 + 读超时"流程，白占一个实例、还刷一条
        // 误导性的连接日志（且探测方立刻关闭会让它记一条 read timeout）。
        // 所以直接重发真实请求：成功即止，到期即败。这样探测的副作用为零。
        var elapsed = Stopwatch.StartNew();
        var attempts = 1;
        var lastFailure = first.Failure;
        var lastDetail = first.Detail;

        while (elapsed.ElapsedMilliseconds < EnsureWaitMs)
        {
            System.Threading.Thread.Sleep(RetryIntervalMs);
            attempts++;

            var next = Attempt(verb, arg, target);
            if (next.Failure == ControlFailure.None)
            {
                return new ControlResult(next.Response.Ok, ControlFailure.None, next.Response, next.Detail, attempts);
            }

            // 被拒 / 畸形：与"core 还没起来"无关，继续等只是浪费 —— 立刻停手并如实上报。
            if (next.Failure is ControlFailure.AccessDenied or ControlFailure.Malformed)
            {
                return new ControlResult(false, next.Failure, next.Response, next.Detail, attempts);
            }

            lastFailure = next.Failure;
            lastDetail = next.Detail;
        }

        return new ControlResult(
            false,
            ControlFailure.EnsureFailed,
            first.Response,
            $"{lastDetail}; core was launched but did not become reachable within {EnsureWaitMs}ms ({attempts} attempts, last={lastFailure})",
            attempts);
    }

    private static ControlResult FinishFirst(AttemptResult first) =>
        new(first.Failure == ControlFailure.None && first.Response.Ok, first.Failure, first.Response, first.Detail, 1);

    private readonly struct AttemptResult
    {
        public AttemptResult(ControlFailure failure, MenuCommandPipeCodec.Response response, string? detail)
        {
            Failure = failure;
            Response = response;
            Detail = detail;
        }

        public ControlFailure Failure { get; }

        public MenuCommandPipeCodec.Response Response { get; }

        public string? Detail { get; }
    }

    /// <summary>单次尝试：连接 → 写一行 → 读一行 → 解码。</summary>
    private static AttemptResult Attempt(string verb, string arg, string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(ConnectTimeoutMs);

            // 直接写字节：编解码器已产出**含 LF 结尾**的规范行，不经 StreamWriter。
            // 好处是分帧完全由契约（共享向量）决定，不受 StreamWriter 换行约定/编码 BOM 影响。
            var payload = Encoding.UTF8.GetBytes(MenuCommandPipeCodec.EncodeControl(verb, arg));
            client.Write(payload, 0, payload.Length);
            client.Flush();

            using var reader = new StreamReader(client, Encoding.UTF8);
            var line = ReadLineWithTimeout(reader, ResponseTimeoutMs);
            if (line is null)
            {
                return new AttemptResult(ControlFailure.Timeout, default, "no response within timeout");
            }

            if (!MenuCommandPipeCodec.TryDecodeResponse(line, out var response, out var failure))
            {
                return new AttemptResult(ControlFailure.Malformed, default, $"{failure}; raw={Truncate(line)}");
            }

            // 响应正确解码但 ok=false：这是**远端业务错误**，不是通信失败 —— 分类必须分清，
            // 否则调用方会把"组件不存在"当成"core 挂了"去反复拉起 core。
            return new AttemptResult(
                response.Ok ? ControlFailure.None : ControlFailure.RemoteError,
                response,
                response.Ok ? null : $"{response.Error}: {response.Message}");
        }
        catch (TimeoutException)
        {
            return new AttemptResult(ControlFailure.NotRunning, default, "connect timeout (core not running?)");
        }
        catch (UnauthorizedAccessException ex)
        {
            // ACL 或调用者校验拒绝。**明确不重试** —— 重试只会重复被拒并掩盖真问题。
            return new AttemptResult(ControlFailure.AccessDenied, default, $"access denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            // 管道存在但已断开 / 服务端正在退出 → 当作"没在跑"，值得补救
            return new AttemptResult(ControlFailure.NotRunning, default, $"io error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new AttemptResult(ControlFailure.NotRunning, default, $"unexpected: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 带超时读一行。
    /// </summary>
    /// <remarks>
    /// 为什么不用 <c>StreamReader</c> 的同步 <c>ReadLine</c>：命名管道不支持 <c>ReadTimeout</c>，
    /// 一个不回包也不断开对端的服务端会让 CLI **永久挂住** —— 那正是"CLI 看起来坏了"的成因。
    /// 超时后放弃等待并随 using 释放流；后台读任务抛出的异常无人观察，由 <see cref="Task"/> 自行丢弃
    /// （此处刻不等待它，也不会让异常逃逸到进程）。
    /// </remarks>
    private static string? ReadLineWithTimeout(StreamReader reader, int timeoutMs)
    {
        var task = reader.ReadLineAsync();
        return task.Wait(timeoutMs) ? task.Result : null;
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
