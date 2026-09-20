# 常驻架构：功能归属清单 + 承载机制（原则：除 Dock/菜单栏外全部常驻）

> 用户定调（2026-09-11）：
> **"除了 Dock 和菜单栏及其配套的功能以外，其他功能都是常驻组件及功能。"**
> 即：**壳体（Host）只留屏幕视觉元素**，其余能力必须能在壳退出后继续工作。
>
> 本文件 = 该原则落到 20 个插件上的**逐项归属** + **承载机制** + **分批迁移顺序**。

---

## 一、为什么不能靠"把插件搬来搬去"（先破两个幻想）

1. **不存在可搬移运行中插件的进程间机制**。`MenuCommandPipe` 是**单向单次命令**（服务端读一行即派发），
   没有任何"服务图交接"能力 → **热迁移不现实**。正确做法是 **启动时按依赖分流加载**：
   壳启动只加载视觉插件；常驻宿主启动只加载能力插件。
2. **"无 WPF 引用"做不到，但也没必要**。实测几乎所有能力包都经 `shell-core`/`shell-settings` 间接拖进 WPF：
   ```
   shell-status  → shell-core(UseWPF)     shell-recent → shell-core + window-tracker
   shell-calendar→ shell-settings         shell-convert→ context-menu + settings
   shell-desktop → shell-core + ...       shell-clipboard → shell-core + settings
   shell-app-source → Kernel + Api ✔      shell-search → Kernel + app-source ✔      shell-pinning → Kernel + app-source ✔
   ```
   真正的约束不是"引用 WPF"，而是**是否创建窗口 / 是否触碰 `Application.Current`**。
   **现成范本**：宿主已有 `--menu-cmd-hosted` 静默装配（无 splash、无主界面、仅装配服务，`App.xaml.cs:89-94,206-218`）——
   Agent 就是"这段静默装配 + 只加载能力插件 + 无窗口 Application 提供 Dispatcher"。

---

## 二、功能归属清单（20 个插件逐项判定）

判定口径：**"关掉壳体后，它还有独立价值吗？"** 有 → 常驻；没有（纯视觉，或唯一消费者就是 Dock/菜单栏）→ 留壳。

### A. 留壳（壳体视觉 = 用户关掉主程序就该消失）

| 插件 | 理由 | 备注 |
|---|---|---|
| `shell-dock` | Dock 视觉本体 + AppBar 协商 | AppBar/自隐藏 tick 属窗口配套 |
| `shell-menu-bar` | 菜单栏 + 状态条自绘 + **托盘接管(SystemTrayIcon)** + Logo 菜单 | 用户明确划归"配套" |
| `shell-start-menu` | 开始菜单视觉（`WH_KEYBOARD_LL` Win 键钩子属其触发器） | 钩子可后续迁 Agent 以支持"壳退出后 Win 键仍开开始菜单"（可选） |
| `shell-quick-note` | 常驻浮窗本体（纯视觉） | 便签**数据**建议下沉（见 B） |
| `shell-window-tracker` 的**缩略图/Peek** | `Thumbnail/*` 是 WPF 视觉 | 追踪能力下沉（见 B），缩略图留壳 |
| `shell-desktop` 的**自绘窗口/画布** | `DesktopWindow`/`DesktopIconsControl` 是视觉 | **图标显隐能力下沉**（见 B） |

### B. 常驻（能力本体下沉到 Agent；其 UI 由壳按需显示）

