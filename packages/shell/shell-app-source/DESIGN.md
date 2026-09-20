# shell-app-source（应用数据来源插件 · 深度设计稿）

> 状态：**现行实现态（v1.3，2026-09-10 重写同步）**。本文与代码逐一核对过：所有契约/文件/行为均为当前真实状态，无 ★ 待办混杂。
> 文档时间序（早的在前）：本文件第 1 节「版本沿革」按升序记录演进；对照旧版设计（2026-08-22）的 diff 已全部吸收进正文，旧稿可弃。

## 1. 版本沿革（时间升序，早 → 新）

| 时间 | 事件 | 要点 |
|---|---|---|
| 2026-08-22 | 初版设计稿 + 首轮复核 | 独立 `IPlugin`（`shell.app-source`）落地：Provide `IAppSourceService` + `IAppIconService`；图标栈自 shell-dock 上移（删除 dock 侧 `IIconProvider`/`Win32IconProvider`/`ShellLinkResolver` 分叉拷贝）；csproj 移除 shell-core 引用。初版遗留 ★：UWP/Store 枚举、UserAdded 写入口、filters.ini、扫描缓存 |
| 2026-09-04 | 高清图标提取器 | `Native/HighResIconExtractor.cs`：shell32 私有导出 `SHExtractIconsW`（Open-Shell ResourceHelper.cpp L270-300 移植，六条红线见 §7.2），dock 默认 48px 行为不变、新增 256px 高清通道 |
| 2026-09-05 ~ 09-06 | 开始菜单 UWP 接入消费链 | 开始菜单条目挂接点传 `AppItemActions.NativePaths`（ShortcutPath 优先 → TargetPath → UWP null 回退自研）；原生右键/启动走系统语义 |
| 2026-09-08 | 契约升格公共 API 包 | 契约与模型迁入 `packages/api/AppSource/`（`IAppSourceService`/`IAppIconService`/`AppItem`/`AppItemId`/`AppSource`/`ProgramFolder`），随 16 域契约包独立化，消费方仅引用 `BetterDesktop.Api` |
| 2026-09-09 ~ 09-10 | v1.3 收口 + 文档重写 | UWP/Store 枚举落地（`AppsFolderSource` + `ShellItemInterop`）、开始菜单变更监听（`StartMenuWatcher`）、全程序大盘点（`ScanAllPrograms`/`GetProgramTree`）、`AppLauncher`；本 DESIGN.md 按当前代码全量重写 |

## 2. 目标与边界

**做什么**：统一的应用数据来源服务——扫描（开始菜单/已安装注册表/UWP·Store/全程序盘点）、图标获取（含高清通道）、新装检测快照、LNK 解析、启动。为 Dock / 应用提取器 / 开始菜单 / 菜单栏程序菜单 / 搜索 / 新装通知 / 窗口追踪等 UI 插件提供**唯一**数据来源。

**不做什么**：不持有固定/运行状态（dock 的 `IDockPinnedService` 职责）；不渲染 UI；不做权限提升以外的启动编排；不涉及窗口（UI 插件统一走 shell-core 窗口体系，与数据层无关）。

## 3. 架构（当前真实文件清单）

```
packages/api/AppSource/                     # 契约 + 模型（公共 API 包，零实现依赖）
├── IAppSourceService.cs / IAppIconService.cs
└── AppItem.cs / AppItemId.cs / AppSource.cs / ProgramFolder.cs

packages/shell/shell-app-source/            # 实现（引用：Kernel + BetterDesktop.Api + ManagedShell）
├── AppSourcePlugin.cs                      # IPlugin 入口（shell.app-source），LoadAsync Provide 双服务
├── Services/
│   ├── AppSourceService.cs                 # 核心：四来源扫描/快照/缓存/解析（32KB）
│   ├── AppsFolderSource.cs                 # shell:appsfolder 枚举 UWP/Store（AUMID）
│   ├── StartMenuWatcher.cs                 # 开始菜单 FileSystemWatcher（防抖 1s → AppSourceChanged）
│   ├── ShellLinkResolver.cs                # LNK / URL / EXE / AppRef 解析（唯一实现）
│   ├── Win32ShellIconService.cs            # IAppIconService 实现（缓存/预取/UWP 图标）
│   └── AppLauncher.cs                      # 启动应用 / 以管理员运行
└── Native/
    ├── HighResIconExtractor.cs             # SHExtractIconsW 高清提取（六红线）
    └── ShellItemInterop.cs                 # IShellItem API：AppsFolder 枚举 / AUMID 图标
```

