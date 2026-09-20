using BetterDesktop.Diagnostics;

namespace BetterDesktop.Updater;

/// <summary>更新器路径契约（部署目录 = 组件所在目录；数据目录与宿主/托盘一致）。</summary>
internal static class UpdaterPaths
{
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>本次安装的版本描述（由 scripts/publish.ps1 生成，禁止手改）。</summary>
    public static string LocalVersionFile => Path.Combine(BaseDir, "version.json");

    /// <summary>本次安装的清单（随发布一起分发，用于本地自校验/回滚）。</summary>
    public static string LocalManifestFile => Path.Combine(BaseDir, "manifest.json");

    /// <summary>更新源配置（本地运维文件，可不随发布分发）：{ "source": "https://... 或 \\share\dir" }。</summary>
    public static string ConfigFile => Path.Combine(BaseDir, "update.config.json");

    public static string HostExe => Path.Combine(BaseDir, "BetterDesktop.Host.exe");

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDesktop");

    /// <summary>更新结果状态文件：托盘读它弹气泡（与控制面解耦，不需要 IPC）。</summary>
    public static string StatusFile => Path.Combine(DataDir, "update-status.json");

    public static string LogDir => DiagnosticLogger.DefaultDirectory;

    /// <summary>当前日志文件路径（供诊断/排障显示）。</summary>
    public static string LogFile => Path.Combine(LogDir, $"updater-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>下载暂存根（按 build 分子目录，便于多版本并存）。</summary>
    public static string StagingRoot => Path.Combine(Path.GetTempPath(), "BetterDesktop.Update");
}

/// <summary>
/// 更新器日志：委托给跨进程统一日志器（异步入队 / 有界 / 滚动 / 保留治理）。
/// 更新器是短命进程，退出前必须 Flush，否则最后几条（往往就是失败原因）会丢。
/// </summary>
internal static class UpdaterLog
{
    private static readonly DiagnosticLogger Logger = DiagnosticLogger.Start("updater", DiagnosticLogger.ResolveLevel());

    /// <summary>--quiet 时只写文件不打控制台（托盘拉起时不闪窗、不弹控制台）。</summary>
    public static bool Quiet { get; set; }

    public static void Write(string message)
    {
        if (!Quiet)
        {
            Console.WriteLine(message);
        }

        Logger.Info(message);
    }

    public static void Warn(string message)
    {
        if (!Quiet)
        {
            Console.WriteLine("[WARN] " + message);
        }

        Logger.Warn(message);
    }

    public static void Error(string what, Exception ex)
    {
        if (!Quiet)
        {
            Console.WriteLine($"[ERROR] {what}: {ex.Message}");
        }

        Logger.Error(what, ex);
    }

    /// <summary>写启动横幅（版本 / 系统 / 命令行），实地测试时用于快速定位环境。</summary>
    public static void Banner() => Logger.Banner("BetterDesktop.Updater 启动", DiagnosticLogger.EnvironmentInfo());

    /// <summary>同步刷盘（退出/崩溃路径）。</summary>
    public static void Flush() => Logger.Flush();
}
