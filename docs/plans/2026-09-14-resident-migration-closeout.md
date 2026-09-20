# 计划 · 常驻迁移收尾（B2 / B5 / B6）+ 可发现性补齐

> Task: 完成 `docs/2026-09-11-resident-architecture.md` 的剩余批次——把 `recent` / `calendar` / `notification` / `music` / `context-menu` 下沉常驻 Agent，并把 `host/cordis.yml` 拆成壳清单与 Agent 清单两份互斥清单（消除两进程重复加载）；同时补齐收尾期暴露的可发现性缺口（根目录可执行工程 README、计划索引）。
> 证据基于 2026-09-14 工作树（最新提交 `b805f6e`，另有约 274 项未提交改动；行号钉在该工作树）。
> 技术力文档：未命中"常驻进程清单拆分 / 跨进程能力下沉"主题（检索 `resident agent split`、`plugin manifest disjoint`）→ 本文新建；沿用既有先例 `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md`（引擎进程 + 客户端代理）与 `docs/plans/2026-09-13-native-index-service-rust.md`（常驻服务 + 回退可见）。
> 形式：full（架构改动类）。

## 1. Objective

用户可感知的结果：

- 关闭主程序（壳）后：最近项、日历数据、新装通知、音乐服务、系统右键菜单继续工作；重新打开壳时数据不丢、不重复初始化。
- 两进程插件清单无交集——同一插件不会被壳与 Agent 各加载一份。
- 新会话能在 `docs/plans/` 找到现行计划与状态，在根目录每个可执行工程找到其职责与构建方式。

## 2. Current Behaviour

| 事实 | 证据 |
|---|---|
| Agent 只常驻 5 个能力（status / app-source / pinning / search / convert），注释自述待迁移项 | `agent/agent.yml:10-12` |
| 壳清单仍是全量 20 条，其中 5 条与 `agent.yml` 重复 → 两进程各加载一份 | `host/cordis.yml:8-89` |
| 不存在 `host.yml`（全仓 0 命中）；`agent/Capabilities/` 仅 3 个文件（图标显隐 / 存在探测 / 独占门控） | 全仓检索 |
| 待下沉能力的 Inject 均为空数组，耦合低 | `packages/shell/shell-recent/RecentPlugin.cs:30`、`shell-calendar/CalendarPlugin.cs:35`、`shell-notification/NotificationPlugin.cs:32`、`shell-context-menu/ContextMenuPlugin.cs:40` |
| `shell-music` 尚无 `IPlugin` 实现（只有服务层） | `packages/shell/shell-music/`（无 `*Plugin.cs`） |
| 根目录可执行工程无 README：`BetterDesktop.Cli/`、`agent/`、`engine/`、`engine-index/`、`native/convert-engine/` | 各目录无 `README.md` |
| `package-readme` 门禁覆盖不到根目录工程（只扫 `packages/**/*.csproj`） | `scripts/verify-package-readme.ps1:27` |
| `docs/plans/` 有 15 份顶层计划 + 23 份归档，无总索引（仅剪贴板专项 `clipboard-docs-index.md`） | 目录清单 |

## 3. Relevant Architecture

- 归属判定与批次：`docs/2026-09-11-resident-architecture.md` 第二节 A/B 表 + 第五节批次表。
- 装配：壳与 Agent 各自 `LoaderService` + yml 清单；未注册 name fail-closed（`agent/agent.yml:13`）。
- 独占能力门控：`agent/Capabilities/HostPresenceWatcher.cs` + `ExclusiveCapabilityHost.cs`（探测失败保守按"壳在场"）。
- 共享设置：两进程各持 `SettingsService` 实例，落盘经 SaveMutex 互斥；禁止各自缓存配置。
- 分层判据：能力包经 `shell-core`/`shell-settings` 间接拖入 WPF 属已知事实；真正的约束是"是否创建窗口 / 是否触碰 `Application.Current`"。

## 4. Technical-Knowledge Findings

| 资产 | 与本计划的关系 |
|---|---|
| `docs/MECHANISMS.md` M17（原生与跨语言承载） | 跨进程能力承载的登记位；本批不新增机制 |
| `docs/2026-09-11-resident-architecture.md` | 归属清单与批次验收标准的唯一来源 |
| `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md` | 数据面/入口面/接线三分法的先例 |
| `docs/plans/2026-09-13-native-index-service-rust.md` | "常驻服务 + backend 开关 + 降级可见"的先例 |
| 未命中 | 清单互斥与能力下沉无现成文档 → 本批新建经验，完成后回写 resident-architecture |

## 5. Constraint Findings

| 约束 | 来源 | 规划含义 |
|---|---|---|
| 视觉插件不得进 `agent.yml` | `agent/agent.yml:6-7` | notification 的弹窗、日历面板、右键菜单弹窗留壳 |
| 独占能力不能被两进程同时持有 | resident-architecture 第三节 | 新增下沉项不得引入第二个独占项 |
| 跨进程服务访问不新建框架 | resident-architecture 第四节 | 按需文件/管道命令，或两边各起一份只读廉价数据 |
| 失败不得正常化 | `docs/runtime-health.md` | Agent 装配失败须显式记录 + 托盘可见 |
| 回退路径不得腐烂 | 剪贴板/索引先例 | 下沉后要么删除壳内实现，要么明确唯一权威；禁止两份事实并存 |
| 契约兼容 | `docs/extension-rules.md` | 下沉不改变既有服务契约与设置键语义 |