依赖：`BetterDesktop.Kernel`（IPlugin/IContext）+ `BetterDesktop.Api`（契约）+ `ManagedShell`（图标提取辅助）。**已无 shell-core 引用**（数据层不依赖 UI 层）。

## 4. 领域模型（packages/api/AppSource/Models）

```csharp
enum AppSource { StartMenu, Installed, Store, UserAdded }

sealed record AppItem
{
    required AppItemId Id;             // 稳定主键：Win32/Url→TargetPath；Store→AUMID
    required string Name;
    string ShortcutPath;               // LNK/URL/AppRef，可空串
    string TargetPath;                 // EXE/AUMID/URL
    required AppSource Source;
    string? AppUserModelId;            // UWP/Store 应用（Win32 为空）
    string? PackageFamilyName;         // UWP 包族名（Store 管理界面/按包卸载用）
    string? IconCacheKey;              // 图标缓存键（空则回退 ShortcutPath/TargetPath）
    string? UninstallCommand;          // 卸载注册表 UninstallString（干净模式右键"卸载"）
}

record ProgramFolder                   // 开始菜单 Programs 层级树（用户+公共合并，同名子文件夹递归合并）
```

`AppSource.UserAdded`：枚举保留，写入口在消费方（dock 固定时构造 `AppItem{Source=UserAdded}`，如 `--menu-cmd dock-pin`），本插件不提供写 API。

## 5. 内核集成（AppSourcePlugin，当前真实代码语义）

- `Name => "shell.app-source"`，`Inject => 空`；
- `LoadAsync`：`new AppSourceService(%APPDATA%\BetterDesktop)` + `new Win32ShellIconService()` → `Provide<IAppSourceService>` / `Provide<IAppIconService>`；
- `UnloadAsync`：`AppSourceService.Dispose()`（停 watcher/清缓存）。
- 消费方通过 `Inject` 声明契约类型，内核按依赖拓扑调度（PENDING 直到可用）。加载顺序：app-source 先于 dock / start-menu / menu-bar / window-tracker / notification。

## 6. 公共契约（语义，与 api 包逐字对齐）

```csharp
interface IAppSourceService
{
    IReadOnlyList<AppItem> ScanStartMenu();        // 开始菜单（用户+公共 Programs），不写持久化
    IReadOnlyList<AppItem> ScanInstalledApps();    // 卸载注册表三视图（HKCU/HKLM/WOW6432Node）+ Apps 文件夹，与开始菜单同名去重
    IReadOnlyList<AppItem> ScanStoreApps();        // 仅 Store/UWP 来源（专用视图）
    IReadOnlyList<AppItem> ScanAllPrograms();      // 全程序大盘点：Program Files×2 / LocalAppData Programs / 各固定盘，排除 Windows 目录
    ProgramFolder GetProgramTree();                // Programs 层级树（经典开始菜单"所有程序"）
    IReadOnlyList<AppItem> GetNewlyInstalledApps();// 快照差集；首调全量记已见返回空
    void MarkAppsSeen(IReadOnlyList<AppItem>);     // 幂等
    AppItem? ResolveFromPath(string path);         // LNK/URL/EXE → AppItem（含 eager 重载）
    void InvalidateCache();                        // 内存扫描缓存失效（卸载/安装后强制重扫）
    event EventHandler? AppSourceChanged;          // StartMenuWatcher 防抖 1s 后触发（后台线程，订阅方自行切 UI 线程）
}

interface IAppIconService
{
    Task<ImageSource?> GetIconAsync(AppItem, CancellationToken);
    void Invalidate(AppItemId);
    Task PrefetchAsync(IEnumerable<AppItem>, CancellationToken);
}
```

错误语义：所有扫描**不抛异常**（内部 catch 隔离，失败返回空集合）；快照读写失败空集合不阻塞主流程；事件在后台线程触发。

## 7. 数据源与过滤

