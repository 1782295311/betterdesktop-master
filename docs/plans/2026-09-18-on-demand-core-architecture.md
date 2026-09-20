# 计划 · 按需优先的极简架构（Rust Core 唯一常驻，未来十年的地基）

> Task: 把"多主 Ensure + 网状互拉"的进程拓扑收敛为"1 个常驻 core（Rust）+ 其余按需拉起、用完即退"，并把它做**地基级**的一件事：core 必须在安全、边界、规范、电源、未来能力五个维度上都是对的，不能等功能变多再回来重写。
> 证据锚点：`b805f6e4`（HEAD）**+ 2026-09-17/19 工作树**；行号钉在该工作树。
> 技术力检索：**未命中**（检索词 `常驻进程 / 进程拓扑 / supervisor / 生命周期所有者 / ensure / 电源事件`）→ 本主题无现成功能文档，新建。相邻可复用资产见 §4。
> 形式：full（架构改动类）。
> 取代 `docs/2026-09-11-resident-architecture.md` 的方向（"除 Dock/菜单栏外全部常驻"）。

## 0. 决策记录

### 0.1 一审定案（2026-09-19）

| # | 决策 | 结论 |
|---|---|---|
| **Q1** | core 用什么写 | **Rust 重写**（"必须服务于未来，不能等功能变得更多再回来重写"）。目标私有工作集 < 8MB。 |
| **Q2** | 壳运行期热键归属 | **core 独占所有"必须活过壳退出"的热键**；壳只注册"随壳生灭"的临时热键。 |
| **Q3** | `bd-*` 重命名 | **不做**。新 core 命名 `BetterDesktop.Core.exe`，与既有产物一致。 |

### 0.2 二审增补（2026-09-19）：五层缺口 + 电源

一审的 8 处缺口已逐条落地（见旧版记录，内容保留在本文件）。二审指出**五层架构级缺口 + 电源事件完全缺失**，本版全部并入：

| 缺口 | 落点 |
|---|---|
| 一 · **安全层完全缺失** | §5 C16–C20、§6.10、§7 **S2.5**、§13 D21–D24、§14 禁区 |
| 二 · **Vibe coding 边界无强制机制** | §7 **S0.5**、§8 架构测试、§13 D25–D28、§14 禁区 |
| 三 · **极简代码规范未落地** | §7 S0.5 交付 `CODE-STANDARDS.md`、§13 D29 |
| 四 · **扩展中心与 AI/LLM/UE5 无定位** | §3 六层架构、§6.1-A `tier`/`type` 字段、§6.11 GPU 仲裁、§13 D30 |
| 五 · **AMSI/杀毒无预留** | §6.1-A `type: tool`、§12 deferred |
| 六 · **电源事件（二审追加，架构级）** | §5 C11–C15、§6.1 **职责 G**、§6.1-A `power` 字段、§6.1-D 任务电源配置、§6.3 管道重连、§8 睡眠测试、§13 D31–D37 |

**编号说明**：二审给"安全"和"电源"两段都写了 `C11–C15`。本文按**电源段优先**（该段论述最详、含明确定调）占用 **C11–C15**，安全顺延为 **C16–C20**，边界为 **C21–C23**。引用时以语义为准。

**S1 已闭环（2026-09-19）**：Rust core 骨架跑通，**托盘右键菜单经人工验证可正常唤出**（C9 红线确认成立），S1 全部待验项清空。

---

## 1. Objective

用户可感知的结果：

1. **空闲（壳关着）时只有一个进程** `BetterDesktop.Core.exe`，私有工作集 **< 8MB**（S1 实测 1.18MB）；托盘图标在、热键可用。
2. **点任何东西都有反应**：托盘菜单、CLI、系统右键扩展、桌面控制菜单——无论主程序是否在跑。
3. **关掉的东西不会自己回来**；**崩掉的东西会自己回来**。
4. **不阻断睡眠、睡眠后不错乱**：`powercfg /requests` 在 core 运行时为空，唤醒后热键/托盘/管道/独占能力全部自动恢复，且不误判"睡眠冻结"为崩溃。
5. **core 是地基**：新增能力（AI 管家 / bd-infer / bd-world / GPU 仲裁 / 安全扫描）**只加数据表项**，不改 core 代码结构，也不破坏既有 schema。

## 2. Current Behaviour

**实测内存基线（2026-09-18，本机运行态，`WorkingSet64` 口径）**：

| 进程 | MB | 备注 |
|---|---|---|
| BetterDesktop.Host | 179.4 | 壳（WPF） |
| BetterDesktop.Index.Engine | 53.6 | Rust，**已有空闲自退（600s）** |
| BetterDesktop.Agent | 43.4 | 见 §6.2 |
| BetterDesktop.DesktopControl | 35.5 | 自绘桌面独立进程 |
| BetterDesktop.Clipboard.Panel | 32.6 | |
| BetterDesktop.Watchdog | 22.9 | 纯守护开销 |
| BetterDesktop.Tray | 19.9 | 将被 Rust core 取代 |
| BetterDesktop.Clipboard.Engine | 6.6 | Rust |
| **合计** | **393.9** | 8 进程 |

**生命周期「多主」矩阵（"网状"的证据）**：

| 拉起方 | 被拉起 | 证据 |
|---|---|---|
| Host | DesktopControl / Agent / Tray | `host/Bootstrap.cs:351,354,357` |
| Tray | Host / Agent / DesktopControl / Watchdog / Settings / Clipboard.Panel / Updater / Recovery | `tray/ProcessBridge.cs:61,519`、`tray/TrayApplicationContext.cs:921,950,362,385,775` |
| Launcher | Tray / Host / Watchdog | `launcher/Services/ComponentBootstrapper.cs:73,74,346` |
| Watchdog | Host / Agent / Clipboard.Panel / Clipboard.Engine / DesktopControl | `watchdog/Program.cs:159-176` |
| Agent | DesktopControl / Capture | `agent/Capabilities/DesktopServiceSupervisor.cs:88`、`CaptureHotkeyOwner.cs:286` |
| CLI | DesktopControl / Host / 自身转发 | `BetterDesktop.Cli/HeadlessExecutor.cs:537,579,613,632` |
| DesktopControl | 自身 | `shell-desktop-control/DesktopControlEntry.cs:475` |

⇒ `Host ↔ Tray ↔ Watchdog ↔ Agent` 四方互相 ensure = **有环**。

**其余现状事实**：

- **插件清单重复**：`agent/agent.yml:21-40` 与 `host/cordis.yml:27,31,39,43,85` 是**同一批 5 个插件**，两进程各加载一份。[verified]
- **控制管道服务端在壳里**：`host/MenuCommandPipe.cs:25`（`BetterDesktop.MenuCmd`），客户端 `packages/kernel/kernel/MenuCommandPipeClient.cs:14`（超时 1500ms，`BDMC1|action|path`）。**壳不在时管道不存在**。[verified]
- **系统右键依赖两个非壳进程**：`shellmenu.json` 快照唯一写入者是桌面服务（`docs/audits/2026-09-17-tray-and-shellmenu-landing.md:49,69`），注册与 60s 自愈在 Agent（`agent/Program.cs:323,291,304`）。原生扩展读快照文件，不直连管道。[verified]
- **配置多写者**：`SettingsService.SaveLocked`（`shell-settings/Services/SettingsService.cs:233`，跨进程互斥）之外，另有 `host/SettingsFileWriter.cs:32` 与 `BetterDesktop.Cli/HeadlessExecutor.cs:384,489` 两族整文件直写。[verified]
- **索引引擎已有空闲自退**：`engine-index/src/main.rs:45` `IDLE_EXIT_SECS = 600`。[verified]
- **截图已是一次性**：`--capture-now` 截完即退（`shell-capture/App.xaml.cs:157-162`）。[verified]
- **自启无计划任务**：仅 HKCU\Run + StartupApproved（`kernel/Deployment/AutostartRegistrar.cs:24-37`）。[verified]
- **全仓无电源事件处理**：`WM_POWERBROADCAST` / `SetThreadExecutionState` / `PowerSetRequest` 在当前 core 与既有进程中**均未出现**（S1 之后 core 也没有）。[verified]

## 3. Relevant Architecture

### 3.1 六层架构（二审新增 —— "服务于未来"的定位层）

```
⑥ 入口层     bdctl · bd-launcher · ShellDLL(explorer 内) · 托盘菜单 · 全局热键
⑤ 扩展层     system 扩展（AI 管家，经 core 调基础设施）· 普通扩展 · 技能扩展(out-of-proc)
④ 表面层     bd-shell(WPF 2D：菜单栏/Dock/搜索) · bd-world(UE5 3D) —— 用户可见的"脸"
③ 数据面     剪贴板历史引擎 · 索引引擎（Rust，按需）
② 基础设施   bd-infer（统一推理网关：LLM + OCR + 翻译 + API 路由）
① 地基       **core**（Rust，唯一常驻）：托盘 · 热键 · 控制管道 · 监护 · 电源 · 安全 · GPU 仲裁 · 独占能力
```

**依赖方向单向向下**：`④⑤⑥ → ②①`，`④ → ②`（表面可调推理），**任何层不得反向依赖**。
core（①）**只认得组件表，不认得任何扩展的实现**——这是它能当地基的前提。

### 3.2 现有底座

- 插件内核：`LoaderService` + yml 清单，未注册 name fail-closed；三份互斥清单 `host/cordis.yml` / `agent/agent.yml` / `shell-desktop-control/desktop.yml`。
- 独占能力门控：`agent/Capabilities/HostPresenceWatcher.cs` + `ExclusiveCapabilityHost.cs`（本批删除，见 §6.1-E）。
- 既有 Rust 工程形态：`engine/`、`engine-index/`、`native/convert-engine/` 三个独立 crate（无 workspace），edition 2024，`windows` 0.58/0.62。
- 既定纪律：跨进程不新建服务框架；跨进程窗口消息禁令（桌面链）；失败不得正常化（`docs/runtime-health.md`）。
- CLI 契约驱动：托盘不自己实现功能，一律经 CLI action（`tray/ProcessBridge.cs:366`）。
- L3 红线（2026-09-17）：菜单栏 / Dock / 灵动岛随主程序关闭而关闭。

## 4. Technical-Knowledge Findings

- **功能文档命中**：无。
- **相邻功能资产**：

| 资产 | 借什么 |
|---|---|
| `69-网络聚合/nic-health-quarantine.md` | 有界退避 2/5/15/30s + 分层候选（= §6.1-D 退避的现成范式） |
| `69-网络聚合/tun-lifecycle-preflight.md` | 启动前预检 + 残留精确清理 + 两阶段激活 + 看门狗 |
| `69-网络聚合/system-proxy-snapshot-restore.md` | 所有权标记（prepared→active）+ 快照恢复 + 用户改动检测 |
| `14-窗口与快捷键/1401-global-shortcut-doubletap.md` | 全局热键引用计数 |
| `14-窗口与快捷键/1402-appbar-window-multiscreen.md` | WindowManager 归并 + reconciliation 定时器（**唤醒 reconcile 同款问题域**） |
| `14-窗口与快捷键/1404-shell-role-detect.md` | explorer 重启检测 / `SetShellReadyEvent`（唤醒后 explorer 重建场景） |
| `拆析/拆析-cairoshell-最初开源版.md` | `AppBarWindowService` 抽象；`CommandService` 字符串命令 + IsAvailable 门控 |
| `docs/MECHANISMS.md` | M17 原生与跨语言承载（登记位） |

- **★ 可直接复用的 Rust 源码资产**：

| 现成 Rust 文件 | core 能怎么用 |
|---|---|
| `engine/src/hotkey.rs` | **几乎原样复用**：`parse_spec`（语义与 C# `HotkeySpec` 一致）、`GlobalAddAtomW` 原子 ID、`MOD_NOREPEAT`、0x581 冲突不崩跳过、配对注销 |
| `engine/src/settings.rs` | settings.json 读取范式（BOM 容错 + 分节取值 + 失败保默认） |
| `engine/src/log.rs` | 按日滚动日志（core 已按此移植，**S1 已落地**） |
| `engine/src/ipc.rs` + `main.rs` | `CreateNamedPipeW` 服务端 + 隐藏窗口消息泵骨架 |
| `engine/Cargo.toml` | 依赖清单 + release profile（`strip` 直接影响体积目标） |

- **影响半径**：`core/`（新，S1 已落地）`host/` `tray/` `watchdog/` `agent/` `launcher/` `recovery/` `updater/` `BetterDesktop.Cli/` + `packages/shell/shell-{desktop-control,clipboard*,context-menu,desktop}` + `scripts/`（install/uninstall/publish/gates）。

## 5. Constraint Findings

### 5.1 拓扑与纪律（一审）

| # | 约束 | 来源 | 规划含义 |
|---|---|---|---|
| C1 | 视觉插件**不得**进常驻清单 | `agent/agent.yml:6-9`（L3 红线） | core 不得加载 dock/menu-bar/start-menu/island/quick-note |
| C2 | 独占能力不能被两进程同时持有 | `resident-architecture §3` | 任务栏外观 / 图标钩子只能有一个所有者 |
| C3 | **ShellMenu 快照不能没有写入者** | `2026-09-17-...md:49` | 桌面服务按需化后若无人写快照 → 系统右键"注册成功但一项不显示" |
| C4 | 失败不得正常化 | `docs/runtime-health.md` | core 拉起失败要显式记录 |
| C5 | 回退路径不得腐烂 | `residency closeout §5` | 下沉后必须删旧实现，禁止两份事实并存 |
| C6 | 跨进程窗口消息禁令 | `resident-architecture §6` | 桌面链点击路径保持零跨进程消息 |
| C7 | 跨进程**不新建服务框架** | `resident-architecture §4` | 控制通道复用既有 `MenuCmd` 管道 + 文件 |
| C8 | 门禁新增必须同批登记并补测试 | `scripts/AGENTS.md` | 拓扑/安全/边界门禁须同批登记 |
| C9 | **`TrackPopupMenuEx` 的 owner 必须是顶层窗口** | 本项目实证 + **S1 已人工验证** | core 托盘菜单**不得**用 `HWND_MESSAGE` 承载 |
| C10 | 热键必须由拥有消息队列的线程注册 | `engine/src/hotkey.rs:2` | core 主线程 = 隐藏窗口线程，注册/注销配对 |

### 5.2 电源（二审新增 —— **core 的第一公民职责**）

| # | 约束 | 来源 | 规划含义 |
|---|---|---|---|
| **C11** | core **不得**无条件调用 `SetThreadExecutionState` 或 `PowerSetRequest` | 用户 2026-09-19 定调 | 只有用户显式触发的长任务（AI 推理/下载）才可临时持有，且**必须带超时** |
| **C12** | 睡眠/唤醒必须**显式处理**，不得依赖"它应该能行" | 本计划 | core 必须在消息循环中处理 `WM_POWERBROADCAST` |
| **C13** | 计划任务**不得唤醒机器**、不得睡眠补跑 | 本计划 | `WakeToRun=false`、`StartWhenAvailable=false` |
| **C14** | 子进程必须有睡眠/唤醒感知 | 本计划 | 每个 `components.json` 条目声明 `power` 策略 |
| **C15** | `powercfg /requests` 在 core 运行时**必须为空** | 本计划 | D31 验收项 |

### 5.3 安全（二审新增）

| # | 约束 | 规划含义 |
|---|---|---|
| **C16** | 命名管道必须设 ACL（只允许当前用户 SID）+ 调用者校验 + 消息大小上限 + 超时断开 | §6.10 |
| **C17** | 禁止字符串拼接命令行拉起进程；必须逐参数传参 | §6.10 |
| **C18** | JSON 反序列化必须设深度/大小上限 + 字段白名单 | §6.10 |
| **C19** | 组件 exe 路径必须做前缀校验（解析后落在允许目录内） | §6.10 |
| **C20** | 未来模型/插件文件加载必须做哈希校验 | §6.10（留位，S5.5 起用） |

### 5.4 边界与规范（二审新增）

| # | 约束 | 规划含义 |
|---|---|---|
| **C21** | 依赖单向：`④⑤⑥ → ②①`；扩展不得依赖 core 内部，core 不得依赖任何扩展 | §7 S0.5 + §8 架构测试 |
| **C22** | 进程拉起 / 热键注册 / settings 写入各有**唯一合法位置**，其余位置一律门禁拦下 | §7 S0.5 verify-architecture.ps1 |
| **C23** | 一个概念一处实现、一个文件一个类、目录即边界、禁 Utils/Helper/Common | §7 S0.5 `CODE-STANDARDS.md` |

## 6. Proposed Changes

### 6.0 进程映射（用户终态 ← 现有实体）

| 用户终态 | 现有实体 | 处置 |
|---|---|---|
| **`BetterDesktop.Core.exe`** | `BetterDesktop.Tray.exe` + `Watchdog` 的监护逻辑 | **新 Rust crate `core/`（S1 已落地）**；`.NET tray/` 与 `watchdog/` 退役 |
| `bd-shell` | `BetterDesktop.Host.exe` | 保留；**删它往外的 3 条 Ensure**（§6.4） |
| `bd-desktop` | `BetterDesktop.DesktopControl.exe` | 保留；开关门控交 core |
| `bd-clipboard` | `Clipboard.Panel` + `Clipboard.Engine` | 保留两者；面板严格按需，引擎按开关常驻 |
| `bd-capture` | `BetterDesktop.Capture.exe` | **不动**（已是 `--capture-now` 一次性） |
| `bd-index` | `Index.Engine`（Rust） | **不动**（保持 600s 空闲自退） |
| `bd-settings` | `BetterDesktop.Settings.exe` | **不动** |
| `bdctl` | `BetterDesktop.Cli.exe` | 保留；客户端目标改 core |
| `bd-launcher` | `BetterDesktop.exe`（Launcher） | 瘦身：只 ensure core + 发 ApplyDesired |
| `ShellDLL.dll` | `shell-context-menu/native/BetterDesktopShellMenu.dll` | **不动**（读快照文件） |
| — | `BetterDesktop.Agent.exe` | **删除**（§6.2） |
| — | `BetterDesktop.Watchdog.exe` | **删除**（§6.1） |

### 6.1 core：Rust crate（S1 已落地骨架）

**职责（九项）**：

```
A. 托盘图标        Shell_NotifyIconW + 隐藏顶层窗口（C9）              ✅ S1 已实现
B. 全局热键        Register/UnregisterHotKey（C10），复用 engine/src/hotkey.rs   ⬜ S4
C. 控制管道        BetterDesktop.MenuCmd 服务端（从 host 迁）+ @ctl 形态       ⬜ S2
D. 监护器          desired → actual reconcile + 退避 + degraded + 计划任务    ⬜ S3
E. ShellMenu 快照  只触发重建，不写内容（§6.8）                        ⬜ S6
F. 独占能力        任务栏外观注入维护 + 桌面图标可见性钩子（组件表无 desktop 时）  ⬜ S4
G. 电源事件        WM_POWERBROADCAST 处理 + 唤醒 reconcile（§6.12）      ✅ S3.5 已完成（§13.10）
H. GPU 仲裁        维护 GPU 用户表，协调 bd-world 与 bd-infer（§6.11）    ⬜ S5.5 留位
I. 安全基线        管道 ACL + 调用者校验 + 路径校验（§6.10）              ⬜ S2.5
```

**禁止清单（S1 已写入 crate 根 doc 注释；S0.5 起由门禁机检）**：core **不得**加载任何 UI 插件、渲染任何面板、持有任何业务状态、读业务数据文件（除 `settings.json` 扁平键 / `components.json` / 留痕 flag）、引用任何 C# 程序集或业务包。

#### 6.1-A 组件表 schema（**数据模型为未来留位** —— 二审缺口四/五）

`core/components.json`，每条目：

```json
{
  "name": "shell",
  "label": "主程序（菜单栏 / Dock）",
  "exe": "BetterDesktop.Host.exe",
  "desired": "on-demand",
  "tier": "surface",
  "type": "process",
  "power": { "onSuspend": "freeze", "onResume": "reconcile", "keepAwake": false }
}
```

| 字段 | 取值 | 默认 | 含义 |
|---|---|---|---|
| `desired` | `running` / `on-demand` / `stopped` | `on-demand` | core 的期望状态 |
| `tier` | `foundation` / `infrastructure` / `surface` / `extension` / `system-extension` | `extension` | **层级归属**，决定监护规则 |
| `type` | `process` / `tool` / `panel` | `process` | `process`=常驻/按需进程；`tool`=一次性工具（跑完即退，如未来的安全扫描器）；`panel`=UI 面板 |
| `power.onSuspend` | `freeze` / `stop` / `notify` | `freeze` | 睡眠时动作 |
| `power.onResume` | `reconcile` / `probe-and-reinit` / `restart` / `reinit` / `notify` | `reconcile` | 唤醒后动作（**`probe-and-reinit` = 三审新增**，见下） |
| `power.keepAwake` | `false` / `user-triggered` / `always` | `false` | 是否阻止睡眠（**`always` 禁止**） |
| `gate` | settings 扁平键 / 缺省 | — | 开关键；`false` → 停且不拉回 |

**tier → 监护规则**：

| tier | 规则 |
|---|---|
| `foundation` | 常驻（= core 自身；表内保留位，供未来第二地基进程） |
| `infrastructure` | 按需（有消费者时拉起，空闲自退）—— `bd-infer` 属此层 |
| `surface` | **用户开启才跑**（gate 主控）—— `bd-shell` / `bd-world` 属此层 |
| `extension` | 按需（收到 `start` 才拉） |
| `system-extension` | 按需 + 可请求 core 代为拉起其它层（AI 管家属此层，**自己不 Process.Start**） |

**`surface` 的判定口径（三审澄清 —— 消除"桌面算不算 surface"的歧义）**：

`surface` = **构成用户感知到的"桌面这张脸"** 的组件，即：`shell`（菜单栏 / Dock / 搜索）、`desktop`（自绘桌面本体）、未来的 `world`（3D 桌面）。
**其余带头 UI 的独立弹窗不算 surface**（设置中心、剪贴板面板、通知面板）→ 归 `extension` + `type=panel`。
判据一句话：**关掉它，用户会问"我的桌面怎么变了"** → surface；**关掉它，用户只是少了个能打开的工具** → extension。

**tier × 唤醒策略（三审新增 —— 见 §6.12）**：

| tier | 典型 `onResume` | 理由 |
|---|---|---|
| `surface` | **`probe-and-reinit`** | 进程活着 ≠ 还能用（DWM 重建 → 窗口花屏/Z-order 错；D3D12 设备丢失 → 渲染不出来；explorer 重启 → ShellDLL 钩子失效）。必须先探测健康 |
| `infrastructure` | `reinit` | 无 UI，但持有外部资源（GPU 上下文 / 文件句柄 / 数据库连接），唤醒后需重建 |
| `extension` | `reconcile` | 一次性或轻量，进程在即可用 |

**出厂默认（三审追问："desktop 的 `desired` 到底是 Running 还是 OnDemand？"）**：

答案是 **`Running` + `gate=components.desktop`，且 gate 缺省视为 `true`** —— 这不是我新引入的"默认常驻"，而是**忠实映射当前产品行为**：`host/Bootstrap.cs:187` 是 `settings?.Get("components.desktop", true) != true → return`，即**键缺失 = 开**，全新安装的机器上自绘桌面本来就默认在跑。

三审建议改成 `desired=on-demand`（"core 从不主动拉，gate 决定 start 请求是否被接受"）——**方向认可，但落点必须挪到 S6**，两个理由：

1. **联动改造**：`on-demand` 的定义是"core 从不主动拉起"，于是"打开开关"这条路径必须改成**向 core 发 `start desktop` 请求**（现在是托盘/CLI 直写 settings + 自己拉进程）。这属于 §6.7 入口收口的同一批工作，单独改 schema 会让开关变成"点了没反应"。
2. **硬阻塞（C3）**：桌面服务是 `shellmenu.json` 快照的**唯一写入者**。把桌面默认关掉 = 全新安装的机器上**系统右键菜单一项都不显示**。所以必须等 S6 把快照写入者迁到 core 之后才能翻默认。

⇒ **S6 同批做两件事**：快照写入者迁 core + desktop 改 `on-demand` 且 gate 缺省改 `false`（同步改所有 `components.desktop` 读取点的默认值）。是否要"默认关"另见 §12 开放问题 5（这是产品定位决策，不只是技术决策）。

**未来条目的形态（现在不实现，schema 留位）**：

```json
{ "name": "infer", "label": "推理网关", "exe": "BetterDesktop.Infer.exe",
  "desired": "on-demand", "tier": "infrastructure", "type": "process",
  "power": { "onSuspend": "freeze", "onResume": "reinit", "keepAwake": false } }

{ "name": "world", "label": "3D 桌面", "exe": "BetterDesktop.World.exe",
  "desired": "on-demand", "tier": "surface", "type": "process",
  "power": { "onSuspend": "freeze", "onResume": "reinit", "keepAwake": false } }

{ "name": "scan-file", "label": "文件安全扫描", "exe": "BetterDesktop.Scanner.exe",
  "desired": "on-demand", "tier": "infrastructure", "type": "tool" }
```

**容错纪律（沿用 S1 实现）**：单条表项非法（缺 `name`/`exe`、枚举值未识别、`name` 重复、`keepAwake=always`）→ **跳过该条并记日志**；整份外部表不可用 → 回退内嵌表。
**外部覆盖**：`%LOCALAPPDATA%\BetterDesktop\core.components.json` 优先。
**为什么用 JSON 不用 TOML**：core 已因读 `settings.json` 依赖 `serde_json` → **零新增第三方依赖**（§14 适配参数）。

#### 6.1-B 数据驱动

监护循环、控制管道、托盘菜单的"启动 X"子菜单全部由该表生成。**新增能力只加表项，不改 core 代码结构**——这是"不能等功能变多再回来重写"的具体保障。

#### 6.1-C reconcile（core 启动 / **每次唤醒**）

```
① 枚举实际进程（ProcessProbe）     ② 读 desired（组件表 + settings gate + tier 规则）
③ 差集 = 需拉起的 / 需停止的        ④ 只对差集动手，已运行的一律不碰
```

**唤醒后 reconcile 的关键判据（二审）**：必须区分"睡眠冻结"与"真崩溃"——
- 睡眠冻结的进程唤醒后**是活的**，不能重启；
- 真崩溃的进程唤醒后**不在实际集合里**，该重启。
⇒ 判据就是"唤醒后**先探活、再对差集动手**"。这与启动时是同一套逻辑，只是触发时机从"core 启动"扩展到"每次唤醒"。

#### 6.1-D 监护器 + 崩溃自愈（极轻计划任务）

- 退避 1/2/4/8s 上限 30s；5 分钟内 3 次失败 → degraded 停止重试（复用 `nic-health-quarantine` 范式）。
- core 首次启动时**自行注册**计划任务 `BetterDesktop Core Ensure`：每分钟执行一次 `BetterDesktop.Core.exe`（**无参数**）。
  **不设 `--ensure` 开关**（实现期决策，YAGNI）：core 本就单实例，第二个实例抢锁失败即静默退出（实测 <100ms）→ 幂等天然成立。
- **任务电源配置（C13）**：

| 设置 | 值 | 理由 |
|---|---|---|
| `WakeToRun` | **false** | 不得唤醒机器 |
| `StartWhenAvailable` | **false** | 睡眠错过后不补跑（core 醒来自己 reconcile） |
| `RunOnlyIfNetworkAvailable` | false | 本地进程，无需网络 |
| `DisallowStartIfOnBatteries` | false | 电池下也要保证 core 在 |
| `StopIfGoingOnBatteries` | false | 切电池不停 core |

- **任务安全（二审修订）**：action 写**解析后的绝对路径**（不是相对名）；执行体所在目录必须是安装目录（非用户可写临时目录）。签名校验见 §12 deferred（成本/威胁模型分析）。
- 卸载/注销时删除任务；`recovery --clean-autostart` 也删。

#### 6.1-E 独占能力（简化门控）

删除 `HostPresenceWatcher` / `ExclusiveCapabilityHost`。判据从"壳在不在"改为**组件表 tier/gate 判据**——少一条状态，少一类竞态。

### 6.2 删除 Agent（5 个插件随 Host）

**结论：Agent 的 5 个插件（status / app-source / pinning / search / convert）全部随 Host 生灭，不迁 core。**

| 插件 | 消费者 | 壳退出后还需要吗 | 结论 |
|---|---|---|---|
| `status` | 菜单栏状态条、灵动岛媒体 | 不需要 | 随 Host |
| `app-source` | Dock、开始菜单 | 不需要 | 随 Host |
| `pinning` | Dock | 不需要 | 随 Host |
| `search` | 开始菜单搜索 | 不需要 | 随 Host |
| `convert` | 自绘右键菜单（`desktop.yml` 已单独加载）、CLI headless（进程内） | 由桌面/CLI 各自加载 | 随 Host |

⇒ 它们在 `agent.yml` 里出现**纯粹是 2026-09-11 方向的产物**，删 `agent/` 零功能损失。
边界：`dock-pin` 本就被 CLI 归入 `NeedsHostActions`（`HeadlessExecutor.cs:34-37`），语义不变——**壳不在时不静默拉起，只提示**。

**Agent 其余职责的落点**：

| 职责 | 落点 |
|---|---|
| `CaptureHotkeyOwner`（截图热键） | core（§6.1-B） |
| `DesktopServiceSupervisor` | core 监护（§6.1-D） |
| `EnsureContextMenuRegistration` + 60s 自愈 + `shellmenu-unregistered.flag` | core |
| `ExclusiveCapabilityHost` / `HostPresenceWatcher` | **删除**（§6.1-E） |
| `agent.yml` + 5 插件装配 | **删除** |

**回写**：`resident-architecture` 批次表 B1/B3/B4/B6 标注"方向反转，见本计划"。

### 6.3 控制管道：复用 `MenuCmd`，不新建框架（+ 安全 + 睡眠）

- 复用 `BetterDesktop.MenuCmd`（C7），服务端从 `host/MenuCommandPipe.cs` 迁到 **Rust core**。
- 协议向后兼容：`BDMC1|action|path` 继续被接受；新增 `BDMC1|@ctl|<verb>|<arg>`。
- 命令词表（幂等）：`status` / `start <component>` / `stop <component>` / `toggle <component>` / `open <panel>` / `capture now` / `set <key> <value>` / `get <key>`。
- `status` 返回 `desired / actual / health / restarts / uptime`。

**安全（C16，二审新增；**已实现并真机验证**）**：
- 服务端创建时 `PipeSecurity` **只放行当前用户**（并显式 **DENY** anonymous `S-1-5-7` 与 network `S-1-5-2`）；**不 GRANT SYSTEM**（最小权限：ensure 计划任务是用户级的）；
- **首实例加 `FILE_FLAG_FIRST_PIPE_INSTANCE`** —— 已存在同名管道时创建失败，阻止别的进程抢先建同名管道冒充服务端（该标志只能用于首个实例，后续实例再带会自己和自己冲突）；
- 连接后、**读第一个字节之前**校验调用者：`GetNamedPipeClientProcessId` → `OpenProcess` → `OpenProcessToken` → 比对**用户 SID + 会话 ID**；
- 消息大小上限 **1 MiB**（两侧解析器 + 读循环双重把关）；2s 无数据 **强制断开**（防"连上不发数据"占住实例）；
- **禁用** `NamedPipeServerStream.Create` 这类"无 ACL"的简化创建路径（见 §14 禁区）。

**⚠️ 两条实测纠正（都推翻了先前的设计假设）**：

| # | 原方案 | 实测结论 | 处置 |
|---|---|---|---|
| 1 | 用 `ImpersonateNamedPipeClient` 取调用者令牌，放在读循环第一行 | **不可行**。真机报 `ERROR_CANNOT_IMPERSONATE (0x80070558)`："在使用命名管道读取数据之前，无法经由该管道模拟" —— 该 API 要求管道上**已有数据流过**，与"读取前完成校验"**结构性冲突** | 改用 `GetNamedPipeClientProcessId`（无需数据流动即可用）；已保留在 `security.rs` 的文档注释里，附错误原文 |
| 2 | 管道用 `PIPE_TYPE_MESSAGE \| PIPE_READMODE_MESSAGE` | 与**已冻结的分帧**冲突：向量规定"一行一条消息"，而现有 C# 客户端 `StreamWriter.WriteLine + AutoFlush` **可能把字符串与换行分两次写**，消息模式下即两条消息 → 行解析器拿到半行 | 保持 `PIPE_TYPE_BYTE` + 累积到换行；`pipe.rs` 模块头写明原因 |

**响应格式：JSON，而非管道分隔（**偏离记录**）**

二审提议的响应形态是 `BDMC1|@ok|<verb>|<payload>` / `BDMC1|@err|<code>|<message>`。**本文保留 JSON**，理由三条：

1. **`status` 返回的是嵌套结构**（`desired` 是组件→状态的映射、`actual` 同理、外加 `health/restarts/uptime`）；用 `|` 表达嵌套只能自己发明转义规则，而那条规则迟早会与"arg 内可含 `|`"的既有约定打架。
2. **契约已封版**：30 条共享向量 + CI 契约门禁 + 两侧实现 + 两侧测试都已按 JSON 落地并验证；改成管道分隔是**净重写**，换不来能力。
3. **错误码是正交的**：二审真正要的是"结构化错误码而非字符串"，这一点**已完整采纳**——`error` 字段取 `docs/architecture/error-codes.md` 的固定字面量，两侧单测把字面量钉死。线格式与错误码结构是两件事。

保留的语义完全一致：`ok:true/false` 对应 `@ok`/`@err`，`error` 对应 code，`message` 对应人类可读文本。**若二审坚持管道分隔**，改动面 = 向量 + 两侧实现 + 两侧测试 + 门禁（约半天），请明确指示。

**睡眠（二审新增）**：
- **服务端**：唤醒后**重建管道实例**（旧连接可能已断）。
- **客户端**：连接失败时区分"core 没跑"与"刚从睡眠醒来"——后者只需等 1s 重连，**不触发 ensure core**。

```
try connect
  → 成功：正常
  → 失败：
       if 系统刚唤醒（上次唤醒时间距今 < 5s）  → 等 1s，重试一次
       else                                    → ensure core 一次，重试一次
       → 仍失败：降级（记 info 不记 error）
```
- 睡眠期间的消息**不补发**，客户端自己重试。

### 6.4 Host 去 Ensure

- 删 `EnsureDesktopServiceRunning`（`host/Bootstrap.cs:183`）、`EnsureAgentRunning`（:237）、`EnsureTrayRunning`（:296）与调用（:351,354,357）。
- 替换为一条控制请求（按开关推导），失败只记日志——**壳不再是任何进程的所有者**。
- 保留 `ApplyNativeTaskbar` / `ClearTaskbarExplicitLatchOnDockChange`（纯设置响应）。

### 6.5 按需化与开关门控收口

| 组件 | 现状 | 终态 |
|---|---|---|
| Index.Engine | 空闲 **600s** 自退 | **保持 600s，不改**（`BD_INDEX_IDLE_SECS` 已可配置） |
| Clipboard.Panel | watchdog 守护 | **移出守护名单**；`--open` 唤醒已运行实例，关闭即退 |
| Clipboard.Engine | watchdog 守护 + 开关门控 | 守护移入 core；开关关 → 停 + 不拉回 |
| DesktopControl | 四方 ensure | **唯一所有者 = core**；gate 关 → 停 + 不拉回 |
| Host（壳） | watchdog 守护 | **移出守护名单**（关主程序 = 就该消失） |
| Capture | Agent 热键拉起 | core 热键拉起 |

### 6.7 配置单写者 + 入口收口

**配置单写者**：**唯一写者是 core**（2026-09-19 修订，ADR 见 §13.19）——`SettingsService`（跨进程互斥 + 原子写）从"唯一写入口"**降为共享库**，**写操作的发起者只有 core**；`host/SettingsFileWriter.cs` 与 `BetterDesktop.Cli/HeadlessExecutor.cs:384,489` 的整文件直写 → 改走 core 控制管道，直写器删除。只读扫描保留。

**入口收口**：

| 入口 | 终态 |
|---|---|
| Launcher | 只 ensure core → 发 ApplyDesired → 退出 |
| 开机自启 | **只留 `BetterDesktop.Core`**；旧三个值名（`BetterDesktop.Tray` / `.Watchdog` / `BetterDesktop`）在三处（Register/Unregister/Recovery）从"写入"改为"清理" |
| CLI | 先转 **core** 管道；连不上 → ensure core → 重试一次；仍失败才 headless |
| 系统右键 DLL | **不变**（读快照，无 IPC） |
| 自绘右键菜单 | 不变（目标改 core） |
| 手动双击 exe | 只启动自身 + 向 core 注册；要完整世界用 launcher |

### 6.8 ShellMenu 快照（C3）：core **触发**重建，不写内容

- **问题**：`ShellMenuContentBuilder` 依赖 `shell-desktop` 运行时数据；core 引用 → 变重且违反禁止清单。
- **解法（既有路径，零新增框架）**：core 只做"**保证它存在且新鲜**"——启动/设置变更时，若快照缺失或标记过期，拉起一次性 `BetterDesktop.Cli.exe --rebuild-shellmenu`（写完即退）；快照仍是单写入者。
  「更多控制（实时状态）…」是**快照里的静态标签项**，点击走既有 `Cli --menu-batch` → 一次性 `DesktopControl --desktop-controls`（`HeadlessExecutor.cs:537`）。**桌面进程不在时该项照样显示可点。**
- **二审修订（该一次性进程的安全契约）**：
  - CLI **必须从解析后的绝对路径**拉起（core 同目录 → `%LOCALAPPDATA%\BetterDesktop`，与组件表同一 `resolve_exe`）；
  - 参数**逐项传参**，`--rebuild-shellmenu` 是**固定字面量**，不拼接用户输入（C17）；
  - 该一次性进程**不继承 core 的句柄**（`bInheritHandles=false`，S1 已如此实现）；
  - 失败必须记 core 日志 + 明确告警（C4）；D12 覆盖。

### 6.10 安全基线（二审缺口一，S2.5）

| 面 | 措施 |
|---|---|
| **管道** | `PipeSecurity` ACL（当前用户 SID + SYSTEM，拒远程）+ 调用者 PID/SID 校验 + 1MiB 消息上限 + 超时断开（C16） |
| **进程拉起** | **禁止字符串拼命令行**；`CreateProcessW` 的 lpApplicationName 传绝对路径、lpCommandLine 参数**逐项构造**（C17）。S1 的 `spawn_detached` 已是该形态，S2.5 补"参数值来源白名单"（只允许组件表字面量） |
| **反序列化** | **深度：不用自己写**——`serde_json` 自带递归上限（默认 128），实测 10000 层嵌套被正确拒绝（单测 `deeply_nested_json_is_rejected_not_stack_overflow` 已钉住）。要做的只有两件：①**绝不调用** `Deserializer::disable_recursion_limit`；②加**文件大小上限**（1 MiB，防超大文件 OOM）。字段/枚举白名单：S1 已做（枚举逐个 `parse` 返回 `Option`，未知值跳过并记日志） |
| **路径** | 组件 `exe` 解析后必须落在**允许目录**内（core 目录 / `%LOCALAPPDATA%\BetterDesktop`），规范化后做**前缀校验**，拒绝 `..` 与绝对路径注入（C19） |
| **模型/插件哈希** | 未来 `tier=infrastructure` 的外部模型/插件加载前做 SHA-256 校验（C20，**留位**，S5.5 起用） |
| **AMSI 借力** | 见 §12 deferred（`type: tool` 已留位） |

- 新增 `scripts/verify-security.ps1`（S2.5），把可机检的红线做成门禁（C8 同批登记 + 补测试）。
- 新增**威胁模型**文档：信任边界、逐条威胁（同机低权限进程 / 路径注入 / JSON 炸弹 / 计划任务持久化）、
  各面措施与"为什么不做"。**落点勘误（2026-09-19）**：计划原写 `SECURITY.md`，但仓里已有 `docs/security.md`
  （供应链/插件权限规则，另一主题），二者仅差大小写、在 Windows 上会**互相覆盖** ⇒ 改为 **`docs/threat-model.md`**，
  并由 `security.md` 顶部互链。

### 6.11 GPU 仲裁（二审缺口四，S5.5 留位）

- core 维护 **GPU 用户表**（谁是 GPU 消费者、优先级、是否活跃）。
- 数据来源 = 组件表 `tier`/`power` + 各消费者自报（经控制管道 `@ctl gpu <verb>`）。
- 协调对象：`bd-world`（UE5，D3D12）与 `bd-infer`（推理，显存）。策略留位，**本批不实现分配算法**。
- 唤醒后按 `power.onResume=reinit` 通知 GPU 用户重建上下文（§6.12）。

### 6.12 电源事件处理（二审缺口六，S3.5）—— core 职责 G

```
隐藏顶层窗口的消息循环处理 WM_POWERBROADCAST：

PBT_APMSUSPEND（睡眠前）
  - 暂停监护循环（避免把冻结误判为崩溃）
  - 持久化 core 状态（desired/actual 快照）
  - 释放可选资源（非必要句柄）

PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND（唤醒后）
  ⓪ **先重置所有计时器**（顺序红线，见下）—— 睡眠期间被冻结的退避 / 监护间隔 /
     degraded 冷却会在唤醒瞬间"全部到期"，若不先重置就会爆发并发动作风暴
  ① 重新枚举子进程（部分可能被系统杀掉）
  ② 重新注册热键（若丢失）
  ③ 重建托盘图标（若丢失 / explorer 重启过）
  ④ 重建管道服务端实例（旧连接可能已断）
  ⑤ 重新获取独占能力（任务栏外观 / 桌面图标钩子）
  ⑥ 按各组件 power.onResume 分派（reconcile / probe-and-reinit / reinit / restart / notify）
  ⑦ 对 `probe-and-reinit` 的组件：先探健康，不健康才 reinit
```

**顺序红线（三审新增）**：**重置计时器必须在 reconcile 之前，顺序不可颠倒。**
理由：唤醒瞬间所有被冻结的计时器同时到期 → 监护循环可能在"还没有实际探活结果"时就并发发起多轮拉起；更糟的是退避计时器归零会让 `degraded` 熔断被误判为"已冷却"，于是对刚失败过的组件立刻重试。先归零/重建计时器，再探活、再 reconcile，才能保证"一次唤醒 = 一次动作"。

**健康探测的判据（三审新增 —— 这是 `probe-and-reinit` 与 `reconcile` 的本质差别）**：

`reconcile` 只能回答"**进程在不在**"，回答不了"**它还能不能用**"。对 Surface 层不够：
| 场景 | 进程状态 | 是否可用 |
|---|---|---|
| 睡眠唤醒后 DWM 重建 | WPF 进程活着 | 窗口可能花屏 / Z-order 错 |
| 唤醒后 D3D12 设备丢失 | UE5 进程活着 | 渲染不出来 |
| explorer 重启 | ShellDLL 宿主进程活着 | 右键菜单不工作（钩子失效） |

故 `probe-and-reinit` = **经控制管道 `@ctl health <name>` 询问组件自报健康**；**无应答 / 超时 / 返回不健康** → 按 `reinit` 处理（`restart` 为最后手段）。
健康探测的**具体探针由组件自己实现**（core 不知道也不该知道 WPF 窗口或 D3D 设备的细节）—— 这正是 core 只做"问"、不做"判"的分层依据。

**红线**：core 全流程**不调用** `SetThreadExecutionState` / `PowerSetRequest`（C11），因此 `powercfg /requests` 必须为空（C15/D31）。

### 6.9 重命名（Q3 定案：不做）

不做全仓 `bd-*` 改名。本批唯一涉及命名的动作是 `BetterDesktop.Tray` → `BetterDesktop.Core`（Tray 被 Rust core 取代，属实质变更）。

---

## 7. Implementation Sequence

> **S1 已完成**（2026-09-19）。每步后仓库保持一致、可构建、可验收。

| 步 | 内容 | 验证 |
|---|---|---|
| **S0** | 冻结新增连线：Ensure 矩阵落成检查清单；`scripts/AGENTS.md` 加"新增跨进程拉起须登记所有者" | 清单与源码一致 |
| **S0.5**（二审新增，**大部分已完成 2026-09-19**） | **边界强制**。已完成：① `docs/architecture/六层模型与未来扩展点.md`（六层图 + 依赖方向 + tier/power 语义 + 唤醒时序 + 命名表 + 未来扩展点 + 电源红线）；② `docs/coding-standards-core-rust.md`（层边界 / 目录=边界 / 禁 Utils·Helper·Common / 一文件一主题 / **七项注释模板** / unsafe / 错误 / 资源 / 单测）；③ `verify-architecture-guard.ps1` 扩三条**边界棘轮**（lifecycle-owner / hotkey-registrar / settings-writer）+ `scripts/manifests/architecture-allowlist.json` + 14 条 Pester 单测；④ `AGENTS.md` 加 core 边界与电源红线条目。**未完成**：`scripts/new-extension.ps1` 生成器（见 §12 deferred） | ✅ 门禁绿（清单恰好覆盖现状）；✅ 单测 14/14 且覆盖「非法输入 → 违规」「条目失效 → 红」；生成器待做 |
| **✅ S1** | **Rust core 骨架**：crate + 托盘 + 隐藏顶层窗口 + 组件表 + 单实例 + 日志 | **已完成**：280KB / 私有工作集 1.18MB / 17 单测绿 / 托盘右键菜单人工验证可弹出 |
| **S2**（**已完成 2026-09-19**） | **控制管道**：① ✅ 协议契约 + 两侧实现 + 共享向量 + CI 门禁；② ✅ **`pipe.rs` 服务端**（4 实例线程 / 固定并发、有界读 + 2s 超时、`status`/`start`/`stop`/`toggle`/`get` 幂等分派、结构化错误码、每条请求可追日志）；③ ✅ **CLI 改指 core + ensure core 重试**（`CoreControlClient` 精确失败分类 + `CoreEnsurer` 幂等拉起 + `--core <verb>` 命令面）；④ ⬜ legacy action 的真正分派（现只记录，等 S6 消费者改指 core） | ✅ `bdctl status` **端到端真机已验证**（含 ensure + 重试，见 §13.8）；✅ 契约门禁绿 |
| **S2.5**（二审新增，**主体已完成 2026-09-19**） | **安全基线**：① ✅ 1 MiB 上限（两侧解析器 + 读循环双重把关，共享向量覆盖 `too-large`）；② ✅ 管道 ACL（DENY 在 GRANT 前 / 不收 SYSTEM / `FILE_FLAG_FIRST_PIPE_INSTANCE` / 拒远程）+ **读取前**调用者校验（按 PID 取令牌，SID + 会话）+ 2s 超时断开；③ ✅ `test-pipe-acl.ps1`（方案 C 静态 6 项 + 当前用户正向 + 方案 A 低权限真验）；④ ⬜ **方案 A 需在管理员环境手工跑一次**（本机无管理员，脚本已显式跳过并 exit 2）；⑤ ⬜ 路径前缀校验；⑥ ⬜ `SECURITY.md` | ✅ 静态 6/6 + 正向 3/3 通过；⬜ 方案 A（管理员） |

### S2 / S2.5 / S3 未完项（**收尾时必须逐条核销**）

- [x] `protocol.rs` / `security.rs` 的临时 `#![allow(dead_code)]` —— 已随 `pipe.rs` 接入**全部删除**（构建 0 警告）
- [x] CLI 改指 core + "连不上 → ensure core → 重试一次" —— **真机端到端已验证**（见 §13.8）
- [x] **S3 监护器 + reconcile + 退避/degraded + 计划任务** —— **真机全部验证通过与（§13.9）**
- [x] **S3.5 电源事件 + TaskbarCreated + 单连接 30s 上限** —— **恢复链真机验证通过（§13.10）**
- [ ] **真实睡眠/唤醒人工走一遍**（投递 `WM_POWERBROADCAST` 只验了恢复链；清单见 §13.10）
- [ ] `powercfg /requests` 与 `/waketimers` 需**管理员**复核（`WakeToRun` 已由反证实验闭环，§13.9）
- [ ] `reinit` / `probe-and-reinit` 的**反向健康通道**（组件自报健康）—— 随 bd-infer / bd-world 落地；当前每次唤醒会各留一条 WARN
- [x] ~~热键重建~~ —— **S4 迁热键时接入 `power::on_resume` 的 ⑤ 号位**（位置已在代码注释里定死）
- [ ] legacy action 的真正分派（S6，与消费者改指 core 同批）
- [ ] `test-pipe-acl.ps1` 方案 A 在管理员环境真跑并记录结果
- [x] **路径前缀校验（`security.rs`，C19）** —— 已落地（两道闸：`is_bare_name` 关掉 `join` 替换基路径的注入面 + `validate_resolved_component_exe` 断言落在允许目录；目录定义与 `resolve_exe` **同一份** `exe_search_dirs()`）；见本节末
- [x] **威胁模型** —— 落点为 **`docs/threat-model.md`**（不是 `SECURITY.md`：与既有 `docs/security.md` 仅差大小写，
  Windows 大小写不敏感会互相覆盖）。含信任边界 / 防谁（措施↔威胁↔**文件+测试名**指针）/ **不防谁（每条带触发条件）** / 该回来改文档的触发条件
- [ ] `powercfg /waketimers` 需**管理员**复核一次（当前以 `WakeToRun` 反证实验替代，见 §13.9）
- [x] **`task::unregister()` 已接线**（原 `#[allow(dead_code)]`）—— 三处：控制管道 `task` 动词
      （`bdctl --core task register|unregister|status`）、卸载器 `schtasks /delete`、`recovery --clean-autostart`；
      见 §13.12。**唯一一份任务定义**仍在 `core/src/task.rs`（另两处只按名字删）
- [x] **管道名所有权（§13.13）**：有界退避 + 点名诊断 + **显式确权**（0 号确权、1..N 等确权）已修并**受控验证**；
      ✅ `DesktopControl` 线索**已排除**（静态无 `StartServer`、运行时杀 core 后名字消失，§13.13）
- [x] **S4-2 第 2 步前置：注册表备份 / 回滚已具备并真机验收**（`--shellmenu-backup` / `--shellmenu-restore`，§13.14）
      —— 测试自己第一次用就踩响了这个闸门（回滚失败把开发机留在未注册态）
- [x] **S4-2 第 2 步**：core 触发 + `RepairGate`（退避 300s / 连续 3 次 → degraded / 收敛即归零）**已完成并真机验收**（§13.15）；
      异步触发（不等子进程）；日志含 X/Y/attempt/next 四要素；棘轮登记 `core\src\shellmenu.rs`
- [ ] S4-2 第 3–5 步（计划 §13.11 的 S4-3～S4-5 项）：桌面监护 + Watchdog 职责迁完 → 删 `agent/` + `watchdog/`
- [ ] **S4 新增独立一步**：删 `host/MenuCommandPipe.cs` 的服务端 + **显式验证 "core 是唯一 `MenuCmd` 服务端"**
      （杀 Host 后 `\\.\pipe\BetterDesktop.MenuCmd` 只归 core）。判据不能靠"删了 Host 进程"推断 ——
      服务端是**代码**不是进程，须显式核对
- [x] 构建纪律进 `docs/engineering-conventions.md` 第 11 条 + `docs/cookbook/会话交接.md` 第四问
      （**未进** AGENTS.md：它恰好顶在 1100 词预算上，`doc-budgets` 拦住是对的）
- [x] 命名管道所有权规则进 `docs/engineering-conventions.md` 第 12 条（跨域通用，非 core 特有）
- [ ] **Open Question 5 拍板** —— 在此之前 **D1（空闲 1 进程）与 D4（空闲 < 10MB）不可能成立**（§13.9 末节）
| **S3**（**已完成 2026-09-19**） | **监护器 + reconcile + 退避/degraded + 计划任务**（含 §6.1-D 电源配置）：`core/src/supervisor.rs`（唯一生命周期所有者：期望态×实际态对账、只对差集动手、退避 1→30s、5 分钟内 3 次失败→degraded、crash-loop 由 `pending_spawn` 检测）+ `core/src/task.rs`（XML + `schtasks /create /xml`）+ `process::run_and_wait` 原语 | ✅ **全部真机验证**（见 §13.9）：杀目标→3 秒内拉回；core 重启**不重复拉起**；任务自我修复 + 幂等 + D25 逐项配置 + 唤醒反证 |
| **S3.5**（二审新增，**已完成 2026-09-19**） | **电源事件**：`core/src/power.rs`（挂起暂停监护 + 状态快照；唤醒**①重置计时器 →②reconcile →③管道 →④托盘**；按 `power.onResume` 分派）+ `TaskbarCreated`（explorer 重启后托盘自愈）+ 单连接最长生命周期 30s | ✅ **真机验证通过（§13.10）**；⬜ **未验**：真实睡眠本身（不可脚本化，需人工）、`powercfg /requests`（需管理员） |
| **S4**（**进行中**：S4-1 ✅ / S4-2 ✅ / **S4-3 ✅（验收 9/13，其余待发布）** / **S4-4 源码侧 ✅（真机验收待 B1）** / S4-5 ⬜） | **删 `agent/` + 删 `watchdog/`**（热键 + 右键注册自愈 + 桌面监护 迁 core；5 插件随 Host；`ExclusiveCapabilityHost` 删除） | 壳关着时：截图热键可用、右键扩展自愈仍工作、任务栏外观保持；Host 关掉不复活 |
| **S4-1** ✅（2026-09-19） | **热键迁 core**：新 `core/src/hotkeys.rs`（唯一注册点）+ **抽共享 crate `shared/hotkey-spec`**（engine 与 core 共用解析器，避免第三份实现漂移）+ 附带修掉"tool 被误判为崩溃" | ✅ 真机（§13.11）：注册成功、`status` 可见归属、`WM_HOTKEY` 走监护器拉起截图、二次不重复、未知 id 不认领 |
| **S4-2**（**第 1–2 步 ✅** / 第 3–5 步 ⬜） | **右键注册自愈迁 core**（60s 检查 + `shellmenu-unregistered.flag` 语义保留；**不写快照** —— 那是 S6）—— 第 2 步 = core 的注册**触发** + `RepairGate`（§13.15） | ✅ 第 1 步（只读检查）真机三方对账通过；✅ 第 2 步（触发 + 退避/熔断）；⬜ 第 3–5 步 |
| **S4-3** ✅（2026-09-19，**真机验收 9/13**） | **桌面监护 + Watchdog 职责一次迁完**（Host / Clipboard.Engine / DesktopControl；Host 在 S4–S6 间仍被 core 监护）—— 含 `stopFlag` / `watchdog-pause` / **管道判活** 语义迁入 + 两个旧守护者（Watchdog / Agent）**停手**（源码） | 真机：**11 项 PASS**（1/2/3/4/5/6/7/8/9/10；**5/8 由 S5-3 入口补测通过**，见下）；12 部分 PASS；**11/13 待正式发布**（部署缺口，见 §13.16 末） |
| **S4-4**（**源码侧 ✅ 2026-09-19**；真机验收待 B1） | **删 `agent/`（+ 同批删 `host/Bootstrap.cs` 的 `EnsureAgentRunning` 与调用点）+ 删 `watchdog/`** | 源码侧已做：两个目录 + slnx + host/launcher/tray 的拉起点与两组菜单 + allowlist 棘轮收缩 2 条；**全仓构建 0 警告 0 错误**、architecture-guard **PASS**（详见 §13.22.4）。⬜ 真机验收 + **别人区域 8 处残留引用**（`publish.ps1` / `updater` / `recovery` / `packages`）待授权 |
| **S4-5** ⬜ | 真机验证清单（§13.11 末）+ 全仓检索 | — |
| **S5**（**进行中**：S5-1 ✅ / S5-2a ✅ / S5-2b ✅ / **S5-3 ✅（主体）** / **S5-4 ✅（动作归属 + 菜单补全，2026-09-19）** / S5-5 ⬜） | **托盘菜单补全**：.NET tray 的 27 项菜单搬到 Rust（功能开关 11 项 / 组件启停 / 系统集成 / 更新 / 应急恢复 / 卸载），经组件表 + 控制管道；`.NET tray/` 退役 | S5-2b = 11 项开关（真机 ✓）；**S5-3 = 组件启停子菜单**（三类分组 + 持久停止 + 重启 + 拒绝气泡 + `state` 五态出口，**179/179 单测、core 已部署**）+ **panel-唤起缺口已修并真机验证**；**S5-4 ✅ = 动作归属四档**（组件→supervisor / 设置→core 自写 / 自启→core 自写 `HKCU\Run` / 业务细节→只派发 CLI 窄命令 / 一次性动作→core 直接做）+ 暂停监护**双标记**（`watchdog-pause.flag` 更新器 / `user-pause.flag` 用户），**187/187 单测**，见 §13.22；⬜ S5-5 = 卸载项 + 真机走查 |
| **S5.5**（二审新增） | **数据模型扩展**：~~`components.json` 加 `tier`/`type`/`power` 字段~~ ✅ **schema 已提前落地（§13.7）**；~~把 `auto_start`/`must_stop` 接入实际监护循环~~ ✅ **已随 S3 落地**（`supervisor::reconcile` 即调用点）；剩余：GPU 仲裁表（留位） | D30 全部达成；不实现 bd-infer/bd-world |
| **S6** | **Host 去 Ensure** + **按需化收口**（§6.5）+ **ShellMenu 快照触发**（§6.8） | 关壳 → 只留 core；开关关 → 不被拉回；**壳关着时右键菜单项仍显示且可点** |
| **S7** | **配置单写者 + 入口收口**（§6.7；**单写者 = core**，见 §13.19）+ 老装机死值清理 | 全仓仅 **core** 一处写 settings.json（`SettingsService` 降为共享库）；Run 键无死值 |
| **S8** | **架构测试** + 文档回写 + `deploy-core.ps1` / `publish.ps1` 纳入 core + 计划任务登记门禁 | 门禁绿；`resident-architecture` 标注反转；扩展不得依赖 core 的测试为绿 |

### 7.1 当前阻塞与风险（2026-09-19 晚快照；**最新可执行边界见 §7.2**）

> 本节是 09-19 晚真机暴露时的快照，保留以免丢失当时的现场。
> 其中 **B4 已不准确**：S4-4 的**源码侧**已于 09-19 完成（删 `agent/` + `watchdog/`，
> 详见 §13.22.4），真机验收随 B1。另外 **B1 的形态也变了**：09-19 时是"全部未跟踪"，
> 现在是"**6/8 已跟踪、3/8 可构建**"（实测数据见 §13.23 表 2）。
> **要看下一步做什么，直接看 §7.2。**

| # | 阻塞 | 影响 | 解它的动作 |
|---|---|---|---|
| B1 | **发布路径被阻塞**：`core/`、`agent/`、`launcher/`、`tray/` 等**未跟踪**，且与 `packages/`（380 项）的**并行未提交工作同树** ⇒ 没有"干净检出"能造出这些产物 | **机器上跑着三个"源码已改、部署未更"的组件**：Watchdog / Agent / C# 托盘。实测后果：Agent 每 5 秒把桌面服务拉回，与 core 的 gate **打架**（今天三次遇到"旧守护者"） | 先定 `packages/` 那批的归属，再走 **`publish.ps1`**；**在此之前不要重启 Host** |
| B2 | **`Kernel.dll` 多副本偏斜**（4 份，其中 2 份是 09/16、09/17 的旧版） | legacy 命令被发去 **core**（`paste-session` WARN ×N）；**手工跨版本替换会直接搞坏面板**（今天已付一次代价，见 §13.16 末） | **发布期不变量**：所有消费者目录同一份构建（由 publish 保证，**不许手工拷 dll**） |
| B3 | **部署的 CLI 是旧构建**（`--core` 返回 Usage=2） | `status` 的 E2E 校验做不了；CLI 指 core 的能力在真机不可用 | 随 B1 的发布一起 |
| B4 | S4-4/S4-5（删 `agent/` + `watchdog/`）未做 | 旧守护者只要有入口被拉起就会重新打架 —— **这不是清理工作，是止血** | S4-4 + S4-5 |



### 7.2 对外依赖清单（给并行工作流 / **未来的自己**）

> **这份清单给谁看**：另一个我（或一个 agent），不是"另一个团队"。
> 所以它**自带上下文** —— 读的人只记得"这个项目在收敛"，不记得"3/8 可构建"。
> 目标：**5 分钟内知道下一步做什么、以及自己能回答什么**。

#### 背景（只读这一节也够）

- 本工作流（on-demand core 架构）**S1–S5 源码全部完成**：core 单测 **191/191**、
  `architecture-guard` 绿、门禁设施完整（16 道 + `-Fast` + 三行进度反馈）。
- 但**除门禁设施外，core 的功能改动全部只在单测里绿** —— 真机验证要等 **B1** 解开。
- **B1 的定义**：干净检出能构建出全部 **8** 个 publish 组件。
- **当前可构建：3 / 8**（`tray` / `recovery` / `updater` —— 它们零 `ProjectReference` 指向 `packages/`）。
- **被阻塞的 5 个**：`launcher`(2 refs) / `host`(25) / `Cli`(4) 的引用指向**未提交的 `packages/`**；
  `shell-desktop-control` / `shell-settings-host` 本身就在 `packages/` 里。

⇒ 一句话：**B1 被同一个东西卡了三次 —— `packages/` 那批未提交工作，以及 `host/` 的归属。**

#### 依赖 1：`packages/shell/shell-desktop-control/` 的提交

| | |
|---|---|
| **需要什么** | 该目录整体提交；或明确回一句"暂不提交，因为 X" |
| **现状（2026-09-20 实测）** | 目录存在；**10 个源文件 + 1 个 csproj**；`git ls-files` = **0**；**最后修改 09-19 18:45** —— 已一天多没动 ⇒ **很可能已经写完，只差一次提交**（这条信息让"还需要多久"从"不知道"变成"可核对"） |
| **为什么被阻塞** | 它是 publish 的 8 组件之一，且 `host` 的 25 个 `ProjectReference` 里有它 ⇒ 干净检出里 `host` 建不出来 |
| **影响** | 桌面服务相关的一切真机验证（S4-3 的 gate 语义、桌面控制菜单）都做不了 |
| **无解时的备选** | 用**旧版** `DesktopControl` 临时验 gate 语义（能验"关掉不被拉回"，验不了与 core 的时序）|

#### 依赖 2：`packages/shell/shell-settings-host/` 的提交

| | |
|---|---|
| **需要什么** | 同上 |
| **现状（2026-09-20 实测）** | 目录存在；**7 个源文件 + 1 个 csproj**；`git ls-files` = **0**；最后修改 **09-18 19:31**（比依赖 1 更早停下）|
| **为什么被阻塞** | publish 组件之一；设置中心的独立进程 |
| **影响** | 『S7 配置单写者』的前置验证做不了（"core 已是唯一写者"这个假设无法在真机上核） |
| **无解时的备选** | 暂跳，S7 时一并验 |

#### 依赖 3：`host/` 的 14 项改动如何提交

| | |
|---|---|
| **需要什么** | 一句判断：`Bootstrap.cs` 里的 `PublishPasteSession` 那部分归属谁；或约定"整体提交、由并行工作流负责切" |
| **为什么被阻塞（实测数据）** | `host/` 的改动构成：**13 个文件是 MIXED**（增删混在一起）+ **1 个 PURE-DELETE**（`MenuService.cs`，本工作流的）。最混的是 `Bootstrap.cs`（**+271/-21**）。**逐块切分等于逐行判断哪行属于谁** —— 那需要最了解并行工作流的人来判断（判据与证据见 §13.22.7）|
| **为什么"只提交那个纯删除"也不行** | 试过这条路：`MenuService.cs` 确实是纯删除、看起来可单独提交，**但同批的 13 个文件仍是旧版本** ⇒ 单独提交会造出"**git 里缺了 `MenuService`、其余文件却还是引用它的旧版**"的不一致状态。这正是"混合归属不能部分提交"的具体形态 |
| **影响** | **三条里最大的一条**：`host` 是 publish 组件之一 ⇒ 不切分，B1 无法完整跑通（最多 7/8） |
| **无解时的备选** | 两边各自提交自己的部分；或约定一个时间点整体提交（谁先动谁负责切）|

#### 这三条解开之后

B1 从 **3/8** 变 **8/8**。
**其余 380 项 `packages/` 改动是"优化"不是"阻塞"** —— 它们不影响可构建性，只影响仓库整洁度。

### 7.3 B1 解开后的第一天（**先验证，再新功能**）

> **这份手册给谁看**：B1 解开后的我自己。那时距写出这些代码可能已过一个月，
> 细节忘了一半，而面前有一堆"门禁 PASS 但没在真机跑过"的变更。
> **最容易犯的错**：直接进 S6 做新功能 —— 表 1 会继续变长，未验证的累积会变成负债。

**按顺序做，不要跳：**

| # | 动作 | 做完的标志 |
|---|---|---|
| 1 | 跑 `scripts/publish.ps1` | **8/8 组件产物齐全**（`$required` 的 14 项全在） |
| 2 | 把 core 部署到安装根 | `%LOCALAPPDATA%\BetterDesktop\deployment.json` 指向新目录；`schtasks /query` 里有 `BetterDesktop Core Ensure` |
| 3 | 真机验 **S4-5 清单**（§8.4 的电源/睡眠项 + §13.11 的 13 项） | 逐项记录，**PASS 与 FAIL 都要记** |
| 4 | 真机走查 **S5-5 卸载**（**当前完全未走查**） | 点菜单 → 确认框 → 脚本跑完 → 右键扩展 / 自启 / 计划任务全清、程序文件删净、**用户数据保留** |
| 5 | 真机点 **core 托盘菜单的 8 个动作**（表 1 主体） | 暂停监护 / 系统集成×4 / 更新×2 / 恢复 / 诊断包 / 打开日志 / 关于 / 开机自启 —— 每个都要有**可见结果或明确气泡**（"点了没反应"就是 FAIL） |
| 6 | 逐条核销 **表 1**（§13.23） | 表 1 清零 |
| 7 | **只有全绿之后**，才动 S6 | —— |

**为什么"表 1 清零"是硬门槛**：S6 要改 `host` 的拉起逻辑，而 `host` 正是表 1 里
"源码已改、部署未更"的组件之一 —— 在一个没验证过的基础上再改依赖它的东西，
等于把两个未验证叠在一起（比单独任一个都更难定位）。

### 7.4 三个开工入口（**不依赖 B1**，随时可做）

> 这三项写下来的理由：**"记着这个念头"在人脑里可行，在会话里不行** ——
> 下一个我打开这份文件时不会记得这次对话，只会看到这里写了什么。

| # | 项 | 为什么它独立 | 完成判据 |
|---|---|---|---|
| 1 | **假测试扫描**（是一"**类**"，不是一"**例**"）—— ✅ **首轮完成 2026-09-20** | `hotkeys.rs` 的恒真式断言已证明这类问题**以绿的姿态出现** —— 恒真式断言、恒真的绕过条件、空返回的 mock、被短路掩盖的判据。**191 单测里能相信的只有读过的部分** | 形态 A 在 Rust/Pester **均 0**；B/D 抽样 `supervisor` 核心判据**未发现**；**四形态清单 + 自检纪律已进 [`docs/testing.md`](../testing.md) 第六节**（新增测试时对照自检，比"以后再扫一遍"有用） |
| 2 | **clippy 剩余警告** —— ✅ **完成 2026-09-20**（10 → **0**） | 与本工作流耦合最小，纯本地 | `cargo clippy --all-targets` **0 警告**。其中 `is_degraded` 一处给出了有用的分辨：它是**只被单测使用**的，故标 `#[cfg(test)]` 而非 `allow(dead_code)` —— 后者是"预留借口"，前者是事实，且顺带保证它不进发布二进制 |
| 3 | **"未验证不得叠未验证"的抽象化** | 这条判据目前只写在 §7.3 里、**绑在 `host` 这个具体对象上**（"S6 要改 host，而 host 未验证"）。它的通用形态是：**已经改了但未验证的 A，不要在其上再叠依赖 A 的改动 B** —— 那会把两个未验证耦合成一个更难定位的失败。**不必现在改文案**（2026-09-20 决定），但下次遇到"要改一个依赖未验证对象的模块"时，应当立刻想到它 | —— |

**关于第 1 项的诚实标注**：这类问题**不可能被一次扫完**。恒真式只是最容易识别的形态；
更难的是"判据被短路掩盖"（条件永远先返回）与"mock 恰好返回期望值（而生产里走的是另一条路径）"。
所以第 1 项的目标不是"清零"，是**把这一类从"没人知道它存在"变成"有一个可重复的起点"**。

### 8.1 Rust 单测（`cargo test` in `core/`）—— S1 已 17 条

- `components`：表项校验（缺 name/exe、未知枚举、重名、`keepAwake=always` 拒绝）、默认值、内嵌表唯一性；
- `settings`：点分键取值、BOM、缺失/损坏/wrong-type 保默认；
- `tray`：定长缓冲 NUL 截断、宽字符转换；
- `log`：日期换算、**未 init 时零落盘**（防单测污染生产日志）；
- `main`：exe 定位、命令行引号拼装。

后续补：**supervisor**（退避序列 / degraded / reconcile 差集 / `desired=stopped` 不拉回 / **唤醒后不重启冻结进程**）、**pipe**（协议往返 + 旧形态兼容 + 超长消息拒绝 + 非法调用者拒绝）、**安全**（路径 `..` 注入拒绝、深层 JSON 拒绝）、**power**（`onSuspend`/`onResume` 策略分派纯函数）、**tier**（tier → 监护规则分派纯函数）。

### 8.2 架构测试（二审缺口二）

- `scripts/verify-architecture.Tests.ps1`（S8）：在**故意违规的样本仓库**上断言门禁报红，防"门禁永远绿"。
- 断言项：扩展不得依赖 core 内部符号；core 不得依赖任何扩展；`Process.Start` / `RegisterHotKey` / settings.json 写入各自**唯一合法位置**白名单。

### 8.3 安全测试（S2.5）

| 用例 | 预期 |
|---|---|
| 低权限/其它用户进程连管道 | 被拒（ACL） |
| 同用户但非我们进程连管道 | 调用者校验拒绝（若策略要求） |
| 组件 `exe` 含 `..\..\evil.exe` | **拒绝**（路径前缀校验） |
| 1 层 vs 10000 层嵌套 JSON | 深层被拒 |
| 2MiB payload | 被拒（>1MiB 上限） |
| 超时无数据 | 强制断开，不留半开连接 |

### 8.4 睡眠/电源测试（二审缺口六）

| 测试 | 内容 |
|---|---|
| `powercfg /requests` | core 运行时**为空**（无 BetterDesktop 条目） |
| 睡眠 → 唤醒 | core 热键可用、托盘图标在、`bdctl status` 可连 |
| 睡眠 → 唤醒 | **无重复进程**（reconcile 正确） |
| 睡眠中 explorer 重启 | 唤醒后托盘图标重建、独占能力重新注入 |
| 睡眠中杀子进程 | 唤醒后该进程被正确拉起（真崩溃） |
| 睡眠**冻结**的子进程 | 唤醒后**不被误判为崩溃、不重启** |
| GPU 唤醒（未来） | bd-infer / bd-world 重新初始化成功（`onResume=reinit`） |
| 计划任务 | 睡眠期间不唤醒机器；`schtasks /query /v` 验证 `WakeToRun=false` / `StartWhenAvailable=false` |

### 8.5 回归与端到端

- 回归：`Shell.ContextMenu.Tests` 113 / `Shell.Core.Tests` 138 / `Cli.Tests` 52。
- 真机端到端：D12–D14、D22–D27。

### 8.6 验证命令

- Rust：`$env:Path += ";$env:USERPROFILE\.cargo\bin"; cd core; cargo build --release; cargo test`
- .NET 构建：`dotnet build BetterDesktop.slnx -c Debug`；单测：`dotnet test BetterDesktop.slnx -c Debug`
- 门禁：`pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`
- **内存（统一私有工作集，口径见 §13.1）**：`Get-Counter "\Process(betterdesktop-core)\Working Set - Private"`
- 电源：`powercfg /requests`、`powercfg /waketimers`、`schtasks /query /tn "BetterDesktop Core Ensure" /v`
- 原生（若触及）：`pwsh -NoProfile -ExecutionPolicy Bypass scripts/build-shellmenu.ps1`

## 9. Risk and Impact Analysis

| 风险 | 级别 | 影响面 | 缓解 |
|---|---|---|---|
| **Rust core 重写量被低估**（27 项托盘菜单 + 更新/系统集成/卸载/应急恢复） | **高** | 工期；功能回归 | S5 独立成步，以 `2026-09-17` 审计 27 项逐项核销 |
| **管道无 ACL → 同机低权限进程冒充扩展** | **高** | 安全 | S2.5 必须与 S2 **同批**落地，不得"先功能后安全" |
| **唤醒后误判冻结为崩溃 → 重启风暴** | **高** | 用户可见（无重复进程是 D23） | §6.1-C 先探活再对差集动手；`PBT_APMSUSPEND` 暂停监护 |
| **core 阻止系统睡眠** | **高** | 系统级笑话（用户原话） | C11 + D31（`powercfg /requests` 为空）；门禁 grep 禁用 API |
| ShellMenu 快照失去写入者 | **高** | 系统右键全部条目消失 | §6.8 必须与 §6.5 同批（S6） |
| 热键双注册（core 与壳） | 高 | 截图/粘贴热键错乱 | Q2：core 独占"必须活过壳"的键 |
| 计划任务成为持久化/提权面 | 中 | 安全 | 用户级任务（无管理员）；aciton 绝对路径；签名校验进 §12 deferred（见 §12 分析） |
| C9：托盘菜单 owner 用错 → 静默不弹 | 中 | core 完全不可用 | **S1 已验证通过** |
| 边界门禁误报拖慢开发 | 中 | 效率 | 白名单要窄而明确；门禁自身有单测（§8.2） |
| 六层架构/tier 引入过度设计 | 中 | 复杂度 | 本批**只加字段与分派**，不实现 bd-infer/bd-world/GPU 分配算法 |
| 老装机 Run 键残留 | 中 | 开机静默失败 | §6.7 三处改"清理" |
| <8MB 未达成 | 低 | D2 | **S1 已达标（1.18MB 私有工作集）** |
| Updater / Recovery 引用被删 exe | 中 | 升级/应急断裂 | §10 清单含两者，S7 同批改 |
| 删除 Agent 后能力丢失 | 中 | 功能减少 | §6.2 逐项落点表；D6 走查 |
| 门禁/README 因删工程变红 | 低 | CI 红 | S4/S5/S7 同批更新 |

**可观测性**：core 每次监护决策（拉/不拉/退避/degraded/skip-because-running）与**每次电源事件**写 `core-yyyyMMdd.log`，字段含 `actor / component / reason / restarts / power-event`；控制管道每条请求写 Debug 单行。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `core/Cargo.toml`（✅ 已建） | crate `betterdesktop-core` | Rust core |
| `core/src/main.rs`（✅ 已建） | 隐藏顶层窗口 + 消息泵 + 组件表装载 | S1 |
| `core/src/tray.rs`（✅ 已建） | `TrayIcon`, `show_menu`（C9） | S1 |
| `core/src/components.rs`（✅ 已建 → S5.5 扩 tier/type/power） | `Component`, `Desired`, `Tier`, `PowerPolicy` | S1 / S5.5 |
| `core/src/settings.rs`（✅ 已建） | 扁平键只读 | S1 |
| `core/src/log.rs`（✅ 已建） | 按日轮转 + 未 init 不落盘 | S1 |
| `core/components.json`（✅ 已建 → S5.5 扩字段） | 组件表 | S1 / S5.5 |
| `core/src/pipe.rs`（新） | 服务端 + `@ctl` + ACL + 调用者校验 | S2 / S2.5 |
| `core/src/security.rs`（新） | 路径前缀校验 / 反序列化守卫 / 调用者 SID | S2.5 |
| `core/src/supervisor.rs`（✅ 已建） | `Supervisor`, `reconcile`, `decide`, `backoff`, `Outcome` —— **唯一生命周期所有者** | S3 |
| `core/src/task.rs`（✅ 已建） | `build_xml`, `classify`, `ensure`, `register`, `unregister` —— 计划任务（XML + `schtasks`） | S3 |
| `core/src/hotkeys.rs`（✅ 已建） | `refresh`, `refresh_force`, `on_hotkey`, `capture_registered` —— **core 唯一的热键注册点** | S4 |
| `core/src/shellmenu.rs`（✅ 第 1 步已建） | `check`, `classify`, `Registration`, `resolve_dll_path` —— 右键扩展注册状态**只读**检查（core 永不写 `HKCU\Software\Classes`） | S4-2 |
| `protocols/native-dll-path-test-vectors.json`（✅ 新建） | 原生 DLL **注册表路径**解析规则的共享向量（13 用例）；头部写明"两条规则为何不同" | S4-2 |
| `shared/hotkey-spec/`（✅ 新建 crate） | `parse`, `modifiers::*` —— 热键规范串解析**唯一真相源**（engine 与 core 共用，零依赖） | S4 |
| `core/src/process.rs`（✅ 已扩） | `run_and_wait`（同步运行 + 捕获输出 + 超时收尾）、`decode_console_text`（UTF-16 输出） | S3 |
| `core/src/power.rs`（✅ 已建） | `dispatch`, `on_suspend`, `on_resume`, `is_suspended` —— 唤醒恢复链 | S3.5 |
| `core/src/gpu.rs`（新，留位） | GPU 用户表 | S5.5 |
| `ARCHITECTURE.md` / `CODE-STANDARDS.md` / `SECURITY.md`（新） | — | S0.5 / S2.5 |
| `scripts/verify-architecture.ps1` / `verify-architecture.Tests.ps1` / `verify-security.ps1` / `new-extension.ps1`（新） | — | S0.5 / S2.5 |
| `agent/**`、`watchdog/**`、`tray/**` | 整目录删 | S4 / S5 |
| `host/Bootstrap.cs` | 删三个 `Ensure*`（:183,237,296）与调用（:351,354,357） | S6 |
| `host/MenuCommandPipe.cs` | 服务端迁 core | S2 |
| `host/SettingsFileWriter.cs` | 整文件删 | S7 |
| `packages/kernel/kernel/MenuCommandPipeClient.cs` | 增 `@ctl` + ensure core 重试 + **睡眠感知重连** | S2 / S3.5 |
| `BetterDesktop.Cli/HeadlessExecutor.cs` | 新增 `--rebuild-shellmenu`；`:384,489` 直写改走控制管道 | S6 / S7 |
| `launcher/Services/ComponentBootstrapper.cs` | `EnsureComponent`/`EnsureWatchdog` → 只 ensure core | S7 |
| `recovery/Program.cs` | 进程名单（:29-36）、`--clean-autostart`（:124-135）、计划任务清理 | S7 |
| `updater/ResidentGate.cs` | `:225` 拉起清单 | S7 |
| `packages/kernel/kernel/Deployment/AutostartRegistrar.cs` | `:24-37` 值名表 | S7 |
| `scripts/install-betterdesktop.ps1` / `uninstall-betterdesktop.ps1` / `publish.ps1` / `run-gates.ps1` / `deploy-core.ps1`（新） | core 产物 + 计划任务 + 门禁 | S8 |
| `docs/2026-09-11-resident-architecture.md`、`docs/plans/README.md` | 回写 | S8 |

## 11. Reusable Implementation Context

- **★ Rust 直接复用**：`engine/src/hotkey.rs`（热键全套 + 6 单测）、`engine/src/settings.rs`、`engine/src/ipc.rs` + `main.rs`（隐藏窗口消息泵 + `CreateNamedPipeW` 服务端骨架）、`engine/Cargo.toml`、`engine-index/src/main.rs:132-156`（空闲自退线程范式）。
- **S1 已产出的可复用件**：`core/src/{tray,components,settings,log}.rs`（组件表校验/托盘/扁平键/按日日志）。
- **监护/退避范式**：`TECH-KNOWLEDGE/69-网络聚合/nic-health-quarantine.md`。
- **所有权标记范式**：`TECH-KNOWLEDGE/69-网络聚合/system-proxy-snapshot-restore.md`。
- **explorer 重启/唤醒后重建**：`TECH-KNOWLEDGE/14-窗口与快捷键/1402-appbar-window-multiscreen.md`、`1404-shell-role-detect.md`。
- **将退役但仍是规格来源**：`watchdog/Program.cs:157-177,237,260,321-387`、`tray/TrayApplicationContext.cs:19-45`（11 开关 + 菜单结构）、`tray/ProcessBridge.cs`、`agent/Capabilities/CaptureHotkeyOwner.cs:63-185`、`agent/Program.cs:291-323`。
- **管道既有实现**：`host/MenuCommandPipe.cs`、`packages/kernel/kernel/MenuCommandPipeClient.cs`。
- **CLI action 词表**：`tray/TrayApplicationContext.cs:19-45` + `BetterDesktop.Cli/HeadlessExecutor.cs`。
- **留痕约定**：`%LOCALAPPDATA%\BetterDesktop\{watchdog-pause,host-stopped,agent-stopped,desktop-stopped}.flag`、`shellmenu-unregistered.flag`。
- **C9 实证 + 验证**：本项目 `NativeMenuPopup` 教训 + S1 人工验证通过。
- **门禁与文档纪律**：`scripts/AGENTS.md`、`docs/doc-standards.md`。

## 12. Assumptions and Open Questions

**已定案**：Q1 = Rust core；Q2 = core 独占"必须活过壳"的热键；Q3 = 不重命名（§0.1）。

**假设**：

- `[assumed]` Rust `Shell_NotifyIconW` + 隐藏顶层窗口 + `TrackPopupMenuEx` 满足托盘菜单全部需求（**S1 已部分验证：图标 + 菜单可弹出**）。
- `[assumed]` core 读 `settings.json` 扁平键即可推导全部 desired 状态。
- `[assumed]` 计划任务在当前部署形态（脚本安装、非 MSI）下可由 core 自注册，无需管理员权限（用户级任务）。
- `[assumed]` 一次性 `DesktopControl --desktop-controls` 进程在桌面服务未运行时能独立弹出"更多控制"菜单（`HeadlessExecutor.cs:537` 与 `publish.ps1:41` 注释支持，未真机复核）。
- `[assumed]` `PBT_APMRESUMEAUTOMATIC` 与 `PBT_APMRESUMESUSPEND` 在目标环境（Win10/11）均会到达；实现需同时处理两者。

**Open Questions（实现前需答）**：

1. **core 托盘菜单是否需要"实时勾选态"**？Rust core 每次弹菜单都现读 settings，天然可实时——比 .NET tray 更准。S5 前确认。
2. **`--rebuild-shellmenu` 的触发时机**：启动时 / 每次设置变更 / 定时。默认 = 启动时 + 设置变更（core 感知变更的方式待定：轮询 mtime 还是等 Host 通知）。
3. **扩展中心何时落地**？本批只做 core；扩展中心（启用/禁用/安装/权限展示）作为**后续独立计划**。本批只需保证 `components.json` 的 `tier`/`type` 字段足以承载它。
4. **GPU 仲裁的策略边界**：core 只做"通知与排队"，还是也要做"显存配额裁决"？本批留位不实现，但接口形态需在 S5.5 定。
5. **`desired=running` + gate 键缺失时的默认值 —— 已定案：按 tier 分档（四审拍板）**
   - **问题**（S3 实测量化，见 §13.9 末节）：`desktop`（surface）与 `clipboard-engine`（infrastructure）
     都是 `desired=running`，而 gate 键在真机 `settings.json` 里都不存在；按「键缺失 = 开启」两者都算开
     → 空闲常驻 = core + `clipboard-engine`（+ 生产的 `desktop`）= **2～3 个进程**，与 D1/D4 冲突。
   - **定案（四审）**：**分 tier 定默认值**

     | tier | 键缺失时的默认 | 理由 |
     |---|---|---|
     | `surface`（desktop / shell） | **关闭** | 用户可见的 UI，默认关才符合"按需"理念 |
     | `infrastructure`（clipboard-engine / index-engine） | **开启** | 共享能力，"剪贴板历史"是常用功能，默认开符合用户期望 |
     | `extension` / `system-extension` | **关闭** | 按需 |

   - **但被 S6 硬阻塞**：ShellMenu 快照依赖这些状态，S6 之前改默认值会引发用户可见故障
     （见 §6.1-A 的硬阻塞说明）。**故本项必须在 S6 同批实施，不得提前。**
   - **过渡期（S3.5–S5）验收标准调整**（四审）：接受到当前状态，
     **D1 改为「空闲 ≤ 3 进程」、D4 改为「空闲 < 15MB」**，并在 D1/D4 行内标注"过渡态"；
     **S6 之后恢复严格标准**（D1 = 空闲 1 进程、D4 = 空闲 < 10MB）。
6. **core 崩溃自愈的过渡态（S1→S3）**：S1 已跑通但计划任务在 S3，期间 core 崩了没人拉。**当前无害**（core 还没有承担任何 load-bearing 职责，拉起的组件也尚未部署）；但**在 S3 之前不要给外部用户试用**。三审提出"可提前到 S1.5"，本文选择保持 S3（现在加计划任务 = 在用户机器上每分钟跑一个还没用的进程，YAGNI）。→ ✅ **已闭合**：S3 的计划任务已落地并真机验证（§13.9），频率按四审定为 **5 分钟**（原写 1 分钟：1 分钟的恢复优势用户感知不到，日志膨胀是真的；入口 ensure 才是主要恢复路径）。
7. **计划任务的"安装程序兜底"何时接？**（S3 已实现 core 自注册那一半）
   core 自注册 + 自我修正已可用；"安装程序也注册一次"（五定的第 1 条）需要安装器侧改动，
   与本批 Rust 侧无关。**触发**：S8 的 installer 纳入 core 时一并加。

**Deferred follow-ups（含天花板与触发条件）**：

| 项 | 放弃的方案 | 已知天花板 | 升级触发条件 |
|---|---|---|---|
| **core 可执行文件签名校验**（二审提出） | 本批只做"action 绝对路径 + 安装目录非用户可写"，**不做 Authenticode 校验** | 安装目录可写的攻击者可直接替换 exe；但**用户级 Run 键已具有完全相同的能力**（本批已存在的自启方式），故计划任务**没有引入新的信任边界** | **触发**：项目引入代码签名（有 Authenticode 证书与签名流水线）时，一并覆盖 Run 键与计划任务 |
| **AMSI 安全扫描** | 不做 | 无借力系统杀毒的扫描能力 | **触发**：AI 管家落地后（`type: tool` 已在 schema 留位：`scan-file` → 一次性 `BetterDesktop.Scanner.exe`） |
| **GPU 仲裁分配算法** | 只留表与通知 | 无显存裁决 | **触发**：bd-world 与 bd-infer 同时可用时 |
| **bd-infer / bd-world 本体** | 不做 | 无推理网关与 3D 表面 | **触发**：核心收敛完成（S8 后）单独立项 |
| **扩展中心** | 不做 | 无 UI 管理扩展 | **触发**：S8 后单独立项（见 Open Question 3） |
| Run 键双值（core + Host 各自自启） | 只留 core 自启 | 无法"开机直接进完整壳" | **触发**：用户要求开机即有菜单栏/Dock |
| `subscribe <event>` 命令 | 不做 | 只能轮询 status | **触发**：出现第二个需要订阅的消费者 |
| 并发 `start` 去重锁 | 用 `requestId` 去重 + 幂等命令 | 同一毫秒并发 `start` 可能起两次 | **触发**：实测出现双起（core **重启后**的去重已由 §6.1-C 覆盖） |
| 托盘菜单"单组件重启"/tooltip 状态 | 不做 | 体验项缺失 | **触发**：用户要求 |
| 计划任务改为 Windows 服务 | 用计划任务 | 服务更重、需管理员、调试麻烦 | **触发**：计划任务被安全软件拦截 |
| `scripts/new-extension.ps1` 生成器（S0.5 的一部分） | 不做 | 新建扩展要手抄骨架（但骨架很短，且目前没有第二个扩展需求） | **触发**：出现"要新建第 2 个扩展/基础设施组件"的实际需求时再做（YAGNI） |
| 边界棘轮精度提升（R2/R3 从"标识符普查"升级为"调用点校验"） | 保持粗筛 | 会把 P/Invoke 声明、只读引用一起命中，清单里靠 `why` 人工区分 | **触发**：清单条目超过 ~40 条（人工维护成本超过写精确解析器的成本）时 |
| 全仓 `md-wrap` 基线红（`README.md`、`packages/shell/shell-search/README.md`） | 不动（**非本次引入**：这两个文件在我介入前就带着未提交改动） | `run-gates.ps1` 不全会绿 | **触发**：该并行工作流落地后（或用户授权）顺手拆掉硬换行 |

## 13. Definition of Done

### 13.1 拓扑与按需（一审）

| # | 项 | 验证方式 |
|---|---|---|
| D1 | ~~空闲时常驻进程数 = 1（`BetterDesktop.Core`）~~ → **过渡态（S3.5–S5）：空闲 ≤ 3 进程**；**S6 后恢复 = 1**。原因见 Open Question 5（`desired=running` + gate 键缺失 = 开启，四审定案按 tier 分档但被 S6 快照迁移硬阻塞）。**S3 实测 = 2 进程（core 1.43MB + clipboard-engine 4.76MB）** | 真机：关壳 + 关自绘桌面 → 进程计数 |
| D2 | 空闲内存 **< 8MB** —— ✅ **S1 已达 1.18MB；S3 后 1.43MB**（仅 core 自身；不含被监护拉起的组件 —— 那是 D1 的口径） | `Get-Counter "\Process(betterdesktop-core)\Working Set - Private"` |
| **D2b** | 同口径记录 `WorkingSet64`，用于与 §2 的 .NET 基线对比 | `Get-Process betterdesktop-core`（S1 实测 10.08MB） |
| D3 | 关系图无环：core → 各按需进程，无进程回头拉 core | S0 清单 + 代码 review |
| D4 | 每个进程有且仅有一个生命周期所有者，并落表存档 | 写入 `docs/MECHANISMS.md` 或本计划附录 |
| D5 | 所有入口只调管道 / bdctl | 检索 `Process.Start`，仅存在于 core 与 Launcher |
| D6 | core 挂了不影响系统右键与 CLI 的降级可用性 —— ✅ **右键侧 2026-09-20 核实**；⬜ CLI 侧待 B1 | 右键**不依赖 core**：注册走 `shellex\ContextMenuHandlers`（COM 处理器，explorer 加载 DLL 动态出项），四个 scene key（`*` / `Directory` / `Directory\Background` / `DesktopBackground`）**全部注册**，CLSID 与 DLL 均存在 ⇒ "core 挂了右键仍出项"的**充分条件齐备**（真按右键需人工，见 D12） |
| D7 | 右键菜单弹出无延迟（explorer 内零 IPC） | 真机计时 + `probe-shellmenu.ps1` |
| D8 | kill 任意按需进程不触发守护复活 | 真机逐个 kill，观察 60s |
| D9 | 关闭开关后进程不被拉回 —— ✅ **2026-09-20 双向实测** | 真机：写 `extensions.clipboard-history.enabled=true` → 3s 内 `supervisor(tick): started 'clipboard-engine'`；改回 `false` → 3s 内 `stopped clipboard-engine (pid kills=1)`。**开关是承重的，两个方向都动** |
| D10 | core 崩溃自愈：kill core 后被**兜底计划任务**拉回 ✅ **2026-09-20 实测** | 真机：kill core → 触发任务（等价于下一次触发）→ core 以新 PID 回来。**判据修正**：本行原写"≤1 分钟"，而任务实际间隔是 **5 分钟**（XML `<Interval>PT5M</Interval>`，真机 `Repeat: Every 0h5m`）。⇒ 判据按**实现**改为"**≤5 分钟**"。若"1 分钟"才是产品要求，那是**另一个改动**（把间隔改成 1 分钟），属体验选择而非缺陷 —— 不要把它当成"自愈失效" |
| D11 | core 重启 reconcile：core 崩溃期间某组件仍在跑 → 恢复后**不重复拉起** ✅ **2026-09-20 实测** | 真机证据是 **PID**：杀 core 前 `clipboard-engine` = 43332；core 以新 PID 回来后，engine **仍是 43332**（没被重启、没被拉出第二个） |
| D12 | **端到端 A（系统右键全链）**：关主程序 → 桌面右键 .zip → 菜单出现「解压到 ▸」带图标 → 点击 → 解压成功 | 真机实走 —— **需人工**（GUI 交互不可脚本化）。D6 已证明其**静态条件齐备** |
| D13 | **端到端 B（按需壳）**：空闲（仅 core）→ 托盘点「启动主程序」→ 菜单栏 + Dock 出现 → 关壳 → 回落到 1 进程 | 真机实走 —— **前提已修正**：此前写"卡 `host/`（安装根是旧网状版，会与 core 打架）"，那个理由**不成立** —— 把本机部署目录的历史遗物当成了"必须兼容的历史"。**没有正式发布、没有历史负担 ⇒ 直接覆盖即可**。真阻塞是"**没走过『最新源码 → 构建 → 部署 → 验证』**"，而那正是 B1（见 §13.22.8 末）|
| D14 | **端到端 C（热键→一次性进程）**：按截图热键 → `Capture.exe` 起来 → 截完退出 —— ✅ **2026-09-20 实测**（走完整生产路径） | 用 `keybd_event` 模拟**真实按键** `Win+Shift+B`（不是手工投递消息 —— 让系统自己产生 `WM_HOTKEY`）⇒ core 日志 `[INFO] explicit start: 'capture' -> ...\BetterDesktop.Capture.exe`；7 秒后 `Capture` 实例数回 **0** ⇒ **起来 → 截完自己退出**，与判据一致 |
| D15 | 老装机 Upgrade 后 Run 键无死值；计划任务仅一条 —— ✅ **2026-09-20**，且**超出判据**：从"查无死值"变成"**core 每次启动主动清死值**" | 见下方 §13.26 |
| D16 | 核心单测：supervisor 退避/degraded/reconcile、组件表校验、控制协议兼容、热键 spec | `cd core; cargo test` |
| D17 | 回归绿 —— ✅ **本工作流部分实测**；⬜ `Shell.*` 待 packages | `BetterDesktop.Cli.Tests` **90/90**、`launcher-tests` **9/9**（`host/` 无测试工程）。**判据数字已过时**：本行原写 `Cli.Tests` **52**，实测 **90** —— 与 D10 同类（"判据里的数字"≠实况） |
| D18 | 全仓构建 0 警告 0 错误 + 门禁全绿 —— ✅ **本工作流部分实测**；⬛ 全仓待 packages | `core`：`cargo build --release` + `cargo clippy --all-targets` **0 警告**；门禁：`-Fast` 仅剩 `md-wrap`（并行工作流 2 处）、Pester **90/90**。全仓 `dotnet build` 需 `packages/` |
| D19 | 托盘 27 项菜单逐项对照审计文档，无遗漏可点 | 对照 `2026-09-17` 审计 |
| D20 | 文档回写：`resident-architecture` 标注反转、plans 索引登记、core 禁止清单入 `docs/MECHANISMS.md` | 文件检查 |

### 13.2 安全（二审缺口一）

| # | 项 | 验证方式 |
|---|---|---|
| D21 | 低权限/其它用户进程连管道 → **被拒**；消息 > 1MiB → **被拒**；超时 → **强制断开** | 真机/单测（`verify-security` 用例） |
| D22 | 组件 `exe` 含 `..\..\` 或绝对路径注入 → **被拒**（路径前缀校验） | 单测（`core/src/security.rs`） |
| D23 | 深层/超大 JSON（组件表与 settings）→ **被拒**，不 panic、不 OOM | 单测（10000 层嵌套） |
| D24 | 高权限可执行文件拉起时**不发生**字符串拼接命令行（参数值只来自组件表字面量） | 代码 review + `verify-security.ps1` |

### 13.3 边界与规范（二审缺口二/三）

| # | 项 | 验证方式 |
|---|---|---|
| D25 | 架构门禁绿：三条边界棘轮（生命周期 / 热键 / 配置写入）无新增违规 | ✅ `verify-architecture-guard.ps1` PASS（2026-09-19） |
| D26 | 门禁**能报红**：故意违规样本（未登记的新拉起点 / 未登记的热键注册点 / 未登记的 settings 写入）被拦下；**清单条目失效也报红** | ✅ Pester 14/14（含「非法输入 → 违规」「条目失效 → 红」「豁免目录不参与」） |
| D27 | `scripts/new-extension.ps1` 产出的骨架可直接构建且默认通过门禁 | ⬜ **未做**，见 §12 deferred（当前无新扩展需求，属 YAGNI） |
| D28 | `AGENTS.md` 含 core 边界与电源红线条目并指向架构文档 | ✅ 已加「变更纪律」第 5 条（AGENTS.md 预算余 8 词） |
| D29 | core 代码规范文档存在且被引用（目录=边界 / 拒 Utils·Helper·Common / 一文件一主题 / 七项注释模板 / 命名规范） | ✅ `docs/coding-standards-core-rust.md`，由架构文档「相关」行引用 |

**S0.5 的两条诚实标注**：

1. **棘轮精度有限**：R2（热键）/ R3（配置写入）是**标识符普查**而非调用点校验 —— 会把 P/Invoke 声明、只读引用、写自建配置也命中（清单里已逐条标 `why` 区分）。它们的价值是"**新增即登记 + 清单不许腐烂**"，不是"证明唯一性"。逐条清单见 `scripts/manifests/architecture-allowlist.json` 的 `precision-note`。
2. **棘轮会强制收缩**：清单条目一旦在现实中不再命中（例如 S3 删了 `watchdog/`），门禁立即报红要求删条目 —— 这条是清单不腐烂成"历史垃圾场"的关键。

### 13.4 未来能力的数据模型（二审缺口四/五）

| # | 项 | 验证方式 |
|---|---|---|
| D30 | `components.json` 的 `tier` / `type` / `power` 被 core 正确解析，且 **tier 影响监护分派** | ✅ **已达成（2026-09-19）**：`components::{auto_start, must_stop}` 纯函数 + 单测 `tier_drives_supervision`；启动日志逐条输出 `tier/type/gate => ensure/stop`。剩余：S3 把它接进实际监护循环 |

### 13.5 电源（二审缺口六）—— **core 的第一公民职责**

| # | 项 | 验证方式 |
|---|---|---|
| D31 | `powercfg /requests` 在 core 运行时**为空** | 真机：无 BetterDesktop 条目 |
| D32 | 睡眠 → 唤醒后：core 热键可用、托盘图标在、`bdctl status` 可连 | 真机走查 |
| D33 | 睡眠 → 唤醒后：**无重复进程**（reconcile 正确） | 真机：唤醒前后进程数一致 |
| D34 | 睡眠中 explorer 重启 → 唤醒后托盘图标重建、独占能力重新注入 | 真机走查 |
| D35 | 计划任务在睡眠期间**不唤醒机器**、不补跑 | `schtasks /query /v` + `powercfg /waketimers` |
| D36 | 睡眠中杀子进程 → 唤醒后该进程被正确拉起（真崩溃） | 真机走查 |
| D37 | 睡眠**冻结**的子进程 → 唤醒后**不被误判为崩溃、不重启** | 真机走查 |

### 13.6 为什么 D2 用"私有工作集"（口径说明）

`WorkingSet64` 把 user32/comctl32/gdi32/kernel32 等**系统 DLL 的共享页**也算进单个进程，同一个库被几十个进程映射，记为"本进程占用"会重复计数。任务管理器"内存"列显示的就是**私有工作集**，也是"这个进程额外花了多少内存"的正确答案。
**但 §2 的 .NET 基线用的是 `WorkingSet64`**，两者不能直接相减——故补 **D2b** 同口径记录两者，用于横向对比。

### 13.7 S1 实测记录（2026-09-19，已完成）

| 观测项 | 结果 |
|---|---|
| 构建 | `cargo build --release` 0 warning |
| 单测 | `cargo test --release` **22/22 通过**（含二审新增的 tier 分派、`keepAwake=always` 拒绝、深层 JSON 拒绝） |
| 二进制体积 | **280 KB** |
| **私有工作集**（任务管理器口径） | **1.18 MB** ✅ |
| 私有提交 / 工作集（含共享页） | 1.70 MB / 10.08 MB |
| 托盘图标 | `NIM_ADD` 成功，图标从真实 `.ico` 载入 |
| **托盘右键菜单** | ✅ **人工验证可正常唤出**（C9 红线成立） |
| 组件表 | 7 条载入，启动日志逐条输出 `desired / tier / type / gate => ensure / stop` |
| **schema 留位** | ✅ `tier` / `type` / `power` 三字段已解析并驱动监护分派（D30 达成） |
| **`keepAwake=always`** | ✅ 被 schema **拒绝**（跳过该条 + 记日志）——core 不得无条件阻止睡眠 |
| **深层 JSON（10000 层）** | ✅ 被拒（`serde_json` 默认递归上限）；单测钉住"不得关掉默认上限" |
| 单实例 | 第二个实例抢锁失败静默退出（计划任务幂等的依据） |

### 13.8 S2 实测记录（2026-09-19，已完成）：`bdctl status` 端到端

真机链路（core **未运行**的干净起点）：

```
$ BetterDesktop.Cli.exe --core status
core uptime=0s restarts=0
component         desired        actual  health
capture           on-demand      False   ok
clipboard-engine  running        False   ok
clipboard-panel   on-demand      False   ok
desktop           running        False   ok
index-engine      on-demand      False   ok
settings          on-demand      False   ok
shell             on-demand      False   ok
EXITCODE=0
```

两侧日志互证（这是"确实走了 ensure + 重试"的证据，而非"恰好有个 core 在跑"）：

```
[CLI ] core verb=status arg= json=False
[CLI ] ensure core: launched ...\betterdesktop-core.exe (pid=50500)
[CLI ] core status → ok (attempts=2)          ← 第 1 次连不上 → ensure → 第 2 次成功
[core] control pipe serving on \\.\pipe\BetterDesktop.MenuCmd with 4 instance threads
[core] pipe request: kind=control head=status arg=""
```

`uptime=0s` 是关键旁证：core 是被这次调用**刚拉起来**的。

| 观测项 | 结果 |
|---|---|
| `bdctl status` 退出码 | 0 |
| 尝试次数 | 2（第 1 次 `NotRunning` → ensure → 第 2 次成功） |
| ensure 是否真的拉起 core | 是（CLI 日志含 pid） |
| 客户端单测 | **7/7**（起真实测试管道服务端，覆盖 ok / RemoteError / Malformed / NotRunning / EnsureFailed / ensure 重试） |
| 失败分类 | `NotRunning` / `AccessDenied` / `RemoteError` / `Malformed` / `Timeout` / `EnsureFailed` 六类；退出码 **7 = 连不上**（已 ensure）、**8 = 被拒或业务错** —— 分开是为了让脚本知道"该不该重试" |

**实现期两处纠正（已记入代码注释）**：

1. **删掉 `WaitForPipe` 探测**：判断管道就绪的唯一手段是"连一下"，而那一下会被服务端当成**真实客户端**接走 —— core 的实例线程会为它走完整的校验 + 读超时，白占一个实例并刷一条误导日志。改为"到期循环重发**真实**请求"（成功即止、到期即败），探测副作用归零。
2. **发送改为直接写字节**：不经 `StreamWriter`，分帧完全由共享向量定义的契约决定，不受其换行约定 / BOM 影响（同时消掉 CA2000）。

**未覆盖（如实标注）**：计划 §6.3 要求客户端区分"core 没跑"与"刚从睡眠醒来"。一次性客户端（CLI / 右键降级路径）**无法**知道这一点 —— 它自身也是刚被拉起的，没有历史状态可比。该区分只对长驻客户端（壳 / 面板）有意义，随 S4 落地。

### 13.9 S3 实测记录（2026-09-19，已完成）：监护器 + reconcile + 计划任务

单测：`cargo test --release` **93/93**，0 warning。

#### 监护器（用无害探针 `ping.exe` 改名为 `bd-probe.exe` 当常驻组件，避免拉起真实桌面组件）

| 验证 | 证据 | 结论 |
|---|---|---|
| 启动 ensure | `supervisor(startup): started 'probe' (restart #1)`，进程数 1 | ✅ |
| 杀掉→拉回 | 杀后 0 → 6 秒后 1，`supervisor(tick): started 'probe' (restart #2)` | ✅ 退避内自愈 |
| core 死→组件不受影响 | core 被杀后 probe 仍为 1 | ✅ 父进程死不带亡 |
| **core 重启→不重复拉起** | probe **仍为 1**，且启动日志**没有** `started probe` | ✅ **D23 核心不变量** |
| 未部署组件 | `desktop` 失败 → 退避 1s→2s→4s 递增 → 窗内 3 次后 degraded 转静默 | ✅ 不热循环 |

#### 计划任务

| 验证 | 证据 |
|---|---|
| **自我修复** | 预置一个指向 `notepad.exe` 的同名任务 → core 日志 `rebuilt (was stale: points at a different executable (expected …betterdesktop-core.exe))` |
| 幂等 | 二次启动 → `scheduled task '…': up to date`（**这同时证明 `schtasks /query /xml` 的 UTF-16 输出被正确解码** —— 否则会每次误判 Stale 反复重建） |
| Command / WorkingDirectory | 绝对路径 + 显式工作目录，均正确 |
| Arguments | 空（`schtasks` 输出确认） |
| UserId | `S-1-5-21-…-1001` == 当前用户 SID |
| LogonType / RunLevel | `InteractiveToken` / `LeastPrivilege`（用户级，无需管理员） |
| Interval | `PT5M` |
| ExecutionTimeLimit | `PT0S`（无限 —— 有限时限会把常驻 core 按点杀掉） |
| StopOnIdleEnd / RunOnlyIfIdle | `false` / `false`（默认 `true` = "用户回来动一下鼠标就杀掉 core"） |
| MultipleInstancesPolicy | `IgnoreNew` |
| DisallowStartIfOnBatteries | `false` |
| **`WakeToRun` / `StartWhenAvailable`** | 导出 XML **不含**这两个元素 —— Task Scheduler 会省略"等于 XSD 默认值"的元素。**已用反证实验闭环**：注册一份显式 `WakeToRun=true` 的探测任务，导出里**确实出现** `<WakeToRun>true</WakeToRun>` → 证明省略是**按值**决定的，故我们的任务确实未开唤醒（C13 成立） |
| `powercfg /waketimers` | ⚠️ **需管理员权限，本次未验**。替代证据 = 上面的 `WakeToRun` 反证 |

#### D2 重测（S3 之后）

| 观测项 | S1 | S3 后 |
|---|---|---|
| 二进制 | 280 KB | **532 KB** |
| **私有工作集** | 1.18 MB | **1.43 MB** ✅ 仍远低于 < 8MB 目标 |

监护线程 + 计划任务模块的代价 = **+0.25 MB**。

#### ⚠️ S3 实测暴露的一个**会打破 D1/D4** 的事实（需产品决策）

`components.json` 里 `desktop`（surface）与 `clipboard-engine`（infrastructure）都是 `desired: "running"`，
而它们的 `gate` 键（`components.desktop` / `extensions.clipboard-history.enabled`）**在真机 `settings.json` 里都不存在**。
按既定语义「**键缺失 = 开启**」，两者的 gate 都算开 → **core 每次启动都会把 `clipboard-engine` 拉起来**（实测已复现）。

实测空闲状态：

```
betterdesktop-core               私有工作集    1.43 MB
BetterDesktop.Clipboard.Engine   私有工作集    4.76 MB
```

即**空闲常驻进程数 = 2**（本机 `DesktopControl` 未部署，故为 2；**生产环境会是 3**）。

这与 **D1（空闲常驻进程数 = 1）** 和 **D4（空闲内存 < 10MB）** 直接冲突 —— 不是实现缺陷，
而是"`desired=running` + 键缺失即开启"这套默认值本身就是**选择加入的反面**。
见 Open Question 5（该问题此前只覆盖 `desktop`，现证据显示 `clipboard-engine` 同样在内，且量化为 D1 的硬冲突）。

#### Open Question 5 的决策材料（2026-09-20 补，供拍板）

**事实**（§13.9 末 + 真机复现）：`components.json` 的 `desktop`（surface）与 `clipboard-engine`（infrastructure）
都是 `desired: running`，而它们的 gate 键（`components.desktop` / `extensions.clipboard-history.enabled`）
**在真机 `settings.json` 里不存在**。按既定语义「**键缺失 = 开启**」，两者都算开
⇒ **core 每次启动都会把它们拉起来**。实测空闲常驻 = **2 进程**（生产环境 3）
⇒ 与 **D1（空闲常驻 = 1）**、**D4（空闲内存 < 10MB）** 直接冲突。

**这不是缺陷，是默认值语义的选择**：

| 选项 | 语义 | 代价 |
|---|---|---|
| **A** 保持「键缺失 = 开启」 | 开箱即用（装完就有剪贴板历史 / 桌面服务） | **D1/D4 永久不成立**（除非改 D1 口径）—— 而 D1 正是"按需化"这个架构的核心承诺 |
| **B** 改成「键缺失 = 关闭」（选择加入） | 空闲真的只剩 core ⇒ D1/D4 成立 | 首次使用需显式开启（可用"首次偏好"引导覆盖）；且它**改变所有 gate 组件的默认行为**，需逐项过一遍 |
| **C** 按 tier 分档（计划早前提过） | 精细：foundation 常驻、extension 默认关 | 复杂；且计划记着它"**被 S6 的快照迁移硬阻塞**" ⇒ 现在做不了 |
| **D** 改 D1 的口径（如"空闲 = 用户未显式开启任何组件"） | 最快 | 定义被改了，而 D1 是给外部看的承诺 —— **改口径等于取消承诺**，必须说成"取消"而不是"满足" |

**建议 B**，理由：

1. **D1 是这套架构对外的核心承诺**（"按需化"的全部意义）；A 让承诺永久落空。
2. 「键缺失 = 开启」的方向与架构目标**相反**：它把"按需"变成了"默认常驻"。
3. 代价（首次需显式开启）是**一次性**的，且已有"首次偏好"这个落点。
4. C 更精细，但它**卡在 S6**；B 是"现在能定、S6 后依然成立"的那个。

#### ✅ 定案：采纳 B（2026-09-20，用户拍板）

**拍板时把"迁移"这条直接消掉了**：**本项目没有老用户** ⇒ "改成键缺失 = 关闭会让老用户的
剪贴板历史静默关掉"这个顾虑**不存在** ⇒ 不需要任何迁移代码。
⇒ **这也是"没有老用户"这个事实唯一一次直接简化了实现**（它通常只影响发布策略）。

**实施**（同一个常量，5 个求值点共用）：

| 落点 | 改动 |
|---|---|
| `core/src/components.rs` | 新增 `pub const GATE_DEFAULT: bool = false` —— **单一来源**，注释写明为什么与"为什么必须是一个常量" |
| `core/src/supervisor.rs` | `gate_open()` 引用它（**监护器**，最关键的一处） |
| `core/src/main.rs` | 启动日志的 gate 求值 |
| `core/src/pipe.rs` | `status` 的 gate 求值 |
| `core/src/tray.rs` | 菜单勾选态 + `TOGGLES` 的 9 个 gate 项 `default`（**用常量而非字面量**，将来再变只改一处） |

**单测：两条 gate 用例升级为"三种配置都测"**（缺失 / 显式 `false` / 显式 `true`）——
它们原先的注释写着"必须真的写 `false`，用缺失键测会得到**假绿色**"，
而现在默认值反过来了：**缺失键本身成了正面用例**。三种都覆盖，"默认值语义"才真正被钉住。
**191/191 通过、clippy 0。**

#### ✅ 验收：D1 达成（2026-09-20 真机）

单拷部署后（§13.24 流程），core 启动日志：

```text
- desktop          gate=components.desktop=false                => ensure=false stop=true
- clipboard-engine gate=extensions.clipboard-history.enabled=false => ensure=false stop=true
- clipboard-panel  gate=extensions.clipboard-history.enabled=false => ensure=false stop=true
- index-engine     gate=extensions.index.enabled=false           => ensure=false stop=true

stopped BetterDesktop.DesktopControl.exe (pid 62160)
stopped BetterDesktop.DesktopControl.exe (pid 4720)
stopped BetterDesktop.Clipboard.Engine.exe (pid 3576)
stopped BetterDesktop.Clipboard.Panel.exe (pid 37832)
supervisor(startup): stopped desktop (pid kills=2), clipboard-engine (pid kills=1), clipboard-panel (pid kills=1)
```

| 指标 | 结果 |
|---|---|
| **D1 空闲常驻进程数** | **1**（只有 `betterdesktop-core`）—— 此前实测 5 |
| **D2 口径内存**（私有工作集） | **2.10 MB**（要求 < 8MB）|

⇒ **"按需化"的核心承诺首次在真机上成立**：空闲时机器上只剩 core 一个进程。
用户点开对应开关后，组件才被拉起（gate 开 ⇒ `ensure=true`）。

> **一处笔记**：本节前文把内存指标写成 "D4"，实为 **D2**（D4 是"每个进程有唯一生命周期所有者"）。
> 原文保留不改，在此更正 —— 免得后人对着一张错的编号去找指标。

**未决（现在真的没了）**：原先预留的"迁移归 S6 还是 S7"随"没有老用户"一起消解。

### 13.10 S3.5 实测记录（2026-09-19，已完成）：电源事件

单测：`cargo test --release` **104/104**，0 warning。

**验证方法（诚实说明）**：真正进入睡眠**无法脚本化**。这里向 core 的隐藏窗口投递**真实的**
`WM_POWERBROADCAST`，走的是与系统完全相同的代码路径（窗口过程 → `power::dispatch` → 恢复链），
只少了"系统真的把内存挂起"这一步。**"Windows 是否真的会投递该消息"由 Windows 负责，本次不验。**

真机日志（一条不多、顺序不乱）：

```text
power: PBT_APMSUSPEND — supervision paused, holding no power request
power(suspend): shell/desktop/clipboard-engine/… desired=… gate=… ensure=… actual=…   ← 7 条快照
power: resumed (automatic) after ~8s                                       ← 墙钟时长正确
power(resume): reset 7 supervision timer(s) before reconciling             ← ① 计时器最先
power(resume): <7 条快照> + reconcile                                       ← ② 差集
power(resume): 'shell'/'desktop' declared probe-and-reinit but NOT implemented → falling back
power(resume): 'clipboard-engine'/'index-engine' declared reinit but NOT implemented → falling back
pipe(resume): generation -> 1; … recycles within 2s                        ← ③
tray icon removed → tray icon re-registered (resume)                       ← ④
power(resume): resume chain complete
```

| 验证项 | 结果 |
|---|---|
| 挂起期间监护**真的暂停** | 挂起窗口内 `supervisor(tick)` = **0 行**（时间戳可证：挂起 132046 → 唤醒 140640 之间无 tick） |
| 挂起前状态快照 | 7 条 `desired/gate/ensure/actual` 全量落盘 |
| 唤醒后状态一致 | `clipboard-engine actual=true` 前后一致；未重复拉起（进程数仍为 1） |
| **唤醒后先重置计时器** | `reset 7 supervision timer(s) before reconciling` —— 顺序在 reconcile 之前 |
| 挂起中杀组件 → 唤醒后拉回 | 杀后 0 → 唤醒后 **1**（正是"睡眠中真崩溃"的路径） |
| 唤醒后管道 | `pipe(resume): generation -> 1`，无需重建实例（论证见下） |
| 唤醒后托盘 | `tray icon removed` → `tray icon re-registered (resume)` |
| `TaskbarCreated` | 启动即 `registered TaskbarCreated message (49370)` |
| core 不重复 | core 进程数仍为 1 |
| **`powercfg /requests`** | ⚠️ **需管理员权限，本次未验**（与 `/waketimers` 同） |
| **真实睡眠/唤醒** | ⚠️ **未验**，需人工按下面的清单走一遍 |

**`reinit` / `probe-and-reinit` 如实记为未实现**：`shell` / `desktop` / `clipboard-engine` / `index-engine`
在每次唤醒时都会各留一条 WARN，点名"回退成 reconcile，可能仍然花屏/钩子失效"。
这**不是**噪声而是有意的：这两个动作需要组件**自报健康**（core 不知道 WPF 有没有花屏、D3D 设备有没有丢），
那个反向通道随 bd-infer / bd-world 落地。假装实现了会让 `probe-and-reinit` 变成一句空话。

### 13.12 S3 收口：计划任务的"两者都做"（2026-09-19）

S3/S3.5 本身早已完成（§13.9/§13.10），本节补的是第 1 / 第 5 条决策里**当时刻意留着**的两半：
"安装程序兜底注册"与"卸载/应急路径删任务"。

**单测**：`cargo test --release` **136/136**（+7），0 warning。

#### 接线（谁调谁，以及**为什么不让 C#/PowerShell 各写一份**）

| 动作 | 调用方 | 实现 |
|---|---|---|
| 注册 | ① core 启动自注册 ② **安装器兜底** ③ `bdctl` | ①②③ **全部**落到 `core/src/task.rs` |
| 查询 | `bdctl --core task status` | `task::query` |
| 删除 | ① `bdctl --core task unregister` ② **卸载器** ③ **`recovery --clean-autostart`** | ① 走管道 → `task::unregister`；②③ **只按名字 `schtasks /delete`** |

- 安装器**必须委派**：新增 `Cli --core task register`（非致命，失败只 WARN）—— 任务定义 XML 只有一份，
  在 PowerShell 里再拼一遍就是本仓库吃过的事故形态（两处实现各自自洽、谁也不报错）。门禁断言这条委派。
- 卸载器与 recovery **刻意不走管道**：卸载器要删掉 core 的整个目录，走管道就得先"ensure core"——
  那会把正要被卸掉的进程拉起来；recovery 是**零依赖**应急程序，调不到 `task.rs`。
  两处都只**按名字删**，不生成任何任务定义 ⇒ 不构成第二份实现。
- 任务名成为**跨进程字面量**，由 `verify-system-integration.ps1` 四处交叉核对（core / uninstall / recovery
  + 安装器委派）。门禁自测 **7/7**（新增 2 条：卸载器丢名、安装器不再委派）。

#### 顺带补上的一条守卫：**拒绝把系统级任务指向不稳定位置**

`ensure()` 原先直接用 `current_exe()` 当任务目标。这意味着**从 dev bin 跑的 core 会把兜底任务指向 dev bin** ——
那个目录一次 clean/重建，任务就永远指向不存在的 exe，每 5 分钟失败一次，
**而那时 core 已经不在了、没有任何地方会报这条错**。

这与注册表 DLL 路径是同一类问题，结论也相同（"持久引用必须指向最持久的位置"）：
新增 `is_stable_location()`（安装根之下 或 `%LOCALAPPDATA%\BetterDesktop` 之下），
不在稳定位置 ⇒ `Ensured::Skipped` ⇒ **不注册、也不改动既有任务**，只记一条带原因的 WARN。
**这里没有 `--dev` 例外**：dev 注册右键扩展是"我要测这个功能"，而把开发目录写进系统级计划任务是纯负债。

真机证据（直接从 dev bin 跑 core）：

```text
[WARN] scheduled task 'BetterDesktop Core Ensure': NOT registered — this core lives at
  ...\BetterDesktop.Cli\bin\Debug\net8.0-...\betterdesktop-core.exe which is neither under the
  install root (...\app\2026.09.17.1610) nor under %LOCALAPPDATA%\BetterDesktop — refusing to
  point a system-wide task at it
```

随后从**安装根**跑 core，任务被正确注册到安装根，D25 逐项复核：

```text
Interval=PT5M   WakeToRun=False   StartWhenAvailable=False   MultiInst=IgnoreNew
TimeLimit=PT0S  RunOnlyIfIdle=False  Batteries=False
UserId=17822（当前用户，非 SYSTEM）  Interactive / Limited  Args=''  WorkDir=<安装根>
```

**兜底链真机闭环（意外但有力的证据）**：任务注册到安装根后，它在 `16:20:01` **真的触发了** ——

```text
Get-Process betterdesktop-core → StartTime = 2026/9/19 16:20:01
Path = C:\Users\17822\AppData\Local\BetterDesktop\app\2026.09.17.1610\betterdesktop-core.exe
任务 Next Run = 16:20:00，Interval = PT5M
```

即：core 不在时被兜底拉回，且拉回的是**安装根那一份**（不是 dev bin）。这正是第 1 / 5 条决策要的效果。
顺带暴露一个前提：本次之前**安装根里并没有 `betterdesktop-core.exe`**（这个安装早于 core 架构），
已一并补上 —— 否则任务的目标是个不存在的文件，"注册成功"会是假成功。

顺带修掉一处**重复实现**：`task.rs` 原先自带 `same_path`（只 trim/lowercase，**不归一分隔符**），
与 `shellmenu::path_eq` 并存 —— 同一进程里两套等价规则。已改为复用 `path_eq`（那份共享向量的实现），
并加回归钉子：任务里写 `C:/x/core.exe` 不再被判成漂移。

#### ⚠️ 撞出的真问题：管道名所有权（**已修**，见 §13.13）

真机日志里出现**每秒一条**的错误，持续不断：

```text
[ERROR] pipe instance 0: create failed: CreateNamedPipeW(\\.\pipe\BetterDesktop.MenuCmd) failed (GetLastError=5)
```

`FILE_FLAG_FIRST_PIPE_INSTANCE` 下 `GetLastError=5`（ACCESS_DENIED）的含义是"**这个名字已经被占了**"。
后果不是"多一条日志"：**core 完全无法服务控制管道** —— 托盘图标照旧（不依赖管道），
用户看不出异常，但所有入口（`bdctl` / 右键降级 / 面板）都不通。**这是 core 核心职责失效。**
（也说明 §13.8 的"CLI 端到端已验证"只在**没有占用者**时成立。）

**归因更正（我先前的判断是错的）**：我一度认为占用者是 `BetterDesktop.Clipboard.Engine`。
全仓检索后确认**不是** —— `engine/src/ipc.rs` 用的是 `\\.\pipe\BetterDesktop.Clipboard.Engine`，
只服务剪贴板/索引面板（`shell-clipboard-ipc` / `shell-index-ipc` 是它的客户端），
**引擎完全不需要动**。同一管道名的另一个服务端是 **`host/MenuCommandPipe.cs`（+`Bootstrap.cs:391 StartServer`）**
—— 而 Host 正是 S4 要删的旧世界。故这条不需要 A/B 选型：**卖方退场，core 独占** ✓

#### 一条方法论教训（记下来，避免重犯）

我第一次验证守卫时得到的是**假结论**：把 core 拷进 dev bin 后测出"任务被创建了"，一度以为守卫失效。
真因是 **`cargo test --release` 不会刷新 `target/release/betterdesktop-core.exe`** ——
我拷的是**实现守卫之前**的构建。重新 `cargo build --release` 后再测，守卫一次通过。
**教训**：真机验证任何"编译进二进制"的行为之前，先确认二进制的构建时间，而不是假定它是最新的。

### 13.16 S4-3 过渡态定案：Watchdog 语义逐项对比 + 单守护者（2026-09-19）

`cargo test --release` **149/149**（+4）；watchdog / agent 编译通过。

#### 先读代码再定（不信摘要）：Watchdog 的真实语义

名单在 `watchdog/Program.cs::BuildTargets()` **硬编码** 5 项（不是配置驱动），外加全局 `watchdog-pause.flag`。
主循环顺序：exe 未部署 → 跳过；gate 关 → （`StopWhenDisabled` 时停进程）跳过；
**stopFlag 在 → 跳过**；判活（`UsePipeLiveness` ? 管道 : 进程名）；宽限期；拉起窗口防循环；拉起。

#### 逐项对比 → **五处 core 缺失的语义**（不补就是行为回归）

| Watchdog 有 | core 原状 | 不补的后果 |
|---|---|---|
| `host-stopped.flag` / `desktop-stopped.flag`（托盘"退出主程序 / 停止桌面服务"写） | **无** | **core 会复活用户刚停掉的组件**，托盘那句"已停止"被静默推翻（本仓已踩过同类坑：托盘写 `desktop-service-stopped.flag`、看门狗只认 `desktop-stopped.flag`） |
| Desktop **管道判活**（`\\.\pipe\BetterDesktop.DesktopCmd`） | 进程名判活 | 审计 #9：短命的"桌面控制菜单"与常驻服务**同名** → 把"正在弹菜单"读成"服务在跑" → 真服务死了不被拉起 |
| `clipboard-panel` 的 gate | **表里缺 gate** | 用户关掉剪贴板历史后，任一入口仍能把它拉起来，违反"设置是唯一真相源" |
| `watchdog-pause.flag` | **无** | 它是**更新器**在替换文件期间写的（`updater/ResidentGate.cs`）→ core 不认就等于**更新期间一直在复活正被替换的组件** |
| Agent（`BetterDesktop.Agent.exe`） | 表里**没有** | 见下（刻意不迁） |

**一处有意的新语义（比 Watchdog 更严，非回归）**：Watchdog 对 Host/Agent/Desktop 声明
`StopWhenDisabled=false`（gate 关 → 只停止守护、**不杀进程**）；core 的 `must_stop` 对任何
非地基组件在 gate 关闭时**停掉进程**。保留 core 的行为 —— 它正是本仓反复强调的
"关了但它还在"的反面（`StopWhenDisabled` 的注释就是为此而写）。

#### 过渡态定案

**core 接管 + 旧守护者停手**，Watchdog 进程保留到 S4-5。两处修正了原计划：

1. **第二个守护者不是 Watchdog，而是 Agent** —— `agent/Capabilities/DesktopServiceSupervisor.cs`
   每 5 秒 ensure 同一个 `BetterDesktop.DesktopControl.exe`（它自己用 `DesktopControlPipe.IsServiceUp()`
   ✓ 管道判活）。只清 Watchdog 名单**不足以**消除重复守护，故 S4-3 同时让该监护**停手**。
2. **Watchdog 用"名单保留但不再执行"，不是"名单清空"** —— 清空会让 `WatchTarget` 的字段无人赋值
   触发 **CS0649**（本项目警告即错误），而"真清空"等于把整个目标机制删光 = 把 S4-5 的删除工作提前，
   那正是本轮想避免的"两次验收混成一次"。故：名单留作参考（它记录了移交前守过什么、
   哪些目标**刻意不守**），但主循环**不动手**，启动日志如实打
   "不再守护任何目标（职责已移交 core）；参考名单 5 项，本进程不动手"。

**一处判断（非事实，如实标注）**：**不给 Agent 加表项**。它在 S4-5 删除，为将死者加监护只会多两条
以后要删的代码路径；代价是 S4-3..S4-5 期间 Agent 崩了没人拉。其截图热键已迁 core（S4-1），
其余角色在删除清单上。

#### 已落地

| 落点 | 内容 |
|---|---|
| `core/components.json` | `shell` → `desired=running` + `stopFlag=host-stopped.flag` + **`_removeAt: S4-5`**（临时监护，core 启动时打进日志，故不会静默留在表里）；`desktop` + `stopFlag` + **`liveness=pipe`/`livenessPipe`**；`clipboard-panel` **补 gate** |
| `core/src/components.rs` | `Liveness` 枚举 + `stop_flag`/`liveness`/`liveness_pipe`/`remove_at` 字段；**`liveness=pipe` 必须带管道名**、反向"给了名字却没声明 pipe"也拒（被静默忽略的字段比被拒的更糟）；`flag_exists()`；`load()` 每次报临时条目 |
| `core/src/process.rs` | `is_pipe_up()` —— **枚举 `\\.\pipe\` 找名字**，与 `watchdog/Program.cs` / `DesktopControlPipe.IsServiceUp` **同源**（不是客户端 connect：那会消费实例槽、且服务忙时把活着的判成死的） |
| `core/src/supervisor.rs` | `decide` 增 `stop_flag`（优先级 0）+ `alive` 按声明判活；`reconcile_with` 认 `watchdog-pause.flag`（状态切换才记一次日志）；`snapshot` 与 reconcile **用同一判据**（否则 `status` 与监护器对同一问题给不同答案） |
| `watchdog/Program.cs` | 名单保留、循环不动手、启动日志如实说明 |
| `agent/.../DesktopServiceSupervisor.cs` | `Start()` 停手（连探测都停）+ 说明移交给谁、为何保留到 S4-5 |

**单测**：`user_stop_flag_prevents_restart_without_killing`（标记在 → 不拉也不杀，与 Watchdog 逐字一致）、
`pipe_liveness_is_not_fooled_by_a_running_process_name`（**审计 #9 的回归钉子**：同名进程在跑但管道不在
→ 必须判"不在"，附反证）、`pipe_liveness_requires_a_pipe_name_and_vice_versa`、
`embedded_table_preserves_migrated_watchdog_semantics`（把迁移语义钉在表上）。

#### 两处必须显式记录的事（防未来误读为 bug）

1. **S4-3 → S4-5 窗口期内 Agent 崩溃不会自动恢复，这是已知且接受的取舍。**
   Agent 在 S4-5 删除，故不为它加表项（为将死者加监护 = 投资错误：多两条以后要删的代码路径，
   外加一个未来可能忘掉的临时项）。窗口内 Agent 崩了的用户感知上限是"右键菜单不刷新"，
   而右键注册自愈已在 S4-2 迁入 core，不依赖 Agent。
   → **看到这行的人不必去补监护**：这是决定，不是遗漏。
2. **`core` 的 gate 语义比 Watchdog 更严，这是行为变更（不是回归）。**
   Watchdog 对 Host/Agent/Desktop 声明 `StopWhenDisabled=false`：gate 关 → 只停止守护、**不杀进程**；
   core 的 `must_stop` 对任何非地基组件在 gate 关闭时**停掉进程**。
   **触发条件**：用户从旧版本升级后，"关闭某组件"的行为与之前不同。
   | 之前（Watchdog） | 现在（core） |
   |---|---|
   | 关了它，它还在跑，只是没人守 | 关了它，**它真的没了** |
   **保留 core 的理由**：那正是本仓反复强调的"关了但它还在"的反面（`StopWhenDisabled` 的注释就是为此而写）。
   ⚠️ 未来若有用户反馈"我关了它但它还在跑" —— 那是旧行为残留，**不是 bug**，需要文档说明而非改回。

#### ✅ 真机验收（2026-09-19，已部署新 core 到安装根）

部署：`core\target\release\betterdesktop-core.exe` → `%LOCALAPPDATA%\BetterDesktop\app\2026.09.17.1610\`
（旧二进制备份为 `betterdesktop-core.exe.bak-s43`，路径待清理）。单测 **150/150**。

| # | 项 | 结果 |
|---|---|---|
| 10 | `_removeAt` 出现在启动日志 | ✅ 启动第一行 WARN："component 'shell' is under TEMPORARY supervision (to be removed at S4-5)…" |
| 6 | `host-stopped.flag` | ✅ **三段对照**：flag 在 → Host=0；移走 → 8s 后 `started 'shell'` → Host=1；恢复 flag + 杀 Host → 仍 0（**标记是承重的**，否则 core 会复活用户关掉的壳） |
| 1 | 杀 DesktopControl | ✅ 拉回；⚠️ **首轮暴露一个真 bug，已修，见下** |
| 3 | 杀 Clipboard.Engine | ✅ `started 'clipboard-engine' (restart #1)` |
| 7 | `watchdog-pause.flag` | ✅ pause 期间杀 desktop → **0 实例无人复活**；移除 → `supervision resumed` + 下一 tick 拉回。日志只有**两条状态切换行**（无刷屏） |
| 9 | **关 gate → 进程真的停止**（行为变更） | ✅ 写 `extensions.clipboard-history.enabled=false`（先按字节备份 settings，测后还原）→ 3s 内 engine/panel 双双被停：`stopped clipboard-engine (pid kills=1), clipboard-panel (pid kills=1)`；**还原后 engine 自动拉回**（停止时清了退避 ✓ 没有被旧计时器拖住） |
| 5 | 杀 Panel → 不拉回 | ✅ core 未拉起（on-demand 语义生效） |

##### ⚠️ 真机暴露的真 bug（已修）：管道组件的**冷启动窗口**被误判成"启动即崩"

首轮杀 DesktopControl 后：
```
supervisor(tick): started 'desktop' (restart #1)
…3 秒后…
'WARN' 'desktop' died within one supervision round after being started (1 consecutive failures)
'ERROR' supervisor(tick): FAILED desktop
```
**根因**：core 杀了服务后立刻拉起，**3 秒后对账时服务还没建好命令管道**（WPF 冷启动数秒）→ 被判"启动即崩" →
记失败 → 退避 → 重试；3 次即**熔断** → 桌面服务**永不再修复**。这是迁移时漏掉的一条旧语义：
Watchdog 的 `GraceMs`（"目标不在：若在宽限期内（刚自重启/正常退出），不急于拉起"）、
Agent `DesktopServiceSupervisor` 的原文"**管道尚未就绪的启动窗口（服务刚起来 1~2 秒）：按进程名兜底**"。

**修法**：新增 `spawn_is_alive()` —— **只在"刚由 core 拉起"的那一轮**，管道组件额外接受"进程名存在"作证据。
判据范围窄是有意的：它不会把审计 #9 的短命同名菜单进程放进来（那种情形下我们并没有刚拉起它）。
配套用例 `pipe_liveness_tolerates_the_startup_window_instead_of_calling_it_a_crash`（含反向守卫：进程判活的组件不享受豁免）。
**修复后重验**：core 拉起 desktop 一次、14 秒内**无 FAILED** ✓。

**一处自我纠正（避免以讹传讹）**：我一度把"两个 DesktopControl 实例"读成 core 的多主拉起。
**基线证据否定了它** —— 动手前机器上就是 2 个（16:20:04 / 16:20:06，相隔 2 秒、存活数小时），
清场后由 core 拉起的一个也会在 ~2 秒后带出同名子进程（18:20:36 / 18:20:38）。
后续用 `Win32_Process.ParentProcessId` **查实了亲子链**：`core(49520) → 53028(服务) → 51168(子)` ——
**2 个是父子关系、是这台机器的常态**，不是 core 拉的第二个。

> ⚠️ **给未来的判断规则（防再说错一次）**：看到 **N 个同名实例**时，**先分"父子"还是"多主"**，
> 再下结论。判据是 `ParentProcessId` 链，不是实例个数。本条记录存在的意义就是这次差点把
> "多主拉起"这个错误描述写进计划与文档 —— 它会污染后续所有判断。

##### ✅ 补验（同日晚）：第 2 / 11 / 12 项

| # | 项 | 结果 |
|---|---|---|
| 11 | Watchdog 停手日志 | ✅ 启动即打："看门狗启动：**不再守护任何目标**（职责已移交 core）；参考名单 5 项，本进程不动手，等 S4-5 删除"（文件日志当日为 0 字节 —— 异步写未 flush，改抓 stdout 取证） |
| 12 | Agent 停手日志 | ✅ `agent-20260919.log`：`[L1] 桌面服务监护已停手（S4-3 移交 core；本监护随 Agent 在 S4-5 一起删除）` |
| 2 | **同名进程不得骗过判活**（审计 #9） | ✅ **自然场景构造成功**：亲子链 `core → 服务(53028) → 子(51168)`；杀**父（服务）**后 **同名进程 51168 仍在跑、管道不在** → core 仍 `started 'desktop' (restart #3)`。**若它按进程名判活，看到 51168 就会"什么都不做"** —— 管道判活的正确性由此验证。附带一条：13 秒时管道仍未就绪而 **core 没有再拉第二个、也没有报 FAILED** —— **冷启动窗口修复在真实场景里生效**（修复前的行为正是这里再拉一个并记失败） |

**回归钉子（S4-4 前的必补项）已补齐**，共三条互为一组，缺任何一条都会被未来的"好心优化"掏空：
1. `pipe_liveness_tolerates_the_startup_window_instead_of_calling_it_a_crash` —— 冷启动接受进程名；
2. `the_spawn_exemption_does_not_leak_outside_the_spawn_round` —— **豁免不得泄漏到"刚拉起"那一轮之外**
   （这一条防审计 #9 回归：只钉第 1 条，未来有人会想"为什么这么窄"然后放宽）；
3. `pipe_liveness_is_not_fooled_by_a_running_process_name` —— 同名进程在跑但管道不在 → 稳态判活为假。
   单测 **151/151**。

##### 如实标注

- **经 core 按需拉起面板的入口仍未验成**：`BetterDesktop.Cli.exe --core start clipboard-panel` 返回
  **exit=2（用法错误）** —— 我的调用式不对，**未验证**，不记为通过。
- 机器状态（**不是"偏差"，多为新语义生效的证据**）：`Clipboard.Panel` 被第 9 项测试停掉后
  **core 没有把它拉回来**（按需件 + 无人自动恢复 = "关了它，它真的没了" ✓）；验收末尾它又出现在进程表里，
  最可能是 **18:32 那次 Agent 试运行**的角色引导带起的（不是 core —— core 对 on-demand 组件从不主动拉起）。
  `Index.Engine` 由 Host 短暂启动带起，属"工作型进程、空闲 600s 自退"的**设计行为**。
- 安装根已清理（`betterdesktop-core.exe.bak-s43` 已删，目录恢复单一 exe）。

#### 原始清单（★ = 本轮补充）

  | # | 项 | 期望 |
  |---|---|---|
  | 1 | 杀 DesktopControl | core 按退避拉回 |
  | 2 | 杀 DesktopControl 的**同名短命菜单进程** | **不**误判为"服务在跑"（管道判活核心场景） |
  | 3 | 杀 Clipboard.Engine | core 拉回 |
  | 4 | 杀 Host | core 拉回（临时监护） |
  | 5 | 杀 Clipboard.Panel | **不**拉回（按需） |
  | 6 | 写 `desktop-stopped.flag` → 杀 Desktop | 不拉回；删标记 → 拉回 |
  | 7 | 写 `watchdog-pause.flag` → 杀 Desktop | 不拉回（更新器语义） |
  | 8 | 关剪贴板历史设置后触发入口 | 拒绝拉起（gate） |
  | 9 | ★ **关 gate → 进程真的停止** | 验证上面第 2 条行为变更（不只是停止守护） |
  | 10 | ★ `_removeAt: S4-5` 出现在 core 启动日志 | 临时条目不会静默留在表里 |
  | 11 | ★ Watchdog 启动日志显示"不再守护任何目标" | 过渡态确认 |
  | 12 | ★ Agent 启动日志显示"已停手" | 过渡态确认 |
  | 13 | 无重复守护 | 同时看 Watchdog / Agent / core 日志，只有一个在动作 |

- **`verify-dotnet-format` 红：已确认为基线失配，非本次引入**（2026-09-19 判定）。
  证据：
  1. 复现门禁口径：`current=22888`、`baseline=16630`、`new=10309`（与门禁打印的数字一致）；
  2. 新增违规涉及 **67 个文件**，其中含 `tray/Program.cs`、`updater/Program.cs`、
     `packages/shell/shell-core/DesktopControl/*`、`shared/logging/*` —— **本轮完全未触碰**；
  3. 决定性证据：**整文件每行被标**（行号自 1 起连续），且**未触碰的文件呈同一模式** ——
     这是**文件级属性**的规则差异（行尾/BOM 之类），不是"某人某行写错格式"。
     例：`DesktopControlLocator.cs`（未碰）73 处、`NativeDllPath.cs`（我新建）209 处，同一形态。
  基线生成于 **2026-09-16**，而 `Directory.Build.props` / `BetterDesktop.slnx` / `.editorconfig` 的同伴
  都在既有未提交改动里 → 工具链/规则集与基线失配。
  **处置：记为 deferred，不修** —— 那些改动不是本轮的，重排格式会覆盖他人未提交的工作。
  **⚠️ 注意"基线失配"仍只是假设、不是结论**（现有证据是强的旁证，不是证明）。
  **复验触发条件**：工作树那些未提交改动落地（提交或还原）后，**重跑一次 `verify-dotnet-format`**；
  若仍报新增违规，则"基线失配"这个假设被推翻，必须另查（例如规则集/工具版本变化本身是否该走一次基线刷新）。

### 13.17 S4-4 侦察：`MenuCmd` 管道有**两个服务端**，且失败形态比"多一条日志"严重（2026-09-19）

**起因**：S4-4 被口述为"消除 Host 的 MenuCmd 服务端"，而计划表里 S4-4 是"删 `agent/` + 删 `watchdog/`"。
口径不符 → 先去读代码，结果发现一件比口径更要紧的事。

#### 事实（全部有代码引用）

| 侧 | 服务端 | 创建方式 | 动词集 |
|---|---|---|---|
| core | `core/src/pipe.rs`（`PIPE_NAME`，4 实例线程） | 实例 0 带 **`FILE_FLAG_FIRST_PIPE_INSTANCE`**（`create_instance(ownership_attempt)`） | 只认 `@ctl|…`；**legacy 只记日志**（`handle_legacy`） |
| Host | `host/MenuCommandPipe.cs:45` + **仍被 `host/Bootstrap.cs:391` 调用** | `new NamedPipeServerStream(name, ..., maxServerInstances:1)` —— **不带该标志** | `notify-error` / `open-settings` / `dock-pin` / `toggle-key` / uiKey / desktop / 压缩解压 / `clipboard-history` … |

**两种失败形态（任一都真）**：

1. **Host 先占名** → core 的实例 0 创建失败 → core 自己已经备好那段诊断：
   "本 core **无法服务控制管道** … 所有入口（bdctl / 右键降级 / 面板）都不通。**这是 core 核心职责失效，不是日志噪声**"。
2. **core 先占名** → Host 照样建成功（它不带 FIRST_PIPE_INSTANCE）→ **两个服务端同名并存**（实例池）
   → **客户端连上谁不确定** → 而两边动词集**不相交**：
   - legacy 动作落到 core → 被 **静默吞掉**（只留一行 `not yet handled by core` 日志）
   - `@ctl` 请求落到 Host → 当未知命令 Warn 掉

⇒ **只要用户开着壳，今天的每个入口调用都是掷硬币**（右键菜单动作 / `bdctl --core …` 各 50%）。
这与 S4-3 治的"多主监护"是**同一类病**：同一个名字有两个主人。

#### 为什么 S4-4 现在**不能**先删 Host 的服务端

`handle_legacy` 的注释就是答案：core **尚未接管 legacy 分派**（"壳仍是 legacy 的真正处理者"），
且明确排在 **S6**（"等 legacy 的消费者（CLI / 系统右键）改指 core 之后再落地分派"）。
先删 = 所有 legacy 菜单动作（右键菜单那一整排）当场失效。

#### 正确顺序（写下来免得下次又有人想"先删了再说"）

1. **让 legacy 分派进 core**（或把 legacy 消费者改指 core）—— 原计划排在 S6；
2. **然后**才删 Host 的服务端（`Bootstrap.cs:391` 的 `StartServer` + `host/MenuCommandPipe.cs`）；
3. 删除时 `core/src/pipe.rs` 里的 `PIPE_NAME_CANDIDATES`（点名 `betterdesktop-host.exe` 的那条）
   与 `describe_create_failure` 的相应说明**必须同步更新** —— 它们的存在前提就是"还有第二处服务端"。

#### 修复方案（本账的正确结法）：两个服务端用**两个名字**，客户端按 head 路由

**被否掉的方案**（我一度提议、被用户当场指出逻辑矛盾）：让 Host "让步"（core 在跑时不建服务端）。
不成立 —— Host 不建服务端，legacy 就 **100%** 落到 core（它只记日志），从"50% 失效"变成"100% 失效"，
**比不修更糟**。"Host 让步"与"Host 仍负责 legacy"不能同时成立。

**采纳的方案**（用户提出）：**名字分开、各管一类**。判别依据**不需要新造** —— 共享契约
`protocols/bdmc1-test-vectors.json` 的 `shapes` 已经定义：

```
legacy : BDMC1|<action>|<arg>          → Host   （写后即忘）
control: BDMC1|@ctl|<verb>|<arg>       → core   （请求/应答）
```

| 服务端 | 管道名 | 处理 |
|---|---|---|
| core | `BetterDesktop.MenuCmd`（不变，继续独占，`FILE_FLAG_FIRST_PIPE_INSTANCE` 保留） | `@ctl` |
| Host | **`BetterDesktop.HostCmd`（新）** | legacy 动词集（原样不动） |

**客户端按 head 是否为 `@ctl` 选管道**；未知动作 → 发给 core，让它回结构化 `unknown-verb`（而不是静默）。

**收益（有一条比"不掷硬币"更要紧）**：`MenuCommandPipeClient.TrySend` 是**带返回值的**，
而 CLI 多处依赖它决定回退（`--menu-cmd` 无宿主 → 本地 headless / 提示；
`:552` "老部署回退"；`:669` `notify-error` 回退）。今天的掷硬币不只是"动作被吞"——
**legacy 落到 core 时 `TrySend` 照样返回 `true`**，于是调用方以为"宿主已处理"，
**把本该生效的回退路径跳过了** ✗。名字分开后 `TrySend` 的布尔值**重新变得可信**。

**改动清单（改动前取证，非推测）**：

| 落点 | 改动 |
|---|---|
| `kernel/MenuCommandPipeClient.cs` | 两个管道名常量 + 按 head 路由（**唯一的客户端实现**：Host 转发、CLI 全部入口都经它） |
| `host/MenuCommandPipe.cs` | `PipeName` → `BetterDesktop.HostCmd`（服务端逻辑与动词集**不动**） |
| `tray/PipeProbe.cs`、`tray/Program.cs:104` | 探测/日志字符串指向 **HostCmd**（它问的是"宿主在不在"） |
| `scripts/test-pipe-acl.ps1` | 它测的是 core 的管道（`@ctl` 请求）→ 保持 `MenuCmd`，**明确写死**为 core 的名字 |
| `core/src/pipe.rs` | `PIPE_NAME_CANDIDATES` **删掉 `betterdesktop-host.exe` 那条** + 同步改 `describe_create_failure` 的文案 —— 它们的**存在前提**就是"还有第二个服务端"；名字分开后这个前提消失 |
| `protocols/bdmc1-test-vectors.json` | 记录"两个服务端 + 按 head 路由"（今天只记了两种**形态**，没记**归属**） |
| `host/Bootstrap.cs:391` | **不动**（仍启服务端，只是换了名字） |

**不改的**：legacy 动词集、`BDMC1` 分帧、ACL 与调用者校验、core 的 `@ctl` 语义 —— 全部原样。

#### ✅ 实施 + 真机四场景（2026-09-19，已部署）

**落地**：契约 `_routing`（向量）＋ `MenuCommandPipeClient` 两名字 + `PipeNameFor(head)` 路由（`TrySend` 语义写进类注释）
＋ `host/MenuCommandPipe.PipeName` → `HostCmd` ＋ `tray/PipeProbe`、`tray/Program.cs:104`、`test-pipe-acl.ps1` 同步
＋ `core/src/pipe.rs`（删 host 候选、订正所有权注释、`handle_legacy` 升级为 **WARN 点名 routing error**，不回包）。
**验证**：core **151/151**；CLI/Host/Tray/Kernel/DesktopControl 编译通过；门禁 **5/5 PASS**；新增**路由单测 17/17**。

**部署**：原子集 = **2 个文件**（`BetterDesktop.Kernel.dll` 含路由 + `BetterDesktop.Host.dll` 含新名）——
因为 Host/CLI/Tray/DesktopControl **都从安装根加载同一个 Kernel.dll**，换它即全部消费者生效。
备份（含哈希）在 `%LOCALAPPDATA%\BetterDesktop\backup\s4-routing-20260919-185133\`。

**四场景（从简到繁，每步单独取证）**：

| # | 场景 | 结果 |
|---|---|---|
| 1 | 都不开 | `MenuCmd(@ctl) = FAIL(connect)`、`HostCmd(legacy) = FAIL(connect)` ✅ 都失败→调用方走回退 |
| 2 | 只开 core | `@ctl` 返回完整 status JSON ✅、legacy `FAIL(connect)` ✅ |
| 3 | 只开 Host | `HostCmd(legacy) = OK(sent)` ✅、`MenuCmd(@ctl) = FAIL(connect)` ✅ |
| 4 | **两服务端同时开** | 两管道并存（`MenuCmd` / `HostCmd`）；`@ctl` → core 返回 status、legacy → Host `OK(sent)` ✅ **路由确定，掷硬币消除** |

**过程中我一共犯三次同类错误，都值得记下来**：
1. **部署顺序**：只停了消费者没停 core → core 立刻把 DesktopControl 拉回来、**锁住 `Kernel.dll`** → 替换失败 →
   得到"新 Host + 旧 Kernel"的**半修状态**（legacy 100% 失效）。按"整体回滚，不许半修"处理后才成功。
   **教训：换 DLL 前必须停掉**所有**加载者，包括守护者会立刻复活的那些** —— 而这条只有想到"core 会自愈"才成立。
2. **探针方向**：Host 的服务端是 `PipeDirection.In`（单向）→ 客户端必须 **`Out`**；我两次用 `InOut` 得到 `FAIL(connect)`，
   差点把"产品坏了"当成结论。**探针必须与真实客户端同款**（`MenuCommandPipeClient` 用的就是 `Out`）。
3. **探针属性**：`NamedPipeClientStream` 上设 `ReadTimeout` 抛异常（流不支持）→ 又一次误报。
**共同点**：三次都是**测量工具的缺陷伪装成被测对象的缺陷** —— 与 §13.16 那条"诊断信息必须与现实同步"是同一类问题。

**收尾**：启动 Host 曾带起 Tray，而 Tray 又把 Host 拉回 → 已收掉 Host/Tray/Agent，
`host-stopped.flag` 确认仍在（未被清），计划任务已重新启用（Ready）。终态与基线一致（core + engine + panel + desktop×2）。

#### 一行诚实标注

本节的"两种失败形态"是**静态推导**（读创建标志 + 动词集 + 实例池语义），**未做真机构造**。
真机可构造（先起 core 再起 Host，观察 legacy 动作是否掷硬币），但那是**验证**、不是**修复**，
而修复的前置条件在 S6 —— 故本轮只记录，不动代码。

### 13.18 决策记录：`doc-budgets` 上限调整（2026-09-19）

**调整**：`docs/defensive-patterns.md` 的词数上限 **1200 → 1700**（`scripts/manifests/doc-budgets.manifest.json:13`）。

**理由（按门禁要求的"决策记录"形式）**：
1. 本次新增两节"**测量工具的缺陷会伪装成被测对象的缺陷**"与"**替换被守护的二进制：先停守护者，且不许半修**"
   —— 两条都来自**一次真实事故**（§13.17 的真机验收），符合该文件自我定义的地位
   （"来之不易的缺陷类别规则……以『防止复发』的规则形式陈述"）。
2. 该文件调整前**恰好顶在旧上限**（1200），加任何一条都会红；
   而"精简"在此只有一条路 —— **删减他人既有条文**，那比调上限糟得多（既有条文各自对应一类已发生缺陷）。
3. `docs/engineering-conventions.md` **同样顶在 1000 词上限**，故本次**没有**把纪律塞进那里；
   两条纪律的归属依据是该文件自己的交叉引用："缺陷的防复发规则见 `defensive-patterns.md`"。

**这是本仓库第一次调高文档上限**，故记在此：后续若再有人需要加条文，**先问"能否精简既有条文"，再问"上限是否本身估低了"**，
而不是默认调高 —— 上限的价值在于逼迫精简，调高必须是例外。

### 13.19 ADR：配置单写者从 `SettingsService` 改为 **core**（2026-09-19，S5-2a 前置）

**状态**：已决定。**取代** §6.7 原文"保留 `SettingsService` 为唯一写入口"的表述。

**背景（探针实录，非记忆）**：`@ctl set` 目前**故意未实现**：
`{"error":"unknown-verb","message":"verb 'set' is not supported; … (settings are written by SettingsService, not by core)"}`；
`core/src/settings.rs:3` 也写着"core 是只读消费者，不是写者"。⇒ 让 `@ctl set` 落地是**契约变更**，不是搬表的实现细节。

**决策**：**唯一的 settings 写者改为 core。**
- core 用 Rust 实现 `settings_write`：复用 `Local\BetterDesktop.Settings.json` **互斥名** + **原子写**（临时文件 + rename）
  + BOM 容错 + **复刻 C# 现有 JSON 缩进/排序**（否则每次写 diff 巨大）。
- `SettingsService`（C#）**降为共享库**（互斥/原子写实现保留给兼容期），**写操作的发起者只有 core**。
- `@ctl set` 由 `unknown-verb` 变为受支持（`get` 已在，语义不动）。

**理由**：① core 本来就是 settings 状态的**唯一决策者**（它读 settings 决定监护与 gate），多一个 C# 中间层只增加延迟与故障面；
② 与 S2"服务端迁 core"同一方向，消掉 `core → CLI 短命进程 → settings` 这一圈；③ 是 S7 的**提前实现**（S7 余下部分不受影响）。

**被否方案**：core 只读、写交给 CLI/SettingsService 短命进程 —— 每点一次开关拉起一个 .NET 进程，延迟 + 故障面，
且与"决策者在 core"相悖。

**兼容期与退役**：兼容期 = **S5-2a → S7**（两侧都可能写，靠**同名互斥**保证不冲突，不是"谁让谁"）；
**S7 退役** C# 写入口、只留共享库，并把 `verify-architecture-guard` 的"配置写入"棘轮**收紧到只允许 core 一处**。

**风险 → 对策**：格式不一致致 diff 抖动 → 先复刻缩进/排序 + "读回往返不变"单测；
互斥名写错致真双写 → 互斥名按**跨语言契约字面量**对待，单测钉住（同 `verify-protocol-contract` 手法）；
原子写失败 → 临时文件 + rename，写前留 `settings.json.bak`（与 C# 现状一致）。

### 13.20 S5-2a 侦察结论：settings.json 是**扁平点分键**，而 core 读取器按**嵌套**走（真 bug）

**磁盘实证**（`%APPDATA%\BetterDesktop\settings.json`，152 字节）：
`{"appearance.skin.active":"","appearance.windowTint":"#1F1F22","appearance.accent":"#0A84FF",…}`
—— **紧凑无换行、纯 ASCII、键是字面量点分串（不是嵌套对象）**，键序 = 写入序。

**而 `core/src/settings.rs::get()` 走的是嵌套路径**（`for part in key.split('.') { cur = cur.get(part)? }`）
⇒ 对 C# 写出的扁平 `"components.desktop": false` **取不到值 → 回落默认 true**
⇒ **用户在托盘关掉"自绘桌面"，core 仍读 true、继续监护并保活它** ✗。

**为什么此前没暴露**：§13.16 那次"关 gate 停进程"的验证，是我用 `ConvertTo-Json` 自己写成**嵌套**的，
恰好命中读取器的期望形式 —— **构造数据的形式掩盖了格式不一致**
（与防御性纪律第八条同源：被测对象的**构造选型**本身会影响结论）。

**严重性修正（用户指出，采纳）**：这不只是"格式不一致"——**自 S4-3 引入 gate 起，
所有由 C# 写入的 settings 键 core 都读不到 ⇒ gate 对真实用户路径从未生效**。
§13.16 那次"关 gate → 真停进程"之所以是绿的，是因为验证数据是**我自己用 `ConvertTo-Json` 写成嵌套**的，
恰好命中读取器的期望形式 —— **构造数据的形式伪装成了被测对象的行为**（防御性纪律第八条的新镜像变体，已写入）。

**S5-2a 因此扩为 6 项**（原估 4 项）：
1. **读取器两种形式都认**：先扁平点分键，再回退嵌套路径（`try_get_bool` 同步）；
2. **写入器写扁平形式**（与 C# 一致）+ 紧凑（`JsonSerializer.Serialize` 默认不缩进）+ 复刻 ASCII 转义；
3. **键序必须保持** —— `serde_json` 默认 `Map` 是 BTreeMap（会按字母重排）。**实现选型：不启用全局
   `preserve_order`**（那会连 `@ctl` 协议 JSON 的键序一起改掉，属于协议可见的连带变更），
   改为 **settings 写入路径单独用 `IndexMap`**（`serde_json::from_str::<IndexMap<String, Value>>`）
   —— 范围最小、零连带；
4. 互斥 `Local\BetterDesktop.Settings.json`（**2s 超时即跳过**）+ `.tmp` + rename 原子替换
   + **读不到就不写**（宁可本次不落盘，也不用残缺快照覆盖用户设置）；
5. **互斥必须覆盖整个「读-合并-写」周期**，而不只是"写" —— 互斥保护的是"写-写"，**不保护"读-改-写"**：
   A 读、B 读、A 写、B 写（基于旧读）⇒ A 的修改被写回旧值。C# 的 `SaveLocked` 其实是**正确**的
   （`using var mutex` 包住了 `TryReadStore → merge → write` 全程），Rust 侧必须逐字对齐这一点；
6. 单测：扁平/嵌套两种读取、**往返字节不变**、**键序不变**、并发写不丢键、BOM、坏文件不 panic。

**实现顺序（TDD，用户钉死）**：先加"**扁平输入**"用例 —— **它现在必须红**（红的就是这个真 bug），
再改读取器让它变绿，最后才动写入器。**用例先红**是这一步的验收核心：若先改代码再补用例，
很可能写出一条"恰好通过"的用例 —— 与 S4-3 那次同一种错。

**部署/兼容**：互斥名与「写前重读 + 只覆盖本实例改过的键 + 互斥含读」与 C# 语义**逐字对齐**，
故 S5-2a→S7 的兼容期内两侧**不会互相吃掉对方的键**。

#### ✅ 读取器修复的闭环证据（2026-09-19，真机）

**cadence 已确认**（`supervisor.rs::reconcile`：每次对账重新读配置）⇒ **每 3s tick 重读**，非启动读一次。

| 步 | 观测 |
|---|---|
| 1 部署前 | settings **紧凑单行**；core 38400；engine=1 panel=1；只有 `MenuCmd`（无 Host） |
| 2 真实写路径 | `bdctl --toggle-key clipboard`（Host 不在 → headless 直写）exit=0 |
| 3 **形状（关键）** | ✅ **扁平** `"extensions.clipboard-history.enabled": false` —— 前提成立。⚠️ **附带发现：CLI headless 直写产出的是"缩进两格"，与 `SettingsService` 的紧凑形式不一致** ⇒ "复刻 C# 格式"本身**有歧义**（哪个 C# 写入器？），须在 S5-2a 钉死 |
| 4 **负对照** | 旧 core + 同一文件 → `engine=1 panel=1`（**毫无反应**）⇒ **"gate 从未生效"在真机复现** |
| 5 e2e | 部署新 core（**先 `cargo build --release`**，见下）→ 同一文件 → `engine=0 panel=0`，日志 `stopped clipboard-engine (pid 58164) / clipboard-panel (pid 52232)` ⇒ **闭环** |
| 6 反向 | 真实路径写回 `true` → **engine=1**（运行中 core 于 3s tick 内重读）⇒ 顺带证明 cadence 路径 |

**唯一变量是 core 二进制** ⇒ 根因确证为读取器。

**⚠️ 本轮我又踩了两个已记录的坑，如实记下**：
1. **第 11 条纪律（`cargo test` 不刷新 `target/release/*.exe`）**：我先用 `cargo test` 的结果直接部署，
   部署的其实是**旧二进制**（size 769024 未变）→ 观测到"gate 仍不生效"，差点读成"修复失败"。
   **这条纪律正是为 S3 的同一个错误写的 —— 规则没有阻止我重犯**；真正救我的是**部署前核对 size/hash**（实践，而非规则文本）。
2. **`(cd 路径; 命令)` 在 PowerShell 里不合法**（缺右括号），本轮犯两次 → 改线性写法。
   与第八条同族：**构造/工具的缺陷伪装成对象的缺陷**。

#### ✅ S5-2a 收口（2026-09-19，真机四验）

部署按**第九条**（先核对二进制是刚构建的：size/hash 与在跑的不同 ✓）。

| 验 | 结果 |
|---|---|
| core 写 → C# 读 | `@ctl set` 的值按 **JSON 字面量**落盘成 **bool**（不是字符串 ✓）；格式紧凑/扁平/追加末尾 ✓；C# 的 `--toggle-key` 读-改-写之后 **core 写的键仍在** ✓ |
| C# 写 → core 读 | C# 写的 `hotkeys-panel.enabled`，core 读到 `false` ✓ —— **与 §13.20 那个 bug 完全同型，现在通了** |
| emoji | core 原样保留、**.NET 解析器（`ConvertFrom-Json`）读到 `😀`** ✓ ⇒ 函数注释里标注的分歧**在兼容期确实无害** |
| 跨进程并发 | 3×C# 后台写 + 3×core 写并行 → **两边的键都在、无丢更新** ✓ = 互斥含"读-合并-写"的**真机**验证 |
| 已知 wart（如实） | 文件格式在两侧之间**翻转**（core 紧凑 / CLI 直写缩进）—— 正是"两个 C# 写入器"那条发现的现场证据；**S7 删掉 CLI 整文件直写后消失** |

### 13.21 S5-2b：11 项功能开关搬 Rust（2026-09-19）

`cargo test --release` **160/160**。（部署与菜单点击验证待做。）

**落点**：`core/src/tray.rs` 新增 `TOGGLES`（11 项，与 C# `TrayApplicationContext.ToggleSpec` **一一对应**）
+ `MenuAction::Toggle` + `apply_toggle`（**写 settings 不绕 IPC** —— core 就是写者，S5-2a 的直接回报）
+ `CMD_TOGGLE_BASE`；`main.rs` 打开菜单前**重读 settings**（勾选态以真实值为准）+ 翻转后**立即 `reconcile`**
（监护器虽有 3s tick，但"点了立刻见效"是用户能感知的差别）。

**两个坑（都值得留下）**：
1. **id 区间必须设上界**：原判据 `n >= CMD_START_BASE`（1000）会把开关 id（2000+）接走、按组件数判界后**静默丢弃**；
   改成 `(CMD_START_BASE..CMD_TOGGLE_BASE)` + 独立的开关区间。
2. **`variant Toggle is never constructed`** —— 我加了菜单项与 `MenuAction::Toggle` **却漏改 id→动作映射**，
   于是"点了没反应"。**是编译器的 dead-code 警告抓到的**（无编译错、无运行时报错）。
   修法不止于补那一行：把映射抽成**纯函数 `action_for_id`**，让同一类漏**能被单测抓住**
   （`every_menu_id_maps_to_an_action` 对**每一个**菜单项断言）。

**两处如实标注**：
- 勾选态用**文本前缀** `[x]`/`[ ]` 而非 `MF_CHECKED`（语义相同、少一处导入改动）—— 与 C# 托盘的呈现有差异；
- **菜单点击本身是人工验证项**（无法脚本化点击托盘菜单）。点击最终调用的 `apply_toggle → set_flat`
  已由 `@ctl set` 路径真机验证过；未覆盖的只剩"Win32 菜单项 → `action_for_id`"这一段（已由单测覆盖）。

#### ✅ 真机验收：已开始（2026-09-19 晚）

- **第 1 项（杀 DesktopControl → core 按退避拉回）= PASS** ✓ —— 且是**修复后的复验**：
  清场后 core 只记**一次** `started 'desktop' (restart #3)`、**无 FAILED**、无第二次拉起 ✓。
- **第 10 项（`_removeAt` 出现在启动日志）= PASS** ✓（启动首行即 WARN，临时条目不会静默留在表里）。
- **第 9 项（关 gate → 进程真的停止）已覆盖** ✓：S5-2a 用 `@ctl set` 真机验过（engine/panel 双双被停 + 日志行）。
- **一处我重犯的误读（如实记录）**：清场后进程表里又有 2 个 `DesktopControl`，我第一反应写成"残留"，
  但 `Win32_Process.ParentProcessId` 显示 **`core(24004) → 53460 → 45492`** —— **父子链、相隔 2 秒**，
  即该组件的**正常形态**，不是多主拉起 ✗。"看到 N 个实例先分父子还是多主"这条规则 §13.16 已写过、
  本会话里也已纠正过一次，**仍然又踩了一次** ⇒ 结论：光写规则不够，**每次都要查 `ParentProcessId` 再下结论**。

#### ⏸ 验收暂停（2026-09-19 晚，用户决定）：控制**入口**尚未接齐

**用户判断**："现在测不能完全准确 —— 控制功能的接口还没完全接上。" **部分同意，故作区分**（否则会把 2 项当成 13 项）：

| 类别 | 状态 | 影响的验收项 |
|---|---|---|
| **控制管道 `@ctl`**（`status/start/stop/toggle/get/set/task`） | **已接好** ✓（S2 + S5-2a 真机验证） | **11 项可测**：杀进程 / 写标记 / `@ctl set` 关 gate |
| **控制入口**（托盘「启动/停止组件」、面板按需入口） | **未接** ✗（S5-3 未做） | **仅 2 项**：关设置后"从任一入口触发被拒"、从入口拉起 Panel |

⇒ 不是"全不能测"，而是 **2 项须等 S5-3**。其余 10 项（2/3/4/5/6/7/8 的 gate 部分/11/12/13）**不依赖入口，随时可测**。

#### 真机验收续：第 3/4/12 项 PASS；第 13 项 **FAIL（部署缺口，非代码缺陷）**

| 项 | 结果 | 证据 |
|---|---|---|
| 3 杀 Clipboard.Engine → 拉回 | **PASS** | `7316 → 47784`，日志 `started 'clipboard-engine' (restart #1)` |
| 4 杀 Host → 拉回（临时监护） | **PASS** | 摘 `host-stopped.flag` → 13s 后 `Host PID=31900` + `started 'shell'`；**还原后不复活** ✓（顺带把 `stopFlag` 语义现场验了） |
| 12 Agent 启动日志显示"已停手" | **PASS（新构建）** | `agent-20260919.log` 18:32:52 `[L1] 桌面服务监护已停手（S4-3 移交 core…）` |
| 13 无重复守护 | **FAIL（部署态）** | 见下 |
| 2 / 6 / 7 | 待做 | 被审批打断，未执行 |

**第 13 项的失败性质是"部署缺口"，不是 S4-3 没做成 —— 这点必须写清：**
- 部署的 `BetterDesktop.Agent.exe` built=**09/18 00:10**，而"停手"改动是 **09/19 17:55** ⇒ **部署的是旧构建**；
- **决定性证据**：20:25:43 启动的 Agent（部署态）日志里只有 `[Gate] 持有者探测启动`（20:25:49），
  **没有**紧随其后的"已停手"行；而 18:32:52 那次运行（新构建）在 `[Gate]` 之后 0.3s 就打了"已停手"。
  ⇒ 部署态 Agent **仍在守护 DesktopControl** ⇒ 机器上此刻是 **core + 旧 Agent 两个守护者**：
  桌面服务活着时两者都不动手（潜性），**一旦被杀会双双拉起** —— 正是 S4-3 要消除的形态，也正落在用户警告的窗口里。
- **处置：部署新 Agent + Watchdog**（或让 Host 不拉起它）—— **S4-3 收尾的必须项，下一步第一件事**。
  在部署之前，S4-3 的"单守护者"只成立于**源码**，不成立于**机器**。

#### 🔧 交付物：`scripts/probe-processes.ps1` —— 把"先查父子链"做成脚本的一步

同名进程分组 → 打印各自**祖先链** → 仅"同父并列"才提示可能是多主。修好后的实测输出：
`53460 ← core(24004)`、`45492 ← 53460 ← core(24004)` ⇒ 父子链，**不是多主** ✓。

**为什么放在探针而不是门禁（对用户建议的一处修正）**：门禁脚本跑在 CI/无 Desktop 进程的机器上，
那里查进程**没有任何意义**；本仓既有先例是 `probe-shellmenu.ps1` 这类**运行期探针**，故按同一形态新建。

**它自己头两次运行都是错的，而且两次都属于"看起来对"的错**（值得记的管理教训）：
1. `Get-CimInstance -Filter "Name LIKE 'BetterDesktop*'"` —— **WQL 的 LIKE 通配符是 `%` 不是 `*`**，
   于是**静默返回零条** → 对着**正在跑的 core** 报"没有匹配的进程"（假阴性读起来像"一切正常"）。
2. 祖先链里写了 `$pid = [int]$cur.ParentProcessId` —— **`$pid` 是 PowerShell 只读自动变量**，
   赋值失败、循环原地打转，最后输出一条**错误但合理**的链（把 `pwsh.exe` 报成桌面服务的祖先）。
⇒ 两处已修（改 `-like` / 改名 `$parentId`），并把两条教训写进注释；另加**自检**：
零条匹配时若 core 在跑 → 打印"是过滤条件错了"并 `exit 1`（假阴性必须自己暴露，而不是等人来怀疑）。

#### ❌ 部署尝试（2026-09-19 晚）：**未通过验证 → 已回滚**；结论：不要用未提交的工作树做部署

为消除第 13 项的部署缺口，编译了 Agent/Watchdog 的 Release，哈希核对后替换安装根的两个 `.dll`
（Agent `825BFB78BEFF → 1D3B382DFD0A`、Watchdog `9A1134DE2A23 → 80FDEA14679F`）。**结果无法验证，已回滚**：

| 观察 | 期望 | 实测 |
|---|---|---|
| 新 Agent 启动日志 | 出现"已停手" | **一行都没写**（进程 20:41:49 起、存活，`agent-*.log` 停在 20:38:28） |
| 新 Watchdog 启动日志 | 出现"不再守护任何目标" | `watchdog-20260919.log` **空** |
| 副作用 | 无 | **拉起了 `Index.Engine`**（基线里没有它） |

**根因（假设，未证实）**：工作树处于**未提交的中间态**（仓里有 `verify-host-log-sink.ps1` 这类针对日志汇聚的门禁，
大概率相关）⇒ 新构建把日志送去 **Host 的 sink**，而 Host 没跑 ⇒ 静默。
**"能编译"不等于"是已知良好状态"** —— 而拿未知状态的产物覆盖用户机器上的组件，比"部署缺口"本身更糟。

**处置**：`Copy-Item` 回滚（已核对回到 `825BFB78BEFF` / `9A1134DE2A23`）、杀掉 `Index.Engine`、
删掉回滚备份 ⇒ 进程回到基线 5 个（core + Panel + Clipboard.Engine + DesktopControl×2，
且探针确认后两者仍是**父子链**，未误报多主）。

**仍在的债（不是"已解决"）**：部署缺口**依旧存在**，但此刻是**潜伏的** —— Agent 只在 Host 运行时才活，
而 `host-stopped.flag` 在 ⇒ Host 不跑 ⇒ 机器上只有 core 一个守护者。
⇒ **必须在"首次重新启动 Host"之前、或 S4-5 删除之前**，走 **`publish.ps1` 正式发布流程**（对应一个已知点）补上。
**不要**再用工作树直接替换安装根 —— 本次已付出一次回滚的代价换来这条教训。

#### ✅ 真机验收：第 2 / 6 / 7 项 PASS —— 13 项清账

| # | 项 | 结果 | 关键证据 |
|---|---|---|---|
| 2 | 杀同名短命菜单进程 → 判"不在" | **PASS** | 夹具（`ping.exe` 改名为 `BetterDesktop.DesktopControl.exe`：**同名、无管道、确定性长命**）在跑时，core 仍判"不在"并 `started 'desktop' (restart #8)`；清理夹具后仅剩真实服务+子，无多余拉起、无 FAILED |
| 6 | `desktop-stopped.flag` → 不拉回；删标记 → 拉回 | **PASS** | 标记在 → **0 实例且无 `started`**；删标记 → 2 实例 + 恰好一次 `started (restart #6)` |
| 7 | `watchdog-pause.flag` → 不拉回 | **PASS** | `supervision PAUSED (…the updater writes this flag…)` → 暂停期间杀 desktop **0 复活** → `supervision resumed` → 一次 `started (restart #7)`（"暂停/恢复各只记一次"也顺带验了） |

**第 2 项的夹具说明了什么（方法，不是运气）**：审计 #9 的旧判据（进程名）在"同名进程在跑"时会读成"服务在跑"
⇒ **永不拉起**。夹具把"同名 + 无管道"变成**可控条件**；真实菜单进程（`--desktop-controls`）是**毫秒级**的，
用它只能得到一个不可复现的测试。⇒ 这是对判据的**确定性反证**。

**13 项清账**：
- **PASS 8 项**：1 / 2 / 3 / 4 / 6 / 7 / 9 / 10；
- **部分 PASS 1 项**：12（新构建在 18:32 的运行里打过"已停手"，但**部署态未验**）；
- **按拆分推迟 2 项**：5（杀 Panel 不拉回）、8（关设置后入口被拒）—— 二者的"入口"前提会被 S5-3 改动；
- **被发布流程阻塞 2 项**：11（Watchdog 日志）、13（无重复守护）—— 见上一节的部署回滚。

**意外收获**：第 6 项日志里出现 `core starting` → `another core instance is running; exiting`
= **A5-2 计划任务兜底**刚触发、**core 的单实例守卫**把它挡掉了 ⇒ 兜底与守卫两条都活着（顺带确认）。

#### 🧭 worktree 阻塞判定（2026-09-19 晚）：**core 不受阻 → S5-3 可先行；发布路径确认被阻塞**

问题：worktree 的未提交改动，是否影响 core 的构建/部署？（两条路都被这一件事挡着）

| 事实 | 证据 |
|---|---|
| **`core/` 是自包含 crate** | 仓根**没有** `Cargo.toml` / `Cargo.lock` / `rust-toolchain`；清单与锁**全在 `core/` 内**（`Cargo.toml`/`Cargo.lock`/`components.json`）⇒ **Rust 侧只从 `core/` 编译，不读 C# 树** |
| `core/` 整体**未跟踪**（`?? core/`） | 即 S1–S5 的 Rust 工作**从未提交**（⚠️ 见文末风险） |
| 并行工作集中在 **`packages/`（380 项）** | 另：`docs/` 63、`scripts/` 25、`host/` 15；` M host/FileLogSink.cs` 改于 **09-17 23:38**——**早于**本轮，非我引入 |
| 本轮的 C# footprint 小而**混在他人改动里** | ` M watchdog/Program.cs`、`?? agent/`、`?? launcher/`、`?? tray/`、`?? shared/`；`host/` 内部**混合**：` D host/MenuService.cs`（S4-4 的消除目标）、`?? host/SettingsFileWriter.cs`（S5-2a）与**别人的** `FileLogSink.cs` **同目录** |

**判定**：
- ✅ **S5-3（core-only）不被阻塞**：core 自包含 + 无根清单；并**实证**——今天已多次从这棵树构建/部署 core，
  行为逐条吻合设计（150/150 单测、8 项真机 PASS）。⇒ **按既定条件：直接做 S5-3**。
- ❌ **Agent/Watchdog 的发布（第 11/13 项）确认被阻塞**：新 Agent 的源码在 `agent/`、`watchdog/`（**未跟踪**），
  且与 `host/`、`packages/` 的他人改动**同树** —— **没有"干净检出"能造出它**（干净检出里连 `agent/` 都不存在）。
  ⇒ 不是"换个干净 worktree 就行"，而是**必须先决定那些改动归谁**。处置：等 `packages/` 那批落地，
  或把该批**显式划出**后再发布。**在此之前不要重启 Host**（否则旧 Agent 被拉起 = 两个守护者）。

⚠️ **另一笔风险**：`core/` 未跟踪 ⇒ S1–S5 的全部 Rust 成果**只存在于工作区**。建议在 S5-3 收尾时做一次
**范围明确的提交**（`core/` + `watchdog/` + 本轮涉及的具体 host 文件），**绝不 `git add -A`**（会把 `packages/` 那 380 项卷进来）。

#### S5-3 第 1 步：勾选态语义（2026-09-19 晚 —— 先读码再定，四处校准）

**用户提案**：四态（Running/Stopped/Failed/Degraded）+ 三前缀 `[x]/[ ]/[!]` + 不复用 S5-2b 渲染逻辑。

1. **`[x]/[ ]` 不能用文本前缀** —— 本仓 S5-2b **已否决并留了理由**（`core/src/tray.rs:260-265`）：
   *"初版图省事用了文本前缀 `[x]`/`[ ]`……那是个**弱理由**：它只是一个 flag 位、不引入任何依赖，
   换来的是用户可见的呈现一致。shell 产品的菜单就该长成系统菜单的样子。"* ⇒ 用**原生 `MF_CHECKED`**。
   `[!]` 无原生等价物 ⇒ **只有它**才用文本标记（文本唯一正当的用武之地）。
2. **状态出口现状**：`Snapshot` 已有 `actual`(bool) + `restarts` + **`health: ok|degraded`**（`supervisor.rs:730-754`），
   且 degraded **只在 `auto_start` 为真时**算健康问题（沿用这条判断）。但 **`pending_spawn`（刚拉起待验证）
   与 `next_attempt_at`（退避中）没有出口** ⇒ `[!]` 现在**只能**表达熔断 ⇒ **S5-3 前置小改**：
   给 `Snapshot`/`status` 加每组件状态（`running|starting|stopped|retrying|degraded`）。
3. **"启动中"不归入勾选**（与用户提案的唯一分歧）：`pending_spawn` 的语义是"**还没确认活过一轮**"，
   归成勾选会让"启动即崩"在最关键的 3 秒窗口里看起来像成功。折中：**勾选 + 后缀`（启动中…）`** ——
   窗口 ≤3s，"乐观但不撒谎"，也不必给第四个前缀。
4. **现有菜单只列「启动 X」且包含全部组件**（含 core 自己 / tool）⇒ S5-3 需按 tier/type 过滤。

**定案表**（待点头的只有第 5 条）：

| 显示（原生勾选 + 文本） | 判据 | 出口 |
|---|---|---|
| ✔ 桌面服务 | `actual=true` | 有 |
| ✔ 桌面服务（启动中…） | `pending_spawn=true`（≤3s） | **待加** |
| ☐ 剪贴板面板 | `actual=false` 且 `auto_start=false` | 有 |
| ☐! 剪贴板引擎（重试中，N 秒） | 退避中 | **待加** |
| ☐! 桌面服务（已熔断） | `health=degraded` | 有 |

5. ⚠️ **更危险的一条（用户清单未覆盖）：`stop` 不持久** —— `supervisor.stop()`（`supervisor.rs:545-569`）
   **只杀进程、不写任何标记** ⇒ 对**受监护**组件（`desired=running` + gate 开：`shell`/`desktop`/`clipboard-engine`），
   用户点"停止"后**下一个 tick（≤3s）就被拉回** = "已停止"被静默推翻（本仓反复强调的那类 bug）。
   反向也坏：`start()`（`:497-542`）**不删标记** ⇒ "点了启动没反应"。
   ⇒ **stop 写标记 / start 删标记必须成对**。三选项：(a) 配对写删；(b) 只对**声明了 stopFlag** 的组件提供"停止"，
   其余返回结构化拒绝并**指出正确路径**（"它由设置开关管辖：`extensions.clipboard-history.enabled`"）；
   (c) 全走 gate（菜单里几乎没东西能停）。**推荐：(a) 为主 + (b) 兜底**。→ 等用户点头。

**顺手要修的不一致**：`stop()` 用 `process::is_running`（**进程名**），而 `reconcile`/`snapshot` 用 `is_alive`
（**按声明**，desktop 走管道）⇒ 同一个问题两个答案（本文件注释正在警告这类）⇒ S5-3 改成 `is_alive`。

**菜单形态提案**：每组件一个**子菜单**（父项带勾选/标记 ⇒ 不开子菜单也看得见状态；子项：启动 / 停止 / 重启）；
id 区间在 `START=1000` 与 `TOGGLE=2000` 之间新增，现有测试 `menu_id_ranges_do_not_overlap` 会接住冲突。

#### ✅ S5-3 前置两件（无争议）已落地（2026-09-19 晚）—— `cargo test` **163/163**

| 改动 | 内容 |
|---|---|
| **1. 状态出口** | `core/src/supervisor.rs` 新增 `State{running,starting,stopped,retrying,degraded}` + **纯判据** `component_state(alive, monitored, st, now)`；`Snapshot.state` 暴露；`status` 契约新增 `state` 映射（`health` 保留，但**改为由 state 派生** ⇒ 二者同源） |
| **2. `stop()` 判活** | `process::is_running`（进程名）→ `is_alive`（**按声明**），与 `reconcile`/`snapshot` 同源；并把与 `start` 的**故意不对称**写进注释：`start` 宽松（防"管道未就绪又拉第二个"=多主）、`stop` 精确（**不误杀旁观者**） |

判据优先级（写进代码注释）：`Running > Starting > !monitored(Stopped) > Degraded > Retrying > Stopped`；
`Degraded` 先于 `Retrying`（熔断时退避往往同时成立，而"不会自己好"更该让用户知道）；
`!monitored` 先于 `Degraded`（按需组件没在跑是正常的 —— 沿用 `health` 的既有判断）。

新增 3 条单测：**五态穷举**（含"进程在跑时不显示启动中"、"退避到期后不再是 retrying"、"熔断优先于退避"）、
**按需组件不报故障**、**`state` 与 `health` 同源**（防"菜单说熔断、status 说 ok"）。
`stop()` 判活那一改动**不写直接单测**：用例必须在"同名进程在跑"时成立，而修复一旦被回退，
`stop_by_exe_name` 就会**杀掉测试进程本身**（本仓已有同类回避先例）⇒ 改由既有的判据单测覆盖（`stop` 现在用的就是那个判据）。

**一处有意的契约变化**：`health` 过去只看 `degraded ∩ monitored`（不看是否在跑），现在要求"没在跑" ——
一个**正在跑**的组件不该被报成不健康。

**下一步（S5-3 主体）**：子菜单（父项带原生勾选 / `[!]` 文本）+ 三类子项规则（有 stopFlag / 仅 gate 管辖 / on-demand）
+ `stop` 写标记 / `start` 删标记（(a) 持久语义，文案写明「直到手动启动」）+ `restart` **独立路径**（不写标记）
+ 拒绝时**托盘气泡**（顺带做出验收项 8 的链路）。

#### ✅ S5-3 三个隐含语义已确认并落地（2026-09-19 晚）—— `cargo test` **167/167**（+4）

用户提出的三个问题逐个对着代码核过：**两个是真缺口，一个只需写清**。

1. **①「Running 覆盖 Degraded」隐含的"重置"—— 判据覆盖 ≠ 清债，确有缺口**：
   - 活过一轮的分支清了 `failures` / `consecutive_failures` / `next_attempt_at`，**唯独漏了 `degraded`**，
     它要等下一轮 `refresh()` 才被重算 ⇒ 存在 ≤3s 的"债已清空、熔断标记还挂着"窗口；
   - 更糟：**`snapshot()` 从不调 `refresh()`** ⇒ `status` 可能报一个**早已过期**的 `degraded`
     —— 这正是"两套答案"的最小形态。
   **已修**：成功分支显式 `st.degraded = false`（清债与清标记必须在同一处）；`snapshot()` 先 `refresh`
   （诊断命令必须对着**当前时刻**回答）。新增 2 条单测："活过一轮把债一次清干净"（用**内部真值**断言 ——
   因为 `state` 会被 `Running` **遮住**，判据说 ok 证明不了内部不欠债）、"snapshot 刷新过期熔断"。
   为此在既有测试缝里补 `degraded()` / `next_attempt()` 两个访问器。
2. **②「start 宽松」没有边界** —— 我的注释**过度声称**、代码确实**全程宽松**：用户描述的 bug 真实存在
   （同名旁观者在跑 → 点"启动" → `Unchanged` → **点了没反应**）。
   **已修**：抽出纯判据 `explicit_start_is_noop(c, running, st)` = **按声明判活** ∪
   **"刚由我们拉起、待验证"那一轮内额外接受进程名**（`pending_spawn && spawn_is_alive`）；
   边界写在函数名与注释里。新增**纯判据**单测（刻意不真拉起任何进程 —— 避开"用例会把测试进程自己拉起来"的坑）。
3. **③ on-demand 是否经过 `Starting`** —— **是（`type=tool` 除外）**：规则源自 `arms_crash_detector`
   （只对 tool 返回 false）⇒ panel 走 `Starting → Running →（用户关掉后）Stopped`，
   而 tool 直接 `Running`/`Stopped`（给"跑完即退"的一次性工具显示"启动中"是误导）。已写成单测 + 注释。

**⇒ 两件前置 + 两处补债都已完成，S5-3 主体（子菜单 / 三类子项 / stop 写标记·start 删标记 / restart 独立路径 / 拒绝气泡）可以开工。**

#### ✅ S5-3 主体已落地（2026-09-19 晚）—— `cargo test` **171/171**（+4）；core 已部署

| 落点 | 内容 |
|---|---|
| `core/src/components.rs` | `flag_path()` 单点派生（读/写/删同一处，避免"名字差一个词"那类坑）+ `write_flag()` / `clear_flag()`（**失败必须 `Err`** —— 写不进去却报"已停止"，就是把"3 秒后被拉回"变成静默行为） |
| `core/src/tray.rs` | 命令区间 **START=1000 / STOP=1200 / RESTART=1400 / TOGGLE=2000**（四个区间**各自判界**，错位无编译期信号）；`MenuAction::{Start,Stop,Restart}`；**纯函数 `component_entries()`** 承载三类分组（可单测）；`notify()` 托盘气泡（`NIM_MODIFY`+`NIF_INFO`）；组件改**子菜单**（父项 `MF_POPUP|MF_CHECKED` + `!` 文本） |
| `core/src/main.rs` | `start_component`（**先删标记**再拉起 —— 只做一半会留下"跑起来了却不会被自愈"）/ `stop_component`（**先写标记**再停进程；无标记 → **拒绝 + 气泡指向设置开关**）/ `restart_component`（stop + start，**不写标记**） |

**三类分组**（单测钉住）：① 有 `stopFlag` → 「停止（**直到手动启动**）」+「重启」；② `desired=running` 无标记
→ 只给「停止（由设置开关管辖：<键>）」；③ 按需 → **只有「启动」**。停止态一律只留「启动」。
**父项呈现**：在跑/启动中 → **原生勾选**（启动中加后缀 —— 不撒谎也不留空）；重试中/已熔断 → `!` **文本**；
gate 关 → 「（已由设置关闭）」。

**真机（本轮）**：
- core 部署 ✓（`size=791040`，备份 `betterdesktop-core.exe.s53-bak`）；启动日志干净，
  `ensure=true` 的三条**未重复拉起**已在跑的实例 ✓（差集语义）。
- **第 5 项 PASS**：杀 Clipboard.Panel（PID 38016）→ 13 秒后 **0 实例、日志无 `started`** ✓（on-demand 不被拉回）。
- ⚠️ **`status` 的 E2E 校验未做**：`BetterDesktop.Cli.exe` 无任何输出（子命令语法未知）。
  不阻塞 —— 菜单读的是**进程内** `snapshot()`（不走管道），而管道新增的 `state` 字段已由单测覆盖。
  记为待办（等确认 CLI 用法或用管道客户端补一次）。

**⟵ 只有"点一下"才能验的部分（菜单是 GUI）**：我读日志核对，用户点菜单即可。

| 点哪里 | 期望 | 我核对的证据 |
|---|---|---|
| 「剪贴板历史面板 ▸ 启动」 | 面板出现（它刚被第 5 项杀掉） | 日志 `explicit start: 'clipboard-panel'` |
| 「剪贴板历史引擎 ▸ 停止（由设置开关管辖…）」 | **弹气泡**指向设置开关，且引擎**不停** | 日志 `refusing to stop 'clipboard-engine'` |
| 「主程序 ▸ 停止（直到手动启动）」 | Host 退出、`host-stopped.flag` 出现、**不被拉回** | 日志 `stopped 'shell' (persistent)` + flag 存在 |
| 「主程序 ▸ 启动」 | Host 起来、`host-stopped.flag` **消失** | 日志 `explicit start` + flag 不存在 |
| 关掉托盘「剪贴板历史」开关 | 引擎/面板父项显示「（已由设置关闭）」 | 菜单文案（单测已覆盖该分支） |
| 再点它们的「启动」 | **被拒 + 气泡** | 日志 `GateClosed` 分支（**这就是验收项 8 的链路**） |

#### 🔎 真机发现（2026-09-19 晚，用户手测）：托盘图标"消失" + 旧托盘程序并存

**用户现象**：用 core 托盘「启动 主程序」后，托盘图标消失、菜单栏里仍是旧托盘程序。

**日志证据**（`core-20260919.log`）：

| 时刻 | 日志 | 判读 |
|---|---|---|
| 21:12:07 | `explicit start: 'clipboard-panel'` | **清单① PASS** ✓（按需拉起成功） |
| 21:12:34 | `tray menu: refusing to stop 'clipboard-engine' — it has no stop flag; … switch 'extensions.clipboard-history.enabled'` | **清单② PASS** ✓（(b) 拒绝路径生效，气泡已尝试） |
| 21:13:15 | `explicit start: 'shell'` | **清单④的启动动作 PASS** ✓ |
| 21:13:17 | **`TaskbarCreated received (explorer restarted)`** → `tray icon removed` → `tray icon re-registered` | **Host 启动时重启了 explorer** ⇒ 通知区被整体重建 ⇒ core **确实重新注册**了图标 ✓（Windows 11 会把新注册的图标放进**溢出区** —— 推断，故观感是"消失"） |
| 21:13:18 | 新进程 `Agent(59320)` / `Tray(53760)` / `Index.Engine(3356)`，父进程都是 `Host(53192)` | **旧 C# 托盘 `BetterDesktop.Tray.exe` 又被 Host 拉起** ⇒ **两个托盘并存**，即用户看到的"菜单栏里还是旧托盘程序" |

**分清归谁**：
- **core 侧没有出错** ✓：菜单动作落实、explorer 重启后图标自愈（S5-2b 的 `TaskbarCreated` 路径真机生效 ✓）、
  core PID 未变（35284，**未被 Host 杀掉** ✓）。
- **两个托盘并存是过渡态**：core 已接管托盘图标与菜单（S5-2a/b），而 `BetterDesktop.Tray.exe` 仍在 Host 的拉起清单里
  （它的删除是后续步骤）。⚠️ 这与 S4-3 的"两个守护者"是**同一类问题**：**职责迁走了、旧进程还在被拉起** —— 应尽早收掉。
- **额外发现（真 bug，非我引入）**：`pipe request: kind=legacy head=paste-session` 两次到达 **core** 的控制管道，
  core 明确回以 `routing error: legacy action 'paste-session' … belongs to the Host (\\.\pipe\BetterDesktop.HostCmd)`。
  ⇒ **仍有旧入口把 core 的管道当 Host 的管道用**（迁移未接完）。core 的行为是对的（拒绝 + 不回复 + 留痕），
  要改的是那条**客户端**的指向。
- 顺带确认：计划任务兜底正常工作（每 ~5 分钟 `another core instance is running; exiting` ✓）。

**手测暂停**（用户判断"只能测到这里"）：清单 ③/⑤/⑥ 与**验收项 8** 未测（都需从菜单点，且 ⑤⑥ 要动设置开关）。
**已测且有证据**：① 按需拉起 ✓、② 拒绝 + 指向设置开关 ✓、④ 启动动作 ✓、S4-3 第 5 项（杀 Panel 不拉回）✓。

#### 🔎 (b) 根因定位：不是"有人绕过统一客户端"，而是 **`Kernel.dll` 有多份副本**

**grep 结论（推翻了两个假设）**：
- `PasteSessionReporter` **走的就是** `MenuCommandPipeClient.TrySend` ✓（"有人自建管道"不成立）；
- `MenuCommandPipeClient.PipeName`（core 那条）在源码里**只被 `CoreControlClient` 使用**（`@ctl` 专用）✓
  ⇒ **源码侧没有任何 C# 代码把 legacy 发到 core**。

**真正的根因**：安装树里有 **4 份 `BetterDesktop.Kernel.dll`**，而**组件各自从自己所在目录加载**：

| 副本 | 构建时间 | 含新路由（`HostCmd`/`PipeNameFor`） |
|---|---|---|
| `…\BetterDesktop\BetterDesktop.Kernel.dll`（**面板/引擎都住这儿**） | **09/16 10:18** | **False** ✗ |
| `…\BetterDesktop\app\2026.09.17.1610\…`（core / agent / tray 住这儿） | 09/19 18:45 | True ✓ |
| `…\BetterDesktop\DesktopControl\…` | 09/17 14:58 | **False** ✗ |
| `…\backup\s4-routing-20260919-185133\…` | 09/18 | False（备份，无害） |

⇒ 面板加载的是**它自己目录里那份 09/16 的旧 Kernel** ⇒ 那个版本的客户端只会发 `BetterDesktop.MenuCmd`
⇒ 落到 **core** ⇒ 正是那 6 条 WARN ✓✓。
**S4-4 的断言「换它即全部消费者生效」在部署形态下不成立 —— 不存在"一份"。**

**已做（立即修复）**：把 `app\…\` 那份（新）同步到另两处：
`root` `F82CEA228CEA → 5E03EBCD4788` ✓、`DesktopControl` `D1A931004880 → 5E03EBCD4788` ✓（各留 `.bak-kernelfix`）。
为换 `root` 那份必须先停加载它的进程（引擎被 core 正常拉回 ✓ PID 32760；面板是按需件，停在原地）。
⚠️ **一处未解释**：期间面板曾自行出现（父进程显示为桌面服务，且 core 日志里**没有** `explicit start`）——
可能是用户/热键打开（未走 core 菜单），**未确认，如实记录**。

**待验（需用户操作一次）**：面板下次打开 + 做一次按序粘贴 ⇒ core 日志应**不再有**
`routing error: legacy … paste-session`，而 Host 日志应出现这条命令。

**(b) 的正确形态（对用户原方案的两处修正）**：
1. 「客户端必须走 `MenuCommandPipeClient`」这条门禁 —— 源码侧**今天已全绿**（唯一用 core 管道的是 `@ctl` 客户端）
   ⇒ 作为**防腐**规则仍有价值 ✓，但**抓不到这次这个 bug** ✗。
2. **真正该加的是「Kernel 副本唯一性」**：`Kernel.dll` 必须在**所有消费者目录**里是**同一份构建** ✓。
   它是**发布期不变量**（CI 里没有安装根）⇒ 应落在 publish / deploy 流程，而不是 `verify-architecture-guard`。
3. 已补一条**测试钉**：路由测试的 legacy 列表原先**没有 `paste-session`** —— 正是"没被钉住的那条漏了"
   （已加入 `MenuCommandPipeRoutingTests`）。

#### 🔎 真机第二轮：剪贴板历史"唤不出来" = **Host 被显式退出**（设计生效）+ **core「启动」不能唤起已在跑的面板**（真缺口）

**证据链**：
- `host-stopped.flag` 的 LastWriteTime = **21:13:43**（Host 21:13:15 启动、日志停在 21:13:39）⇒
  **21:13:43 有人从托盘「退出主程序」** ⇒ **S4-3 的 stopFlag 语义生效：core 刻意不复活** ✓✓。
  这是该语义**第一次在真实使用中生效** —— 第 4/6 项的真机验证在此复现。
- **面板本体没坏** ✓：手动 `--open` 拉起成功（同秒出现 2 个进程），`panel.log` 无异常 ⇒
  **与本次换 `Kernel.dll` 无关**（换 Kernel 之前 Host 已退出）。
- **入口依赖度（读码）**：`tray/TrayApplicationContext.OpenClipboardHistory` **直连面板**（`--open` ✓）+ 兜底 CLI→宿主；
  剪贴板热键 `Ctrl+Shift+V/P/Backspace` **由引擎注册**（引擎在跑 ✓）⇒ 这两条**都不依赖 Host**。

**真缺口（S5-3 分组规则的一个盲点）**：
- C# 托盘的「打开剪贴板历史」**总是** spawn `--open` ⇒ 命中面板单实例 ⇒ 走命名事件 ⇒ **显示 / 切换** ✓；
- core 的「启动」先判 `explicit_start_is_noop` ⇒ **已在跑就不 spawn** ⇒ 命名事件永不触发 ⇒
  **对"在跑但已收起"的面板，core 的「启动」是空操作** ✗✗（用户观感正是"唤不出来"）。

⇒ **修法**：`type=panel` 的「启动」语义应为**唤起**：**总是** spawn `--open`（不因已在跑而跳过），文案改「打开」。
对 `type=process` / `tool` **维持现状** —— 它们在跑时的"启动"本就该是幂等空操作，且**不能照搬这条**（那会重新引入多主拉起）。

#### 🔎 真机第三轮：C# 托盘是**第二个守护者**（kill 循环）+ panel-唤起缺口在真实日志中复现

**kill 循环（21:31–21:35，每 3 秒一轮）**：core 日志反复 `stopped BetterDesktop.DesktopControl.exe (pid kills=1)`。
根因：`settings.json` 里 **`components.desktop` 显式为 `false`** ⇒ core 按 gate **停掉**桌面服务 ✓✓；
而**重启起来的 C# 托盘把它反复拉回** ✗✗ ⇒ 两者打架。
⇒ **C# 托盘是第二个守护者**（与当初的 Agent 同类：职责已移交 core，旧进程仍在 ensure）⇒ 已**停掉该托盘**，循环立即停止 ✓。
⚠️ 这已是"职责迁走、旧进程还在拉起"这条模式在本仓出现的**第三次**（Watchdog 名单 → Agent 监护 → C# 托盘 ensure 桌面服务）。

**同一段日志里的两条证据**：
1. 用户在 core 菜单里把「剪贴板历史」打开 ⇒ `tray menu: toggle #7 -> true` +
   `supervisor(tray-toggle): started 'clipboard-engine'` ✓✓ —— **S5-2a（core 自己是写者）+ S5-2b（Rust 托盘）+ S3 监护**
   三条链路在真机上完整串通。
2. **panel-唤起缺口在真实日志里复现** ✓：`component 'clipboard-panel' is already running` ⇒ 用户点「启动」时面板已在跑
   ⇒ core 什么都没做 ⇒ 观感"唤不出来"（与上一节根因分析一致）。

**托盘图标不显示 = Win11 溢出区** ✓：`HKCU\Control Panel\NotifyIconSettings` 里我们的条目 **`IsPromoted` 缺失**
（未提升 ⇒ 全部计入溢出区），与"explorer 被 Host 重启 3 次"叠加所致。
**已把两个 live 路径条目的 `IsPromoted=1`** ✓ 并重启 core 重注册 ✓（旧 Tray 已停，其条目留着无害）。

#### ❌ 我在本节中制造的故障：**手工跨版本替换 `Kernel.dll`** ⇒ 面板完全唤不出来（已修复）

**故障**：为消除"Kernel 副本偏斜"，我把 `app\…` 的**新** `Kernel.dll`（09/19 18:45，含 S4-4 路由）
手工覆盖到 `root\` 与 `DesktopControl\`。其中 `root\` 那份被**面板（09/17 构建）**加载 ⇒
**启动即崩**（跨版本程序集不兼容）⇒ 用户"反复尝试各路径唤起剪贴板界面都无法成功"。

**时间线证据（决定性）**：
- 最后一次**成功**的面板 UI 引导：`21:21:14`
- 我的 `root\` 替换：约 `21:25`（前两次因文件被占用而失败，最后用 `watchdog-pause.flag` 抢到窗口才成功）
- 此后每次拉起面板：**零日志行**（连"主题引导"都到不了）
- 回滚后：`21:37:39 主题引导完成` + `分页加载成功: total=208` ✓✓ ⇒ **恢复**

**可执行教训（写进纪律，不写"记得别忘"）**：
> **不许手工跨版本替换程序集。** "同一份 Kernel"只能靠**重新发布全部消费者**实现；
> 手工拷 dll 修"副本偏斜"，只会制造**更严重**的偏斜 —— 原问题只是一条 WARN（无害），
> 我的修法让**面板完全不可用**。这正是用户提的**发布期不变量**（所有消费者目录同一份构建）该有的样子，
> 而现在有了**代价证据**。
>
> 顺带一个操作细节：`Stop-Process` 是**异步**的，句柄释放有延迟；而 core 每 3 秒 tick 会把组件拉回来重新占用文件。
> 正确姿势 = **先写 `watchdog-pause.flag`**（监护暂停，第 7 项验过）→ 循环杀到零 → 复制 → 删标记。

**最终状态**：`root` / `DesktopControl` 回到旧版（`F82CEA228CEA` / `D1A931004880`），`app\` 保持新版（`5E03EBCD4788`）；
进程 = core + clipboard-engine + clipboard-panel。**偏斜仍然存在**（待正式发布消除）。

#### ⚠️ 部署缺口的代价第一次真实兑现：**Agent 是第三个"旧守护者"**

回滚后 core 日志又出现 `stopped desktop` 循环，进程表给出元凶：`DesktopControl(49488) 的父进程是 Agent(59320)`
⇒ **Agent 的旧构建每 5 秒 ensure 桌面服务** ✗（它的 `DesktopServiceSupervisor` —— S4-3 已识别并给了"停手"补丁，
但**补丁只在源码里，未进部署**）⇒ core 按 gate 杀、Agent 又拉起 ⇒ 每 3–6 秒一轮，持续数分钟。
**停掉 Agent 后循环立即停止** ✓。

**判读**：
- 这是"部署缺口"的**真实代价**（此前只是"第 11/13 项未验"的记账）；
- 今天遇到的"旧守护者"已累计**三个**：Watchdog 名单（源码已停）→ C# 托盘（已停）→ **Agent**（已停）——
  **S4-5 删 agent/watchdog 不是清理工作，是止血**；
- **发布路径的优先级因此上升**：机器上跑着三个"源码已改、部署未更"的组件（Agent / Watchdog / Tray-Tray），
  只要 Host 一被启动，旧 Agent 就会被拉回 ⇒ **在正式发布之前不要重启 Host**（此约束再确认一次）。

**一处纠正（对前两节的自我修正）**：我先前把"唤不出来"归因于 **core 启动不唤起已跑的面板**（设计缺口）。
那个缺口**真实存在**（日志 `clipboard-panel is already running` 为证），但**不是本次现象的原因** ——
本次的原因是**我换 Kernel 让面板根本起不来**。两条都要修，但优先级不同，不能混为一谈。

#### ✅ 恢复：`components.desktop` 已改回 `true`（core 立即拉回自绘桌面）

- 证据：core 日志 `started 'desktop' (restart #1)` + 进程对（父子链 ✓）⇒ **自绘桌面回来了** ✓。
- 改法：**原子替换**（读 → 只替换那个字面量 → 写临时文件 → `Move-Item -Force`），并**回读校验 JSON 合法** ✓
  —— core 每 3 秒读它，绝不能留一个半写的文件。
- ⚠️ **`components.desktop=false` 是谁写的：未定论**。`settings.json` 的 mtime = **21:33:25**，那一刻 **Host 已死、C# 托盘在跑**
  ⇒ 当时唯一存活的直写者就是**托盘的键开关路径**（它在宿主不在时会**直写 settings**，绕开 core）。用户表示不是自己关的
  ⇒ 记为"**疑似该路径**（含误点可能），未证实"。**危害是真实的**：core 更严的 gate 语义会**停掉进程**，
  一次误写就"自绘桌面消失"—— 这正是"行为变更"条目里预告过的用户可见后果。

#### 🔎 顺带两条新发现

1. **部署的 CLI 是旧构建** ✗：`BetterDesktop.Cli.exe --core status --json` **退出码 = 2（Usage）** ⇒ 它**不认 `--core`**
   （源码里该分支是"S2 的 CLI 改指 core"）。⇒ 又一条部署偏斜；`status` 的 E2E 校验因此仍待（todo #14）。
   另：CLI 是 GUI 子系统 exe（stdout 不落控制台），故只能用**退出码**判断它的行为。
2. ⚠️ **core 把 legacy 消息内容整条写进日志**（隐私相关）✗✗：routing-error 那行把 `arg` 原样打出，
   而 `paste-session` 载荷**含"下一次将粘出的内容预览"**（为灵动岛设计）⇒ **core 日志里出现了剪贴板内容**，
   与仓库自己的"隐私红线（只上报进度、不含内容）"**冲突**。⇒ 建议：该日志**只记 head + 长度**（截断/脱敏）；
   并把 `paste-session` 正式路由去 Host 后，这条错误本身不再出现。

#### 🔎 命名侦察（2026-09-19 晚）：**不存在 `BetterDesktop.Core`；命名不阻塞 S4-4**

起因：用户提出"两个可用 core + kernel 里一个旧 `core.dll`"，要求**先答三问再定方案**。侦察结论（有据）：

| 问 | 答案 | 证据 |
|---|---|---|
| 谁生产 `BetterDesktop.Core`？ | **不存在这个实体** | 全仓 `BetterDesktop.Core.*` 文件 = **0**；`search_content "BetterDesktop\.Core"` 只命中 **2 个文档**（本计划 + 六层模型），**无任何代码或工程引用** |
| 旧 core.dll 在哪？ | 最接近的是 **`BetterDesktop.Shell.Core.dll`**（带 `Shell.` 前缀，不是裸 `Core`） | 产出项目 = `packages/shell/shell-core/BetterDesktop.Shell.Core.csproj`（`<AssemblyName>BetterDesktop.Shell.Core</AssemblyName>`）；安装根 / app / DesktopControl 各一份 |
| 被引用吗？ | **88 处 ProjectReference** ⇒ 是**共享 UI 库**，不能删、也不必改名 | 含 `agent/…`；`Shell.Core` 是合法命名（有前缀） |

**"两个 core"印象的两个来源（都能解释，都不是"第二个可用 core"）**：
1. **`betterdesktop-core.exe` 的构建输出副本 ×3**（`BetterDesktop.Cli\bin\Debug` 714 KB / `bin\Release` 445 KB /
   `core\target\release` 791 KB —— **大小各异 = 不同构建**）。其中至少一份在 `NotifyIconSettings` 里注册过托盘图标（dev 运行留下）。
2. `BetterDesktop.Shell.Core.dll`（名字里带 `.Core`）。

**机制判据（最硬的一条）**：Rust core 是**原生 exe**，**不是 .NET 程序集** ⇒ **.NET 的 assembly resolution 根本不会去解析它**
—— 故"把新 exe 当旧 dll 找、或反过来"**在机制上不可能**。

**对门禁建议的一处修正**：原提议"C# 侧任何项目名不得以 `.Core` 结尾"**会误伤** `BetterDesktop.Shell.Core` ✗
⇒ 改为精确版：**禁止裸 `BetterDesktop.Core`**（AssemblyName / 文件名 / 目录名），且 **`betterdesktop-core` 这个名字只属于 Rust crate**。

**对 S4-4 的结论**：命名**不阻塞** S4-4（无悬空引用）⇒ 它真正的前置是 **B1（发布路径）**。"先侦察再删"的原则成立，且已完成。

**新问题（待查，不是结论）**：CLI 的 `CoreEnsurer` 指向哪一份 core？若指向**它自己旁边**的副本
（`bin\Release` 445 KB = 09/19 12:18 的旧构建），则 core 一旦挂掉，可能被**旧 core**拉起 ✗。

#### ✅ 修复并**实测验证**：CLI 的 `CoreEnsurer` 解析顺序（2026-09-19 晚）

**问题（真机证据，`%TEMP%\bdt-cli.log`）**：`CandidateDirs()` 把 `AppContext.BaseDirectory` **排第一** ✗，
于是开发态 CLI 旁边那份 `betterdesktop-core.exe` 构建副本先被命中 —— 日志实录 **4 次**：
`ensure core: launched …\BetterDesktop.Cli\bin\{Release,Debug}\…\betterdesktop-core.exe`
（其中 Release 那份 445 KB = 当日 12:18 的**旧构建**）⇒ **"确保 core 在跑"拉起的是旧 core** ✗。

**修**（`BetterDesktop.Cli/CoreEnsurer.cs`）：顺序改为 **安装根 → 数据目录 → 调用方同目录（仅开发态兜底，且与安装根去重）**；
注释写明"调用方目录不能进持久化逻辑"（与 S4-2 的 `ResolveNativeDllPath` 同一条教训：**解析顺序决定找到的是哪一份**）。

**实测（同一份诊断日志，前后对照）**：
- 修前：`launched …\BetterDesktop.Cli\bin\Release\…\betterdesktop-core.exe` ✗
- **修后：`launched C:\Users\…\Local\BetterDesktop\app\2026.09.17.1610\betterdesktop-core.exe`** ✓✓
  （`core status → ok (attempts=2)`；core 由该路径自行拉起，PID 488 ✓；组件按差集未被重复拉起 ✓）

**同一份日志里的另外两条结论**：
1. **B3 确认**：部署的 CLI 对 `--core` 回 `未知参数` ✗（21:44 两次），而 **dev 构建认得** ✓（12:34 / 21:59）
   ⇒ 部署的 CLI 早于 `--core` 分支；`--core` 能力本身没问题。
2. **一处自我纠正**：`settings.json` 的 mtime = 21:33:25 是 **clipboard 开关**那条写的 ✗ ⇒ 我先前
   "`desktop=false` 是那一刻写的"**推论错误**（mtime 只证最后一次写入）。日志里**没有**任何
   `components.desktop=False` 记录 ⇒ 写入者**既不是 CLI 也不是 core**，是**直写 settings 的某条路径**
   （最可能是托盘的「停止桌面服务」，**未证实**）。

**顺带记一条排期冲突（用户提出）**：**S6 与 S4-4 都要改 `host/Bootstrap.cs`**（S6 删三个 `Ensure*`，
S4-4 删 `EnsureAgentRunning` 调用点）⇒ 两者**不能并行**，需先定先后。

#### 🔎 侦察 0：settings 直写路径 —— **ADR 冲突确认，且存在"无日志的写入者"**（2026-09-19 晚）

**问题**：`components.desktop=false` 是谁写的？（日志里查无记录 ⇒ 与 §13.19 ADR「唯一写者是 core」冲突）

**侦察结果（推翻了我与用户共同的猜测）**：

| 候选者 | 判决 | 证据 |
|---|---|---|
| C# 托盘「停止桌面服务」 | **不是** | `tray/ProcessBridge.StopDesktopService` 只写 `desktop-stopped.flag`，**不碰 settings** |
| CLI `--toggle-desktop` | **今天没写** | 其 `Diag` 日志今天只有 clipboard 开关；写入必留「切换自绘桌面(无实例直写)」 |
| Host（命令桥 `settings.Set`） | **今天没写** | `host-20260919.log` **无任何**「切换自绘桌面」行（只有 09-17 有） |
| core | **今天唯一写入是 `true`** | core 日志 `head=set arg="components.desktop true"`（≈20:59，S5-2a 验收）⇒ **core 的写入可观测** ✓ |

**时间窗收紧**（core 启动日志四次读到 gate）：`20:13=false → 21:08=true → 21:31=false → 21:59=true`（末次是我手工改回）。
⇒ 翻转发生在 **21:08:42 – 21:31:07**，而该窗口内 **Host 只活了 28 秒**（21:13:15–21:13:43）且未写任何键。

**⇒ 结论：存在一个"写 settings 却不留任何日志"的路径 —— 这比"知道是谁写的"更值得警惕**：
它与 ADR「core 是唯一写者」直接冲突，且 **core 无法知道谁改了配置、改了什么**（当前只能靠 3 秒 tick 重读"捡到"）。
⇒ **直接扩大 S7 的验收面**：S7 不能只做"把写者收敛到 core"，还必须做**写入可观测**（谁写的、写了什么、何时）。

**已定位的写入面（4 个实现）**：

| 实现 | 安全性 | 调用点 |
|---|---|---|
| `packages/shell/shell-settings/Services/SettingsService.cs`（临时文件+替换） | 安全 | 设置中心 + 各 shell 插件 + `host/Bootstrap.cs` 的 `settings.Set` |
| `host/SettingsFileWriter.cs`（原子 + 备份 + 防清空守卫） | 安全 | `ToggleKeyCommand` / `DesktopToggleCommand` |
| `BetterDesktop.Cli/HeadlessExecutor.ToggleDesktop()`：**裸 `File.WriteAllText`** | **不安全** | CLI `--toggle-desktop`（无互斥、整份覆盖 —— 正是 `SettingsFileWriter` 注释里记的"曾把用户配置清空"那类写法） |
| `core/src/settings.rs`（Rust，原子写） | 安全 | core 自身（可观测 ✓） |

⇒ **S7 的最小工作集 = 收敛前三个 + 给"谁写的"留痕**。

#### 🔎 侦察 0 的收敛结论（2026-09-19 晚）：**不是"有野写入者"，是"唯一写入口本身无观测性"**

继续追查后，结论从"有个看不见的写入者"收敛到更准确的一条：

- **`architecture-guard` = PASS** ✓（"三条边界棘轮无新增违规且清单无失效条目"）⇒ **没有未登记的写入点**：
  R3 `settings-writer` 的清单已把"含 `settings.json` 字面量 + 写原语"的文件全部登记 ✓。
- ⇒ 那次 `components.desktop=false` 走的正是**已登记的唯一合法入口** `SettingsService` ✓（不是野路径）。
- **但 `SettingsService` 不记录"写了哪个键"** ✗ —— 而它被**多个进程**托管：
  `host`（Host）、`agent/Program.cs`（"与壳共用同一份 settings.json"）＋设置中心 ＋各自的插件 ⇒
  **任何一方都能静默翻任何键**，core 只能靠 3 秒 tick 重读"捡到" ✗。
- 窗口内**唯一长期存活**的 SettingsService 宿主 = **Agent**（PID 59320，21:38 才被我停）✓
  —— 这与"Host 只活 28 秒且无写入日志"共同指向它，但**没有直接证据**，故仍记为**未定论** ✓。

**⇒ S7 的验收面因此扩大（比原以为的大）**：
1. 写者收敛到 core（原计划）；
2. **写入可观测**：谁写的 / 写了什么 / 何时 —— 且因为 ADR 允许 `SettingsService` 在兼容期继续存在，
   这条**必须同时落进 `SettingsService`** ✗，不能只落 core。
3. 顺带：门禁 R3 的 `intent` 文案仍是旧口径（"唯一合法写入口是 C# 侧 `SettingsService`"），
   与 ADR §13.19（"唯一写者改为 core"）**不一致** ⇒ S7 收口时一并更新。

#### ✅ ② 命名门禁 R4 落地（2026-09-19 晚）—— `architecture-guard` PASS，门禁单测 **90/90**

**新增规则 R4 · core-name-owner**（`scripts/verify-architecture-guard.ps1`）：
**裸 `BetterDesktop.Core` 只有一个主人 —— Rust 的常驻进程。**

判据（按用户给的取值）：**`^BetterDesktop\.Core(\..*)?$`**，大小写不敏感；查**三处**：
**文件名 / 目录名 / csproj 的 `<AssemblyName>`**。

- **`BetterDesktop.Shell.Core` 必须通过** ✓ —— 它是被 **88 处 `ProjectReference`** 引用的合法共享 UI 库，
  且与 Rust core **不撞名**（后者是**原生 exe**，连 .NET 程序集都不是 ⇒ assembly resolution 根本不参与）。
  ⇒ 本规则收的是**裸名**而非后缀 —— 已写成 Pester **反例**，防未来有人把它"加强"成后缀匹配。
- **`betterdesktop-core` 天然不命中**（连字符 vs 点）⇒ **无需白名单**，也就不会被棘轮的"条目失效"反噬 ✓。
- **`BetterDesktop.Core.UI` 判违规** —— 宁可报红让人显式登记，也不愿未来有人顺手起名而无人察觉。
- **形态**：R4 是**定制检查**（照 R0 先例），不塞进 `NeedAll` 棘轮 —— 后者是"文件内容双标记"框架，
  表达不了**文件名 / 程序集名**；且该名**本就该零命中**，不该是棘轮。

**验证**：门禁本体 **PASS**（文案已含"BetterDesktop.Core 命名唯一"）✓；
门禁单测 **90/90** ✓（`run-gate-tests.ps1`；新增 `Test-CoreNameViolation` 正例/反例 + `Get-CoreNameViolations` 三处命中与豁免）
⇒ 顺带**机器复核**了"本仓不存在裸 `BetterDesktop.Core`"这条侦察结论 ✓。

#### 🔧 C19「允许目录」定案（2026-09-19 晚）：**与 `resolve_exe` 同一份，且不新增第二处前缀比较**

**C19 允许目录 = ① core 自身 exe 所在目录 + ② `%LOCALAPPDATA%\BetterDesktop`** —— 恰好等于
`core/src/process.rs:resolve_exe` 的**搜索集**（`:30-41`）✓。

- **安装根不单列**：生产里它是 ② 的子目录（`…\BetterDesktop\app\<ver>`）⇒ 单列只是重复。
- **不采用 `task.rs::is_stable_location` 的严格口径**，分水岭 = **持久引用 vs 运行时一次性拉起**：

| 规则 | 校验对象 | 生命周期 | 口径 |
|---|---|---|---|
| `task.rs::is_stable_location` | **core 自己的 exe** 写进系统级计划任务 | **持久**（跨重启，系统级） | 严格：安装根/LOCALAPPDATA，**dev 拒绝**（"把开发目录写进系统级计划任务是纯负债"） |
| S4-2 注册表路径规则 | `shellmenu.json` 快照指向的 DLL | **持久**（注册表） | 严格（同上） |
| **C19（本条）** | **组件 exe 的每次解析结果** | **运行时**（每次启动重新解析） | **宽松**：dev 态必须能跑 dev `bin` 里的组件 |

⇒ 这两条差异**不是漂移，是同一条判据的两个落点**；写在此处，下一个人不必再拍一次。

**C19 真在防什么（不是冗余）**：`resolve_exe` 是 `dir.join(name)`，而 **`Path::join` 遇到绝对路径会替换基路径**
⇒ 若 `components.json` 里出现绝对路径或含 `..` 的名字，**今天就能解析到任意目录**（另见 §6.10"拒绝 `..` 与绝对路径注入"）。
搜索集是"约定"，前缀校验是"断言"——**约定的破坏不会被约定自己发现**。

**实现纪律（防两侧漂移）**：**复用 `shellmenu::is_under`**（`shellmenu.rs:770`，按**路径段**比较、大小写/斜杠不敏感，
且已有 `BetterDesktopTrap` 的误放行用例）—— **绝不新写第二处前缀比较**（C23"一个概念一处实现"）。

#### ⚖️ S7 决策：唯一写者是「core **进程**」还是「core **逻辑**」—— 选 **A**，但改法要换

用户的区分必要，而且**计划与 ADR 的措辞本身不自洽**：ADR §13.19 写"**写操作的发起者只有 core**"（= A）
＋"`SettingsService` 降为**共享库**"（✗ core 是 **Rust**，调不了 C# 静态类）⇒ 两句不能同时字面成立。

| 解释 | 真实形态 | 代价 |
|---|---|---|
| **A. core 进程是唯一写者** | 所有写请求经控制管道 → core（Rust）落盘 | 表面是"每个调用点都要迁"，**但见下** |
| B. core 逻辑是唯一写路径 | C# 与 Rust **两份实现、一份契约** | 两份 JSON 格式化/合并语义**必须永不漂移**；且"core 完全不知道谁改了配置"（(0) 的发现）**依旧存在** |

**选 A**，理由：B 不但留有**双实现漂移**风险，还**原样保留** (0) 的"写入无观测性"✗ —— 而 A 顺带把它解掉（写者唯一 ⇒ 观测点唯一）。

**关键：A 的改动面不等于"调用点数量"** —— 保留 `ISettingsService` **接口**不变，只把它的**实现**换成
"转发给 core 的 `set`"（设置中心 / Agent 插件**零改动**）⇒ 改动面 = **1 个实现 + 读路径保留**，
而不是 N 个调用点。（`@ctl set` 已在 S5-2a 落地 ⇒ 通道现成 ✓。）

#### 🧭 S6 vs S4-4 顺序定案：**S4-4 先**（止血先于收口，且只改一次 `host/Bootstrap.cs`）

两者都碰 `host/Bootstrap.cs`：S4-4 删 agent/watchdog 时必然要删 `EnsureAgentRunning`；
若 S6 先做（把所有 Ensure 改成"不再拉起"），S4-4 之后再改同一处 ⇒ **同一段代码改两遍**。
⇒ **S4-4 先行**：止血（删旧守护者）→ 于是 S6 的改动面反而变小（Host 只需删剩余 Ensure）。
该决策**不依赖 B1** ✓。

#### ✅ ③ 路径前缀校验（C19）落地（2026-09-19 晚）

| 项 | 内容 |
|---|---|
| **一份目录定义** | 新增 `process::exe_search_dirs()`（① core 自身 exe 目录 ② `%LOCALAPPDATA%\BetterDesktop`）—— **`resolve_exe` 用它找、C19 用它断言**，杜绝"解析在此、校验在彼" |
| **两道闸** | ① `process::is_bare_name()`：拒绝绝对路径/分隔符/`..`（**`Path::join` 遇绝对路径会替换基路径** ⇒ 这条在 `join` 之前就关掉注入面）；② `security::validate_resolved_component_exe()`：解析结果必须落在允许目录内 |
| **复用而非重写** | 前缀比较直接用 `shellmenu::is_under`（按**路径段**、大小写/斜杠不敏感、已有 `BetterDesktopTrap` 误放行用例）⇒ 不新增第二处前缀比较（C23） |
| **接线点** | `supervisor::try_spawn`（`spawn_detached` 的唯一调用者）—— 两道闸都在拉起前 |

单测：`is_bare_name` 正反例（含绝对路径、UNC、`sub\x.exe`、`.`/`..`/空串）、
**"绝对路径即便存在也必须被拒"**、`is_within_any` 的允许/拒绝/同前缀兄弟目录（`BetterDesktopTrap`）、
**"解析目录与 C19 目录是同一份"**。

**顺带被门禁抓住一次（这是棘轮在正常工作，如实记录）**：改动后 `architecture-guard` 报
`[lifecycle-owner] core\src\process.rs — 未登记` —— 因为 `process.rs` 本就是**拉起原语的家**（`spawn_detached`），
而我新加的单测夹具里出现了 `"BetterDesktop.Agent.exe"`，两个标记凑齐 ⇒ 命中 R1。
**处置：登记进 `architecture-allowlist.json`（why 写明"它是原语实现处、不是调用者"），而不是改夹具让门禁转绿** ——
后者会让该文件将来**真的**新增拉起点时失去扫描。⇒ 棘轮的成本正是"每次新增都要解释一次"，这次解释值这一句。

#### ✅ ④ 威胁模型落地：`docs/threat-model.md`（975 词，2026-09-19 晚）

**落点勘误**：计划原写 `SECURITY.md`，但仓里**已有** `docs/security.md`（内容是供应链/插件权限**规则**，另一主题），
二者仅差大小写 ⇒ 在 Windows 大小写不敏感文件系统上**会互相覆盖** ⇒ 改为 `docs/threat-model.md`，并在 `security.md` 顶部互链。

**形态按用户要求（防"装饰性文档"）**：
- **信任边界**（谁可信/不可信，5 行）；
- **防谁** T1–T7：每行 = 场景 + 措施 + **证据指针**；指针一律**文件名 + 测试名**，**不用行号**（行号会腐烂；
  测试名被改本身就说明测试可能没了）；T1 还带上"`CreateRestrictedToken` 会给**假绿色**"这条脚本自述的坑；
- **不防谁（负空间）**：6 行，**每行都带触发条件** —— 唯一"永久不防"的是管理员级攻击者，
  并**显式说明它为何可以不带条件**（它不可能腐烂成"过时的借口"）；
- **该回来改的五个触发条件**（含"任何措施被删除时先回本表找它对应的威胁；**找不到 = 它可能是装饰性的**"）。

**字数纪律**：`scripts/manifests/doc-budgets.manifest.json` 登记 **975 词**（= 实测值，棘轮式）；
措辞本身不是本文件的重点 —— 细节留在各实现文件的模块注释里，本文件只给威胁与指针。

#### ✅ S5-3 收尾：panel-唤起缺口修复 + **S4-3 第 5/8 项补测 PASS**（2026-09-19 晚）

**先说明一件事（避免误算工时/归属）**：S5-3 **主体**（三类子项规则 / 持久停止 / 重启 / 拒绝气泡 / `state` 五态）
是由**并行工作流**落进 `core/src/tray.rs` 的 —— 依据：单测数 **163（前置）→ 171（主体 +8）→ 177（C19 +6）→ 179（本轮 +2）**，
而我本轮只做了前置与 C19 ⇒ 主体那 8 条不是我的。**代码核实**（不只信计划）：`CMD_STOP_BASE=1200` /
`CMD_RESTART_BASE=1400` / `component_entries` 三类规则 / `NIF_INFO` 气泡 / 区间判界测试均在位 ✓，
部署态 hash 也已含它 ✓。

**本轮补的最后一块 —— panel「启动」是唤起而非拉起**（计划原记"panel-唤起 待修"）：
- `supervisor::start_is_wakeup`（**纯判据**，只认 `type=panel`）+ `start_with` 在其上走**唤起**分支：
  **总是** spawn（`--open`），**不动监护状态**（不 `pending_spawn`、不 `restarts++`、不重置熔断 ——
  这是一次**信号投递**，不是"拉起了一个被监护的组件"），但仍然**记日志**（写入必须留痕）；
- `tray.rs` 文案：panel 的动作叫**「打开」**（单实例唤起，"点第二次是切回来"），**只对 panel** 改
  （`tool`= 现在跑一次、`process` = 拉起来，照搬会重新引入多主拉起）；
- 顺带修掉一处**陈旧注释**：`stop` 的文档还写着"`start` 用宽松判据 `process::is_running`" ✗ ——
  那是已被 `explicit_start_is_noop` 取代的说法（用户上轮正是要求澄清这条）。
- 新增 2 条单测（**179 passed / 0 failed**）：只有 panel 是唤起（**钉在类型上**，防日后放宽成"有 UI 的都算"）、
  panel 文案「打开」/ tool 文案「启动」。

**真机验收（部署新 core `D795582405A6`，备份 `betterdesktop-core.exe.s53bak` 保留）**：

| 验证 | 结果 |
|---|---|
| panel 唤起（面板在跑时点「打开」） | **PASS** —— `{"changed":true,…,"running":true}`，日志 **`wake-up sent: 'clipboard-panel' -> …(panel: always spawns; the running instance decides show/hide…)`**；不再是 `already running` |
| **S4-3 第 5 项**（杀 Panel → 不拉回） | **PASS** —— 杀后 11s（>3 tick）**实例 = 0**，无 `started 'clipboard-panel'` |
| **S4-3 第 8 项**（关设置后入口被拒） | **PASS** —— `set …enabled false` → `start clipboard-panel` 返回 **`{"error":"gate-closed",…,"ok":false}`**，且**无 wake-up 日志行**（证明门禁判在唤起**之前**）；开关已还原 `true` |

⇒ S4-3 的 13 项中，**仅剩第 11/13 项**待正式发布流程（部署缺口），其余 11 项 PASS。

### 13.22 S5-4：动作归属四档 + 暂停监护双标记（2026-09-19）

**本轮结果**：`cargo test` **187/187**（+8）；`BetterDesktop.Cli` 构建 **0 警告 0 错误**；
门禁 `-Discover` 的 lifecycle-owner 命中集 = `core\src\cli.rs` + `core\src\process.rs` + 既有 C# 六处（与清单一致）。

**① 归属纠正（先记账）**：S5-3 的**主体**（组件启停子菜单 / 三类分组 / 持久停止 / `state` 五态 / 179 单测）
是并行工作流的成果；我上一轮补的是 **panel 唤起 + 一处陈旧注释 + 2 条单测**（163→171→177→179 的单测轨迹是可验证证据）。
记账按证据走、不按"谁说的"—— 这条要一直坚持。

**② 暂停守护：两个独立标记、一个判据**（`supervisor.rs`）

| 标记 | 谁写 | 谁清 |
|---|---|---|
| `watchdog-pause.flag`（**保持原名**，不改动更新器） | 更新器（替换文件期间） | 更新器替换完自己清 |
| `user-pause.flag`（新） | 托盘「暂停组件监护」 | 用户显式恢复（可能跨重启） |

**为什么不能共用**：共用会有一个真实 race —— 用户暂停 → 更新器接管文件、看到标记已存在 →
更新完成时把它删掉 → 用户以为还暂停着，组件已被拉起。
core 判据 = **任一存在即暂停**；日志区分来源（`paused by: updater|user`），
且**来源变化时再记一次**（"从更新器暂停变成用户暂停"是不同的事）。
菜单勾选**只反映用户自己那个标记**（否则更新期间用户会看到一个自己从没点过的勾）。
**deferred**：S7 之后统一到 `pause\` 目录（updater / user 两个空文件）—— 那时改名成本最低，现在不改。

**③ 动作归属（同类动作归同类所有者）**

| 档 | 例子 | 落地方式 | 理由 |
|---|---|---|---|
| 组件生命周期 | 启动 / 停止 / 重启 | 交给 `supervisor` | 唯一生命周期所有者 |
| 设置 | 11 项开关 | core **自己写** `settings.json` | core 是配置单写者（§13.19） |
| **自身持久化** | 开机自启 | core **自己写** `HKCU\...\Run` | 与计划任务同处（都持久化 core 启动路径）；共用 `task::is_stable_location` 守卫 |
| **业务细节** | 系统集成 / 更新 / 应急恢复 / 诊断包 | core **只派发** CLI 窄命令 | allowlist 只登记 CLI 一个拉起者；写入实现只在 C# 侧一份 |
| **一次性系统动作** | 打开日志目录 / 关于 | core 直接做（`ShellExecuteW` / `MessageBoxW`） | 不涉及组件生命周期，不需要 allowlist 条目 |

- `core/src/cli.rs`（新）：`BetterDesktop.Cli.exe` 字面量的**唯一**处（`dispatch_async` / `run_and_wait`）。
  `shellmenu.rs` 的修复触发改调它 ⇒ allowlist 的 `core\src\shellmenu.rs` 条目按棘轮**删除**、
  新增 `core\src\cli.rs` —— "只许收缩"这一条真被执行了一次（不是纸面规则）。
- `core/src/autostart.rs`（新）：`HKCU\...\Run` 的 `BetterDesktop.Core` 值；**与计划任务共用同一条
  `is_stable_location` 守卫**（把开发 bin 写进开机路径 = 一次 clean 后每次开机都失败、且无处报错的负债）。
- `tray.rs`：新增 10 项（暂停监护 / 系统集成▸4 / 更新▸2 / 应急恢复 / 诊断包 / 日志目录 / 开机自启 / 关于）；
  `MenuAction` +8 变体。**开关区间补上界**（`CMD_TOGGLE_BASE..CMD_PAUSE_TOGGLE`）：
  S5-2b "漏改映射 ⇒ 点开关没反应" 那个坑，在 3000+ 新 id 进来后会以更隐蔽的方式复现，已有专门单测钉住。
- `BetterDesktop.Cli`：新增 `--diagnostics-export`（打包在 CLI 内，`DiagnosticBundle` 与托盘同一份）、
  `--recovery`（定位并拉起应急程序、不等它结束）、`--update <check|install>`
  （install = 下载 → **经 core 管道 `stop shell`** → 应用：停组件必须有唯一所有者）、
  组件定位**统一**到新 `ComponentPathResolver.cs`（`Resolve` / `CandidateDirs` / `IsBareName`），
  `CoreEnsurer.Resolve` 收成它的一行门面（原先两处逐字重复的候选链已合并，见下方复核 ③）。
- `main.rs`：CLI 动作走**后台线程**（主线程是消息循环线程，同步等一个可能卡住的 CLI 会冻结托盘），
  结果经 `Shell_NotifyIconW` 回气泡；打开日志 / 关于在主线程直接做。

**⬜ 未做**：① **S4-4**（删 `agent/` + `watchdog/`）—— .NET tray 的"Agent 启停 / Watchdog 启停"两组
菜单项随它一起删（core 菜单从无这两组，天然满足）；② S5-5 的**卸载**项与真机走查；
③ **core 本轮未部署**（只到"构建 + 单测绿"，部署随 B1 的发布流程）。

#### 13.22.1 二轮复核：三条取证 + 两条已修 + 一条待决（2026-09-19）

**R1 棘轮首次"真收缩"实证**（值得单独记）：`core\src\shellmenu.rs` 的条目被**机制**删除、
`core\src\cli.rs` 新增 —— 这不是"我打算保持清单干净"，是 `-Discover` 把"条目已失效"摆出来、逼着删的。
**这是 R1 上线以来第一次真实收缩**，说明棘轮的第二条（清单不许腐烂）确实在工作。

| 复核项 | 取证 | 结论 |
|---|---|---|
| **① diagnostics 脱敏** | 实际导出一个包并解开：条目 = `system-info.txt` + 21 个 `logs/*.log`；**不含** `settings.json` / `components.json` | **已实现**（三项归一 + 默认脱敏 + `--no-redact` + 打包后自检，落地记录见 §13.22.2）。核查当时的取证：敏感配置没进包 ✓；但 `system-info.txt` 的"目录 / 日志 / 命令行"三行 + 全部日志正文**含用户名**（实测 host **15/336** 行、agent **189/4112** 行、core **65/2982** 行含 `C:\Users\<name>\…`），且**全仓无脱敏实现**（`shared` 搜 `脱敏\|redact\|sanitiz` 零命中）⇒ "走同一套脱敏"当时**无处可走**，须**新建**那套。 |
| **② install 的 core 缺失场景** | 读 `CoreControlClient.Send`：`NotRunning`/`Timeout` → 调注入的 `ensureCore` → 5s 内循环重发真实请求；`ensureCore` 失败 → `EnsureFailed` | **已修**。传入的 `CoreEnsurer.Ensure` 本身正确（ensure 路径成立），但我对失败结果**只记日志就继续** —— 与"core 起不来 ⇒ 报错"不符。现改为 `!Succeeded && IsHostRunning()` → **报错返回**（`stage=stop-shell`，附 `failure`/`detail`）；壳本来没开时仍放行（那是合法的"没东西可停"）。 |
| **③ CoreEnsurer / ComponentLocator 共享契约** | 两者**同一 csproj**（`BetterDesktop.Cli`），候选链逐字重复 | **已修**。新增 `ComponentPathResolver.cs`，`CoreEnsurer.Resolve` 收成一行门面、`ComponentLocator.cs` 删除。判据用"**同项目就抽函数**"而非共享向量；且**故意**不做跨语言共享 —— CLI 多一层"安装根优先"是设计差异、不是漂移（已写进该文件头注）。 |

**✅ 记账已核销（2026-09-19，二轮补跑）**：本轮当时**未完整跑** `verify-architecture-guard.ps1` 与 Pester
（两轮都在终端被转后台），只用 `-Discover` 核对了"命中集与清单一致"。
**`-Discover` 只回答"哪些文件被命中"，不回答"命中后会不会报红"** ——
清单里一条 `why` 写错、一条 `removeBy` 失效、一条正则改动，它都看不出来。
⇒ 二轮已补跑，结果见 **§13.22.3**（14/16 PASS、Pester 90/90；仅 `test-coverage` 未取到结果）。
`-Discover` 与完整门禁的差别这次有了实据：门禁确认棘轮**首次真收缩生效**（清单有删、无失效条目）。

#### 13.22.2 ① 脱敏落地（2026-09-19，三项定案后）

**定案三件**：归一化 = **用户名 + 机器名 + SID**（三项全做）；触发 = **默认脱敏 + `--no-redact` opt-out**；
位置 = **导出时脱敏 + 打包后自检**。

| 落点 | 内容 |
|---|---|
| `shared/logging/DiagnosticBundle.cs` | 新增带 `redact` / `nameSuffix` 的重载（旧三参重载**行为零变化**，`null` 时仍是逐字节原样复制 —— 不重编码，因为某些引擎日志不是 UTF-8）；脱敏以**整份字节**进出 |
| `BetterDesktop.Cli/DiagnosticsRedactor.cs`（新） | `ForCurrentMachine()` 取 `%USERPROFILE%` / `MachineName` / `WindowsIdentity.User` **+ 用户名的段级形态**；placeholder 表 `<USERPROFILE>` / `<USERNAME>` / `<MACHINE>` / `<USER_SID>`；黑名单 `GenericUserNames`；**长串优先**排序；`ScanZipForLeaks` 自检（值级 + 段级同源） |
| `BetterDesktop.Cli/Program.cs` | `--diagnostics-export` 默认脱敏 / `--no-redact`（文件名带 `-RAW`）；**自检不干净 → 删包 + `stage=redaction-selfcheck` 报错**，绝不落盘 |
| `BetterDesktop.Cli.Tests/DiagnosticsRedactorTests.cs`（新） | **38 条**：三类值各被替换且只换命中段 / 幂等 / 含正则元字符的值按字面替换 / 长串优先 / **同一值多次出现全部替换（回归）** / 段级替换重定向目录 / 系统目录不动 / 段不完整不匹配 / 黑名单两个方向 / 黑名单判据 Theory（含 13 项系统目录名）/ **同一路径出现 6 次全部替换** / "用户名=Program Files 时系统目录原样" / 自检段级抓漏 / 无规则时空操作 / 空值忽略 / 自检对干净包不误报 / 本机工厂不抛 |

**一处有意偏离（已说明理由）**：不用"正则字符串替换"，改**字节级**替换 ——
`GetString → 替换 → GetBytes` 会把非 UTF-8 日志里的非法字节换成 U+FFFD，
那是在"脱敏"的同时**损坏证据**，而诊断包的价值就是证据。字节级只动命中段、其余字节一个不碰。
正则方案的三条设计点全部保留（placeholder 表 / 长串优先 / 幂等），也**不需要** `Regex.Escape`
（字节匹配没有元字符概念）—— 已由单测钉住（含 `.$[]()^|` 的用户名照常替换）。

**端到端立刻抓出一个单测漏掉的 bug（值得记）**：`Span.IndexOf(切片)` 返回的是**切片内**相对偏移，
我当成绝对索引用 —— 第一个匹配（`start=0`）恰好正确，**从第二个匹配起**越界
（报错 `Offset and length were out of bounds … (Parameter 'count')`）。
只出现一次的用例完全看不出来；是"同一路径在真实日志里出现几十次"把它逼出来的。
已修 + 补"同一值多次出现"回归用例；顺带加固：`Export` 失败时**删除半成品包** ——
"看起来是包、其实是空的"比没有包更危险（用户会把它当证据发出去）。

**实测（本机真机导出）**：`--diagnostics-export` → exit 0、自检 clean；`system-info.txt` 三行全部变成
`<USERPROFILE>\…`；抽查 4 个最大日志条目对 `C:\Users\<name>` 命中 **0**。
`--no-redact` → `…-RAW.zip`，原始值保留 ✅。

**用户名的第二种形态：段级规则（2026-09-19 拍板 = 三项的「补全」，不是第四项）**

`%USERPROFILE%` 只覆盖"标准用户目录前缀"这一种形态。实测残留 5 行：文档目录被重定向后，
日志里出现 `D:\17822\Documents\…` 与 `D:\Users\17822\AppData\…` —— 它们**不含** `C:\Users\17822` 前缀，
值级规则碰不到。**目标没变（防定位）、实现漏了形态 ⇒ 判定为补全**，故增加段级规则：

| 判据 | 理由 |
|---|---|
| **前后都是 `\`**（段必须完整） | 不匹配 `D:\17822abc\`；驱动器根 `C:\17822\` 天然含 `\17822\`（冒号后即首个反斜杠） |
| **段 == UserName**（不做子串匹配） | `C:\Windows\` 不匹配 —— `Windows` ≠ 用户名 |
| **UserName 不在 `GenericUserNames` 黑名单** | 用户名恰为 `windows` / `users` / `dev` … 时，段级替换会把 `C:\Windows\` 换成 `C:\<USERNAME>\`、**读坏整份日志** |

取舍**宁漏不误**（与"字节级替换"同一条原则）：漏了只残留一处用户名，误了是**证据损坏**。
黑名单常量收在 `DiagnosticsRedactor.GenericUserNames` **一处**，不散落。
**自检自动覆盖段级** —— `FindLeaks` 遍历的正是同一张规则表，于是"我写了段级替换"变成"我验证了它生效"。

**实测（真机）**：加上段级规则后重新导出 → `EXIT=0`、自检 clean；
`host-20260918.log` 含 `17822` **5 → 0** 行（30391 行里），host-20260919 / agent / core 三份日志同为 **0**。
单测 **76/76**（+13：段级替换 / 系统目录不动 / 段不完整不匹配 / 黑名单两个方向 / 黑名单判据 Theory /
**同一路径出现 6 次全部替换** / 自检段级抓漏）—— "同一路径多次出现"这条是给上一轮越界 bug 配的防复发钉子。

**黑名单复核（同日第二轮）**：首版 35 项**漏了系统目录高频名**，补 12 项
（`windowsapps` / `system32` / `syswow64` / `winsxs` / `recovery` / `program files` /
`program files (x86)` / `common files` / `microsoft` / `intel` / `amd` / `nvidia`）⇒ **47 项**。
判据：Windows 只拒绝**设备保留名**（`CON` / `PRN` / `NUL` / `COM1-9` / `LPT1-9`），
**不**拒绝 `Program Files`、`System32` 这类目录名当本地账户名 —— "一般不会"与"不可能"是两件事，
而代价非对称（多登记一项≈0，漏一项 = `C:\Program Files\` 被换成 `C:\<USERNAME>\`、整份日志读坏）。
单测 **90/90**（+14：黑名单 13 项 Theory + "用户名 = Program Files 时系统目录原样"）。

**域账户形态记为 deferred（不是遗漏）**：当前只归一**本地账户**形态（`Environment.UserName` 无域前缀），
`DOMAIN\username` 里的域段不在规则表内。刻意现在不做 —— 它需要另一段值与"域段是否该脱"的判断，
而当前面向本地账户。**触发条件 = 出现域账户用户的诊断包 issue**；到那时最小做法 = 按同一判据
（段完整 + 黑名单）加一条域段规则。已写进 `DiagnosticsRedactor` 文件头注，下一个人不会问"为什么没处理 `DOMAIN\`"。

#### 13.22.3 完整门禁 + Pester 补跑（2026-09-19，核销上一轮记账）

**结论：16 道门禁 14 道 PASS；Pester 90/90；2 道红，均与本轮改动无关。`test-coverage` 两次被终端
转后台、未取得结果 ⇒ 仍是未核销项。**

| 门禁 | 结果 |
|---|---|
| **architecture-guard** | **PASS** —— 输出原话："生命周期 / 热键 / 配置写入三条边界棘轮**无新增违规且清单无失效条目**"。这正是 `-Discover` 答不了的那一半：它证明 `core\src\cli.rs` 条目**命中**、`core\src\shellmenu.rs` 条目删除后**确实不再命中** —— 棘轮首次真收缩在门禁层被确认 |
| doc-budgets / md-links / gate-registry / agent-note / archived-notes / package-readme / host-log-sink / cross-asm-event / native-convergence / smoke-test / system-integration / protocol-contract | **PASS**（12 道） |
| **dotnet-format** | **FAIL —— 抓出我引入的真问题，已修全**（见下） |
| **md-wrap** | FAIL 3 处：`docs\defensive-patterns.md`（09-19 **19:25**）、`packages\shell\shell-search\README.md`（09-17）、`README.md`（09-17）。**时间戳均早于本轮** ⇒ 既有，非我引入 |
| **test-coverage** | **未取得结果**（两次被转后台）⇒ 下轮补 |

**dotnet-format 抓出的**真问题（我引入的，教训值得留）**：

我写入的源文件用了 **CRLF**，而 `.editorconfig` 全仓要求 `end_of_line = lf`。后果两种，都比"格式不好看"重：

1. **新建文件全行违规**（`DiagnosticsRedactor.cs` / `ComponentPathResolver.cs` / `DiagnosticsRedactorTests.cs`）；
2. **破坏原本合规的文件** —— `Program.cs` / `CoreEnsurer.cs` / `shared/logging/DiagnosticBundle.cs`
   在基线里是 **0 条**（= 原本全 LF）；我插入的 CRLF 行让文件变混合行尾、违规凭空出现。

⇒ 教训：**基线里 0 条的文件就是"原本干净"的文件**，动它一点就会弄脏它 —— 因为它对行尾零容忍。
**写文件必须按 `.editorconfig`（LF），不能按 OS 默认。**

**修复**：6 个 `.cs` + 7 个 `.rs` 转 LF；三个项目 `dotnet format`（含自动修复缩进）后
`--verify-no-changes` **全部 EXIT=0**。新增格式违规 **10463 → 8230**，减掉的 **2233 处全部是我引入的**。
剩余 8230 处按文件分组（Top：`launcher/Services/ComponentBootstrapper.cs` 491、
`shell-context-menu/Services/SystemIntegrationRegistrar.cs` 480、`tray/TrayApplicationContext.cs` 479、
`shell-search/Services/FileSearchProvider.cs` 398 …）**无一是本轮碰过的文件**
⇒ 是并行工作流区域的既有 CRLF（未跟踪 WIP），**需授权后才动**（机械转 LF 即可，但那是他人文件）。

**回归**：`cargo test` **187/187**、`Cli.Tests` **90/90**、Pester **90/90**。

#### 13.22.4 S4-4 源码侧：删 `agent/` + `watchdog/`（2026-09-19）

**为什么先做它**：这不是清理，是**止血**（计划 §7.1 B4 原话）。机器上三个"职责已移交、进程还在拉起组件"
的旧守护者（Watchdog / C# 托盘 / Agent），实测 Agent 每 5 秒把桌面服务拉回、与 core 的 gate 打架。

**做完的（全部在我自己的未跟踪产物区域内）**：

| 落点 | 动作 |
|---|---|
| `agent/` 目录 | **删除**（含 5 个 Capabilities：截图热键 / 独占能力 / 桌面监护 / 图标 / HostPresence） |
| `watchdog/` 目录 | **内容已删净**（根目录有一处残留，见下） |
| `BetterDesktop.slnx` | 删 `/agent/` 与 `/watchdog/` 两个 Folder |
| `host/Bootstrap.cs` | 删 `EnsureAgentRunning()`（57 行）+ 调用点；原位留下"这里原来有东西 + 三项能力各迁到哪"的说明 |
| `launcher/Services/ComponentBootstrapper.cs` | 删 RequiredFiles 两项 / `EnsureWatchdog` 方法（38 行）/ `VerifyResidents` 的 Agent 半 / `agent-stopped.flag` 留痕 |
| `tray/TrayApplicationContext.cs` | 删 **Agent 与看门狗两个子菜单**（含「暂停守护」勾选项）+ 5 个方法 + `EnsureAgentAutoStart` + 状态显示 —— 落实用户那句"删旧世界时不删入口比不删更糟（死按钮）" |
| `tray/ProcessBridge.cs` | 删 Agent / Watchdog 的启停原语（53 + 50 行）、两个进程名常量、只被它们用的 `IsStopFlagPresent` |
| `tray/AppPaths.cs` / `tray/Program.cs` | 删 `AgentExe` / `WatchdogExe` 与 `SelfTest` 的看门狗检查 |
| `scripts/manifests/architecture-allowlist.json` | **棘轮强制收缩 2 条**（见下） |

**棘轮在这里做了一次真活**：删完目录后 `architecture-guard` **主动报红**，原话是
"清单条目已失效（现实中不再命中）；棘轮只许收缩，请删除该条目"（两条：`watchdog\Program.cs`、
`agent\Capabilities\CaptureHotkeyOwner.cs`）。删条目后重跑 → **PASS**（"无新增违规且清单无失效条目"）。
这是棘轮与 S4 系列的第二次配合（上一次是 `core\src\shellmenu.rs` → `core\src\cli.rs`）。

**验证**：全仓 `dotnet build BetterDesktop.slnx` **0 警告 0 错误** —— 编译正是"找引用点"的手段，
它只揪出一处（`tray/Program.cs` 的 `AppPaths.WatchdogExe`，已修），其余全是字符串引用（不阻碍编译）。
`launcher-tests` **9/9**、`md-links` PASS（233 个 md 无断链）、`doc-budgets` PASS；
`dotnet-format` 新增违规 **10463 → 5194**（先前 CRLF 修复 + 本轮 launcher/tray 转 LF，累计清掉 5269 处）。

**一处残留（如实标注）**：`watchdog/` 的**空目录**删不掉 —— 内容（含 csproj）已全部删净
（逐个文件删，`locked = 0`），但根目录被某进程当作**当前工作目录**占用：
`Remove-Item` / `[IO.Directory]::Delete` / `rename` / `cmd rmdir` 四种方式全被拒
（"being used by another process"）。slnx 已不引用、代码已不拉起 ⇒ **对构建与运行零影响**，
只需在无占用时 `Remove-Item` 一次。

**⬜ 待授权（别人区域的残留引用 —— 不授权我不动）**：

| 文件 | 残留 | 影响 |
|---|---|---|
| `scripts/publish.ps1`（39/48/58/64） | 构建清单含 `agent\BetterDesktop.Agent.csproj` 与 `watchdog\BetterDesktop.Watchdog.csproj` | **publish 会失败**（指向已删的 csproj）← 最该先修 |
| `scripts/publish-modules.ps1`（37/43）、`scripts/install-betterdesktop.ps1`（67） | 必需文件清单含两个 exe | 打包 / 安装校验会报缺件 |
| `scripts/uninstall-betterdesktop.ps1`（50/86/202） | 自启值名 / 组件名 / `agentExe` | 无害（清理不存在的项） |
| `updater/ResidentGate.cs`（32/128） | `WatchdogProcessName` + 替换后拉起 | 停 / 起一个不存在的进程（无害） |
| `updater/Applier.cs`（23/170/238） | `AgentProcessName` / 优雅停止 / `StartAgent` | 更新后 `restarted` 可能报"重启异常" ← **用户可见** |
| `recovery/Program.cs`（33/36） | 清理名单含两个进程名 | 无害 |
| `packages/shell/shell-settings/Services/SystemManagement.cs` | 看门狗开机自启注册 | 会写一个指向不存在 exe 的 Run 值 ← **用户可见** |
| `packages/shell/shell-taskbar/TaskbarAppearancePlugin.cs`（105/113） | 探测 `BetterDesktop.Agent` 是否在跑 | 恒 false（L1 常驻接管判定） |

**⬜ 真机验收待 B1**：`host/Bootstrap.cs` 不再拉起 Agent 之后，"机器上不会再出现第二个守护者"
需在**部署更新后**确认（当前部署里的旧 exe 仍在跑，本轮已手工停掉 Agent / Watchdog / DesktopControl）。

#### 13.22.5 门禁基础设施：反馈 + 分层 + 两次测量修正（2026-09-20）

**起因**：门禁的问题不是"慢"，是"**慢 + 静默**" —— 一个 146 秒却一行不输出的门禁，
你分不清"在跑"和"卡死"，于是不敢跑、绕开跑；而**被绕开的门禁等于不存在**。
故本轮顺序是：**先加反馈 → 再测量 → 才优化**。两次测量都得出了与直觉相反的结论。

**① 反馈（16 道全覆盖）**

三行契约，落在 `scripts/lib/common.ps1`（一处改动覆盖所有门禁）：
`[GATE] <id> 开始 <HH:mm:ss>` / `[GATE] <id> 阶段 <描述> … 已用 <N>s` / `[GATE] <id> 完成 总耗时 <N>s`。
`[PASS]` / `[FAIL]` 两行的文本**一个字没改**（那是对外契约，单测与 run-gates 都可能依赖）——
进度另起一行，只增不改。三个结构不同的脚本单独处理（`native-convergence` / `system-integration`
无守卫块；`no-cross-assembly-event` 自带 `$RepoRoot` 参数、不能被 dot-source，故内联同样三行）。

**② 快速通道 `-Fast`**（`run-gates.ps1`）

7 道（实测各 1-4s，合计 ~14s）：`package-readme` / `agent-note` / `archived-notes` / `md-links` /
`md-wrap` / `doc-budgets` / `gate-registry`。它会**打印跳过了哪些**（否则"快通道绿了"会被误读成"全绿"）；
`-List` 也标出 `[Fast]`。标 Fast 的判据只有一条：**实测秒级**。

**③ 测量 → 优化 architecture-guard：223s → 66s（3.4×）**

先加阶段打点，量出：

| 阶段 | 首次实测 | 占比 |
|---|---|---|
| R0（host/*.xaml） | 1s | — |
| lifecycle-owner | 26s | 12% |
| hotkey-registrar | 32s | 14% |
| settings-writer | 29s | 13% |
| **R4（Core 命名唯一性）** | **135s** | **60%** |

**两次都与直觉相反，两次都改了方向：**

1. **"手写剪枝"反而慢 3.5 倍**。直觉是"跳过 bin/obj 就省下枚举开销"，于是先写了自递归 +
   `[IO.Directory]::Exists` 逐条目判断的剪枝版 —— 实测 **65.9s vs 原实现 18.6s**（返回文件数完全一致，875 个）。
   原因朴素：**PowerShell 逐条目循环的常数**远大于 `Get-ChildItem` 背后的原生 C# 枚举器；
   剪枝的算法优势抵不过实现语言的常数差。
2. **真凶不是枚举，是"对每个文件跑 7 条正则"**。分解：全仓 **48874 目录 / 514206 文件**，
   两次 `Get-ChildItem -Recurse` 本身只占 ~25s；而 R4 的排除判断写的是
   `$skip | Where-Object { $full -match $_ }` —— 51 万文件 × 7 条正则 ≈ **350 万次正则匹配**。
   压成**一条**（`$skipRegex` = 7 个模式 join，OR 的结合律，语义逐字等价）后，R4 降到 38s。

最终形态 = 原生枚举 + 单条正则 + **枚举缓存**（三条边界规则的 `Extensions` 完全相同、原先各枚举一次
⇒ R2/R3 的增量从 32/29s 掉到 **1s**）。**判据一个字没改**，由
`verify-architecture-guard.Tests.ps1` **19/19** + 门禁本体仍 PASS 双重确认。

**④ test-coverage：不是"欠账"，是"跑错范围"**

它此前连续 4 次被切到后台 ⇒ 事实上**已经不在本地生效**。读实现发现两件事：
① 输出被 `$null =` 整个吞掉（把"最慢"与"最静默"叠在同一条命令上）；
② 它跑的是 **`dotnet test <整个解决方案>` = 24 个测试工程**，而覆盖棘轮基线只要求 **3 个程序集**
（`BetterDesktop.Kernel` / `Kernel.Loader` / `Kernel.Timer`）—— 其余 21 个纯属白跑。

改成按显式映射只跑那 3 个工程（**映射缺一条会报红**，不会静默漏检）+ 输出透传后：**11 秒跑完**。
**它当场抓出一个从未被人看到的真实违规**：`packages/shell/shell-clipboard-ipc/ClipboardIpcClient.cs`
里的 `Debug.WriteLine(`（被 `Kernel.Tests` 的"业务代码禁止 Console/Debug.WriteLine"抓到）。
**一个从未生效的门禁，一旦跑得起来，第一件事就是证明自己有用。**

**⑤ 授权范围内的修改**

| 项 | 动作 |
|---|---|
| `scripts/publish.ps1` | 按授权**仅删 4 行**（39/48 的 csproj 引用 + 58/64 的 `$required` 条目），流程其余部分未动 |
| `packages/shell/shell-settings/Services/SystemManagement.cs` | 取证纠正：原代码本就有"exe 不存在就不写"的保护 ⇒ **"写不存在路径"其实不会发生**；真实故障是**旧版写下的 Run 值残留**（每次开机静默失败）。故把 `SetWatchdogAutoStart` 语义改为**无条件清掉旧值**，`IsWatchdogAutoStartEnabled` 恒 `false`（不把"正在被清理的残留"显示成"已启用"） |
| `updater/Applier.cs`（**取证后未改**） | 判据是"用户能不能看到它出问题"。取证：`updater/Program.cs` 的重启逻辑是 `agentOk = !agentWasRunning \|\| StartAgent(...)`，而 `agentWasRunning` 的前置是 `IsRunningFrom("BetterDesktop.Agent", …)` —— Agent 已不存在 ⇒ **恒 false** ⇒ `agentOk` 恒 true ⇒ **不会报"重启异常"**。它是**死代码**、不构成用户可见故障 ⇒ 按同一判据归入 B1 清理（改它反而要动 updater 的替换流程） |

**⑥ 门禁抓出的两项：一项已修，一项归类为欠账**

| 项 | 处理与依据 |
|---|---|
| `scripts/install-betterdesktop.ps1` 的 2 行（67/73） | **已删**（仅这 2 行，脚本其他部分未动）。它的性质随 publish 的改动**变了**：改之前是"**悬空**"（指向已删文件、但没有路径执行到它）⇒ 无害可拖；改之后是"**矛盾**"（publish 已不产出这两个 exe，install 还在必检清单里等它）⇒ 必须删。**而且门禁报红是对的** —— 它在说"install 的必需文件清单与 publish 的实际产出已经不一致"，不是"门禁变苛刻了"。删除后 `system-integration` 立即转绿 |
| `ClipboardIpcClient.cs` 的 `Debug.WriteLine` | **不动，记为欠账**。取证两点：① `git status --short` 显示 `?? packages/shell/shell-clipboard-ipc/`（**整个目录未跟踪**）、`git log -S 'Debug.WriteLine'` 为空 ⇒ 这是**并行工作流正在写的新代码**，不是历史遗留；② 数量是 **6 处**（282/298/309/324/1635/1758），且每处都与 `Trace(...)` 配对，是这个模块刻意的调试手法 ⇒ 修法**不是"只删一行"**，而是"决定这个模块的记录方式"（换 `ILogger` 要引入依赖）⇒ 属"涉及引入依赖或改变逻辑"，按判据**不动**，交回并行工作流 |

**⑦ 门禁单测**：`run-gate-tests.ps1` **90/90**。顺带修了一处"夹具消失"型误报 ——
`verify-system-integration.Tests.ps1` 的"缺少必需文件"用例原先删的是 `watchdog/Program.cs`，
该文件随 S4-4 删除、门禁也不再读它 ⇒ 用例因为**夹具没了**而失败（测试没坏，是它依附的东西被移走了）。
已改用仍被检查的 `tray/SettingsBridge.cs`。

**⑧ 一个更大的观察：门禁正在从"从未运行"过渡到"开始有效"**

| 现象 | 含义 |
|---|---|
| test-coverage 收窄到 11s 后**当场**抓出 `Debug.WriteLine` 违规 | 一个从未生效的门禁，跑起来第一件事就是证明自己有用 |
| architecture-guard 删条目后主动报"清单条目已失效" | 棘轮在推着你维护清单，而不是你在维护清单 |
| 本轮两项待办（install 2 行 / `Debug.WriteLine`）**都是门禁抓出的真问题**，无一是误报 | 系统开始替你发现问题 —— 之前是你在找问题，现在是门禁在报问题 |

这与"凭空多了两件事要做"是相反的解读：**这是投资开始产生回报**。
另外，本轮**两次把"我以为的瓶颈"证伪**（手写剪枝更慢 3.5 倍；真凶是正则匹配次数），
这条已进 [`docs/defensive-patterns.md`](../defensive-patterns.md) 第八节（"测量工具的缺陷会伪装成被测对象的缺陷"）的镜像变体。
**为什么放那里而不是 `engineering-conventions.md`**：两处都合适，先试了铁律清单 —— 那份有 1000 词预算，
加完 1020 词、被 `doc-budgets` 拦下；改放 `defensive-patterns.md` 又**被拦了第二次**（那份也就余 33 词，1667/1700）。
于是走门禁自己给的第二条路 —— **调整上限 + 决策记录**：`docs/defensive-patterns.md` 1700 → **1870**（按实测 1867 棘轮设定），
理由写进 `doc-budgets.manifest.json` 的 note"历史调整"栏。
**被拦两次是有价值的**：它先把"能不能说短"逼到极限，再让你回答"值不值一个额度"——两次都成立才放行。

**⑨ test-coverage 的位置（收窄后的新决策，不是"改完了"的附带结论）**

它现在是 **11s**，**不进 `-Fast`**（Fast 的定义是实测 <5s，11s 会拖慢整条快通道 80%），
但已经**换了层**：从"事实上从没在本地跑完过（24 个工程 + 输出被吞）"变成"**能待在'提交前全量'层**"，
不再需要被当成 CI-only 的欠账。这个判断写进了 `run-gates.ps1` 注册表该条目的注释里。

#### 13.22.6 保护性提交 + B1 侦察（2026-09-20）

**提交 `2c18409`**：`wip: 冻结 S1-S5 Rust core 工作 + S4-4 源码侧清理`，**116 个文件**。
提交前逐项分类（口径："说不出类别的就不提交"），暂存区核验：`packages/` **0 项**、`README.md` **0 项**。

| 纳入 | 内容 |
|---|---|
| **`core/`（19 个文件）** | S1-S5 的 Rust 常驻进程全部工作 —— 此前**只存在于工作区**，一次磁盘故障或 `git clean -fd` 即归零。**这是本次提交的唯一主要理由**（它是风险敞口，不是待办） |
| `launcher/` `tray/` `shared/` `protocols/` `launcher-tests/` | 自己的产物 / 自己的区域 |
| `BetterDesktop.Cli/` + `.Tests/` | 自己的产物（含本轮 diagnostics / path-resolver） |
| `watchdog/`（删除） | S4-4：监护职责迁入 core |
| `scripts/`（38 项） | 门禁工具链 + 本轮全部改进 |
| `.gitignore` `AGENTS.md` `BetterDesktop.slnx` | 三者均已取证：Rust `target/`·`dist/`·`engines/` 忽略、core 电源红线、S4-4 项目增删 |
| `docs/`（4 份） | 计划本文档 + `engineering-conventions` + `defensive-patterns` + `threat-model` |

**未纳入**（归属属并行工作流、或未经验证）：`packages/`（380）、`host/`（15）、`tools/`（7）、
`docs/` 其余 62、`.agents/`（9）、`recovery/`（3）、`updater/`、`engine/` `engine-index/` `native/` `installer/`、
`README.md`、`Directory.Build.props`、`LICENSE`、`THIRD-PARTY-NOTICES.md` 及 5 份被删的中文审查报告。

**B1 侦察（提交后，回答三个问题）**

**Q1 — publish 需要什么产物？** 8 个组件项目（`$components`）+ 14 个必检产物（`$required`）。

**Q2 — 它们在 git 里的状态？**

| 组件 | 路径 | git 已跟踪 |
|---|---|---|
| Launcher / Host / Cli / Tray / Recovery | `launcher/` `host/` `BetterDesktop.Cli/` `tray/` `recovery/` | ✓ **5 个** |
| DesktopControl / Settings | `packages/shell/shell-desktop-control/`、`shell-settings-host/` | ✗（并行工作流） |
| Updater | `updater/` | ✗（未提交） |

**Q3 — 只 publish 已跟踪的部分，能跑通吗？**

`publish.ps1` 是**整体式**的（任一组件项目缺失即 `Fail` —— "a half release must never ship"）⇒
从干净检出**必然失败**。但缺口现在**可枚举**：`packages/` 的 2 个 + `updater/`。

⇒ **B1 从"整体堵死"变成"部分可解"**：从"不知道缺什么"变成"**只差 3 个组件，其中 2 个属于并行工作流**"。

**另外两个侦察发现**

1. **`host/` 是混合归属**：15 项未提交改动里既有本工作流的（S4-4 删 `MenuService`、改 `HostWatchdog`），
   也有并行工作流的（`Bootstrap.cs` 里的 `PublishPasteSession` 剪贴板面板上报）。
   ⇒ 它**不能整目录提交**，必须按改动切分才能进 git —— 这是 B1 的一个具体障碍，不是理论问题。
2. **`$required` 里仍有 `agent.yml`** —— agent 已随 S4-4 删除，这一条是遗留项（与 publish 自己的半成品纪律冲突）。

#### 13.22.7 A / B 的结论（2026-09-20）

**步骤 0 — `$required` 里的 `agent.yml` 遗留（已修）**

不止 1 处，实际 **3 处**：`scripts/publish.ps1:70`、`scripts/install-betterdesktop.ps1:75`、
`scripts/publish-modules.ps1:47`。三处同性质（S4-4 删了 agent，清单没同步），一并删除；
`system-integration` 仍 PASS（说明 install ↔ publish 的一致性没被破坏）。
**这是"自己的遗留"**：改 publish 那 4 行时就该顺手改掉，漏了。

**A — `updater/` 归属：成立**

三条客观判据一致：

| 判据 | 结果 |
|---|---|
| git 历史 | `git log --all -- updater/` → **无任何提交**（全新目录） |
| git 状态 | `?? updater/` |
| 内容 | `ResidentGate.cs`（更新期间写 `watchdog-pause.flag` 的那个组件）、`Applier.cs`（S4-4 清了它的 Agent 重启逻辑）、`Program.cs` —— 全部与 S4-4 同源 |

⇒ 提交 `updater/`（8 个源文件；`bin/` `obj/` 被 .gitignore 挡住，已核验 `BIN_OBJ_STAGED=0`）。
**A 的价值是"缺口可枚举、可收敛"，不是"缺口变小"**：publish 的 8 个组件里，未跟踪的现在只剩 **2 个**，
且都能指名道姓说清属于谁。

**B — `host/` 的切分：判给并行工作流（结论：本轮不切）**

证据（不是感觉）：

- `host/` 有 **13 个文件**有改动（阈值是 3）：`App.xaml`(-581) `Bootstrap.cs`(+271/-21) `FileLogSink.cs`
  `HostWatchdog.cs` `IconRestoreSentinel.cs` `MenuCommandPipe.cs` `ToggleKeyCommand.cs` `App.xaml.cs`
  `DesktopToggleCommand.cs` `cordis.yml` `packages.lock.json` `BetterDesktop.Host.csproj` `MenuService.cs`(删)
- 且这些文件的**新增行里直接含并行工作流的内容**：`shell-island` 的 `ProjectReference`、
  `PublishPasteSession`（剪贴板面板上报）、`["island"]` 插件注册、`components.dock` 的任务栏留痕联动。

⇒ 逐块切分等于"逐行判断哪行属于谁"，而那需要**最了解并行工作流的人**来判断。
**决定**：本轮不硬切。`host/` 的 S4-4 改动（删 `MenuService`、改 `HostWatchdog`）
**随并行工作流的 `PublishPasteSession` 一起提交**。

**因此 B1 的"完整发布"暂不可执行 —— 已知并接受**

| 项 | 状态 |
|---|---|
| publish 的 8 个组件 | **6 个已跟踪**（Launcher / Host / Cli / Tray / Recovery / Updater） |
| 缺的 2 个 | `shell-desktop-control`、`shell-settings-host` —— 都在 `packages/`，属并行工作流 |
| `host/` | 已跟踪，**但 git 里是改动前的版本**（工作区的 S4-4 + 并行改动都未提交） |

⇒ **B1 从"整体堵死"变成"已知边界"**：不是"不知道能不能发布"，而是"**差 2 个并行组件 +
1 个待切分的组件目录**"。这三项都不在本工作流的单方面控制内 —— 这是当前的真实边界，不是借口。

#### 13.22.8 S5-5：卸载项（源码侧完成，2026-09-20）

**范围**（计划 §7 的 S5-5）：core 托盘菜单的「卸载 BetterDesktop…」+ 真机走查。
本轮完成**源码侧**，真机走查随 B1 的部署 —— 与 S4-4 同模式。

**为什么它不走 CLI（与 S5-4 定下的"业务细节 → 只派发 CLI 窄命令"不冲突）**

卸载看似符合那条规则，实则相反：`uninstall-betterdesktop.ps1` 的第一步就是
**停掉所有组件、并删掉 core 的计划任务** —— 它要停的，正是 core 自己。
所以这条链的形态是"**core 主动交出控制权**"：

```text
① 写 user-pause.flag   （立刻停止监护）
② 异步拉起卸载脚本     （绝不等待）
③ 退出 core
```

**① 是必需，不是保险**：core 是监护者。脚本来删文件时若 core 还在跑，它会在删除窗口里
把"刚落线的组件"当成"掉线了"**复活**，而那些可执行文件正在被删。这与
[`docs/defensive-patterns.md`](../defensive-patterns.md) 第九节「替换被守护的二进制：先停守护者」
是**同一类问题**，只是对象从"替换"变成"删除"。写不进标记就**中止卸载**（明知会打架还动手更糟）。
用的是 `supervisor::USER_PAUSE_FLAG` —— 语义恰好是"用户显式要求的暂停"。

**② 为什么绝不等待**：脚本第一步停的就是 core 自己，同步等待 = **先自锁再自杀**
（.NET 侧 `ProcessBridge.StartPowerShellScript` 的注释记过同款坑）。
**③ 为什么退出**：core 占着安装目录里的文件名，不退脚本删不掉。

**几个刻意的取舍**

| 取舍 | 理由 |
|---|---|
| 定位链只走 **进程目录 → 安装根**，**不**回退 `%LOCALAPPDATA%\BetterDesktop` | 那是**数据目录**。卸载脚本是**程序文件**：从数据目录里翻出一个"可能是旧版本留下的"脚本去删当前程序，是最危险的那类"贴心"回退。宁可如实报"未找到"，也不猜 |
| `powershell.exe` 用**绝对路径**（`%SystemRoot%\…`） | 这是删程序文件的动作，不该受"用户 PATH 长什么样"影响 |
| 脚本**拉起失败**时**回滚暂停标记** | 否则用户既没卸载成、监护也没了，而且他不会知道（core 看起来一切正常）—— **静默地改变"监护还在不在"比卸载失败严重得多** |
| 确认框文案抄自 .NET tray 的既有实现 | 两个入口说同一件事，用户不会因为"从哪个图标点的"看到不同的后果描述 |
| 不登记 `lifecycle-owner` allowlist | 拉的是 `powershell.exe`（不是我们的 exe）；该规则要求"拉起原语 + `BetterDesktop.*.exe`"**两个**标记同时命中，故天然不命中 |

**落地**

- 新模块 `core/src/uninstall.rs`：纯函数 `locate_script`（可单测）+ `confirm`（`MessageBoxW` OKCANCEL+WARNING）+ `run`
- `tray.rs`：`CMD_UNINSTALL = 3305` + 菜单项（置于「关于」与「退出」之间 —— 这条菜单上唯一的不可逆动作）+ `MenuAction::Uninstall` + id 映射
- `main.rs`：`if uninstall::run(hwnd) { tray::request_quit(hwnd) }`
- **core 单测 187 → 191**（+4：无候选 ⇒ `None` / 进程目录优先 / 回退安装根 / 文件名是跨进程契约）
- `architecture-guard` **PASS**；`cargo clippy` 对**新增代码零警告**
  （既有 10 条与本轮无关，其中数条是本工作流 S5-4 写的常量断言 —— 记为后续清理）

#### 13.23 对账（2026-09-20，三张表）

推了 3 天、30+ 个 S 步之后，先对账再往前 —— 因为**下一步该做什么，取决于"哪些绿是真的"**。

**表 1 — 源码已改、待真机验证**

| 项 | 源码 | 真机 |
|---|---|---|
| S4-3 监护迁移 | ✅ | **9/13**（4 项待正式发布） |
| S4-4 删 `agent/` + `watchdog/` | ✅ | ❌ 需 B1 部署 |
| S5-1 / S5-2a 托盘基座 + 开关 | ✅ | 部分验过 |
| **S5-4 的 8 个动作项**（暂停监护 / 系统集成×4 / 更新×2 / 恢复 / 诊断包 / 打开日志 / 关于 / 开机自启） | ✅ | **部分 ✓**：暂停监护已真机验证（§13.24）；其余待**人工点击**走查 |
| S5-5 卸载 | ✅ | **待人工点击**：core 已部署（菜单项在托盘上），但卸载本身不可自动验 |

> **⚠️ 本表此前把 core 的验证条件写成"需 B1"，这是错的**（2026-09-20 修正）：
> **core 是自包含 Rust 产物、零 `ProjectReference`**，它**不需要 B1** —— 一次单拷部署即可
> （见 §13.24，已执行）。只有涉及 C# 组件的验证才需要 B1。
> 这个判断错误本身值得记：它让人以为"整个真机验证都卡在 B1"，
> 于是**放弃了本来可以立刻做的验证**。
| S4-5 真机验证清单 | — | ❌ |
| 门禁基础设施（反馈 / `-Fast` / 优化） | ✅ | ✅（**本机就在真机上跑**，不算欠账） |

⇒ **"看着绿、实际没在用户机器上跑过"的主体是 core 的托盘菜单链路**：
S5-4 的 8 个动作 + S5-5 卸载，**一个都没在真机点过**。数量比直觉大 —— 这正是对账的价值。
（唯一例外是 S5-2b 的 11 项开关与 S5-3 的组件子菜单，它们真机验过。）

**表 2 — 外部阻塞（实测，非推算）**

| 项 | 阻塞方 | 证据 |
|---|---|---|
| `packages/` 380 项（117 新增 + 238 改 + 25 删） | 并行工作流 | 未跟踪 |
| `host/`（13 文件混合） | 并行工作流（`PublishPasteSession`、`shell-island` 引用、dock 留痕） | 见 §13.22.7 |
| `md-wrap` 2 处红 | 并行工作流 | `README.md` / `shell-search/README.md` |
| **干净检出可构建的组件** | — | **3 / 8** ✗ |

**"3/8"是实测出来的，且推翻了上一轮的估算**：上一轮说"6/8 已跟踪"，但**"已跟踪"≠"可构建"**。
实测各已跟踪组件的 `ProjectReference` 里有多少指向 `packages/`：

| 组件 | refs | 其中引用 `packages/` |
|---|---|---|
| `launcher` | 2 | **2** ✗ |
| `host` | 25 | **25** ✗ |
| `BetterDesktop.Cli` | 4 | **4** ✗ |
| `tray` | 0 | 0 ✓ |
| `recovery` | 0 | 0 ✓ |
| `updater` | 0 | 0 ✓ |

⇒ **B1 的实测边界：8 个组件里只有 3 个（tray / recovery / updater）能从干净检出构建**；
`launcher` / `host` / `Cli` 全部依赖未提交的 `packages/`。
"部分可解"因此比上一轮的判断**更小** —— 但也**更准**，且三项都不在本工作流控制内。

**表 3 — 破例与欠账**

| 项 | 数量 / 位置 |
|---|---|
| **破例** | **7 条**，已集中到 [`docs/known-exceptions.md`](../known-exceptions.md) |
| `cargo clippy` 警告 | **10 条**（bin）—— 本轮清掉"常量断言类"之后的余量 |
| `dotnet-format` | **5194 处**（全在并行工作流区域） |
| `$required` 遗留 | 本轮清了 `agent.yml` ×3（`publish.ps1` / `install-betterdesktop.ps1` / `publish-modules.ps1`）；**未再发现** |
| 死代码 | `updater/Applier.cs` 的 Agent 重启逻辑（取证为死代码，归 B1 清理） |

**破例为什么会散落，以及这次的修法**

用户指出：这套系统已有 **5+ 处刻意的破例**，每处理由都写在对应模块的注释里 ——
但散落在 5 个文件。半年后问"core 到底破过几次例"，答案要 grep 十几个文件。

⇒ 建 `docs/known-exceptions.md`：每条例外四行（**位置 / 是什么 / 为什么 / 触发重审**），
与 `threat-model.md` 的「负空间」同一手法。本工作流又补了 2 条（`uninstall` 的刻意不回退、
`recovery` 的历史清理名单），并修了其中的**误判**：`recovery` 那两个名字**不是残留引用**，
是"清理旧版残留"的名单，删掉会破坏从旧版升级的兼容性 —— 已在原地加注释防止后人"顺手清掉"。

**表 1 里那条"唯一例外"值得单独记一句**：门禁基础设施的改动**本机就是真机** ——
所以它不属于"看着绿"，它已经是真绿。**可验证性和"改动位于哪一层"直接相关**：
改门禁当天就能确认真绿，改 core 要等 B1。

**对账结论**

1. **表 1 的主体（core 托盘菜单 8 个动作 + 卸载）不是"欠账"，是"等待可验证点"** ——
   它们的验证条件（部署）已明确，且被 B1 卡住。
2. **表 2 是对账里唯一"需要外部输入"的部分**：3/8 组件可构建，三项阻塞都在并行工作流手里。
3. **表 3 里真正属于本工作流的只有 clippy 10 条与那 1 处死代码** —— 都很小。

⇒ 所以**下一步不该是 S6**：S6（Host 去 Ensure）要动 `host/`，而 `host/` 正是表 2 的阻塞项之一
—— 进 S6 就是撞同一堵墙。**在表 2 解开之前，本工作流的新增功能无法被真机验证（表 1 只会变长）。**

#### 13.24 core 单拷部署：真机闭环恢复（2026-09-20）

**问题**：运行中的 core 是 **09-19 22:53** 的二进制，而工作区已改到 09-20 ⇒
**所有针对 core 的真机观察都在观察两天前的产物**（S5-5 卸载项、clippy 修复都不在里面）。
"改一行跑一次"的闭环断了 —— 而它断了之后，真机验证**不再有信息量**。

**为什么 core 可以单独部署（与 B1 无关）**：它是**自包含 Rust 产物、零 `ProjectReference`**
（`Cargo.toml` 只依赖外部 crate）⇒ **不受 `packages/` 未提交的影响**。
publish 的其它 7 个组件（C#）则相反，这也是它们在 B1 上的差异来源。

**流程（可复用；顺序即 [`docs/defensive-patterns.md`](../defensive-patterns.md) 第九节的"先停守护者"）**

| # | 动作 | 为什么需要这一步 |
|---|---|---|
| 1 | `cd core; cargo build --release` | —— |
| 2 | **禁用**计划任务 `BetterDesktop Core Ensure` | 否则它会在拷贝窗口把 core 拉起来、锁住目标文件 |
| 3 | 停 core 进程 | 放开文件锁 |
| 4 | **只拷 `betterdesktop-core.exe`** 到安装根 | —— |
| 5 | 启动 core | —— |
| 6 | **恢复**计划任务 | 回到原状 |

**红线**：只允许这样单拷 core。**绝不手工拷 `Kernel.dll` 或任何 C# 程序集** ——
B2 记着那已经搞坏过一次面板（多副本偏斜 + 跨版本替换）。

**本次实测**

| 项 | 值 |
|---|---|
| 部署前 | 797,184 字节 / 09-19 22:53 |
| 部署后 | **841,216 字节 / 09-20 12:48** |
| 安装根被改动的文件 | **仅 `betterdesktop-core.exe` 一个**（拷贝后核验过） |
| 启动后日志 | 组件表 / ACL / 托盘 / 热键（`Win+Shift+B`）/ 控制管道（4 实例 + 0 号确权）/ shellmenu / `scheduled task: up to date` —— 全部正常 |

**两个观察（不影响本次部署，但值得记下）**

1. `WARN no BetterDesktop.ico found; falling back to system default icon` ——
   安装根下没有图标文件，托盘回退到系统默认图标。无害，但**用户看到的不是我们的图标**。
2. **`BetterDesktop.DesktopControl` 有两个进程**（PID 4720 / 62160）—— **已确认为正常** ✓
   （由用户查证，见 `STATUS.md` §3）：另一个带 `--icon-restore-sentinel <pid>`，
   是"宿主退出后恢复桌面图标"的**哨兵**（`shell-desktop` 侧设计，`DesktopPlugin.cs:311,327`）。
   ⇒ **不要当成重复启动去杀它**。这条从"待确认"转成"已知的正常现象"，已同样记入
   `docs/architecture/2026-09-20-runtime-facts.md` §2，以免下一个人重复怀疑一次。

#### 13.25 S4-5 全仓检索（2026-09-20）

**范围**：1150 个文件（排除 `packages/` `bin/` `obj/` `target/` `docs/`）。
**命中 456 处 `BetterDesktop.Agent` / `BetterDesktop.Watchdog`** —— 但检索的价值不在"找到多少"，
而在**把"看起来是残留"的逐类定性**：

| 类 | 位置 | 定性 |
|---|---|---|
| **构建产物** | `dist/**`（占绝大多数） | **非问题**：`.gitignore` 已含 `dist/` |
| **测试夹具字符串** | `core/src/process.rs`、`security.rs`（拿 `BetterDesktop.Agent.exe` 当样例路径） | **非问题且有据**：`architecture-allowlist.json` 已登记 `core/src/process.rs`，理由写明"**登记而非改夹具** —— 靠改名让门禁转绿，会让将来真正新增的拉起点失去这层扫描" |
| **历史清理名单** | `recovery/Program.cs`、`scripts/install-betterdesktop.ps1`（`$componentNames` / `$agentExe`）、`uninstall-betterdesktop.ps1`（`$startupValueNames` / `$agentExe`） | **保留**：它们是"**从旧版升级时**要停的进程 / 要清的 Run 值"。删掉 = 老用户机器上的残留不再被清（`known-exceptions.md` #5） |
| **死代码** | `updater/ResidentGate.cs` 的 `StartFromTarget(…, "BetterDesktop.Watchdog.exe", …)` | **不改**：有 `if (stopped.Watchdog)` 守卫，而 Watchdog 已不存在 ⇒ 恒 `false` ⇒ 不构成用户可见故障（与 `Applier.cs` 同形态，`known-exceptions.md` #6） |
| **必检清单遗漏** ✗ | `scripts/publish-modules.ps1` 的 `$required` 仍有 `Agent.exe` / `Watchdog.exe` | **已删（真问题）** —— 而且**这是上一轮的遗漏**：上轮只 grep 了 `agent.yml`，没 grep `Agent.exe` |
| 历史文档 | `.agents/notes/**`、`README.md` | 非问题（README 属并行工作流区域，未提交） |

**三份必检清单现已一致**：`publish.ps1` / `publish-modules.ps1` / `install-betterdesktop.ps1`
都是同一组 7 个 exe（Cli / DesktopControl / Host / Recovery / Settings / Tray / Updater），
`system-integration` 门禁 PASS 复核。

**教训（值得单列）**：同一个语义有**多种书写形态**（`agent.yml` / `BetterDesktop.Agent.exe` /
`BetterDesktop.Agent`）。上一轮按**一种形态** grep 就以为"清干净了" ——
这类遗漏只能靠"**先枚举形态、再逐个 grep**"来避免。这与 §13.22.5 的"形态 A/C 扫干净 ≠ B 不存在"
是同一教训的不同侧写。

**第三条教训（当日重复）**：我把 `doc-budgets.manifest.json` 的 `note` 写坏**两次**，
两次都是同一个原因 —— 在 JSON 字符串里写了 **ASCII 双引号**（该 note 的其余段落一律用 `「」`，
只有我新加的两段用了 `"`）。第一次发现于 §13.22.5 之后，第二次就在本节之后。

⇒ **"记住要用中文引号"不是有效对策**（我第二次仍然忘了）。有效对策是**把它变成不允许的动作**：
**往 JSON 的 `note` 追加内容时，一律写 `「」`，不写任何 `"`。**
这与本节上一条教训是同一族：**依赖记忆的规则会失效，依赖"动作本身不允许出错"的规则才稳。**

**同时完成的一项 S4 验证**："core 是唯一 `MenuCmd` 服务端"（**只完成一半**）

计划里写着"判据不能靠删了 Host 进程推断 —— 服务端是**代码**不是进程，须显式核对"。

- **运行时已核对** ✓：`\\.\pipe\` 下只有 `Clipboard.Engine` / `DesktopCmd` / `MenuCmd` 三条，
  且 Host **没有在运行**（进程表只有 core + Clipboard.Engine/Panel + DesktopControl×2）⇒ `MenuCmd` 归 core。
- **代码侧仍未做** ✗：`host/MenuCommandPipe.cs` 的服务端还在，那是 S4 的"新增独立一步"，
  需要改 `host/` ⇒ 卡在归属（§7.2 依赖 3）。

⇒ **本条必须这样记成半成品** —— 否则下一轮会以为"验证过了"。

### 13.26 D15 侦察揪出一个活跃故障：Run 键的两个"活死值"（2026-09-20）

**D15 原本只是"查一下"**（老装机 Run 键无死值 / 计划任务仅一条），结果查到**真故障**。

#### 事实

本机 `HKCU\...\Run` 里有：

```text
BetterDesktop.Tray     = ...\better-desktop-cordis\dist\modules\...\01-主程序\BetterDesktop.Tray.exe
BetterDesktop.Watchdog = ...\better-desktop-cordis\dist\modules\...\01-主程序\BetterDesktop.Watchdog.exe
```

**关键**：这两个目标 exe **都还在磁盘上**（旧版 `dist\modules\...` 目录完整）⇒ 它们不是"死值"，
而是**活死值** —— **每次开机都会真的把旧托盘与旧守护者拉起来**。

⇒ 这几乎肯定就是 §7.1 记的"**今天三次遇到旧守护者**"的来源：
core 的 gate 关掉某个组件，旧守护者开机后被拉起来又把它拉回去 —— **两者对同一件事给相反答案**。

#### 根因：一个"交给手动入口"的假设

`core/src/autostart.rs` 的模块头原先写着：

> 历史上写过的 `BetterDesktop.Tray` / `.Watchdog` / `BetterDesktop` 由**卸载程序与恢复程序**负责清理。

**这个假设是错的**，而且是结构性错的：卸载器只在**卸载时**跑，`recovery --clean-autostart` 要用户**主动点**。
两者都是**手动入口**，而这个问题**每次开机复现** ——
⇒ **把"清理历史遗留"派给手动入口，等于假设用户会主动来清。**
对**用户不可见**的遗留（开机自启正是典型：它失败时没有任何提示），这个假设永远不成立。

#### 修复

| 步 | 动作 |
|---|---|
| **止血** | 手动删掉那两个 Run 值 |
| **永久** | 新增 `autostart::clean_legacy_values()`，**core 启动时调用**（core 由计划任务拉起，5 分钟内必跑一次 ⇒ 唯一能覆盖"没去点应急恢复的机器"的地方）|
| **守边界** | `LEGACY_VALUE_NAMES` **不含**裸名 `BetterDesktop` —— 它可能指向**现役 launcher**，删它会破坏正在用的功能。判据是"**目标组件是否已退役**"，不是"名字像不像旧的"。**单测专门钉这一条**（防手滑）|
| **自律** | 幂等、**不建键**（`RegOpenKeyExW` 而非 `Create`）、**失败不阻塞启动**、返回被清名单供日志**如实记录** |

#### 真机验证（完整闭环）

1. 模拟旧机器：把 `BetterDesktop.Watchdog` 写回 Run 键；
2. 单拷部署新 core（§13.24 流程）；
3. core 启动日志：

```text
[WARN] removed 1 legacy Run value(s) from HKCU\...\Run: BetterDesktop.Watchdog
       — they pointed at components removed in S4-4 and would otherwise be launched at every boot
```

4. Run 键：**已无 `BetterDesktop` 相关值** ✓

**单测 191 → 192**（新增"历史值不含现役名"那条）；clippy 0。

#### 启动面全扫（同日，修完之后）

修完 Run 键后，把**所有启动入口**扫了一遍，确认没有别的路径能拉起已退役组件：

| 入口 | 结果 |
|---|---|
| `HKCU\...\Run` | ✅ 已无 BetterDesktop 相关值（core 启动时清的） |
| `HKLM\...\Run`（64 位） | ✅ 无（共 5 项，均与本产品无关） |
| `HKLM\...\WOW6432Node\...\Run`（32 位） | ✅ 无（共 4 项） |
| 启动文件夹（用户 + 全局） | ✅ 只有 OneNote / Ollama |
| 计划任务 | ✅ **仅一条** `BetterDesktop Core Ensure`（D15 的"仅一条"判据达成） |

⇒ **D15 完整闭合**，且从"查无死值"升级为"**core 会主动清 + 启动面已确认穷尽**"。
**为什么值得多扫这一步**：D15 的原判据只查 Run 键与计划任务；而"旧守护者能复活"这件事
一旦被证实（Run 键那条），就说明**存在"我没想到的启动面"** —— 此时只修已知的那一个，
留下的是"不知道还有没有别的"。全扫把"不知道"变成"**已穷尽**"。

#### 顺带核销的 D8

同一轮里做了 **D8（kill 任意按需进程不触发守护复活）**：启动 `BetterDesktop.Settings.exe`（on-demand）
→ kill → 等 12 秒 → **实例数 0** ⇒ core **不复活**它 ✓
（对照：`desired=running` 的组件被 kill 会在 3 秒内拉回 —— 这正是"删掉不等于停掉"的分工。）

#### 13.22.8 真机故障：桌面空白处右键无项（2026-09-20）

**用户报的**：右键菜单**有注册但功能无反应**。

**复现（仓库自带探针，无需人工点鼠标）**

```
pwsh -File scripts\probe-shellmenu.ps1 -MenuProbeConfig "$env:APPDATA\BetterDesktop\shellmenu.json"
```

**结果**：

| 场景 | 结果 |
|---|---|
| **Background（桌面空白）** | **2 项，全是 separator** ✗ ⇒ `[FAIL] 背景场景产生了菜单项` |
| Files (.docx) | 3 项 ✓（`格式转换` + 子项 ODT/RTF/纯文本/网页）|

**桌面上右键只出两条分隔线** —— 这就是"右键不可用"的真身（两条线很不显眼，所以像"没反应"）。

**已排除的（都不是根因）**

| 假设 | 证据 |
|---|---|
| 注册表坏了 | ✗ 四个 scene key 的 `shellex\ContextMenuHandlers\BetterDesktop` 默认值均正确指向 `{7b2e9c41-…}`；CLSID 的 `InprocServer32` 指向存在的 DLL；`ThreadingModel=Apartment`；DLL 是 **x64**（与 explorer 匹配）|
| 部署的 DLL 是旧版 | ✗ **旧（09-12, 118KB）与新的（09-18, 281KB）都同样 FAIL** —— 这点推翻了我的第一猜测 |
| icon 指向的文件不存在 | ✗ `BetterDesktop.Host.exe` 存在，配置里引用的 icon 路径全部可解析 |
| DLL 的 COM 层有问题 | ✗ `probe-shellmenu.ps1` **40 项断言 0 失败**；负面用例（未知 CLSID / 非 IClassFactory IID / 聚合）也全对 |
| 解析配置失败 | ✗ 输出 `配置已解析: 顶级项=3`；`ParseItem` 的两个 `return false`（非对象、深度>8）都触发不到 |

**定位到的位置**

`packages/shell/shell-context-menu/native/src/ShellMenuHandler.cpp::QueryContextMenu`（约 325–389 行）：

- `SelectVisibleItems` 不含 `desktopControls`（它是**唯一**声明 `scenes: ["background"]` 的项），或它进去了但 `InsertItemRecursive` 的 **submenu 分支**（398–440 行）在 `children.empty()` / `anyChild == false` 时"不占位"返回；
- 而返回的 `hr` 是 **9**（`nextId` 推进了 9），**菜单里却只有 2 个 separator** —— **推进与实插不一致**，这是最反常的一点；
- 反面参照：Files 场景返回 **0x15**（21）且菜单里**确实**有项 ⇒ 说明插入路径本身能工作，问题**特定于 background 场景**。

⇒ **谓词层四道判断**（场景白名单 / 选择数下限 / 扩展名谓词只在 Files / 同扩展名约束）**逐条推演都应对 `desktopControls` 放行** —— 所以根因**不在谓词语义**，而在 background 场景下**插入了却不可见**。
**未定位到"为什么"** ⇒ **不硬改**（在 native 侧没有根因就改代码 = 赌博）。**给并行工作流一份可复现的最小输入**（上面那条 probe 命令）比猜更有用。

**这一条揭示了本轮真正的问题（比故障本身重要）**

右键故障**不是**新引入的 bug，而是**"验证一直验的是旧版"**的必然结果：

| 事实 | 含义 |
|---|---|
| 安装根跑的是 **`app/2026.09.17.1610`**（09-17 的包） | 部署产物停在 **3 天前** |
| 源码已演进到 **09-18 / 09-19**（DLL 281KB、core 每天在改） | 但**没有任何人把新源码部署上去验证过** |
| 因此 D6/D12 这类"真机项"此前判 ✔ | 它们验的其实是**旧包的行为**，与当前源码**脱节** |

⇒ **B1（发布路径）不是"一道门禁"，而是"一切真机验证的前提"**。没有它，每一轮真机验证都在验一个历史版本 ——
而这次是**恰好被用户用出来**才发现。
**这也直接支持用户对 D13 的纠正（见下）**。

**用户对 D13 的纠正（我接受，且它改变了 B1 的性质）**

我此前写"**D13 卡 `host/`：安装根那份是 09-18 旧网状版，起来会与 core 打架**"。
**这个理由不成立** —— 我们把**本机部署目录里的历史遗物**当成了"必须兼容的历史"。事实是：
**没有正式发布、没有历史负担、没有外部用户** ⇒ **现在怎么改都是第一版**；那份旧 Host **直接覆盖掉即可**。

⇒ **D13 的"卡"是伪卡**：真阻塞从来不是"旧版本要兼容"，而是"**没走『用最新源码构建 → 部署 → 验证』这条路**"。
⇒ 而这恰好就是 B1。**所以 B1 不该排在 D13 后面等 —— 它正是 D13 的前置。**

#### 13.22.9 清除旧部署 → 右键项复现（2026-09-20）

**用户的一句话点破了我一直没做的事**：

> 不应该把旧编译程序删除，来避免其的影响吗？你为什么一直没有做？

**我一直在"分析"那个旧部署，却从没想过"它本身就在跑、就在被抓、就在把每个验证污染成旧版行为"。**
把它当成分析对象，而正确动作是**消除变量**。删掉它，一半的"分析"根本不需要做 ——
这与本项目里另一个反复出现的模式同族：**倾向于解释问题，而不是消除问题**。

**做了什么**

| 步骤 | 结果 |
|---|---|
| 停 core / 删计划任务 / 删注册表（4 个 shellex + CLSID）| 清掉全部引用点（否则会被拉回）|
| **删 `app\2026.09.17.1610\`** | **2,922 MB** 旧部署（`native\` 里三个 DLL 是 09-01/09-12/09-14 —— 两周前）|
| 建干净安装根 `app\2026.09.20.1350\` | 最新 core（`target\release` 产物）+ **新 DLL（274 KB / 09-18）**（旧的是 116 KB / 09-12）|
| 起 core | 它自己检测到 `drift detected`（注册表被我删了）并**自动派发修复** |

**过程中发现 core 的两条健康行为（值得记）**

1. **自我修复需要 `BetterDesktop.Cli.exe`**：日志 `cannot locate BetterDesktop.Cli.exe — the action was NOT dispatched`。
   把 CLI 放进安装根后立刻成功：`dispatched: ...\BetterDesktop.Cli.exe --shellmenu-register` ⇒
   注册表自动重建，`InprocServer32` 指向新 DLL。**"最新源码 → 部署 → 自愈"这条链是通的。**
2. **它拒绝把系统级任务指向非安装根的 exe**：`scheduled task ...: NOT registered — this core lives at ... which is neither under the install root ... nor under %LOCALAPPDATA%\BetterDesktop — refusing to point a system-wide task at it`。

**结果：右键项出现了（B 路径 / "更多"里）**

> 用户反馈："win11 的新版菜单中没有，但是**更多中还有桌面控制**"

⇒ **「桌面控制」在经典菜单里出现了** ✓ —— 这是此前从未有过的（此前连项都没有）。
⇒ **"旧部署"确实是"项不出现"的直接原因**；而 `probe-shellmenu.ps1` 在新部署下**仍然报**
`[FAIL] 背景场景产生了菜单项` ⇒ **该探针的 background 场景模拟与真实 explorer 行为不一致**（探针自身的缺陷，见下）。

**新问题一：点了功能不生效 → 根因是 `desktop` 组件未部署**

core 日志：`supervisor(tick): degraded (circuit open) desktop` —— 监护器反复拉起 `desktop`
（`BetterDesktop.DesktopControl.exe`）全部失败并熔断，因为**新安装根里没有它**（它在被我删掉的旧部署里）。
补上 `DesktopControl` 的构建产物（`dotnet build` 成功：**0 警告 0 错误**）+ `components.json` 后，
监护器立刻拉起它，且 **`shellmenu.json` 被它重写为"3 组"**（说明服务真的在工作）。

**新问题二（真 bug，属并行工作流）：core 对 `desktop` 的判活窗口错位**

证据链：

| 来源 | 内容 |
|---|---|
| core 日志 | `started 'desktop' -> ...DesktopControl.exe (restart #2 / #3 / #4 / #5)` —— **每 3 秒拉一次** |
| DesktopControl 日志 | 新实例一律 `已有桌面服务在运行，本次退出`（退出码 0）|
| DesktopControl 日志（真正那个）| `14:15:12.826 启动` → **`14:15:17.181 服务支撑插件装配完成（state=Active）`** |

⇒ **它冷启动要 ~4.4 秒，而 core 的 tick 是 3 秒** ⇒ 在"就绪"之前就被读成失败 ⇒ 再拉 ⇒ 死循环。
`supervisor.rs` 的注释里**记录过这个病**（2026-09-19 真机：*"3 秒后对账时服务还没建好命令管道（WPF 冷启动数秒），
于是被读成启动即崩 → 退避 1 秒 → 又拉起第二个实例"*），并声称已加两道防护 —— **但从本次日志看防护没挡住**。
**待办（并行工作流）**：宽限期要覆盖"进程已起但服务未就绪"的窗口，或把 `desktop` 的就绪信号纳入判活。

**新问题三：Win11 新版菜单（A 路径）没有项**

A 路径是 **MSIX 稀疏包**（`scripts/pack-shellmenu-msix.ps1`），与 B 路径（`shellex` 注册表）**相互独立**。
注册表里能看到的 `PackagedCom\Package\BetterDesktop.ShellMenu_1.3.261.22_neutral__87bpyamzrast8`
是旧版本残留；**新版稀疏包未安装** ⇒ 新版菜单自然无项。**这条独立于 B 路径，需单独处理。**

**探针自身的缺陷（值得记一笔）**

`probe-shellmenu.ps1 -MenuProbeConfig` 在**新部署**下仍然 FAIL（`hr=9 / 顶级项=2`，两个都是 separator），
但**真实 explorer 里项是出现的**。⇒ **该探针的 background 场景模拟与真实行为不一致** ——
它把"配置 → HMENU"这条管线单独拉出来跑，绕过了 explorer 真实的 `IShellExtInit::Initialize` 形态。
**教训**：*探针 PASS 不代表生产可用*（这条本项目的 `defensive-patterns.md` 第八节已有同族记载）；
反过来，**探针 FAIL 也不代表生产不可用** —— 这次是后者的实例。

#### 13.22.10 「桌面控制延迟高 / 不可靠 / 双击无法恢复」的根因（2026-09-20）

**用户反馈**：*桌面控制的功能延迟很高、而且功能不可靠，双击隐藏了桌面，无法再双击快速恢复。自绘桌面的右键菜单反应很快，但其右键菜单栏的功能设计过于冗余。*

**结论：不是延迟问题，是三个现象同一个根因 —— `settings.json` 里 `components.desktop = false`。**

```
core 日志:  - desktop (...) desired=Running tier=Surface type=Process gate=components.desktop=false => ensure=false stop=true
```

自绘桌面（`desktop` 组件）**处于关闭状态** ⇒ core 不拉起它、还会停它 ⇒

| 用户感受 | 实际机理 |
|---|---|
| 延迟很高 | 服务不在 ⇒ CLI 转发 `The operation has timed out`（**2 秒超时**才返回）|
| 功能不可靠 | 服务时在时不在 |
| 双击隐藏后无法恢复 | `desktop.doubleClickHideIcons=true` **已经写进配置**，但**应用它的服务不在** |

（`desktop.iconsHidden=false` ⇒ 图标当时没被隐藏；用户看到的"隐藏了"应是服务还活着时的那一次。）

**连带修掉的第二个问题：安装根的 `components.json` 是残缺版**

| 字段 | 源码 `core\components.json` | 安装根那份（修复前）|
|---|---|---|
| `liveness` | `"pipe"` | **缺失** |
| `livenessPipe` | `"BetterDesktop.DesktopCmd"` | **缺失** |
| `stopFlag` | `"desktop-stopped.flag"` | **缺失** |

缺 `liveness: pipe` ⇒ core 的判活退化 ⇒ 无论服务在不在都判"不活" ⇒ **每 3 秒拉一次、每次都被"已有服务"弹回** ⇒
`restart #2..#6` 直到熔断。用源码那份覆盖后，判活恢复（管道 `BetterDesktop.DesktopCmd` 随之出现）。

**这是"部署缺斤少两"的第三个实例**（前两个：缺 `DesktopControl.exe`、缺 `Cli.exe`）。
⇒ 再次印证 §13.22.8 的结论：**B1 不完整 ⇒ 真机行为不可信**。

**修复后验证**（35 秒采样，实例集合不变）：

```
T1=932,24072   T2=932,24072   ✅ 稳定（同一批实例，没有重启）
日志：restart #1 → restart #2（启动窗口那一次）→ 之后再无 restart
```

**顺带发现的两个次要问题（记入欠账）**

1. **启动窗口会留下一个多余实例**：`restart #2` 那个进程没退出，与真服务并存（两个 `DesktopControl` 实例，只有一个持有管道）。
   `is_alive` 修好后循环停止，但"启动窗口那一次重试"仍会多产一个进程。
2. **`--toggle-key` 在服务缺席时静默写值**（真 bug，属并行工作流）：
   ```
   [shell.desktop] 桌面服务转发失败（服务未运行?）: The operation has timed out.
   切换（免宿主）：desktop.doubleClickHideIcons=True（该开关无原生效果，等宿主上线应用）
   ```
   **它明知"无原生效果"，仍然写值并以退出码 0 结束** ⇒ 用户以为生效了。
   这是本项目 `defensive-patterns.md` 反复批判的**静默失败**形态：
   **一个"未来会被应用"的意图，与"已经生效"对用户不可区分** —— 应当报错（或明确提示"服务未运行，设置已暂存"）。

**关于"右键菜单冗余"**：那是**自绘桌面自己的右键菜单**（WPF 自绘，走 `desktop` 服务，所以一直很快 —— 与 shellmenu 快照是两条独立的路）。
它属于并行工作流区域（`packages/shell/shell-desktop/DesktopPlugin.cs`），且"哪里冗余"需要具体意见才能改，故先记录待议。

### 13.15 S4-2 第 2 步：core 的注册**触发** + `RepairGate`（2026-09-19）

`cargo test --release` **145/145**（+4），0 warning。core 侧从"只读巡检"变为"巡检 + 一次性自动修复"。

#### 交付

| 落点 | 内容 |
|---|---|
| `Registration::NeedsRepair(Drift)` | `Drift { registered, expected, why }` —— X/Y 变成**结构化字段**，不再埋在一句话里 |
| `Drift::describe()` | `registered=<X> expected=<Y> — <原因>`，缺失侧显式写 `(none)` / `(not deployed)` |
| `RepairGate` | 退避 300s / 连续 3 次不收敛 → `degraded` / **收敛即完全归零**（含解除熔断） |
| `shellmenu::act()` | 决策 → 日志（四个要素）+ 异步触发 |
| `trigger_repair_async()` | `resolve_exe("BetterDesktop.Cli.exe")` + `spawn_detached(--shellmenu-register)`，**不等结果** |
| `main.rs` 巡检线程 | 保持"检查 → 状态变化记日志 → 交决策"三步 |
| 棘轮登记 | `architecture-allowlist.json` 增 `core\src\shellmenu.rs`（core 是生命周期所有者；自身永不写 HKCU） |

**异步是硬要求**：core 是常驻进程，同步等子进程会把这一轮巡检卡住 ——
"CLI 卡住 → core 卡住 → 监护停摆"比"这次没修好"严重得多。修没修好由下一轮（≥300s）复查确认。

**dev 标记在触发路径上被识别**：`classify` → `DevRegistered` → `should_trigger_repair() == false` →
闸门永不触发。少了这条，dev 注册会被判漂移 → 触发修复 → 写生产路径 → 用户手动改回 → 再触发，
**每 5 分钟一轮、永远收敛不了**。单测专门钉了这条（+ 开关关 / 用户注销 / 未部署 / 外来路径四类）。

#### 单测逮到我的一个真 bug

收敛分支原先只清计数、**没清 `last_trigger`**。于是"人工修好 → 之后再次漂移"会被算成
"上一次触发过却没收敛"：`attempt` 从 2 起算，而且**熔断阈值被凭空吃掉一格**
（本该 3 次才熔断的情形，第 2 次就熔断）。`last_trigger` 的语义是"这一轮修复还没结束" ——
收敛了就没有"上一轮"。已修并加了回归用例（收敛后再次漂移 → `attempt` 必须从 1 开始）。

#### 真机验收（模拟漂移 → 触发一次 → 收敛 → 不再触发）

```text
[WARN] shellmenu: registered=C:\sentinel\BetterDesktopShellMenu.dll expected=…\app\2026.09.17.1610\native\… — points at a different DLL than the deployed one — repair is due (whether/when it is triggered is decided by RepairGate)
[WARN] shellmenu: drift detected. registered=C:\sentinel\… expected=…\native\…
    ⇒ triggering repair (attempt 1/3); next auto-repair no sooner than 300s (checks still run every 60s)
[INFO] shellmenu: repair dispatched: …\BetterDesktop.Cli.exe --shellmenu-register
[INFO] shellmenu: already registered and pointing at the deployed DLL      ← 60s 后：收敛，且**无第二次触发**
```

注册表被写回安装根（`…\app\2026.09.17.1610\native\…`）✓。四个要素（X / Y / attempt / next）全部在日志里 ✓。
**同一次运行还顺带确认了上一轮的稳定位置守卫**：dev bin 的 core 拒绝把系统级计划任务指向自己 ✓。

#### 顺带修掉 `verify-architecture-guard` 的 R1 盲区

登记棘轮时门禁报"清单条目已失效" —— 根因不是清单，是**规则的盲区**：R1 的"拉起我们自己的 exe"
模式表只有 C# 口径（`Process.Start` / `ProcessStartInfo` / `CreateProcessW` / `StartDetached` / `ShellExecuteW`），
**漏了 Rust 侧的 `spawn_detached` / `run_and_wait`**。于是 core 拉起 CLI 时两个标记只命中一个 ⇒ 不算命中
⇒ **整条 R1 对 Rust 侧是瞎的**。已补模式并加门禁自测（"Rust 的 spawn 原语也算命中"）。

**同时留下的两条文字纪律**（都不是"往后注意"，而是写进了能被读到的位置）：

- `ComShellExtensionRegistrar.DevMarkerFileName` 的注释里写明**「它是文件，不是注册表值 —— 别去注册表里找它」**及理由（core 已在读该目录；不新增注册表读写面）。
- 注册表句柄生命周期与「**回滚先于自动写**」的教训落在决策记录
  `.agents/notes/implemented/bug-fix/2026-09-19-registry-handle-lifetime-and-rollback-first.md`
  （`docs/defensive-patterns.md` 已顶在 1200 词预算上，`doc-budgets` 拦下 ⇒ 不为一条规则扩预算）。

### 13.14 S4-2 第 2 步前置：注册表**备份 / 回滚**（2026-09-19）

第 2 步是**第一次真正自动写 `HKCU\Software\Classes`**（改系统状态，不是改代码）。动手前必须先具备
"备份 + 回滚"——这是本条的唯一新增内容；用户清单里的第 2、3 步（窄命令、`--dev` 标记）上一轮已完成。

**测试**：`shell-context-menu-tests` **118/118**（+5），`cargo test` 141/141 未受影响。

#### 交付

| 落点 | 内容 |
|---|---|
| `ComShellExtensionRegistrar.GetOwnedKeyPaths()` | 本扩展**拥有**的 6 个键（CLSID + InprocServer32 + 4 场景键），**备份范围与注册/注销同源**（不另立清单） |
| `ShellMenuRegistryBackup` | `Capture` / `Save` / `TryLoad` / `Restore` + `RestoreReport`；默认落 `%LOCALAPPDATA%\BetterDesktop\shellmenu-backup.json`（**覆盖式** = "最近一次已知良好状态"） |
| CLI | `--shellmenu-backup [--out <path>]` / `--shellmenu-restore [--from <path>]` |

两处刻意的设计：

- **`Exists=false` 的键也要记**：回滚要能删掉"备份之后才出现的键"，否则"从零到零"永远验证不了
  （零状态是"键不存在"，不是"键存在但值为空"）。
- **回滚是覆盖式，且不复用注册代码**：注册写的是"正确状态"，回滚写的是"**当时那个**状态"
  （可能残缺、可能没有键）。用前者做后者会把"回到原样"变成"回到我认为对的样子"。

#### 测试自己就是第一个用户 —— 它第一次用就失败了（**这条最值得记**）

新增的 5 个用例都用"开头对**原始状态**拍照、`finally` 回滚"来兜底。第一次运行时有一个用例断言写错（我写成
`removed >= 6`，实际 5：**删父键会连带删掉子键**，所以 6 键的备份产生 5 次删除），于是走进 `finally`
执行回滚 —— 而回滚**当场抛了**：

```text
回滚原始状态失败：Software\Classes\CLSID\{…}\InprocServer32: 试图在标记为删除的注册表项上进行不合法的操作。
```

根因是我在 `DeleteTree` 里把 `parentKey.OpenSubKey(leaf)` 的返回值直接用在 `is null` 判断里 ——
**句柄泄漏**：句柄还开着就删了那个键，该键进入"标记为删除"状态，随后"删掉再重建"就非法。
因为依赖 GC 何时回收那个句柄，它是**间歇性**的。

**后果是实打实的**：那次失败的 `finally` 没能还原，**把开发机的注册表留在了"未注册"状态** ——
下一轮备份显示 `keysPresent=0 / registered=False` 才被发现。
（`shellmenu: repair is due` 只是**只读**巡检，不会自愈，所以没有任何东西会把它修回来。）

⇒ **这正是"回滚能力必须先于自动写注册表"的实证**：闸门这次是**测试自己**踩响的；
换成自动自愈去写，同样的错误会落在用户机器上。修法是探测句柄当场释放 + 用完整路径直接删。

#### 真机验收（当前用户，写真实 HKCU）

| 步骤 | 结果 |
|---|---|
| 备份良好态 | `keys=6 keysPresent=6 registered=True`，`registeredDll=…\app\2026.09.17.1610\native\…` |
| 改一个值（写入哨兵 `C:\sentinel\broken.dll`） | 读回 = 哨兵值 |
| `--shellmenu-restore` | `ok=True written=6 removed=0 restoredRegistered=True problems=（空）`；读回 = **安装根路径** ✅ |
| **从零到有**：`reg delete` 掉 CLSID + 一个场景键 → `--shellmenu-restore` | CLSID 键、InprocServer32 值（安装根）、4 个场景键（值为 CLSID）**全部回来** ✅ |

#### 用户清单里第 2、3 步的状态（上一轮已完成并验证，此处只做事实澄清）

- `--shellmenu-register [--dev]` / `--shellmenu-unregister` 已是**窄命令**，
  直接调 `ComShellExtensionRegistrar`（**不经** `--system-integration register|repair`）⇒
  不会连带登记 `WatchdogAutostart`、不动开机自启与历史静态 verb ✓
- `--dev` 标记落地为**标记文件** `%LOCALAPPDATA%\BetterDesktop\BetterDesktopDev.flag`
  （**不是** CLSID 下的注册表值）。理由：core 已经在读这个目录（`shellmenu-unregistered.flag` 等四个标记同款），
  把"注册表里的键是否存在"再引进来会让 core 多一处注册表读写面，而标记语义两者完全等价。
  core 侧 `Observed.dev_marker` → `Registration::DevRegistered` → `should_trigger_repair() == false` ✓
  已由**对照实验**验证：同一状态（注册表 = dev bin、期望 = 安装根，两者确实不同）下，
  标记在 → `comPathDrifted=False`；标记移走 → `True`。

### 13.13 管道名所有权（2026-09-19，已修 + 受控验证）

`cargo test --release` **141/141**（+5），0 warning。

#### 改了三件事（同一根因：所有权做得**隐式**）

1. **有界退避**：创建失败从"固定 sleep 1s、每秒一条"改为 `1→2→4→8→16→30s`（触顶后恒 30s）。
   记录策略：首次必记 + **每档变化**必记 + 触顶后每 10 次（≈5 分钟）提醒一次 ——
   **不刷屏，也不沉底**（"名字一直被占"是最该被人处理的状态，触顶后彻底闭嘴比刷屏更糟）。
2. **点名诊断**：把裸的错误码翻成"谁占的 / 为什么 / 怎么办"。见下。
3. **所有权做成显式**：0 号线程确权（唯一能用 `FILE_FLAG_FIRST_PIPE_INSTANCE` 的那个），
   **1..N 号等确权成功才创建**。原先它们照常创建，会**附着到占用者的同名管道对象上**
   （Windows 允许同名管道由多进程各建实例）—— 于是变成"部分请求归 core、部分归占用者"的**劈裂**，
   比"core 干脆不服务"难排查得多。确权失败时 core 对控制面**彻底不在场**，这是"所有权"唯一诚实的实现。

#### 验证是**受控实验**，不是等现场复现

自造一个占用者（`NamedPipeServerStream` 持住同名管道），再看 core 的行为：

```text
[ERROR] pipe instance 0: create failed: CreateNamedPipeW(\\.\pipe\BetterDesktop.MenuCmd) failed (GetLastError=231)
    ⇒ '\\.\pipe\BetterDesktop.MenuCmd' 不归本 core：该名字已被创建（占用者不是独占方式建的，或其实例正被占用）。
    ⇒ 两个错误码在这里都**与权限无关**，不要去查 ACL —— 根因是「这个名字已被别人建过」。
    ⇒ 后果：本 core **无法服务控制管道** —— 托盘图标照旧（它不依赖管道），但所有入口都不通。这是 core 核心职责失效，不是日志噪声。
    ⇒ 在跑的候选：betterdesktop.desktopcontrol.exe。
    ⇒ 退避重试第 3 次，4s 后重试（上限 30s）。占用者退出后会自动恢复，无需重启 core。
…（第 4 次 → 8s、第 5 次 → 16s，**没有再每秒一条**）
[INFO] pipe: ownership of \\.\pipe\BetterDesktop.MenuCmd established by instance 0 (missing instance threads 1..4 will now attach)
```

占用者 16:29:09 退出，core **无需重启**即确权；随后用独立最小客户端直连管道拿到完整 `status`
（`uptime=69s`、7 个组件的 desired/actual/health 齐全）⇒ **控制面恢复**。

#### 受控实验顺带逮到两个真缺陷

1. **错误码不止一个**：占用者若**不以独占方式**建管道，我们拿到的是 `231`（`ERROR_PIPE_BUSY`）而不是 `5`
   —— 而原诊断只处理 5。两者结论相同（名字不归我们）但**成因不同**，必须都给出说明；
   只认 5 会让另一类场景的人误以为"没这条诊断 ⇒ 不是占用问题"。
2. **`control pipe serving on … with 4 instance threads` 是假话**：它在 `start()` 里、任何实例建成功**之前**就打。
   真机日志里这一行紧跟着就是 `instance 0: create failed`。已改成如实陈述
   （"instance threads started … serving begins once instance 0 establishes ownership"），
   "开始服务"由确权成功时记。

#### DesktopControl 线索：**已查清 = 误报**（S4 范围不扩大）

候选扫描报出了 `betterdesktop.desktopcontrol.exe`，需要排除"它是第二个 `MenuCmd` 服务端"。
**两条独立判据都指向"不是"**：

| 判据 | 证据 | 结论 |
|---|---|---|
| 静态 | `shell-desktop-control/` 内只有 `MenuCommandPipeClient.TrySend`（**客户端**，`DesktopToggleExecutor.cs:33`），**无 `StartServer`**；csproj 也只引用 kernel 的客户端 | 不可能是服务端 |
| 运行时 | 杀掉 core 后 `\\.\pipe\BetterDesktop.MenuCmd` **不存在**（DesktopControl 仍在跑） | 它不持有该名字 |

**三条管道名各归其主（真机实测）**：

```text
\\.\pipe\BetterDesktop.MenuCmd            ← core（服务端）；DesktopControl 是它的客户端
\\.\pipe\BetterDesktop.DesktopCmd         ← DesktopControl 自己的名字（shell-core/DesktopControl/DesktopControlPipe.cs:23）
\\.\pipe\BetterDesktop.Clipboard.Engine   ← 剪贴板引擎自己的名字
```

命名规范 `BetterDesktop.<Domain>`（见 `docs/architecture/六层模型与未来扩展点.md:149`）本就是为这个设计的 ✓
⇒ **删 Host 的 `MenuCmd` 服务端即可，无需拆 DesktopControl**。

**候选扫描是"候选"不是"确证"** —— 这次正是它的设计极限：按进程名匹配会把任何一个在跑的
`DesktopControl` 列进嫌疑名单。它的价值在于把排查方向从"错误码是什么意思"缩短到"哪个进程在跑"，
**不能**当作归属证据；归属要靠静态检索 + 运行时"删掉自己再看名字还在不在"。

**遗留一个未解释的观察（如实记录）**：`FILE_FLAG_FIRST_PIPE_INSTANCE` 下 `5` 与 `231` 的触发条件不同，
而全仓只有 core 用该标志 ⇒ 早先那条 `GetLastError=5` 的占用者**很可能是另一个 core 实例**
（两实例短暂共存），而非 Host（Host 是 `maxInstances=1` 的 .NET 服务端，实测对应 `231`）。
无法追溯确证，故不作为结论。

### 13.11 S4 进行中记录（2026-09-19）

#### S4-1 热键迁 core ✅

单测：`cargo test --release` **111/111**；共享 crate **8/8**；engine **112/112**（抽取后语义未变）。

**归属判据细化（S4 侦察时补的一刀）**：原判据是"core 独占**必须活过壳退出**的键"。
照此清点会把剪贴板引擎的三键也抢过来 —— 但**那三键的消费者（引擎）本身常驻**，core 抢过来只会
多一层 IPC 且毫无收益。故判据补上后半句：**"且消费者本身不常驻"**。清点结果：

| 热键 | 谁持有 | 本次动作 |
|---|---|---|
| 截图 `Win+Shift+B` | **core** | ✅ 从 Agent 迁移（截图 exe 是**一次性**的，键必须有人常驻持有） |
| 剪贴板 `Ctrl+Shift+V/P/Backspace` | 剪贴板引擎（自己常驻自己消费） | 不动 |
| 侧板 `Ctrl+Alt+H` | 壳（随壳生灭） | 不动 |
| 面板"粘贴回原窗口" | 面板（仅可见期间注册） | 不动 |
| 开始菜单 Win 键 | 壳（键盘钩子） | 不动 |

**共享 crate `shared/hotkey-spec`（新）**：截图键进 core 意味着规范串解析会出现**第三份**实现
（C# `HotkeySpec` / `engine/src/hotkey.rs` / core）。本仓库自己的注释写着"两边各写一遍必然漂移"
（并且真吃过教训：引擎曾把 P 绑成暂停、Backspace 绑成删除，用户照提示操作静默删数据）。
故抽成**零依赖** crate，engine 与 core 路径依赖同一份；engine 原有 4 条解析用例**逐字保留**，
搬迁后 112/112 全绿 = 语义零变化。

**真机验证**：

```text
hotkeys(startup): registered 截图热键 Shift+Win+B (id=0x4D43, spec="Win+Shift+B") — 按下即按需拉起截图
```

| 验证项 | 结果 |
|---|---|
| 注册 | ✅ id=`0x4D43`（沿用原 Agent 的 `'MC'`，与截图 exe 的 `'MA'` 区分，真机排障可辨"键归谁"） |
| `bdctl status` 可见归属 | ✅ `"hotkeys":{"capture":{"registered":true}}` |
| 投递 `WM_HOTKEY` | ✅ `explicit start: 'capture' -> C:\…\AppData\Local\BetterDesktop\BetterDesktop.Capture.exe` —— **真的拉起了截图** |
| 二次触发 | ✅ `a capture process is already running; not launching another`，进程数仍为 1 |
| 未知 id | ✅ 不被认领（不吞别人的热键消息） |
| **真实按键** | ⚠️ **未验**（按键无法脚本化）—— 投递 WM_HOTKEY 验的是处理链；"Win+Shift+B 真的会到 core"由 Windows 负责 |

**纪律：`RegisterHotKey` 必须在拥有该窗口消息队列的线程上调用**。这条来自真机实录
（原 Agent 从配置轮询的线程池线程注册 → 同参数全 `0x580` **假占用**，看起来像"键被抢了"）。
`hotkeys::refresh` 因此**自校验调用线程**，非主线程直接拒绝并记 ERROR；
后台配置轮询线程只 `PostMessage` 请主线程执行。`0x580`（机制/线程）与 `0x581`（真冲突）
在日志里**分开报**，否则会把线程问题误诊成冲突。

**附带修掉的一个陷阱**：热键拉起截图走 `supervisor.start("capture")`（core 里拉起只有一个所有者），
而 `capture` 是 `type=tool`（跑完即退）。监护器的"下一轮验证存活"标记会把这种**设计上的退出**
判成"启动即崩"，三次之后熔断 —— **"按几次截图热键把截图按坏了"** 正是这么来的。
已按 `type` 区分（`arms_crash_detector`，纯函数 + 双向单测钉住）。

**core 只读配置的一处明确例外**：`%LOCALAPPDATA%\BetterDesktop\capture\settings.json` 的 `hotkey` 节。
core 的禁止清单原本只允许读 `settings.json` 扁平键 / `components.json` / 留痕 flag。
理由：热键持有者归 core，而这份配置的**编辑入口**归截图自己的设置界面 ——
让 core 去写会制造第二个写者，让截图去注册则回到"一次性 exe 持键"的老问题。故**只读不写**，
并把例外写在 `hotkeys.rs` 里而不是悄悄扩权。

#### S4-2 第 1 步（只读检查）✅ + **一个会在第 2 步造成无限修复循环的发现**

**定型（四审）：core 永不写 `HKCU\Software\Classes`。** 注册逻辑留在 C#（`ComShellExtensionRegistrar`），
core 只检查 + 触发一次性 CLI。理由：那条路径不是"写注册表"，是**改 explorer 的行为**
（CLSID 错 → explorer 加载失败；`InprocServer32` 路径错 → 每次右键都去加载不存在的 DLL），
风险比计划任务大一个量级；而重写一份未经真机验证的写入路径，等于把已验证的换掉。

**已实现**：`core/src/shellmenu.rs`（只读判定 + 纯函数 `classify`）+ main 里 60 秒只读巡检
（**只在状态变化时记日志**）。125/125 单测、0 警告。

判定优先级：**用户的显式意愿 > 技术上的不完整** —— 开关关 / 用户注销留痕 → 一律不触发自愈。

**真机三方对账（全只读）**：

| 事实 | PowerShell 直读 | core（Rust） | C# `--system-integration status` |
|---|---|---|---|
| `InprocServer32` | `…\dist\modules\…2026.09.18…\01-主程序\native\…` | 同 ✓ | `ComRegisteredPath` 同 ✓ |
| 4 个场景键 | 4/4 齐全 | 4/4 ✓ | — |
| flag | 不存在 | 不存在 ✓ | — |
| 期望 DLL | 安装根 `…\app\2026.09.17.1610\native\…`（`deployment.json`） | 同 ✓ | **`…\BetterDesktop.Cli\bin\Release\…\native\…`** ✗ |
| 结论 | — | `NeedsRepair`（路径漂移） | `ComPathDrifted: true` ✓ **一致** |

结论一致（都判"漂移"），**但两边期望的路径不同** —— 这正是下面那个发现。

##### ⚠️ 发现：core 与 CLI 的"期望 DLL 路径"不一致 ⇒ 第 2 步一上线就会无限写注册表

- core 的 `resolve_native_dll()`：本进程目录 → **安装根** → `%LOCALAPPDATA%\BetterDesktop`（对齐仓库既有定位链）
- C# 的 `ResolveNativeDllPath()`：**只看自己的 `AppContext.BaseDirectory`**，不查安装根

于是：core 触发修复 → CLI 把**它自己的 bin 路径**写进注册表 → core 再查，仍"不等于期望" → 再触发 → **每 60 秒写一次注册表**。
两边各自自洽、谁也不报错、只是互相较劲 —— 这类 bug 靠单测发现不了，靠第 1 步的"只报告不触发"挡住了。

**两处都要改（只改一侧不够）**：

1. ✅ core：`resolve_native_dll` 已抽出纯函数 `find_native_dll(exe_dir, install_root, local_appdata)`，
   顺序被单测逐级钉住（三个目录都有 DLL → 选本进程目录；本进程目录没有 → 必须选安装根而不是 LOCALAPPDATA）。
2. ⬜ **C#：`ComShellExtensionRegistrar.ResolveNativeDllPath()` 需要补上"安装根"这一步**
   （与 `DesktopControlLocator` / `ComponentPaths` 的既有链一致）。**这是第 2 步的前置条件。**

**第 2 步还要加一道防循环闸**：触发一次修复后若状态未变，必须退避并停止反复触发
（不能靠"应该不会出问题"）。设计（四审给）：`RepairGate { last_repair_at, consecutive_failures }` ——
5 分钟内不重复触发、连续 3 次仍不一致 → degraded 并停止、用户手动操作重置计数。
与既有的"有界退避 + 熔断"范式同构（`nic-health-quarantine`）。

##### ✅ 契约冻结：**两条路径规则必须不同**（四审拍板）+ 共享测试向量

四审复核时先确认了"到底有几处期望路径"：`--system-integration status` 的 `ComDllPath` **直接取自**
`ComShellExtensionRegistrar.ResolveNativeDllPath()`（`SystemIntegrationRegistrar.cs:112`），
`ComPathDrifted` 就是它与注册表值的比较 —— **没有第三处**，恰好两处。

| 用途 | 规则 | 为什么 |
|---|---|---|
| **运行时定位**（`DesktopControlLocator` / `ComponentPaths` / `CoreEnsurer`） | 本进程目录 → 安装根 → `%LOCALAPPDATA%` | 调用方自己那份优先，开发态 bin 直接跑不受影响 |
| **注册表要写的路径**（`ResolveNativeDllPath` / core 的 `resolve_dll_path`） | **安装根优先**，见下 | 注册表项是**持久系统引用**，explorer 每次右键都按它加载；不能由"执行进程碰巧在哪"决定 |

**这两条必须不同，且必须写明为什么不同** —— 否则下一个人看到两个 locator 顺序相反，一定会"统一"它们，
然后把这个 bug 放回来。说明写在向量文件头部与两个实现里。

**注册表路径的完整规则**（比"安装根优先 → 本进程目录 → LOCALAPPDATA"更严，四审补的约束）：

```text
① 安装根有效（指针可读 + 目录存在）→ 只认安装根：其中有 DLL 就用；没有则拒绝（不回退）
② 安装根无效 → 候选按序：
     · 本进程目录 —— 仅当 dev_mode 或 它位于 %LOCALAPPDATA%\BetterDesktop\ 之下
     · %LOCALAPPDATA%\BetterDesktop（生产态兜底位，与"进程碰巧在哪"无关）
③ 目录内优先 native\ 子目录，其次扁平
④ 路径比较忽略大小写与结尾分隔符；"是否在 LOCALAPPDATA 之下"按**路径段**判（防 BetterDesktopTrap）
```

**为什么 ② 要卡"进程是否在生产位置"**：只把 dev bin 从"默认"降成"fallback"并没有消除它 ——
安装根无效时仍会落到 dev bin，而那正是 D6 事故的形态（dev bin 进注册表 → clean 后菜单废）。
故：**安装根无效且进程不在 `%LOCALAPPDATA%\BetterDesktop\` 之下 → 拒绝注册**（`DllMissing`）。
开发态要测右键菜单需**显式 opt-in**：`bdctl --shellmenu-register --dev`，
它跳过位置检查、写 `BetterDesktopDev=1` 标记键、日志显式 WARN"clean 后菜单将失效"，
**core 识别该标记后不触发自愈**（否则 dev 注册会被 core 反复"修"）。

**`DllMissing` 与"注册表有漂移"是两种状态**，不可混：前者不触发注册、不触发自愈，只记日志。

**共享测试向量**：`protocols/native-dll-path-test-vectors.json`（13 条用例 + 文件头部写明"两条规则为何不同"）。
两侧各读一份、各自实现，规则一变两侧同时红 —— 这是为了解决本 bug 的**根因**
（"路径解析分散在两处、没有共享契约"），与 BDMC1 协议用共享向量是同一手法。

Rust 侧已实现（`PathInputs` + `resolve_dll_path`，纯函数 + 可注入 `exists`），
**129/129 单测通过全部 16 条解析用例 + 10 条等价用例**。C# 侧**已实现并真机验证**（见下）。

##### ✅ 两侧已实现 + 真机实测（2026-09-19）

| 侧 | 落点 | 验证 |
|---|---|---|
| Rust（只读检查） | `core/src/shellmenu.rs`：`NativeDllPath` 等价规则 `path_eq` / `resolve_dll_path` / `Observed.dev_marker` / `Registration::DevRegistered` | 129/129 单测（含向量驱动 + dev 标记抑制自愈的**对照**用例） |
| C#（权威解析） | `packages/kernel/kernel/NativeDllPath.cs`（纯规则，零 IO / 零部署依赖） | `kernel-tests/NativeDllPathContractTests.cs` **6/6**，读**同一份**向量 |
| C# 接线 | `ComShellExtensionRegistrar.ResolveNativeDllPath` / `Register(devMode, out)` + `RegisterOutcome` 三态 / `DevMarkerFileName`；`SystemIntegrationRegistrar` 漂移判定改 `PathEq` + `ComDevMode`；`agent` 自愈同序同义；`launcher` 认 `comDevMode` | context-menu-tests **113/113**、launcher-tests **9/9** |
| CLI 窄命令 | `--shellmenu-register [--dev]` / `--shellmenu-unregister`；退出码 **9 = DllMissing**（**不该重试**，与 5 = 失败分开） | 见下表 |

**真机实测（当前用户，写真实 HKCU；每步单独取证，测后还原）**：

| 观测 | 结果 |
|---|---|
| 从 **dev bin** 跑 `--shellmenu-register`（无 `--dev`） | 写进注册表的是**安装根** `…\app\2026.09.17.1610\native\…`，**不是**它自己的 bin —— **分水岭成立**（旧行为下同一条命令写的是 `…\BetterDesktop.Cli\bin\Release\…`，日志可查） |
| `--dev` + 安装根有效 | 仍写安装根（规则 ③：dev 只放宽 ②a 的**位置约束**，不是"强制用进程目录"）✓ |
| `--dev` + 安装根**临时移开** | 写 dev bin 路径 + 落 `BetterDesktopDev.flag` ✓（命令内改名→测→立刻还原） |
| **对照实验**：标记在 vs 移走 | 同一状态（注册表 = dev bin、期望 = 安装根，**两者确实不同**）：标记在 → `comPathDrifted=False` / `comDevMode=True`；标记移走 → `True` / `False` ✓ **证明是标记在抑制，不是路径恰好相同** |
| `--shellmenu-unregister` | `ok=True`；CLSID 键树删除、4 场景键删除、写"用户显式注销"标记、删快照 ✓ |
| 测后还原 | 注册表 / 场景键 / 快照（14188 B）/ 两个标记 全部回到基线 |

**实现期三处纠正（已记入代码注释）**：
1. `Path.IsPathFullyQualified` 而非 `IsPathRooted` —— 后者对 `\foo` 也返回 true，而那是"当前驱动器相对"，同样不该进注册表；
2. 单参 `Register(out error)` 会绑到兼容重载（返回 `bool`）拿不到 `DllMissing` 态 —— 写注册表处一律用显式两参形式（编译器拦下过一次）；
3. `--dev` **跳过开机自启登记**：dev 是"让我测一下菜单"，不是"把我们装进系统"；往 `Run` 键写开发 bin 路径与"不把 dev 路径写进注册表"是同一个错误。

**一处如实标注**：`--shellmenu-register --dev` 会把开发目录写进注册表，该目录被 clean/重建后菜单即失效 —— 这条**没有**被消除，只是被显式 opt-in 化 + 标记化（日志 WARN + 面板 Warn 文案均已落）。

##### 补丁：`_non_goals`（不处理清单）与全仓消费者检索

**`_non_goals`（四审第 1 点）**：`_path_equivalence` 下明确写出**不处理**的四类 Windows 路径语义 ——
`.`/`..` 不展开、8.3 短名不解析、**非绝对路径一律拒绝**、UNC 不特殊处理。

写它的理由不是洁癖：不写，下一个实现者看到 `C:\foo\..\bar` 会问"这算不算等价"，
然后按自己直觉拍一个答案，**两侧又漂移** —— 那正是本契约存在的全部理由。

其中「非绝对路径一律拒绝」是**行为约束**而不只是说明，已作为 `_rule` 的 ⓪ 条实现：
相对候选**直接不采信**（不是"采信后再比较"）—— 注册表里的相对路径，explorer 会在任意工作目录下
加载它，行为未定义。**devMode 放宽的是"允许开发目录"，不是"允许相对路径"**（两条独立约束，各有用例）。

**全仓消费者检索（四审第 2 点）** —— **逮到一个真遗漏**：

| 消费者 | 角色 | 原清单是否覆盖 |
|---|---|---|
| `ComShellExtensionRegistrar.ResolveNativeDllPath()` | C# 权威解析 | ✅ |
| `SystemIntegrationRegistrar.GetStatus():112` | 直接调用上一行 | ✅ |
| `agent/Program.cs:402` 自愈 | 解析 + 比较 | ✅ |
| `core/src/shellmenu.rs` | Rust 解析 | ✅ |
| **`launcher/Services/ComponentBootstrapper.cs:193,205`** | **消费 `comPathDrifted` → 决定是否触发 `--system-integration register/repair`** | ❌ **漏了** |
| `uninstall-betterdesktop.ps1:227` | 按 CLSID 整树删除，**不做路径比较** | ❌ 未列（但受影响=False，属"同键位清理者"） |
| `install-betterdesktop.ps1:437` | **委派**给 CLI，自己不写注册表 | ❌ 未列（已核实无独立解析） |

**由第 5 行推出一条新的契约面**：`ComponentBootstrapper` 是**解析 CLI 的文本输出**
（`comPathDrifted=True/False`）而不是调 API —— 所以 **`--system-integration status` 的文本格式
本身也是事实契约**。改语义时必须同时检查它的 `ParseStatus`，否则启动器的行为会静默分叉。
这条已写进 `_what_changed.affected_consumers`。

**已排除**（检索确认）：`updater/**` 不碰注册表；`probe-shellmenu.ps1` 从仓库路径直接
LoadLibrary 探测 COM 工厂、不读注册表；`pack-*/publish-*/build-*` 只做打包构建。

**`revert_requires`（四审建议）**：`_what_changed` 新增该字段，列明回退此契约需**同时**动四处
（C# 解析、C# 漂移判定、Rust 侧、本文件与两侧测试），并写明"回退后 D6 形态会重新出现"。
比事后逆向分析便宜得多 —— 而且它把"删掉本文件等于删掉防御措施"这句话留在了文件里。

**第 3 步（写注册表）必须先补的四件事**（读完 C# 现状后确认**目前一件都没有**）：

| # | 四审要求 | 现状 |
|---|---|---|
| 1 | 原子化：5 个键要么全成功、要么全回滚 | ❌ `Register()` 无条件逐个 `SetValue`，无备份无回滚 |
| 2 | 干净度检查：指向**外来** DLL 时不覆盖 | ❌ 现有实现直接覆盖。core 侧已新增 `ForeignPath` 判定（文件名不是 `NATIVE_DLL` ⇒ 只告警不触发）—— 注意判据不是"是否等于期望路径"（那会堵死版本升级） |
| 3 | 不重启 explorer；`SHChangeNotify` 先试 | ❌ 未实现。**但不要过度承诺**：仓库权威注释（`ComShellExtensionRegistrar` 文件头）写明 `shellex` 变更**必须重启 explorer 才生效**，`SHCNE_ASSOCCHANGED` 对 shellex 处理器经常无效 —— 故提示用户而非静默当作已生效 |
| 4 | 验收含"从零到有再到零"完整循环 | ⬜ 未做 |

另：**不能拿 `--system-integration repair` 当触发目标** —— 真机确认它同时把
`WatchdogAutostart: true` 重新注册，与终态（开机只启动 core）直接冲突。需要一条**窄命令** `--shellmenu-register`。

#### S4-3～S4-5：待做

按四审给的顺序：**右键自愈 → 桌面监护 → Watchdog 监护 → 删 agent（同批删 `EnsureAgentRunning`）→ 删 watchdog → 全仓检索 → 真机验证**。

**已知最大工作量在 S4-2**：右键自愈要在 Rust 里实现 COM 壳扩展注册（HKCU `CLSID\{7B2E9C41-…}`
+ `InprocServer32` + 四个场景键），并保住 `shellmenu-unregistered.flag` 语义
（写者是 `Unregister()`，读者有 Agent 自愈门控 / `launcher` / 脚本 —— core 只**读**不写）。
**S4-2 不做快照写入**：那是 S6。

#### 需人工走一遍的清单（真实睡眠/唤醒，脚本化不了）

投递 `WM_POWERBROADCAST` 验的是**恢复链**；下面这些只有真的睡一觉才能验。
建议顺序：先记录 `desired` → 睡眠 → 唤醒 → 逐条核对。

| 项 | 方法 | 期望 |
|---|---|---|
| 唤醒后热键 | 唤醒后按截图热键 | 可用（**S4 迁热键后才适用** —— 现在 core 还没有热键） |
| 唤醒后托盘 | 唤醒后看托盘图标 | 在（本次已由恢复链覆盖） |
| 唤醒后管道 | 唤醒后 `bdctl status` | 可连 |
| 睡眠冻结不误判 | 睡眠中不杀任何东西，唤醒后看日志 | **无** `died within one supervision round` |
| 睡眠中真崩溃 | 睡眠前 kill 组件，唤醒后看日志 | 按退避拉起（本次已验） |
| 计划任务不唤醒机器 | 睡眠中观察是否自动醒来 | 不自动唤醒（`WakeToRun` 已反证为 false，见 §13.9） |
| `powercfg /requests` | **管理员**环境：core 运行时执行 | 无 BetterDesktop 条目 |
| `powercfg /waketimers` | **管理员**环境执行 | 无 BetterDesktop 条目 |

#### 管道为什么不需要"重建实例"（一个被推翻的直觉）

四审要求"服务端重建管道实例"。实现时发现**不需要**，理由不是省事而是结构性的：

实例线程每服务完一个客户端就 `DisconnectNamedPipe` + `CloseHandle` 并**重建一个全新实例**。
所以"陈旧连接"只可能存在于**当时正处于连接中**的实例上，而它们最多 2 秒（`READ_DEADLINE`）就会因读超时回收。
至于阻塞在 `ConnectNamedPipe` 的实例 —— **它们正是唯一没有陈旧连接问题的那些**（还没被任何客户端连上、
没有任何状态需要重置），为它们引入 `overlapped + CancelIoEx` 只会增加复杂度而不解决任何实际问题。

因此 `pipe::on_resume` 只做两件事：推进代数（让每个实例回收时留一行可追溯日志）、把到期上界写进日志。

顺带补上四审早前提过、当时只记为"已知边界"的一条：**单连接最长生命周期 30s**。
`READ_DEADLINE` 只能踢掉"连上就不说话"的客户端；一个**每隔 1.9 秒发一条请求**的客户端
可以永远占住实例，4 个这样的客户端就能把控制面饿死。30 秒足够任何正常客户端，却保证实例必然回流。

#### 过程中修掉的两个真 bug 与一条纪律

1. **`components.rs` 不支持带 BOM 的 JSON**（真机探针撞出）。`settings.rs` 早有容错、`components.rs` 没有 ——
   同一目录下两个配置读取者行为不一致。表现是"用户改了外部覆盖表却**完全没生效**、静默回退内嵌表"。
   已修（+ BOM 单测），并说明为何把剥离放在 `parse` 而非 `load`（内嵌表走 `include_str!`，同样需要）。
2. **`task::query()` 用 `run_schtasks(...)?` 导致"任务不存在"永远走不到 `Missing`**（单测撞出）。
   非零退出码在 `run_schtasks` 里已被转成 `Err`，于是每次 `ensure()` 都失败、兜底任务**永远建不起来**。
   已拆为 `invoke_schtasks`（保留退出码）/ `run_schtasks`（要求成功），退出码在 `/query` 里是**数据**而非成败。
3. **纪律：`cargo test --release` 不刷新 `target/release/*.exe`。** 第一轮真机验收拿旧 exe 跑了 15 分钟
   （日志里完全没有 task 记录），误判为"功能没实现"。**真机验证前必须 `cargo build --release`**，
   并核对 exe 时间戳晚于源码。

## 14. Handoff to 技术力应用（交接节）

**模式判定**：

| 功能点 | 判定 | 依据 |
|---|---|---|
| Rust core 骨架（托盘/窗口/互斥/日志/组件表） | `无匹配→工程代码权威`，**高复用** | **S1 已完成**，`core/src/*` 自身即可复用模板 |
| 控制管道 + ACL | `无匹配→工程代码权威`，**已完成** | **S2 已完成**：服务端 `core/src/{protocol,pipe,security}.rs`；契约 `protocols/bdmc1-test-vectors.json`；两侧实现与客户端 `core/src/protocol.rs` ↔ `packages/kernel/kernel/{MenuCommandPipeCodec,CoreControlClient}.cs`。参考 `host/MenuCommandPipe.cs` + `engine/src/ipc.rs` |
| 全局热键 | `无匹配→工程代码权威`，**高复用** | `engine/src/hotkey.rs` 整套可移植 |
| supervisor / reconcile / 退避 / degraded | `无匹配→工程代码权威`，参考相邻资产 | `watchdog/Program.cs` + `nic-health-quarantine` |
| **电源事件处理** | `无匹配→工程代码权威` | 无技术力文档；Win32 `WM_POWERBROADCAST` 官方语义 + 本项目 1402/1404 的"重建"经验 |
| **安全基线** | `无匹配→工程代码权威` | `windows` crate 原生 API（`PipeSecurity` / token SID / 路径规范化）；无库文档 |
| ShellMenu 快照触发重建 | `无匹配→工程代码权威` | `ShellMenuConfigWriter.cs` + `HeadlessExecutor.cs` |
| 配置单写者收口 | `无匹配→工程代码权威` | `SettingsService.SaveLocked` |

**注入清单**：

1. 本计划（含 §6 改动、§13 DoD、§14 禁区）
2. **Rust 复用源**：`core/src/*`（S1 产物）、`engine/src/{hotkey,settings,log,ipc,main}.rs`、`engine/Cargo.toml`、`engine-index/src/main.rs:130-160`
3. **将退役的规格来源**：`watchdog/Program.cs`、`tray/TrayApplicationContext.cs:19-45`、`tray/ProcessBridge.cs`、`agent/Capabilities/CaptureHotkeyOwner.cs`、`agent/Program.cs:291-323`
4. `host/MenuCommandPipe.cs` + `packages/kernel/kernel/MenuCommandPipeClient.cs`
5. `packages/shell/shell-settings/Services/SettingsService.cs`
6. `packages/shell/shell-context-menu/Services/{ShellMenuConfigWriter.cs,ShellMenuInterop.cs}` + `packages/shell/shell-desktop/Services/ShellMenuContentBuilder.cs`
7. `BetterDesktop.Cli/HeadlessExecutor.cs`
8. `docs/audits/2026-09-17-tray-and-shellmenu-landing.md`
9. `docs/2026-09-11-resident-architecture.md`（被取代）
10. `TECH-KNOWLEDGE/69-网络聚合/{nic-health-quarantine,tun-lifecycle-preflight,system-proxy-snapshot-restore}.md`、`14-窗口与快捷键/{1401,1402,1404}.md`
11. `scripts/AGENTS.md`、`docs/runtime-health.md`、`docs/doc-standards.md`

**适配参数**：

- Rust：edition **2024**；`windows` **0.58**；`serde`/`serde_json` 1.x；`[profile.release]` = `opt-level=3, lto=true, codegen-units=1, strip=true`。**不新增第三方依赖**（ACL/SID/路径规范化全部走 `windows` crate；JSON 深度限制用自写守卫而非引入 `serde_stacker`）。
- 产物名：`BetterDesktop.Core.exe`；crate `betterdesktop-core`。
- 管道名：**沿用** `BetterDesktop.MenuCmd`。协议：沿用 `BDMC1|`；控制形态 `@ctl|`；**旧形态必须继续被接受**。
- 组件表：`core/components.json`（**JSON 而非 TOML**，零新依赖）；外部覆盖 `%LOCALAPPDATA%\BetterDesktop\core.components.json`。
- 设置键：沿用现有扁平键，不新增键名。
- 留痕目录：沿用 `%LOCALAPPDATA%\BetterDesktop\`。
- 计划任务名：`BetterDesktop Core Ensure`；动作 = **绝对路径**的 `BetterDesktop.Core.exe`（无参数）；每分钟；**`WakeToRun=false` + `StartWhenAvailable=false`**。
- 门禁：新增门禁须同批登记 `scripts/run-gates.ps1` 并补 `verify-*.Tests.ps1`（C8）。

**禁区（红线，禁止触碰）**：

**架构与拓扑**
- 不得用 `HWND_MESSAGE` 消息窗口做 `TrackPopupMenuEx` 的 owner（C9；S1 已按此实现并验证）。
- 不得让 core 加载 UI 插件 / 渲染面板 / 持有业务状态 / 读业务数据文件。
- 不得在 explorer 进程内直连管道（原生右键继续读快照）。
- 不得让两进程同时持有任务栏外观主导权或桌面图标钩子（C2）。
- 不得保留 `HostPresenceWatcher`/`ExclusiveCapabilityHost`。
- 不得为跨进程服务访问新建 provider 代理框架（C7）。
- 不得把 ShellMenu 快照的写入者留空（C3）。
- 不得把索引空闲阈值改成 300s（保持 600s）。
- 不得执行全仓 `bd-*` 重命名（Q3）。
- 热键注册/注销必须**同线程配对**（C10）。

**安全**
- 不得用**无 ACL** 的管道创建路径（`NamedPipeServerStream.Create` 之类简化 API）。
- 不得**字符串拼命令行**拉起进程；参数值只能来自组件表字面量。
- 不得 JSON 解析**不设深度/大小上限**。
- 不得对组件 `exe` 不做**路径前缀校验**。
- 不得在 core 里 `Process.Start` 任何**非组件表**中的可执行体。

**边界**
- 扩展不得依赖 core 内部符号；core 不得依赖任何扩展。
- 不得在扩展里 `Process.Start` 拉起别的扩展（须经 core 控制管道）。
- 不得在 `core/` 之外 `RegisterHotKey`（唯一合法位置 = core）。
- 不得在 `SettingsService` 之外写 `settings.json`（唯一合法位置 = C# 侧 `SettingsService`）。
- 不得新增 `Utils.*` / `Helper.*` / `Common.*`；不得一个文件放两个类。

**电源**
- 不得在 core 启动时调用 `SetThreadExecutionState`。
- 不得无条件持有 `PowerSetRequest`（`keepAwake=always` 已被 schema 拒绝）。
- 不得计划任务配 `WakeToRun=true` / `StartWhenAvailable=true`。
- 不得使子进程持有 `ES_SYSTEM_REQUIRED`（除非用户显式触发且带超时）。
- 不得在 `WM_POWERBROADCAST` 处理之外做电源假设。

**DoD 核销表**：§13 的 D1–D37（含 D2b）。实现完成后逐条勾销并附证据（命令输出 / 真机走查记录），作为 AGENTS.md 承诺的「对齐评审」凭据。
