// CLI 的"确保 core 在跑"（计划 §6.6 入口层职责）。
//
// 【为什么不需要"先检查再启动"】core 是**单实例**进程（抢命名互斥量，抢不到即静默退出，实测 <100ms）。
// 所以"确保 core 在跑" == 直接执行 core 本身，天然幂等。
// 反过来若先探测再启动，就会引入"探测说没跑、启动时它其实已经在跑"这种自造竞态。
//
// 【为什么放 CLI 而不放 kernel】拉起进程是**生命周期所有权**，必须发生在被架构门禁登记的那一层
// （scripts/manifests/architecture-allowlist.json 的 lifecycle-owner 规则）。kernel 是零依赖契约层，
// 让它拉进程既破坏分层，也会让这条边界从门禁视野里消失。

using System;
using System.Diagnostics;
using System.IO;

namespace BetterDesktop.Cli;

/// <summary>core 可执行体定位与拉起（供 <c>--core</c> 的补救路径使用）。</summary>
internal static class CoreEnsurer
{
    /// <summary>core 可执行体文件名（与 Rust crate 名一致；跨进程契约）。</summary>
    public const string CoreExeName = ComponentPathResolver.CoreExeName;

    /// <summary>
    /// 定位 core：**安装根 → <c>%LOCALAPPDATA%\BetterDesktop</c> → 调用方同目录（仅开发态兜底）**。
    /// </summary>
    /// <remarks>
    /// 【为什么"调用方同目录"必须排最后】（2026-09-19 真机证据，`%TEMP%\bdt-cli.log`）
    ///
    /// 它原先排**第一**，于是开发态的 CLI（<c>BetterDesktop.Cli\bin\{Debug,Release}\</c>）旁边那份
    /// <c>betterdesktop-core.exe</c> 构建副本先被命中 —— 诊断日志实录 4 次：
    /// <code>ensure core: launched …\BetterDesktop.Cli\bin\Release\…\betterdesktop-core.exe</code>
    /// 而那份副本是**旧构建**（445 KB，当日 12:18），于是"确保 core 在跑"拉起的是**旧 core** ✗。
    ///
    /// 调用方目录在**生产**里就等于安装根（两者相同 ⇒ 去重后仍只留一条），所以把安装根判在前
    /// **不损失任何东西**；而在开发态它只是 bin 目录 —— **持久化逻辑不该以它为先**
    /// （与 S4-2 的 `ResolveNativeDllPath` 同一条教训：解析顺序决定"找到的是哪一份"）。
    ///
    /// 【顺序本身已收进共享实现】<see cref="ComponentPathResolver"/> —— 原先这里与
    /// <c>ComponentLocator</c> 各写一份同样的候选链，正是 S4-2 那个"路径解析分散在两处"的病。
    /// 本方法现在只是它的一行门面，**顺序改一处即全改**。
    /// </remarks>
    public static string? Resolve() => ComponentPathResolver.Resolve(CoreExeName);

    /// <summary>
    /// 拉起 core。返回 <c>false</c> = 找不到可执行体或启动失败（**已记入诊断日志**，不弹窗）。
    /// </summary>
    /// <remarks>
    /// 不传任何参数，因此不存在"拼命令行"的注入面（计划 C20）；即便将来要传参，
    /// 也必须走 <c>ProcessStartInfo.ArgumentList</c> 逐个传，不得拼字符串。
    /// </remarks>
    public static bool Ensure()
    {
        var exe = Resolve();
        if (exe is null)
        {
            Diag($"ensure core: {CoreExeName} not found in base dir / install root / data dir");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // 工作目录 = core 自己的目录：core 从同目录读 components.json 与托盘图标
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 不等结果、不接管生命周期：core 是常驻进程，CLI 拉完就退。
            using var process = Process.Start(psi);
            Diag($"ensure core: launched {exe} (pid={process?.Id.ToString() ?? "?"})");
            return process is not null;
        }
        catch (Exception ex)
        {
            Diag($"ensure core: launch failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Diag(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "bdt-cli.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }
        catch
        {
            // 诊断写入失败不阻断
        }
    }
}
