using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.IndexIpc;

namespace BetterDesktop.Shell.AppSource.Services;

// ── 本文件方法级白话索引（应用枚举总服务，白话 → 方法）──
//   "扫开始菜单快捷方式"            → ScanStartMenu（目录遍历 ScanDirectory）
//   "扫已安装桌面程序（注册表卸载键）" → ScanInstalledApps（exe 解析 ResolveInstalledExecutable、系统组件排除 IsSystemComponentKey）
//   "扫微软商店应用"                → ScanStoreApps；Apps 文件夹来源 AppsFolderSource
//   "新安装应用角标"                → GetNewlyInstalledApps / MarkAppsSeen（已读集合 LoadSeen/SaveSeen 持久化）
//   "由文件路径反查应用项"          → ResolveFromPath；缓存失效 InvalidateCache
//   "各类过滤判定（文档/系统工具/可执行/排除名）" → IsDocumentTarget / IsSystemTool / IsLikelyExecutable / IsExcludedName
//   "稳定应用 ID 生成"              → CreateStableId（干净/全程序/引擎升格三路共用）；开始菜单目录变化监听 OnStartMenuChanged
//   "全程序模式按稳定 Id 去重"       → DedupByStableId / DedupAndLog
//   "全程序模式过滤（CLI/工具链）"    → ApplyAllProgramsFilter（开关 IsAllProgramsFilterEnabled，默认关）；规则表 Services/AppFilterRules.cs
//   "口袋目录 / 桌面快捷方式"        → SetExtraScanRoots + ScanExtraRoots / SetDesktopShortcutsEnabled + ScanDesktopShortcuts；设置映射 Services/AppSourceSettings.cs
// ────────────────────────────────────

/// <summary>
/// 应用来源服务实现：扫描开始菜单、已安装程序，注册表解析。
/// 不涉及 UI 渲染或固定逻辑。
/// </summary>
public sealed class AppSourceService : IAppSourceService, IDisposable
{
    private readonly string _seenPath;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seenGate = new();

    // 扫描结果内存缓存：开始菜单 / 已安装程序 两类全量扫描代价高（注册表 + 目录遍历 + 文件 IO），
    // 同一进程内短时间内被 DockAppsService / AppGrabber / NewApps 等多处重复触发。
    // 加带 TTL 的缓存，避免每次都重扫整盘。
    private readonly object _cacheGate = new();
    private List<AppItem>? _startMenuCache;
    private List<AppItem>? _installedCache;
    private List<AppItem>? _storeCache;
    private DateTime _startMenuCacheAt = DateTime.MinValue;
    private DateTime _installedCacheAt = DateTime.MinValue;
    private DateTime _storeCacheAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    // `shell:appsfolder` 数据源（UWP / Store 应用）。枚举本身有代价（COM 遍历），
    // 与已安装缓存同一 TTL 策略；Store 项亦被 ScanInstalledApps 合并带出。
    private readonly AppsFolderSource _appsFolderSource = new();

    // 程序树缓存：随 InvalidateCache / 开始菜单变化事件一起失效，保证"所有程序"树始终新鲜。
    private ProgramFolder? _programTreeCache;

    // 开始菜单目录监控：FileSystemWatcher 防抖 1s 后失效缓存并发出 AppSourceChanged。
    private readonly StartMenuWatcher? _watcher;

    /// <inheritdoc />
    public event EventHandler? AppSourceChanged;

