# BetterDesktop.Shell.AppSource



> 应用数据来源服务：统一的应用扫描（开始菜单/已安装注册表/UWP·Store/全程序盘点）、图标获取（含 256px 高清通道）、新应用检测、开始菜单变更监听、LNK 解析、启动。

> 深度设计见 `DESIGN.md`（v1.3 现行态）。



## 职责



- 扫描开始菜单、已安装程序（注册表三视图）、UWP/Store（shell:appsfolder）、全程序盘点

- 获取应用图标（Win32 Shell API + SHExtractIconsW 高清 + AUMID 图标）

- 检测新安装应用（installed-seen.json 快照差集）

- **不涉及**：UI 渲染、固定逻辑、Dock 特有状态



## 服务（契约位于 `packages/api/AppSource/`，BetterDesktop.Api）



| 接口 | 说明 |

|------|------|

| `IAppSourceService` | 四来源扫描/程序树/新应用检测/快照/缓存失效 + `AppSourceChanged` 事件 |

| `IAppIconService` | 应用图标获取（GetIconAsync/Invalidate/PrefetchAsync） |



## 模型（packages/api/AppSource/Models）



| 类型 | 说明 |

|------|------|

| `AppItem` | 通用应用数据模型（AUMID/包族名/图标缓存键/卸载命令） |

| `AppItemId` | 应用 ID（强类型） |

| `AppSource` | 来源类型枚举（StartMenu/Installed/Store/UserAdded） |

| `ProgramFolder` | 开始菜单 Programs 层级树 |



## 索引引擎后端（M2 · 2026-09-13）

`AppSourceService` 的两个**代价最高**的扫描走常驻索引引擎（`engine-index`，经 `shell-index-ipc` 客户端）：

| 方法 | 本地实现 | 引擎后端 |
|---|---|---|
| `ScanStartMenu()` | 全递归枚举两个 Programs 目录（**4254ms**） | `list_apps` 取 `start-menu` 候选 → 逐候选复用 `ResolveFromPath` 升格 |
| `ScanAllPrograms()` | Program Files 深度 3 递归（冷 **53,107ms** / 1619 项） | `list_apps` 取 `program-files` 候选 → 逐候选 `ShellLinkResolver.Resolve` 升格 |
| `ScanInstalledApps()` | 注册表三视图（**65ms**） | **保持本地**（无收益，不下沉） |

**语义不变的关键**：引擎只给「磁盘上有哪些可执行文件」这一**事实**（路径 + 名称候选 + 来源根标记），lnk 解析 / 显示名 / 排除词过滤 / Id 生成全部由本包的既有实现完成（`AppCandidateMapper`），因此**不会出现第二份应用语义**（计划 §5.1 权威源纪律）。注意引擎的 `source` 是**来源根**，而 `AppItem.Source` 是**应用类型**（由文件扩展名判定）—— 两者不可混用。

**降级（必须可见）**：引擎未部署 / 未连接 / 仍在构建（`ListAppsResult.Building`）/ IPC 异常时，一律**自动回退本地实现**并记 `Warn`（`[app-source] … → 本次回退本地扫描`）。强制回退（排障 / 对照）：环境变量 `BETTERDESKTOP_INDEX_BACKEND=local`。

**实测对照（同机真机）**：本地冷 53,107ms → 引擎路径 `list_apps` 57~90ms + 升格 341~391ms，**升格后 1619 项与本地完全一致**。

