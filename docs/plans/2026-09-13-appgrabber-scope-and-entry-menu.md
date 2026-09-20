# Cairo 开发计划 · 应用提取器范围 + 应用条目右键菜单（三块合一）

> Task: ① 修复右键菜单「功能时有时无」（根因 = 两种模式 Id 语义分裂）；② 做对全程序模式的 exe 提取范围与过滤；③ 补齐并统一「应用条目右键菜单」（应用提取器 + 菜单栏搜索共用一套项集）。
> 证据基于 2026-09-13 工作树验证（**本仓尚无 commit**，行号钉在该工作树，全部经当日逐行复核）。技术力文档：**命中可用** `05-图标/506-app-enumeration`（应用枚举三源 + AppGrabber + 红线 1–6）；**不适用** `72-右键菜单/*`（全部是「系统第三方菜单的注册表模型/注入/夺权」，与本议题的自绘应用条目菜单不同层）；**未命中（新建）**：应用条目菜单构建器（检索 `menu builder`/`菜单项集`）、程序过滤规则（`CLI 过滤`/`suite filter`）、便携目录范围（`portable app`/`口袋目录`）。
> 深度：full（架构 + 功能 + 行为变更混合）。

## 1. Objective

用户在**应用提取器**里固定/管理应用时，右键项**不再随模式漂移**：
- 干净模式固定过的应用，切到**全程序模式**查找同一程序，仍显示「从 Dock 移除」+ 固定绿点，并出现在「已固定」筛选里；
- 两个模式都能用**同一套右键项**（启动 / 固定 / 移出分组 / 打开所在目录 / 复制路径 / 以管理员身份运行 / 属性 / 卸载）；
- 在全程序模式里，**列表默认不再淹没在 CLI 工具与套件工具链里**（`usr\bin`、`jdk\bin`、`Python\Scripts`、`vc_redist`、`*Service.exe` 等），但**套件主程序保留**（`Ssms.exe`、`devenv.exe`）；
- 用户能把自己那些**便携/解压即用**的工具（MAA / OneDragon 一类）**加进来并被索引到**，也能在**菜单栏搜索**里搜到它们并右键固定。

## 2. Current Behaviour

### 2.1 应用提取器双模式与数据源（全部 [verified]）

| 模式 | 数据源 | 条目 | 过滤 |
|---|---|---|---|
| 干净模式 | `DockAppsService.ScanStartMenu()`（`:374`，内部 `AppSourceService.ScanDirectory:551` → `ResolveFromPath`）+ `ScanInstalledApps()`（`:476`） | 开始菜单 .lnk + 注册表已安装 + Store | **有**（`ResolveFromPath:471-549` 五道闸门） |
| 全程序模式 | `DockAppsService.ScanAllPrograms()`（`:498`）→ `AppSourceService.ScanAllPrograms():750` → `EnumerateExecutablesRecursive:777-838` | 磁盘根集枚举，实测 **1789** 项 | **无**——只有 `IsSupportedFile` 扩展名闸门，随后 `Resolve` + **无条件 Add**（`:804-811`） |

根集 = `GetAllProgramRoots():840-880`：`ProgramFiles` + `ProgramFilesX86` + `%LocalAppData%\Programs` + 各固定盘 `X:\Program Files` 与 `X:\Program Files (x86)`（后者 2026-09-13 刚补）；深度 3（`:766`）。

### 2.2 右键菜单现状（[verified]）

- **应用提取器** `AppGrabberWindow.ShowItemMenu:1112-1183`：启动 / 固定·移除 / 打开所在目录 / 移出分组 / 移动到分组 ▸ / **卸载…（仅 `UninstallCommand` 非空时，`:1177`）**；渲染走 `DockMenuPopup.ShowAtCursor`（`:1182`）。
- **菜单栏搜索** `SearchPopupWindow.ShowResultMenu:345-402`：打开（默认）/ 固定·取消固定 / 打开所在位置·复制路径（有路径时）/ 复制链接（设置类）；渲染走 WPF `ContextMenu`。
- 两处**各自实现一份项集**，模型同为 `MenuItemDef` 但构建逻辑无共用。

### 2.3 ★ 核心缺陷：两种模式的 Id 语义分裂（「功能时有时无」的根因）

```
全程序模式  AppSourceService.cs:806   Id = new AppItemId("all:" + file)      → "all:D:\...\a.exe"
干净模式    AppSourceService.cs:517   Id = CreateStableId(...)  :579-588    → "D:\...\a.exe"（TargetPath）
固定库      DockAppsService.cs:143    AddByPath → ResolveFromPath → 同上    → "D:\...\a.exe"
```
`AppItemId.ToString()` 即原始键、比较 `OrdinalIgnoreCase`（`AppItemId.cs:26,34`）。判固定态处一律 `_pinnedIds.Contains(app.Id)`：

| 位置 | 受影响行为（仅全程序模式失效） |
|---|---|
| `AppGrabberWindow.cs:1114` | 右键**永远**只有「固定到 Dock」，**永远没有**「从 Dock 移除」 |
| `:907` | 固定绿点不显示 |
| `:583` | 「已固定 / 未固定」筛选恒空/恒全 |
| `:875,1389` | 批量排序的编号收敛判据失真 |
| `:541`（`MarkInstalledAppsSeen`） | `"all:..."` 当 Id 与 seen 集合比对 → 新装提醒异常 |

### 2.4 次要原因（同样表现为「时有时无」）（[verified]）

| # | 机制 | 证据 |
|---|---|---|
| 3 | 菜单栏搜索的 `IPinningService` 用**可空 `Get`** 取，取不到就整项省略「固定」 | `MenuBarPlugin.cs:80-82`（注释自承依赖装配顺序）+ `SearchPopupWindow.cs:358` |
| 4 | 搜索结果按类别给项：`AppItem` 只有程序类有 | `ProgramSearchProvider.cs:62`；设置/文件类无「固定」 |
| 5 | `UninstallCommand` 只有注册表来源有；干净模式的开始菜单条目靠**反查注册表补齐** | `DockAppsService.cs:378-398` vs 全程序模式无 |
| 6 | 固定列表读取自带失效过滤 + 同名去重 + 注册表自愈 → 集合本身会漂移 | `DockAppsService.cs:70-90`、`IsPinnedItemValid:190-205` |

## 3. Relevant Architecture