    /// <inheritdoc />
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _startMenuCache = null;
            _installedCache = null;
            _storeCache = null;
            _startMenuCacheAt = DateTime.MinValue;
            _installedCacheAt = DateTime.MinValue;
            _storeCacheAt = DateTime.MinValue;
            _programTreeCache = null;
        }
    }

    // 排除列表（中文/英文：帮助、卸载、安装等非应用项）
    private static readonly string[] ExcludedNames =
    {
        // 英文
        "documentation", "help", "install", "more info",
        "read me", "read first", "readme", "remove",
        "setup", "what's new", "support", "on the web", "safe mode",
        "uninstall", "release notes", "license",
        "donate", "donation", "update", "upgrade", "download",
        // 中文
        "帮助", "手册", "文档", "说明书", "自述", "说明",
        "卸载", "安装", "许可", "许可协议", "服务条款", "隐私",
        "更新", "升级", "下载", "捐赠", "教程", "帮助文档"
    };

    // 文档/帮助类目标扩展名（非应用，需排除）
    private static readonly string[] DocumentTargetExtensions =
    {
        ".html", ".htm", ".chm", ".mht", ".mhtml",
        ".txt", ".md", ".rtf", ".pdf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    /// <summary>
    /// 【M2 · 2026-09-13】索引引擎客户端（`null` = 未接引擎 → 全部走本地实现，行为与接入前**逐字一致**）。
    /// <para>
    /// 引擎只提供「磁盘上有哪些可执行文件」这一**事实**（省掉全盘扫描），
    /// 应用语义（lnk 解析、显示名、过滤链、Id）仍由本类按既有实现升格 —— 见 <see cref="AppCandidateMapper"/>。
    /// </para>
    /// </summary>
    private readonly IndexIpcClient? _indexClient;

    /// <summary>降级/诊断日志（可空：无日志时降级仍以返回值表达，不为日志阻塞构造）。</summary>
    private readonly IKernelLogger? _logger;

    /// <summary>全程序模式是否启用过滤（默认关；见 <see cref="IsAllProgramsFilterEnabled"/>）。</summary>
    private readonly bool _filterAllPrograms;

    /// <summary>口袋目录（设置「应用扫描目录」；默认空，§12 Q2 裁决）。</summary>
    private volatile string[] _extraScanRoots = Array.Empty<string>();

    /// <summary>桌面快捷方式是否纳入干净模式（默认纳入；§12 Q3 裁决）。</summary>
    private volatile bool _scanDesktopShortcuts = true;

    public AppSourceService(
        string? dataDirectory = null,
        IndexIpcClient? indexClient = null,
        IKernelLogger? logger = null,
        bool? filterAllPrograms = null)
    {
        _indexClient = indexClient;
        _logger = logger;
        _filterAllPrograms = filterAllPrograms ?? IsAllProgramsFilterEnabled();
        var appData = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");
        _seenPath = Path.Combine(appData, "installed-seen.json");

        // 监控用户/公共两个开始菜单 Programs 目录：安装/卸载/改名后失效缓存并通知订阅方。
        var startMenuDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };
        _watcher = new StartMenuWatcher(startMenuDirs);
        _watcher.Changed += OnStartMenuChanged;
    }

    /// <summary>
    /// 设置口袋目录（便携 / 解压即用工具的自定义根）。由 <c>AppSourcePlugin</c> 从设置推送。
    /// <para>只影响<see cref="ScanAllPrograms"/>的附加扫描，故无需失效既有缓存。</para>
    /// </summary>
    public void SetExtraScanRoots(IReadOnlyList<string>? roots)
    {
        _extraScanRoots = roots is null ? Array.Empty<string>() : roots.ToArray();
    }

    /// <summary>
    /// 设置桌面快捷方式是否纳入干净模式。桌面来源影响<see cref="ScanStartMenu"/>的缓存结果，故必须失效缓存。
    /// </summary>
    public void SetDesktopShortcutsEnabled(bool enabled)
    {
        if (_scanDesktopShortcuts == enabled)
        {
            return;
        }

        _scanDesktopShortcuts = enabled;
        InvalidateCache();
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItem> ScanStartMenu()
    {
        lock (_cacheGate)
        {
            if (_startMenuCache is not null && DateTime.UtcNow - _startMenuCacheAt < CacheTtl)
            {
                return _startMenuCache;
            }
        }

        // 【M2 · 2026-09-13】优先走索引引擎（省掉全递归目录扫描：M0 基线 4254ms → 引擎候选 + 本地升格）；
        // 引擎不可用/构建中 → 回退下方本地实现（降级记 Warn，绝不静默）。
        var ordered = ScanStartMenuFromEngine();
        if (ordered is null)
        {
            var result = new List<AppItem>();
            var directories = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs"
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                result.AddRange(ScanDirectory(directory));
            }

            ordered = result
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 桌面快捷方式（附加来源）对**两条路径都**并入——否则切换后端会改变干净模式条目集
        ordered = MergeDesktopShortcuts(ordered);

        lock (_cacheGate)
        {
            _startMenuCache = ordered;
            _startMenuCacheAt = DateTime.UtcNow;
        }

        return ordered;
    }

    /// <summary>
    /// 【M2】开始菜单的引擎路径：`list_apps` 只给磁盘事实（路径），
    /// 这里**逐候选复用本地 <see cref="ResolveFromPath"/> 升格**（完整过滤链语义不变）。
    /// 返回 <c>null</c> = 引擎不可用/构建中 → 调用方回退本地实现。
    /// </summary>
    private List<AppItem>? ScanStartMenuFromEngine()
    {
        var page = TryListAppsFromEngine();
        if (page is null)
        {
            return null;
        }

        var result = new List<AppItem>();
        var filtered = 0;
        foreach (var candidate in page.Apps)
        {
            if (!string.Equals(candidate.Source, AppCandidateMapper.EngineSourceStartMenu, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = AppCandidateMapper.FromStartMenuCandidate(candidate, ResolveFromPath);
            if (item is null)
            {
                filtered++; // 被本地过滤链拒收（排除名/文档目标/非可执行/系统工具）
                continue;
            }

            result.Add(item);
        }

        var ordered = result
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger?.Info($"[app-source] 开始菜单走索引引擎：候选 {page.Apps.Count} 项 → 收录 {ordered.Count} 项（本地过滤 {filtered} 项）");
        return ordered;
    }

    /// <summary>
    /// 【M2】引擎可用时取候选快照；不可用 / 构建中 / 异常 → <c>null</c>（调用方回退本地实现）。
    /// </summary>
    private ListAppsResult? TryListAppsFromEngine()
    {
        var client = _indexClient;
        if (client is null)
        {
            return null; // 未接引擎（构造未注入）→ 调用方走本地，属正常路径不记降级
        }

        if (!client.IsConnected)
        {
            _logger?.Warn("[app-source] 索引引擎未连接 → 本次回退本地扫描");
            return null;
        }

        try
        {
            // 本方法是同步接口（IAppSourceService），必须在**后台线程**等待异步 IPC：
            // 直接 GetResult 会捕获调用方（UI）上下文 → 有死锁风险。
            var page = System.Threading.Tasks.Task
                .Run(() => client.ListAppsAsync(System.Threading.CancellationToken.None))
                .GetAwaiter()
                .GetResult();

            if (page.Building)
            {
                _logger?.Warn("[app-source] 索引引擎仍在构建中 → 本次回退本地扫描");
                return null;
            }

            // 【2026-09-14 修复】降级（应用索引为空 / 无可用根目录）同样必须回退本地实现。
            // 此前只判 Building → 引擎"一条都没扫到"会被当成"系统里真的没有应用"，
            // 开始菜单与应用盘点列表直接变空，且没有任何降级提示。
            // 纪律与 TrySearchFilesAsync 的 `Building || Degraded` 一致（降级不得静默）。
            // 注：引擎侧 `list_apps.degraded` 只反映**应用索引**，不会被"文件索引触顶"牵连。
            if (page.Degraded)
            {
                _logger?.Warn($"[app-source] 应用索引降级（{page.DegradeReason}）→ 本次回退本地扫描");
                return null;
            }

            return page;
        }
        catch (Exception e)
        {
            _logger?.Warn($"[app-source] 查询索引引擎失败（{e.Message}）→ 本次回退本地扫描");
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItem> ScanInstalledApps()
    {
        lock (_cacheGate)
        {
            if (_installedCache is not null && DateTime.UtcNow - _installedCacheAt < CacheTtl)
            {
                return _installedCache;
            }
        }

        var result = new List<AppItem>();

        var rootViews = new (Microsoft.Win32.RegistryKey Root, string SubPath)[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var view in rootViews)
        {
            try
            {
                using var parent = view.Root.OpenSubKey(view.SubPath);
                if (parent is null)
                {
                    continue;
                }

                foreach (var subKeyName in parent.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = parent.OpenSubKey(subKeyName);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var name = subKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        // 过滤系统组件/更新包
                        if (IsSystemComponentKey(subKey))
                        {
                            continue;
                        }

                        // 过滤排除项
                        if (IsExcludedName(name))
                        {
                            continue;
                        }

                        var exePath = ResolveInstalledExecutable(
                            subKey.GetValue("DisplayIcon") as string,
                            subKey.GetValue("InstallLocation") as string);
                        if (string.IsNullOrWhiteSpace(exePath))
                        {
                            continue;
                        }

                        // 过滤系统目录工具
                        if (IsSystemTool(exePath))
                        {
                            continue;
                        }

                        result.Add(new AppItem
                        {
                            Id = new AppItemId(exePath),
                            Name = name,
                            ShortcutPath = exePath,
                            TargetPath = exePath,
                            Source = BetterDesktop.Shell.AppSource.Models.AppSource.Installed,
                            UninstallCommand = subKey.GetValue("UninstallString") as string
                        });
                    }
                    catch
                    {
                        // 单个注册表项解析失败不阻断整体扫描。
                    }
                }
            }
            catch
            {
                // 卸载注册表主键访问失败不阻断整体扫描。
            }
        }

        var ordered = result
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 合并 UWP / Store 应用（shell:appsfolder）：与注册表结果按 Id 去重，
        // 让干净模式 / 搜索 / 新装通知等消费方自动获得 Store 应用。
        foreach (var store in ScanStoreApps())
        {
            if (!ordered.Any(x => x.Id.Equals(store.Id)))
            {
                ordered.Add(store);
            }
        }

        var merged = ordered.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        lock (_cacheGate)
        {
            _installedCache = merged;
            _installedCacheAt = DateTime.UtcNow;
        }

        return merged;
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItem> ScanStoreApps()
    {
        lock (_cacheGate)
        {
            if (_storeCache is not null && DateTime.UtcNow - _storeCacheAt < CacheTtl)
            {
                return _storeCache;
            }
        }

        var items = _appsFolderSource.ScanStoreApps();

        lock (_cacheGate)
        {
            _storeCache = items.ToList();
            _storeCacheAt = DateTime.UtcNow;
        }

        return items;
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItem> GetNewlyInstalledApps()
    {
        var installed = ScanInstalledApps();
        if (installed.Count == 0)
        {
            return Array.Empty<AppItem>();
        }

        lock (_seenGate)
        {
            LoadSeen();

            // 首次运行：把当前已安装程序全部记为已见，避免一次性弹出所有旧应用提醒。
            if (_seen.Count == 0)
            {
                foreach (var app in installed)
                {
                    _seen.Add(app.Id.ToString());
                }

                SaveSeen();
                return Array.Empty<AppItem>();
            }

            return installed
                .Where(a => !_seen.Contains(a.Id.ToString()))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void MarkAppsSeen(IReadOnlyList<AppItem> apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return;
        }

        lock (_seenGate)
        {
            LoadSeen();
            var changed = false;
            foreach (var app in apps)
            {
                changed |= _seen.Add(app.Id.ToString());
            }

            if (changed)
            {
                SaveSeen();
            }
        }
    }

    /// <inheritdoc />
    public AppItem? ResolveFromPath(string path)
    {
        // 默认不强制读取 FileVersionInfo（高代价同步文件 IO），显示名优先用快捷方式/文件名。
        // 仅在显式 eager 时才去读版本信息——用于应用管理中心按需展示更友好的名称。
        return ResolveFromPath(path, eager: false);
    }

    /// <summary>
    /// 解析单个文件为应用项。
    /// <paramref name="eager"/> 为 true 时对 .exe 读取 FileVersionInfo.FileDescription 作为显示名
    /// （代价较高，每个几十 ms 文件 IO，仅在用户显式浏览场景使用，禁止在启动/批量扫描路径调用）。
    /// </summary>
    public AppItem? ResolveFromPath(string path, bool eager)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!ShellLinkResolver.IsSupportedFile(path))
        {
            return null;
        }

        try
        {
            var (displayName, targetPath, source) = ShellLinkResolver.Resolve(path);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                // 快捷方式无名称时退回文件名，避免直接丢弃（扫描场景需要占位名）。
                displayName = Path.GetFileNameWithoutExtension(path);
            }

            // 过滤排除项
            if (IsExcludedName(displayName))
            {
                return null;
            }

            // 过滤文档类目标
            if (IsDocumentTarget(targetPath))
            {
                return null;
            }

            // 只保留真正可执行应用
            if (source != BetterDesktop.Shell.AppSource.Models.AppSource.Store && !IsLikelyExecutable(targetPath))
            {
                return null;
            }

            // 过滤系统目录工具
            if (IsSystemTool(targetPath))
            {
                return null;
            }

            var id = CreateStableId(source, path, targetPath);

            // 仅 eager 模式（按需、非批量）才读取 FileVersionInfo，避免启动/扫描时数百次同步文件 IO 卡 UI。
            if (eager && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fileVersionInfo = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(fileVersionInfo.FileDescription))
                    {
                        displayName = fileVersionInfo.FileDescription;
                    }
                }
                catch
                {
                    // 获取版本信息失败时使用原名称
                }
            }

            return new AppItem
            {
                Id = id,
                Name = displayName,
                ShortcutPath = path,
                TargetPath = targetPath,
                Source = source
            };
        }
        catch
        {
            return null;
        }
    }

    private List<AppItem> ScanDirectory(string directory)
    {
        var result = new List<AppItem>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                if (!ShellLinkResolver.IsSupportedFile(file))
                {
                    continue;
                }

                var app = ResolveFromPath(file);
                if (app is not null)
                {
                    result.Add(app);
                }
            }
        }
        catch
        {
            // 目录访问失败不阻断扫描
        }

        return result;
    }

    /// <summary>
    /// 稳定 Id 的**唯一产生点**（干净模式 / 全程序模式 / 引擎升格三条路径共用）。
    /// 与固定库同源（<c>DockAppsService.AddByPath</c> → <see cref="ResolveFromPath"/>），
    /// 故「同一个程序」在两种模式与固定集合里得到同一个 Id。
    /// <para>内部而非私有 + 单一实现是刻意的：全程序模式曾自造 <c>"all:" + 路径</c> 前缀，
    /// 导致固定态 / 绿点 / 已固定筛选 / 新装提醒在两种模式间全部分裂（2026-09-13 修）。
    /// 引擎升格路径（<c>AppCandidateMapper</c>）也必须调本函数，否则引擎/本地结果集不再平价。</para>
    /// </summary>
    internal static AppItemId CreateStableId(BetterDesktop.Shell.AppSource.Models.AppSource source, string shortcutPath, string targetPath)
    {
        var keySource = source switch
        {
            BetterDesktop.Shell.AppSource.Models.AppSource.Store => targetPath,
            _ => string.IsNullOrWhiteSpace(targetPath) ? shortcutPath : targetPath
        };

        // 双保险：任何来源都不得产出空 Id。空 Id 会让所有「无路径」项在固定集合与
        // _containers 字典里互相碰撞（Store 分支此前直接取 targetPath，空则产出空 Id —— 2026-09-14 修）。
        if (string.IsNullOrWhiteSpace(keySource))
        {
            keySource = shortcutPath;
        }

        return new AppItemId(keySource ?? string.Empty);
    }

    private static bool IsSystemComponentKey(Microsoft.Win32.RegistryKey subKey)
    {
        // SystemComponent==1 表示组件
        if (subKey.GetValue("SystemComponent") is int systemComponent && systemComponent == 1)
        {
            return true;
        }

        // ParentKeyName 存在表示属于某个父组件的子项
        if (!string.IsNullOrWhiteSpace(subKey.GetValue("ParentKeyName") as string))
        {
            return true;
        }

        var displayName = (subKey.GetValue("DisplayName") as string) ?? string.Empty;
        if (displayName.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase) ||
            displayName.StartsWith("Update for", StringComparison.OrdinalIgnoreCase) ||
            displayName.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase) ||
            displayName.StartsWith("Hotfix for", StringComparison.OrdinalIgnoreCase) ||
            displayName.StartsWith("Performance Update", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string ResolveInstalledExecutable(string? displayIcon, string? installLocation)
    {
        // 优先 DisplayIcon（如 "C:\Path\app.exe,0"）
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var candidate = displayIcon;
            var comma = candidate.LastIndexOf(',');
            if (comma > 0)
            {
                var stripped = candidate[..comma];
                if (File.Exists(stripped))
                {
                    candidate = stripped;
                }
            }

            candidate = candidate.Trim('"');
            if (File.Exists(candidate) && IsLikelyExecutable(candidate))
            {
                return candidate;
            }
        }

        // 回退：InstallLocation 目录顶层如果只有一个/最可能的主程序 exe
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var exeFiles = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(IsLikelyExecutable)
                    .ToList();
                if (exeFiles.Count == 1)
                {
                    return exeFiles[0];
                }

                if (exeFiles.Count > 0)
                {
                    var best = exeFiles
                        .OrderBy(p => Path.GetFileNameWithoutExtension(p).Length)
                        .FirstOrDefault(p =>
                            !Path.GetFileNameWithoutExtension(p).Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileNameWithoutExtension(p).Contains("uninstall", StringComparison.OrdinalIgnoreCase));
                    return best ?? exeFiles[0];
                }
            }
            catch
            {
                // 目录遍历失败时回退为空
            }
        }

        return string.Empty;
    }

    private static bool IsExcludedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var lowerName = name.ToLowerInvariant();
        foreach (var exclude in ExcludedNames)
        {
            if (lowerName.Contains(exclude))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDocumentTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var cleanPath = targetPath;
        var queryIndex = targetPath.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
        {
            cleanPath = targetPath[..queryIndex];
        }

        var extension = Path.GetExtension(cleanPath);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        foreach (var docExt in DocumentTargetExtensions)
        {
            if (extension.Equals(docExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyExecutable(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var extension = Path.GetExtension(targetPath);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".msc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemTool(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        ReadOnlySpan<string> systemPaths = new[]
        {
            @"C:\Windows\System32",
            @"C:\Windows\SysWOW64",
            @"C:\Windows",
            @"C:\Program Files\Windows Defender",
            @"C:\Program Files\Windows Mail",
            @"C:\Program Files\Windows Media Player",
            @"C:\Program Files\Windows NT",
            @"C:\Program Files\Windows Photo Viewer",
            @"C:\Program Files\Windows Portable Devices",
            @"C:\Program Files\Windows Sidebar",
            @"C:\Program Files\WindowsApps"
        };

        foreach (var systemPath in systemPaths)
        {
            if (targetPath.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void LoadSeen()
    {
        try
        {
            _seen.Clear();
            if (!File.Exists(_seenPath))
            {
                return;
            }

            var json = File.ReadAllText(_seenPath);
            var values = JsonSerializer.Deserialize<List<string>>(json);
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _seen.Add(value);
                }
            }
        }
        catch
        {
            // 快照读取失败时保持空集合，后续再重新建立快照。
        }
    }

    private void SaveSeen()
    {
        try
        {
            var directory = Path.GetDirectoryName(_seenPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_seenPath, JsonSerializer.Serialize(_seen.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 快照写入失败不阻断主流程。
        }
    }

    /// <summary>
    /// 开始菜单目录变化（创建 / 删除 / 改名）防抖回调：失效缓存并通知订阅方。
    /// FileSystemWatcher 回调运行在工作线程，事件在调用线程直接触发，订阅方需自行切回 UI 线程。
    /// </summary>
    private void OnStartMenuChanged(object? sender, EventArgs e)
    {
        try
        {
            InvalidateCache();
            AppSourceChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 通知失败不阻断（M10）。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnStartMenuChanged;
            _watcher.Dispose();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AppItem> ScanAllPrograms()
    {
        // 【M2 · 2026-09-13】优先走索引引擎（M0 基线：冷扫描 53,107ms → 引擎候选 + 本地 lnk 升格）；
        // 引擎不可用/构建中 → 回退下方本地递归扫描（降级记 Warn）。
        var fromEngine = ScanAllProgramsFromEngine();
        if (fromEngine is not null)
        {
            return DedupAndLog(ApplyAllProgramsFilter(MergeExtraRoots(fromEngine)));
        }

        var roots = GetAllProgramRoots();
        var results = new List<AppItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                // 深度受控：仅递归到深度 3，避免 C:\ 全盘遍历卡死。
                EnumerateExecutablesRecursive(root, results, seenPaths, currentDepth: 0, maxDepth: 3);
            }
            catch
            {
                // 单个根目录异常不阻断其他根。
            }
        }

        return DedupAndLog(ApplyAllProgramsFilter(MergeExtraRoots(results)));
    }

    /// <summary>
    /// 桌面快捷方式作为**干净模式的附加来源**（用户桌面 + 公共桌面，深度 1，只收快捷方式类文件）。
    /// <para>为什么纳入：桌面是用户最主要的手动启动入口，「主动放桌面」是极强的「我在意这个应用」信号
    /// （分析稿 §7.2 指出桌面完全没参与索引）。**下载目录不纳入**（噪音大，§12 Q3 裁决）。</para>
    /// <para>解析后按稳定 Id 与既有条目合并去重（依赖 2026-09-14 的 Id 统一），故与开始菜单里
    /// 指向同一目标的快捷方式不会重复出现。</para>
    /// </summary>
    private List<AppItem> ScanDesktopShortcuts()
    {
        if (!_scanDesktopShortcuts)
        {
            return new List<AppItem>();
        }

        var result = new List<AppItem>();
        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                // 深度 1：桌面上的快捷方式（子目录是用户自建的项目文件夹，不猜）
                foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!ShellLinkResolver.IsSupportedFile(file))
                    {
                        continue;
                    }

                    // 复用完整过滤链（排除名 / 文档目标 / 可执行 / 系统工具）
                    var app = ResolveFromPath(file);
                    if (app is not null)
                    {
                        result.Add(app);
                    }
                }
            }
            catch
            {
                // 单个桌面目录不可读不阻断（M10）
            }
        }

        return result;
    }

    /// <summary>把桌面快捷方式并入干净模式结果（按稳定 Id 去重、按名排序；无新增则原样返回）。</summary>
    private List<AppItem> MergeDesktopShortcuts(List<AppItem> existing)
    {
        var extra = ScanDesktopShortcuts();
        if (extra.Count == 0)
        {
            return existing;
        }

        var merged = existing
            .Concat(extra)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger?.Info($"[app-source] 桌面快捷方式并入干净模式：新增候选 {extra.Count} 项 → 合并后 {merged.Count} 项");
        return merged;
    }

    /// <summary>
    /// 口袋目录的本地扫描（深度 3，与主根集一致）。
    /// <para><b>为什么在 C# 侧扫而不是交给引擎</b>：引擎的根集是它自己的 <c>scan_roots</c>，
    /// 不知道用户后加的口袋目录；若只走引擎路径，口袋目录在「后端=engine」时会静默失效。
    /// 本地附加 + 按稳定 Id 合并，保证**切换后端不改变条目集**。</para>
    /// <para>目录不存在 / 不可访问**记 Warn 不静默**（计划 §9：口袋目录失效必须可见）。</para>
    /// </summary>
    private List<AppItem> ScanExtraRoots()
    {
        var roots = _extraScanRoots;
        if (roots.Length == 0)
        {
            return new List<AppItem>();
        }

        var results = new List<AppItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (!Directory.Exists(root))
            {
                _logger?.Warn($"[app-source] 口袋目录不存在或不可访问，已跳过：{root}");
                continue;
            }

            try
            {
                EnumerateExecutablesRecursive(root, results, seenPaths, currentDepth: 0, maxDepth: 3);
            }
            catch (Exception e)
            {
                _logger?.Warn($"[app-source] 口袋目录扫描失败（{root}）：{e.Message}");
            }
        }

        return results;
    }

    /// <summary>把口袋目录并入全程序模式结果（去重交给 <see cref="DedupAndLog"/> 的稳定 Id 去重）。</summary>
    private List<AppItem> MergeExtraRoots(List<AppItem> existing)
    {
        var extra = ScanExtraRoots();
        if (extra.Count == 0)
        {
            return existing;
        }

        existing.AddRange(extra);
        _logger?.Info($"[app-source] 口袋目录并入全程序模式：{extra.Count} 项");
        return existing;
    }

    /// <summary>
    /// 全程序模式的过滤层（**只作用于 <see cref="ScanAllPrograms"/>**，干净模式不受影响）。
    /// <para>默认关闭：过滤会改变条目集（实测 1789 → 更少），属行为变更，需先在真机对比两档效果
    /// 再定默认（§12 Q1 裁决）。开启时记 Info，保证「条目为什么变少」可查（不静默）。</para>
    /// <para>引擎路径与本地路径**都**要过这里——否则切换后端会导致条目集不一致（破坏 M2 平价）。</para>
    /// </summary>
    private List<AppItem> ApplyAllProgramsFilter(List<AppItem> items)
    {
        if (!_filterAllPrograms)
        {
            return items;
        }

        var kept = new List<AppItem>(items.Count);
        foreach (var item in items)
        {
            // 按路径/文件名判定（不用显示名：lnk 描述是文案，见 AppFilterRules 注释）
            var path = string.IsNullOrWhiteSpace(item.TargetPath) ? item.ShortcutPath : item.TargetPath;
            if (AppFilterRules.ShouldFilter(path))
            {
                continue;
            }

            kept.Add(item);
        }

        _logger?.Info($"[app-source] 全程序模式过滤生效：{items.Count} → {kept.Count} 项（滤除 {items.Count - kept.Count}）");
        return kept;
    }

    /// <summary>
    /// 全程序模式过滤开关，默认**关**。
    /// <para>【为什么用环境变量而不是设置键】本包未引用 shell-settings（避免为一个开关引入新包依赖），
    /// 与 <c>AppSourcePlugin.IsEngineBackendEnabled</c>（<c>BETTERDESKTOP_INDEX_BACKEND</c>）同一惯例；
    /// 设置中心的可见开关待与索引后端状态行一并落地（计划 §12 Q1 的「设置内可开」）。</para>
    /// <para>取值 <c>on</c> / <c>true</c> / <c>1</c> 视为开启，其余（含未设）为关。</para>
    /// </summary>
    private static bool IsAllProgramsFilterEnabled()
    {
        var value = Environment.GetEnvironmentVariable("BETTERDESKTOP_APP_FILTER");
        return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按稳定 Id 去重（保留首次出现顺序）。
    /// <para>为什么必须有：Id 统一为「target 路径」后，同一 target 可能被多个文件命中
    /// （同名 exe 的多版本目录、lnk 与其目标同时被枚举、未来桌面 lnk 与开始菜单 lnk 同目标）。
    /// 而下游 <c>DockItemData.Id</c> 是字典键（<c>AppGrabberWindow._containers</c>）——
    /// 重复键会让后一项覆盖前一项的容器、角标与批量编号错位。</para>
    /// </summary>
    internal static List<AppItem> DedupByStableId(List<AppItem> items)
    {
        var seen = new HashSet<AppItemId>();
        var result = new List<AppItem>(items.Count);
        foreach (var item in items)
        {
            if (seen.Add(item.Id))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>去重 + 差异日志（去重不得静默发生：计划 §9 风险表要求记录条目数变化）。</summary>
    private List<AppItem> DedupAndLog(List<AppItem> items)
    {
        var result = DedupByStableId(items);
        if (result.Count != items.Count)
        {
            _logger?.Info($"[app-source] 全程序模式按稳定 Id 去重：{items.Count} → {result.Count} 项");
        }

        return result;
    }

    /// <summary>
    /// 【M2】程序盘点的引擎路径：与本地 <c>EnumerateExecutablesRecursive</c> **逐条对齐** ——
    /// 直接 <see cref="ShellLinkResolver.Resolve"/> 升格，**不跑**四道过滤、不排序、Id 为 <c>"all:" + 路径</c>。
    /// 返回 <c>null</c> = 引擎不可用/构建中 → 调用方回退本地实现。
    /// </summary>
    private List<AppItem>? ScanAllProgramsFromEngine()
    {
        var page = TryListAppsFromEngine();
        if (page is null)
        {
            return null;
        }

        var result = new List<AppItem>();
        foreach (var candidate in page.Apps)
        {
            if (!string.Equals(candidate.Source, AppCandidateMapper.EngineSourceProgramFiles, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = AppCandidateMapper.FromProgramFilesCandidate(candidate);
            if (item is not null)
            {
                result.Add(item);
            }
        }

        _logger?.Info($"[app-source] 程序盘点走索引引擎：候选 {page.Apps.Count} 项 → 收录 {result.Count} 项");
        return result;
    }

    private static void EnumerateExecutablesRecursive(string dir, List<AppItem> sink, HashSet<string> seenPaths, int currentDepth, int maxDepth)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            if (!ShellLinkResolver.IsSupportedFile(file))
            {
                continue;
            }

            if (!seenPaths.Add(file))
            {
                continue;
            }

            try
            {
                var (displayName, targetPath, source) = ShellLinkResolver.Resolve(file);
                sink.Add(new AppItem
                {
                    // Id 与干净模式/固定库同源（见 CreateStableId）。此处曾为 new AppItemId("all:" + file)，
                    // 使全程序模式的条目与固定集合永远对不上 Id（固定态在两种模式间分裂）。
                    Id = CreateStableId(source, file, targetPath),
                    Name = displayName,
                    ShortcutPath = file,
                    TargetPath = string.IsNullOrWhiteSpace(targetPath) ? file : targetPath,
                    Source = source
                });
            }
            catch
            {
                // 单个文件解析失败不阻断。
            }
        }

        if (currentDepth >= maxDepth)
        {
            return;
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return;
        }

        foreach (var sub in subDirs)
        {
            EnumerateExecutablesRecursive(sub, sink, seenPaths, currentDepth + 1, maxDepth);
        }
    }

    private static IEnumerable<string> GetAllProgramRoots()
    {
        var roots = new List<string>();

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf)) roots.Add(pf);
        if (!string.IsNullOrEmpty(pfx86) && !string.Equals(pf, pfx86, StringComparison.OrdinalIgnoreCase)) roots.Add(pfx86);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var programs = Path.Combine(localAppData, "Programs");
            if (Directory.Exists(programs)) roots.Add(programs);
        }

        // 常见第三方安装盘（D:/E: 下的 Program Files），覆盖非系统盘安装的程序。
        // 注意必须同时收 Program Files 与 Program Files (x86)：系统盘的两个目录由
        // SpecialFolder.ProgramFiles / ProgramFilesX86 提供，非系统盘的 (x86) 只能在这里补，
        // 否则装在 D:\Program Files (x86) 下的 32 位程序永远扫不到（2026-09-13 实测缺口）。
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            foreach (var leaf in new[] { "Program Files", "Program Files (x86)" })
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, leaf);
                if (Directory.Exists(candidate) && !roots.Any(r => r.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(candidate);
                }
            }
        }

        // 去重（大小写不敏感）并排除 Windows 系统目录。
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsRoot = Path.GetPathRoot(systemDir);
        var cleaned = new List<string>();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (windowsRoot is not null && r.TrimEnd('\\').Equals(windowsRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (systemDir is not null && r.TrimEnd('\\').Equals(systemDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.Add(r)) cleaned.Add(r);
        }

        return cleaned;
    }

    /// <inheritdoc />
    public ProgramFolder GetProgramTree()
    {
        lock (_cacheGate)
        {
            if (_programTreeCache is not null)
            {
                return _programTreeCache;
            }
        }

        ProgramFolder tree;
        try
        {
            var directories = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
            };

            var trees = directories.Where(Directory.Exists).Select(BuildFolder).ToList();
            if (trees.Count == 0)
            {
                tree = new ProgramFolder { Name = "Programs" };
            }
            else if (trees.Count == 1)
            {
                tree = trees[0];
            }
            else
            {
                tree = MergeFolders(trees);
            }
        }
        catch
        {
            tree = new ProgramFolder { Name = "Programs" };
        }

        lock (_cacheGate)
        {
            _programTreeCache = tree;
        }

        return tree;
    }

    private ProgramFolder BuildFolder(string path)
    {
        var folder = new ProgramFolder
        {
            Name = Path.GetFileName(path),
            FullPath = path
        };

        var subFolders = new List<ProgramFolder>();
        var items = new List<AppItem>();

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(path))
            {
                subFolders.Add(BuildFolder(sub));
            }
        }
        catch
        {
            // 子目录遍历失败跳过该目录层级。
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (!ShellLinkResolver.IsSupportedFile(file))
                {
                    continue;
                }

                var app = ResolveFromPath(file);
                if (app is not null)
                {
                    items.Add(app);
                }
            }
        }
        catch
        {
            // 文件遍历失败跳过该目录层级。
        }

        return folder with
        {
            SubFolders = subFolders.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Items = items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// 合并用户 / 公共两个 Programs 树：同名子文件夹递归合并，同 Id 应用取先出现者。
    /// </summary>
    private static ProgramFolder MergeFolders(IReadOnlyList<ProgramFolder> trees)
    {
        var subMap = new Dictionary<string, ProgramFolder>(StringComparer.OrdinalIgnoreCase);
        var itemMap = new Dictionary<AppItemId, AppItem>();

        foreach (var tree in trees)
        {
            foreach (var sub in tree.SubFolders)
            {
                if (subMap.TryGetValue(sub.Name, out var existing))
                {
                    subMap[sub.Name] = MergeFolders(new[] { existing, sub });
                }
                else
                {
                    subMap[sub.Name] = sub;
                }
            }

            foreach (var item in tree.Items)
            {
                itemMap[item.Id] = item;
            }
        }

        return new ProgramFolder
        {
            Name = trees.Count > 0 ? trees[0].Name : "Programs",
            FullPath = trees.Count > 0 ? trees[0].FullPath : string.Empty,
            SubFolders = subMap.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Items = itemMap.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
}
