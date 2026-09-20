using System.Diagnostics;
using System.IO;
using System.Threading;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Services;

namespace BetterDesktop.Cli;

/// <summary>CLI 退出码（对外契约；注册表命令只关心进程结束，错误码供诊断/测试）。</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 2;          // 参数错误 / 未知动作
    public const int FileMissing = 3;    // 输入文件/路径不存在
    public const int EngineMissing = 4;  // 转换引擎缺失（不是用户文件错误）
    public const int Failed = 5;         // 执行失败（转换/压缩/解压业务失败）
    public const int NeedsHost = 6;      // 动作需宿主完整在线（剪贴板历史等）
    public const int CoreUnavailable = 7; // 连不上 core（含 ensure + 重试后仍失败）
    public const int CoreRejected = 8;    // 连上了但被 core 拒绝（ACL/鉴权或远端业务错误）
    public const int DllMissing = 9;      // 原生扩展 DLL 按注册表路径规则解析不出 → **拒绝注册**（不是失败）
}

/// <summary>
/// BetterDesktop 系统右键菜单轻量入口（M3.1）：宿主未运行时由 CLI 直执行 headless 动作。
/// 入口契约：--menu-batch &lt;file&gt; / --menu-cmd &lt;action&gt; [&lt;path&gt;] / --toggle-desktop / --toggle-key &lt;name&gt;。
/// 路由：① --menu-batch 直接由 HeadlessExecutor.RunBatch 执行（原生扩展多选入口，不走管道）；
/// ② --menu-cmd：管道转发（宿主在跑）→ 退出；无宿主 &amp; headless 动作 → 本地执行 → 退出；
/// ③ 无宿主 &amp; 需宿主动作 → 原生提示「需要 BetterDesktop 正在运行」→ 退出（不拉起静默宿主）。
/// --toggle-desktop 翻到"开"拉完整宿主承载（用户主动开启桌面外壳，非静默装配）；--toggle-key 仅直写设置。
/// CLI 绝不启动 WPF Application/Dispatcher；UseWPF=true 仅因引用链（shell-convert），不建窗口。
/// </summary>
internal static class Program
{
    private static readonly string DiagFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdt-cli.log");

