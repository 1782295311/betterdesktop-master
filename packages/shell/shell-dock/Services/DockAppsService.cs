using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Pinning.Contracts;
using AppSourceModels = BetterDesktop.Shell.AppSource.Models;
using ShellLinkResolver = BetterDesktop.Shell.AppSource.Services.ShellLinkResolver;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 应用服务门面。
/// 整合 <see cref="IAppSourceService"/> 的扫描能力与固定列表管理。
/// UI 层通过此门面统一访问，屏蔽内部实现细节。
/// </summary>
public sealed class DockAppsService : IDockAppsService
{
    private readonly IAppSourceService _appSourceService;
    private readonly IPinningService _pinningService;
    private readonly HashSet<DockItemId> _excluded = new();
    private readonly object _sync = new();
    private readonly string _storagePath;
    private readonly string _excludedPath;

    /// <inheritdoc />
    public event EventHandler? PinnedChanged;

    private void RaisePinnedChanged()
    {
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    public DockAppsService(IAppSourceService appSourceService, IPinningService pinningService, string storagePath)
    {
        _appSourceService = appSourceService ?? throw new ArgumentNullException(nameof(appSourceService));
        _pinningService = pinningService ?? throw new ArgumentNullException(nameof(pinningService));
        _storagePath = storagePath;
        _excludedPath = Path.Combine(Path.GetDirectoryName(storagePath) ?? Path.GetTempPath(), "grabber-excluded.txt");

        // 固定列表现在由通用固定服务（IPinningService）承载；zone=="dock" 的变更同步到本门面的 PinnedChanged。
        _pinningService.PinnedChanged += OnPinningChanged;
        LoadExcluded();
    }

    private void OnPinningChanged(object? sender, PinnedChangedEventArgs e)
    {
        if (string.Equals(e.Zone, "dock", StringComparison.OrdinalIgnoreCase))
        {
            RaisePinnedChanged();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DockItemData> Pinned
    {
        get
        {
            try
            {
                // 固定列表现由通用固定服务承载；按 zone=="dock" 读取并零失真重建 DockItemData。
                // ① 过滤"已失效"固定项（应用真被卸载，见 IsPinnedItemValid 两级判定）→ 让位；
                // ② 同名应用分组去重：Edge 等多开/自动更新会在不同版本时期留下多个固定条目，
                //    失效旧条目的图标从失效路径提取失败 → dock 空位（用户实测 bug）。
                //    组内有效路径优先显示；全部失效才显示第一个（v2 已保证注册表命中，点击自愈）。
                return _pinningService.GetPinned("dock")
                    .Where(IsPinnedItemValid)
                    .GroupBy(DedupKey, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g =>
                    {
                        var valid = g.Where(IsPathValid).ToList();
                        return valid.Count > 0 ? valid : g.Take(1);
                    })
                    .Select(ToDockItemData)
                    .ToList();
            }
            catch
            {
                // 读取固定列表异常时降级为空，避免阻断 Dock 启动（M10）。
                return Array.Empty<DockItemData>();
            }
        }
    }

    /// <inheritdoc />
    public void Load()
    {
        try
        {
            // 固定列表交由通用固定服务加载（含旧 dock-pinned.json 迁移）。
            _pinningService.Load();
        }
        catch
        {
            // 固定服务加载失败不影响排除列表与扫描能力（M10）。
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        try
        {
            // 固定列表落盘交由通用固定服务（pinning.json，含迁移后落盘）。
            _pinningService.Save();
        }
        catch
        {
            // 固定服务持久化失败时不上抛，避免中断主流程（M10）。
        }

        SaveExcluded();
    }

    /// <inheritdoc />
    public void AddByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!ShellLinkResolver.IsSupportedFile(path))
        {
            return;
        }

        var appItem = _appSourceService.ResolveFromPath(path);
        if (appItem is null)
        {
            return;
        }

        // 固定到 "dock" zone；去重与 PinnedChanged（zone=="dock"）由通用固定服务负责。
        _pinningService.Pin("dock", appItem);
    }

    /// <inheritdoc />
    public void RemoveById(DockItemId id)
    {
        // 取消固定 "dock" zone；PinnedChanged 由通用固定服务经订阅转发。
        _pinningService.Unpin("dock", new AppItemId(id.Value));
    }

    /// <inheritdoc />
    public void Reorder(IReadOnlyList<DockItemId> order)
    {
        if (order is null || order.Count == 0)
        {
            return;
        }

        // 按 DockItemId 还原为 AppItemId 后提交 "dock" zone 重排；变更由通用固定服务经订阅转发。
        var appIds = order.Select(id => new AppItemId(id.Value)).ToList();
        _pinningService.Reorder("dock", appIds);
    }

    /// <inheritdoc />
    /// <summary>
    /// 固定项是否仍有效（渲染层过滤）：被固定的应用真被卸载后，其快照仍在固定列表里，
    /// 若不过滤，Dock 固定区会永远占着这个位置（用户实测 bug）。
    ///
    /// ⚠️ 判定不能只靠 File.Exists——Edge/Chrome 等版本化目录应用（多开/自动更新）固定时存的是
    /// 当时版本路径（如 ...\Edge\Application\151.0.4129.101\msedge.exe），更新后旧版本目录被
    /// 清理，路径失效 ≠ 应用被删（实测：Edge 152.x 仍在卸载注册表，固定项却被误杀、固定位空掉）。
    ///
    /// 两级判定：
    ///   1) 路径仍有效 → 保留（多数情况直接短路，不触达注册表）。
    ///   2) 路径失效 → 反查已安装注册表二次确认（<see cref="GetInstalledAppsCache"/>）：
    ///      名称/目录命中已安装列表 → 应用仍在（版本更新/临时失效）→ 保留；
    ///      未命中 → 真被卸载 → 剔除让位。反查失败保守保留（宁可多显示，不可误杀）。
    /// 只剔除显示、不删持久化数据——应用重新安装恢复路径后，固定项自动回来。
    /// UWP 以 AUMID 为主键，无法文件判定，一律保留。
    /// </summary>
    private bool IsPinnedItemValid(PinnedItem p)
    {
        var a = p.AppItem;
        if (AppSourceConverter.ToDockAppType(a.Source, a.TargetPath) == DockAppType.Uwp)
        {
            return true;
        }

        if ((!string.IsNullOrWhiteSpace(a.TargetPath) && File.Exists(a.TargetPath))
            || (!string.IsNullOrWhiteSpace(a.ShortcutPath) && File.Exists(a.ShortcutPath)))
        {
            return true;
        }

        return IsStillInstalled(a);
    }

    /// <summary>
    /// 同名固定项去重键 = 归一化名称 + 应用安装根目录（exe 所在目录的父目录）。
    /// 为什么带目录：两个「原神」（正式服 / B 服）名称相同但目录不同，是不同应用，不能去重；
    /// Edge 等版本化目录应用（...\Edge\Application\151.x / 152.x）根目录同为 Application，才视为同一应用。
    /// </summary>
    private static string DedupKey(PinnedItem p)
    {
        var a = p.AppItem;
        var dir = string.IsNullOrWhiteSpace(a.TargetPath)
            ? string.Empty
            : System.IO.Path.GetDirectoryName(a.TargetPath);
        var root = string.IsNullOrEmpty(dir)
            ? string.Empty
            : System.IO.Path.GetDirectoryName(dir);
        return NormalizeKey(a.Name) + "|" + NormalizeKey(root);
    }

    /// <summary>固定项快照路径是否仍存在（纯文件判定；供同名组内"有效优先"用）。</summary>
    private static bool IsPathValid(PinnedItem p)
    {
        var a = p.AppItem;
        return (!string.IsNullOrWhiteSpace(a.TargetPath) && File.Exists(a.TargetPath))
            || (!string.IsNullOrWhiteSpace(a.ShortcutPath) && File.Exists(a.ShortcutPath));
    }

    /// <summary>路径失效时反查已安装注册表：应用仍在已安装列表（名称或 exe 目录命中）→ true。</summary>
    private bool IsStillInstalled(BetterDesktop.Shell.AppSource.Models.AppItem a)
    {
        try
        {
            var nameKey = NormalizeKey(a.Name);
            var dirKey = string.IsNullOrWhiteSpace(a.TargetPath)
                ? string.Empty
                : NormalizeKey(System.IO.Path.GetDirectoryName(a.TargetPath));

            foreach (var installed in GetInstalledAppsCache())
            {
                if (!string.IsNullOrEmpty(nameKey) && NormalizeKey(installed.Name) == nameKey)
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(dirKey)
                    && !string.IsNullOrWhiteSpace(installed.TargetPath)
                    && NormalizeKey(System.IO.Path.GetDirectoryName(installed.TargetPath)) == dirKey)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 反查失败：保守保留（不误杀），等下次刷新再判。
            return true;
        }

        return false;
    }

    /// <summary>
    /// 固定项启动路径自愈：快照路径已失效（版本更新/临时移除）时，按名称反查已安装列表取最新路径。
    /// 返回修正后的副本（携带最新 TargetPath/ShortcutPath）；路径仍有效返回原项；
    /// 未命中（应用真被卸载）返回 null，调用方走原启动逻辑（失败不崩溃）。
    /// </summary>
    public DockItemData? TryRefreshStalePath(DockItemData item)
    {
        if ((!string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath))
            || (!string.IsNullOrWhiteSpace(item.ShortcutPath) && File.Exists(item.ShortcutPath)))
        {
            return item;
        }

        try
        {
            var nameKey = NormalizeKey(item.Name);
            if (string.IsNullOrEmpty(nameKey))
            {
                return null;
            }

            foreach (var app in GetInstalledAppsCache())
            {
                if (NormalizeKey(app.Name) != nameKey)
                {
                    continue;
                }

                return new DockItemData
                {
                    Id = item.Id,
                    Name = app.Name,
                    ShortcutPath = app.ShortcutPath,
                    TargetPath = app.TargetPath,
                    AppType = app.AppType,
                    AppUserModelId = app.AppUserModelId,
                    IconCacheKey = app.IconCacheKey,
                    IsPinned = true,
                    UninstallCommand = app.UninstallCommand
                };
            }
        }
        catch
        {
            // 自愈失败不阻断原启动逻辑。
        }

        return null;
    }

    // 已安装列表缓存（注册表扫描较重，Pinned 读取/点击高频；30s TTL 足够，失效项才触达）。
    private IReadOnlyList<DockItemData>? _installedCache;
    private DateTime _installedCacheAt;
    private const double InstalledCacheTtlSeconds = 30;

    private IReadOnlyList<DockItemData> GetInstalledAppsCache()
    {
        var now = DateTime.UtcNow;
        if (_installedCache is null || (now - _installedCacheAt).TotalSeconds > InstalledCacheTtlSeconds)
        {
            _installedCache = ScanInstalledApps();
            _installedCacheAt = now;
        }

        return _installedCache;
    }

    /// <summary>
    /// 将通用固定项（<see cref="PinnedItem"/>，承载 AppItem 快照）零失真重建为 Dock 展示模型。
    /// 与旧 DockPinnedService.AddByPath 的字段映射一致，保证 Dock 图标/名称/快捷方式行为不变。
    /// </summary>
    private static DockItemData ToDockItemData(PinnedItem p)
    {
        var a = p.AppItem;
        return new DockItemData
        {
            Id = new DockItemId(a.Id.ToString()),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            AppType = AppSourceConverter.ToDockAppType(a.Source, a.TargetPath),
            AppUserModelId = a.AppUserModelId,
            IconCacheKey = a.IconCacheKey,
            IsPinned = true,
            UninstallCommand = a.UninstallCommand
        };
    }

    /// <summary>
    /// 将通用应用项（<see cref="AppItem"/>）重建为 Dock 展示模型（未固定）。
    /// 供转发 IAppSourceService 扫描结果时复用，字段映射与旧实现一致。
    /// </summary>
    private static DockItemData ToDockItemData(AppItem a)
    {
        return new DockItemData
        {
            Id = new DockItemId(a.Id.ToString()),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            AppType = AppSourceConverter.ToDockAppType(a.Source, a.TargetPath),
            AppUserModelId = a.AppUserModelId,
            IconCacheKey = a.IconCacheKey,
            IsPinned = false,
            UninstallCommand = a.UninstallCommand
        };
    }

    public IReadOnlyList<DockItemData> ScanStartMenu()
    {
        var apps = _appSourceService.ScanStartMenu();

        // 开始菜单项本身不携带卸载命令；为让干净模式下"全部/开始菜单"视图也能卸载，
        // 反查已安装注册表（带缓存）的卸载信息：先按规范化名称精确匹配，未命中再按
        // 可执行文件所在目录匹配（名称差异大但同目录的程序），尽量全覆盖。
        var (uninstallByName, uninstallByDir) = BuildUninstallIndex();

        return apps.Select(a =>
        {
            var cmd = a.UninstallCommand;
            if (string.IsNullOrWhiteSpace(cmd))
            {
                uninstallByName.TryGetValue(NormalizeKey(a.Name), out cmd!);
            }

            if (string.IsNullOrWhiteSpace(cmd) && !string.IsNullOrWhiteSpace(a.TargetPath))
            {
                var dir = Path.GetDirectoryName(a.TargetPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    uninstallByDir.TryGetValue(NormalizeKey(dir), out cmd!);
                }
            }

            return new DockItemData
            {
                Id = new DockItemId(a.Id.ToString()),
                Name = a.Name,
                ShortcutPath = a.ShortcutPath,
                TargetPath = a.TargetPath,
                AppType = AppSourceConverter.ToDockAppType(a.Source, a.TargetPath),
                IsPinned = false,
                UninstallCommand = cmd
            };
        }).Where(x => !IsExcluded(x.Id)).ToList();
    }

    /// <summary>
    /// 构建"已安装程序 → 卸载命令"的双索引（按规范化显示名 + 按可执行文件目录），
    /// 供开始菜单项反查卸载入口。复用 <see cref="IAppSourceService.ScanInstalledApps"/> 的缓存，
    /// 避免重复读注册表。
    /// </summary>
    private (Dictionary<string, string> ByName, Dictionary<string, string> ByDir) BuildUninstallIndex()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var installed in _appSourceService.ScanInstalledApps())
            {
                if (string.IsNullOrWhiteSpace(installed.UninstallCommand))
                {
                    continue;
                }

                var nameKey = NormalizeKey(installed.Name);
                if (!string.IsNullOrEmpty(nameKey) && !byName.ContainsKey(nameKey))
                {
                    byName[nameKey] = installed.UninstallCommand!;
                }

                var exeDir = Path.GetDirectoryName(installed.TargetPath);
                if (!string.IsNullOrWhiteSpace(exeDir))
                {
                    var dirKey = NormalizeKey(exeDir);
                    if (!string.IsNullOrEmpty(dirKey) && !byDir.ContainsKey(dirKey))
                    {
                        byDir[dirKey] = installed.UninstallCommand!;
                    }
                }
            }
        }
        catch
        {
            // 反查失败不影响开始菜单主流程。
        }

        return (byName, byDir);
    }

    private static string NormalizeKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name!)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public IReadOnlyList<DockItemData> ScanInstalledApps()
    {
        var apps = _appSourceService.ScanInstalledApps();
        return apps.Select(a => new DockItemData
        {
            Id = new DockItemId(a.Id.ToString()),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            AppType = AppSourceConverter.ToDockAppType(a.Source, a.TargetPath),
            IsPinned = false,
            UninstallCommand = a.UninstallCommand
        }).Where(x => !IsExcluded(x.Id)).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 全程序大盘点：遍历常见程序根目录，按文件物理所在文件夹分组。
    /// 排除 Windows 系统目录（避免把系统工具大量捞入），跳过无权限/异常的目录与文件。
    /// 结果与干净模式（开始菜单+已安装注册表）互补，覆盖"装在非标准位置但磁盘上存在"的程序。
    /// 实际扫描逻辑已下沉到 <see cref="IAppSourceService.ScanAllPrograms"/>（本方法仅转发并转换模型）。
    /// </remarks>
    public IReadOnlyList<DockItemData> ScanAllPrograms()
    {
        return _appSourceService.ScanAllPrograms().Select(ToDockItemData).ToList();
    }

    /// <inheritdoc />
    /// <remarks>委托 <see cref="_appSourceService"/> 的"已见"判定（_seen 持久化集合），
    /// 只返回真正新增、且未固定的应用——避免旧方案（自造"未固定=新装"定义）导致关掉弹窗后下一轮轮询又弹出的无限循环。</remarks>
    public IReadOnlyList<DockItemData> GetNewlyInstalledApps()
    {
        var newly = _appSourceService.GetNewlyInstalledApps();
        if (newly.Count == 0)
        {
            return Array.Empty<DockItemData>();
        }

        // 排除已固定的（用户已主动固定过的不再提醒）
        var pinnedIds = Pinned.Select(x => x.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = newly.Where(a => !pinnedIds.Contains(a.Id.ToString())).ToList();

        return candidates.Select(a => new DockItemData
        {
            Id = new DockItemId(a.Id.ToString()),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            AppType = AppSourceConverter.ToDockAppType(a.Source, a.TargetPath),
            IsPinned = false,
            UninstallCommand = a.UninstallCommand
        }).ToList();
    }

    /// <inheritdoc />
    public void MarkInstalledAppsSeen(IReadOnlyList<DockItemData> apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return;
        }

        // 将 DockItemData 转换为 AppItem 以委托给 IAppSourceService
        var appItems = apps.Select(a => new AppItem
        {
            Id = new AppItemId(a.Id.ToString()),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            Source = ConvertDockAppTypeToAppSource(a.AppType)
        }).ToList();

        _appSourceService.MarkAppsSeen(appItems);
    }

    /// <inheritdoc />
    public void ExcludeApp(DockItemData app)
    {
        if (app is null)
        {
            return;
        }

        bool added;
        lock (_sync)
        {
            added = _excluded.Add(app.Id);
        }

        if (added)
        {
            SaveExcluded();
        }
    }

    /// <inheritdoc />
    public bool IsExcluded(DockItemId id)
    {
        lock (_sync)
        {
            return _excluded.Contains(id);
        }
    }

    /// <inheritdoc />
    public void InvalidateScanCache()
    {
        _appSourceService.InvalidateCache();
    }

    private void LoadExcluded()
    {
        try
        {
            if (!File.Exists(_excludedPath))
            {
                return;
            }

            lock (_sync)
            {
                _excluded.Clear();
                foreach (var line in File.ReadAllLines(_excludedPath))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _excluded.Add(new DockItemId(line.Trim()));
                    }
                }
            }
        }
        catch
        {
            // 排除列表读取失败保持空集合。
        }
    }

    private void SaveExcluded()
    {
        try
        {
            var dir = Path.GetDirectoryName(_excludedPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            List<DockItemId> snapshot;
            lock (_sync)
            {
                snapshot = _excluded.ToList();
            }

            File.WriteAllLines(_excludedPath, snapshot.Select(x => x.ToString()));
        }
        catch
        {
            // 排除列表保存失败不阻断。
        }
    }

    private static AppSourceModels.AppSource ConvertDockAppTypeToAppSource(DockAppType appType)
    {
        return appType switch
        {
            DockAppType.Uwp => AppSourceModels.AppSource.Store,
            _ => AppSourceModels.AppSource.Installed
        };
    }
}
