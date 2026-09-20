// BetterDesktop.Host — 菜单命令桥（M3：系统右键注入项 → 运行中宿主）
// 第二实例带 --menu-cmd 启动 → 经命名管道转发给首实例 → 派发到对应服务 → 自身退出。
//
// C1 安全模型说明：命名管道默认 DACL 仅允许当前用户连接。在「同用户任意进程」威胁模型下，
// DACL 无法阻止同用户恶意进程连接（它们持有同一用户令牌）。本桥采用三层轻量加固：
//   ① 管道实例数限制为 1（单实例宿主，无需多实例），减少攻击面；
//   ② 协议 magic 握手（MagicPrefix）——服务端丢弃不带正确前缀的消息，防止误连与低技能攻击；
//   ③ 读取既带**总超时**又带**字节上限**（BoundedPipeLine）：实例数限 1 时，
//      "连上不说话"能占死命令通道、"只说不换行"能吃满内存 —— 两者都是可用性攻击，
//      与 magic 防的不是同一类问题，故三条并存（判据见 docs/threat-model.md C 系列）。
// 同用户下的高技能对抗（反编译 magic / 注入宿主进程）超出 P2 加固范围，需配合后续
// 进程身份校验或一次性 token 方案；当前桥只转发 open-settings/dock-pin/toggle/convert/compress
// 等用户主动触发的动作，不暴露文件读写原语，影响面有限。

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Host;

/// <summary>菜单命令桥：管道服务端（首实例常驻）+ 客户端（第二实例一次性转发）。
/// 客户端实现已抽到 kernel（MenuCommandPipeClient），Host 与 BetterDesktop.Cli 共用，协议单点维护。</summary>
public static class MenuCommandPipe
{
    /// <summary>
    /// legacy 形态的服务端管道名 —— **与 core 的控制管道分开**（2026-09-19，见计划 §13.17）。
    ///
    /// 曾经与 core 同名（`BetterDesktop.MenuCmd`）→ 两个服务端抢同一个名字，而两侧动词集不相交
    /// → **客户端连上谁不确定**（legacy 落到 core 被静默吞掉，且 `TrySend` 照样返回 true →
    /// 调用方连回退都被短路）。分开之后：**本服务端 100% 处理 legacy**，core 100% 处理 `@ctl`；
    /// 归属契约见 `protocols/bdmc1-test-vectors.json` 的 `_routing`。
    /// </summary>
    public const string PipeName = "BetterDesktop.HostCmd";

    /// <summary>协议 magic 前缀（BetterDesktop Menu Command v1），服务端校验后才派发。</summary>
    private const string MagicPrefix = "BDMC1|";

    /// <summary>
    /// 单连接读取超时（毫秒）。
    /// <para>
    /// 【2026-09-16 真机事故】读取过去没有超时：客户端只要**连上却不发消息**（探活探针、半开连接、
    /// 进程被杀留下的句柄），<c>ReadLineAsync</c> 就永远挂着 —— 而管道实例数限 1，
    /// 于是**后续所有命令都连不上**（真机症状：按序粘贴上报全部静默丢失、CLI/托盘命令无反应）。
    /// 现在超时即丢弃该连接、实例槽立刻释放。
    /// </para>
    /// <para>
    /// 本常量是**整个连接的**总超时（不是每次 read 的超时），与字节上限一起由
    /// <see cref="BoundedPipeLine.TryRead"/> 执行 —— 两者缺一都会留下可用性攻击面。
    /// </para>
    /// </summary>
    private const int ReadTimeoutMs = 2000;

    /// <summary>第二实例：转发命令给运行中的首实例。返回 false = 无运行实例（共享 kernel 实现）。</summary>
    public static bool TrySend(string action, string path) => MenuCommandPipeClient.TrySend(action, path);

    /// <summary>首实例：启动常驻服务循环（后台线程，逐连接处理）。</summary>
    public static void StartServer(Func<string, string, Task> dispatch)
    {
        var thread = new Thread(async () =>
        {
            while (true)
            {
                try
                {
                    // C1：maxNumberOfServerInstances=1（单实例宿主），减少攻击面
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    // 读取带**字节上限**与总超时（上限取协议常量，与 core 侧 read_line_bounded 同源）：
                    //   · 超时 —— 连上不发消息的客户端不许占住唯一实例槽（2026-09-16 真机事故）；
                    //   · 上限 —— 只发数据不发换行符的客户端不许把内存吃满（同一事故的另一半）。
                    var outcome = BoundedPipeLine.TryRead(
                        server, BoundedPipeLine.DefaultMaxBytes, ReadTimeoutMs, out var line);
                    if (outcome != BoundedPipeLine.Outcome.Line)
                    {
                        DiagnosticLog.Trace("menu-cmd", $"丢弃连接（{outcome}，未收到合法单行命令）");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    // C1：magic 握手校验——丢弃不带正确前缀的连接（误连/低技能攻击）
                    if (!line.StartsWith(MagicPrefix, StringComparison.Ordinal))
                    {
                        DiagnosticLog.Trace("menu-cmd", $"丢弃无 magic 前缀的连接（{line.Length} 字节）");
                        continue;
                    }
                    var payload = line[MagicPrefix.Length..];
                    var separator = payload.IndexOf('|');
                    var action = separator < 0 ? payload : payload[..separator];
                    var path = separator < 0 ? string.Empty : payload[(separator + 1)..];
                    await dispatch(action, path).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Trace("menu-cmd", $"服务循环异常（继续）: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "menu-cmd-pipe",
        };
        thread.Start();
    }
}
