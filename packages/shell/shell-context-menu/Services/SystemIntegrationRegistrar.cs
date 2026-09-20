// BetterDesktop.Shell.ContextMenus — 「注册到系统」编排中心（安装器级）
//
// 【它解决什么】安装器级落地前，注册动作散落在四处（桌面服务写快照并顺手注册、Agent 60s 自愈、
//   托盘自启另写一套、A 路只有 dev 脚本能注册），且没有任何地方能一次回答
//   "现在到底注册成什么样了"。本类把"注册 / 修复 / 注销 / 状态"收成一个点，
//   由 CLI（headless）、托盘（经 CLI）、设置分区（显示状态）共用。
//
// 【各段职责（单一真相）】
//   · B 路（经典菜单 COM 扩展）→ ComShellExtensionRegistrar（同包，注册表唯一写者）
//   · 菜单快照 shellmenu.json   → ShellMenuConfigWriter（内容由桌面服务/宿主装配；本类只校验存在性）
//   · 开机自启                  → Kernel.Deployment.AutostartRegistrar（Run + StartupApproved 双写）
//   · A 路（Win11 新菜单稀疏包）→ **只读状态**：注册/注销仍由 scripts/pack-shellmenu-msix.ps1
//     （MakeAppx/SignTool 是 SDK 工具，C# 侧不重写一套；卸载走 PowerShell 的 Remove-AppxPackage）
//
// 【失败语义】全部方法不抛：失败返回 false 且 error 带原因（调用方 = CLI 打印 + 非零退出码）。
//   刻意不把"部分成功"报成成功 —— 半套注册比没注册更难排查（失败不得正常化）。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Deployment;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>系统集成状态快照（一次采齐，供 CLI / 托盘 / 设置中心展示）。</summary>
public sealed record IntegrationStatus
{
    /// <summary>安装根（读 deployment.json；未用安装器安装时为 null）。</summary>
    public string? InstallRoot { get; init; }

    public string? Version { get; init; }

    public string? Build { get; init; }

    /// <summary>A 路注册模式（deployment.json 记录：signed / loose / skipped / unknown）。</summary>
    public string MsixMode { get; init; } = "unknown";

    /// <summary>B 路是否已注册（CLSID 键 + 至少一个场景键存在）。</summary>
    public bool ComRegistered { get; init; }

    /// <summary>当前部署解析到的原生 DLL 路径（可能为 null = 未部署）。</summary>
    public string? ComDllPath { get; init; }

    /// <summary>注册表里记录的 DLL 路径。</summary>
    public string? ComRegisteredPath { get; init; }

    /// <summary>注册路径与当前部署路径不一致（升级换目录/测试残留 → 需要修复）。</summary>
    public bool ComPathDrifted { get; init; }

    /// <summary>
    /// 当前注册是一次 **dev 注册**（<c>BetterDesktopDev.flag</c>）：
    /// 有意指向开发目录，自愈按标记停手，且 <see cref="ComPathDrifted"/> 被强制为 false。
    /// </summary>
    public bool ComDevMode { get; init; }

    /// <summary>菜单快照 shellmenu.json 是否存在（原生侧没有它就不显示任何项）。</summary>
    public bool SnapshotPresent { get; init; }

    public string? SnapshotPath { get; init; }

    /// <summary>托盘开机自启是否**真的会生效**（Run 值存在且未被任务管理器禁用）。</summary>
    public bool TrayAutostart { get; init; }

    public bool WatchdogAutostart { get; init; }

    public bool ShellAutostart { get; init; }

    /// <summary>A 路包是否已注册；null = 查询不可用（WinRT 投影不可用 / 查询异常）。</summary>
    public bool? MsixRegistered { get; init; }

    public string? MsixVersion { get; init; }

    /// <summary>A 路包的实际安装位置（稀疏包 = 外部位置）。</summary>
    public string? MsixLocation { get; init; }

    /// <summary>A 路状态查询失败原因（仅诊断用，不影响 B 路结论）。</summary>
    public string? MsixError { get; init; }
}