    [STAThread]
    private static int Main(string[] args)
    {
        // 原生右键扩展批文件入口（2026-09-11 双路 COM 扩展）：--menu-batch <批文件>
        // 多选批量的唯一通道——原生侧落 UTF-8 JSON（action/args/paths），CLI 读后即删。
        // 【刻意不走管道转发】批路径本就是"免宿主"设计，转发只会平白多等一次 1.5s 连接超时。
        if (args.Length >= 2 && string.Equals(args[0], "--menu-batch", StringComparison.Ordinal))
        {
            Diag($"menu-batch file={args[1]}");
            return HeadlessExecutor.RunBatch(args[1]);
        }

        if (args.Length >= 2 && string.Equals(args[0], "--menu-cmd", StringComparison.Ordinal))
        {
            var action = args[1];
            var path = args.Length >= 3 ? args[2] : string.Empty;
            Diag($"menu-cmd action={action} path={path}");

            // ① 宿主在跑：转发（热切/面板复用），本进程退出。
            if (MenuCommandPipeClient.TrySend(action, path))
            {
                Diag("转发成功（宿主在运行），本进程退出");
                return ExitCodes.Ok;
            }

            // ②/③ 无宿主：headless 直执行 或 需宿主提示。
            Diag("无运行实例，进入 CLI 路由");
            return HeadlessExecutor.Run(action, path);
        }

        // 自绘桌面开关（系统桌面右键「切换自绘桌面」）：宿主在跑 → 命令桥热切；无宿主 → 直写 settings.json
        //（翻到"开"= 用户明确要启用桌面外壳，拉完整宿主承载——非静默装配）。
        if (args.Length >= 1 && string.Equals(args[0], "--toggle-desktop", StringComparison.Ordinal))
        {
            if (MenuCommandPipeClient.TrySend("toggle-desktop", string.Empty))
            {
                Diag("转发成功（宿主在运行），本进程退出");
                return ExitCodes.Ok;
            }
            Diag("无运行实例，CLI 直写设置");
            return HeadlessExecutor.ToggleDesktop();
        }

        // 自绘 UI 开关（系统桌面右键「自绘桌面 ▸」子项）：无宿主 → 仅翻转设置，不拉起宿主。
        if (args.Length >= 2 && string.Equals(args[0], "--toggle-key", StringComparison.Ordinal))
        {
            if (MenuCommandPipeClient.TrySend("toggle-key", args[1]))
            {
                Diag("转发成功（宿主在运行），本进程退出");
                return ExitCodes.Ok;
            }
            Diag("无运行实例，CLI 直写设置");
            return HeadlessExecutor.ToggleKey(args[1]);
        }

        // 系统集成（安装器级落地 2026-09-17）：--system-integration <status|register|repair|unregister> [--all] [--json]
        // 这是"把程序正式注册到系统"的可编程入口（安装/卸载脚本与托盘「系统集成」菜单都走它）：
        //   status     打印 `键=值` 状态（脚本可解析；--json 输出单行 JSON）
        //   register   注册 B 路右键扩展 + 托盘开机自启（幂等）
        //   repair     与 register 同一实现（先看状态再补缺，见 SystemIntegrationRegistrar）
        //   unregister 注销右键扩展 + 删快照；带 --all 时连自启项一起清（卸载脚本用）
        // headless 纪律：不弹窗、不拉起宿主；成功 0 / 未知子命令 2 / 执行失败 5。
        if (args.Length >= 2 && string.Equals(args[0], "--system-integration", StringComparison.Ordinal))
        {
            return SystemIntegration(args);
        }

        // core 控制面（计划 §6.3/S2）：--core <verb> [arg] [--json]
        // 这是 CLI 指 core 的唯一入口（S2 的"CLI 改指 core"）；旧动作（--menu-cmd 等）暂留到 S6 收口。
        if (args.Length >= 2 && string.Equals(args[0], "--core", StringComparison.Ordinal))
        {
            return CoreCommand(args);
        }

        // 原生右键扩展**窄命令**（S3-3）：只做注册/注销这一件事。
        // 【为什么不复用 --system-integration register】那条路会连带登记开机自启、清历史静态 verb ——
        // "注册右键扩展"与"把托盘登记进开机自启"是两件独立的事，混在一起就无法单独重放与回滚，
        // 而且没法表达"只注册 dev 扩展、别动自启"这个开发态真正需要的语义。
        if (args.Length >= 1 && string.Equals(args[0], "--shellmenu-register", StringComparison.Ordinal))
        {
            return ShellMenuRegister(args);
        }

        if (args.Length >= 1 && string.Equals(args[0], "--shellmenu-unregister", StringComparison.Ordinal))
        {
            return ShellMenuUnregister();
        }

        // 备份 / 回滚：**写注册表之前先具备的能力**（S4-2 第 2 步的前置）。
        // 写 HKCU\Software\Classes 是改系统状态而不是改代码，"从零到有再到零"的验收
        // 与真机出错时的救场都依赖它 —— 故它必须先于任何自动写注册表的代码存在。
        if (args.Length >= 1 && string.Equals(args[0], "--shellmenu-backup", StringComparison.Ordinal))
        {
            return ShellMenuBackup(args);
        }

        if (args.Length >= 1 && string.Equals(args[0], "--shellmenu-restore", StringComparison.Ordinal))
        {
            return ShellMenuRestore(args);
        }

        // 托盘「导出诊断包 / 应急恢复 / 更新」的落点（S5-4 动作归属）：
        // core 只派发窄命令，实现细节都在本进程（打包 / 定位组件 / 编排更新），
        // 于是 core 的代码里不会出现 DiagnosticBundle / Recovery.exe / Updater.exe 的任何形态
        // —— 这也是"allowlist 只登记 CLI 为拉起者"能成立的前提。
        // 导出诊断包：**默认脱敏**（见 DiagnosticsRedactor 的头注 —— 用户导包就是为了发给别人）。
        // --no-redact 显式要原包，文件名带 -RAW 提醒接手的人"这里面有原始路径"。
        if (args.Length >= 1 && string.Equals(args[0], "--diagnostics-export", StringComparison.Ordinal))
        {
            var noRedact = Array.Exists(args, a => string.Equals(a, "--no-redact", StringComparison.Ordinal));
            return DiagnosticsExport(redact: !noRedact);
        }

        if (args.Length >= 1 && string.Equals(args[0], "--recovery", StringComparison.Ordinal))
        {
            return LaunchRecovery();
        }

        if (args.Length >= 2 && string.Equals(args[0], "--update", StringComparison.Ordinal))
        {
            return Update(args[1]);
        }

        Diag($"未知参数: {string.Join(" ", args)}");
        return ExitCodes.Usage;
    }

