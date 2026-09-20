using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;
using BetterDesktop.Shell.Core.DesktopControl;

namespace BetterDesktop.Cli;

/// <summary>headless 动作分类（纯逻辑，可单测；Run 的分派依据）。</summary>
public enum HeadlessActionKind
{
    Unknown,
    NeedsHost,
    ConvertTo,
    Compress,
    Unzip,

    /// <summary>弹「桌面控制」菜单（[2026-09-17] 转交独立进程 BetterDesktop.DesktopControl.exe，**不需要宿主在线**）。</summary>
    DesktopControls,
}

/// <summary>
/// 无宿主 headless 执行器（M3.1）：CLI 直执行转换/压缩/解压动作，干完退出。
/// 反馈从简（"只要工作就行"）：成功静默（输出文件即结果）；失败原生 MessageBox + 分层退出码。
/// 动作路由 = 统一注册体系的 Action 标识（与自绘 MenuItemDef.Action / 注册表 verb 同源）。
/// </summary>
public static class HeadlessExecutor
{
    /// <summary>需宿主完整在线的动作白名单（CLI 一律提示，不拉起静默宿主——用户拍板 2026-09-10）。</summary>
    private static readonly HashSet<string> NeedsHostActions = new(StringComparer.Ordinal)
    {
        "clipboard-history", "open-settings", "dock-pin", "convert", "convert-more",
    };

    /// <summary>动作分类（纯逻辑：不弹框、不执行，供路由与单测）。</summary>
    public static HeadlessActionKind Classify(string action)
    {
        if (NeedsHostActions.Contains(action))
        {
            return HeadlessActionKind.NeedsHost;
        }

        if (action.StartsWith("convert-to-", StringComparison.Ordinal))
        {
            return HeadlessActionKind.ConvertTo;
        }

        return action switch
        {
            "compress-zip" or "compress-7z" or "compress-rar" => HeadlessActionKind.Compress,
            "unzip-here" or "unzip-to" => HeadlessActionKind.Unzip,
            // 【2026-09-17 桌面控制独立化】「桌面控制」不再要求宿主在线：转交独立进程弹菜单 + 执行动作。
            // 旧行为（`Bootstrap case "desktop-controls"` → 宿主内自绘菜单）只在宿主在线的转发路径上还留着。
            "desktop-controls" => HeadlessActionKind.DesktopControls,
            _ => HeadlessActionKind.Unknown,
        };
    }

    /// <summary>转换错误 → 退出码映射（纯逻辑：EngineMissing 与文件错误分层，禁止伪装成转换失败）。</summary>
    public static int MapConvertError(ConvertError error) =>
        error == ConvertError.EngineMissing ? ExitCodes.EngineMissing : ExitCodes.Failed;

    // =====================================================================
    // 批文件入口（原生右键扩展 → CLI 的唯一数据契约）
    // =====================================================================
    //
    // 为什么走批文件而不是命令行：多选时路径数量/长度不可控（命令行 32767 上限），
    // 且含空格/引号/Unicode 的路径拼命令行极易转义出错。原生侧只落一份 UTF-8 JSON，
    // 命令行里只有一个被引号包住的路径（见 native/include/Launcher.h）。
    //
    // 协议版本独立于 --menu-cmd：version 不匹配一律拒绝，避免"旧原生 DLL + 新 CLI"静默错行为。

    /// <summary>批文件协议版本（与 native/Launcher.cpp 的 BuildBatchJson 保持一致）。</summary>
    public const int BatchProtocolVersion = 1;

    /// <summary>单次批处理的路径上限（防御异常/被篡改的输入）。</summary>
    public const int MaxBatchPaths = 4096;

    /// <summary>
    /// 测试缝：抑制一切用户可见反馈（通知气泡 / 原生弹框）。仅供单测设置。
    /// 生产代码不得改写本属性。
    /// </summary>
    internal static bool SuppressUserFeedback { get; set; }