/// <summary>「注册到系统」编排：注册 / 修复 / 注销 / 状态（幂等，全部不抛）。</summary>
public static class SystemIntegrationRegistrar
{
    /// <summary>A 路稀疏包包名（AppxManifest.xml 的 Identity/@Name，跨进程契约）。</summary>
    public const string MsixPackageName = "BetterDesktop.ShellMenu";

    /// <summary>托盘可执行文件名（开机自启的指向目标）。</summary>
    public const string TrayExeName = "BetterDesktop.Tray.exe";

    /// <summary>看门狗可执行文件名（卸载时会清它的自启项）。</summary>
    public const string WatchdogExeName = "BetterDesktop.Watchdog.exe";

    /// <summary>
    /// 用户"显式注销右键扩展"的留痕标记文件名（与 host-stopped / agent-stopped / watchdog-pause
    /// 同款约定，落在 %LOCALAPPDATA%\BetterDesktop\ 下）。
    /// <para>
    /// 【为什么必须有】Agent 每 60s 自愈：只要 shellmenu.comExtension 为真就重注册 —— 于是用户从托盘
    /// 点「注销系统右键扩展」后，最多 1 分钟内就被悄悄装回来（审计确认的链路：tray unregister → agent
    /// Register）。有了标记，"用户显式注销"才是持久意图；用户再点「注册到系统」时清除标记，自愈随即恢复。
    /// </para>
    /// </summary>
    public const string UnregisteredFlagFileName = "shellmenu-unregistered.flag";