- 契约层在 `packages/api/AppSource/`（`IAppSourceService` / `AppItem` / `AppItemId` / `AppSource` / `IAppIconService`）；`MenuItemDef` 为既有菜单项模型（shell-context-menu 契约，供自绘菜单消费）。
- **自研右键菜单的合法范围（2026-09-05 拍板）**：只保留 **dock 图标** 与 **应用提取器** 两处；其余表面（文件/文件夹/桌面）一律系统原生。菜单栏搜索面板属于「我方功能面板内部条目」，其条目菜单与 dock 同性质，**不得**被改造成系统原生文件菜单。
- 跨包依赖纪律：`shell-dock` 不得反向依赖 `shell-menu-bar`，反之亦然；共用件应落在 `packages/api`（无 UI 依赖）或 `shell-core`。
- 应用枚举语义与红线见 `05-图标/506-app-enumeration`（递归跳 `\startup`、UWP 前置判定、lnk 回退、显示名 FileDescription、类目查重、启动失败可见）。

## 4. Technical-Knowledge Findings

| 文档 | 定位 | 关系 |
|---|---|---|
| `05-图标/506-app-enumeration` | 三源程序枚举 + lnk 解析 + 提权状态机 + 6 条红线 | **直接命中**：本计划「范围层/过滤层」的语义基线；红线 1（跳 `\startup`）**与当前 betterdt 现状冲突**（见 §5.1） |
| `72-右键菜单/*`（11 份） | HKCR 注册表菜单模型 / ShellEx / 注入 / 夺权 | **不适用**：那是「系统菜单挂载层」，本计划是「我方条目菜单项集」 |
| `36-设计系统/3603-菜单栏插入按钮弹窗模式` | 菜单栏扩展插入模式（L1） | 参考：C 面的弹层纪律（`MenuBarPopupWindow`） |
| 未命中 | 应用条目菜单构建器 / 程序过滤规则 / 便携目录范围 | **新建**；无现成实现可复用 |

**相关测试工程（已定位，真实存在）**：`packages/shell/shell-dock-tests/`、`shell-menu-bar-tests/`、`shell-app-source-tests/`、`shell-search-tests/`。

## 5. Constraint Findings

### 5.1 506 红线与现状的冲突（必须先裁决）
- 506 红线 1「递归枚举开始菜单必须跳过 `\startup`」——**当前 betterdt 未落实**（`ScanDirectory:557` 用 `SearchOption.AllDirectories`，无 startup 过滤；索引引擎为结果集平价亦未跳）。
- 影响：若补，两侧（C# + 引擎）必须**同批**改，否则引擎与本地扫描结果集不一致（会破坏 M2 已建立的平价）。
- 决策见 §12 Q5。

### 5.2 行为变更必须与「一致性修复」分离验收
- **P0（Id 统一）** 属缺陷修复：修好后全程序模式的**条目集合不变**（仍 1789），只是固定态/筛选/角标恢复正确 → 可单独验收。
- **P2/P3（过滤 / 扩范围）** 会**改变条目集合**（1789 → 更少 / 更多）→ 必须独立验收，不能和 P0 混批。

### 5.3 扩展名闸门与过滤链是两件事
`ScanAllPrograms` 无过滤是**既有设计**（其 doc 自述"遍历常见程序根目录…覆盖非标准位置"）。补过滤 = 引入新行为，需在设置中可回退（见 §12 Q1）。

## 6. Proposed Changes

### P0 一致性修复（缺陷，可独立交付）

**P0-1 Id 语义统一（方案 A）**
- 文件：`packages/shell/shell-app-source/Services/AppSourceService.cs`
- 符号：`EnumerateExecutablesRecursive`
- 职责：把 `Id = new AppItemId("all:" + file)` 改为与 `ResolveFromPath` 同源的 `CreateStableId(source, file, targetPath)`（`Resolve` 已返回 `targetPath`）。
- 期望行为：全程序模式与干净模式的 Id 对同一程序一致 → 固定态/筛选/角标/批量排序/新装提醒全部恢复。
- 约束：`ScanAllPrograms` 目前**不做** `GroupBy(Id)` 去重（`ScanStartMenu:140-144` 做了）→ **必须补去重**，否则同一 target 的多个文件会让 `AppGrabberWindow._containers[app.Id]`（`:82`）字典键冲突。
- 备注：`"all:"` 前缀若被别处消费（先全仓 grep 确认）需同批改；确认无消费者后再改。

**P0-2 菜单栏搜索的固定项改为显式依赖**
- 文件：`packages/shell/shell-menu-bar/MenuBarPlugin.cs`
- 现状：`context.Get<IPinningService>()`（可空 `Get`，`:82`）→ 装配顺序敏感。
- 职责：把 `IPinningService` 写进 `Inject` 声明（内核按依赖调度重载），`SearchPopupWindow` 的 `_pinning` 降为「依赖未满足时的兜底」，而非正常路径。
- 备注：**这是「不要用可空 `Get` 表达跨插件依赖」的既有纪律**（历史坑：dock 先于 context-menu 激活导致 `Get` 恒 null）。

**P0-3 结果类别 → 菜单项集明确化**
- 文件：`packages/shell/shell-menu-bar/Windows/SearchPopupWindow.cs`
- 职责：把「哪些类别该有哪些项」写成显式表（程序 / 设置 / 文件），不再靠 `if` 链隐式表达；无该项时**不显示空菜单**（现状 `hasItem` 已有此保护，保留）。

### P1 共用菜单构建器 + 项集补齐

**P1-1 抽出 `AppEntryMenuBuilder`（纯函数，无 UI 依赖）**
- 新文件：`packages/api/AppSource/AppEntryMenuBuilder.cs`
- 输入：`AppEntryMenuContext { string? Path; AppItem? AppItem; bool IsPinned; string? UninstallCommand; IReadOnlyList<string> Groups; string? CurrentGroup; bool AllowDestructive }`
- 输出：`IReadOnlyList<MenuItemDef>`（沿用既有模型，不新造）
- 职责：集中「条目能有哪些项」的判断；**渲染仍各面自理**（应用提取器 = `DockMenuPopup`；搜索面板 = 其现有 WPF `ContextMenu`）→ 避免 `shell-dock` ↔ `shell-menu-bar` 互相依赖。
- 约束：构建器只做「项集与可用性判定」，不执行动作（动作由调用方注入回调），保证可单测。

**P1-2 补齐项集**（两处共用同一结果）
| 项 | 条件 |
|---|---|
| 启动 / 以管理员身份运行 | 有可执行路径 |
| 固定到 Dock / 从 Dock 移除 | `IPinningService` 可用 |
| 移动到分组 ▸ / 从「X」移出 | 分组数据可用（仅应用提取器） |
| 打开所在目录 / 复制路径 | 有真实路径 |
| 属性 | 有真实路径 |
| 在终端中打开 | 可执行扩展（CLI 类有用） |
| 卸载… | `UninstallCommand` 非空 |

