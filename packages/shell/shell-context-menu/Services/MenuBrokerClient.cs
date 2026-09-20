// BetterDesktop.Shell.ContextMenus — 进程外 broker 客户端（跨语言候选评估 §4.1 第二/三步）
//
// 【它解决什么】第三方 handler 的「实例化 + QueryContextMenu」原先在宿主进程内跑，SEH 级 AV 无法托管
//   隔离：managed try/catch 拦不住，ShellExMenuPreview 的 5s 超时也拦不住（在 native 里 AV 的线程无法
//   终止）。现由 BetterDesktopMenuBroker.exe（原生 / 独立进程 / 与免宿主 B 路同一份 source）承担，
//   宿主只发一次「JSON 进 → JSON 出」。handler 崩了死的是 broker，宿主照常可用。
//
// 【崩溃归因（in-proc 路径给不出的信号）】broker 非 0 退出 == 这个 CLSID 把 broker 干掉了。
//   in-proc 时宿主自己就死了，只能靠 inflight 落盘 + 下次启动才归因；这里当场就能记账：
//   HandlerCrashGuard.RecordExternalCrash → 连续 3 次由 HandlerCrashBreaker 自动停用（可逆）。
//
// 【两种失败，两种处置】
//   ① BrokerCrashed：broker 起来了、被某个 handler 干掉 → 产出一条**可见说明项**并当场记账熔断。
//      （宿主侧已无 in-proc 实现可"回退"——那份重复实现已删，见 ShellMenuInterop 头注。）
//   ② 不可用：exe 缺失 / 启动失败 / 超时 / 应答非法 → 返回 false，调用方给出可见降级文案，不静默。
//
// 【为什么每次只送一个 CLSID】崩溃才能精确归因：多 CLSID 一次调用时，崩了不知道是谁。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>把「一次 broker 调用」抽象出来，便于单测注入假实现（真进程启动不属于单测范围）。</summary>
internal interface IMenuBrokerTransport
{
    bool TryRun(string requestJson, int timeoutMs, out string responseJson, out int exitCode, out string error);
}

/// <summary>真实实现：CreateProcessW + stdin/stdout 重定向（与 shell-convert 的 convert-engine 同款）。</summary>
internal sealed class ProcessMenuBrokerTransport : IMenuBrokerTransport
{
    private readonly string _exePath;

    public ProcessMenuBrokerTransport(string exePath) => _exePath = exePath;

    public bool TryRun(string requestJson, int timeoutMs, out string responseJson, out int exitCode, out string error)
    {
        responseJson = string.Empty;
        exitCode = 0;
        error = string.Empty;

        var psi = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            error = "broker 进程启动失败";
            return false;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync(); // 并发读走，防 stderr 写满阻塞 broker
        try
        {
            process.StandardInput.Write(requestJson);
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            error = $"写入 broker 请求失败：{ex.Message}";
            return false;
        }

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已被自行终止
            }
            error = $"broker 未在 {timeoutMs}ms 内应答（疑 handler 卡死）";
            return false;
        }

        process.WaitForExit(); // 等异步输出读完
        responseJson = stdout.GetAwaiter().GetResult();
        exitCode = process.ExitCode;
        return true;
    }
}

/// <summary>
/// broker 客户端：定位 exe、按"一次一个 CLSID"调用、解析应答、归因崩溃、维护冷却与降级。
/// </summary>
internal static class MenuBrokerClient
{
    /// <summary>发布布局 exe 名（随插件 native\ 目录分发）。</summary>
    internal const string ExeName = "BetterDesktopMenuBroker.exe";

    /// <summary>exe 定位覆盖（排障用；与 IndexEngineLauncher/ClipboardEngineLauncher 同款）。</summary>
    internal const string PathEnvVar = "BETTERDESKTOP_MENU_BROKER";

    /// <summary>与 ShellExMenuPreview 的 5s 同量级：单次查询的等待上限。</summary>
    internal const int DefaultTimeoutMs = 5000;