| 插件 | 下沉的部分 | 留壳的部分 | 依赖提示 |
|---|---|---|---|
| `shell-desktop` | **原生图标显隐**（`WH_MOUSE_LL` 自判定双击 + 类名白名单 + `ShowWindow`，点击路径零跨进程消息） | 自绘桌面窗口/图标画布 | 需从 `DesktopPlugin` 拆出（该文件已含窗口创建） |
| `shell-status` | **全部采集**（电池/CPU/内存/网络/音量/麦克风/亮度/IME）+ 媒体(SMTC) | 无（绘制本就在 shell-menu-bar） | ✅ 零 `System.Windows` 引用，最易迁 |
| `shell-taskbar` | 任务栏外观引擎 + `ExplorerTAP` 注入维护 | 设置分区 UI | 注入后代码活在 explorer；Agent 负责重建/重注入 |
| `shell-clipboard` | **不走 Agent**：由独立 Rust 引擎进程（监听/捕获/存储/热键）+ 面板 exe（入口面）承载，宿主仅作 IPC 客户端（legacy 路径保留为回退） | `ClipboardHistoryWindow`（legacy 回退实现） | 见 `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md` |
| `engine-index` | **独立原生进程**：文件/应用/图标索引的事实源，被 `shell-app-source` 与 `shell-search` 经 IPC 消费（不可用时回退本地实现） | 无 | 见 `docs/plans/2026-09-13-native-index-service-rust.md` |
| `shell-app-source` | 应用源 + `FileSystemWatcher` | 无 | ✅ 引用仅 Kernel+Api |
| `shell-search` | 文件搜索服务 | 开始菜单搜索框 | ✅ |
| `shell-recent` | 最近程序/文档/跳转列表 | 无 | 依赖 window-tracker |
| `shell-pinning` | 固定/收藏数据 | 无 | ✅ 引用仅 Kernel+app-source |
| `shell-calendar` | 农历/节假日/节气/天气数据 | 日历面板 | |
| `shell-convert` | 转换/压缩/解压/引擎管理（已有 headless 路径） | 右键菜单 UI | CLI 已能无宿主执行，Agent 只是"常驻化" |
| `shell-context-menu` | 注册表接管 + 常驻 STA COM 服务 | 菜单弹窗 | |
| `shell-notification` | 通知服务 | 通知弹窗 | 需 `IAppearanceService` |
| `shell-music` | 音乐 API 服务（当前**无 IPlugin**，需先接入） | 无 | |
| `shell-settings` | `SettingsService` + `AppearanceService`（主题令牌） | 设置中心窗口 | Agent 需 Provide，供其他能力读取 |
| `packages/api/*` | 纯契约库，随宿主/组件共载 | — | |

---

## 三、承载机制：`BetterDesktop.Agent.exe`

```
BetterDesktop.Host.exe（壳）        ── 只加载：dock / menu-bar / start-menu / quick-note / desktop-窗口 / thumbnail
        ▲ 共享 settings.json（唯一配置源，双方都用 SettingsService 读写同一份）
        │
BetterDesktop.Agent.exe（常驻）     ── 【现状】status / app-source / pinning / search / convert
        ▲                             【门控独占】桌面图标显隐 + 任务栏外观（壳走则装载，壳回来则卸载）
        │                             【目标】+ window-tracker / recent / calendar / notification / music / context-menu

        │ 由托盘托管（启动/停止/自启/健康状态）
BetterDesktop.Tray.exe（托盘）      ── 控制面：开关功能、拉起壳、更新、查看 Agent 健康
```

**装配方式（照搬宿主静默装配，去掉窗口）**：
1. `new Application { ShutdownMode = OnExplicitShutdown }`（**无窗口**，只为提供 Dispatcher，供插件 marshal）→ `app.Run()` 起泵；
2. `new CordisContext(logSink: agentSink)` + `DiagnosticLog.SetSink(...)`（Agent 自带轻量 sink，不复用 host 的 `FileLogSink`）；
3. `context.Provide<ISettingsService>(new SettingsService(context))` + `ISettingsSectionRegistry`；
4. **不 Provide `IWindowHandleService`**（需要它的插件不进 Agent 清单）；
5. `LoaderService(new LoaderOptions { ConfigPath = "agent.yml", Factories = {能力子集} })` → `context.Plugin(loader).AwaitAsync()`；
6. 单实例互斥 + 插件状态落盘（与宿主同款 `[Bootstrap] plugin X => State` 诊断）+ `--selftest`。

**关键纪律（避免重蹈今天的坑）**：
- `agent.yml` 只列**已注册工厂**的 name（LoaderService 对未注册 name fail-closed）；
- Agent 与壳**绝不各自缓存配置**：一律 `SettingsService` 读写 `settings.json`；
- 能力接口的"跨进程可见性"暂不做（见下）。

---

## 四、暂缓项：跨进程服务访问（明确不做，避免过度设计）

壳要消费 Agent 的服务（例如 Dock 要 `IAppSourceService`）时，**暂不新建 IPC 服务框架**，按需二选一：
1. **文件/管道命令**（现状能力）：Agent 写状态文件或经 `MenuCommandPipe` 收命令；
2. **两边各起一份**（只在数据是只读且廉价的场景，例如 status 采集）——代价是重复采集。

> 若将来确有需要，再引入"服务代理层"（Provide 的跨进程实现）。**现在做等于凭空造框架**，会把风险放大。

---

## 五、分批迁移顺序（每批都能独立验收）