    /// <summary>
    /// 注册原生右键扩展（<c>--shellmenu-register [--dev]</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 退出码三态：<c>0</c> 成功 / <c>9</c> <see cref="ExitCodes.DllMissing"/>（按规则解析不出可用 DLL ⇒
    /// <b>拒绝注册</b>，**不该重试**）/ <c>5</c> 真的写失败了（可重试）。
    /// </para>
    /// <para>
    /// <c>--dev</c> 会把**开发目录**的路径写进注册表（显式 opt-in），并写下
    /// <see cref="ComShellExtensionRegistrar.DevMarkerFileName"/> 让 core / agent 停手。
    /// 它同时跳过开机自启登记 —— dev 只是"让我测一下菜单"，不是"把我们装进系统"。
    /// </para>
    /// </remarks>
    private static int ShellMenuRegister(string[] args)
    {
        var dev = Array.Exists(args, a => string.Equals(a, "--dev", StringComparison.Ordinal));
        Diag($"shellmenu-register dev={dev}");

        var resolved = ComShellExtensionRegistrar.ResolveNativeDllPath(dev);
        var outcome = ComShellExtensionRegistrar.Register(dev, out var error);

        var text = string.Concat(
            "ok=", (outcome == ComShellExtensionRegistrar.RegisterOutcome.Registered).ToString(), Environment.NewLine,
            "outcome=", outcome.ToString(), Environment.NewLine,
            "dllPath=", resolved ?? string.Empty, Environment.NewLine,
            "devMode=", dev.ToString(), Environment.NewLine,
            "error=", error ?? string.Empty);
        Console.WriteLine(text);
        Diag(text.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));

