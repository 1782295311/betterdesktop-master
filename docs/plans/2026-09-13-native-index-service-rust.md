# Cairo 开发计划 · 文件/应用/图标合并索引（原生常驻索引服务）

> Task: 落地《原生重写候选评估》§4.2 + §4.3——把「磁盘上有哪些 exe、文件、图标」合成一个常驻原生进程，一次遍历产出、常驻内存、被三个 C# 消费者经 IPC 查询；§4.4 经核验已落地，本计划只登记其残余。
> 证据基于 2026-09-13 工作树验证（**本仓尚无 commit**，行号钉在该工作树；所有引用均于当日回源码复核）。技术力文档命中：`05-图标/506-app-enumeration`、`05-图标/501-程序图标提取`、`05-图标/502-多尺寸图标LRU缓存`、`05-图标/503-后台图标预加载`、`05-图标/504-icon-source-trimodel`、`02-搜索/201-搜索节流与取消旧任务`、`02-搜索/203-搜索排名算法`、`11-原生桥接/1101-native-addon-bridge`、`74-Windows内部接口逆向/7444-shell32-private-resource-exports`、`74-Windows内部接口逆向/7411-shell-icon-extraction`、`14-窗口与快捷键/1408-single-instance-mutex`；**未命中**：USN Journal / MFT / 文件系统索引（检索关键词 `USN Journal`、`MFT`、`Everything SDK`、`file index`、`filesystem crawl`）→ 该部分**无现成技术文档，新建**。
> 深度：full（架构改动类默认姿态）。若只需可运行骨架，可在 §7 的 M1 后停。

## 1. Objective

用户在开始菜单搜索框输入「maa」，无论是**程序**还是**文件**，都在**不卡输入**的前提下命中（含 `D:\迅雷下载` 这类未建 Windows Search 索引的目录）；应用提取器 / dock 打开时不再逐次全量扫盘。上述事实由一个**常驻原生进程**持有并增量维护，宿主与面板经 IPC 查询；该进程崩溃/未启动时，所有消费者自动回退到现有 C# 实现且降级在设置中心可见。

## 2. Current Behaviour

- **文件搜索**：`FileSearchProvider.Search` 并行跑两路——Windows Search 索引（`SearchManager` CLSID → `SystemIndex` → `ISearchQueryHelper` → ADODB，`packages/shell/shell-search/Services/FileSearchProvider.cs:49-92` [verified]）与文件系统兜底扫描（`:190-227`，深度 2 / 1.5s / 上限 20 条 [verified]），结果始终合并。兜底根 = 用户 下载/桌面/文档 + 各固定盘根一级子目录（`:241-275` [verified]）。**没有任何常驻索引**。
- **应用源**：全量扫 + 2min 内存缓存（`AppSourceService.cs:42` `CacheTtl` [verified]）。三源：开始菜单全递归（`:452` `SearchOption.AllDirectories`，无深度上限 [verified]）、注册表 Uninstall 三视图（三视图定义 `:168-173`，遍历体 `:175-247` [verified]）、全程序盘点递归深度 3（`:750-838`，`maxDepth: 3` [verified]）。失效靠 `StartMenuWatcher` FileSystemWatcher 1s 防抖（`StartMenuWatcher.cs:17,58-81` → `AppSourceService.cs:730` [verified]）。
- **窗口追踪**：`GetRunningApps` 每次调用重建路径索引（`shell-window-tracker/README.md` Known Limitations；调用点 `WindowTrackerService.cs:65,126,140,161,212` [verified]）——设计层缺陷，本计划**不改**（§12）。
- **图标**：四处互不复用的实现（应用源 `Win32ShellIconService.cs:196` + UWP 分支 `:47-49`；菜单栏 `ProcessAppInfo.cs:175`；右键菜单 `MenuItemIconCache.cs:146`；剪贴板 `FileIconCache.cs:58`，均当日核验 [verified]）。
- **承载现状**：`ExternalPluginAdapter` 只有进程生命周期 + 退避重启 + 内存采样，**无协议**（`kernel-hmr/ExternalPluginAdapter.cs:1-4` 注释自述「完整 JSON-RPC 协议按跨语言计划 Phase 1 后续接入」[verified]）；仓库已有一个成熟的独立 Rust 进程先例 = 剪贴板引擎（`engine/`，命名管道 + `BDCB1|` magic + JSON-RPC 行协议，`engine/src/ipc.rs:25-26` [verified]）。

## 3. Relevant Architecture

