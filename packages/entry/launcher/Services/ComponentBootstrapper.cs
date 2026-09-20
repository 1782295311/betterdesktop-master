// BetterDesktop 启动器 —— 拉起全部组件 + 自检 + 修复。
//
// 【职责】按依赖顺序把常驻组件带起来，并在带起前后做"功能可用性"自检：
//   ① 组件就位（文件级）  ② 清掉"用户显式停止"的留痕（本次启动 = 要完整外壳）
//   ③ 系统整合自检/修复（右键菜单注册 + 自启）  ④ 托盘（它负责带起桌面服务）
//   ⑤ 主程序外壳  ⑥ 常驻驻留件核对  ⑦ 功能件核对（面板 / 截屏 / 引擎 / 转换引擎）
//
// 【S4-4（2026-09-19）Agent 与看门狗已退役】两者职责全部迁入 core：
// 截图热键（`core/src/hotkeys.rs`）、右键扩展自愈（`core/src/shellmenu.rs`）、
// 组件与桌面监护（`core/src/supervisor.rs`）。本启动器因此**不再拉起 Agent，也不再单独起看门狗**
// —— core 自己就是那个唯一的监护者，再来一个只会与它抢组件（真机上表现为"core 停、旧守护者拉"的循环）。
//
// 【为什么不自己写"启动顺序表"】顺序不是随便排的：
//   · 托盘是**常驻控制面**，它会在构造时 `EnsureDesktopServiceAutoStart`
//     （见 tray/TrayApplicationContext.cs）→ 先起托盘，就顺手把桌面服务带起来了；
//   · 主程序放最后：它启动时会自己补齐托盘 / 桌面服务（host/Bootstrap.cs），
//     此时其余组件已就位，不会产生额外的等待。
//
// 【留痕纪律】"用户显式停止"（*-stopped.flag）代表持久意图。本启动器是"启动"语义
//（等价于托盘菜单里的"启动 X"），因此**清留痕是有意为之**，但必须逐条报给用户看，绝不静默。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BetterDesktop.Launcher.Services;

/// <summary>系统整合状态（--system-integration status 的关键字段；null = 未取到）。</summary>
/// <param name="ComRegistered">B 路经典右键菜单 COM 扩展是否已注册。</param>
/// <param name="ComPathDrifted">注册表记录的 DLL 路径与当前部署路径是否漂移。</param>
/// <param name="SnapshotPresent">菜单快照 shellmenu.json 是否存在（缺失时原生侧一项都不显示）。</param>
/// <param name="TrayAutostart">托盘自启是否真的会生效。</param>
internal sealed record IntegrationStatus(
    bool? ComRegistered,
    bool? ComPathDrifted,
    bool? SnapshotPresent,
    bool? TrayAutostart)
{
    /// <summary>
    /// 当前注册是一次 **dev 注册**（<c>--shellmenu-register --dev</c> 写下的标记）：
    /// 注册表**有意**指向开发目录，自愈按标记停手。
    /// </summary>
    /// <remarks>
    /// 单独取这个字段的理由：它是**唯一**能让"已注册、但指向开发目录"这件事被说清楚的信息。
    /// CLI 侧 <c>comPathDrifted</c> 在这种情形下是 <c>false</c>（有意为之 ≠ 漂移），
    /// 所以单看前者只会显示"已注册且路径无漂移" —— 而那句话在此刻是不完整的。
    /// </remarks>
    public bool? ComDevMode { get; init; }

    public static IntegrationStatus Unknown { get; } = new(null, null, null, null);
}

/// <summary>拉起并核验全部组件。</summary>
internal sealed class ComponentBootstrapper
{
    private readonly Action<BootStep> _progress;