## 6. Proposed Changes

### 6.1 S1 · 清单拆分（B6，优先做，收益立竿见影）

- 新增 `host/host.yml`：只列视觉插件（dock / menu-bar / start-menu / quick-note / 自绘桌面窗口 / 缩略图与 Peek / 设置中心 UI / 任务栏外观 UI）。
- 壳装载路径改读 `host.yml`；`host/cordis.yml` 退役或保留为历史注释（不保留第二份清单）。
- `agent.yml` 继续只列能力插件。
- 验收：两清单 name 集合交集为空；壳与 Agent 同时运行时日志中同名插件各只出现一次。

### 6.2 S2 · B2 能力下沉

| 能力 | 下沉内容 | 留壳内容 | 前置 |
|---|---|---|---|
| `calendar` | `CalendarService`（农历 / 节气 / 节假日 / 天气） | 菜单栏日历面板 | 无（Inject 为空，最易做） |
| `notification` | `NotificationService`（新装检测 + 队列 + 已读） | `NewAppsNotificationWindow` 弹窗与占位图 | 弹窗需外观令牌 → 改为事件 + 壳订阅 |
| `recent` | `RecentItemsService` + JSON 持久化 | 无（纯数据服务） | 需确认"运行中应用"数据在 Agent 内的来源 |
| `music` | `KugouMusicApi` 与播放状态服务 | 无 | **先补 `IPlugin` 实现**（当前只有服务层） |

统一纪律：下沉后不在壳内保留第二份实现；壳需要时经 IPC/事件获取，或明确"该能力只存在于 Agent"。

### 6.3 S3 · B5 context-menu 常驻（+ 开始菜单 Win 键，可选）

- `context-menu`：注册表接管与常驻 STA COM 服务下沉 Agent；菜单弹窗留壳。
- 开始菜单 Win 键热键：钩子迁 Agent 以支持"壳退出后 Win 键仍开开始菜单"——需评估与开始菜单视觉的耦合，标记为可选子项。
- 若下沉后壳不在场时右键原生菜单仍可用（注册表路径 + CLI），则本项验收以"行为不变"为准。

### 6.4 S4 · 可发现性补齐

- 补 5 个根目录工程 README：`BetterDesktop.Cli/`、`agent/`、`engine/`、`engine-index/`、`native/convert-engine/`（职责 / 输入输出 / 构建与部署 / Known Limitations）。
- 新增 `docs/plans/README.md`：计划索引（时间序 + 状态：现行 / 已收口 / 已归档），登记全部顶层计划与本批新增。
- 复核 `docs/audits/`、`docs/analysis/` 是否需要用途说明与索引行。

## 7. Implementation Sequence

| 步 | 内容 | 验证 |
|---|---|---|
| S1 | 拆 `host.yml` + 改壳装载路径 | 两清单交集为空；同时运行无重复加载 |
| S2-a | `calendar` 下沉 | 壳退出后日历数据仍可查；壳重启数据不重复初始化 |
| S2-b | `notification` 下沉（数据与弹窗拆分） | 壳在场仍弹窗；壳退出时新装记录不丢 |
| S2-c | `recent` 下沉 | 最近程序/文档在壳退出后仍更新 |
| S2-d | `music` 补 IPlugin 并下沉 | 音乐服务可独立于壳工作 |
| S3 | `context-menu` 常驻（Win 键可选） | 壳退出后右键菜单行为不变 |
| S4 | 根目录 README + 计划索引 | 门禁全绿；导航可达 |

## 8. Test Strategy

- 单测：各能力包既有测试保持绿；下沉后为"壳内不保留第二份实现"补断言或架构测试。
- 集成：Agent `--selftest` 输出插件集与 `agent.yml` 一致；壳/Agent 同时运行日志核对；杀 Agent 后壳功能降级可见不崩。
- 端到端：壳退出 → 各能力仍工作 → 壳重启 → 无重复初始化且数据连续。
- 门禁：`pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1` 全绿（本批已把文档门禁与注册表门禁修绿，勿引入新红）。

## 9. Risk and Impact Analysis

| 风险 | 级别 | 缓解 |
|---|---|---|
| 重复加载未彻底消除 | 中 | 清单交集可加机检（见 §12 Q4） |
| 下沉后壳内残留第二份实现 | 高 | 每项下沉收尾时删除壳内实现并回归测试 |
| `notification` 依赖外观服务 | 中 | 改事件 + 壳订阅；壳不在场时静默入队并计数 |
| `recent` 依赖运行中应用数据 | 中 | 先决定 window-tracker 追踪能力是否下沉，或 recent 在 Agent 内自采前台 |
| 壳/Agent 双写设置 | 中 | 沿用 SaveMutex；Agent 以只读为主，写仅经壳 |
| 下沉导致能力在壳内不可用 | 中 | 跨进程获取失败时的降级路径必须显式且可见 |