| 来源 | 枚举方式 | 过滤 |
|---|---|---|
| StartMenu | 用户+公共 Programs 递归扫 .lnk/.url | 排除名单（中英文）、系统组件/系统工具、文档目标 |
| Installed | 卸载注册表三视图 + DisplayIcon→InstallLocation 回退 | 同上；产出 `UninstallCommand` |
| Store/UWP | `ShellItemInterop.EnumerateAppsFolder()`（FOLDERID_AppsFolder） | ① 无 AUMID 跳过；② `PKEY_AppUserModel_IsSystemComponent` 跳过；③ 与开始菜单同名 .lnk 跳过（避免重复） |
| AllPrograms | 磁盘盘点（Program Files×2 / LocalAppData Programs / 各固定盘） | 排除 Windows 系统目录，深度受控 |

## 8. 图标管线

### 8.1 Win32ShellIconService（IAppIconService 实现）

- 内存缓存 `ConcurrentDictionary<string, ImageSource>`（键 = `ResolveCacheKey`：IconCacheKey → ShortcutPath → TargetPath）；
- **UWP/Store**（无文件路径）：`ShellItemInterop.GetAppIcon(AUMID, 44)`；
- **Win32**：ManagedShell ExtraLarge(48) 原生帧为主，Jumbo(256) 高清源经 `HighResIconExtractor`；
- `PrefetchAsync` 批量预热（应用提取器等大列表场景）；`Invalidate` 按主键清缓存。

### 8.2 HighResIconExtractor 红线（SHExtractIconsW，勿改）

1. 必须 `GetProcAddress` 动态加载（shell32 无公开导出名，静态 DllImport 必失败）；
2. 私有提取失败必须回退公开 `ExtractIconEx`（降级链是契约一部分）；
3. 返回 0 = 提取失败（不是图标数）；
4. 签名含 `pid` 出参 + `flags`（比 ExtractIconEx 多 pid，别按 ExtractIconEx 写）;
5. 函数指针静态缓存只解析一次；
6. HICON 由调用方释放（走 `IconImageConverter.GetImageFromHIcon` 自动释放路径）。

## 9. 数据流与消费方（Inject 拓扑）

```
AppSourcePlugin.Provide ──┬─ IAppSourceService ──→ shell-dock(DockAppsService) / shell-start-menu / shell-menu-bar(ProgramsMenuService)
                          │                          shell-window-tracker / shell-notification
                          └─ IAppIconService ────→ shell-dock(DockIconService 包装) / shell-menu-bar(搜索图标/状态栏) / shell-quick-note
StartMenuWatcher ── 防抖 1s ── AppSourceChanged ── DockPlugin（切 UI 线程刷新 dock）
ResolveFromPath ←── dock 拖拽落点 / --menu-cmd dock-pin（Source=UserAdded）
```

## 10. 配置

- 数据目录：`%APPDATA%\BetterDesktop\`（`installed-seen.json` 已见快照）。
- 排除名单（`ExcludedNames`/`DocumentTargetExtensions`）仍为代码内常量；`filters.ini` 外置可配置化保留为开放项。

## 11. 性能

- 扫描结果内存缓存 + `InvalidateCache()` 显式失效（比初版"5 分钟 TTL"方案更可控：安装/卸载事件驱动）；
- `StartMenuWatcher` FileSystemWatcher 防抖 1s，避免批量变更期反复重扫；
- 图标：缓存 + 异步提取 + 批量预取；UWP 走 IShellItemImageFactory 单次合成。

## 12. 验收现状（对照初版 §11 全部达成）

- ✅ `shell.app-source` 独立插件加载，Dock/AppGrabber/开始菜单/菜单栏/窗口追踪经 Inject 拿到服务；
- ✅ `IAppIconService` 落地并被消费（dock 的 DockIconService 仅包装转发）；
- ✅ shell-dock 已无 `IIconProvider`/`Win32IconProvider`/重复 ShellLinkResolver；
- ✅ csproj 仅引用 Kernel + Api + ManagedShell，构建 0 警告 0 错误。

## 13. 开放问题（未排期）

1. `AppSource.UserAdded` 本插件侧写入口（当前由消费方构造，够用）；
2. 排除名单 `filters.ini` 外置；
3. 快照"新装检测"从轮询改事件化（订阅 StartMenuWatcher + MSI/注册表安装事件）的取舍；
4. `ScanAllPrograms` 大盘点增量缓存细则（当前全量扫 + 内存缓存）。