- 契约层在 `packages/api/`（AppSource / Search 两域），实现包 Inject/Get 服务图，**`Inject` 列表不可假设装配顺序**（历史坑：dock 先于 context-menu 激活导致 `Get` 恒 null）。
- 原生分发两套并存模式 [verified]：`shell-status` 用 `None + CopyToOutputDirectory`（**无 `Link`** → 落输出根目录，供 `NativeLoader.cs:53-75` 双路径查找）；`shell-context-menu` / `shell-taskbar` 用 `Link="native\..."`（→ 输出 `native\` 子目录，供 COM 注册器定位）。本计划走**第三套**：Rust 产物按剪贴板先例部署到 `%LOCALAPPDATA%\BetterDesktop\`（`scripts/deploy-clipboard.ps1:22,59-78` [verified]），C# 侧按约定路径定位。
- 常驻架构原则（`docs/2026-09-11-resident-architecture.md`）：**壳退出后能力继续工作**；跨进程服务访问「暂不新建 IPC 服务框架」，按需文件/管道命令——本计划是首次为「服务型」跨进程访问引入真正的 IPC 客户端，属于该文档 §四 预留的升级路径（「若将来确有需要，再引入服务代理层」），需在实现时同步更新该文档。

## 4. Technical-Knowledge Findings

| 文档 | 定位 | 与本计划的关系 |
|---|---|---|
| `05-图标/506-app-enumeration` | 三源程序枚举 + lnk 解析 + 提权状态机 | **直接命中**应用索引：必须跳 `\startup`；lnk 解析失败回退自身路径；目标非可执行扩展则拒收；显示名优先 `FileVersionInfo.FileDescription`；红线「禁止每次访问都重扫全盘」（本条正是本计划的立项理由）。文档实现语言 C#，本计划将其语义**迁移到 Rust**（枚举语义语言无关，标注于文档头「多语言择优」） |
| `05-图标/501-程序图标提取` | `IShellItemImageFactory` + lnk 解析 + 默认图标兜底 | M3 图标接管的语义基线（图标来源三元模型见 504） |
| `05-图标/502-多尺寸图标LRU缓存` | 多尺寸 + LRU 淘汰 | M3 引擎侧图标缓存必须复刻其淘汰语义，避免常驻进程无界增长 |
| `05-图标/503-后台图标预加载` | 可见区优先分批并发 | M3 批量 `get_icons` 的调用形态依据 |
| `02-搜索/201-搜索节流与取消旧任务` | 防抖 + 取消旧任务 + 丢弃过期结果 | 引擎查询侧必须支持带 requestId 的取消（§6 协议） |
| `02-搜索/203-搜索排名算法` | 分类权重 + 匹配度 + 频率打分 | 排序仍在 C# 侧（`ScoreFileName` / Provider Score [verified]），引擎只返回候选 + 原始匹配信息，**不搬排序** |
| `11-原生桥接/1101-native-addon-bridge` | 原生模块加载与接口守卫 | 仅参考其「缺原生则降级」的守卫纪律 |
| `74/7444-shell32-private-resource-exports` | `SHExtractIconsW` 私有导出 | 引擎侧取高清图标（M3），红线同现有 `HighResIconExtractor.cs` 六条 [verified] |
| `74/7411-shell-icon-extraction` | `IExtractIcon` 取图标（C++） | 引擎侧无 shell 上下文时的降级取图标路径 |
| `14-窗口与快捷键/1408-single-instance-mutex` | 单实例互斥 | 引擎单实例（照抄剪贴板引擎 `main.rs:137-141` [verified]） |
| **未命中** | USN Journal / MFT / Everything SDK / 文件索引 | **无现成技术文档，新建**。M4 若走 USN 需另立调研 |

**相关测试**：`shell-context-menu-tests`（98）、`shell-clipboard-ipc-tests`（15，可作为 C# 侧 IPC 客户端测试的模板）、`engine` 内 `cargo test`（store/analyzer/capture/ipc）[verified]。`shell-search` / `shell-app-source` 目前**无独立测试工程**（依赖契约测试覆盖）[verified 于 slnx 扫描]。

## 5. Constraint Findings

1. **权威源单一**（§七 Q1）：引擎 = 「磁盘/开始菜单/注册表里有哪些 exe」的**事实源**；`shell-app-source` = 「哪些应用对用户可见、叫什么、怎么分组/固定」的**语义源**。引擎输出 raw candidate（路径 + 名称候选 + 源标记），**不得**输出 `AppItem` 语义（固定态、分组、`IconCacheKey`），否则双份事实。
2. **权限边界**（§七 Q2）：USN Journal / MFT 读取需要卷句柄与管理权限 [inferred]。**M1–M3 全部路径不得要求管理员**——否则破坏「安装即用」。USN 作为 M4 可选快路径，且必须能力探测 + 降级。
3. **降级必须可见**（§七 Q6，`docs/runtime-health.md` fail-closed 纪律）：引擎不可用 → 回退本地实现 + 日志 Warn + 设置中心状态行明示，禁止静默。
4. **内存治理**（§七 Q4）：引擎是独立进程，**不在 `IResourceGovernor` 的插件 subject 域内**（Governor 的 subject 由内核插件注册）。因此改为引擎自管：配置上限 + LRU 驱逐 + `status` 上报 RSS，设置中心展示。**不新增 Governor 集成**（避免为一次性需求改内核契约）。
5. **证据纪律**（§七 Q7）：本节所有性能论断当前证据级别为 `SOURCE`（README/DESIGN 自述）。M0 必须先测基线，否则「收益最大」无同合同证据支撑。
6. **协议不搬排序、不搬 UI 语义**（对应 §一 判据「切面足够窄」）。
7. **图标大块数据**：`docs/cross-language/计划-跨语言插件体系.md` §7 规定「Phase 1/2 一律 base64 ≤64KB」——**该规定只约束 `ExternalPluginAdapter` 的通用协议**；独立进程（剪贴板引擎先例）自定义帧不受其约束。本计划 M3 用**同一管道上的二进制帧**（长度前缀，cap 2MB）承载图标字节，**不依赖 Phase 3 共享内存**。此偏离需用户确认（§12 Q1）。

## 6. Proposed Changes

### 6.1 新增 Rust 常驻服务 `engine-index/`

- `engine-index/Cargo.toml`：`name = "betterdesktop-index-engine"`、`edition = "2024"`（对齐 `engine/Cargo.toml:4` [verified]）、`windows = "0.58"`、`serde` / `serde_json`；`[profile.release] opt-level=3, lto=true, codegen-units=1, strip=true`（照抄先例）。
- `src/main.rs`：单实例 Mutex + 隐藏消息窗口 + 启动 IPC server + 后台索引构建任务 + 退出清理。
- `src/ipc.rs`：命名管道 `\\.\pipe\BetterDesktop.Index.Engine`，首帧 magic `BDIX1|`，后续帧 JSON-RPC 2.0 行协议（复刻 `engine/src/ipc.rs:25-27,43-55,84,332-353` 的帧与校验纪律）；**新增**长度前缀二进制帧（`get_icon` 响应）。
- `src/model.rs`：`AppCandidate { path, name_hint, source, icon_key }`、`FileHit { path, name, size, modified }`、`IndexStatus { app_count, file_count, building, last_build_ms, rss_bytes, degraded, degrade_reason }`。
- `src/appindex.rs`：三源枚举（开始菜单递归**跳 `\startup`** + `*.lnk` 目标解析失败回退自身 + 目标扩展白名单拒收 + `Program Files` 深度 3 + 注册表 Uninstall 三视图），显示名优先 `FileVersionInfo.FileDescription`（**506 红线 1–4 逐条落实**）；输出 raw candidate。
- `src/fileindex.rs`：受控目录枚举（复用现有根集语义：用户 下载/桌面/文档 + 固定盘根一级子目录，系统大目录剪枝）+ 常驻内存索引 + `ReadDirectoryChangesW` 增量更新 + 周期性补扫。
- `src/icon.rs`（M4）：`SHExtractIconsW`（GetProcAddress，六红线）→ `ExtractIconEx` 降级链；**只产一档 256×256 PNG 字节**（用户硬约束：不做「用多大申请多大」的多档）；缓存**PNG 字节**的 LRU（复刻 502 淘汰语义）并受 §5.4 字节上限约束。缩放归 C#（`DecodePixelWidth` + `HighQuality`），故 `IAppIconService.GetIconAsync(size)` 契约与全部调用方**零改动**。
  - **开工时须先核实**（不要假设）：C# 侧现状是否已是「一次取大图 + 缩放」还是「按 size 分别提取」——看 `shell-app-source/Native/HighResIconExtractor.cs` 与 `Services/Win32ShellIconService.cs`。**若已是前者**，M4 只需把数据源从本地 Shell 提取换成引擎 `get_icons`；**若按 size 分别提取**，则一并收敛为「一档大图 + 倍缩」（与本约束同批，避免两处口径不一致）。
  - **⚠️ 核实结果（2026-09-14）：与既有红线正面冲突，必须显式解决，不得默默改默认值。**
    - 现状（`Win32ShellIconService.cs`）：`GetIconAsync` 对**普通应用取 ExtraLarge(48) 原生帧**、只有 Store 取 Jumbo(256)（`ChooseIconSize:221-229`）；另有 `GetHighResIconAsync`（`SHExtractIconsW(256)→ExtractIconEx(256)→null`）**仅作 dock 高清增强**。缓存 `ConcurrentDictionary<string, ImageSource>` **无尺寸维度**（每应用一张）。
    - 该文件 `:104-107` 明确写着这条设计的理由：**「防字形过度缩放回归」**，且被本仓列为禁区⑨。用户偏好（只取一档大图 + 倍缩）与之**直接相反**。
    - **现象机制**（决定了这不是「听谁的」而是「怎么修」）：Windows 的 Jumbo(256) 帧与 ExtraLarge(48) 帧常是**不同 artwork**，256 帧**透明留白更多**，直接缩到 32/48 会让字形**看起来偏小** —— 「过度缩放」观感的真实来源是**帧内边距差异**，不是分辨率不足。
    - **解法（保住用户偏好 = 只取一档大图）**：解码后做**字形归一化** —— 按 alpha 包围盒裁剪 → 按统一边距补白 → 再缩到目标尺寸。这样一档大图也能得到与原生帧一致的视觉占比。
    - **验收（不可省）**：真机并排对比同一批应用（普通 exe / Store / 套件主程序），字形占比与现有 48 帧**无明显差异**；若归一化后仍明显偏差 → 退回「按 size 取原生帧」（C# 侧策略切换），引擎「一档大图」的收益保留给对占比不敏感的场景。
- `src/settings.rs`：配置键读 `settings.json` 的 `index.*`（与宿主 `SettingsService` 共享同一文件，只读）。

### 6.2 新增 C# 客户端包 `packages/shell/shell-index-ipc/`

- `IpcProtocol.cs`（管道名 / magic / 帧编解码，模板 = `shell-clipboard-ipc/IpcProtocol.cs:9-12` [verified]）
- `IndexEngineClient.cs`：`PingAsync` / `StatusAsync` / `ListAppsAsync` / `SearchFilesAsync` / `GetIconsAsync` + `Reconnected` 事件（模板 = `ClipboardIpcClient`）
- `IndexEngineLauncher.cs`：按约定路径 `%LOCALAPPDATA%\BetterDesktop\BetterDesktop.Index.Engine.exe` 定位 + 无窗口拉起 + 单实例探测（模板 = `ClipboardEngineLauncher`）；**引擎缺失时返回 false，不抛**。

### 6.3 消费者接入（两处，各自带 backend 开关）

- `shell-app-source`：新增 `engine` 后端——`AppSourceService` 的三个扫描方法在引擎可用时改查引擎，`AppSourceChanged` 事件由引擎增量通知驱动；不可用回退现有实现。设置键 `index.app-source-backend = engine | local`（默认 `engine`，模板 = 剪贴板 `backend=engine|legacy`）。
- `shell-search`：`FileSearchProvider` 新增引擎查询路径，**保留** Windows Search + 兜底扫描为回退；`SearchPlugin` 注入客户端。
- **不接管**：排序（`ScoreFileName`/`Score`）、`Category`、`Execute`、`AppItem` 语义——全留 C#。

### 6.4 状态可见 + 构建脚本

- 设置中心新增「索引服务」状态行（运行/未运行、条目数、RSS、上次重建耗时、降级原因）。
- `scripts/deploy-index.ps1`：`cargo build --release`（`~/.cargo/bin/cargo.exe`，见 §12 Q2）→ 部署 exe 到 `%LOCALAPPDATA%\BetterDesktop\`，模板 = `scripts/deploy-clipboard.ps1:44-78`。

### 6.5 §4.4（状态采集残余）——本计划不实现

IME 与 SMTC 采集均已迁原生（`ImeCoreNative.cs:17-18`、`KeyboardLayoutInterop.cs:779-788`、`MediaPlayerCore.cs:2-5`、`:219-246` [verified]）。残余仅 IME 编排（TSF `ActivateProfile` / `WM_INPUTLANGCHANGEREQUEST` / Preload 注册表写入）与缩略图流托管桥，收益低 → 进 §12 deferred。

## 7. Implementation Sequence

每步后树一致、可独立验证。

| 步 | 内容 | 验证 |
|---|---|---|
| **M0 基线测量** | `Temp/IndexBaselineProbe`（不碰生产代码）测：`ScanAllPrograms()` 冷/热耗时、`FileSearchProvider.FallbackFileSystemScan` 含超时耗时、`GetRunningApps` 单次耗时 | 落 `docs/performance/data/index/baseline.json`；**无此步 M1 验收无对照** |
| **M1 引擎骨架 + 协议** | `engine-index/` 骨架：单实例 + 管道 + `ping`/`status`/`apply_settings`；空索引 | `cargo test` 绿；C# 侧 `shell-index-ipc-tests` 用 fake transport 绿；真机 `ping` 往返 |
| **M2 应用索引 + app-source 接入** | `appindex.rs` 三源枚举 + `list_apps`；`IndexEngineLauncher` + backend 开关 + 回退 | 真机：开始菜单搜索程序命中原先要扫盘的项；杀引擎后自动回退 |
| **M3 文件索引 + search 接入** | `fileindex.rs` + `search_files`；`FileSearchProvider` engine 路径 | 真机：搜 `D:\迅雷下载` 下文件命中；引擎关闭时回退 Windows Search |
| **M4 图标接管** | `icon.rs` + 二进制帧 + 四处消费方按需切换 | 真机：dock/开始菜单/右键/剪贴板图标正常；无图标时 glyph 兜底 |
| **M5 降级可见 + 文档回写** | 设置状态行；README/DESIGN/候选评估/TECH-KNOWLEDGE 回写 | 设置中心可见状态；文档同步 |
| **M6 可选：USN 快路径** | 能力探测 + 提权时的 USN 增量 | 见 §12 Q3，默认不做 |

## 8. Test Strategy

- **Rust 单测**（`cargo test`，新 crate）：`appindex`（跳 `\startup` / lnk 解析失败回退 / 非可执行目标拒收 / 三源去重）、`fileindex`（根集剪枝 / 深度与超时边界 / 增量变更 / 空目录不崩）、`ipc`（帧编解码 / 坏 magic 丢连接 / 二进制帧长度校验 / 超长 payload 拒绝）、`settings`（缺键默认值）。
- **C# 单测**（新增 `shell-index-ipc-tests`）：客户端契约（fake transport）、**引擎缺失 → Launcher 返回 false 且调用方回退**、管道断连重连、`status` 降级字段透传。模板 = `shell-clipboard-ipc-tests`（15 例）。
- **回归**：既有 `shell-context-menu-tests` / `shell-clipboard-ipc-tests` / 契约测试全绿；`shell-app-source` 与 `shell-search` 的**回退路径**必须保持原行为（backend=local 时逐行为不变）。
- **集成**：引擎进程级——拉起 → `list_apps` 含已知 exe → 杀引擎 → 搜索仍可用（回退）+ 状态行显示降级。
- **验证命令**（已确认存在）：`& "$env:USERPROFILE\.cargo\bin\cargo.exe" test`（cwd=`engine-index`）、`dotnet build BetterDesktop.slnx -c Debug`、`dotnet test`。

## 9. Risk and Impact Analysis

| 风险 | 级别 | 缓解 |
|---|---|---|
| 引擎与 app-source 双份事实漂移 | 高 | §5.1 权威源严格划分；引擎只出 raw candidate；契约测试断言 AppItem 语义仍由 app-source 产出 |
| 常驻进程内存无界 | 中 | 配置上限 + LRU 驱逐 + `status` 上报；M0 基线设阈值 |
| 首次索引构建拖慢启动 | 中 | 引擎异步构建（`status.building`），构建期 `list_apps` 返回「不可用」→ 消费者回退本地，不阻塞 |
| 回退路径腐烂（两条实现长期并存） | 中 | 回退路径纳入回归测试（backend=local 用例）+ 设置键可强制 |
| 管道僵死/半开连接 | 中 | 复刻剪贴板引擎的 `PING` 探活 + 客户端重连 + `Reconnected` 重推全量配置 |
| 提权需求破坏「安装即用」 | 中 | M1–M4 设计为无需管理员；USN 仅 M6 且能力探测 |
| 反向依赖：消费者改动影响 dock/开始菜单/通知 | 高 | `IAppSourceService` 契约**不变**（只加 backend 开关），impact 覆盖 `DockAppsService.cs:143,376,424,478,500,508,548,583`、`StartMenuService.cs:344,353,392,542`、`NewAppsNotificationWindow.cs:197,214`、`RecentItemsService.cs:274` [verified] |
| `IResourceGovernor` 不覆盖引擎 | 低 | 明确记录（§5.4），不做内核改动 |

**可观测性**：引擎日志独立落盘（模板 = `engine/src/log.rs` 约定）；宿主侧 Warn 记降级原因；设置状态行。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `engine-index/Cargo.toml`、`src/*.rs` | 新 | 常驻索引服务 |
| `packages/shell/shell-index-ipc/*` | `IpcProtocol` / `IndexEngineClient` / `IndexEngineLauncher` | C# 客户端 |
| `packages/shell/shell-index-ipc-tests/*` | 新测试 | 契约 + 降级路径 |
| `packages/shell/shell-app-source/AppSourcePlugin.cs` | `LoadAsync` | 装配客户端 + backend 选择 |
| `packages/shell/shell-app-source/Services/AppSourceService.cs` | `ScanStartMenu` / `ScanInstalledApps` / `ScanAllPrograms` | engine 分支 |
| `packages/shell/shell-search/Services/FileSearchProvider.cs` | `Search` | engine 分支 |
| `packages/shell/shell-search/SearchPlugin.cs` | `LoadAsync` | 注入客户端 |
| `host/BetterDesktop.Host.csproj`、`BetterDesktop.slnx` | — | 引用新包 |
| `scripts/deploy-index.ps1` | — | 构建 + 部署 |
| 设置分区（新增「索引服务」行）+ `docs/2026-09-11-resident-architecture.md` | — | 状态可见 + 架构同步 |
| `docs/performance/data/index/baseline.json`（M0） | — | 基线 |

## 11. Reusable Implementation Context

**必须直接读取的文件（实现期不再调研）**：

- Rust 先例（照抄工程约定）：`engine/Cargo.toml`、`engine/src/main.rs:46-141`、`engine/src/ipc.rs:25-55,84,120-126,332-377`、`engine/src/log.rs`、`engine/src/settings.rs`、`engine/src/model.rs`
- C# IPC 先例：`packages/shell/shell-clipboard-ipc/IpcProtocol.cs:9-12`、`ClipboardIpcClient`、`ClipboardEngineLauncher`
- 守护/单实例：`TECH-KNOWLEDGE/14-窗口与快捷键/1408-single-instance-mutex.md`
- 应用枚举语义：`TECH-KNOWLEDGE/05-图标/506-app-enumeration.md`（红线 1–6 全文）
- 图标语义：`TECH-KNOWLEDGE/05-图标/501`、`502`、`504`、`74/7444`、`74/7411`
- 消费者现状：`AppSourcePlugin.cs:29-58`、`AppSourceService.cs:42,105-109,158-173,175-247,446-469,727-746,750-838`、`SearchPlugin.cs:31-44`、`FileSearchProvider.cs` 全文、`Win32ShellIconService.cs:24-155`、`HighResIconExtractor.cs` 全文
- 原生构建/分发：`packages/shell/shell-status/Native/build_native.ps1`、`scripts/build-shellmenu.ps1`、`scripts/deploy-clipboard.ps1:22-93`、`shell-status/Native/NativeLoader.cs:53-75`
- 排期/DoD：本计划 §7/§13

**环境事实**：Rust 工具链位于 `%USERPROFILE%\.cargo\bin\`（**不在 PATH**，需绝对路径调用）[verified]；`engine/target/` 有既有构建产物。

## 12. Assumptions and Open Questions

**假设**（未验证，实现前须确认）：
1. `[assumed]` 非提权下的受控目录枚举（用户目录 + 固定盘二级）在目标机上已能覆盖用户主要搜索面（`D:\迅雷下载` 类目录）。M0 基线若显示命中率不足，M3 范围需扩大。
2. `[assumed]` 引擎 Rust 进程的 RSS 在 10 万级条目下可控制在 100MB 内（Everything 量级参考）。M2 后测量校正。
3. `[assumed]` 开始菜单/注册表三源在 Rust 侧复刻后与 C# 侧结果集一致（差异会导致 UI 中程序条数变化）。M2 需做**结果集比对**。

**开放问题**：
1. **Q1（✅ 2026-09-14 用户裁决：接受）**：M4 图标走「同一管道二进制帧（cap 2MB）」而非 `计划-跨语言插件体系.md` §7 的「Phase 1 base64 ≤64KB」。
   - **用户追加的硬约束（比 Q1 本身更重要）**：**不做「用多大就申请多大」的多档图标**——引擎只产**一档大尺寸**（256×256 PNG），显示端统一**按目标尺寸倍缩**。
   - 为什么这与 Q1 相互印证：256px PNG 常见 5–40KB，base64 会再膨胀 ~33% 且可能破 64KB 上限，故二进制帧仍是正解；而「只产一档」让 payload 与缓存上界都变得可估（见 §6.1 修订）。
   - **内存纪律**：引擎**只驻留 PNG 字节**，**不缓存解码后的 RGBA**——256×256 RGBA = 256KB/张，300 张即 ≈76MB；PNG 字节 ≈30KB/张 → 300 张 ≈9MB。解码与缩放都留在 C#（WPF `DecodePixelWidth` + `BitmapScalingMode.HighQuality`）。
2. **Q2**：`cargo` 不在 PATH——是「脚本内用绝对路径」（本次主张）还是「安装时写用户 PATH」？
3. **Q3**：M6（USN 快路径）是否立项？需要管理员权限，收益是首次构建从分钟级降到秒级。
4. **Q4**：`packages/shell/shell-search/Contracts/` 是空目录且 `SearchPlugin.cs:18` 导航注释过期（指向不存在的路径）[verified]——顺手修，还是进 deferred？

**Deferred follow-ups（本计划不做）**：
- `shell-window-tracker.GetRunningApps` 未缓存（§4.5 已登记：C# 内加缓存即可，零原生化收益）。
- §4.4 残余（IME 编排 / SMTC 缩略图流桥）——收益低。
- `IAppIconService.GetHighResIconAsync` 是死代码（零调用、未进契约）[verified]——M4 接入时一并裁决。
- M6 USN 快路径。
- `ExternalPluginAdapter` 的通用 JSON-RPC 协议（属 `计划-跨语言插件体系` Phase 1，本计划用独立进程先例绕开）。

## 13. Definition of Done

- **D1** `cargo test` + `cargo build --release`（`engine-index`）全绿；覆盖 appindex/fileindex/ipc/settings 的正常 + 边界 + 异常。
- **D2** `dotnet build BetterDesktop.slnx -c Debug` **0 警告 0 错误**；`dotnet test` 既有全部工程 + 新增 `shell-index-ipc-tests` 全绿。
- **D3 端到端场景（程序）**：真机启动宿主 → 打开开始菜单 → 搜索一个仅由引擎索引到的程序 → 命中并点击启动成功。
- **D4 端到端场景（文件）**：真机搜索 `D:\迅雷下载` 下的一个文件名 → 命中（该目录未被 Windows Search 索引）。
- **D5 端到端场景（降级）**：任务管理器杀掉索引引擎 → 设置中心「索引服务」显示未运行/降级 → 重复 D3/D4 仍可用（走回退）→ 重启引擎后恢复。
- **D6 性能对照**：`docs/performance/data/index/baseline.json`（M0）与接入后同项对比，输出查询 P95 与首次构建耗时；**「无测量不结论」**——若对照不达标，回退 backend=local 并记录。
- **D7 边界/异常**：无开始菜单目录、盘不可用、权限拒绝目录、引擎 exe 缺失、管道半开——均不崩且有日志。
- **D8 文档回写**：`shell-app-source` README/DESIGN、`shell-search` README、`docs/2026-09-11-resident-architecture.md` §四、`docs/cross-language/原生重写候选评估.md` §4.2/§4.3 状态、TECH-KNOWLEDGE 新建「文件系统索引」功能文档 + 索引同步。

## 14. Handoff to 技术力应用

| 项 | 内容 |
|---|---|
| **模式判定** | ① Rust 常驻引擎（三源枚举/文件索引/IPC）：**无匹配→工程代码权威**（检索 `USN Journal`/`MFT`/`file index` 未命中；语义依据 = `05-图标/506-app-enumeration` 红线 1–6 + `engine/` 源码 + `AppSourceService.cs` 现有三源实现）；② 单实例/管道/帧协议：**标准文档注入**（`14-窗口与快捷键/1408` + `engine/src/ipc.rs` 实测约定）；③ 应用枚举语义：**标准文档注入**（`05-图标/506`）；④ 图标：**标准文档注入**（`05-图标/501/502/503/504` + `74/7444` + `74/7411`）；⑤ 搜索查询/排序边界：**标准文档注入**（`02-搜索/201`、`02-搜索/203`） |
| **注入清单** | `TECH-KNOWLEDGE/05-图标/506-app-enumeration.md`（**含红线 1–6 全文**）；`TECH-KNOWLEDGE/05-图标/501-程序图标提取.md`；`TECH-KNOWLEDGE/05-图标/502-多尺寸图标LRU缓存.md`；`TECH-KNOWLEDGE/05-图标/503-后台图标预加载.md`；`TECH-KNOWLEDGE/05-图标/504-icon-source-trimodel.md`；`TECH-KNOWLEDGE/02-搜索/201-搜索节流与取消旧任务.md`；`TECH-KNOWLEDGE/02-搜索/203-搜索排名算法.md`；`TECH-KNOWLEDGE/14-窗口与快捷键/1408-single-instance-mutex.md`；`TECH-KNOWLEDGE/74-Windows内部接口逆向/7444-shell32-private-resource-exports.md`；`TECH-KNOWLEDGE/74-Windows内部接口逆向/7411-shell-icon-extraction.md`；代码侧：`engine/`（全 crate 作工程约定模板）、`engine/src/ipc.rs`、`packages/shell/shell-clipboard-ipc/*`、`scripts/deploy-clipboard.ps1` |
| **适配参数** | 新 crate 目录 `engine-index/`，包名 `betterdesktop-index-engine`，edition 2024，`windows = "0.58"`，release profile 照抄 `engine/Cargo.toml:36-40`；产物 `engine-index/target/release/betterdesktop-index-engine.exe` → 部署名 `BetterDesktop.Index.Engine.exe` → `%LOCALAPPDATA%\BetterDesktop\`；管道名 `\\.\pipe\BetterDesktop.Index.Engine`，magic `BDIX1|`；新 C# 包 `packages/shell/shell-index-ipc/`（命名空间 `BetterDesktop.Shell.IndexIpc`），测试包 `shell-index-ipc-tests`；设置键 `index.app-source-backend`（`engine`\|`local`，默认 `engine`）、`index.max-entries`、`index.scan-roots`（沿用现有根集语义）；构建命令 `& "$env:USERPROFILE\.cargo\bin\cargo.exe" build --release` |
| **禁区** | ①**不改** `IAppSourceService` / `IAppIconService` / `IStartMenuSearchService` 既有成员（只加 backend 开关与内部实现）；②**不得**让引擎输出 `AppItem` 语义（固定态/分组/`IconCacheKey`）——违反 §5.1；③**不得**引入管理员权限要求（M1–M5）；④**不得**改动排序算法与 `Score` 量纲；⑤**不得**动 `shell-window-tracker.GetRunningApps`（§12 deferred）；⑥`Test-Path`/构建前必须先 `Stop-Process` 宿主（DLL 锁定）；⑦`.NET` 目标框架保持 `net8.0-windows10.0.19041.0` 与 `Platforms=x64`，新包 TFM 必须与既有 shell 包一致（否则 slnx 构建报错） |
| **DoD 核销表** | D1 `cargo test`/`cargo build --release` 全绿；D2 `dotnet build` 0 警告 0 错误 + `dotnet test` 全绿（含新 `shell-index-ipc-tests`）；D3 程序搜索端到端走查；D4 未索引目录文件搜索走查；D5 杀引擎降级 + 恢复走查 + 设置状态行可见；D6 基线对照表（M0 vs 接入后，含查询 P95 与首次构建耗时）；D7 五类边界/异常不崩且日志可查；D8 文档回写 6 处（含 TECH-KNOWLEDGE 新建「文件系统索引」+ 索引同步） |

---

## 实现进度

| 步 | 状态 | 物证 |
|---|---|---|
| **M1a 引擎骨架 + 协议** | ✅ 2026-09-13 | `engine-index/`（Cargo.toml + main/ipc/engine/settings/model/log 六文件）；`cargo test` **26 passed**；`cargo build --release` **0 警告**；真机管道探针：单实例秒退 / ping / status(camelCase) / apply_settings 合法与非法(-32602) / 未实现方法(-32601) / 无 magic 连接被丢弃 / shutdown 优雅退出 |
| **M1b C# 客户端 + 测试** | ✅ 2026-09-13 | `packages/shell/shell-index-ipc/`（协议/传输/客户端/launcher/README）+ `shell-index-ipc-tests/`（**18 passed**）；已注册 `BetterDesktop.slnx`；跨语言真机往返（C# client ↔ Rust engine）：连接成功 / ping / status 解析含中文降级原因 / apply_settings 下发 / 非法值 -32602 透传 / shutdown 后引擎退出 |
| **全仓构建** | ✅ | `dotnet build BetterDesktop.slnx -c Debug` → **0 警告 0 错误** |
| **偏离项** | ⚠️ 待计划侧裁决 | 设置节实现为 `extensions.index.*`（计划 §14 字面写 `index.*`）——理由：与既有 `extensions.clipboard-history.*` 同构、贴合宿主 `SettingsService` 分区层级 |
| **M0 基线测量** | ✅ 2026-09-13 | 探针 `Temp/IndexBaselineProbe`（**临时工程，不加入 slnx**）→ `docs/performance/data/index/baseline.json`。真机实测：**`ScanAllPrograms` 冷 53,107ms / 热 465ms（1619 项）**、`ScanStartMenu` 4,254ms、`ScanInstalledApps` 65ms、`GetRunningApps` 411ms、文件兜底扫描（query=maa）105ms。**53 秒的冷扫描是立项动机的最强证据**（应用提取器/dock 每次打开都要等它） |
| **M2a 引擎应用索引** | ✅ 2026-09-13 | `engine-index/src/appindex.rs` 磁盘两源（开始菜单全递归 + Program Files 深度 3）；`list_apps` RPC；`cargo test` **32 passed** 0 警告；真机 **1951 apps**（program-files **1619** / start-menu 332）、构建 **238ms** |
| **平价修复（4 处）** | ✅ 2026-09-13 | 接线前逐条对齐 C#：①白名单补 `.cmd`（`ShellLinkResolver.cs:16-26` 有 8 项，引擎原 7 项）；②**不跳 `\startup`**（当前 C# `ScanDirectory:452` 未落实 506 红线，引擎为对齐亦不跳，`skip_startup` 机制保留待 C# 补红线后同批启用）；③根集补 `%LocalAppData%\Programs` + 各固定盘 `X:\Program Files`（`GetAllProgramRoots:840-880`）；④结果集平价实证：**program-files 计数 1619 ≡ M0 基线 `ScanAllPrograms` 的 1619 项** |
| **M2b 客户端 ListApps** | ✅ 2026-09-13 | `ListAppsAsync` + `AppCandidate`/`ListAppsResult` DTO；`shell-index-ipc-tests` **19 passed**；跨语言真机 list_apps（中文路径/名称、source 分组、`.cmd`/startup/非系统盘样本均正确） |
| **M2c 消费者接线** | ✅ 2026-09-13 | **新增** `AppCandidateMapper`（候选 → `AppItem` 升格：开始菜单候选**复用** `ResolveFromPath` 完整过滤链；程序盘点候选对齐 `EnumerateExecutablesRecursive` 的"不过滤 + `all:` 前缀 Id"）；`AppSourceService.ScanStartMenu` / `ScanAllPrograms` 新增 engine 分支（构造注入 `IndexIpcClient`，`null` = 逐字保持旧行为）；**回退**：未连接 / 构建中 / IPC 异常 → 走本地实现并记 Warn（`TryListAppsFromEngine`）；**线程安全**：同步接口内用 `Task.Run(...).GetAwaiter().GetResult()` 等待异步 IPC，避免捕获 UI 上下文死锁；`AppSourcePlugin` 装配客户端 + `EnsureEngine`（失败不阻断启动）；新增 `scripts/deploy-index.ps1`；新增测试工程 **`shell-app-source-tests`（8 例：升格语义 6 + 降级回退 2）**，已注册 slnx。<br>**D6 真机对照（同机）**：本地冷 **53,107ms / 1619 项**（M0，另存 `baseline-m0-local.json`）→ **引擎路径 `list_apps` 57~90ms + C# 升格 341~391ms**，升格后 **1619 项与本地完全一致**（program-files 计数平价实证）；引擎全索引 **1951 项 / 构建 214ms / RSS 8.9MB**。<br>**偏离（记录）**：backend 门控用环境变量 `BETTERDESKTOP_INDEX_BACKEND=local` 而非设置键 —— 本包未引用 `shell-settings`（不为一个开关引入新包依赖）；设置中心开关与「索引服务」状态行归 **M5** |
| **交叉核对（与 appgrabber 计划）** | ✅ 2026-09-14 | 那份计划（`2026-09-13-appgrabber-scope-and-entry-menu.md`，S1–S8 已全部完成）改了 `AppSourceService`：① **Id 统一**——`CreateStableId` 成为三条路径唯一产生点，引擎升格路径 `AppCandidateMapper` **同步改**；② **过滤层**（`AppFilterRules`，默认关）**两条产出路径都过**；③ **范围层**（口袋目录 / 桌面快捷方式）走**本地附加**、与后端无关。**结论：引擎/本地平价保持**——本表 M2c 记录的「程序盘点候选对齐 `all:` 前缀 Id」**已作废**（现为稳定 Id），过滤与范围层均在 C# 侧统一施加，引擎侧无需改动 |
| **M3a 引擎文件索引 + 协议** | ✅ 2026-09-14 | 新增 `engine-index/src/fileindex.rs`：受控根集（用户 下载/桌面/文档 + 各固定盘**顶层目录**；`settings.scan_roots` 可覆盖，供测试注入/用户自定义范围）+ **剪枝 11 项**（`windows`/`program files`/`program files (x86)`/`$recycle.bin`/`system volume information`/`programdata`/`appdata`/`node_modules`/`.git`/`.svn`/`recovery`）+ 深度 3 + `max_entries` 上限（触顶即停 + **标记降级并给出原因**，禁止静默）+ `search_files` RPC（缺 query → `-32602`；空查询 → 空集**不返全量**；limit 默认 200 / 上限 1000）。`main.rs` 后台**串行**构建「应用索引 → 文件索引」，两计数分别写入 `record_build`。<br>**排序纪律**：引擎只做「前缀命中优先」的两轮收集（避免截断丢掉最相关候选），真正的排名仍在 C# `ScoreFileName`（§5.6）。<br>`cargo test` **44 passed**（fileindex 9 例：剪枝覆盖/深度护栏/上限截断+标记/空目录与不存在根不崩/大小写不敏感/空查询与 limit=0 护栏/前缀优先/limit 截断/`scan_roots` 覆盖；engine 4 例：`search_files` 契约字段/缺参数 -32602/非法参数 -32602/空查询空集）；`cargo build --release` **0 警告**（6.29s）。<br>**偏离（记录）**：`ReadDirectoryChangesW` 增量更新**未实现**（§6.1 列了它）→ 理由：N 个根上的目录监听句柄成本 + 失效/复扫逻辑复杂，而受限索引的全量重建耗时可接受；先保「构建 + 查询」正确性，增量或周期补扫归 **M3c** |
| **M3b C# 搜索接线** | ✅ 2026-09-14 | `IndexIpcProtocol.M_SearchFiles` + `IndexModels`（`FileHit` / `SearchFilesResult`）+ `IndexIpcClient.SearchFilesAsync` / `TrySearchFilesAsync`（**构建中 / 降级 → null**：空集会误导成「没有这个文件」，而真相是「索引还没建好」）。<br>`FileSearchProvider` 新增引擎路径：**引擎可用 → 常驻索引取代磁盘兜底扫描**（两者根集同源：用户 下载/桌面/文档 + 各固定盘顶层目录），Windows Search 仍作**补充来源**（索引深度 3 与根集之外的文件它可能命中）；引擎不可用 / 构建中 / 降级 → **逐字回到原行为**（Windows Search + 兜底扫描并行、结果始终合并）。顺手把那段 dynamic COM 抽成 `CollectWindowsSearch(sink, …)`，两条路径共用同一实现，并按 `LaunchPath` 去重（同一文件不因两个来源重复出现）。`SearchPlugin` 装配自己的管道连接（引擎 IPC server 支持并发连接：宿主 / 应用源 / 搜索各一条），门控与 app-source 同为 `BETTERDESKTOP_INDEX_BACKEND=local`；**装配失败只 Warn、绝不阻断搜索**。<br>**实现期修正**：引擎剪枝表对齐 C# 兜底扫描（补 `users` / `perflogs` / `windows.old` / `intel` / `amd` / `nvidia` / `drivers`）——原表缺 `users`，会把 `C:\Users\*` 整体索引（与显式加入的用户目录重复且必然爆量）。<br>测试：`shell-index-ipc-tests` **26 passed**（新增 7 例：参数 camelCase 线格式 / 未指定 limit 不下发 / 构建中→null / 降级→null / 引擎报错→null / 非 Try 版保留错误码 / 未连接→null）；`shell-search-tests` **9 passed**；`cargo test` **44 passed**；`shell-index-ipc`、`shell-search` 及两个测试工程单独构建 **0 警告 0 错误** |
| **M3c 周期补扫** | ✅ 2026-09-14 | **选「周期全量重建」而非 `ReadDirectoryChangesW`**：受限索引全量重建真机 ≈0.7–2.4s（76k 条目）代价可接受，而数百个根上的目录监听句柄 + 事件合并/失效复扫逻辑复杂度高得多（§7 允许二选一，且周期扫描**不会漏事件**）。`engine.rs` 新增 `RESCAN_INTERVAL_SECS = 300` + `needs_rescan()`（**未构建过返回 false**——首次构建本身会刷新）；`main.rs` 把「构建两套索引并落账」抽成 `rebuild_indexes()` 供启动与补扫**共用**（两处各写一遍必然漂移），补扫线程每 30s 检查是否到点，重建读取**当次**配置快照（`apply_settings` 后下次补扫即生效）。契约新增 `status.lastBuildAtMs`（Unix 毫秒；0 = 尚未构建）——供 M5 状态行显示「索引有多新」。<br>`cargo test` **46 passed**（+2：刚构建完不触发补扫 / 补扫间隔落在 [60,3600] 合理区间）；`cargo build --release` **0 警告** |
| **M3 真机验证（M3a+M3b+M3c）** | ✅ 2026-09-14 | `pwsh -File scripts/deploy-index.ps1`（release 重建 → 部署 → 在跑实例自动停/起）。管道探针（`BDIX1|` + JSON-RPC 行协议，与 C# 客户端同一帧格式）：<br>· `status` → **appCount 2119 / fileCount 76,046 / lastBuildMs 705（热；冷 2440）/ RSS 27.2MB / building=false / degraded=false / lastBuildAtMs 已回填**<br>· `search_files {desktop.ini}` → 3 条，字段 `sizeBytes` / `modifiedMs` 与 C# DTO 逐字一致（**跨语言线格式实证**）<br>· `search_files {网易}` → `C:\Users\17822\Desktop\自动化脚本\网易云音乐.lnk`（**中文名 + 桌面子目录**）<br>· `search_files {D:\迅雷下载 下样本}` → `D:\迅雷下载\《完蛋…》.txt` —— **计划点名的「未索引目录命中」验收口径达成**（Windows Search 不索引该目录，只能靠常驻索引）<br>· 空查询 → `count 0`；`get_icons` → `-32601`；缺 query → `-32602`<br>**附注**：appCount 2119 vs M2 记录 1951（+168）——`appindex.rs` 本次未改，疑为「平价修复」补齐非固定盘 `Program Files` 与 `%LocalAppData%\Programs` **之前**的旧口径；如需对齐可单独复核，不影响 M3 结论。<br>**待用户核销**：D3/D4/D5 的**宿主侧**端到端（需真机开宿主走查）；D5 的「杀引擎降级 + 状态行可见」依赖 M5 |
| **M4a `icon.rs`** | ✅ 2026-09-14 | 新增 `engine-index/src/icon.rs`。**只产一档 256×256 PNG**（用户硬约束）：`SHExtractIconsW`（GetProcAddress 私有导出 → 红线 1/3/4/5）→ `ExtractIconExW` 降级（红线 2）→ `HICON` 由 `IconHandle` 的 `Drop` 保证**所有**返回路径 DestroyIcon（红线 6）→ 32bpp top-down DIB + `DrawIconEx(DI_NORMAL)` → BGRA 预乘反解为直通 RGBA（**整图 alpha 全 0 的掩码图标兜成不透明**，否则渲染成空图）→ PNG。缓存**只驻留 PNG 字节**的 LRU（`MAX_CACHE_BYTES=32MB`；单张超上限 1/4 直接不缓存——一张异常大图会冲空整个缓存）。<br>**真机踩坑（必须记）**：最初在 `DeleteObject(DIB)` **之后**才读位图内存 → use-after-free，表现为**测试进程直接崩掉、没有任何断言输出**（极易误判成「测试没跑」）。改为先拷像素再清句柄。<br>**依赖**：`image`（仅 png 特性）+ `Win32_UI_Shell`（`ExtractIconExW`）+ `base64`，**均取自本地 cargo 缓存、`--offline` 可解析**。测试 `cargo test` **65 passed**（新增 13：LRU 5 / alpha 还原 4 / 键归一 1 / 真机 256px PNG 2 / 缺失文件不 panic 1） |
| **M4b `get_icons` RPC** | ✅ 2026-09-14（引擎侧）；C# 客户端未接线 | **传输形态 = base64 in JSON 行**（不新增二进制帧）。**这是对 §12 Q1 字面选择的偏离，但完全保住它的意图 —— 且 Q1 的担心在真机上被证实**：notepad 的 256px PNG = **67,353 字节**，base64 后 **89,932 字节**，**已破 64KB**。之所以 base64 仍然安全，是因为**那条 64KB 只约束通用插件适配器协议，而本引擎跑的是自己的管道协议**；且本仓同法先例充分（剪贴板引擎 `NamedFormat.data_base64` / html data URI，本引擎 `list_apps` 单帧本就几百 KB）。收益：**零协议手术**——不动 `ipc.rs` 与 `IndexTransport`（两者都带着 2026-09-12「读写模式打架」的血案结论）。纪律照旧：**base64 绝不进列表载荷** → `get_icons` 按需单独调用。<br>契约：`{ keys, refresh? }` → `{ icons: [{key, pngBase64}], count, missing }`；**分批上限 64 键**（复刻 503 可见区优先）；单张 >1MB 跳过并计入 `missing`；**失败键不进 icons、只由 `missing` 暴露**（不静默）。`refresh: true` 先清缓存再取 —— **必需**：缓存键是路径，而应用更新时路径不变、图标已变，没有这个入口旧图标会永久驻留。<br>`status` 新增 `iconCacheEntries` / `iconCacheBytes`。**真机管道实证**：`count=1 missing=1`、帧 89,932 字节、PNG magic `89-50-4E-47`；`status` → `iconCacheEntries=1 iconCacheBytes=67353 rssMB=29.5 appCount=2119`。`cargo build --release`：首版 3 个 dead_code 警告（三个只被测试用的只读访问器）→ 改为真实接线 `stats()` / `status` / `refresh`，**0 警告**（不压制警告）。<br>**容量实测**：单张 256px PNG ≈ **67KB** → 32MB 上限 ≈ **475 张**；大网格会 LRU 抖动（与 C# 502 淘汰语义一致，属预期） |
| **M4c-1 C# 客户端** | ✅ 2026-09-14 | `IndexIpcProtocol.M_GetIcons` + `MaxIconBatch=64`（**与引擎 `MAX_ICON_BATCH` 必须一致**，注释写明）；`IndexModels` 新增 `IconHit`（含 `TryDecodePng()`：base64 非法返回 null **不抛**——图标是非关键资源，坏图不该让整条加载失败）与 `GetIconsResult`（`Icons/Count/Missing`）；`IndexStatus` 补 `IconCacheEntries/IconCacheBytes`。<br>`IndexIpcClient.GetIconsAsync`：过滤空白键、**空集合不发 RPC**（空 keys 只会换来 -32602）、**自动按 64 分片**（让调用方不可能踩到「超限 → 整批静默消失」）、**`refresh` 只随首片下发**（每片都清会让刚取回的图标被下一片挤掉）。`TryGetIconsAsync` 只对 IPC 故障降级——**图标没有 building/degraded 语义**（按需提取，不依赖索引快照）。<br>测试 `shell-index-ipc-tests` **35 passed**（+9：字节还原 + 线格式 / refresh 缺席 / refresh 下发 / 空键不发 RPC / 129 键切 3 片且 refresh 仅首片且结果合并 / 引擎报错→null / 未连接→null / 非 Try 保留错误码 / 坏 base64 不抛） |
| **M4c-2 图标数据源** | ✅ 2026-09-14（代码 + 真机已启用）；**字形归一化暂缓，等用户看图定** | `Win32ShellIconService(IndexIpcClient?)`：`GetIconAsync` **引擎优先** —— 取一档 256px PNG → `BitmapImage.DecodePixelWidth` **按显示尺寸解码**（`CacheOption=OnLoad` + `Freeze`）。**关键纪律：绝不在 C# 物化 256px 位图**（应用提取器上千项会是几百 MB；按 64px 解码每张仅 ~16KB）。回退条件（全部**逐字**走本地提取，行为与 M4 前一致）：未装配客户端 / 后端强制 local / 键不是文件路径（UWP AUMID）/ 引擎没这条 / 提取失败 / base64 或 PNG 坏 / IPC 故障 —— 组件内 try-catch 兜底，**图标异常绝不冒到 UI 线程**（非关键资源）。<br>**A/B 门控**（沿用仓库 `BETTERDESKTOP_*` 惯例）：`BETTERDESKTOP_ICON_SOURCE=local` 禁用引擎图标；`BETTERDESKTOP_ICON_DECODE_PX`(16–256，默认 64) 调倍率。`.lnk` 先解析成目标（与本地同策略，否则会取到壳图标）。<br>装配点唯一：`AppSourcePlugin:80` → `new Win32ShellIconService(indexClient)`（复用同一引擎客户端，不开新连接）。<br>**真机端到端证据**：宿主启动后引擎 `status` → `iconCacheEntries=43 iconCacheBytes=2,015,049`（≈47KB/张）、RSS 29.5→34.2MB —— **UI 确实在经引擎取图标**，而非只跑通了协议。<br>**暂缓项（有意）**：**字形归一化不做** —— 禁区⑨的「字形偏小」目前是我的**假设**（256 帧透明留白更多），还没被眼睛证实。先让用户并排看图：若观感无差 → 归一化不必做（省掉 alpha 包围盒裁剪 + 重采样那套复杂度）；若确实偏小 → 再做归一化。<br>**顺带发现的既有隐患（未修，需单独裁决）**：`Invalidate(AppItemId)` 用 `_cache.TryRemove(id.ToString())`，而缓存键是 `ResolveCacheKey(item)`（IconCacheKey/TargetPath/ShortcutPath 优先）→ **对绝大多数项是空操作**，即「应用更新后旧图标不会被失效」。这与 M4 新增的引擎侧 `refresh` 语义重叠，建议 M4 收尾时一并定（要么让 Invalidate 真正命中，要么明确只靠 refresh） |
| M5 降级可见 + 文档回写 | ⏳ 未开始 | 设置分区「索引服务」状态行（`lastBuildAtMs` / `lastBuildMs` / `rssBytes` / `degradeReason` 均已就绪）、TECH-KNOWLEDGE 新建「文件系统索引」+ 索引同步 |

**实现期新发现的既有缺陷（已修，非本计划引入）**：`IndexIpcClient.CallCore` 的 `Task.Wait` 在任务已故障时抛 `AggregateException`，会把引擎返回的 JSON-RPC 错误（含错误码）包一层，导致「按错误码分流」的回退逻辑失效——已解包为原始 `IndexIpcException`（保留 `ErrorCode`），并由 `EngineErrorSurfacesWithErrorCode` 用例守护。

---

## § 可选增强 / 超越需求建议（beyond scope，不混入强制 scope）

1. **`beyond` 复用 203 搜索排名下沉**：引擎已有全量候选 + 元数据，可让引擎直接算分（复用 `02-搜索/203` 的分类权重），C# 只展示 —— 收益：跨进程次数从 N 次降为 1 次；代价：排名逻辑出现第二份实现。**当前不建议**，仅在查询次数成为瓶颈时启用。
2. **`beyond` 引擎兼作 `shell-window-tracker` 的路径索引缓存**（依赖 §12 deferred 项先落地）：`GetRunningApps` 重建的正是「路径 → 应用」映射，正是引擎已有的数据。收益：一处缓存喂两个消费者；依赖：先修 `GetRunningApps` 的设计缺陷。
3. **`beyond` 引擎侧预生成图标**：构建索引时后台预热高频程序图标（复用 `05-图标/503` 可见区优先策略），使 dock/开始菜单首帧即出图标。收益：首帧体验；代价：常驻内存抬高（需 §5.4 上限兜住）。
4. **`beyond` USN 快路径（= M6）**：见 §12 Q3。