| 批次 | 内容 | 验收 |
|---|---|---|
| **B1 ✅ 已完成** | Agent 骨架（`BetterDesktop.Agent.exe`，无窗口 Cordis 宿主）+ 首批能力：`status` / `app-source` / `pinning` / `search` + 托盘"常驻服务"菜单（启动/停止/状态）+ 发布链路纳入 Agent | **实测通过**：`--selftest` 四个插件全 `Active`（loader=Active）；常驻 PID 47160 / 63MB；`--stop` 优雅退出（"卸载插件 + flush 设置" → 已退出）；发布门禁含 `Agent.exe` + `agent.yml` |
| **B3 ✅ 已完成** | 桌面图标显隐下沉：`agent/Capabilities/DesktopIconsCapability.cs`（移植已实证的纯净实现：自判定双击 + 类名白名单 + `ShowWindow` + `IsWindowVisible`，点击路径零跨进程消息）；意图与壳共享 `desktop.iconsHidden` | **实测通过**：门控接管/让出双向正确（见下表交接日志） |
| **B4 部分 ✅** | 任务栏外观下沉（独占能力：动态装载/卸载 `TaskbarAppearancePlugin`）+ **格式转换**下沉（`shell-convert` 常驻加载，引擎装载+预热）；**剪贴板不走 Agent**（已由独立 Rust 引擎进程 + 面板 exe 承载，宿主仅作 IPC 客户端；2026-09-12 S8 宿主集成收口） | **实测通过**：接管时 `Win11 注入成功` + `Apply Solid => True`；让出时`已还原任务栏到系统默认外观`；再接管时重新注入并恢复；`shell-convert => Active`（本机仅 tesseract 就绪，其余引擎未安装） |

### 独占能力门控（本轮新增机制，务必理解）

**问题**：图标钩子与任务栏外观**不能两进程同时持有** —— 双钩子会让一次双击切换两次（净效果为零）；双主导会让 ACCENT/注入互相覆盖。

**做法**（`agent/Capabilities/HostPresenceWatcher.cs` + `ExclusiveCapabilityHost.cs`）：
```
Host 在场  → 常驻能力待命（不装钩子 / 动态卸载任务栏插件并还原默认外观）
Host 退出  → 常驻能力接管（装钩子 / context.Plugin(TaskbarAppearancePlugin) 现场加载并注入）
```
- 探测：2s 轮询进程名（廉价，与 watchdog 同款）；**探测失败时保守按"壳在场"**（宁可待命，也不冒双钩子风险）；
- 任务栏外观用的是 **Cordis 动态插件装载/卸载**（`context.Plugin(...)` / `DisposeAsync()`），不新建 IPC；
- 交接实测日志（2026-09-11 16:25:58 → 16:26:20）：接管→让出（还原默认外观）→再接管（重新注入并套用）全部正确。

> 观察项（**非本次引入**，但值得排期）：
> 1. `[TaskbarAccent] RefreshAll` 日志每秒 3~5 条 → 高频刷新是既有行为，当初那批"COM 轰炸"归因就来自它；现在它跑在 Agent 里，若出问题会先在 agent 日志暴露。建议把该日志降到 Debug 级并复核刷新频率。
> 2. `CoCreateInstance(AppVisibility) 失败 hr=0x80040154` → Win11 上该 COM 组件不可用，属既有降级（开始菜单联动不生效）。
> 3. `soffice/pandoc/ffmpeg` 未安装 → 该机转换能力受限，属环境问题。
### 剩余批次（待迁移，按优先级）

> 下表只列**尚未落地**的项；已完成项见上表（B1 / B3 / B4-部分）。
> 原 B4 中的"剪贴板"**不走 Agent**——已由独立 Rust 引擎进程（数据面）+ 面板 exe（入口面）承载，
> 宿主仅作 IPC 客户端（2026-09-12 S8 收口；见 `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md`）。
> "任务栏外观与格式转换"已随本轮完成。

| 批次 | 内容 | 验收 |
|---|---|---|
| **B2** | `recent` / `calendar` / `notification` / `music`（数据与通知服务） | 壳退出后重新打开，数据不丢、不重复初始化 |
| **B5** | `context-menu`（注册表接管 + 常驻 STA COM） + `start-menu` Win 键热键（可选） | 壳退出后右键菜单/开始菜单行为不变 |
| **B6（收尾）** | 壳的清单拆成 `host.yml`（只留 A 类视觉插件），彻底避免两进程重复加载 | 两进程插件清单无交集；`cordis.yml` 只列视觉插件 |

**留壳插件**在 Agent 运行时的行为：壳启动时**只加载自己那份 yml 清单**（把 A 类插件从 `cordis.yml` 里拆到 `host.yml`），
两进程共享 `settings.json`，互不重复加载。

---

## 六、与既有纪律的衔接

- 跨进程窗口消息禁令（桌面链）继续有效：Agent 承载图标显隐时**点击路径仍必须零跨进程消息**；
- 失败不得正常化：Agent 启动失败要显式记录 + 托盘可见（不能"看着在跑其实没装配"）；
- 注释即契约：因果结论附可复现命令（见 `docs/2026-09-11-comment-audit.md` §4）。
