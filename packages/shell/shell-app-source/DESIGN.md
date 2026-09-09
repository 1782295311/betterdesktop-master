# shell-app-source（应用数据来源插件 · 深度设计稿）

> 状态：**部分已实现**（见 §0 现状盘点），本文给出完整目标设计，标 ★ 为待办。

## 0. 现状盘点（2026-08-22 实地核查）

已实现（真实代码）：

- `IAppSourceService`：`ScanStartMenu` / `ScanInstalledApps` / `GetNewlyInstalledApps` / `MarkAppsSeen` / `ResolveFromPath`，语义与实现一致。
- `AppSourceService`：开始菜单扫描（用户+公共 Programs）、卸载注册表三视图（HKCU / HKLM / WOW6432Node）、排除列表（中英文）、系统组件/系统工具/文档目标过滤、`installed-seen.json` 快照（首次全量记为已见）、DisplayIcon→InstallLocation 回退解析。
- `ShellLinkResolver`：LNK / URL / EXE 解析。
- 模型：`AppItem` / `AppItemId`（强类型）/ `AppSource`（StartMenu | Installed | Store | UserAdded）。

缺失 / 待办（★）：

- ★ `IAppIconService`：README 已声明但**代码中不存在**（图标实际在 shell-dock 的 `IDockIconService` + `Win32IconProvider`）——应上移到本插件，消除职责重复。
- ★ 没有独立 `IPlugin` 入口：当前由 `DockPlugin` 手动 `new AppSourceService(...)` 并 `context.Provide<IAppSourceService>`，导致应用来源与 Dock 强耦合。
- ★ UWP/AppX 完整枚举（AUMID）未做（`AppSource.Store` 分支目前为空）。
- ★ `AppSource.UserAdded` 尚无写入口。

## 0.2 重复实现盘点（Dock 侧残留，待上移/合并）

实地核查（2026-08-22）确认 `shell-dock` 内残留两处与 app-source 职责重叠的实现：

1. **`Services/ShellLinkResolver.cs`（重复文件）**：与 `shell-app-source/Services/ShellLinkResolver.cs` 是同一逻辑（LNK/URL/EXE/AppRef 解析）的分叉拷贝，仅返回类型不同（app-source 版返回 `AppSource` 枚举；dock 版返回 `DockAppType`，且 URL→Url）。dock 版被 **8 处**使用：AppGrabberWindow / DockWindow / LaunchpadWindow / MinimalDockWindow / NewAppsNotificationWindow / DockAppsService / DockPinnedService / Win32IconProvider。
2. **图标提取栈**：`IIconProvider` + `Win32IconProvider` + `DockIconService`（缓存/预取）全在 dock；app-source 目录 `find -iname "*icon*"` 为空——即 README 声称的 `IAppIconService` 从未实现，图标职责整体错位在 dock。

**合并方案（★）**：

- 以 app-source 版 `ShellLinkResolver` 为唯一实现；dock 侧统一映射 `AppSource → DockAppType`（需补 `Url` 映射：URL 快捷方式在 AppSource 侧保留 `StartMenu` 来源，DockAppType.Url 由扩展名派生）。
- `IIconProvider`/`Win32IconProvider`/`DockIconService` 上移为 app-source 的 `IAppIconService`（接口语义一致：GetIconAsync/Invalidate/PrefetchAsync）；`DockIconService` 缓存逻辑一并迁移，dock 只保留对 `IAppIconService` 的 `Inject` 引用。
- 移除后 dock 侧删除：`Services/ShellLinkResolver.cs`、`Services/IIconProvider.cs`、`Services/Win32IconProvider.cs`（原 8 处调用点改为消费 app-source 服务）。

## 1. 目标与边界

**做什么**：统一的应用数据来源服务——扫描（开始菜单/已安装/Store）、图标获取、新装检测快照。为 Dock / AppGrabber / Launchpad / 新装通知等 UI 插件提供**唯一**数据来源。 **不做什么**：不持有固定/运行状态（那是 dock 的 `IDockPinnedService` 职责）；不渲染 UI；不做启动逻辑；不涉及窗口（UI 插件统一继承 `shell-core.Surface.ShellWindow`，与数据层无关）。

## 2. 架构

```
shell-app-source (独立 IPlugin)
├── AppSourcePlugin         # IPlugin 入口，LoadAsync 里 Provide 全部服务
├── Scanner/               # StartMenuScanner / InstalledAppsScanner / StoreScanner(★)
├── Filter/                # 排除名单、系统组件、系统工具、文档目标（现内联在 AppSourceService）
├── Snapshot/              # installed-seen.json 已见快照
├── Resolver/              # ShellLinkResolver + RegistryIconFallback
└── Icons/                 # ★ IAppIconService + Win32ShellIconService（自 shell-dock 上移）
```

依赖：`BetterDesktop.Kernel`（IPlugin/IContext）；**去掉**对 `shell-core` 的引用（数据层不应依赖 UI 层，仅因 TFM 约定引入，需核查移除后是否可编译）。

