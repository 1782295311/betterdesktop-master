using System.IO.Pipes;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 菜单命令桥客户端（M3 共享实现）：**两个服务端、按形态路由**。
///
/// <para>
/// 【为什么是两个服务端】(2026-09-19，见计划 §13.17) 两种消息形态由不同进程处理：
/// <list type="bullet">
///   <item><c>@ctl</c>（控制形态，请求/应答）→ **core**（<c>core/src/pipe.rs</c>）；</item>
///   <item>legacy（写后即忘）→ **Host**（<c>host/MenuCommandPipe.cs</c>）。</item>
/// </list>
/// 此前两侧**同名**（都叫 <c>BetterDesktop.MenuCmd</c>），而动词集不相交 → 客户端连上谁不确定
/// → 每个入口调用都是掷硬币：legacy 落到 core 被静默吞掉、<c>@ctl</c> 落到 Host 被当未知命令丢掉。
/// 现在按 head 段分流，**两条路各自确定**。
/// </para>
///
/// <para>
/// 【<see cref="TrySend"/> 的返回语义（契约，改前先读）】返回 <c>true</c> 的**唯一含义**是
/// 『**目标**服务端收下了这条消息』，**不是**『有任意一个服务端活着』。
/// 这条必须守住：legacy 落到 core 时它照样返回 <c>true</c> 的老毛病，真正的伤害不是"动作被吞"，
/// 而是**调用方以为宿主已处理、把本该生效的回退路径跳过了**（CLI 多处依赖这个布尔值）。
/// 因此**不得**改成"任一管道可写即 true" —— 那只是把短路换个方向
/// （Host 活着就假装处理了 <c>@ctl</c>）。
/// </para>
///
/// <para>管道名 / magic / 消息格式单点维护（对外契约 BDMC1），Host 与 CLI 共用本类。</para>
/// </summary>
public static class MenuCommandPipeClient
{
    /// <summary>控制形态（<c>@ctl</c>）的管道名 —— 服务端是 **core**（与 <c>core/src/pipe.rs</c> 的 PIPE_NAME 逐字一致）。</summary>
    public const string PipeName = "BetterDesktop.MenuCmd";

    /// <summary>legacy 形态的管道名 —— 服务端是 **Host**（与 <c>host/MenuCommandPipe.cs</c> 的 PipeName 逐字一致）。</summary>
    public const string HostPipeName = "BetterDesktop.HostCmd";

    /// <summary>控制形态的 head 标记（路由判据；见契约 <c>_routing.rule</c>）。</summary>
    private const string ControlHead = "@ctl";

    /// <summary>协议 magic 前缀（BetterDesktop Menu Command v1），服务端校验后才派发。</summary>
    private const string MagicPrefix = "BDMC1|";

    /// <summary>
    /// 按 **head 段**选目标管道：<c>@ctl</c> → core；其余 → Host。
    /// </summary>
    /// <remarks>
    /// 判据只依赖 first 段，所以**每个调用方都被同一条规则覆盖**（CLI 各处 / Host 二实例转发 /
    /// DesktopToggleExecutor）—— 不需要各处自己记住"我属于哪一侧"。
    /// **公开出去是为了让测试能直接断言路由**（而不是靠连真管道碰运气）。
    /// </remarks>
    public static string PipeNameFor(string action) =>
        action.StartsWith(ControlHead, StringComparison.Ordinal) ? PipeName : HostPipeName;

    /// <summary>
    /// 转发命令给**对应的**服务端。返回 <c>false</c> = 该服务端不在 ⇒ 调用方走降级路径。
    /// </summary>
    public static bool TrySend(string action, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNameFor(action), PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            // C1：带 magic 前缀，服务端校验后才派发
            writer.WriteLine($"{MagicPrefix}{action}|{path}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"转发失败（无运行实例?）: {ex.Message}");
            return false;
        }
    }
}
