using System.Diagnostics;

namespace BetterDesktop.Updater;

/// <summary>
/// 替换编排：停壳与常驻组件 → 备份 → 落新文件（占用则"改名旧的再落新的"）→ 重启。
///
/// 为什么是这套顺序（都是被实现约束逼出来的）：
///   · 单文件自包含的发布里，正在运行的 exe/dll 无法删除或覆写 —— 但**可以重命名**，
///     于是"rename .old → copy 新文件"是唯一不依赖外部脚本的替换方式；
///   · 本更新器进程自身也在被替换目录里（它正在运行）→ 它自己的旧 exe 只能留到下次启动清理；
///   · **必须同时停 Host 与 Agent**：只等 Host 会让运行中的 Agent.exe/dll 保持旧版本，
///     更新后磁盘上是"新 Host/CLI/Tray + 旧 Agent"的混合版本（且 Agent 的 .old 文件永远删不掉）。
///
/// 回滚契约（禁止半更新状态，**由代码保证而不是靠注释**）：
///   · 替换前把"将被覆盖的文件"备份到 backup-&lt;stamp&gt;；
///   · 清单里**原本不存在**的文件（本次新增，如新组件）写入 _rollback-created.txt；
///   · 回滚时：还原备份 + **删除新增文件** —— 否则回滚后是"旧版本 + 新组件"的半更新状态。
/// </summary>
internal static class Applier
{
    public const string HostProcessName = "BetterDesktop.Host";
    public const string AgentProcessName = "BetterDesktop.Agent";

    /// <summary>备份目录内记录"本次新增文件"的清单文件名（回滚据此删除；前缀下划线避免与真实文件重名）。</summary>
    private const string RollbackMarker = "_rollback-created.txt";

    /// <summary>保留的备份目录份数（多余的删除，避免磁盘长期膨胀）。</summary>
    private const int KeepBackups = 2;

    // ---------------- 进程编队 ----------------