### P2 全程序模式过滤层（行为变更，需 §12 Q1 裁决后启用）

- 文件：`packages/shell/shell-app-source/Services/AppSourceService.cs`（或抽 `Services/AppFilterRules.cs` 便于单测）
- 规则（**按粒度，不按供应商目录整块**）：
  1. **CLI / 工具形迹**（实测 536/1487，见 `docs/analysis/2026-09-13-app-filter-derivation.md` §7.1）：路径含 `\bin\`、`\Scripts\`、`jre|jdk*\bin\`、`\usr\bin\`、`\nodejs\`、`Windows Kits`、`Tesseract-OCR`、`Docker\cli-plugins`。
  2. **套件工具链**：`VC\Tools\MSVC\*\bin\`、`Roslyn`、`MSBuild`、`Common7\IDE\CommonExtensions` 等——**但保留套件主程序**（`Ssms.exe` / `devenv.exe` 一类：位于 `<Suite>\Common7\IDE\` 顶层的同名主 exe）。
  3. **服务 / 崩溃遥测 / 运行库**：`*Service.exe`、`crash*`、`*report*`、`vc_redist*`、`*runtime*`（注意 `runtime` 需白名单排除 Node.js 这类用户可能真用的）。
- 约束：规则表必须**可配置 + 可回退**（对齐 `shell-app-source/DESIGN.md` 已登记的 `filters.ini` 开放项）；过滤只作用于**全程序模式**，**不得**影响干净模式。

### P3 范围层（行为变更，需 §12 Q2/Q3 裁决）

- **P3-1 口袋目录**：设置中心新增「应用扫描目录」（多目录，逗号/列表），并入全程序模式根集（深度 3）；引擎侧同步（`engine-index` 的 `scan_roots` 已支持）。
- **P3-2 桌面/下载快捷方式**：把「用户桌面 + 公共桌面 + 下载目录（深度 1）」的 `.lnk` 纳入**干净模式**输入（桌面 lnk 是「用户在意」的强信号）；解析目标后与既有条目按 Id 合并去重。
- **P3-3 复用 `UserAdded` 通道**：应用提取器增加「添加应用…」入口（走 `_pinningService.Pin("dock", AppItem{Source=UserAdded})`），让便携应用可被手动纳入（不依赖扫描）。
- 约束：P3 全部为**新增**条目来源，必须与 P0 的 Id 统一**同批或之后**落地（否则新来源又引入第三套 Id 语义）。

### P4（可选，§12 Q4）全程序模式的破坏性处置
- 现状：全程序模式无「卸载」（注释 `:1176` 自述"直接删 exe 不清理残留，不暴露"）。
- 可选：提供「删除文件（进回收站）」+ 二次确认；默认**不提供**（保守）。

### P5 固定项生命周期同步（「同删同更」契约）

> 来源：2026-09-13 用户提问「如何保证固定到 dock 的应用始终存在可用，与自己在电脑里面的状态同步，同删同更」。这是 Q3（桌面 lnk 是否纳入）的真正前置——锚点选错，「纳入桌面」就会引入新的同步脆弱性。

**现状（[verified] `DockAppsService.cs`）**——已做对的与缺口：

| 能力 | 现状 | 锚点 |
|---|---|---|
| 真卸载 → 让位（不显示） | ✅ 有 | `Pinned:70-90` + `IsPinnedItemValid:190-205` |
| 快照保留（重装同路径自动回来） | ✅ 有（注释自述「只剔除显示、不删持久化数据」） | `:187` |
| 同名多版本去重（名称 + 安装根目录） | ✅ 有 | `DedupKey:212-222` |
| 路径失效 → 重绑 | ⚠️ 只能按**名称精确**匹配 | `TryRefreshStalePath:271-314`、`IsStillInstalled:233-255` |
| UWP/Store 存在性校验 | ❌ **一律 `return true`**（卸载后永久占位） | `IsPinnedItemValid:193-196` |
| 触发时机 | ❌ **纯惰性**：只在 `Pinned` 被读取时校验，无事件驱动、无巡检 | `:70-90` |
| 失效状态可见 | ❌ 只在日志/静默让位，用户只感知「图标没了」 | — |

**目标契约**：固定项 = **不可变快照**（`PinnedItem.AppItem`）+ **可变健康态**（由磁盘事实判定，不由 UI 判）：

| 态 | 判定 | 显示 | 持久化 |
|---|---|---|---|
| `Healthy` | `TargetPath`/`ShortcutPath` 存在；或 Store 的 AUMID 在 Store 列表命中 | 正常 | 保留 |
| `Healable` | 路径失效，但多级重绑命中同一应用的新路径 | 用**新路径**显示 + 静默重绑（记日志） | **回写新路径** |
| `Orphaned` | 各级匹配全失配 | 让位（不显示） | **保留快照**（现状已对） |

**多级重绑策略（从强到弱逐级下探）**：
1. Store：AUMID 精确（**补**——现状跳过此级）
2. Win32：`App Paths`（`HKLM|HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\<exe 名>`）→ 新路径
3. Uninstall 注册表：`DisplayIcon` / `InstallLocation` + 同名（现状只判「还在不在」，**不产出新路径**）
4. 安装根目录（沿用 `DedupKey` 的 root 思路）+ 同名
5. 全失配 → `Orphaned`

**触发面（从「惰性」到「事件驱动 + 巡检兜底」）**：
1. **事件（主）**：注册表 Uninstall 键变更（`RegNotifyChangeKeyValue`）、开始菜单 watcher（已有）、**索引引擎 `AppSourceChanged`**（`2026-09-13-native-index-service-rust.md` §6.3 已规划）——事件带变化标识 → **只定向校验相关固定项**，不全区反查。
2. **低频巡检（兜底）**：15–30 分钟一次，或「dock 显示时一次」（把现行隐性行为显式化）。
3. **固定集合自身变更**：`PinnedChanged`（已有）。

**可观测**：设置中心「Dock 固定项」列出全部固定项 + 健康态；`Orphaned` 提供「重新绑定…（手动指认路径）」与「清理」。现状**完全不可见**。

**保证不了的三类（诚实边界，不承诺）**：
1. **便携应用路径变更**（无注册表、无 AUMID）→ 物理上无法自动关联，只能手动重绑（与 `docs/analysis/2026-09-13-app-filter-derivation.md` §7.2 便携缺口同源）。
2. **应用改名 + 换目录 + 换品牌**（第 2–4 级全失配）→ 按 `Orphaned` 处理，快照保留。
3. **Store 应用 AUMID 变更**（重打包）→ 同 2。

**与 Q3 的耦合（硬约束）**：固定项锚点必须是**解析后的 `TargetPath` / AUMID**（现状 `CreateStableId:579-588` 正是如此），**不得锚桌面 lnk 路径**。因此「桌面 lnk 纳入干净模式」只增加**发现来源**，不引入新的同步脆弱性——用户在桌面移动/改名/删除 lnk 都不影响已固定项。

**与 4.2 索引的依赖**：事件驱动路径依赖引擎的 `AppSourceChanged`；引擎未接入前，本项可先落「多级重绑 + 低频巡检 + 可观测」三件（不阻塞），事件驱动部分作为引擎 M2.5 的消费者。

## 7. Implementation Sequence

| 步 | 内容 | 独立可验证 |
|---|---|---|
| **S1（P0）** | Id 语义统一 + 去重 + 全仓 grep `"all:"` 消费者 | 全程序模式固定态恢复；条目数仍 1789 |
| **S2（P0）** | `IPinningService` 改 `Inject`；结果类别→项集表 | 菜单栏搜索固定项稳定出现 |
| **S3（P1）** | 抽 `AppEntryMenuBuilder` + 两处接入（**项集不变**，先纯重构） | 两处菜单项与改造前逐项一致 |
| **S4（P1）** | 补齐项集（管理员运行/复制路径/属性/终端） | 新项按条件出现 |
| **S5（P2）** | 过滤规则 + 配置开关（默认**关**，先只上规则与测试） | 开关打开后条目数下降且套件主程序保留 |
| **S6（P3）** | 口袋目录 / 桌面·下载 lnk / `UserAdded` 入口 | 便携应用可被搜到并固定 |
| **S7** | 文档回写（analysis 报告、shell-dock/shell-app-source/shell-menu-bar README、TECH-KNOWLEDGE 新增「程序过滤规则」） | — |
| **S8（P5）** | 固定项健康态 + 多级重绑 + 可观测（先落「多级重绑 + 巡检 + 设置中心可见」，事件驱动等引擎 `AppSourceChanged`） | 手动移动/删除 exe 后：更新路径的项自动重绑；真卸载的项让位但快照保留；设置中心能看到 `Orphaned` 项 |

## 8. Test Strategy

- **新增单测**（`packages/shell/shell-app-source-tests/`）：
  - Id 一致性：同一 `.exe` 经 `ScanAllPrograms` 与 `ResolveFromPath` 得到**相同 Id**；`ScanAllPrograms` 结果**Id 唯一**（去重生效）。
  - 过滤规则：CLI 形迹被滤、套件主程序保留、干净模式不受影响（输入 → 过滤 → 期望集合）。
  - 便携范围：临时目录内造 exe/lnk，验证口袋目录与桌面 lnk 被纳入。
- **新增单测**（`shell-dock-tests` / `shell-menu-bar-tests`）：`AppEntryMenuBuilder` 的项集矩阵（正常/边界/异常：无路径、无固定服务、有卸载命令、CLI 类）——纯函数，无需 UI。
- **回归**：`dotnet build BetterDesktop.slnx -c Debug`（0 警告）+ 全量 `dotnet test`（现有 15 个 shell 测试工程 + kernel 4 个）。
- **验证命令**（已确认存在）：`dotnet build BetterDesktop.slnx -c Debug`、`dotnet test packages/shell/shell-dock-tests/BetterDesktop.Shell.Dock.Tests.csproj -p:Platform=x64`。

## 9. Risk and Impact Analysis

| 风险 | 级别 | 缓解 |
|---|---|---|
| Id 改动影响 dock 固定区渲染（`_containers` 字典键、`PinnedChanged` 增量更新） | **高** | S1 单独一批；回归 dock 固定区增删/排序/角标 + 批量排序全流程 |
| `"all:"` 前缀有隐藏消费者 | 中 | S1 先全仓 grep；有消费者则同批改 |
| 全程序模式去重后条目数变化（同 target 多文件合并） | 中 | 记录改前/改后条目数；差异逐条解释 |
| 过滤规则误杀（把用户真用的 CLI/工具滤掉） | **高** | 默认开关=关；规则表外置可配置；过滤只作用于全程序模式；提供"显示被过滤项"诊断视图（§12 Q1） |
| 桌面/下载 lnk 纳入后与开始菜单项重复 | 中 | 按 Id 合并去重（依赖 S1 的 Id 统一） |
| 两处菜单渲染器不同导致「项集一致但观感不一致」 | 低 | P1 只统一项集；观感统一列为 deferred（§12 Q4） |
| 端口：改动 `IAppSourceService` 实现类的公开行为 | 中 | 契约**成员不变**，只改实现内部与新增重载；`ScanAllPrograms` 的**签名不变** |

**可观测性**：过滤生效时在 `DiagnosticLog` 记「全程序模式过滤 N 项 / 保留 M 项」；口袋目录失效时记 Warn（禁止静默）。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-app-source/Services/AppSourceService.cs` | `EnumerateExecutablesRecursive` / `GetAllProgramRoots` | Id 统一 + 去重 + 范围层 |
| `packages/shell/shell-app-source/Services/AppFilterRules.cs`（新） | 过滤规则表 | P2 |
| `packages/api/AppSource/AppEntryMenuBuilder.cs`（新） | `AppEntryMenuContext` / `Build` | P1 共用项集 |
| `packages/shell/shell-dock/Windows/AppGrabberWindow.cs` | `ShowItemMenu` | 接入 builder |
| `packages/shell/shell-menu-bar/Windows/SearchPopupWindow.cs` | `ShowResultMenu` | 接入 builder |
| `packages/shell/shell-menu-bar/MenuBarPlugin.cs` | `Inject` | P0-2 |
| 设置分区（`shell-app-source` 或 `shell-settings`） | 新增「应用扫描目录」「全程序过滤」开关 | P2/P3 |
| `packages/shell/shell-app-source-tests/`、`shell-dock-tests/`、`shell-menu-bar-tests/` | 新用例 | §8 |
| 文档 4 处 | README / DESIGN / analysis 报告 / TECH-KNOWLEDGE | S7 |