    private static readonly object Gate = new();
    private static IMenuBrokerTransport? _transportOverride;

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// broker 应答的解析选项。
    /// <para>
    /// 【C1 为什么限深】应答来自**独立进程的 stdio**（半外部输入：该进程加载第三方 shell 扩展），
    /// 显式设 <see cref="JsonSerializerOptions.MaxDepth"/>=32，不依赖库的默认值。
    /// 刻意**不改**大小写策略：现状是大小写敏感，改动会让"现在能解析的变成不能解析"，属行为回归而非加固。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions ResponseOptions = new() { MaxDepth = 32 };

    /// <summary>测试缝：注入假 transport（null 恢复真实进程启动）。</summary>
    internal static void OverrideTransport(IMenuBrokerTransport? transport)
    {
        lock (Gate)
        {
            _transportOverride = transport;
        }
    }

    /// <summary>broker exe 路径：环境变量覆盖 → 宿主输出目录 native\。缺失返回 null。</summary>
    internal static string? LocateExe()
    {
        var overridden = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return overridden;
        }

        var local = Path.Combine(AppContext.BaseDirectory, "native", ExeName);
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// 查询多个 handler 的菜单项。true = 已由 broker 得出完整答案（含"某 handler 崩了 → 一条可见说明项"）；
    /// false = broker 不可用/不可信（调用方给出可见降级文案——宿主内已无第二份实现可回退）。
    /// </summary>
    internal static bool TryQuery(
        IReadOnlyList<string> clsids,
        IReadOnlyList<string> paths,
        bool background,
        bool extendedVerbs,
        IReadOnlyDictionary<string, string>? handlerNames,
        int timeoutMs,
        out List<ShellVerbItem> items)
    {
        items = [];
        if (clsids.Count == 0)
        {
            return true; // 没有目标 = 空菜单，不必起进程
        }

        var exe = LocateExe();
        lock (Gate)
        {
            if (exe is null && _transportOverride is null)
            {
                return false; // 未部署 broker（纯 C# 构建）：由调用方给出可见降级文案
            }
        }

        var merged = new List<ShellVerbItem>();
        foreach (var clsid in clsids)
        {
            var name = handlerNames is not null && handlerNames.TryGetValue(clsid, out var known) ? known : null;
            var requestJson = BuildRequest("menu", clsid, paths, background, extendedVerbs, verb: null);

            switch (RunOnce(exe, requestJson, clsid, name, timeoutMs, out var response))
            {
                case BrokerOutcome.Responded:
                    if (response is { Ok: true, Items: not null })
                    {
                        merged.AddRange(MapItems(response.Items));
                        HandlerCrashGuard.NoteSuccess(clsid);
                    }
                    else
                    {
                        // broker 正常应答但该 handler 没产出（未注册 / 拒绝初始化）：与 in-proc 同语义——跳过
                        DiagnosticLog.Trace("shell.contextmenu",
                            $"broker: {clsid} 无产出（{response?.Error ?? "未声明原因"}）");
                    }
                    break;

                case BrokerOutcome.BrokerCrashed:
                    // 已知凶器：本次按"无内容"处理，绝不在宿主内重试（那等于把崩溃搬回进程内）。
                    // 但**降级必须可见**（§4.1 验收不变量：不得静默无菜单）——产出一条说明项，
                    // 而不是让上层把"读不到"显示成"该扩展没有内容"。
                    DiagnosticLog.Trace("shell.contextmenu",
                        $"broker 被 handler 干掉：{clsid}{(name is null ? string.Empty : $"（{name}）")}——已记账熔断");
                    merged.Add(new ShellVerbItem(
                        $"无法读取该扩展的菜单：它导致读取进程崩溃（已记录；连续 {HandlerCrashGuard.Threshold} 次将自动停用）",
                        IsSeparator: false,
                        IsSubMenu: false,
                        [],
                        Invoke: null));
                    break;

                default:
                    // broker 不可用/不可信：交给调用方出可见降级文案
                    items = [];
                    return false;
            }
        }

        items = merged;
        return true;
    }