    /// <summary>批处理动作分类（纯逻辑，可单测）。</summary>
    public enum BatchActionKind
    {
        /// <summary>无法识别（参数错误）。</summary>
        Unknown,

        /// <summary>格式转换：args[0] = 目标格式。</summary>
        ConvertTo,

        /// <summary>UI 开关翻转：args[0] = 开关键。</summary>
        ToggleKey,

        /// <summary>弹「桌面控制」菜单（[2026-09-17] 转交独立进程；不需要宿主在线）。</summary>
        OpenDesktopControls,

        /// <summary>
        /// 归档压缩（`compress-zip` / `compress-7z` / `compress-rar`）：paths 为选中的文件与/或文件夹。
        /// 【2026-09-17 新增】此前批协议白名单只有 convert-to / toggle-key / desktop-controls，
        /// 于是"文件夹右键"场景即使注册了也没有任何可挂的项（Directory 场景恒空）。
        /// </summary>
        Compress,

        /// <summary>
        /// 切换自绘桌面（`toggle-desktop`）→ CLI 顶级 <c>--toggle-desktop</c>。
        /// 【2026-09-17 新增】原先是独立的静态注册表项（免宿主直写设置），
        /// 现并入快照的「桌面控制」开关组，与其它开关同一形态（图标 + 勾选态）。
        /// </summary>
        ToggleDesktop,
    }

    /// <summary>批文件请求（原生侧落盘、CLI 侧解析）。</summary>
    public sealed record MenuBatchRequest(string Action, IReadOnlyList<string> Args, IReadOnlyList<string> Paths);

    /// <summary>动作分类（纯函数；Batch 专用动作名与 --menu-cmd 的单文件动作名刻意不同）。</summary>
    public static BatchActionKind ClassifyBatch(string action) => action switch
    {
        "convert-to" => BatchActionKind.ConvertTo,
        "toggle-key" => BatchActionKind.ToggleKey,
        "compress-zip" or "compress-7z" or "compress-rar" => BatchActionKind.Compress,
        "toggle-desktop" => BatchActionKind.ToggleDesktop,
        // 【2026-09-17 桌面控制独立化】原生扩展的「桌面控制」入口现在是一个**叶子命令**（无子项），
        // 点一下派发到这里 → 由独立进程现算勾选态/置灰并自己执行（见 ShellMenuContentBuilder.BuildDesktopControls）。
        "desktop-controls" => BatchActionKind.OpenDesktopControls,
        _ => BatchActionKind.Unknown,
    };

    /// <summary>
    /// 解析批文件 JSON（纯逻辑，可单测）。失败返回 false 并给出中文原因（供日志/提示）。
    /// 校验：协议版本、action 非空、paths 上限；**不校验路径是否存在**（那属于业务失败，走错误码分层）。
    /// </summary>
    public static bool TryParseBatch(string json, out MenuBatchRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "批文件为空";
            return false;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "批文件根节点不是对象";
                return false;
            }

            var version = root["version"]?.GetValue<int>() ?? BatchProtocolVersion;
            if (version != BatchProtocolVersion)
            {
                error = $"批文件协议版本不支持：{version}（本 CLI 支持 {BatchProtocolVersion}）";
                return false;
            }

            var action = root["action"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(action))
            {
                error = "批文件缺少 action";
                return false;
            }

            var args = new List<string>();
            if (root["args"] is JsonArray argsNode)
            {
                foreach (var node in argsNode)
                {
                    args.Add(node?.GetValue<string>() ?? string.Empty);
                }
            }

