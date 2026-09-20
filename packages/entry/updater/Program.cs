namespace BetterDesktop.Updater;

/// <summary>
/// BetterDesktop 更新器（独立进程，对齐 CTM 的 Renewal.exe）。
///
/// 四个动作各自独立、可单独重试，互不隐式串联：
///   --check     只读：比对本地 version.json 与远端 manifest.json
///   --download  下载 + SHA256 校验到暂存目录（不碰安装目录）
///   --apply     优雅停 Agent → 等 Host/Agent 退出 → 备份 → 替换 → 重启（失败自动回滚并删除新增文件）
///   --rollback  从最近备份还原，并删除上次新增的文件（防"旧版本 + 新组件"半更新）
///
/// 结果统一写 %LOCALAPPDATA%\BetterDesktop\update-status.json，托盘读它弹气泡（无需 IPC）。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitHasUpdate = 10;
    private const int ExitDownloaded = 11;
    private const int ExitApplied = 12;
    private const int ExitRolledBack = 13;
    private const int ExitUsage = 2;
    private const int ExitSourceUnavailable = 3;
    private const int ExitBadManifest = 4;
    private const int ExitApplyFailed = 5;

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "--help";
        var sourceArg = Value(args, "--source");
        var stagingArg = Value(args, "--staging");
        var noRestart = Has(args, "--no-restart");
        UpdaterLog.Quiet = Has(args, "--quiet");

        UpdaterLog.Write($"updater 启动 pid={Environment.ProcessId} 参数=[{string.Join(" ", args)}]");

        // 上一次替换留下的 .old-* 只有等到占用进程退出后才能删 → 每次启动先清一次
        Applier.CleanupOldArtifacts(UpdaterPaths.BaseDir);

        try
        {
            return command switch
            {
                "--check" => Check(sourceArg, command),
                "--download" => Download(sourceArg, stagingArg, command),
                "--apply" => Apply(stagingArg, command, noRestart),
                "--rollback" => Rollback(command, noRestart),
                "--version" => PrintVersion(),
                "--help" or "-h" or "/?" => Help(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            UpdaterLog.Error(command, ex);
            WriteStatus(command, ok: false, hasUpdate: false, remote: string.Empty, staging: string.Empty,
                message: $"未处理异常：{ex.Message}");
            return ExitApplyFailed;
        }
    }

    // ---------------- 动作 ----------------

    private static int Check(string? sourceArg, string command)
    {
        var local = ReleaseIo.LoadLocalVersion();
        var sources = UpdateSource.ResolveSources(sourceArg);
        if (sources.Count == 0)
        {
            return FailSource(command, "未配置更新源：请用 --source <目录或URL>，"
                + "或在组件同目录创建 update.config.json {\"source\":\"...\"}");
        }

        var fetch = UpdateSource.FetchManifest(sources);
        if (!fetch.Ok || fetch.Manifest is null)
        {
            return FailSource(command, $"更新源不可达或清单无效：{fetch.Error}");
        }

        var remote = fetch.Manifest;
        var same = UpdateSource.SameRelease(local, remote);
        var message = same
            ? $"已是最新（{local.Informational}）"
            : $"发现新版本：{remote.Version} build={remote.Build}（当前 {local.Informational}）"
              + (string.IsNullOrEmpty(remote.Notes) ? string.Empty : $"　更新说明：{remote.Notes}");

        UpdaterLog.Write(message);
        WriteStatus(command, ok: true, hasUpdate: !same, remote: $"{remote.Version}-{remote.Build}",
            staging: string.Empty, message: message);
        return same ? ExitOk : ExitHasUpdate;
    }

    private static int Download(string? sourceArg, string? stagingArg, string command)
    {
        var sources = UpdateSource.ResolveSources(sourceArg);
        if (sources.Count == 0)
        {
            return FailSource(command, "未配置更新源（--source 或 update.config.json）");
        }

        var fetch = UpdateSource.FetchManifest(sources);
        if (!fetch.Ok || fetch.Manifest is null)
        {
            return FailSource(command, $"更新源不可达或清单无效：{fetch.Error}");
        }

        var remote = fetch.Manifest;
        var name = !string.IsNullOrEmpty(remote.Build) ? remote.Build : remote.Version;
        var staging = stagingArg ?? Path.Combine(UpdaterPaths.StagingRoot, name);

        UpdaterLog.Write($"开始下载：{remote.Files.Count} 个文件 → {staging}");
        if (!UpdateSource.DownloadAll(remote, sources, staging, out var error))
        {
            UpdaterLog.Write($"下载失败：{error}");
            WriteStatus(command, ok: false, hasUpdate: true, remote: $"{remote.Version}-{remote.Build}",
                staging: staging, message: $"下载失败：{error}");
            return ExitBadManifest;
        }

        var message = $"下载完成并全部校验通过：{remote.Version} build={remote.Build}（{remote.Files.Count} 个文件）";
        UpdaterLog.Write(message);
        WriteStatus(command, ok: true, hasUpdate: true, remote: $"{remote.Version}-{remote.Build}",
            staging: staging, message: message);
        return ExitDownloaded;
    }

    private static int Apply(string? stagingArg, string command, bool noRestart)
    {
        var staging = stagingArg;
        if (string.IsNullOrEmpty(staging))
        {
            staging = Applier.FindLatestStaging();
        }

        if (string.IsNullOrEmpty(staging) || !Directory.Exists(staging))
        {
            var msg = "找不到暂存目录：请先执行 --download，或用 --staging <目录> 指定";
            UpdaterLog.Write(msg);
            WriteStatus(command, ok: false, hasUpdate: false, remote: string.Empty, staging: string.Empty, message: msg);
            return ExitUsage;
        }

        var manifest = ReleaseIo.TryLoad<ReleaseManifest>(Path.Combine(staging, "manifest.json"));
        if (manifest is null || manifest.Files.Count == 0)
        {
            var msg = $"暂存目录缺少有效 manifest.json：{staging}";
            UpdaterLog.Write(msg);
            WriteStatus(command, ok: false, hasUpdate: true, remote: string.Empty, staging: staging, message: msg);
            return ExitBadManifest;
        }

        var target = UpdaterPaths.BaseDir;
        var stamp = DateTime.Now.ToString("yyyy.MM.dd.HHmmss");

        // 【2026-09-18 P0 修复】先让常驻组件给更新让路（缘由见 updater/ResidentGate.cs 头注释）：
        //  · 暂停看门狗 —— 否则它会在 8s 宽限后把刚被优雅停掉的 Agent 用**旧 exe** 拉回来，
        //    WaitHostExit 永远等不到"两者都退出"，60s 后以"等待退出超时"收场（更新失败）；
        //  · 停齐 Tray / Watchdog / 桌面服务 —— 否则替换后它们仍以旧二进制运行（新旧混跑，
        //    旧 exe 被占着改名成 .old-* 也删不掉）。
        var watchdogPaused = ResidentGate.PauseWatchdog(out var pauseMessage);
        UpdaterLog.Write(pauseMessage);

        var residents = ResidentGate.StopResidents(out var residentStopMessages);
        foreach (var line in residentStopMessages)
        {
            UpdaterLog.Write(line);
        }

        try
        {
            return ApplyCore(staging, manifest, target, stamp, command, noRestart, residents);
        }
        finally
        {
            // 无论成功/失败都必须恢复守护：漏恢复 = 用户的组件从此不会被自动拉起（比更新失败更糟）。
            if (watchdogPaused)
            {
                ResidentGate.ResumeWatchdog(out var resumeMessage);
                UpdaterLog.Write(resumeMessage);
            }
        }
    }

    /// <summary>`--apply` 主体（替换 + 重启）。抽成独立方法只为把"恢复看门狗守护"放进 finally。</summary>
    private static int ApplyCore(
        string staging,
        ReleaseManifest manifest,
        string target,
        string stamp,
        string command,
        bool noRestart,
        ResidentGate.StoppedSet residents)
    {
        // 1) 常驻组件（Agent）：优先走它自己的优雅停止通道（--stop → 卸载插件 + flush 设置），
        //    强杀会丢掉 SettingsService 末次 debounce 窗口内的改动。
        //    注意只针对**运行自目标目录**的实例（按进程名全局判定会误等别处的 Host/Agent）。
        var agentWasRunning = Applier.IsRunningFrom(Applier.AgentProcessName, target);
        if (agentWasRunning)
        {
            if (!Applier.StopAgentGracefully(target, 15000, out var agentStopError))
            {
                UpdaterLog.Write($"Agent 优雅停止未完成：{agentStopError}（继续等待其退出）");
            }
        }

        // 2) 目标目录的 Host 与 Agent 都必须退出：只等 Host 会让运行中的 Agent.exe/dll 停留在旧版本，
        //    更新后磁盘上是"新 Host/CLI/Tray + 旧 Agent"的混合版本，且其 .old 文件永远删不掉。
        if (!Applier.WaitHostExit(target, 60000, out var waitError))
        {
            UpdaterLog.Write(waitError);
            WriteStatus(command, ok: false, hasUpdate: true, remote: $"{manifest.Version}-{manifest.Build}",
                staging: staging, message: waitError);
            return ExitApplyFailed;
        }

        var backup = Applier.BackupExisting(target, manifest, stamp);

        if (!Applier.ApplyFiles(staging, target, manifest, stamp, out var error, out var applied))
        {
            UpdaterLog.Write($"替换失败，开始回滚：{error}");
            var rolled = Applier.Rollback(target, stamp, out var rollbackError);
            var msg = rolled
                ? $"替换失败，已自动回滚到替换前状态：{error}"
                : $"替换失败，且回滚失败（{rollbackError}）：{error}";
            WriteStatus(command, ok: false, hasUpdate: true, remote: $"{manifest.Version}-{manifest.Build}",
                staging: staging, message: msg);
            return ExitApplyFailed;
        }

        var restarted = "（--no-restart：未自动启动主程序与常驻服务）";
        if (!noRestart)
        {
            // Host 必起；Agent 若更新前在跑则一并恢复——否则新组件永远不会被加载（版本混合）
            var hostOk = Applier.StartHost(target, out var startError);
            var agentOk = !agentWasRunning || Applier.StartAgent(target, out _);
            restarted = hostOk && agentOk
                ? (agentWasRunning ? "（已重启主程序与常驻服务）" : "（已重启主程序）")
                : $"（重启异常：{startError}，可手动启动）";

            // 【2026-09-18】再把托盘 / 桌面服务 / 看门狗按"更新前是否在跑"恢复（看门狗最后起，
            // 起晚了才能看到其它组件都已就位，不会误判"缺失"而重复拉起）。
            ResidentGate.RestartResidents(residents, target, out var residentStartMessages);
            foreach (var line in residentStartMessages)
            {
                UpdaterLog.Write(line);
            }
        }

        var message = $"更新完成：{manifest.Version} build={manifest.Build}"
            + $"（替换 {applied.Count} 个文件，备份 {Path.GetFileName(backup)}，{restarted}）";
        UpdaterLog.Write(message);
        WriteStatus(command, ok: true, hasUpdate: false, remote: $"{manifest.Version}-{manifest.Build}",
            staging: staging, message: message);
        return ExitApplied;
    }

    private static int Rollback(string command, bool noRestart)
    {
        // 【2026-09-18】回滚同样要替换文件 → 也必须先让看门狗让路（理由与 --apply 完全相同）。
        var watchdogPaused = ResidentGate.PauseWatchdog(out var pauseMessage);
        UpdaterLog.Write(pauseMessage);
        try
        {
            return RollbackCore(command, noRestart);
        }
        finally
        {
            if (watchdogPaused)
            {
                ResidentGate.ResumeWatchdog(out var resumeMessage);
                UpdaterLog.Write(resumeMessage);
            }
        }
    }

    /// <summary>`--rollback` 主体（抽成独立方法只为把"恢复看门狗守护"放进 finally）。</summary>
    private static int RollbackCore(string command, bool noRestart)
    {
        var target = UpdaterPaths.BaseDir;
        if (!Applier.WaitHostExit(target, 30000, out var waitError))
        {
            WriteStatus(command, ok: false, hasUpdate: false, remote: string.Empty, staging: string.Empty, message: waitError);
            return ExitApplyFailed;
        }

        var stamp = DateTime.Now.ToString("yyyy.MM.dd.HHmmss");
        if (!Applier.Rollback(target, stamp, out var error))
        {
            UpdaterLog.Write($"回滚失败：{error}");
            WriteStatus(command, ok: false, hasUpdate: false, remote: string.Empty, staging: string.Empty,
                message: $"回滚失败：{error}");
            return ExitApplyFailed;
        }

        var message = "已回滚到上一版本";
        if (!noRestart)
        {
            Applier.StartHost(target, out _);
            message += "并重启主程序";
        }
        UpdaterLog.Write(message);
        WriteStatus(command, ok: true, hasUpdate: false, remote: string.Empty, staging: string.Empty, message: message);
        return ExitRolledBack;
    }

    private static int PrintVersion()
    {
        var local = ReleaseIo.LoadLocalVersion();
        UpdaterLog.Write($"BetterDesktop.Updater {local.Informational}（version={local.Version} build={local.Build}）");
        return ExitOk;
    }

    private static int Help()
    {
        UpdaterLog.Write(string.Join(Environment.NewLine, [
            "BetterDesktop.Updater —— 独立更新器",
            string.Empty,
            "用法：",
            "  BetterDesktop.Updater.exe --check    [--source <目录或URL>[;镜像2]] [--quiet]",
            "  BetterDesktop.Updater.exe --download [--source ...] [--staging <目录>] [--quiet]",
            "  BetterDesktop.Updater.exe --apply    [--staging <目录>] [--quiet]",
            "  BetterDesktop.Updater.exe --rollback [--quiet]",
            "  BetterDesktop.Updater.exe --version",
            string.Empty,
            "退出码：0 已最新 / 10 有更新 / 11 下载完成 / 12 替换完成 / 13 回滚完成",
            "        2 参数错误 / 3 源不可达 / 4 清单无效 / 5 应用失败（已尝试回滚）",
            string.Empty,
            $"更新源配置：{UpdaterPaths.ConfigFile}",
            $"结果状态：  {UpdaterPaths.StatusFile}",
            $"日志：      {UpdaterPaths.LogFile}",
        ]));
        return ExitOk;
    }

    // ---------------- 辅助 ----------------

    private static int FailSource(string command, string message)
    {
        UpdaterLog.Write(message);
        WriteStatus(command, ok: false, hasUpdate: false, remote: string.Empty, staging: string.Empty, message: message);
        return ExitSourceUnavailable;
    }

    private static void WriteStatus(string command, bool ok, bool hasUpdate, string remote, string staging, string message)
    {
        ReleaseIo.SaveJson(UpdaterPaths.StatusFile, new UpdateStatus
        {
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Command = command,
            Ok = ok,
            Local = ReleaseIo.LoadLocalVersion().Informational,
            Remote = remote,
            HasUpdate = hasUpdate,
            Staging = staging,
            Message = message,
        });
    }

    /// <summary>取值：支持 <c>--name value</c> 与 <c>--name=value</c> 两种写法。</summary>
    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) => Array.Exists(args, a => string.Equals(a, name, StringComparison.Ordinal));
}