## 3. 领域模型

```csharp
enum AppSource { StartMenu, Installed, Store, UserAdded }

sealed record AppItem
{
    required AppItemId Id;          // 稳定主键：Win32/Url→TargetPath；Store→AUMID
    required string Name;
    required string ShortcutPath;   // LNK/URL/AppRef
    required string TargetPath;     // EXE/AUMID/URL
    required AppSource Source;
    string? AppUserModelId;         // ★ UWP 支持后填充
}
```

## 4. 内核集成（真实契约 IPlugin）

```csharp
public sealed class AppSourcePlugin : IPlugin
{
    public string Name => "shell.app-source";
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    public Task LoadAsync(IContext context, CancellationToken ct = default)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");
        var service = new AppSourceService(dataDir);
        context.Provide<IAppSourceService>(service);
        context.Provide<IAppIconService>(new Win32ShellIconService(service));   // ★
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

依赖顺序：app-source 先于 dock / app-grabber / launchpad；消费方通过 `Inject` 声明 `typeof(IAppSourceService)`，由内核按依赖拓扑调度（PENDING 直到可用）。

## 5. 公共契约（语义）

```csharp
interface IAppSourceService
{
    IReadOnlyList<AppItem> ScanStartMenu();                    // 纯扫描，不写持久化
    IReadOnlyList<AppItem> ScanInstalledApps();                // 注册表卸载项
    IReadOnlyList<AppItem> GetNewlyInstalledApps();            // 快照差集；首调全量记为已见返回空
    void MarkAppsSeen(IReadOnlyList<AppItem> apps);            // 幂等
    AppItem? ResolveFromPath(string path);                     // LNK/URL/EXE → AppItem
}

interface IAppIconService                                   // ★ 自 shell-dock 上移
{
    Task<ImageSource?> GetIconAsync(AppItem item, CancellationToken ct = default);
    void Invalidate(AppItemId id);
    Task PrefetchAsync(IEnumerable<AppItem> items, CancellationToken ct = default);
}
```

- 契约语义：所有扫描方法**不抛异常**（内部 catch 隔离）；快照读写失败保持空集合不阻塞主流程；`MarkAppsSeen` 幂等（HashSet）。

## 6. 配置

- 数据目录：`%APPDATA%\BetterDesktop\`（`installed-seen.json` 快照）。
- ★ 排除名单可配置化（当前硬编码 `ExcludedNames` / `DocumentTargetExtensions`），后续抽为 `filters.ini` 可覆盖。

## 7. 数据流

```
Dock/AppGrabber/Launchpad/NewAppsNotification
  → Inject: IAppSourceService / IAppIconService
  → Scan* / GetIconAsync
  → Snapshot 读写（仅 GetNewlyInstalledApps/MarkAppsSeen）
```

## 8. 跨插件协作

- `shell-dock.DockAppsService` 改为**通过 Inject 消费** `IAppSourceService`，不再由 DockPlugin 手动 new（消除强耦合）。
- 图标服务从 shell-dock 上移后，`shell-dock` 的 `IIconProvider` / `Win32IconProvider` 删除，统一走 `IAppIconService`。

## 9. 错误处理

- 注册表/目录访问失败：单条跳过，整体不中断（现状已满足）。
- 快照损坏：清空重建（现状已满足）。

## 10. 性能

- 扫描结果缓存（如 5 分钟）+ `GetNewlyInstalledApps` 不重复全量扫（★：当前每次全量扫注册表，可加缓存）。
- 图标异步加载 + 缓存 + 预取（现状 Dock 侧已有，上移后保留）。

## 落实跟踪（2026-08-22 复核）

- ✅ `AppSourcePlugin.cs`（独立 IPlugin，`shell.app-source`）：Provide `IAppSourceService` + `IAppIconService`。
- ✅ `Contracts/IAppIconService.cs` + `Services/Win32ShellIconService.cs` 上移落地。
- ✅ csproj 移除 `shell-core` 引用（仅 kernel + ManagedShell）。
- ✅ 图标栈去重完成：dock 的 `IIconProvider`/`Win32IconProvider`/`ShellLinkResolver` 已删，`DockIconService` 改包装 `IAppIconService`。
- ✅ 构建实证：0 警告 0 错误（连带 shell-dock 与测试）。
- ⏳ 仍待办：UWP/AppX 枚举（Store 分支）、`AppSource.UserAdded` 写入口、排除名单 `filters.ini` 可配置化、扫描结果缓存。

## 11. 验收

- [ ] `shell.app-source` 独立插件可加载，Dock/AppGrabber 经 Inject 拿到服务
- [ ] `IAppIconService` 存在且被 Dock/AppGrabber 使用
- [ ] shell-dock 不再包含 `IIconProvider`/`Win32IconProvider`
- [ ] 移除对 shell-core 的引用后构建通过

## 12. 开放问题

- Store/UWP 枚举方案（`PackageManager` vs `ManagedShell` 已引入但未用）。
- 快照缓存与"新装检测"的轮询/事件化取舍。
