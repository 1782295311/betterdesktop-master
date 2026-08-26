using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.AppSource.Services;

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

    public AppSourceService(string? dataDirectory = null)
    {
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

        var ordered = result
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_cacheGate)
        {
            _startMenuCache = ordered;
            _startMenuCacheAt = DateTime.UtcNow;
        }

        return ordered;
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

    private static AppItemId CreateStableId(BetterDesktop.Shell.AppSource.Models.AppSource source, string shortcutPath, string targetPath)
    {
        var keySource = source switch
        {
            BetterDesktop.Shell.AppSource.Models.AppSource.Store => targetPath,
            _ => string.IsNullOrWhiteSpace(targetPath) ? shortcutPath : targetPath
        };

        return new AppItemId(keySource ?? shortcutPath ?? string.Empty);
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

        return results;
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
                    Id = new AppItemId("all:" + file),
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
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var candidate = Path.Combine(drive.RootDirectory.FullName, "Program Files");
            if (Directory.Exists(candidate) && !roots.Any(r => r.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(candidate);
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