## 11. Reusable Implementation Context

**必须直接读取（实现期不再调研）**：
- 枚举与过滤：`AppSourceService.cs:471-549`（`ResolveFromPath` 五道闸门）、`:551-577`（`ScanDirectory`）、`:579-588`（`CreateStableId`）、`:777-838`（`EnumerateExecutablesRecursive`）、`:840-880`（`GetAllProgramRoots`）
- 固定态：`DockAppsService.cs:58-98`（`Pinned`）、`:131-151`（`AddByPath`）、`:337-372`（`ToDockItemData` ×2）、`:374-410`（`ScanStartMenu` 反查卸载命令）
- 菜单：`AppGrabberWindow.cs:1112-1183`、`SearchPopupWindow.cs:345-414`、`MenuItemDef`（shell-context-menu 契约）、`DockMenuPopup`
- 依赖纪律：`MenuBarPlugin.cs:70-86`、`SearchPopupWindow.cs:59-74`
- 语义基线：`TECH-KNOWLEDGE/05-图标/506-app-enumeration.md`（**红线 1–6 全文**）
- 数据分析：`docs/analysis/2026-09-13-app-filter-derivation.md`（§7.1 CLI 形迹量化、§7.2 便携缺口、§7.4 三层建议）
- 引擎侧同步点：`engine-index/src/appindex.rs`（`EXECUTABLE_EXTS` / `program_files_roots` / `scan_roots`）、`engine-index/src/settings.rs`