            var paths = new List<string>();
            if (root["paths"] is JsonArray pathsNode)
            {
                foreach (var node in pathsNode)
                {
                    var path = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            if (paths.Count > MaxBatchPaths)
            {
                error = $"批文件路径数超限：{paths.Count} > {MaxBatchPaths}";
                return false;
            }

            request = new MenuBatchRequest(action, args, paths);
            return true;
        }
        catch (Exception ex)
        {
            error = $"批文件解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 批文件入口：读取 → 解析 → 执行 → **删除批文件**（成功失败都删，避免 %TEMP% 堆积）。
    /// 不走命名管道转发：批路径本就是"免宿主"设计，转发只会平白多等一次 1.5s 连接超时。
    /// </summary>
    public static int RunBatch(string batchFilePath)
    {
        if (string.IsNullOrWhiteSpace(batchFilePath) || !File.Exists(batchFilePath))
        {
            Diag($"批文件不存在: {batchFilePath}");
            ShowError($"批文件不存在：{batchFilePath}");
            return ExitCodes.Usage;
        }

        try
        {
            string json;
            try
            {
                json = File.ReadAllText(batchFilePath);
            }
            catch (Exception ex)
            {
                Diag($"批文件读取失败: {ex.Message}");
                ShowError($"批文件读取失败：{ex.Message}");
                return ExitCodes.Usage;
            }
            finally
            {
                // 读后即删（尽力而为：删除失败不影响执行结果）
                try { File.Delete(batchFilePath); } catch { /* 忽略 */ }
            }

            if (!TryParseBatch(json, out var request, out var error) || request is null)
            {
                Diag($"批文件解析失败: {error}");
                ShowError(error ?? "批文件解析失败");
                return ExitCodes.Usage;
            }

            Diag($"batch action={request.Action} args={string.Join(',', request.Args)} paths={request.Paths.Count}");

            switch (ClassifyBatch(request.Action))
            {
                case BatchActionKind.ConvertTo:
                    if (request.Args.Count == 0 || string.IsNullOrWhiteSpace(request.Args[0]))
                    {
                        ShowError("格式转换缺少目标格式参数");
                        return ExitCodes.Usage;
                    }
                    if (request.Paths.Count == 0)
                    {
                        ShowError("格式转换没有选中任何文件");
                        return ExitCodes.Usage;
                    }
                    return ConvertTo(request.Paths, request.Args[0]);

                case BatchActionKind.ToggleKey:
                    if (request.Args.Count == 0)
                    {
                        ShowError("开关动作缺少键名");
                        return ExitCodes.Usage;
                    }
                    return ToggleKey(request.Args[0]);

                case BatchActionKind.Compress:
                    if (request.Paths.Count == 0)
                    {
                        ShowError("压缩没有选中任何项");
                        return ExitCodes.Usage;
                    }
                    return ArchiveCore(request.Paths, request.Action);

                case BatchActionKind.ToggleDesktop:
                    // 不吃 paths：桌面空白右键没有选中对象，语义就是"翻转自绘桌面开关"。
                    return ToggleDesktop();

                case BatchActionKind.OpenDesktopControls:
                    // 不吃 paths：桌面空白右键没有选中对象（多选也该忽略），动作语义就是"弹菜单"。
                    return LaunchDesktopControls();

                default:
                    Diag($"批文件未知动作: {request.Action}");
                    ShowError($"未知的 BetterDesktop 命令：{request.Action}");
                    return ExitCodes.Usage;
            }
        }
        catch (Exception ex)
        {
            Diag($"批处理异常: {ex.Message}");
            ShowError($"批处理异常：{ex.Message}");
            return ExitCodes.Failed;
        }
    }

    public static int Run(string action, string path)
    {
        switch (Classify(action))
        {
            case HeadlessActionKind.NeedsHost:
                Diag($"动作需宿主完整在线: {action}");
                NativeMessageBox.ShowInfo("该功能需要 BetterDesktop 正在运行。\n请先启动 BetterDesktop 后再试。");
                return ExitCodes.NeedsHost;
            case HeadlessActionKind.ConvertTo:
                return ConvertTo(path, action["convert-to-".Length..]);
            case HeadlessActionKind.Compress:
            case HeadlessActionKind.Unzip:
                return Archive(path, action);
            case HeadlessActionKind.DesktopControls:
                return LaunchDesktopControls();
            default:
                return Unknown(action);
        }
    }

    // ===== 设置直写（--toggle-desktop / --toggle-key 无宿主场景；与 host ToggleKeyCommand/
    // DesktopToggleCommand 同款逻辑——只读写 settings.json，不引 UI；O2 assumed: CLI 内轻量实现） =====

    /// <summary>settings.json 绝对路径（与宿主一致：%APPDATA%\BetterDesktop\settings.json）。</summary>
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterDesktop",
        "settings.json");

    /// <summary>切换自绘桌面：翻转 components.desktop；翻到"开"= 用户明确要启用桌面外壳 → 拉完整宿主承载（非静默装配）。</summary>
    public static int ToggleDesktop()
    {
        var path = SettingsPath;
        var current = true;
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (JsonNode.Parse(text) is JsonObject root &&
                    root["components.desktop"] is JsonValue v &&
                    v.TryGetValue<bool>(out var b))
                {
                    current = b;
                }
            }
        }
        catch (Exception ex)
        {
            Diag($"读取设置失败（按默认处理）: {ex.Message}");
        }