## 10. Files Expected to Change

| File | Reason |
|---|---|
| `host/host.yml`（新增）、`host/Bootstrap.cs`、`host/cordis.yml` | S1 清单拆分 |
| `agent/agent.yml`、`agent/Program.cs` | 注册新下沉插件的工厂 |
| `agent/Capabilities/*` | 视需要新增能力承载文件 |
| `packages/shell/shell-calendar/*`、`shell-notification/*`、`shell-recent/*`、`shell-music/*` | S2 下沉与拆窗 |
| `packages/shell/shell-context-menu/*` | S3 常驻化 |
| `BetterDesktop.Cli/README.md`、`agent/README.md`、`engine/README.md`、`engine-index/README.md`、`native/convert-engine/README.md` | S4 |
| `docs/plans/README.md`（新增） | S4 计划索引 |
| `docs/2026-09-11-resident-architecture.md` | 批次表状态回写 |

## 11. Reusable Implementation Context

- 常驻装配先例：`agent/agent.yml`、`agent/Program.cs`、`agent/AgentLog.cs`。
- 独占门控先例：`agent/Capabilities/HostPresenceWatcher.cs`、`ExclusiveCapabilityHost.cs`、`DesktopIconsCapability.cs`。
- 跨进程客户端先例：`packages/shell/shell-clipboard-ipc/`、`packages/shell/shell-index-ipc/`。
- 托盘控制面：`tray/TrayApplicationContext.cs`、`tray/ProcessBridge.cs`（经 CLI 契约驱动）。
- 环境事实：Rust 工具链在 `%USERPROFILE%\.cargo\bin\`（不在 PATH，需绝对路径）。

## 12. Assumptions and Open Questions

1. `notification` 在壳不在场时的呈现方式：仅入队记录，还是经托盘气泡提示？
2. `recent` 的"运行中应用"在 Agent 内如何取得：随 window-tracker 追踪能力下沉，还是 Agent 自采前台窗口？
3. `context-menu` 下沉后菜单弹窗仍在壳内，壳退出时右键只走原生扩展路径——是否可接受？
4. 是否需要为"两清单无交集"新增门禁（如 `verify-manifest-disjoint.ps1`，须登记 + 单测）？
5. `shell-music` 补齐 `IPlugin` 时，播放控制是否也一并下沉（涉及 `IMediaPlaybackService` 契约的消费方）。

## 13. DoD 核销表

| # | 项 | 状态 |
|---|---|---|
| D1 | `host.yml` 与 `agent.yml` 无交集，壳与 Agent 同时运行无重复加载 | ⬜ |
| D2 | `calendar` 下沉且壳退出后数据可用 | ⬜ |
| D3 | `notification` 下沉且弹窗行为不变 | ⬜ |
| D4 | `recent` 下沉且持续更新 | ⬜ |
| D5 | `music` 有 `IPlugin` 且可独立工作 | ⬜ |
| D6 | `context-menu` 常驻化后右键行为不变 | ⬜ |
| D7 | 5 个根目录工程 README 补齐 | ⬜ |
| D8 | `docs/plans/README.md` 索引登记全部顶层计划 | ⬜ |
| D9 | 全量门禁绿（含文档门禁与注册表门禁） | ⬜ |
| D10 | resident-architecture 批次表状态回写 | ⬜ |

## 14. 交接节

**注入清单（实现期直接读取，不再调研）**：

- 归属与验收标准：`docs/2026-09-11-resident-architecture.md`（§2 A/B 表、§5 批次表与独占门控说明）。
- 装配与工厂注册：`agent/Program.cs`、`agent/agent.yml`、`host/Bootstrap.cs`、`host/cordis.yml`。
- 独占门控模板：`agent/Capabilities/HostPresenceWatcher.cs`、`ExclusiveCapabilityHost.cs`。
- 跨进程客户端模板：`packages/shell/shell-clipboard-ipc/`（`IpcProtocol` / `*Client` / `*Launcher`）。
- 待下沉插件入口：`packages/shell/shell-{calendar,notification,recent,music,context-menu}/*Plugin.cs`。
- 门禁与文档纪律：`scripts/AGENTS.md`、`docs/doc-standards.md`、`.agents/notes/README.md`。

**模式判定**：本批属"架构改动 + 能力迁移"，按 `docs/plans/` 既有 full 形态执行；S4 为纯文档补写，可独立先行。

**适配参数**：

- 清单文件：壳侧新增 `host/host.yml`；Agent 侧沿用 `agent/agent.yml`。
- 后端/降级开关：沿用 `extensions.*.backend` 命名惯例（如需要）。
- 门禁：新增门禁必须同一变更登记 `scripts/run-gates.ps1` 并补 `verify-*.Tests.ps1`（否则 `gate-registry` 红）。

**DoD 核销表**：见 §13，每项完成后就地回写状态与证据（实测日志/命令）。