    public static bool IsRunning(string processName)
    {
        try
        {
            var ps = Process.GetProcessesByName(processName);
            try
            {
                return ps.Length > 0;
            }
            finally
            {
                foreach (var p in ps)
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断是否存在**运行自指定目录**的同名进程。
    ///
    /// 为什么不能只按进程名：多安装目录（生产 dist + 开发 bin 并存）是常态，
    /// 按进程名判定会让更新器去等一个**根本不属于本次更新目标**的进程 → 空等 60s 后拒绝更新。
    /// 读不到映像路径时保守按"可能是我们的"处理（宁可多等，也不能在对的进程还在跑时动手替换）。
    /// </summary>
    public static bool IsRunningFrom(string processName, string targetDir)
    {
        try
        {
            var root = Path.GetFullPath(targetDir);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = null;
                    try
                    {
                        path = p.MainModule?.FileName;
                    }
                    catch
                    {
                        // 少数进程读不到映像路径（权限/位数差异）：保守认为可能是目标实例
                    }

                    if (string.IsNullOrEmpty(path))
                    {
                        return true;
                    }

                    if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            return true; // 探测异常保守处理（同上）
        }

        return false;
    }

    /// <summary>列出仍在运行的**目标目录实例**（进程名 + pid + 映像路径），用于超时后给出可操作提示。</summary>
    private static string DescribeRunning(string[] names, string targetDir)
    {
        var found = new List<string>();
        var root = Path.GetFullPath(targetDir);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        foreach (var name in names)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? path = null;
                        try
                        {
                            path = p.MainModule?.FileName;
                        }
                        catch
                        {
                            path = null; // 读不到路径：可能是目标实例，列入
                        }

                        // 别处的实例（开发 bin / 另一份安装）与本目录替换无关，不列入提示
                        if (!string.IsNullOrEmpty(path) &&
                            !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        found.Add($"{name}(pid={p.Id}, {path ?? "路径不可读"})");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
                // 忽略：仅为提示用
            }
        }

        return found.Count == 0 ? "（未探测到目标实例，可能刚退出）" : string.Join("；", found);
    }

    /// <summary>
    /// 通过 Agent 自带的优雅停止通道（`--stop` 命名事件）请求停止：它会卸载插件并 flush 设置。
    /// 比强杀安全——强杀会丢掉 SettingsService 末次 debounce 窗口内的改动。
    /// 只针对**运行自目标目录**的实例；返回后仍应调用 <see cref="WaitHostExit"/> 确认进程真的退出。
    /// </summary>
    public static bool StopAgentGracefully(string targetDir, int timeoutMs, out string error)
    {
        error = string.Empty;
        if (!IsRunningFrom(AgentProcessName, targetDir))
        {
            return true;
        }

        var exe = Path.Combine(targetDir, "BetterDesktop.Agent.exe");
        if (!File.Exists(exe))
        {
            // 目标目录里没有 Agent.exe 却探测到"运行自该目录的 Agent"：几乎不可能，保守放行等待阶段
            error = $"需要在更新前停止 Agent，但目标目录找不到 {exe}（将等待其自行退出）";
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = targetDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--stop" },
            });

            if (p is not null && !p.WaitForExit(timeoutMs))
            {
                error = $"Agent --stop 超时未返回（{timeoutMs}ms）";
                return false;
            }

            UpdaterLog.Write("已向 Agent 发送优雅停止请求（--stop）");
            return true;
        }
        catch (Exception ex)
        {
            error = $"请求 Agent 停止失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 等待**运行自目标目录**的 Host 与 Agent 都退出（替换必须在两者都退出后进行）。
    /// 只等目标目录的实例：别处（开发 bin/另一份安装）的 Host/Agent 与本目录的替换无关，
    /// 不该让更新器空等或拒绝更新。
    /// </summary>
    public static bool WaitHostExit(string targetDir, int timeoutMs, out string error)
    {
        error = string.Empty;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var host = IsRunningFrom(HostProcessName, targetDir);
            var agent = IsRunningFrom(AgentProcessName, targetDir);
            if (!host && !agent)
            {
                UpdaterLog.Write($"目标目录的 Host 与 Agent 均已退出（等待 {sw.ElapsedMilliseconds}ms）");
                return true;
            }

            Thread.Sleep(500);
        }

        error = $"等待退出超时（{timeoutMs}ms），仍在运行：{DescribeRunning([HostProcessName, AgentProcessName], targetDir)}"
            + " —— 请从托盘停止常驻服务并退出主程序后重试（本次未改动任何文件）";
        return false;
    }

    /// <summary>拉起宿主（替换完成后恢复用户界面）。</summary>
    public static bool StartHost(string targetDir, out string error)
        => StartExe(targetDir, "BetterDesktop.Host.exe", "宿主", out error);

    /// <summary>按需恢复常驻宿主（更新前它在运行，更新后必须重启，否则新组件永远不会被加载）。</summary>
    public static bool StartAgent(string targetDir, out string error)
        => StartExe(targetDir, "BetterDesktop.Agent.exe", "常驻服务", out error);

    private static bool StartExe(string targetDir, string fileName, string what, out string error)
    {
        error = string.Empty;
        var exe = Path.Combine(targetDir, fileName);
        if (!File.Exists(exe))
        {
            error = "找不到 " + exe;
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = targetDir,
                UseShellExecute = false,
            });
            UpdaterLog.Write($"已拉起{what}");
            return p is not null;
        }
        catch (Exception ex)
        {
            error = $"拉起{what}失败：{ex.Message}";
            return false;
        }
    }

    // ---------------- 备份 / 替换 / 回滚 ----------------

    /// <summary>
    /// 备份"将被替换"的文件（只备份清单里的文件，避免整目录拷贝把引擎大包也抄一遍），
    /// 并把"清单里原本不存在、本次将新增"的文件登记到 <see cref="RollbackMarker"/>，
    /// 使回滚能删除它们（否则回滚后残留新组件 = 半更新状态）。
    /// </summary>
    public static string BackupExisting(string targetDir, ReleaseManifest manifest, string stamp)
    {
        var backupDir = Path.Combine(targetDir, $"backup-{stamp}");
        Directory.CreateDirectory(backupDir);
        var backedUp = 0;
        var created = new List<string>();

        foreach (var file in manifest.Files)
        {
            var src = ReleaseIo.ResolveWithin(targetDir, file.Path);
            var dst = ReleaseIo.ResolveWithin(backupDir, file.Path);
            if (src is null || dst is null)
            {
                UpdaterLog.Write($"清单路径非法，跳过备份：{file.Path}");
                continue;
            }

            if (!File.Exists(src))
            {
                // 本次新增的文件：无法备份内容，但必须登记，回滚时删除
                created.Add(file.Path);
                continue;
            }

            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                File.Copy(src, dst, overwrite: true);
                backedUp++;
            }
            catch (Exception ex)
            {
                UpdaterLog.Write($"备份失败（{file.Path}）：{ex.Message}（该文件将无法回滚）");
            }
        }

        if (created.Count > 0)
        {
            File.WriteAllLines(Path.Combine(backupDir, RollbackMarker), created);
            UpdaterLog.Write($"登记本次新增文件 {created.Count} 个（回滚时删除）");
        }

        UpdaterLog.Write($"已备份 {backedUp} 个文件 → {backupDir}");
        PruneOldBackups(targetDir, KeepBackups);
        return backupDir;
    }

    /// <summary>把暂存目录的清单文件落到目标目录；返回成功落地的相对路径列表。</summary>
    public static bool ApplyFiles(string stagingDir, string targetDir, ReleaseManifest manifest,
        string stamp, out string error, out List<string> applied)
    {
        error = string.Empty;
        applied = [];

        foreach (var file in manifest.Files)
        {
            // 两侧都做路径越界校验：拒绝绝对路径与 `..`（防被劫持的更新源做任意文件写）
            var src = ReleaseIo.ResolveWithin(stagingDir, file.Path);
            var dst = ReleaseIo.ResolveWithin(targetDir, file.Path);
            if (src is null || dst is null)
            {
                error = $"清单包含非法路径（拒绝写入）：{file.Path}";
                return false;
            }

            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(src))
            {
                error = $"暂存目录缺少文件：{file.Path}";
                return false;
            }

            try
            {
                CopyOver(src, dst, stamp);
                applied.Add(file.Path);
            }
            catch (Exception ex)
            {
                error = $"替换失败（{file.Path}）：{ex.Message}";
                return false;
            }
        }

        UpdaterLog.Write($"已落地 {applied.Count} 个文件 → {targetDir}");
        return true;
    }

    /// <summary>覆盖复制；被占用（正在运行的 exe/dll）则先把旧文件改名为 .old-&lt;stamp&gt; 再落新文件。</summary>
    private static void CopyOver(string src, string dst, string stamp)
    {
        if (!File.Exists(dst))
        {
            File.Copy(src, dst);
            return;
        }

        try
        {
            File.Copy(src, dst, overwrite: true);
            return;
        }
        catch (IOException)
        {
            // 被占用：改名腾位后重试（Windows 允许重命名运行中的文件，只禁止删除）
        }
        catch (UnauthorizedAccessException)
        {
            // 同上（只读/占用的另一种表现）
        }

        var old = $"{dst}.old-{stamp}";
        if (File.Exists(old))
        {
            TryDelete(old);
        }

        File.Move(dst, old);
        File.Copy(src, dst, overwrite: true);
        UpdaterLog.Write($"占用文件已改名替换：{Path.GetFileName(dst)} → {Path.GetFileName(old)}");
    }

    /// <summary>清理上一次替换留下的 .old-*（它们只有在占用进程退出后才能删除，故放在每次启动时做）。</summary>
    public static void CleanupOldArtifacts(string targetDir)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(targetDir, "*.old-*", SearchOption.AllDirectories))
            {
                TryDelete(path);
            }
        }
        catch (Exception ex)
        {
            UpdaterLog.Write($"清理 .old 残留失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 仍被占用：留给下一次启动清理
        }
    }

    /// <summary>
    /// 从最近的备份还原（回滚）：先还原备份文件，再**删除本次新增的文件**。
    /// 少了后一步，回滚后会留下"旧版本 + 新组件"的半更新状态。
    /// </summary>
    public static bool Rollback(string targetDir, string stamp, out string error)
    {
        error = string.Empty;

        var backups = Directory.Exists(targetDir)
            ? Directory.GetDirectories(targetDir, "backup-*").OrderByDescending(d => d).ToList()
            : [];

        if (backups.Count == 0)
        {
            error = $"找不到备份目录（{targetDir}\\backup-*），无法回滚";
            return false;
        }

        var backup = backups[0];
        UpdaterLog.Write($"使用备份回滚：{backup}");
        var restored = 0;
        var removed = 0;

        // 1) 删除本次新增的文件（登记在 marker 里）
        var marker = Path.Combine(backup, RollbackMarker);
        if (File.Exists(marker))
        {
            foreach (var rel in File.ReadAllLines(marker))
            {
                var victim = ReleaseIo.ResolveWithin(targetDir, rel);
                if (victim is null || !File.Exists(victim))
                {
                    continue;
                }

                try
                {
                    File.Delete(victim);
                    removed++;
                }
                catch (Exception ex)
                {
                    error = $"删除新增文件失败（{rel}）：{ex.Message}";
                    return false;
                }
            }

            UpdaterLog.Write($"已删除本次新增的文件 {removed} 个");
        }

        // 2) 还原备份内容（跳过 marker 自身，它不是目标目录的真实文件）
        foreach (var src in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backup, src);
            if (string.Equals(rel, RollbackMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dst = ReleaseIo.ResolveWithin(targetDir, rel);
            if (dst is null)
            {
                UpdaterLog.Write($"备份内路径非法，跳过：{rel}");
                continue;
            }

            try
            {
                CopyOver(src, dst, stamp);
                restored++;
            }
            catch (Exception ex)
            {
                error = $"回滚失败（{rel}）：{ex.Message}";
                return false;
            }
        }

        UpdaterLog.Write($"回滚完成：还原 {restored} 个文件，删除新增 {removed} 个文件");
        return true;
    }

    /// <summary>只保留最近 N 份备份，避免磁盘长期膨胀。</summary>
    private static void PruneOldBackups(string targetDir, int keep)
    {
        try
        {
            var backups = Directory.GetDirectories(targetDir, "backup-*").OrderByDescending(d => d).Skip(keep);
            foreach (var dir in backups)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    UpdaterLog.Write($"已清理旧备份：{Path.GetFileName(dir)}");
                }
                catch
                {
                    // 目录内有被占用的文件：留给下次清理
                }
            }
        }
        catch (Exception ex)
        {
            UpdaterLog.Write($"清理旧备份失败：{ex.Message}");
        }
    }

    /// <summary>暂存根下按 build 命名的目录里，取最近修改的一个（--apply 未显式指定 --staging 时使用）。</summary>
    public static string? FindLatestStaging()
    {
        try
        {
            var root = UpdaterPaths.StagingRoot;
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            UpdaterLog.Write($"查找暂存目录失败：{ex.Message}");
            return null;
        }
    }
}