## 12. Assumptions and Open Questions

**开放问题（2026-09-13 已裁决）**：

| 问题 | 裁决 | 备注 |
|---|---|---|
| Q1 全程序模式过滤默认开关 | **默认关**（保留 1789 现状），设置内可开 | 真机对比两档后再定默认 |
| Q2 口袋目录默认值 | **默认空** + 首次进入应用提取器时提示 | — |
| Q3 桌面/下载 lnk 纳入干净模式 | **桌面纳入、下载不纳入** | 附硬约束：锚点必须是 `TargetPath`/AUMID，**不得锚 lnk 路径**（见 §6 P5） |
| Q4 渲染器是否统一 | **deferred**（本计划只统一项集） | 观感统一需把弹层渲染器提到 `shell-core` |
| Q5 506 红线 1（跳 `\startup`） | **独立小批补**（C# + 索引引擎同批） | 否则破坏 M2 平价 |
| **Q6（新增）固定项同删同更** | **纳入本计划 = §6 P5**（用户 2026-09-13 提出） | 事件驱动部分依赖引擎 `AppSourceChanged`，不阻塞其余 |

**假设**：
1. ~~`[assumed]` `"all:"` 前缀无外部消费者~~ → **已证伪（2026-09-14）**：索引引擎 M2 落地后，引擎升格路径 `AppCandidateMapper.FromProgramFilesCandidate`（`:62`）与 `AppCandidateMapperTests`（`:34`/`:20`）都在消费该前缀；故 S1 改为让两条产出路径**共用同一个 Id 函数**（既修固定态分裂，又保住引擎/本地平价）。
2. `[assumed]` 过滤规则不会误杀用户真用的工具（故默认关 + 提供诊断视图）。

**Deferred（本计划不做）**：菜单渲染器统一（Q4）；全程序模式破坏性删除（P4）；`GetRunningApps` 未缓存（在索引计划 §12）；索引引擎接管 `ScanAllPrograms` 枚举（属 `2026-09-13-native-index-service-rust.md` M2c，与本计划 P0 的 Id 统一**有依赖**：引擎输出的候选经 `ResolveFromPath` 映射时 Id 已统一，故 P0 应先做）。

## 13. Definition of Done

- **D1 构建/测试**：`dotnet build BetterDesktop.slnx -c Debug` **0 警告 0 错误**；`dotnet test` 全部工程绿（含新增用例）。
- **D2 端到端场景（一致性修复）**：真机→应用提取器→干净模式固定「记事本」→切**全程序模式**→找到同一程序→**右键出现「从 Dock 移除」+ 固定绿点**→「已固定」筛选能筛出它。
- **D3 端到端场景（菜单栏搜索）**：重启宿主，菜单栏搜索打开→搜一个应用→右键有「固定到 Dock」→固定成功→再次搜索该项显示「从 Dock 取消固定」。
- **D4 端到端场景（过滤，开关开）**：全程序模式下 `7z.exe`/`javac.exe`/`vc_redist.x64.exe` 不再出现；`Ssms.exe`、`devenv.exe` **仍出现**；干净模式条目数**不变**。
- **D5 端到端场景（便携应用）**：把一个便携 exe 放入用户新增的口袋目录 → 应用提取器里能搜到 → 固定到 dock → dock 上可启动。
- **D6 边界/异常**：无固定服务时不显示固定项且不抛；`UninstallCommand` 为空时不显示卸载项；口袋目录不存在/无权限时记 Warn 不崩。
- **D7 文档回写**：`shell-dock`/`shell-app-source`/`shell-menu-bar` README、`docs/analysis/2026-09-13-app-filter-derivation.md` 状态更新、TECH-KNOWLEDGE 新增「程序过滤规则」+ 索引同步。
- **D8 端到端场景（同删同更）**：① 固定一个应用 → 把它**改名/移到同目录另一版本目录** → dock 与提取器显示**自动重绑后的新路径**（不出现空位）；② 真卸载该应用 → **让位不显示**（不留空位）→ 用原路径重装 → 固定项**自动回来**；③ 设置中心「Dock 固定项」能看到 `Orphaned` 项并可手动重绑/清理；④ UWP 应用卸载后**不再永久占位**。

## 14. Handoff to 技术力应用

| 项 | 内容 |
|---|---|
| **模式判定** | ① Id 统一 / 过滤规则 / 范围层：**无匹配→工程代码权威**（检索 `menu builder`/`CLI 过滤`/`portable app` 未命中；语义依据 = `05-图标/506-app-enumeration` 红线 + 本计划 §2 的源码锚点）；② 应用枚举语义：**标准文档注入**（`05-图标/506`）；③ 菜单栏弹层纪律：**标准文档注入**（`36-设计系统/3603`）；④ 系统注册表菜单层：**不注入**（`72-右键菜单/*` 判定不适用，禁止照其模型改自绘菜单） |
| **注入清单** | `TECH-KNOWLEDGE/05-图标/506-app-enumeration.md`（**含红线 1–6 全文**）；`TECH-KNOWLEDGE/36-设计系统/3603-菜单栏插入按钮弹窗模式.md`；代码侧锚点见 §11 |
| **适配参数** | 新文件 `packages/api/AppSource/AppEntryMenuBuilder.cs`（命名空间 `BetterDesktop.Shell.AppSource.Models`，纯函数无 UI 依赖）；新文件 `packages/shell/shell-app-source/Services/AppFilterRules.cs`；设置键建议 `app-source.extra-roots`（string[]）、`app-source.filter-all-programs`（bool，默认 false）、`app-source.scan-desktop-shortcuts`（bool，默认 true）；新增项 Id 前缀沿用既有 `grab.*` 约定（`grab.admin` / `grab.copy-path` / `grab.properties` / `grab.terminal`）；测试工程路径见 §8；构建命令 `dotnet build BetterDesktop.slnx -c Debug`、`dotnet test <proj> -p:Platform=x64` |
| **禁区** | ①**不改**`IAppSourceService` 既有成员签名（`ScanAllPrograms` 签名不变）；②**不得**把菜单栏搜索的条目菜单改造为系统原生菜单（违反 2026-09-05 自研范围拍板）；③**不得**让 `shell-dock` 依赖 `shell-menu-bar`（或反向）——共用件落 `packages/api`；④**不得**把过滤规则做成「按供应商目录整块滤」（会连 `Ssms.exe`/`devenv.exe` 一起滤掉）；⑤过滤**不得**作用于干净模式；⑥P2/P3 的开关**默认关闭/保守**，禁止静默改变现状；⑦构建前先 `Stop-Process BetterDesktop.Host`（DLL 锁定） |
| **DoD 核销表** | D1 `dotnet build` 0/0 + `dotnet test` 全绿；D2 双模式固定态一致性走查（真机）；D3 菜单栏搜索固定走查（真机）；D4 过滤生效但套件主程序保留 + 干净模式不变；D5 口袋目录便携应用可搜可固定可启动；D6 三类边界不崩且日志可查；D7 文档回写 4 处 + TECH-KNOWLEDGE 新建 |