**部署**：`pwsh -File scripts/deploy-index.ps1`（构建 Rust 引擎并部署到 `%LOCALAPPDATA%\BetterDesktop\`）。

## 稳定 Id：三条路径的唯一产生点（S1 · 2026-09-14）

`CreateStableId` 是**唯一**的 Id 产生点，干净模式 / 全程序模式 / 引擎升格（`AppCandidateMapper`）**共用**它。历史坑：全程序模式曾自造 `"all:" + 路径` 前缀，导致同一程序在两种模式与固定库里 **Id 不同**——用户表现为「右键固定态时有时无、固定绿点不亮、已固定筛选恒空、新装提醒错乱」。**改 Id 必须三处同步改**，否则破坏引擎/本地点数平价。全程序模式产出后按稳定 Id 去重（`DedupByStableId`）：下游 `DockItemData.Id` 是字典键，重复键会让条目/角标互相覆盖。

## 全程序模式：过滤层 + 范围层（S5/S6 · 2026-09-14）

### 过滤层（默认关）

`AppFilterRules`（纯函数规则表）+ `ApplyAllProgramsFilter`：**只作用于 `ScanAllPrograms`，不影响干净模式**；且**引擎与本地两条产出路径都过**（只接一条会让切换后端时条目集不一致）。开关：环境变量 `BETTERDESKTOP_APP_FILTER`（`on`/`true`/`1` 开启，默认**关**——过滤会改变条目集，属行为变更）。

| 维度 | 规则 |
|---|---|
| 目录段（**整段相等**） | `bin`（覆盖 `\usr\bin\`）、`Scripts`、`nodejs`、`Roslyn` |
| 版本化段前缀 | `jre` / `jdk`（`jdk-17` 命中；`Jreport`/`Jdkeeper` **不**命中） |
| 跨段路径形迹 | `\VC\Tools\MSVC\`、`\Common7\IDE\CommonExtensions`、`Windows Kits`、`Tesseract-OCR`、`\Docker\cli-plugins\` |
| 服务 / 遥测 / 运行库 | `*Service.exe`、`crash`、`report`、`telemetry`、`redist`、`vcruntime` |
| 主程序白名单（**优先**） | `Ssms.exe` / `devenv.exe` / `vmware.exe` |

- **粒度纪律**：按「同一套件内区分主程序与工具链」判定，**不按供应商目录整块滤**（整块滤会把 Ssms/devenv 一起滤掉）。
- **匹配纪律**：按**路径 / 文件名**判定，**不用显示名**——lnk 的 `GetDescription()` 常是整句文案，用 `Contains` 打在描述上会误杀。
- 刻意**不含**裸 `runtime`（会误伤用户真用的运行时类应用）。
- 语义依据与量化：`docs/analysis/2026-09-13-app-filter-derivation.md` §7.1（保留项里 536/1487 = 36% 是 CLI / 工具链）。

### 范围层：口袋目录 + 桌面快捷方式

| 来源 | 实现 | 设置键 | 默认 |
|---|---|---|---|
| 口袋目录（便携 / 解压即用工具） | `SetExtraScanRoots` + `ScanExtraRoots`（深度 3） | `app-source.extra-roots`（JSON 字符串数组） | 空（不扫额外目录） |
| 桌面快捷方式（干净模式） | `SetDesktopShortcutsEnabled` + `ScanDesktopShortcuts`（用户桌面 + 公共桌面，深度 1） | `app-source.scan-desktop-shortcuts` | **true** |

- **口袋目录刻意在 C# 侧本地扫、不走引擎**：引擎根集是它自己的 `scan_roots`，不知道用户后加的目录；只走引擎路径会让口袋目录在「后端 = engine」时**静默失效**。本地附加 + 按稳定 Id 合并 → **切换后端不改变条目集**。
- 目录不存在 / 不可读**记 Warn 不静默**；桌面来源按稳定 Id 与开始菜单条目去重（同目标不重复出现）。
- **下载目录不纳入**（噪音大）。
- 键名与映射集中在 `Services/AppSourceSettings.cs`（键名/默认值只有一份，避免设置 UI 与插件各写一份而静默失效）；设置分区 `Sections/AppSourceSection.cs` 由插件自贡献 —— 本包**未**引用 `shell-settings`，因为 `ISettingsService` 等契约定义在 `packages/api` 且 Bootstrap 在插件加载前已 Provide。

## 依赖



- `BetterDesktop.Kernel`、`BetterDesktop.Api`（契约）、`ManagedShell`（图标辅助）

- 消费方：Dock / 开始菜单 / 菜单栏程序菜单 / 搜索 / 窗口追踪 / 新装通知（均经 Inject）



## Known Limitations



- 仅支持 Windows 10 19041+ (net8.0-windows10.0.19041.0)

- `AppSource.UserAdded` 本包不提供写入口（消费方自行构造，如 dock 固定）