    /// <summary>注销标记文件完整路径。</summary>
    public static string UnregisteredFlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop",
        UnregisteredFlagFileName);

    /// <summary>用户是否显式注销过右键扩展（Agent 自愈据此停手）。</summary>
    public static bool IsUserUnregistered() => File.Exists(UnregisteredFlagPath);

    /// <summary>采集一次全量状态（任何一项查不到都只反映为该项的取值，不影响其它项）。</summary>
    public static IntegrationStatus GetStatus()
    {
        var hasDeployment = DeploymentInfo.TryRead(out var record, out _);
        var devMode = ComShellExtensionRegistrar.IsDevRegistered();
        var dllPath = ComShellExtensionRegistrar.ResolveNativeDllPath();
        var registeredPath = ComShellExtensionRegistrar.GetRegisteredDllPath();
        var snapshotPath = ShellMenuConfigWriter.GetConfigPath();

        // 【判据用契约的等价规则，不用 string.Equals(OrdinalIgnoreCase)】
        // 后者不归一分隔符，于是 "C:\X/native\a.dll" 与 "C:\X\native\a.dll" 会被判成漂移 ——
        // 而 Rust 侧 same_path 会判等价。两侧结论不一致 = "一侧说漂移、另一侧说没有"，
        // 正是 protocols/native-dll-path-test-vectors.json 要消灭的那类病。
        //
        // 【dev 注册期间永远不报漂移】core / agent 已按标记停手；这里若仍报 true，**启动器**
        // （ComponentBootstrapper，它解析本输出的文本）会照样去 repair —— 等于把刚停下的手伸回去。
        // 空 dllPath（= DllMissing，规则拒绝解析）也不报漂移：它是"没得注册"，不是"注册错了"。
        var drifted = !devMode
            && !string.IsNullOrWhiteSpace(dllPath)
            && !string.IsNullOrWhiteSpace(registeredPath)
            && !NativeDllPath.PathEq(dllPath, registeredPath);

        var msixRegistered = QueryMsix(out var msixVersion, out var msixLocation, out var msixError);

        return new IntegrationStatus
        {
            InstallRoot = hasDeployment ? record!.InstallRoot : null,
            Version = hasDeployment ? record!.Version : null,
            Build = hasDeployment ? record!.Build : null,
            MsixMode = hasDeployment && !string.IsNullOrWhiteSpace(record!.MsixMode) ? record!.MsixMode : "unknown",
            ComRegistered = ComShellExtensionRegistrar.IsRegistered(),
            ComDllPath = dllPath,
            ComRegisteredPath = registeredPath,
            ComPathDrifted = drifted,
            ComDevMode = devMode,
            SnapshotPresent = File.Exists(snapshotPath),
            SnapshotPath = snapshotPath,
            TrayAutostart = AutostartRegistrar.IsEnabled(AutostartRegistrar.TrayValueName),
            WatchdogAutostart = AutostartRegistrar.IsEnabled(AutostartRegistrar.WatchdogValueName),
            ShellAutostart = AutostartRegistrar.IsEnabled(AutostartRegistrar.ShellValueName),
            MsixRegistered = msixRegistered,
            MsixVersion = msixVersion,
            MsixLocation = msixLocation,
            MsixError = msixError,
        };
    }

    /// <summary>注册结果（三态 —— "拒绝"与"失败"必须分开：前者不该重试，也不该被报成故障）。</summary>
    public enum IntegrationOutcome
    {
        /// <summary>成功。</summary>
        Ok,

        /// <summary>按注册表路径规则解析不出可用 DLL ⇒ 拒绝注册（不写任何持久状态）。</summary>
        DllMissing,

        /// <summary>写注册表 / 自启项时出错。</summary>
        Failed,
    }

    /// <summary>
    /// 注册到系统：① B 路 COM 扩展 ② 托盘开机自启（指安装根/同目录的托盘 exe）。
    /// **不写快照**——快照由拥有设置与转换服务的进程（桌面服务/宿主）装配，见文件头职责划分。
    /// </summary>
    /// <param name="devMode">
    /// 显式开发者模式（<c>--dev</c>）：允许注册开发目录的 DLL，并**跳过开机自启登记**。
    /// <para>
    /// 为什么 dev 要跳过自启：dev 的目的是"让我测一下右键菜单"，不是"把我们装进系统"。
    /// 往 <c>Run</c> 键里写一个**开发 bin 路径**，与"不把 dev 路径写进注册表"是同一个错误 ——
    /// 而且它还会在 dev 目录被清掉后变成开机报错。
    /// </para>
    /// </param>
    public static IntegrationOutcome Register(bool devMode, out string? error)
    {
        error = null;

        var com = ComShellExtensionRegistrar.Register(devMode, out var comError);
        if (com != ComShellExtensionRegistrar.RegisterOutcome.Registered)
        {
            error = $"右键扩展注册失败：{comError}";
            return com == ComShellExtensionRegistrar.RegisterOutcome.DllMissing
                ? IntegrationOutcome.DllMissing
                : IntegrationOutcome.Failed;
        }

        if (devMode)
        {
            DiagnosticLog.Trace("shell.context-menu", "DEV 注册：跳过托盘开机自启登记（只改右键扩展）");
            ClearUnregisteredFlag();
            CleanupStaticVerbKeys();
            return IntegrationOutcome.Ok;
        }

        if (!TryRegisterAutostart(AutostartRegistrar.TrayValueName, TrayExeName, required: true, out var autoError))
        {
            error = autoError;
            return IntegrationOutcome.Failed;
        }

        // 看门狗是可选常驻件：存在才登记（缺失不视为失败——托盘自己也会兜底拉起组件）。
        _ = TryRegisterAutostart(AutostartRegistrar.WatchdogValueName, WatchdogExeName, required: false, out _);

        // 顺手清理历史静态 verb（幂等）：形态统一后「剪贴板历史…」「切换到自绘桌面」等
        // 已改为快照开关，旧的静态注册表项必须删掉——否则升级安装后它会指向**已被删除的旧安装目录**，
        // 变成点击报"找不到文件"的死链，而用户看到的是"右键里有两个语义不同的剪贴板项"。
        CleanupStaticVerbKeys();

        // 用户主动注册 → 清除"显式注销"标记，让 Agent 自愈恢复（否则注册完仍被标记压着，
        // 一旦注册表键被外部清掉就再也不会自动修复）。
        ClearUnregisteredFlag();
        return IntegrationOutcome.Ok;
    }

    /// <summary>兼容重载（非 dev）。既有调用方（设置分区 / 测试）无需改。</summary>
    public static bool Register(out string? error) =>
        Register(devMode: false, out error) == IntegrationOutcome.Ok;

    /// <summary>清除"用户显式注销"标记（注册成功后调用）。</summary>
    private static void ClearUnregisteredFlag()
    {
        try
        {
            if (File.Exists(UnregisteredFlagPath))
            {
                File.Delete(UnregisteredFlagPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Trace("shell.context-menu", $"清除注销标记失败: {ex.Message}");
        }
    }

    /// <summary>写下"用户显式注销"标记（Agent 自愈据此停手）。</summary>
    private static void SetUnregisteredFlag()
    {
        try
        {
            var directory = Path.GetDirectoryName(UnregisteredFlagPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(UnregisteredFlagPath, DateTime.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记写不上 → Agent 会在 60s 内把扩展装回来（用户体感是"注销没生效"），必须留痕
            DiagnosticLog.Trace("shell.context-menu", $"写注销标记失败（注销可能被自愈回补）: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理**静态注册表 verb**（不属于 B 路 CLSID 体系，此前无人清理的残留）。
    /// <para>
    /// 这些键由其它进程在运行期写入，卸载/注销时那些进程往往已停止 → 留下指向已删除 CLI 的死链
    /// （用户右键点了报"找不到文件"，比没有菜单项更糟）。
    /// </para>
    /// <para>
    /// 键名必须与写者逐字一致，改一处务必改另一处：
    ///   · <c>Directory\Background\shell\BetterDesktop.ToggleDesktop</c> ← shell-desktop/DesktopSystemMenuRegistrar
    ///   · 四场景 <c>shell\BetterDesktopClipboardHistory</c> ← shell-clipboard/ClipboardShellMenuRegistrar
    ///   · <c>BetterDesktop.Ui</c> / <c>BetterDesktopCompress</c> / <c>BetterDesktopDock</c> ← 历史版本残留
    /// 本类不反向依赖那两个包（依赖方向是它们引用本包），故此处按契约硬编码。
    /// </para>
    /// </summary>
    private static void CleanupStaticVerbKeys()
    {
        string[] clipboardScenes = ["*", "Directory", @"Directory\Background", "DesktopBackground"];
        foreach (var scene in clipboardScenes)
        {
            TryDeleteVerbKey(scene, "BetterDesktopClipboardHistory");
        }

        TryDeleteVerbKey(@"Directory\Background", "BetterDesktop.ToggleDesktop");
        TryDeleteVerbKey(@"Directory\Background", "BetterDesktop.Ui");
        TryDeleteVerbKey(@"Directory", "BetterDesktopDock");
        TryDeleteVerbKey("*", "BetterDesktopDock");
        TryDeleteVerbKey("AllFilesystemObjects", "BetterDesktopDock");
        TryDeleteVerbKey("*", "BetterDesktopCompress");
    }

    private static void TryDeleteVerbKey(string scene, string verbName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scene}\shell", writable: true);
            key?.DeleteSubKeyTree(verbName, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Trace("shell.context-menu", $"清理静态右键项失败 {scene}\\{verbName}: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复：逐项检查状态后重做缺失/漂移的部分（幂等；与 Register 共用同一实现，避免两套语义漂移）。
    /// </summary>
    /// <remarks>
    /// 修复**永远不是 dev**：它是对"状态不对"的自动收敛，而 dev 注册是人的一次性显式动作。
    /// 修复若也走 dev，一次 --dev 之后自愈会把 dev 路径当成目标反复写回去。
    /// </remarks>
    public static bool Repair(out string? error) =>
        Register(devMode: false, out error) == IntegrationOutcome.Ok;

    /// <summary>
    /// 注销。<paramref name="includeAutostart"/> = true 时同时清掉三个自启值（卸载脚本用）；
    /// false 只注销右键扩展与快照（托盘「注销系统右键扩展」用，不动开机自启）。
    /// </summary>
    public static bool Unregister(bool includeAutostart, out string? error)
    {
        error = null;
        var failures = new List<string>();

        // 三个调用都不抛（内部各自吞并记日志），失败面主要体现在自启项（有明确返回值）
        ComShellExtensionRegistrar.Unregister();
        ShellMenuConfigWriter.Remove();
        CleanupStaticVerbKeys();

        // 记下"用户显式注销"：否则 Agent 的 60s 自愈会在 1 分钟内把扩展装回来。
        // （卸载路径 includeAutostart=true 时同样写，无害——那时 Agent 已被停掉。）
        SetUnregisteredFlag();

        if (includeAutostart)
        {
            foreach (var name in AutostartRegistrar.KnownValueNames)
            {
                if (!AutostartRegistrar.Remove(name, out var autoError))
                {
                    failures.Add($"{name}:{autoError}");
                }
            }
        }

        if (failures.Count > 0)
        {
            error = $"部分自启项未能清除：{string.Join("；", failures)}";
            return false;
        }

        return true;
    }

    /// <summary>人读 + 可解析的状态文本（`键=值` 逐行；CLI 直接打印、脚本 / 设置中心共用同一份文案）。</summary>
    public static string Describe(IntegrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var lines = new List<string>
        {
            $"installRoot={status.InstallRoot ?? "(未安装：deployment.json 缺失)"}",
            $"version={status.Version ?? "?"}",
            $"build={status.Build ?? "?"}",
            $"msixMode={status.MsixMode}",
            $"comRegistered={status.ComRegistered}",
            $"comDllPath={status.ComDllPath ?? "(未部署)"}",
            $"comRegisteredPath={status.ComRegisteredPath ?? "(未注册)"}",
            $"comPathDrifted={status.ComPathDrifted}",
            $"comDevMode={status.ComDevMode}",
            $"snapshotPresent={status.SnapshotPresent}",
            $"snapshotPath={status.SnapshotPath}",
            $"autostartTray={status.TrayAutostart}",
            $"autostartWatchdog={status.WatchdogAutostart}",
            $"autostartShell={status.ShellAutostart}",
            $"msixRegistered={(status.MsixRegistered is null ? "unknown" : status.MsixRegistered.Value.ToString())}",
            $"msixVersion={status.MsixVersion ?? "?"}",
            $"msixLocation={status.MsixLocation ?? "?"}",
        };

        if (!string.IsNullOrWhiteSpace(status.MsixError))
        {
            lines.Add($"msixError={status.MsixError}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>登记一个自启项：exe 不存在时（required=false）静默跳过，绝不写"指向不存在路径"的键。</summary>
    private static bool TryRegisterAutostart(string valueName, string exeName, bool required, out string? error)
    {
        error = null;
        var exe = ResolveSibling(exeName);
        if (exe is null)
        {
            if (required)
            {
                error = $"未找到 {exeName}（同目录与安装根都没有）→ {valueName} 自启未登记";
                return false;
            }

            return false;
        }

        if (!AutostartRegistrar.Set(valueName, exe, out var setError))
        {
            error = $"{valueName} 自启登记失败：{setError}";
            return false;
        }

        return true;
    }

    /// <summary>同目录优先，其次安装根（与 DesktopControlLocator / watchdog 同一查找次序）。</summary>
    private static string? ResolveSibling(string exeName)
    {
        var sameDir = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(sameDir))
        {
            return sameDir;
        }

        var root = DeploymentInfo.ResolveInstallRoot();
        if (root is not null)
        {
            var installed = Path.Combine(root, exeName);
            if (File.Exists(installed))
            {
                return installed;
            }
        }

        return null;
    }

    /// <summary>
    /// 查 A 路稀疏包状态（只读）：true = 已注册 / false = 未注册 / null = 查询不可用（error 带原因）。
    /// 查询不可用只影响"状态显示为 unknown"，**不阻塞**注册/卸载（那两条走 PowerShell 的 Appx 命令）。
    /// </summary>
    private static bool? QueryMsix(out string? version, out string? location, out string? error)
    {
        version = null;
        location = null;
        error = null;
        try
        {
            // global:: 前缀：避免本项目命名空间链里的同名段遮蔽全局 Windows 命名空间（本仓已踩过 CS0234）
            var manager = new global::Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                var id = package.Id;
                if (!string.Equals(id.Name, MsixPackageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}";
                try
                {
                    location = package.InstalledLocation?.Path;
                }
                catch
                {
                    location = null; // 位置读不到不影响"已注册"这个结论
                }

                return true;
            }

            return false; // 查到"没有"= 未注册（不是错误）
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null; // 查询机制不可用：状态显示 unknown，不冒充"未注册"
        }
    }
}
