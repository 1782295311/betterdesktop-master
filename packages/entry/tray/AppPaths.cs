namespace BetterDesktop.Tray;

/// <summary>
/// 组件路径与数据目录解析（唯一路径来源，禁止在别处写死绝对路径）。
/// 部署约定：Tray / Host / CLI / Updater / Watchdog / Recovery 同目录，
/// 因此一切以 <see cref="AppContext.BaseDirectory"/> 为基准。
/// 2026-09-17 安装器级：再叠一层"当前安装根"（deployment.json）——
/// 配置中心与更新器会把组件装到 %LOCALAPPDATA%\BetterDesktop\app\&lt;build&gt;，
/// 此时同目录仍是权威（生产就是把它们放一起），安装根只用于"调用方不在安装目录里"的场景。
/// </summary>
internal static class AppPaths
{
    /// <summary>组件所在目录（exe 所在目录）。</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>组件实际所在目录：同目录有 Host 就用同目录，否则回退安装根（deployment.json）。</summary>
    public static string ComponentsDir
    {
        get
        {
            if (File.Exists(Path.Combine(BaseDir, "BetterDesktop.Host.exe")))
            {
                return BaseDir;
            }

            var root = InstallRoot();
            return root.Length > 0 ? root : BaseDir;
        }
    }

    /// <summary>
    /// 读 %LOCALAPPDATA%\BetterDesktop\deployment.json 的 installRoot。
    /// 托盘是**零包引用**工程（见 csproj 头注释），故此处复刻 Kernel.Deployment.DeploymentInfo
    /// 的同一份契约（watchdog/Program.cs 也复刻了一段，三处字面量必须一致）；失败返回空串、不抛。
    /// </summary>
    public static string InstallRoot()
    {
        try
        {
            var pointer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop", "deployment.json");
            if (!File.Exists(pointer))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(pointer);
            var match = System.Text.RegularExpressions.Regex.Match(
                text, "\"installRoot\"\\s*:\\s*\"(?<p>(\\\\.|[^\"\\\\])*)\"");
            if (!match.Success)
            {
                return string.Empty;
            }

            var raw = match.Groups["p"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            return Directory.Exists(raw) ? raw : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string HostExe => Path.Combine(ComponentsDir, "BetterDesktop.Host.exe");

    public static string CliExe => Path.Combine(ComponentsDir, "BetterDesktop.Cli.exe");

    /// <summary>
    /// 桌面服务（自绘桌面 + 桌面控制菜单 + 原生双击钩子，2026-09-17 独立化）——
    /// **不依赖主程序**：这是"桌面控制/自绘桌面像剪贴板历史一样独立"的落点。
    /// 定位顺序必须与 packages/shell/shell-core/DesktopControl/DesktopControlLocator.cs 一致
    /// （托盘是零包引用的 WinForms 保活进程，故此处复刻这段查找；**改一处务必改另一处**）：
    ///   ① 组件目录（同目录或安装根）→ ② %LOCALAPPDATA%\BetterDesktop → ③ %LOCALAPPDATA%\BetterDesktop\DesktopControl。
    /// </summary>
    public static string DesktopServiceExe
    {
        get
        {
            var sameDir = Path.Combine(ComponentsDir, "BetterDesktop.DesktopControl.exe");
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var deployed = Path.Combine(local, "BetterDesktop", "BetterDesktop.DesktopControl.exe");
            if (File.Exists(deployed))
            {
                return deployed;
            }

            return Path.Combine(local, "BetterDesktop", "DesktopControl", "BetterDesktop.DesktopControl.exe");
        }
    }

    /// <summary>
    /// 独立设置进程（2026-09-17）：设置中心是"管理常驻功能"的控制台，**不依赖宿主**——
    /// 托盘直接拉起它，不再"为了看一眼设置先把整个壳（≈125MB）拉起来"。关窗即退。
    /// </summary>
    public static string SettingsExe => Path.Combine(ComponentsDir, "BetterDesktop.Settings.exe");
    public static string UpdaterExe => Path.Combine(ComponentsDir, "BetterDesktop.Updater.exe");
    public static string RecoveryExe => Path.Combine(ComponentsDir, "BetterDesktop.Recovery.exe");

    /// <summary>
    /// 剪贴板历史面板（独立 exe）：定位顺序与 <see cref="DesktopServiceExe"/> 同款 ——
    /// 组件目录 → %LOCALAPPDATA%\BetterDesktop（deploy-clipboard.ps1 的部署位置）。
    /// 托盘「打开剪贴板历史」优先直连它（`--open` 唤醒已运行实例）。
    /// </summary>
    public static string ClipboardPanelExe
    {
        get
        {
            var sameDir = Path.Combine(ComponentsDir, "BetterDesktop.Clipboard.Panel.exe");
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "BetterDesktop", "BetterDesktop.Clipboard.Panel.exe");
        }
    }

    /// <summary>
    /// 更新源配置（与 Updater 同目录，用同款"同目录优先、安装根回退"解析）。
    /// 缺失时 updater 必然以"更新源不可达"失败，故托盘据此提示而不是让用户白点一次。
    /// </summary>
    public static string UpdateConfigFile => Path.Combine(ComponentsDir, "update.config.json");

    /// <summary>
    /// 卸载脚本（由 publish.ps1 随包分发到组件目录）：注销右键扩展与稀疏包、清自启、
    /// 还原 explorer 任务栏/图标、删除程序文件。托盘「卸载 BetterDesktop…」入口用它 ——
    /// 卸载必须能在"托盘自己也在被删的目录里"的情况下完成，所以是本进程之外的脚本在做。
    /// </summary>
    public static string UninstallScript => Path.Combine(ComponentsDir, "uninstall-betterdesktop.ps1");

    /// <summary>settings.json：与主程序同一份（%APPDATA%\BetterDesktop\settings.json）。</summary>
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterDesktop",
        "settings.json");

    /// <summary>日志目录：与宿主 FileLogSink 同目录（%LOCALAPPDATA%\BetterDesktop\logs）。</summary>
    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop",
        "logs");

    /// <summary>托盘自身日志（按日期滚动，与宿主 host-yyyyMMdd.log 并列）。</summary>
    public static string TrayLogFile => Path.Combine(LogDir, $"tray-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// 更新状态文件：由 BetterDesktop.Updater.exe 写、托盘读（气泡反馈）。
    /// 用它而不是 IPC —— 更新器可能在不同时刻由不同入口拉起，文件是唯一不会丢结果的信道。
    /// </summary>
    public static string UpdateStatusFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop",
        "update-status.json");
}