    /// <param name="progress">每完成一步回调一次（UI 需要实时看到进度，长任务不得静默）。</param>
    public ComponentBootstrapper(Action<BootStep> progress)
    {
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    /// <summary>本次启动采到的系统整合状态（开关区读取"系统右键菜单"当前态用）。</summary>
    public IntegrationStatus Integration { get; private set; } = IntegrationStatus.Unknown;

    /// <summary>执行整个启动流程（阻塞；由调用方放到后台线程）。</summary>
    public BootReport Run()
    {
        var report = new BootReport();

        void Add(StepState state, string title, string detail)
        {
            var step = new BootStep(state, title, detail);
            report.Add(state, title, detail);
            LauncherLog.Write($"[{state}] {title} —— {detail}");
            _progress(step);
        }

        VerifyFiles(Add);
        ClearStopFlags(Add);
        EnsureSystemIntegration(Add);
        EnsureComponent(Add, "BetterDesktop.Tray.exe", "BetterDesktop.Tray", "托盘（常驻控制面）", 6000);
        EnsureComponent(Add, "BetterDesktop.Host.exe", "BetterDesktop.Host", "主程序外壳（菜单栏 / Dock / 桌面）", 6000);
        VerifyResidents(Add);
        VerifyFeatureFiles(Add);
        return report;
    }

    // ---------------- ① 组件就位 ----------------

    /// <summary>必须存在的文件（名称 → 用户可读标签）。与 publish.ps1 的 $required 同一真相源。</summary>
    private static readonly (string File, string Label)[] RequiredFiles =
    {
        ("BetterDesktop.Host.exe", "主程序外壳"),
        ("BetterDesktop.Tray.exe", "托盘"),
        ("BetterDesktop.DesktopControl.exe", "桌面服务"),
        ("BetterDesktop.Cli.exe", "命令行入口"),
        ("BetterDesktop.Settings.exe", "设置中心"),
        ("BetterDesktop.Recovery.exe", "应急恢复"),
        ("BetterDesktop.Clipboard.Panel.exe", "剪贴板面板"),
        ("BetterDesktop.Clipboard.Engine.exe", "剪贴板引擎"),
        ("BetterDesktop.Index.Engine.exe", "搜索索引引擎"),
        ("BetterDesktop.Capture.exe", "截屏工具"),
        ("convert-engine.exe", "格式转换引擎"),
        (@"native\BetterDesktopShellMenu.dll", "右键菜单原生扩展"),
        ("cordis.yml", "插件声明"),
        ("agent.yml", "能力声明"),
    };

    private void VerifyFiles(Action<StepState, string, string> add)
    {
        var missing = new List<string>();
        foreach (var (file, label) in RequiredFiles)
        {
            if (ComponentPaths.Find(file) is null)
            {
                missing.Add($"{label}（{file}）");
            }
        }

        if (missing.Count == 0)
        {
            add(StepState.Ok, "组件就位", $"{RequiredFiles.Length} 个必需文件全部找到");
            return;
        }

        // 缺件必须显式失败：这正是历史上"装了但功能不存在"（面板/截屏没进 01-主程序）的观感来源。
        add(
            StepState.Fail,
            "组件不完整",
            $"缺少 {missing.Count} 项：{string.Join("；", missing)}。安装可能中断，建议重新运行安装包。");
    }

    // ---------------- ② 留痕（用户显式停止的意图） ----------------

    private static readonly string[] ComponentStopFlags =
    {
        "host-stopped.flag",
        "desktop-stopped.flag",
    };

    private void ClearStopFlags(Action<StepState, string, string> add)
    {
        var cleared = new List<string>();
        foreach (var flag in ComponentStopFlags)
        {
            var path = ComponentPaths.FlagPath(flag);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    cleared.Add(flag);
                }
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"清除留痕 {flag}", ex);
            }
        }

        if (cleared.Count == 0)
        {
            add(StepState.Ok, "启动意图", "没有发现「停止」留痕，全部组件按默认启动");
            return;
        }

        add(
            StepState.Warn,
            "已覆盖此前的「停止」",
            $"清除了 {string.Join("、", cleared)}（本次是「启动」操作，与托盘菜单里点「启动」同义）");
    }

    // ---------------- ③ 系统整合（右键菜单 + 自启） ----------------

    private void EnsureSystemIntegration(Action<StepState, string, string> add)
    {
        var status = CliRunner.Run("--system-integration", "status");
        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StdOut))
        {
            add(StepState.Warn, "系统整合", $"无法读取整合状态（CLI 退出码 {status.ExitCode}），跳过自动修复");
            return;
        }

        Integration = ParseStatus(status.StdOut);

        // 用户显式注销过右键扩展（shellmenu-unregistered.flag）：那是**持久意图**，不擅自装回来。
        // Agent 的 60s 自愈同样尊重该标记（见 SystemIntegrationRegistrar.IsUserUnregistered）。
        if (ComponentPaths.FlagExists("shellmenu-unregistered.flag"))
        {
            add(
                StepState.Warn,
                "系统右键菜单",
                "你此前显式注销过它，本次保持不变（可在下方开关里重新启用）");
            return;
        }

        // dev 注册要先说清楚（见 ComDevMode 的说明）：它同样满足 needsRepair=false，
        // 但"已注册且路径无漂移"会把"指向开发目录、clean 后就会失效"这件事瞒住。
        if (Integration.ComDevMode == true)
        {
            add(
                StepState.Warn,
                "系统右键菜单",
                "已注册为**开发模式**（指向开发目录）：该目录被清理/重建后菜单会失效，"
                + "且注册自愈已停手。要回到正式安装请重新注册（--system-integration register）");
            return;
        }

        var needsRepair = Integration.ComRegistered == false || Integration.ComPathDrifted == true;
        if (!needsRepair)
        {
            add(
                StepState.Ok,
                "系统右键菜单",
                Integration.SnapshotPresent == false
                    ? "已注册（菜单内容快照由桌面服务稍后写入）"
                    : "已注册且路径无漂移");
            return;
        }

        var reason = Integration.ComPathDrifted == true ? "注册路径漂移（装到新目录了）" : "尚未注册";
        var repair = CliRunner.Run("--system-integration", "register");
        if (repair.ExitCode == 0)
        {
            add(StepState.Ok, "系统右键菜单", $"已修复：{reason} → 重新注册成功");
            return;
        }

        add(
            StepState.Fail,
            "系统右键菜单",
            $"修复失败（{reason}，CLI 退出码 {repair.ExitCode}）。可稍后手动运行：BetterDesktop.Cli.exe --system-integration register");
    }

    /// <summary>解析 `键=值` 状态文本（CLI 的 Describe 输出；未知键忽略）。</summary>
    internal static IntegrationStatus ParseStatus(string text)
    {
        bool? registered = null, drifted = null, snapshot = null, tray = null, dev = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            var parsed = value.Equals("True", StringComparison.OrdinalIgnoreCase) ? true
                : value.Equals("False", StringComparison.OrdinalIgnoreCase) ? false
                : (bool?)null;

            switch (key)
            {
                case "comRegistered":
                    registered = parsed;
                    break;
                case "comPathDrifted":
                    drifted = parsed;
                    break;
                case "snapshotPresent":
                    snapshot = parsed;
                    break;
                case "autostartTray":
                    tray = parsed;
                    break;
                case "comDevMode":
                    dev = parsed;
                    break;
            }
        }

        return new IntegrationStatus(registered, drifted, snapshot, tray) { ComDevMode = dev };
    }

    // ---------------- ④⑤ 拉起单个组件 ----------------

    /// <summary>
    /// 确保某组件在运行：已运行 → 就绪；未部署 → 失败；未运行 → 拉起并**核实真的起来了**。
    /// <para>核实很重要：静默启动失败（缺依赖 / 单实例互斥被旧进程占着）与"没装"对用户完全一样。</para>
    /// </summary>
    private void EnsureComponent(
        Action<StepState, string, string> add,
        string exeName,
        string processName,
        string label,
        int verifyTimeoutMs)
    {
        if (IsProcessRunning(processName))
        {
            add(StepState.Ok, label, "已在运行");
            return;
        }

        var exe = ComponentPaths.Find(exeName);
        if (exe is null)
        {
            add(StepState.Fail, label, $"未部署（找不到 {exeName}）");
            return;
        }

        if (!TryStart(exe))
        {
            add(StepState.Fail, label, $"拉起失败（{exe}）");
            return;
        }

        if (WaitForProcess(processName, verifyTimeoutMs))
        {
            add(StepState.Ok, label, "已拉起");
            return;
        }

        add(StepState.Warn, label, $"已发起启动但未在 {verifyTimeoutMs / 1000} 秒内看到进程（可能启动较慢或启动即退出，见日志）");
    }

    // ---------------- ⑥ 常驻驻留件核对 ----------------

    private void VerifyResidents(Action<StepState, string, string> add)
    {
        // 桌面服务由托盘/主程序带起，需要一点时间；这里只**核对**，不重复拉起
        //（重复拉起会与单实例互斥打架，产生"拉两次死一次"的噪声）。
        //
        // 【S4-4】原先这里还核对 Agent（常驻能力宿主）—— 它已退役，职责迁入 core（见文件头注）。
        if (WaitForProcess("BetterDesktop.DesktopControl", 8000))
        {
            add(StepState.Ok, "常驻服务", "桌面服务在运行");
            return;
        }

        add(
            StepState.Warn,
            "常驻服务",
            "桌面服务未运行；它通常由托盘带起——若持续缺失，请检查托盘是否被安全软件拦截");
    }

    // ---------------- ⑦ 看门狗：已随 S4-4 删除 ----------------
    //
    // 这里原为 `EnsureWatchdog(Add)` 及其实现：它拉起 `BetterDesktop.Watchdog.exe`，
    // 并在启动前清理"暂停守护"留痕。看门狗的职责（组件消失后拉回）已由 **core 的 supervisor** 接管，
    // 而"暂停监护"的留痕语义也迁到了 core 读的那两个标记（`watchdog-pause.flag` 更新器写 /
    // `user-pause.flag` 用户写，见 `core/src/supervisor.rs`）—— 本启动器不再碰它们。
    //
    // 保留这段说明的理由同上：让"这里**原来有东西**"可见，而不是让下一个人以为从来如此。

    // ---------------- ⑧ 功能件核对 ----------------

    /// <summary>
    /// 功能件核对：目前只有"可选引擎树"这一项。
    /// <para>
    /// 【2026-09-20 convert-lite 迁移后】格式转换本身**不再需要** <c>engines\</c>（文档/表格/电子书族
    /// 由 convert-engine.exe 的进程内 lite 核心完成）；随包分发的只剩 lite 替代不了的三棵树
    /// （tesseract=OCR / poppler=PDF / ffmpeg=音视频），由 <c>06-可选引擎</c> 模块承载。
    /// 引擎按 <c>AppContext.BaseDirectory\engines\...</c> 定位（shell-convert），缺失时
    /// <c>ConvertMenuService</c> 对不可用目标是**整项隐藏**（不是置灰）。
    /// 这里只报"缺失"是不够的 —— 必须给出**从哪儿拷到哪儿**，用户才能自己一步解决。
    /// </para>
    /// </summary>
    private void VerifyFeatureFiles(Action<StepState, string, string> add)
    {
        if (ComponentPaths.DirectoryExists("engines"))
        {
            add(StepState.Ok, "可选引擎", "engines\\ 目录就位（OCR / PDF 渲染可用；格式转换由 lite 进程内完成，不依赖它）");
            return;
        }

        var source = FindEnginesSource();
        var detail = source is null
            ? "engines\\ 目录缺失：格式转换照常可用（lite 进程内），仅 OCR（tesseract）与 PDF 渲染（poppler）不可用。需要时把发布包 06-可选引擎 里的 engines 文件夹拷到程序目录即可"
            : $"engines\\ 目录缺失：把 {source} 里的内容复制到 {Path.Combine(ComponentPaths.BaseDir, "engines")} 即可恢复 OCR / PDF 渲染（或运行该目录下的 install-engines.ps1）";
        add(StepState.Warn, "可选引擎", detail);
    }

    /// <summary>在常见发布形态里找引擎树（模块包 06-*\engines / 发布根 engines）。</summary>
    private static string? FindEnginesSource()
    {
        foreach (var dir in ComponentPaths.SearchDirs)
        {
            try
            {
                var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar);
                var parent = Directory.GetParent(trimmed)?.FullName;
                // 2026-09-20：模块已由 06-格式转换引擎 改名为 06-可选引擎（只再发 lite 替代不了的
                // 三棵树：tesseract/poppler/ffmpeg）。这里改用**前缀匹配 06-***，改名不会再静默地让
                // 引擎发现失效（scripts/install-betterdesktop.ps1 用的是同一口径）。
                var candidates = new List<string> { Path.Combine(dir, "engines") };
                if (parent is not null)
                {
                    foreach (var module in Directory.GetDirectories(parent, "06-*"))
                    {
                        candidates.Add(Path.Combine(module, "engines"));
                    }

                    candidates.Add(Path.Combine(parent, "engines"));
                }

                foreach (var candidate in candidates)
                {
                    if (candidate.Length > 0 && Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
                // 路径异常：继续找下一处（提示信息不值得让启动失败）
            }
        }

        return null;
    }

    // ---------------- 进程工具 ----------------

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            // 句柄必须释放：这些调用在启动期会走多次，漏释放会累积句柄。
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static bool WaitForProcess(string processName, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (IsProcessRunning(processName))
            {
                return true;
            }

            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                return false;
            }

            Thread.Sleep(150);
        }
    }

    /// <summary>脱离式启动（不等待退出）：正常工作目录 = exe 所在目录。</summary>
    private static bool TryStart(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ComponentPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"启动 {exe}", ex);
            return false;
        }
    }
}
