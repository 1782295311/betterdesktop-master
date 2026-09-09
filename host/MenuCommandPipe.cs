// BetterDesktop.Host — 菜单命令桥（M3：系统右键注入项 → 运行中宿主）
// 第二实例带 --menu-cmd 启动 → 经命名管道转发给首实例 → 派发到对应服务 → 自身退出。
//
// C1 安全模型说明：命名管道默认 DACL 仅允许当前用户连接。在「同用户任意进程」威胁模型下，
// DACL 无法阻止同用户恶意进程连接（它们持有同一用户令牌）。本桥采用两层轻量加固：
//   ① 管道实例数限制为 1（单实例宿主，无需多实例），减少攻击面；
//   ② 协议 magic 握手（MagicPrefix）——服务端丢弃不带正确前缀的消息，防止误连与低技能攻击。
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

/// <summary>菜单命令桥：管道服务端（首实例常驻）+ 客户端（第二实例一次性转发）。</summary>
public static class MenuCommandPipe
{
    public const string PipeName = "BetterDesktop.MenuCmd";

    /// <summary>协议 magic 前缀（BetterDesktop Menu Command v1），服务端校验后才派发。</summary>
    private const string MagicPrefix = "BDMC1|";

    /// <summary>第二实例：转发命令给运行中的首实例。返回 false = 无运行实例。</summary>
    public static bool TrySend(string action, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
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
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
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