        var next = !current;

        // 【防清空】文件存在但读取失败/非对象 → 放弃本次切换，绝不重建空对象覆盖（会丢全部其他键）。
        try
        {
            var root = new JsonObject();
            if (File.Exists(path))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed)
                    {
                        Diag("切换自绘桌面取消：settings.json 非有效对象，不覆盖以免清空配置");
                        return ExitCodes.Failed;
                    }
                    root = parsed;
                }
                catch (Exception ex)
                {
                    Diag($"切换自绘桌面取消：读取设置失败，不覆盖原文件: {ex.Message}");
                    return ExitCodes.Failed;
                }
            }
            root["components.desktop"] = next;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Diag($"切换自绘桌面(无实例直写): components.desktop={next}");
        }
        catch (Exception ex)
        {
            Diag($"写入设置失败: {ex.Message}");
            return ExitCodes.Failed;
        }

        if (next)
        {
            // 【M2 2026-09-17】自绘桌面层已搬到独立进程：翻到"开"要拉起的是**桌面服务**，不是整个壳
            //（用户定调：自绘桌面不需要主程序；壳只在用户要菜单栏/Dock 时才付内存）。
            LaunchDesktopService();
        }
        return ExitCodes.Ok;
    }

    /// <summary>
    /// 切换「桌面控制」开关（icons / taskbar / doubleclick / menubar / dock / hotkey-panel）。
    /// <para>
    /// 【2026-09-17 宿主缺席也要真的生效】旧行为只翻转 settings.json —— 而写盘的消费者（宿主）根本不在，
    /// 用户实测原话"点了没反应"。走到本方法即说明宿主缺席（宿主在线时命令行/管道已先转发走掉了），
    /// 因此这里对**能被 explorer 原生层直接生效**的开关（图标 / 任务栏）额外当场生效。
    /// </para>
    /// <para>键名 / 默认值 / 可执行性 = <see cref="DesktopToggleCatalog"/> 单点
    /// （此前宿主 / CLI / Host ToggleKeyCommand 各持一份映射，已漂移过一次，注释自陈）。</para>
    /// </summary>
    public static int ToggleKey(string cmdKey)
    {
        if (!DesktopToggleCatalog.TryGet(cmdKey, out var spec))
        {
            Diag($"未知 toggle-key: {cmdKey}");
            return ExitCodes.Usage;
        }

        // 【2026-09-18 真机：右键菜单里的开关点了没反应（用户点名"热键侧板"）】
        // 本方法同时被 --menu-batch 调用，而 batch 通道是**刻意免宿主**的（原生多选入口不走管道，
        // 见 Program.cs 里 --menu-batch 分支的注释）→ 于是下面那段"无实例直写"的隐含前提
        //（"宿主在线时命令行早已转发走掉了"）**在 batch 路径上不成立**：
        // 只写 settings.json，而 SettingsService 没有文件监视（无 FileSystemWatcher），
        // **正在运行的宿主不会重载** → 热键侧板 / 灵动岛 / 菜单栏 / Dock 这些"宿主内消费"的键
        // 点了就是没反应（只有图标 / 任务栏这类"原生层当场生效"的键看起来正常，极具迷惑性）。
        // 修法：先尝试转交宿主（在线则它进程内翻转 + 广播 SettingsChanged → 插件立即响应；
        // TrySend 自带 1.5s 连接超时，无实例时只是多等一次连接失败），失败再回退本地直写 —— 与旧行为一致。
        if (MenuCommandPipeClient.TrySend("toggle-key", cmdKey))
        {
            Diag($"切换UI(经宿主管道): {cmdKey}");
            return ExitCodes.Ok;
        }

        // 【M2 2026-09-17】桌面自有的开关键（桌面图标显隐 / 双击隐藏图标）**必须由桌面服务进程翻**：
        // 自绘图标网格读的是它自己内存里的设置快照，外部进程写盘它不会重载 → 本地直写 = 用户看着"没反应"。
        // 转交方式：拉起桌面服务 exe 的 --toggle-key（有常驻服务就转发给它进程内翻；没有则它自己本地直写+原生生效）。
        if (spec.Name is DesktopToggleCatalog.Icons or DesktopToggleCatalog.DoubleClick &&
            LaunchDesktopServiceToggle(cmdKey))
        {
            return ExitCodes.Ok;
        }

        var path = SettingsPath;
        var current = spec.Default;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root &&
                root[spec.SettingsKey] is JsonValue v && v.TryGetValue<bool>(out var b))
            {
                current = b;
            }
        }
        catch (Exception ex)
        {
            Diag($"读取设置失败（按默认处理）: {ex.Message}");
        }

        var next = !current;
        try
        {
            var root = new JsonObject();
            if (File.Exists(path))
            {
                // 【防清空】与 ToggleDesktop 同款纪律：文件存在但解析失败/非对象 → 放弃本次切换，
                // 绝不重建空对象覆盖（否则一次文件损坏就清空用户全部设置）。
                // 解析异常会落到下方 catch → 返回 Failed，同样不写文件。
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed)
                {
                    Diag("切换UI取消：settings.json 非有效对象，不覆盖以免清空配置");
                    return ExitCodes.Failed;
                }

                root = parsed;
            }
            root[spec.SettingsKey] = next;

            // 显式留痕：让「桌面控制」的选择压过其它功能的默认隐藏（如 dock 默认隐藏原生任务栏）。
            foreach (var (overrideKey, overrideValue) in DesktopToggleCatalog.ExplicitOverrides(spec, next))
            {
                root[overrideKey] = overrideValue;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Diag($"切换UI(无实例直写): {spec.SettingsKey}={next}");

            // 【2026-09-17】免宿主当场生效：原生层（图标 / 任务栏）。失败不改变退出码——
            // 设置已落盘，宿主上线后仍会归位；这里只是"让用户立刻看到效果"。
            if (spec.NativeEffective)
            {
                var applied = DesktopControlNative.Apply(spec.Name, next);
                Diag($"原生层立即生效: {spec.Name} → {next}（applied={applied}）");
            }

            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            Diag($"写入设置失败: {ex.Message}");
            return ExitCodes.Failed;
        }
    }

    /// <summary>
    /// 弹「桌面控制」菜单（[2026-09-17 独立化]）：**转交独立进程** BetterDesktop.DesktopControl.exe。
    /// <para>
    /// 为什么不在这里自己弹：CLI 是控制台入口，绝不建 WPF 窗口/Dispatcher（文件头纪律），
    /// 而且菜单需要"现算勾选态 + 现判宿主在线 + 自己执行动作"，那正是独立进程的职责。
    /// </para>
    /// <para>
    /// 兜底顺序：独立进程 → 老部署（无该 exe）退回宿主命令桥 → 都没有则按"需要宿主"提示，
    /// 绝不静默什么都不做（静默失败正是本次要修的问题）。
    /// </para>
    /// </summary>
    public static int LaunchDesktopControls()
    {
        var exe = DesktopControlLocator.Find();
        if (exe is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    // 菜单进程自带 WPF 窗口，不能 CreateNoWindow；也不要 WaitForExit（等它 = 等用户点完菜单）。
                };
                // 【必须显式传参】DesktopControl 无参时走"常驻桌面服务"分支（DesktopControlEntry:133-137），
                // 只有带 --desktop-controls 才弹菜单。漏传的表现是：用户点「桌面控制」什么都不出来，
                // 还顺带起了个后台常驻服务。
                psi.ArgumentList.Add("--desktop-controls");
                Process.Start(psi);
                Diag("已拉起独立进程 BetterDesktop.DesktopControl.exe --desktop-controls");
                return ExitCodes.Ok;
            }
            catch (Exception ex)
            {
                Diag($"拉起桌面控制进程失败: {ex.Message}");
            }
        }
        else
        {
            Diag("未部署 BetterDesktop.DesktopControl.exe，回退宿主命令桥");
        }

        // 老部署回退：宿主在线则由它弹（进程内同一份菜单实现）。
        if (MenuCommandPipeClient.TrySend("desktop-controls", string.Empty))
        {
            Diag("已转交宿主（回退路径）");
            return ExitCodes.Ok;
        }

        Diag("桌面控制不可用：独立进程未部署且宿主未运行");
        ShowError("未找到「桌面控制」组件（BetterDesktop.DesktopControl.exe），请重新安装或更新 BetterDesktop。");
        return ExitCodes.NeedsHost;
    }

    /// <summary>
    /// 拉起**桌面服务**（自绘桌面层的拥有者，2026-09-17 M2）。未部署时退回拉起宿主（老部署行为，保证功能不丢）。
    /// </summary>
    private static void LaunchDesktopService()
    {
        // 定位：同目录 → %LOCALAPPDATA%\BetterDesktop（开发/局部部署），见 DesktopControlLocator。
        var exe = DesktopControlLocator.Find();
        if (exe is null)
        {
            Diag("未部署桌面服务 exe（同目录与 %LOCALAPPDATA%\\BetterDesktop 都没有），回退拉起宿主（老部署）");
            LaunchHost();
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Diag("已拉起桌面服务（自绘桌面）");
        }
        catch (Exception ex)
        {
            Diag($"拉起桌面服务失败: {ex.Message}");
        }
    }

    /// <summary>把一次开关翻转交给桌面服务进程。返回 false = 未部署该 exe（调用方走本地直写路径）。</summary>
    private static bool LaunchDesktopServiceToggle(string cmdKey)
    {
        var exe = DesktopControlLocator.Find();
        if (exe is null)
        {
            Diag("未部署桌面服务 exe（同目录与 %LOCALAPPDATA%\\BetterDesktop 都没有），改用本地直写");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--toggle-key");
            psi.ArgumentList.Add(cmdKey);
            using var process = Process.Start(psi);
            Diag($"已转交桌面服务翻转开关：{cmdKey}");
            return true;
        }
        catch (Exception ex)
        {
            Diag($"转交桌面服务失败（{cmdKey}）: {ex.Message}，改用本地直写");
            return false;
        }
    }

    /// <summary>拉起完整宿主（同目录部署）。仅自绘桌面"翻到开"调用——用户主动开启桌面外壳，属显式意图。</summary>
    private static void LaunchHost()
    {
        try
        {
            var hostExe = Path.Combine(AppContext.BaseDirectory, "BetterDesktop.Host.exe");
            if (File.Exists(hostExe))
            {
                Process.Start(new ProcessStartInfo(hostExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Diag("已拉起宿主（自绘桌面开启）");
            }
            else
            {
                Diag("宿主未部署（同目录无 BetterDesktop.Host.exe），下次手动启动生效");
            }
        }
        catch (Exception ex)
        {
            Diag($"拉起宿主失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 错误提示统一入口（2026-09-10）：宿主在线 → 命令桥转发「notify-error」由宿主统一提示；
    /// 宿主离线 → 系统通知气泡（NotifyIcon，无窗口柔和反馈）；气泡不可用（无通知区域等）→
    /// 原生 MessageBox 最后兜底。消息单行化：管道协议按行读取，\n 拆行会截断；| 是协议分隔符需转义。
    /// </summary>
    private static void ShowError(string message)
    {
        // 测试缝：单测会走到 ShowError（例如批文件不存在/坏 JSON），若不抑制，
        // TryNotifyBalloon 会 Thread.Sleep(6500) 并弹气泡 —— 测试挂 6.5s 还会污染桌面。
        if (SuppressUserFeedback)
        {
            Diag($"[suppressed] {message}");
            return;
        }

        var singleLine = message
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("|", "｜");
        if (MenuCommandPipeClient.TrySend("notify-error", singleLine))
        {
            return; // 宿主已接收，走宿主统一提示
        }

        if (TryNotifyBalloon(message))
        {
            return; // 宿主离线：系统通知气泡已展示
        }
        NativeMessageBox.ShowError(message);
    }

    /// <summary>系统通知气泡（WinForms NotifyIcon + 消息泵线程；失败返回 false 走原生兜底）。</summary>
    private static bool TryNotifyBalloon(string message)
    {
        try
        {
            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    using var icon = new System.Windows.Forms.NotifyIcon
                    {
                        Visible = true,
                        Icon = System.Drawing.SystemIcons.Warning,
                        Text = "BetterDesktop",
                        BalloonTipTitle = "BetterDesktop",
                        BalloonTipText = message,
                    };
                    icon.ShowBalloonTip(6000);
                    ready.Set();
                    System.Windows.Forms.Application.Run(); // 消息泵：保持气泡存活直至进程退出
                    icon.Visible = false;
                }
                catch
                {
                    ready.Set();
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.Wait(3000))
            {
                return false; // 图标/气泡创建超时，判定不可用
            }
            Thread.Sleep(6500); // 主线程停留，让气泡完整展示（进程退出即杀消息泵线程）
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>单文件转换（兼容既有 --menu-cmd 单文件路径）。</summary>
    private static int ConvertTo(string path, string format) => ConvertTo([path], format);

    /// <summary>多文件转换（批文件路径；多选批量在此收口）。</summary>
    private static int ConvertTo(IReadOnlyList<string> paths, string format)
    {
        var missing = paths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            var detail = missing.Count == 1
                ? missing[0]
                : $"{missing[0]}\n（共 {missing.Count} 个文件不存在）";
            Diag($"输入文件不存在: {missing[0]}（缺失 {missing.Count}/{paths.Count}）");
            ShowError($"文件不存在：\n{detail}");
            return ExitCodes.FileMissing;
        }

        try
        {
            // 完整引擎链（与 ConvertPlugin 同源；ManagedEngine 的 settings 可空——headless 不写操作记忆）。
            // 2026-09-10 架构主线：pandoc 主 + soffice/COM 辅（LibreOffice 26.8.0 内置回归）。
            var registry = new EngineRegistry()
                .Add(new SofficeEngine())
                .Add(new ComPdfEngine())
                .Add(new ManagedEngine(settings: null))
                // 2026-09-10 收尾：ComPdfTwoHop 在 TwoHopEngine 之前（演示族 png/jpg 两跳 COM 优先，soffice 兜底）
                .Add(new ComPdfTwoHopEngine())
                .Add(new TwoHopEngine())
                .Add(new ManagedImageEngine())
                .Add(new PdfComposeEngine())
                .Add(new PopplerEngine())
                .Add(new PandocEngine())
                .Add(new FfmpegEngine())
                .Add(new TextPdfEngine())
                .Add(new TesseractEngine())
                .Add(new HeicEngine())
                .Add(new RawDecodeEngine())
                .Add(new CalibreEngine());

            var service = new ConversionService(registry);
            var results = service.ConvertAsync(paths, format).GetAwaiter().GetResult();
            var failed = results.FirstOrDefault(r => !r.Success);

            if (failed is null)
            {
                var outputs = string.Join("、", results
                    .Where(r => r.Output is { Length: > 0 })
                    .Take(3)
                    .Select(r => r.Output));
                Diag($"转换成功 {results.Count}/{results.Count}: {outputs}");
                return ExitCodes.Ok;
            }

            var message = string.IsNullOrWhiteSpace(failed.Message)
                ? $"转换失败：{failed.Error}" : failed.Message;
            if (results.Count > 1)
            {
                var failCount = results.Count(r => !r.Success);
                message = $"{message}\n（成功 {results.Count - failCount} 个，失败 {failCount} 个）";
            }
            Diag($"转换失败: {format} error={failed.Error} msg={message}");
            ShowError(message);
            return MapConvertError(failed.Error);
        }
        catch (Exception ex)
        {
            Diag($"转换异常: {ex.Message}");
            ShowError($"转换异常：{ex.Message}");
            return ExitCodes.Failed;
        }
    }

    private static int Archive(string path, string action) => ArchiveCore(new[] { path }, action);

    /// <summary>
    /// 归档动作执行核心。<paramref name="paths"/> 支持多路径（批协议入口：系统右键选中多个文件/文件夹
    /// 一次压缩）；解压只用第一条（一次解压一个归档文件）。
    /// </summary>
    private static int ArchiveCore(IReadOnlyList<string> paths, string action)
    {
        if (paths.Count == 0)
        {
            Diag("归档动作没有可处理的项");
            ShowError("没有可处理的项。");
            return ExitCodes.Usage;
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Diag($"路径不存在: {path}");
                ShowError($"路径不存在：\n{path}");
                return ExitCodes.FileMissing;
            }
        }

        try
        {
            var service = new ArchiveService();
            var result = action switch
            {
                "compress-zip" => service.CompressZipAsync(paths).GetAwaiter().GetResult(),
                "compress-7z" => service.Compress7zAsync(paths).GetAwaiter().GetResult(),
                "compress-rar" => service.CompressRarAsync(paths).GetAwaiter().GetResult(),
                "unzip-here" => service.ExtractAsync(paths[0], toNamedFolder: false).GetAwaiter().GetResult(),
                "unzip-to" => service.ExtractAsync(paths[0], toNamedFolder: true).GetAwaiter().GetResult(),
                _ => new ArchiveResult { Success = false, Message = $"未知归档动作: {action}" },
            };
            if (result.Success)
            {
                Diag($"归档操作成功: {string.Join(" | ", paths)} → {result.Output}");
                return ExitCodes.Ok;
            }

            Diag($"归档操作失败: {result.Message}");
            ShowError(result.Message);
            return ExitCodes.Failed;
        }
        catch (Exception ex)
        {
            Diag($"归档操作异常: {ex.Message}");
            ShowError($"操作异常：{ex.Message}");
            return ExitCodes.Failed;
        }
    }

    private static int Unknown(string action)
    {
        Diag($"未知动作: {action}");
        ShowError($"未知的 BetterDesktop 命令：{action}");
        return ExitCodes.Usage;
    }

    private static void Diag(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdt-cli.log"),
            $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }
        catch { /* 诊断写入失败不阻断 */ }
    }
}