        switch (outcome)
        {
            case ComShellExtensionRegistrar.RegisterOutcome.Registered:
                // 【生效提示必须如实】explorer 在启动时枚举并**缓存** shellex 处理程序，
                // 写注册表本身不会让它重新读 —— 只有重启 explorer 才生效。这里给出人可执行的一句，
                // 而不是让用户对着"注册成功"却看不到菜单项。
                Console.WriteLine("注意：需重启 explorer 后右键菜单才会出现（或注销/重登一次）。");
                return ExitCodes.Ok;

            case ComShellExtensionRegistrar.RegisterOutcome.DllMissing:
                return ExitCodes.DllMissing;

            default:
                return ExitCodes.Failed;
        }
    }

    /// <summary>
    /// 备份本扩展拥有的注册表键（<c>--shellmenu-backup [--out &lt;path&gt;]</c>）。
    /// </summary>
    /// <remarks>
    /// 默认写到 <c>%LOCALAPPDATA%\BetterDesktop\shellmenu-backup.json</c>（**覆盖式**：
    /// 回滚最需要的是"最近一次已知良好状态"，而不是一堆带时间戳的目录）。
    /// 输出把"拍了什么"逐项打出来 —— 回滚前最该确认的就是这份备份里到底有什么。
    /// </remarks>
    private static int ShellMenuBackup(string[] args)
    {
        var path = ValueOf(args, "--out") ?? ShellMenuRegistryBackup.DefaultPath;
        Diag($"shellmenu-backup out={path}");

        var snapshot = ShellMenuRegistryBackup.Capture();
        if (!ShellMenuRegistryBackup.Save(snapshot, path, out var error))
        {
            Console.WriteLine($"ok=False{Environment.NewLine}error={error}");
            Diag($"shellmenu-backup FAILED: {error}");
            return ExitCodes.Failed;
        }

        var present = snapshot.Keys.Count(k => k.Exists);
        Console.WriteLine(string.Concat(
            "ok=True", Environment.NewLine,
            "path=", path, Environment.NewLine,
            "keys=", snapshot.Keys.Count.ToString(), Environment.NewLine,
            "keysPresent=", present.ToString(), Environment.NewLine,
            "registered=", snapshot.WasRegistered.ToString(), Environment.NewLine,
            "registeredDll=", snapshot.RegisteredDllPath ?? string.Empty));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// 按备份回滚（<c>--shellmenu-restore [--from &lt;path&gt;]</c>）。
    /// </summary>
    /// <remarks>
    /// 回滚**不复用**注册/注销代码：那两条是"写出正确状态"，回滚是"写出**当时那个**状态"
    /// （可能是残缺的、可能根本没有键）。用前者做后者会把"回到原样"变成"回到我认为对的样子"。
    /// </remarks>
    private static int ShellMenuRestore(string[] args)
    {
        var path = ValueOf(args, "--from") ?? ShellMenuRegistryBackup.DefaultPath;
        Diag($"shellmenu-restore from={path}");

        if (!ShellMenuRegistryBackup.TryLoad(path, out var backup, out var error) || backup is null)
        {
            Console.WriteLine($"ok=False{Environment.NewLine}error={error}");
            Diag($"shellmenu-restore load FAILED: {error}");
            return ExitCodes.Failed;
        }

        var report = ShellMenuRegistryBackup.Restore(backup);
        var text = string.Concat(
            "ok=", report.Ok.ToString(), Environment.NewLine,
            "written=", report.Written.ToString(), Environment.NewLine,
            "removed=", report.Removed.ToString(), Environment.NewLine,
            // 回滚后**重新读一遍实际状态**：这才是"有没有回到原样"的答案，
            // 而不是"我们写了几次"。两者不一致时下一行会立刻暴露。
            "restoredRegistered=", ComShellExtensionRegistrar.IsRegistered().ToString(), Environment.NewLine,
            "restoredDll=", ComShellExtensionRegistrar.GetRegisteredDllPath() ?? string.Empty, Environment.NewLine,
            "problems=", report.Problems.Count == 0 ? string.Empty : string.Join("; ", report.Problems));
        Console.WriteLine(text);
        Diag($"shellmenu-restore → {text.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}");
        return report.Ok ? ExitCodes.Ok : ExitCodes.Failed;
    }

    /// <summary>取 <c>--flag value</c> 形式的值（缺失返回 <c>null</c>）。</summary>
    private static string? ValueOf(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// 注销原生右键扩展（<c>--shellmenu-unregister</c>）。
    /// </summary>
    /// <remarks>
    /// **委派**给 <see cref="SystemIntegrationRegistrar.Unregister"/>（<c>includeAutostart: false</c>）——
    /// 而不是重写一遍删键逻辑：那样会长出第二套"注销"语义，而其中一套必然漏掉
    /// "写下'用户显式注销'标记"这一步，于是 agent 的 60 秒自愈会在 1 分钟内把它装回来
    /// （这是本仓库已实测过的链路，见 <c>UnregisteredFlagPath</c> 的注释）。
    /// </remarks>
    private static int ShellMenuUnregister()
    {
        Diag("shellmenu-unregister");
        var ok = SystemIntegrationRegistrar.Unregister(includeAutostart: false, out var error);
        Console.WriteLine($"ok={ok}{Environment.NewLine}error={error ?? string.Empty}");
        Diag($"shellmenu-unregister ok={ok} error={error ?? string.Empty}");
        return ok ? ExitCodes.Ok : ExitCodes.Failed;
    }

    /// <summary>
    /// core 控制命令。默认输出人类可读摘要；<c>--json</c> 输出**单行原始 JSON**（供脚本解析）。
    /// </summary>
    /// <remarks>
    /// 退出码区分三类结果，脚本据此分流：连不上（7，已 ensure + 重试一次）/ 被拒或业务错（8）/ 成功（0）。
    /// 把"连不上"与"被拒绝"分成两个码是有意的：前者可能要拉起 core，后者绝对不该重试（重试只会再被拒）。
    /// </remarks>
    private static int CoreCommand(string[] args)
    {
        var verb = args[1];
        var json = Array.Exists(args, a => string.Equals(a, "--json", StringComparison.Ordinal));
        var arg = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : string.Empty;
        Diag($"core verb={verb} arg={arg} json={json}");

        var result = CoreControlClient.Send(verb, arg, CoreEnsurer.Ensure);

        if (!result.Succeeded)
        {
            // 失败细节走诊断日志；控制台给可脚本解析的三行。
            var text = string.Concat(
                "ok=False", Environment.NewLine,
                "failure=", result.Failure.ToString(), Environment.NewLine,
                "detail=", result.FailureDetail ?? string.Empty);
            Console.WriteLine(text);
            Diag($"core {verb} → {result.Failure} (attempts={result.Attempts}) {result.FailureDetail}");

            return result.Failure is ControlFailure.AccessDenied or ControlFailure.RemoteError
                ? ExitCodes.CoreRejected
                : ExitCodes.CoreUnavailable;
        }

        var output = json ? RawJson(result) : Describe(result);
        Console.WriteLine(output);
        Diag($"core {verb} → ok (attempts={result.Attempts})");
        return ExitCodes.Ok;
    }

    private static string RawJson(ControlResult result) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = true,
            verb = result.Response.Verb,
            data = result.Response.Data,
        });

    /// <summary>人类可读摘要（status 出表格，其余出 `键=值`）。</summary>
    private static string Describe(ControlResult result)
    {
        var data = result.Response.Data;
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return "ok=True";
        }

        // status 的特殊排版：desired/actual/health 是"组件 → 值"的映射，表格最易读。
        if (result.Response.Verb == "status" &&
            data.TryGetProperty("desired", out var desired) &&
            data.TryGetProperty("actual", out var actual))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"core uptime={Get(data, "uptime")}s restarts={Get(data, "restarts")}");
            sb.AppendLine($"{"component",-18}{"desired",-22}{"actual",-8}health");
            foreach (var property in desired.EnumerateObject())
            {
                var name = property.Name;
                var actualValue = actual.TryGetProperty(name, out var a) ? a.ToString() : "?";
                var health = data.TryGetProperty("health", out var h) && h.TryGetProperty(name, out var hv)
                    ? hv.ToString()
                    : "?";
                sb.AppendLine($"{name,-18}{property.Value,-22}{actualValue,-8}{health}");
            }

            return sb.ToString().TrimEnd();
        }

        var lines = new System.Text.StringBuilder("ok=True");
        foreach (var property in data.EnumerateObject())
        {
            lines.AppendLine().Append(property.Name).Append('=').Append(property.Value);
        }

        return lines.ToString();
    }

    private static string Get(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "?";

    /// <summary>系统集成子命令（见 Main 内注释）。输出同时落 bdt-cli.log，便于安装脚本事后取证。</summary>
    private static int SystemIntegration(string[] args)
    {
        var action = args[1];
        var all = Array.Exists(args, a => string.Equals(a, "--all", StringComparison.Ordinal));
        var json = Array.Exists(args, a => string.Equals(a, "--json", StringComparison.Ordinal));
        Diag($"system-integration action={action} all={all} json={json}");

        switch (action)
        {
            case "status":
                {
                    var status = SystemIntegrationRegistrar.GetStatus();
                    var text = json
                        ? System.Text.Json.JsonSerializer.Serialize(status)
                        : SystemIntegrationRegistrar.Describe(status);
                    Console.WriteLine(text);
                    Diag(text);
                    return ExitCodes.Ok;
                }

            case "register":
            case "repair":
                {
                    string? error;
                    // repair **永远不是 dev**（见 SystemIntegrationRegistrar.Repair 的说明）；
                    // register 也不带 --dev —— dev 注册有专门的窄命令（--shellmenu-register --dev），
                    // 免得"顺手加个 --dev"把安装器/自愈的路径也带偏。
                    // 必须写成两参形式：单参的 `Register(out error)` 会绑到兼容重载（返回 bool），
                    // 拿不到 DllMissing 这一态（编译器会拦下，但值得在这里说明为什么不图省事）。
                    var outcome = action == "register"
                        ? SystemIntegrationRegistrar.Register(devMode: false, out error)
                        : (SystemIntegrationRegistrar.Repair(out error)
                            ? SystemIntegrationRegistrar.IntegrationOutcome.Ok
                            : SystemIntegrationRegistrar.IntegrationOutcome.Failed);

                    // 「按规则解析不出可用 DLL」与「写失败了」必须分成两个码：
                    // 前者重试一百次也是同一结果（安装器/脚本据此**不该重试**），后者可以重试。
                    // 都并进 Failed 会让自动化对前者做无意义的重试循环。
                    if (outcome == SystemIntegrationRegistrar.IntegrationOutcome.DllMissing)
                    {
                        Console.WriteLine(string.Concat(
                            "ok=False", Environment.NewLine,
                            "failure=", outcome.ToString(), Environment.NewLine,
                            "error=", error ?? string.Empty));
                        Diag($"{action} → DllMissing {error}");
                        return ExitCodes.DllMissing;
                    }

                    return ReportActionResult(action, outcome == SystemIntegrationRegistrar.IntegrationOutcome.Ok, error);
                }

            case "unregister":
                {
                    var ok = SystemIntegrationRegistrar.Unregister(all, out var error);
                    return ReportActionResult("unregister", ok, error);
                }

            default:
                Diag($"system-integration 未知子命令: {action}");
                Console.WriteLine("用法: BetterDesktop.Cli.exe --system-integration <status|register|repair|unregister> [--all] [--json]");
                return ExitCodes.Usage;
        }
    }

    /// <summary>动作结果统一输出（`ok=` / `error=` 两行；失败非零退出——失败不得被脚本当成成功）。</summary>
    private static int ReportActionResult(string action, bool ok, string? error)
    {
        var text = $"ok={ok}{Environment.NewLine}error={error ?? string.Empty}";
        Console.WriteLine(text);
        Diag($"{action} → {text.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}");
        return ok ? ExitCodes.Ok : ExitCodes.Failed;
    }

    // ───────────── S5-4：托盘动作的 CLI 落点（core 只派发窄命令） ─────────────

    /// <summary>导出诊断包（托盘「导出诊断包…」）。打包到桌面并输出 zip 路径。</summary>
    /// <remarks>
    /// <para>
    /// 打包实现在共享源文件里（<c>shared/logging/DiagnosticBundle.cs</c>，经 kernel 引用进本进程），
    /// 与托盘 / 宿主是**同一份** —— 本命令只是给它一个"core 能派发过来"的入口。
    /// </para>
    /// <para>
    /// **三步，缺一不可**：① 打包（顺带把三类可定位到人的值换成占位符）；
    /// ② **自检**（重开 zip 逐条目扫描残留）；③ 自检不干净 → **删包 + 报错**，
    /// 绝不把"自称已脱敏"的包交出去。自检是这套机制的"锚"——
    /// 脱敏是单点，只有它能把"实现有 bug"变成一次显式失败，而不是一个看起来正常的包。
    /// </para>
    /// </remarks>
    private static int DiagnosticsExport(bool redact)
    {
        var redactor = DiagnosticsRedactor.ForCurrentMachine();
        Func<byte[], byte[]>? hook = redact && redactor.HasRules ? redactor.Redact : null;

        try
        {
            Diag($"diagnostics-export: begin (redact={redact}, rules={redactor.HasRules})");
            var zip = BetterDesktop.Diagnostics.DiagnosticBundle.Export(
                null,
                "cli",
                null,
                hook,
                redact ? null : "-RAW");
            Diag($"diagnostics-export: {zip}");

            if (hook is not null)
            {
                var leaks = redactor.ScanZipForLeaks(zip);
                if (leaks.Count > 0)
                {
                    // 不落盘：宁可这次导出失败，也不给出一份"自称已脱敏"的包。
                    try
                    {
                        File.Delete(zip);
                    }
                    catch
                    {
                        // 删不掉也必须继续报错 —— 报错才是主要目的
                    }

                    var shown = new List<string>();
                    for (var i = 0; i < leaks.Count && i < 5; i++)
                    {
                        shown.Add(leaks[i]);
                    }

                    var detail = string.Join("; ", shown)
                                 + (leaks.Count > shown.Count ? $" …（共 {leaks.Count} 条）" : string.Empty);
                    Diag($"diagnostics-export SELF-CHECK FAILED ({leaks.Count} leaks): {detail}");
                    Console.WriteLine(
                        "ok=False" + Environment.NewLine
                        + "stage=redaction-selfcheck" + Environment.NewLine
                        + "leaks=" + leaks.Count + Environment.NewLine
                        + "detail=" + detail);
                    return ExitCodes.Failed;
                }

                Diag("diagnostics-export: self-check clean");
            }

            Console.WriteLine(zip);
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            Diag($"diagnostics-export failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("ok=False" + Environment.NewLine + "error=" + ex.Message);
            return ExitCodes.Failed;
        }
    }

    /// <summary>应急恢复：拉起独立的应急程序（<c>BetterDesktop.Recovery.exe</c>），**不等它结束**。</summary>
    /// <remarks>
    /// 恢复程序是**零依赖**的应急进程（壳 / 插件全坏时它必须还能跑），故这里只负责定位与拉起。
    /// 它自己是 GUI，用户看得见；等它结束会停在用户操作上 —— core 那边也是按"不等结果"派发的。
    /// </remarks>
    private static int LaunchRecovery()
    {
        var exe = ComponentPathResolver.Resolve(ComponentPathResolver.RecoveryExeName);
        if (exe is null)
        {
            Diag($"recovery: {ComponentPathResolver.RecoveryExeName} not found (install root / data dir / CLI dir)");
            Console.WriteLine(
                "ok=False" + Environment.NewLine
                + "error=应急恢复程序未部署（" + ComponentPathResolver.RecoveryExeName + "）");
            return ExitCodes.FileMissing;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Diag($"recovery: launched {exe}");
            Console.WriteLine("ok=True" + Environment.NewLine + "exe=" + exe);
            return process is null ? ExitCodes.Failed : ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            Diag($"recovery: launch failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("ok=False" + Environment.NewLine + "error=" + ex.Message);
            return ExitCodes.Failed;
        }
    }

    /// <summary>更新：<c>--update check</c> 只检查；<c>--update install</c> 下载 → **经 core 停壳** → 替换。</summary>
    /// <remarks>
    /// <para>
    /// 停壳**必须经 core 控制管道**（`stop shell`）而不是在这里杀进程：壳退出时要恢复桌面图标 /
    /// 任务栏，直接杀会跳过那些收尾；而"谁可以停组件"在终态里只有 core 一个所有者（计划 §6.1-C）。
    /// </para>
    /// <para>
    /// 更新器退出码约定（见 <c>updater/Program.cs</c>）：11 = 已下载待应用；12 = 已应用并重启完毕。
    /// 拿到别的值一律如实报失败 —— 绝不把"没更新成功"说成成功。
    /// </para>
    /// <para>
    /// **core 缺失场景**（三条分支都与"停壳"有关，缺一不可）：
    /// ① core 在 → 直接发 `stop shell`；
    /// ② core 不在 → `CoreControlClient` 调注入的 <c>ensureCore</c>（= `CoreEnsurer.Ensure`）
    ///    拉起 core，并在 5 秒内循环重发真实请求；core 本身是单实例，重复执行是幂等的；
    /// ③ core 拉不起来 → 若壳仍在跑，**报错返回**（见下），不静默继续。
    /// </para>
    /// </remarks>
    private static int Update(string verb)
    {
        switch (verb)
        {
            case "check":
                {
                    var exe = RequireUpdater(out var missing);
                    if (exe is null)
                    {
                        return missing;
                    }

                    var rc = RunTool(exe, 120_000, "--check", "--quiet");
                    Diag($"update check: exit={rc}");
                    Console.WriteLine($"ok={(rc is 0 or 10)}" + Environment.NewLine + $"exit={rc}");
                    return rc is 0 or 10 ? ExitCodes.Ok : ExitCodes.Failed;
                }

            case "install":
                {
                    var exe = RequireUpdater(out var missing);
                    if (exe is null)
                    {
                        return missing;
                    }

                    var download = RunTool(exe, 600_000, "--download", "--quiet");
                    if (download != 11)
                    {
                        Diag($"update install: download exit={download}（期望 11）");
                        Console.WriteLine("ok=False" + Environment.NewLine + $"stage=download exit={download}");
                        return ExitCodes.Failed;
                    }

                    // 停壳：**经 core**（它才是生命周期所有者）。ensure 委托交给 CoreControlClient ——
                    // core 不在时它会先拉起 core 再重发（这就是"core 缺失场景"的处置，不需要另写一遍）。
                    var stopped = CoreControlClient.Send("stop", "shell", CoreEnsurer.Ensure);
                    if (!stopped.Succeeded && IsHostRunning())
                    {
                        // core 拉不起来 ⇒ 壳停不掉 ⇒ 更新器 `--apply` 会一直卡在"等宿主退出"直到超时。
                        // **报错停手**，不静默继续：继续了却停不掉壳，用户会以为在更新、实际什么都没发生。
                        Diag($"update install: cannot stop the shell ({stopped.Failure}: {stopped.FailureDetail}); host still running");
                        Console.WriteLine(
                            "ok=False" + Environment.NewLine
                            + "stage=stop-shell" + Environment.NewLine
                            + "failure=" + stopped.Failure + Environment.NewLine
                            + "detail=" + (stopped.FailureDetail ?? string.Empty));
                        return ExitCodes.Failed;
                    }

                    Diag($"update install: stop shell → {(stopped.Succeeded ? "ok" : stopped.Failure.ToString())}");
                    Thread.Sleep(1500);

                    var apply = RunTool(exe, 300_000, "--apply", "--quiet");
                    Diag($"update install: apply exit={apply}（12 = 完成并重启）");
                    Console.WriteLine($"ok={(apply == 12)}" + Environment.NewLine + $"stage=apply exit={apply}");
                    return apply == 12 ? ExitCodes.Ok : ExitCodes.Failed;
                }

            default:
                Console.WriteLine("用法: BetterDesktop.Cli.exe --update <check|install>");
                Diag($"update: 未知子命令 {verb}");
                return ExitCodes.Usage;
        }
    }

    private static string? RequireUpdater(out int failureCode)
    {
        var exe = ComponentPathResolver.Resolve(ComponentPathResolver.UpdaterExeName);
        if (exe is null)
        {
            Diag($"update: {ComponentPathResolver.UpdaterExeName} not found");
            Console.WriteLine(
                "ok=False" + Environment.NewLine
                + "error=更新组件未部署（" + ComponentPathResolver.UpdaterExeName + "）");
            failureCode = ExitCodes.FileMissing;
            return null;
        }

        failureCode = ExitCodes.Ok;
        return exe;
    }

    /// <summary>壳（主程序）是否仍在跑 —— 用来区分"停壳失败"与"壳本来就没开"。</summary>
    /// <remarks>
    /// 探测本身失败时**保守当作在跑**：宁可多报一次错，也不要因为"以为壳没开"而静默继续 ——
    /// 后者正是"更新看起来在跑、实际卡在等宿主退出"的来源。
    /// </remarks>
    private static bool IsHostRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("BetterDesktop.Host");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>跑一个组件并等它结束（带超时；超时收尸杀整棵进程树）。</summary>
    private static int RunTool(string exe, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return -1;
            }

            if (!process.WaitForExit(timeoutMs))
            {
                Diag($"tool timeout ({timeoutMs}ms): {exe}");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 收尸失败不阻断
                }

                return -2;
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Diag($"tool failed: {exe}: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    private static void Diag(string message)
    {
        try { System.IO.File.AppendAllText(DiagFile, $"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); }
        catch { /* 诊断写入失败不阻断 */ }
    }
}