## 15. 执行记录

| 步 | 状态 | 证据 |
|---|---|---|
| **S1（P0-1）** | ✅ 2026-09-14（代码 + 测试）；D2 待真机走查 | ① `CreateStableId` 提升为 `internal static`，成为**三条路径的唯一 Id 产生点**（干净模式 / 全程序模式 / 引擎升格）；② `EnumerateExecutablesRecursive` 的 `new AppItemId("all:" + file)` → `CreateStableId(source, file, targetPath)`；③ `AppCandidateMapper.FromProgramFilesCandidate` 同步改用 `CreateStableId`（**否则会破坏 M2 刚建立的引擎/本地平价**——§12 假设 1「无外部消费者」已证伪）；④ 新增 `DedupByStableId` + `DedupAndLog`（去重差异记 Info，不静默）；⑤ 附带修复 `CreateStableId` Store 分支会产出**空 Id**。测试：`shell-app-source-tests` 新增 11 例（Id 契约 6 / 引擎与本地同 Id 1 / 固定集合大小写命中 1 / 去重 3）→ **19 绿**；相邻工程 `shell-dock-tests` 11 绿、`shell-menu-bar-tests` 34 绿、`shell-index-ipc-tests` 19 绿；`dotnet build BetterDesktop.slnx -c Debug` **0 警告 0 错误**。条目数影响：去重只合并**同一稳定 Id** 的项（`.exe` 的 Id = 自身路径，已被 `seenPaths` 去重），实测根集内深度 ≤3 的快捷方式类文件仅 **26 个**（C:\PF 4 / C:\PF(x86) 10 / LocalAppData\Programs 1 / D:\PF 3 / D:\PF(x86) 8）→ 变化上限 ≤26 且需两两同目标；实际以真机日志「按稳定 Id 去重」行为准 |
| **S2（P0-2）** | ✅ 2026-09-14（代码 + 测试）；D3 待真机走查 | `MenuBarPlugin.Inject` 由 `Array.Empty<Type>()` 改为 `new[] { typeof(IPinningService) }`。取证：内核 `Inject` 为**硬依赖**（`DependencyReloadTests`：未满足 → `PluginState.Pending`，`LoadAsync` 不执行），故先确认该服务实际必在——`StartMenuPlugin.Inject` 早已声明 `IPinningService`（开始菜单硬依赖它），声明不引入新约束。`SearchPopupWindow._pinning` 降为兜底（注释写明正常路径恒非 null）。测试：`shell-menu-bar-tests` 新增 2 例（Inject 含 `IPinningService`；Inject 只含接口）→ **34 绿** |
| **S2-3 → 并入 S3** | ⏭ 延期（记录偏离） | 计划原列「结果类别→项集表」。经取证：**该项不是缺陷**——「设置/文件类无『固定』」本来就是正确行为（`SearchResult.AppItem` 仅程序类有）。且其内容（类别 → 可用项集）正是 S3 的 `AppEntryMenuBuilder` 要集中表达的东西，先写一张表再被 S3 取代属重复劳动。故并入 S3 一次做对，S3 的验收追加「类别 → 项集映射有显式表达」 |
| **S3（P1-1）** | ✅ 2026-09-14（代码 + 测试）；D2/D3 待真机走查 | 新增 `packages/api/AppSource/AppEntryMenuBuilder.cs`：`AppEntryMenuContext`（事实：`Path` / `IsPinned` / `UninstallCommand` / `Groups` / `CurrentGroup`）+ `AppEntryMenuActions`（动作回调；构建器**只判定、不执行**）+ `Build()` 纯函数。**落 `packages/api` 而非新包**：取证发现 `MenuItemDef` 本就在 `packages/api/ContextMenu/MenuItemDef.cs`（计划 §3 记作「shell-context-menu 契约」不准确），故**无新增跨包依赖**。两处接入：`AppGrabberWindow.ShowItemMenu`（提供路径/固定态/分组/卸载 + 9 个回调）、`SearchPopupWindow.ShowResultMenu`（应用类走构建器；**设置/文件类保持原项集不变** —— 它们不是「应用条目」）。<br>**项集变化（有意，均属 §6 P1-2 目标集）**：① dock 应用条目新增「复制路径」；② 两处「打开所在目录 / 打开所在位置」文案统一为「打开所在目录」。<br>**行为收紧**：「打开所在目录 / 复制路径」现仅在**存在真实路径**时出现（此前 dock 无条件显示，对无路径项点了无动作）。<br>**未接入项**：搜索面板暂不暴露「卸载…」（需动作实现与二次确认，归 S4）——构建器要求「命令 + 动作」同时具备，故不会出现空动作项。测试：`shell-dock-tests` 新增 14 例项集矩阵（基本项集 2 / 缺路径 1 / 缺固定服务 2 / 卸载命令 ± 动作 2 / 分组三态 3 / 文案透传 1 / 空动作 1 / null 上下文 1 / 不分组 1）→ **25 绿**；`packages/api`、`shell-dock`、`shell-menu-bar` 三个改动工程单独构建 **0 警告 0 错误** |