    private enum BrokerOutcome
    {
        Responded,
        BrokerCrashed,
        Infrastructure,
    }

    private static BrokerOutcome RunOnce(
        string? exe, string requestJson, string clsid, string? displayName, int timeoutMs, out BrokerResponse? response)
    {
        response = null;
        IMenuBrokerTransport? transport;
        lock (Gate)
        {
            transport = _transportOverride;
        }
        transport ??= exe is null ? null : new ProcessMenuBrokerTransport(exe);
        if (transport is null)
        {
            return BrokerOutcome.Infrastructure;
        }

        try
        {
            if (!transport.TryRun(requestJson, timeoutMs, out var responseJson, out var exitCode, out var error))
            {
                DiagnosticLog.Trace("shell.contextmenu", $"broker 调用失败（回退 in-proc）：{error}");
                return BrokerOutcome.Infrastructure;
            }

            if (exitCode != 0)
            {
                // 非 0 退出 = 进程异常终止（handler 里的 AV 等）。协议契约见 native/include/ShellBroker.h。
                var report = HandlerCrashGuard.RecordExternalCrash(clsid, displayName);
                DiagnosticLog.Trace("shell.contextmenu",
                    $"broker 异常退出 exit=0x{exitCode:X8} clsid={clsid}——连续 {report.Consecutive}/{HandlerCrashGuard.Threshold} 次");
                if (report.ReachedThreshold)
                {
                    HandlerCrashBreaker.DisableNow(clsid, report.DisplayName);
                }
                return BrokerOutcome.BrokerCrashed;
            }

            response = JsonSerializer.Deserialize<BrokerResponse>(responseJson, ResponseOptions);
            if (response is null)
            {
                DiagnosticLog.Trace("shell.contextmenu", "broker 应答为空（回退 in-proc）");
                return BrokerOutcome.Infrastructure;
            }
            return BrokerOutcome.Responded;
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"broker 应答非法 JSON（回退 in-proc）：{ex.Message}");
            return BrokerOutcome.Infrastructure;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"broker 调用异常（回退 in-proc）：{ex.Message}");
            return BrokerOutcome.Infrastructure;
        }
    }

    private static string BuildRequest(
        string op, string clsid, IReadOnlyList<string> paths, bool background, bool extendedVerbs, string? verb)
    {
        var request = new BrokerRequest(
            Op: op,
            Background: background,
            Extended: extendedVerbs,
            Paths: paths.ToArray(),
            Clsids: [clsid],
            Verb: verb);
        return JsonSerializer.Serialize(request, RequestOptions);
    }

    private static IEnumerable<ShellVerbItem> MapItems(IReadOnlyList<BrokerItemDto> nodes)
    {
        foreach (var node in nodes)
        {
            // 文本已由 broker 归一（去 & / 去 C1 控制符）；此处再过一遍宿主既有归一，保证两套路径输出同形。
            // Invoke 留空：broker 的 invoke 命令已实现（见 ShellBroker.h），但当前无生产消费方，
            // 等真有调用方时再接（预置未消费的调用链等于给未来埋错）。
            yield return new ShellVerbItem(
                MenuText.FromWin32(node.Text ?? string.Empty),
                node.Separator,
                node.Submenu,
                MapItems(node.Children ?? []).ToList(),
                null);
        }
    }

    private sealed record BrokerRequest(
        string Op, bool Background, bool Extended, string[] Paths, string[] Clsids, string? Verb);

    private sealed record BrokerResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("items")] List<BrokerItemDto>? Items);

    private sealed record BrokerItemDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("verb")] string? Verb,
        [property: JsonPropertyName("sep")] bool Separator,
        [property: JsonPropertyName("sub")] bool Submenu,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("checked")] bool Checked,
        [property: JsonPropertyName("children")] List<BrokerItemDto>? Children);
}
