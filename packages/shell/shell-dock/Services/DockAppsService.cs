using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Pinning.Contracts;
using Microsoft.Win32;
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
                // ① 健康判定（见 EvaluateHealth）：Orphaned → 让位（不显示，但保留快照）；
                //    Healable → 用重绑后的新路径显示，并**静默回写**快照（下次读即 Healthy，不必再重绑）。
                // ② 同名多版本去重：Edge 等自动更新会在 store 里留下多个条目（旧 Id 与新 Id 并存），
                //    组内路径有效者优先显示，避免 dock 出现空位（用户实测 bug）。
                var items = new List<DockItemData>();
                foreach (var p in _pinningService.GetPinned("dock"))
                {
                    var health = EvaluateHealth(p);
                    if (health.State == PinnedHealthState.Orphaned)
                    {
                        continue;
                    }

                    var item = ToDockItemData(p);
                    if (health.State == PinnedHealthState.Healable && !string.IsNullOrWhiteSpace(health.CurrentPath))
                    {
                        item = ApplyPath(item, health.CurrentPath);
                        WriteBackSnapshot(p, item);
                    }

                    items.Add(item);
                }

                return items
                    .GroupBy(DedupKey, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g =>
                    {
                        var valid = g.Where(IsPathUsable).ToList();
                        return valid.Count > 0 ? valid : g.Take(1);
                    })
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

    /// <summary>
    /// 固定项健康判定（三态，**只读**：不修改任何状态）。
    /// <para><b>为什么不能只靠 <c>File.Exists</c></b>：Edge/Chrome 等版本化目录应用更新后旧版本目录被清理，
    /// 「路径失效」≠「应用被删」（实测：Edge 152.x 仍在卸载注册表，固定项却被误杀、固定位空掉）。</para>
    /// <para>判定链：路径可用 → <see cref="PinnedHealthState.Healthy"/>；否则多级重绑
    /// （<see cref="PinnedRebindResolver"/>）命中 → <see cref="PinnedHealthState.Healable"/>；
    /// 全失配 → <see cref="PinnedHealthState.Orphaned"/>（让位但**保留快照**，重装同路径即自动回来）。</para>
    /// <para>Store 应用按 AUMID 存在性判定（旧实现一律保留 → 卸载后永久占位）；Store 列表为空表示
    /// **未知**（AppsFolderSource 不可用），此时保守保留，不据此判孤。</para>
    /// </summary>
    private PinnedHealth EvaluateHealth(PinnedItem p)
    {
        var item = ToDockItemData(p);

        if (IsPathUsable(item))
        {
            return new PinnedHealth
            {
                Id = item.Id,
                Name = item.Name,
                State = PinnedHealthState.Healthy,
                CurrentPath = CurrentPath(item),
                Detail = "快照路径有效",
            };
        }

        var rebound = PinnedRebindResolver.Resolve(item, GetRebindSources());
        if (rebound is null)
        {
            return new PinnedHealth
            {
                Id = item.Id,
                Name = item.Name,
                State = PinnedHealthState.Orphaned,
                CurrentPath = null,
                Detail = "原路径失效且各级重绑均未命中：视为已卸载（快照保留，重装同路径自动回来）",
            };
        }

        return new PinnedHealth
        {
            Id = item.Id,
            Name = item.Name,
            State = PinnedHealthState.Healable,
            CurrentPath = rebound,
            Detail = $"原路径失效，已重绑到：{rebound}",
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<PinnedHealth> GetPinnedHealth()
    {
        try
        {
            return _pinningService.GetPinned("dock").Select(EvaluateHealth).ToList();
        }
        catch
        {
            // 健康查询失败返回空（不阻断设置窗口渲染，M10）
            return Array.Empty<PinnedHealth>();
        }
    }

    /// <inheritdoc />
    public void RebindTo(DockItemId id, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var existing = _pinningService.GetPinned("dock")
                .FirstOrDefault(x => new DockItemId(x.AppItem.Id.ToString()) == id);
            if (existing is null)
            {
                return;
            }

            var resolved = _appSourceService.ResolveFromPath(path);
            if (resolved is null)
            {
                return;
            }

            // 静默回写（保留原主键：主键是「这次固定」的身份，分组/排序/已见集合都以它为键；
            // 换主键会连带丢掉分组归属——见 UpdateSnapshot 注释）
            _pinningService.UpdateSnapshot("dock", existing.AppItem.Id, resolved);
        }
        catch
        {
            // 手动重绑失败静默（设置窗口不因单项失败而崩，M10）
        }
    }

    /// <inheritdoc />
    public int PurgeOrphaned()
    {
        try
        {
            var orphans = _pinningService.GetPinned("dock")
                .Where(p => EvaluateHealth(p).State == PinnedHealthState.Orphaned)
                .ToList();

            foreach (var orphan in orphans)
            {
                // 这里走 Unpin：用户显式清理，是**删除持久化数据**（与渲染层"让位"不同）
                _pinningService.Unpin("dock", orphan.AppItem.Id);
            }

            return orphans.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 同名固定项去重键 = 归一化名称 + 应用安装根目录（exe 所在目录的父目录）。
    /// 为什么带目录：两个「原神」（正式服 / B 服）名称相同但目录不同，是不同应用，不能去重；
    /// Edge 等版本化目录应用（...\Edge\Application\151.x / 152.x）根目录同为 Application，才视为同一应用。
    /// </summary>
    private static string DedupKey(DockItemData item)
    {
        var dir = string.IsNullOrWhiteSpace(item.TargetPath)
            ? string.Empty
            : System.IO.Path.GetDirectoryName(item.TargetPath);
        var root = string.IsNullOrEmpty(dir)
            ? string.Empty
            : System.IO.Path.GetDirectoryName(dir);
        return NormalizeKey(item.Name) + "|" + NormalizeKey(root);
    }

    /// <summary>快照路径是否可用（纯文件判定；供同名组内「有效优先」用）。</summary>
    private static bool IsPathUsable(DockItemData item)
        => (!string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath))
            || (!string.IsNullOrWhiteSpace(item.ShortcutPath) && File.Exists(item.ShortcutPath));

    /// <summary>当前可用路径（优先真实目标，其次快捷方式）。</summary>
    private static string? CurrentPath(DockItemData item)
    {
        if (!string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath))
        {
            return item.TargetPath;
        }

        return !string.IsNullOrWhiteSpace(item.ShortcutPath) && File.Exists(item.ShortcutPath)
            ? item.ShortcutPath
            : null;
    }

    /// <summary>用重绑后的新路径改写展示项（其余字段保持不变）。</summary>
    private static DockItemData ApplyPath(DockItemData item, string path)
    {
        var isShortcutTarget = !string.IsNullOrWhiteSpace(item.ShortcutPath)
            && string.Equals(item.ShortcutPath, item.TargetPath, StringComparison.OrdinalIgnoreCase);

        return new DockItemData
        {
            Id = item.Id,
            Name = item.Name,
            ShortcutPath = isShortcutTarget ? path : item.ShortcutPath,
            TargetPath = path,
            AppType = item.AppType,
            AppUserModelId = item.AppUserModelId,
            IconCacheKey = item.IconCacheKey,
            IsPinned = item.IsPinned,
            UninstallCommand = item.UninstallCommand,
        };
    }

    /// <summary>
    /// 把自愈结果静默回写持久化快照（**保留原主键**）。
    /// <para>回写发生在「读取固定列表」的过程中，故走 <see cref="IPinningService.UpdateSnapshot"/>
    /// （不发 PinnedChanged）——否则会形成「事件 → 重绘 → 再读取 → 再回写」的重入。</para>
    /// <para>主键保留是刻意的：主键是这次固定的身份，分组归属（AppGroupStore 按主键存）与排序都以它为键；
    /// 改成新路径派生的主键会让分组归属丢失。代价：被自愈项的「提取器绿点」可能对不上
    /// （扫描出的条目带的是新路径主键）——属已知限制，需路径无关锚点才能根治。</para>
    /// </summary>
    private void WriteBackSnapshot(PinnedItem p, DockItemData healed)
    {
        try
        {
            var a = p.AppItem;
            var updated = a with
            {
                TargetPath = string.IsNullOrWhiteSpace(healed.TargetPath) ? a.TargetPath : healed.TargetPath,
                ShortcutPath = string.IsNullOrWhiteSpace(healed.ShortcutPath) ? a.ShortcutPath : healed.ShortcutPath,
                UninstallCommand = healed.UninstallCommand,
            };

            _pinningService.UpdateSnapshot("dock", a.Id, updated);
        }
        catch
        {
            // 回写失败不影响本次显示（下次读取会再试，M10）
        }
    }

    /// <summary>
    /// 固定项启动路径自愈：快照路径已失效时按 <see cref="PinnedRebindResolver"/> **多级重绑**取新路径。
    /// 返回修正后的副本（携带新 TargetPath）；路径仍有效返回原项；全失配（应用真被卸载）返回 null，
    /// 调用方走原启动逻辑（失败不崩溃）。
    /// </summary>
    public DockItemData? TryRefreshStalePath(DockItemData item)
    {
        if (IsPathUsable(item))
        {
            return item;
        }

        try
        {
            var rebound = PinnedRebindResolver.Resolve(item, GetRebindSources());
            return rebound is null ? null : ApplyPath(item, rebound);
        }
        catch
        {
            // 自愈失败不阻断原启动逻辑（M10）
            return null;
        }
    }

    // 重绑事实来源缓存：App Paths / 已安装列表 / Store AUMID 都来自注册表与 Shell，代价不低。
    // 30s TTL 与已安装列表缓存同档；只在出现失效项时才被构建（健康项不触达）。
    private PinnedRebindSources? _rebindSources;
    private DateTime _rebindSourcesAt;
    private const double RebindSourcesTtlSeconds = 30;

    private PinnedRebindSources GetRebindSources()
    {
        var now = DateTime.UtcNow;
        if (_rebindSources is null || (now - _rebindSourcesAt).TotalSeconds > RebindSourcesTtlSeconds)
        {
            _rebindSources = new PinnedRebindSources
            {
                Installed = GetInstalledAppsCache(),
                AppPaths = BuildAppPathsIndex(),
                StoreAppIds = BuildStoreAppIdSet(),
                FileExists = File.Exists,
            };
            _rebindSourcesAt = now;
        }

        return _rebindSources;
    }

    /// <summary>
    /// App Paths 索引：<c>HKLM|HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\&lt;exe 名&gt;</c>
    /// 默认值 = 完整路径。安装器注册的规范入口，**exe 名未变**的应用可由此救回（含改了安装目录的情况）。
    /// </summary>
    private static Dictionary<string, string> BuildAppPathsIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var views = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null)
                {
                    continue;
                }

                foreach (var subName in appPaths.GetSubKeyNames())
                {
                    using var sub = appPaths.OpenSubKey(subName);
                    if (sub?.GetValue(null) is not string path || string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    // 去掉可能的引号（值常写成 "C:\...\a.exe"）
                    index.TryAdd(subName, path.Trim('"', ' '));
                }
            }
            catch
            {
                // 单个注册表视图读取失败不阻断（M10）
            }
        }

        return index;
    }

    /// <summary>
    /// 已安装 Store 应用 AUMID 集合（判定 Store 固定项是否仍在，替代旧实现的「一律保留」）。
    /// 返回**空集合表示未知**（AppsFolderSource 不可用）→ 调用方保守保留，不据此判孤。
    /// </summary>
    private IReadOnlySet<string> BuildStoreAppIdSet()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var app in _appSourceService.ScanStoreApps())
            {
                var key = string.IsNullOrWhiteSpace(app.TargetPath) ? app.Id.ToString() : app.TargetPath;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    ids.Add(key);
                }
            }
        }
        catch
        {
            // 取不到 Store 列表 → 返回空集 = 未知（不误杀，M10）
        }

        return ids;
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

        // 重绑事实来源同样失效：安装/卸载后 App Paths 与 Store 列表都可能已变，
        // 不失效会让固定项在长达 30s 内按旧事实判定（误报孤或漏判自愈）。
        _rebindSources = null;
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