> **环境问题（待用户处理）**：一次被取消的 `dotnet test` 留下了 `testhost` 进程（PID 17220），它锁住了 `packages/shell/shell-status-tests/bin/**/BetterDesktop.{Api,Shell.Core,Shell.Status}.dll`，导致全解决方案 `dotnet build` 报 6 个 MSB3021/MSB3027 拷贝错误。`Stop-Process` / `taskkill /F` 均报「找不到该进程」（疑似跨会话/提权进程），**非代码问题**：绕开该测试工程单独构建三个改动工程均为 0 警告 0 错误。请在任务管理器结束该 `testhost` 进程后再跑全量 `dotnet build`/`dotnet test` 复核 D1。

| 步 | 状态 | 证据 |
|---|---|---|
| **S4（P1-2）** | ✅ 2026-09-14（代码 + 测试）；D2/D3/D4 待真机走查 | 新增 `packages/shell/shell-core/Services/AppEntryActions.cs`：`RevealInExplorer`（目录→打开 / 文件→`/select` 选中）、`RunAsAdmin`（verb `runas`）、`ShowProperties`（verb `properties`）、`OpenInTerminal`（`wt.exe -d <dir>`，缺失回退 `cmd /k cd /d`）、`RunUninstaller`（`cmd /c`，因 `UninstallString` 普遍带参数）、`CopyToClipboard` —— 全部 try-catch 静默（M10）。**落 shell-core 而非 packages/api**：`packages/api` 是契约/模型层，不该出现 `Process.Start`；`shell-core` 已引用 api 且启用 WPF，是 §3 认可的共用层（层向仍为 shell-* → api）。<br>构建器新增 3 项：`grab.admin`（以管理员身份运行）、`grab.terminal`（在终端中打开）—— **按扩展名判定**（`.exe/.bat/.cmd/.com/.msc`；对 `.lnk` / `.url` / UWP AUMID 提权与开终端无意义）、`grab.properties`（属性，按真实路径判定）。最终顺序：启动 → 以管理员身份运行 → 固定/移除 → 打开所在目录 → 复制路径 → 在终端中打开 → ［分组］ → 属性 → 卸载。<br>**消除重复实现**：`AppGrabberWindow` 删除私有 `OpenContainingDirectory` / `RunUninstaller` / `CopyPathToClipboard`；`SearchPopupWindow` 删除私有 `RevealInExplorer` / `CopyToClipboard`；两处改为注入 shell-core 公共动作；**搜索面板首次获得「卸载…」**（S3 的遗留项，`UninstallString` 与动作同时具备才出现）。<br>**行为变更（记录）**：dock 的「打开所在目录」由「打开该目录」改为「在资源管理器中**选中该文件**」（与搜索面板及 Windows「打开文件所在的位置」一致）。<br>测试：`shell-dock-tests` 项集矩阵扩到 18 例（新增：非可执行路径不给提权/终端、5 种可执行扩展参数化、缺系统动作时整项省略、属性位于卸载之前）→ **32 绿**；`shell-menu-bar-tests` 34 绿、`shell-app-source-tests` 19 绿；`packages/api` / `shell-core` / `shell-dock` / `shell-menu-bar` 四个改动工程单独构建 **0 警告 0 错误** |

> **S4 后遗留（归 S7 文档回写）**：`packages/shell/shell-core/README.md` 的文件索引尚未补 `Services/AppEntryActions.cs`；`shell-dock` / `shell-menu-bar` README 的右键项集描述待同步。

| 步 | 状态 | 证据 |
|---|---|---|
| **S5（P2）** | ✅ 2026-09-14（代码 + 测试）；D4 待真机对比两档 | 新增 `packages/shell/shell-app-source/Services/AppFilterRules.cs`（纯函数规则表）+ `AppSourceService.ApplyAllProgramsFilter`，接入 `ScanAllPrograms` 的**两条产出路径**（引擎与本地——只接一条会让切换后端时条目集不一致，破坏 M2 平价）。<br>**开关默认关**：环境变量 `BETTERDESKTOP_APP_FILTER`（`on`/`true`/`1` 开启），与 `AppSourcePlugin.IsEngineBackendEnabled` 同惯例（本包未引用 shell-settings，不为一个开关引入新包依赖；设置中心可见开关待与索引后端状态行一并落地）；另留构造参数 `filterAllPrograms` 供测试与将来设置键注入。<br>**规则**（基线 = §7.1 的 536/1487 ≈ 36%）：① 目录段**整段相等** `bin` / `Scripts` / `nodejs` / `Roslyn`（`bin` 覆盖 `\usr\bin\`，后者单独占 246 项）；② 版本化段前缀 `jre` / `jdk`（`jdk-17` / `jre1.8.0_291` 命中；`Jreport` / `Jdkeeper` **不**命中——裸 `StartsWith` 会误杀）；③ 跨段路径形迹 `\VC\Tools\MSVC\`、`\Common7\IDE\CommonExtensions`、`Windows Kits`、`Tesseract-OCR`、`\Docker\cli-plugins\`；④ 服务 / 遥测 / 运行库：`*Service.exe`、`crash`、`report`、`telemetry`、`redist`、`vcruntime`；⑤ **主程序白名单优先**（`Ssms.exe` / `devenv.exe` / `vmware.exe`）。<br>**刻意不做**：不含裸 `runtime`（会误伤用户真用的运行时应用，§6.1 谨慎项）；**不按供应商目录整块滤**（会把 `Ssms.exe` / `devenv.exe` 一起滤掉）。<br>**匹配口径**：一律按**路径 / 文件名**判定，**不用显示名**——lnk 描述是整句文案，`Contains` 打在描述上正是分析稿 §4 的机制缺陷。<br>**可观测**：过滤生效时记 Info「全程序模式过滤生效：N → M 项」（不静默），条目为何变少可查。<br>测试：`shell-app-source-tests` 新增 37 例（工具链 13 / 服务遥测 7 / **真实应用保留 8** / **名字相近不误杀 5** / 空路径 4）→ **56 绿**；`shell-app-source` 单独构建 **0 警告 0 错误**。<br>**未做（记录）**：`filters.ini` 外置仍为 `DESIGN.md` 的既有开放项；「显示被过滤项」诊断视图与设置中心开关同属后续 |

| 步 | 状态 | 证据 |
|---|---|---|
| **S6（P3）** | ✅ 2026-09-14（代码 + 测试）；D5 待真机走查 | 三块一起落：<br>**P3-1 口袋目录**：设置键 `app-source.extra-roots`（JSON 字符串数组——`ISettingsService` 只支持基本类型）+ `AppSourceService.SetExtraScanRoots` + `ScanExtraRoots`（深度 3，与主根集一致）。**刻意在 C# 侧本地扫、不走引擎**：引擎根集是它自己的 `scan_roots`，不知道用户后加的目录——若只走引擎路径，口袋目录在「后端 = engine」时会**静默失效**；本地附加 + 按稳定 Id 合并，保证**切换后端不改变条目集**。目录不存在 / 不可读**记 Warn 不静默**。设置中心新增「应用来源」分区（`Sections/AppSourceSection.cs`，插件自贡献 + **UI 自包含**——本包未引用 shell-settings）。<br>**P3-2 桌面快捷方式**：`ScanDesktopShortcuts` 收「用户桌面 + 公共桌面」深度 1 的快捷方式，解析后按**稳定 Id** 与既有条目合并去重（依赖 S1 的 Id 统一）；**下载目录不纳入**（Q3 裁决）。设置键 `app-source.scan-desktop-shortcuts`（默认 **true**）。**两条产出路径（引擎 / 本地）都并入**。<br>**P3-3 手动添加入口**：应用提取器工具条新增「添加应用…」→ 多选（`*.exe;*.bat;*.cmd;*.com;*.msc;*.lnk;*.url;*.appref-ms`，沿用反编译参考实现 `tools/decomp/AppGrabberWindow.decomp.cs:1716` 的过滤器）→ 逐个 `AddByPath` + `Save`；走同一通道故与扫描出的同程序**同 Id**。**首次进入提示**（Q2）：状态栏追加一次「便携工具不在扫描范围…」。<br>**关键依赖结论（纠正原判断）**：`ISettingsService` 契约定义在 `packages/api`，且 Bootstrap 在插件加载前已 Provide → app-source **无需引用 shell-settings** 即可读写设置；设置变更经 `context.Effect` 订阅 `ShellEvents.SettingsChanged` 即时生效。<br>测试：`shell-app-source-tests` 新增 10 例（JSON 往返 / **坏数据降级不抛**——设置文件被手改坏不得让设置窗口白屏 / 去空与 trim）→ **66 绿**；`api`、`shell-app-source`、`shell-dock` 单独构建 **0 警告 0 错误** |
| **S8（P5）** | ✅ 2026-09-14（代码 + 测试）；D8 待真机走查 | **模型**：新增 `packages/api/Dock/PinnedHealth.cs`（三态 `PinnedHealthState` + `PinnedHealth` 报告）。<br>**多级重绑**：新增 `packages/shell/shell-dock/Services/PinnedRebindResolver.cs`（**纯函数**，事实经 `PinnedRebindSources` 注入 → 可脱注册表单测）。梯子：① Store AUMID ② App Paths（exe 名；HKLM 64/32 + HKCU）③ 同名 + 同安装根 ④ 同安装根（**显示名变了也救回**——旧实现只有「名称精确」一级）⑤ 同名（**仅当候选唯一**）。<br>**实现期发现并修正的三处隐患**：① 旧 `IsPinnedItemValid` 对 UWP **一律 `return true`** → 卸载后永久占位；现按 AUMID 存在性判定，且**Store 列表为空 = 未知**（保守保留，不据此判孤）。② 安装根「父目录的父目录」在浅目录下退化为盘根（`D:\Gone` 与 `D:\Other` 同根 → 误绑）→ **盘根不算根**。③ 同名候选不唯一时**不猜**（猜错 = 静默启动错误的程序）。<br>**回写**：`Healable` 项显示时静默回写快照（`IPinningService.UpdateSnapshot` —— **保留原主键**、**不发 `PinnedChanged`**。保留主键：主键是这次固定的身份，`AppGroupStore` 分组归属与排序都以它为键；不发事件：回写发生在「读取固定列表」过程中，发事件会形成「事件 → 重绘 → 再读取 → 再回写」重入）。`Orphaned` → 渲染层让位但**保留快照**（重装同路径自动回来）。<br>**可观测**：设置中心新增「Dock 固定项」分区（`Sections/DockPinnedSection.cs` + `DockAppsServiceBridge`；分区签名只给 `(ISettingsService, IThemeTokens)`，桥接是仓库既有做法）——逐项列健康态 + 当前路径 + 「重新绑定…」（手动指认）+ 「清理失效项」（**删除持久化数据**，与让位语义不同）。<br>**触发面**：健康判定在**读取固定列表时**执行（dock 每次重绘都会读 → 等价于「dock 显示时校验一次」）；**事件驱动（引擎 `AppSourceChanged`）仍为 deferred**（引擎侧事件未落地，见索引计划 §6.3）。`InvalidateScanCache` 同时失效重绑事实缓存（否则安装/卸载后最长 30s 内按旧事实判定）。<br>测试：`shell-dock-tests` 新增 14 例（App Paths 命中 / 命中但文件不在、同名、**同根改名仍救回**、同名跨根不猜、唯一同名绑回、**浅目录不共享根**、Store 在/不在/未知、全失配、ShortcutPath 回退、空路径、null 参数）→ **45 绿**；另 1 例 `ReflectionRtbAllocationTests` 是**既有分配门用例**（只测 RTB 分配，与本次改动无关），单独复跑 **3/3 通过** = flaky，已记录不修。<br>**已知限制（记录）**：主键仍是路径派生 → 被自愈项的「提取器绿点」可能与扫描条目对不上；根治需**路径无关锚点**（属 Id 方案变更，应另立计划）。 |

| 步 | 状态 | 证据 |
|---|---|---|
| **S7** | ✅ 2026-09-14 | 文档回写 6 处：① `shell-app-source/README.md` 新增「稳定 Id：三条路径的唯一产生点」与「全程序模式：过滤层 + 范围层」（规则表 + 口袋目录/桌面来源 + 粒度与匹配纪律）；② `shell-dock/README.md` 补条目菜单项集统一、添加应用入口、固定项三态与重绑层次、`IDockAppsService` 健康扩展、设置分区「Dock 固定项」；③ `shell-menu-bar/README.md` 更正 `Inject` 声明（原写「全服务 context.Get 可选获取」已过时）+ 新增 §17「搜索结果右键菜单（统一项集）」；④ `shell-core/README.md` 补 `Services/AppEntryActions`（应用条目菜单系统级动作公共实现）；⑤ `docs/analysis/2026-09-13-app-filter-derivation.md` 文首加**落地状态**（§7.4 过滤层 + 范围层已实现、开关默认关、实现期修正 3 处）；⑥ TECH-KNOWLEDGE 新建 `05-图标/507-app-filter-rules.md`（按 `_模板.md` 契约头 + 红线 5 条 + 实现 + 测试 + 变体），并同步 `索引.md`（区间 501→507、05 区表新增 507、关键词导航、待沉淀勾选）与 `index-ai.md`（扁平索引新增一行） |

> **本计划 S1–S8 全部完成**（S7 为最后一笔）。未做/延期项见各步「未做（记录）」与 §12 Deferred。 |
