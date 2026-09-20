# 桌面控制独立化（脱离主程序）· 2026-09-17

> 用户需求（原话）：「让桌面控制这个功能做成独立于主程序外独立功能，可以在主程序不启动的时候也能够正常使用。
> 像剪贴板历史功能一样的级别」，并给出边界：
> **热键面板 → 像剪贴板一样独立；菜单栏 / Dock → 需要主程序；自绘桌面 + 连带的自绘右键菜单 → 不需要主程序。**

## 1. 目标（M1，本次落地）

主程序（`BetterDesktop.Host.exe`）**未运行**时，「桌面控制」菜单仍然：

1. **能弹出来**——不依赖宿主进程、不依赖命令桥；
2. **勾选态是真的**（读当前设置，不是我方缓存的过期快照）；
3. **点了真的生效**——
   - 桌面图标显隐 / 隐藏任务栏：走 **explorer 原生层**（`ShowWindow(SysListView32)` / `Shell_TrayWnd`）立即生效；
   - 剪贴板历史：直接拉起面板 exe（面板 + 引擎本就不依赖宿主）；
   - 菜单栏 / Dock / 热键面板 / 双击隐藏图标：**置灰 + 注明「需启动 BetterDesktop」**（用户拍板：不假装能点）。

## 2. Current Behaviour（[verified] 2026-09-17）

| 路径 | 现状 | 无宿主真实结果 |
|---|---|---|
| explorer 桌面右键「桌面控制」 | 原生 COM 扩展读 `%APPDATA%\BetterDesktop\shellmenu.json` → 静态**二级子菜单**（`ShellMenuContentBuilder.BuildDesktopControls`） | 菜单能出（快照是文件），但子项动作是 `toggle-key` → CLI 只**直写 settings.json**，没有人消费 → **「点了没反应」**（用户实测） |
| 宿主内自绘桌面右键「桌面控制」 | `DesktopIconsControl` → `DesktopControlMenu.Build/ShowAtCursor`（进程内 WPF 菜单） | **完全出不来**（宿主不在 = 桌面层不在） |
| 注册表 `Directory\Background\shell\BetterDesktop.Ui` | `DesktopSystemMenuRegistrar.EnsureUiTogglesRegistered` **无任何调用方**（死代码，2026-09-11 COM 方案已取代它） | — |
| 剪贴板历史… | `Host.exe --menu-cmd clipboard-history`（注册表项同样指向宿主 exe） | 宿主不在 → 「需要 BetterDesktop 正在运行」 |

**结论**：菜单的「渲染」这一半在无宿主时靠静态快照还能出现，**「执行」那一半全程依赖宿主**；
而带勾选态 / 置灰 / 条件项这些"每次现算"的能力，静态快照结构上做不到（原生侧 `enabled` 字段写死在快照里，
快照又只能由宿主在运行时写 → 快照里永远是"宿主在线"）。**故必须由独立进程在点击那一刻现算并自己执行。**

## 3. Relevant Architecture（[verified]）

- 原生扩展支持**叶子项自带 action**：`ShellMenuHandler::InvokeCommand` → `CommandDescriptor{action,args}` →
  `BuildBatchJson` → `Cli.exe --menu-batch <file>`（`native/src/Launcher.cpp`、`ShellMenuHandler.cpp:19-31,496`）。
  → **父项做成叶子命令即可把"点开"这一步转交出去，无需改原生代码**（原生产物不重编、MSIX 不重签）。
- 批协议动作集由 **C# 侧**把关：`HeadlessExecutor.ClassifyBatch`（`BetterDesktop.Cli/HeadlessExecutor.cs:99-104`）。
- 设置唯一契约：`%APPDATA%\BetterDesktop\settings.json`，读写入口 `SettingsService`（`context` 可空 → headless 可用，
  跨进程 mutex `Local\BetterDesktop.Settings.json`，读-合并-写）。
- 宿主在线探测：进程名 `BetterDesktop.Host`（托盘 `ProcessBridge.IsHostRunning`）+ 命名管道 `BetterDesktop.MenuCmd`
  （`MenuCommandPipeClient.TrySend`，客户端在 kernel，公开可复用）。
- 原生层原语（免宿主可用）：`DesktopHostWindow.FindIconListView()` + `NativeMethods.ShowWindow/IsWindowVisible`
  （shell-core）；`NativeTaskbarManager.SetTaskbarVisible(bool)`（shell-core，已处理 Win11 XAML 任务栏）。
- 独立 exe 模板：`BetterDesktop.Clipboard.Panel`（`WinExe + UseWPF + app.manifest`，Mutex 单实例，命名事件转发）；
  打包在 `scripts/publish.ps1`（与 Host 同目录部署）。
- **键名三处漂移是既有隐患**（CLI `ToggleKeyMap` / 宿主 `Bootstrap` `case "toggle-key"` / `Host ToggleKeyCommand`，
  源码注释已自陈）→ 本次把"命令名 → 设置键/默认值/可执行性"收到 shell-core 单点。

## 4. 设计（M1）

### 4.1 新进程 `BetterDesktop.DesktopControl.exe`（项目 `packages/shell/shell-desktop-control`）

职责：**只做「桌面控制」这一件事**——在光标处渲染菜单、承接点击、执行动作。

- 入口：`--desktop-controls`（默认，无参同义）；短命进程（菜单关闭即退出），故**不进看门狗**（避免被反复拉起）。
- 菜单内容 = 复用 `DesktopControlMenu.Build`（与宿主内那份**同一实现**，不复制）；
- 宿主在线 → 走命令桥 `toggle-key` 热切（宿主进程内改设置 → 事件 → 组件即时反应）；
  宿主不在 → 直写 settings.json + 原生层直接生效（图标 / 任务栏）；
- 剪贴板历史 → `ClipboardEngineLauncher.OpenPanel()`（面板 exe，**不经宿主**）；
- 承载窗：1px 不可见窗口，作用有二 —— ① 提供 DPI / 工作区换算源（贴边收敛不退化）；② 让本进程取得前台，菜单可交互。

### 4.2 菜单入口改指独立进程

`ShellMenuContentBuilder.BuildDesktopControls`：由「静态二级子菜单」改为**单叶子项**
（`Action = "desktop-controls"`）→ 原生扩展 → CLI 批协议 → 独立进程。
好处：不重编原生、保住「Windows 11 新菜单里能看到」（注册表项只能进"显示更多选项"）、内容每次现算。

### 4.3 免宿主的执行真值

| 菜单项 | 无宿主行为 |
|---|---|
| 桌面图标显隐 | 原生 `ShowWindow(SysListView32)` 立即生效（写设置 + 生效） |
| 隐藏任务栏 | 原生 `NativeTaskbarManager.SetTaskbarVisible` 立即生效 |
| 剪贴板历史… | 拉起 `BetterDesktop.Clipboard.Panel.exe --open` |
| 双击隐藏图标 | 置灰「需启动 BetterDesktop」（钩子在宿主进程；M2 后归独立进程） |
| 菜单栏显隐 / Dock 显隐 | 置灰「需启动 BetterDesktop」（用户拍板：需主程序） |
| 热键侧板显隐 | 置灰「需启动 BetterDesktop」（M3 独立化后转为可用） |

### 4.4 优先级：「桌面控制」的显式选择 > 其它功能的默认隐藏效果（用户 2026-09-17 追加拍板）

现实里唯一的冲突：**dock 启用即默认隐藏原生任务栏**（2026-09-02 定稿的联动——dock 独占底部条带）。
旧规则只有**单向**优先级（源码注释原文："显式关闭 → 无条件隐藏原生任务栏（压倒 dock 联动）；保持开启 → 回退 dock
独占联动"），于是用户实测到的错位是：

```
settings.json：components.dock 未设(默认开) / components.wintaskbar 未设(默认"可见")
→ Bootstrap 规则 show = wintaskbar && !dock = false   ← 任务栏实际是隐藏的
→ 菜单勾选态只读意图键 = "未勾（没隐藏）"             ← 与实际相反
→ 再点它只会往"更隐藏"方向翻                          ← 看着像"点了没反应"
```

**新规则（双向对称）**，抽成纯函数 `DesktopControlRules.ShouldShowNativeTaskbar(wintaskbar, dock, explicitVisible)`：

| 情形 | 结果 |
|---|---|
| 显式隐藏（`components.wintaskbar=false`） | 隐藏（含 dock 关闭时） |
| 显式显示（`wintaskbar=true` **且** 留痕键 true） | **显示**——dock 的默认隐藏让位 |
| 从未动过（`wintaskbar` 默认 true、留痕键 false/缺失） | 沿用 dock 联动（默认隐藏）——**既有观感不变** |

配套两点：
1. **留痕键 `desktop.taskbarExplicitVisible`**：只有"默认值恰是可见"的开关才需要它（否则无法把"用户明确要它可见"
   与"用户从没动过"分开，而这两者行为相反）。翻转时由 `DesktopToggleCatalog.ExplicitOverrides` 统一给出要一并写入的键，
   **五条写入路径全部接上**（宿主命令桥 / 宿主短命入口 / CLI 无宿主直写 / 独立进程 / 宿主内自绘菜单），不再各写一份。
2. **勾选态改读"实际"**：`NativeTaskbarManager.IsTaskbarVisible()`（探测 `Shell_TrayWnd`）优先于意图键——
   菜单说的状态就是用户看到的状态，不再出现"菜单说没隐藏、任务栏其实没了"。
3. **留痕的失效时机（用户 2026-09-17 追加拍板）**：**Dock 一变（关掉 / 重开）→ 清掉任务栏留痕，回到 dock 默认隐藏**。
   理由：Dock 是"任务栏默认隐藏"的驱动方，它一变，用户之前对任务栏的显式要求就失去了上下文。
   实现：`DesktopToggleCatalog` 的 Dock 条目声明 `ResetsExplicitKeys = [TaskbarExplicitVisibleKey]`，
   于是**所有翻转路径**在翻 Dock 时都会顺手把留痕写回 false；宿主另在 `components.dock` 变更时也复位一次，
   以覆盖**旁路写入**（设置中心的 Dock 开关直接写 settings，不经过桌面控制的翻转 helper）。

## 5. 分期（2026-09-17 用户重定调：托盘 = 程序中转站）

> **用户原话**："设置窗、自绘桌面窗口这两个功能还是需要在主程序关闭后，可以正常唤出来，来管理常驻功能的。
> 其他的功能都还挺对的。我其实真正想要是做一个托盘程序作为程序中转站，来拉起来其他的常驻功能，
> 设置窗、自绘桌面窗口等功能，让用户按需启动功能，以节省内存开销。"
>
> → 因此**修正 §4.3 的边界**：设置窗、自绘桌面窗口**不在**"随 Host 关闭"一列 —— 它们恰恰是
> "主程序关着时最需要"的两个（一个管理常驻功能、一个就是桌面本身）。Host 退化成**可随时启停的重视觉壳**
> （菜单栏 / Dock / 状态栏 / 灵动岛 / 开始菜单），托盘是拉起/停止一切的**中转站**。

| 期 | 内容 | 关键动作 |
|---|---|---|
| **M1（已完成）** | 桌面控制菜单独立进程 + 免宿主执行 + 入口改指 | 新增 exe；CLI 批动作；shell-core 单点目录 + 原生原语；快照改叶子项 |
| **M2** | **自绘桌面层**（含自绘右键菜单、桌面控制）移入独立进程，可脱离 Host 常驻 | kernel loader 子集清单装配 `desktop` 插件（范例：`agent/agent.yml` + `Agent/Program.cs` + `ExclusiveCapabilityHost`）；`cordis.yml` 摘除 `desktop`；双击隐藏图标钩子随手归位；进程转常驻 + 看门狗 `BuildTargets` 接入 + 自己的豁免标记 |
| **M3** | **热键面板**独立（像剪贴板） | `shell-hotkey-panel` 迁入独立进程（或并入 M2 的进程），`hotkeys-panel.enabled` 转为该进程内订阅 |
| **M4** | **设置中心**独立成进程（脱离 Host 可开） | 见 §11 的第二条阻碍：需先把"分区来源"从"插件 LoadAsync 注册"改为**静态分区目录 + 既有 `*ServiceBridge` 模式** |
| **M5** | **托盘中转站改造** | 托盘菜单直接对每个常驻功能做「启动 / 停止 / 状态」；「打开设置中心」不再"先把整个壳拉起来"；看门狗目标表按"按需"重排（用户没启用的项不守护、不被拉回）。**本轮已起步**：托盘新增「启动主程序 / 停止主程序（保留常驻功能）」，「退出 BetterDesktop」补写看门狗豁免标记 |

## 6. Implementation Sequence（M1）

1. `packages/shell/shell-core/DesktopControl/`（新增）：`DesktopToggleCatalog`（命令名 → 键/默认/可执行性单点）、
   `DesktopControlNative`（原生层立即生效原语）。
2. `packages/shell/shell-desktop`：`DesktopControlMenu` 增 `hostRunning` / `toggle` 参数 + 置灰注明；
   `DesktopMenuPopup.Show` 增 `onClosed`（短命进程用它退出）；`ShellMenuContentBuilder` 改叶子项。
3. `packages/shell/shell-desktop-control/`（新增 exe）：csproj / manifest / Program / Entry / 动作路由 / 日志。
4. `BetterDesktop.Cli`：批协议新增 `desktop-controls`（+ 单文件路径兜底）；`ToggleKey` 无宿主时补原生生效；
   单测补 case。
5. `host/Bootstrap.cs`：`toggle-key` 映射补 `hotkey-panel`。
6. `scripts/publish.ps1`：纳入 `DesktopControl` 组件 + `$required`。

## 7. Risks

- **菜单可交互性**：进程由 explorer（→ CLI）间接拉起，前台/焦点依 Windows 前台锁规则；
  已用"不可见承载窗 + Activate"争取前台，若个别环境菜单点不动，退路是改用 `SetForegroundWindow` 显式抢焦点（待真机验证）。
- **多显示器 / 混合 DPI**：承载窗在主屏 DPI 下，非主屏高 DPI 时贴边收敛可能有几像素误差（菜单短，影响小）。
- **旧部署**：`BetterDesktop.DesktopControl.exe` 缺失时 CLI 回退到宿主命令桥（= 旧行为），不会静默什么都不做。
- **两份菜单实现**：宿主内自绘右键仍在进程内渲染（同一 `Build`，动作语义一致）；M2 统一到一个进程后消失。

## 8. 验证（2026-09-17 真机）

| 项 | 结果 |
|---|---|
| 独立进程编译 | `dotnet build packages/shell/shell-desktop-control/BetterDesktop.DesktopControl.csproj` → **0 警告 0 错误** |
| 独立进程真机冒烟 | 运行 `BetterDesktop.DesktopControl.exe --desktop-controls`，日志依次：`承载窗就绪` → `宿主在线=True` → `[shell.desktop] 桌面控制菜单已显示: 项数=7` → `菜单已弹出：项数=7，等待用户操作`，进程保持存活等待交互（验证后已结束该进程，未写任何设置） |
| CLI 批协议单测 | `MenuBatchTests` **22/22 通过**（含新增 `desktop-controls` → `OpenDesktopControls` case） |
| CLI / 宿主编译 | 均 0 错误（宿主因正在运行锁定输出，改用独立 `OutDir` 编译验证） |
| 注册表残留 | 本机 HKCU 无 `BetterDesktop.Ui` 旧级联键（无需清理）；`UnregisterUiControls` 保留用于清理别的机器上的历史键 |
| 优先级规则单测 | `packages/shell/shell-core-tests/DesktopControl/DesktopControlRulesTests` **13/13 通过**（含"从未动过仍沿用 dock 隐藏"这条护栏，确保默认观感没被改掉） |
| 优先级真机取证 | 独立进程日志：`任务栏：意图可见=True 实际可见=False 显式留痕=False dock=True` ——**正是用户报的错位现场**，勾选态现在会说真话，点它也能把任务栏叫回来 |
| CLI 全量单测 | **48/48 通过**（此前那条 `Run_ConvertToHtml_Success` 失败 = §10 的引擎部署缺口；补上引擎后转绿，见 §10） |

**生效条件（真机部署）**：原生扩展按**自己所在安装目录**解析 `BetterDesktop.Cli.exe`
（`native/src/Launcher.cpp: ResolveSiblingExecutable`），所以必须让「Host + Cli + DesktopControl 同目录」的
那份构建到位，并**重启主程序**（重启时才会把 shellmenu.json 快照重写为单叶子项）。
原生 DLL / MSIX **无需重编、无需重签**。

## 9. 遗留（已记录，未在本期做）

- `DesktopSystemMenuRegistrar.EnsureUiTogglesRegistered` 已加"停写"警示，但方法体仍在（无任何调用方）；
  确认无历史键残留后应整体删除（2026-09-11 COM 方案 §6.8 也是这个结论）。
- 宿主内自绘桌面右键的「桌面控制」仍是进程内渲染（同一 `Build`）；M2 把桌面层搬进独立进程后两份合一。

## 10. 附录：顺带发现并修掉的部署链缺口 —— Rust 转换引擎未部署（2026-09-17）

**现象**：用户桌面弹出原生框「Rust 转换引擎未部署（convert-engine.exe 缺失）——请运行发布脚本或检查安装」。

**取证（[verified]）**

| 环节 | 事实 |
|---|---|
| 报错来源 | `packages/shell/shell-convert/Services/RustConvertRunner.cs:58-60`：`LocateEngine()` 只认 `BETTERDESKTOP_CONVERT_ENGINE` 或 `AppContext.BaseDirectory\convert-engine.exe`，都取不到就抛 `ConvertError.EngineMissing` |
| 引擎本体 | **已构建**：`native/convert-engine/target/release/convert-engine.exe`（3.4 MB，2026-09-14 18:44） |
| 部署链 | 全仓 `*.csproj / *.ps1 / *.props / *.targets / *.slnx` 里 `convert-engine` 命中数 = **0** —— clipboard / index 各有 `deploy-*.ps1`（含 `cargo build --release`），**convert 这一环从来没人拷** |
| 连带影响 | `BetterDesktop.Cli.Tests.HeadlessExecutorTests.Run_ConvertToHtml_Success` 期望 `ExitCodes.Ok` 实得 4 = `EngineMissing`（同一根因）；且该测试类未开 `SuppressUserFeedback` → 原生弹框**弹在运行者桌面上**（用户看到的那个框就是跑这条用例弹出来的） |

**修复**

1. `packages/shell/shell-convert/BetterDesktop.Shell.Convert.csproj`：新增 `Content` 引用
   `..\..\..\native\convert-engine\target\release\convert-engine.exe`（`Link=convert-engine.exe` +
   `CopyToOutputDirectory=PreserveNewest` + `Condition=Exists(...)`）。放本工程 = **单点**：
   host / Cli / agent 等所有引用 shell-convert 的宿主都自动带上，dev 输出与 `dotnet publish` 一致落位。
   （cargo 构建仍是显式步骤——本仓不代跑 cargo，engine / engine-index 同规矩。）
2. `scripts/publish.ps1`：`$required` 增 `convert-engine.exe` —— 缺了就**发布失败**（文件头纪律：
   "a half release must never ship"）；此前它不在任何清单里，发布包必然缺引擎。
3. `BetterDesktop.Cli.Tests/HeadlessExecutorTests.cs`：两条 `Run_*` 用例补 `SuppressUserFeedback = true`
   （`MenuBatchTests` 一直有这条纪律，此处补齐，避免单测把原生框弹到用户桌面）。

**验证**：`Test-Path` 确认引擎已随构建落到 `BetterDesktop.Cli.Tests/bin`、`BetterDesktop.Cli/bin`、
host 构建输出（临时 OutDir）三处；`Run_ConvertToHtml_Success` 转绿，CLI 全量 **48/48**；
用户正在跑的 dev 宿主输出目录（`host/bin/Debug/...`）已手工补拷一次，**无需重启环境即可用**（下次构建会自动带上）。

## 11. 目标架构：托盘 = 程序中转站（2026-09-17 用户定调）

```
                     ┌──────────────────────────────────────────────┐
                     │  BetterDesktop.Tray.exe  （零依赖 WinForms）  │
                     │  中转站：启动 / 停止 / 状态 / 统一入口        │
                     └───────┬───────────┬───────────┬──────────────┘
            按需启动         │           │           │
      ┌──────────────────────┘           │           └──────────────────────┐
      ▼                                  ▼                                  ▼
  Host.exe（重视觉壳）          设置窗进程（待做 M4）              独立功能进程组
  菜单栏 / Dock / 状态栏        管理常驻功能开关                  · 自绘桌面 + 桌面控制（M2）
  灵动岛 / 开始菜单             （关窗即退 → 省内存）              · 剪贴板引擎 + 面板（已有）
  真正需要时才付这份内存                                            · 索引引擎（已有）
                                                                  · 热键侧板（M3）
                                                                  · 常驻服务 Agent（已有）
```

**内存基线（2026-09-17 实测本机，WorkingSet）**——这就是"按需"值不值钱的答案：

| 进程 | 内存 | 说明 |
|---|---|---|
| `BetterDesktop.Host` | 136.3 MB（2 实例） | 其中一个是图标恢复哨兵（等宿主死亡的轻量进程）；壳本体约 125 MB |
| `BetterDesktop.Clipboard.Panel` | 59.3 MB | 已独立 ✓ |
| `BetterDesktop.Index.Engine` | 36.5 MB | 已独立 ✓ |
| `BetterDesktop.Clipboard.Engine` | 8.6 MB | 已独立 ✓ |
| `BetterDesktop.Capture` | 9.0 MB | 按需拉起 ✓ |
| Tray / Watchdog / Agent | 未运行 | dev 机没跑；生产部署由自启带上 |
| **合计** | **≈250 MB** | 把壳做成"按需"≈ 省下 ~125 MB；把剪贴板/索引做成"用户关了就停"再省 ~95 MB |

**两个必须先解决的阻碍**（决定 M2/M4 的可行性）

1. **自绘桌面要"加载插件"**：`desktop` 是插件（`DesktopPlugin` 在 `LoadAsync` 里建窗口 + 隐藏原生图标），
   依赖 `IContext` + 设置/外观/毛玻璃/app-source/convert/archive/剪贴板(延迟)/事件总线/`IDesktopBrowser`。
   → 做法：kernel loader **子集清单**装配（范例 `agent/agent.yml` + `Agent/Program.cs`：受限 `Factories`，
   未注册即 fail-closed）+ `ExclusiveCapabilityHost` 的单插件动态装配；`cordis.yml` 摘掉 `desktop`，
   使"桌面"永远只由一个进程拥有（避免两进程抢桌面窗口）。
2. **设置窗的分区来源绑在插件加载上**：分区由各插件 `LoadAsync` 里
   `context.Get<ISettingsSectionRegistry>()?.Register(new XxxSection(...))` 注册
   （`shell-settings/Services/SettingsSectionRegistry.cs` + `SettingsPlugin`）→ 不加载插件 = 设置窗几乎空白。
   → 做法：**引用（≠ 加载）**插件程序集 + 一个**静态分区目录**（把各 `XxxSection` 直接 new 出来，
   需要活体回调的（菜单栏/灵动岛）走仓库既有的 `*ServiceBridge` 静态绑定模式，或传 no-op 降级为
   "保存后下次生效"）。先例：`tools/ShellComponentsPlayground` 已证明"独立进程装配 shell 组件并开窗"可行；
   `shell-core/Surface/EntryTheme` + `SlimScrollBar` 已证明"不依赖内核也能读出主题"（剪贴板面板就是这么做的）。

## 12. M4 首步落地：`BetterDesktop.Settings.exe`（独立设置进程，2026-09-17）

**做法（三件事）**

1. **新进程** `packages/shell/shell-settings-host/`（`AssemblyName = BetterDesktop.Settings`）：
   `WinExe + UseWPF` + 单实例（命名事件：重复打开 = 把已有窗口带到前台）+ **关窗即退**（内存立刻归还）。
   最小装配 = `CordisContext`（只为事件总线）+ `SettingsService(context)` + `AppearanceService.Initialize()`
   + `VibrancyService` + `SettingsWindow`；**不加载任何插件**（所以不会建菜单栏/Dock/桌面层那些重窗口）。
2. **分区目录** `SectionCatalog.cs`：**引用**（≠ 加载）各插件程序集，直接 `new` 出 12 个 `ISettingsSection`
   （→ 需给 5 个 `internal` 分区所在工程加 `InternalsVisibleTo Include="BetterDesktop.Settings"`）。
   按用户裁决处理三类特殊依赖：
   · 菜单栏 / 灵动岛 → 传空回调 = **只持久化，下次该功能启动时生效**（用户原话："记录设置对于关闭功能的最后修改，等下次功能启动后再应用修改"）；
   · 任务栏外观 / 开始菜单 / Dock 固定项 → 静态桥未绑定 → 分区显示占位（既有 null 兜底）；
   · **「热键」分区暂缺席**：硬依赖 `IHotkeyRegistryService`（无静态桥、null 即抛），该服务只有内核 `hotkeys` 插件提供
     → M3「热键面板独立」后随注册表一起补上；当前改热键仍用宿主内的设置窗。
3. **样式单点**：把 `host/App.xaml` 的主题令牌 + macOS 风控件样式抽成
   `shell-core/Surface/ShellTheme.xaml`（资源字典），**宿主改为 `MergedDictionaries` 引用同一份**，
   新进程也引用它 —— 不复制样式、不漂移（宿主行为/外观不变）。

**接线**：托盘 `AppPaths.SettingsExe` + `OpenShell()` 改为**直接拉起本进程**（不再"为了看一眼设置先启动整个壳"）；
老部署（无该 exe）自动回退旧链路（CLI `open-settings` → 宿主）。`scripts/publish.ps1` 纳入组件 + 必检清单。

**验证（真机）**：`settings-host.log` → `分区目录构建完成（全部成功）` →
`BuildAllSections 分区数=12：设置、主题、左侧 Dock、桌面、菜单栏、灵动岛、任务栏外观、开始菜单、Dock 固定项、右键菜单、剪贴板、应用来源`
→ `设置窗已显示：分区 12 个`；进程与宿主**零交互**（不探活、不发管道），故"关掉主程序也能开"是结构性成立的。
构建：设置进程 / 托盘 / 宿主 均 0 错误（宿主在 App.xaml 改成共享字典后重新构建通过）。

**M4 剩余**（按序）：

- **宿主侧入口收敛**：菜单栏 Logo / 开始菜单 / 热键侧板的「设置」目前仍是**宿主进程内窗**（有完整分区含热键），
  与独立进程窗并存 → 待「热键」分区能在独立进程构建后，把这几处改为拉起独立进程（单一拥有者，避免两个设置窗）。
- 「热键」分区补齐（依赖 M3）。
- 托盘其余功能项（自绘桌面 / 剪贴板 / 热键侧板）的「启动 / 停止 / 状态」——中转站本体，等 M2/M3 各自的进程存在后接入。

## 13. M2 落地：自绘桌面搬进「桌面服务」进程（2026-09-17）

**搬了什么**：`BetterDesktop.DesktopControl.exe` 从 M1 的"短命菜单渲染器"升级为**常驻「桌面服务」**——
自绘桌面层（全屏窗口 + 图标网格 + 自绘右键菜单）+「桌面控制」菜单 + 原生模式双击隐藏图标钩子 + 图标恢复哨兵，
**唯一拥有者 = 本进程**；`host/cordis.yml` 里 `desktop` 条目摘除（两进程同时拥有桌面层 = 两层图标打架，硬红线）。

**归属图（谁翻哪个键）**

| 键 | 归属 | 谁执行 |
|---|---|---|
| `components.desktop`（自绘桌面总开关） | 桌面服务 | 服务进程内翻（DesktopPlugin 订阅 SettingsChanged 即时启停） |
| `desktop.iconsHidden` / `desktop.doubleClickHideIcons` | 桌面服务 | 同上（CLI 对这两个键**转交**服务：`--toggle-key icons`） |
| `components.dock` / `components.wintaskbar` / `components.menubar` / `hotkeys-panel.enabled` | 宿主 | 宿主命令桥（既有） |
| 任务栏"显式可见"优先级（dock 联动） | 宿主 | `DesktopControlRules`（既有） |

**通道**：`BetterDesktop.DesktopCmd`（契约在 `shell-core/DesktopControl/DesktopControlPipe.cs`，协议与宿主 MenuCmd 同形：
单行 `BDDC1|<action>|<path>`）。动作：`desktop-controls` / `toggle-key` / `toggle-desktop` / `stop`。
入口模式：无参=常驻服务；`--desktop-controls`（有服务→转发，没有→M1 自行弹）；`--toggle-key <name>`；
`--toggle-desktop`；`--stop`；`--icon-restore-sentinel <pid>`（哨兵，DesktopPlugin 本就以"自己的 exe + 该参数"拉起 ✓）。

**装配**：kernel loader + 新清单 `desktop.yml`（vibrancy / settings / context-menu / convert / clipboard-history，
五个提供者都不建窗口）+ **动态装载** `desktop` 插件（`context.Plugin(...)`，可卸载 → 总开关能进程内即时启停）。

**宿主侧**：`Bootstrap.EnsureDesktopServiceRunning()` —— 壳启动时"确保桌面服务在跑"（观感与搬家前一致），
但**不接管、不随壳退出**（用户拍板：自绘桌面不需要主程序）。`case "desktop-controls"` 优先转交服务。
**看门狗**：新增目标 `Desktop`（`RequiresEnabledKey = components.desktop`）+ 豁免标记 `desktop-stopped.flag`。

**编译验证**：桌面服务 / CLI / 看门狗 / 宿主 全部 0 错误。

**真机验证（2026-09-17 18:05，已通过）**——用户报"自绘桌面起不来"，查出**三个叠加的真原因**，逐条修完后
服务进程成功把自绘桌面画了出来（`SetParent` 跨进程嵌 explorer 桌面在**非宿主进程**属仓库首次，现已证实可行）：

| # | 症状/原因 | 修法 |
|---|---|---|
| ① | 宿主 bin 里找不到服务 exe：**开发态各工程各自 bin**，而"同目录"只在发布布局成立（用户日志：`[desktop] 桌面服务未部署…自绘桌面不可用`） | 新增 `DesktopControlLocator`（同目录 → `%LOCALAPPDATA%\BetterDesktop` → `…\DesktopControl`）；宿主/CLI 共用 |
| ② | 进程**起来就退、日志一个字都没有**：SDK 风格构建的 `X.exe` 只是 apphost，入口程序集是 `X.dll` —— 只拷 exe 等于缺入口 | 开发态投递改为**整份产物**进自包含子目录 `%LOCALAPPDATA%\BetterDesktop\DesktopControl`；新增 `scripts/deploy-desktop-service.ps1`（脚本头写明这条坑） |
| ③ | `退出兜底注册失败：调用线程无法访问此对象，因为另一个线程拥有该对象`：照抄 agent 的 `ConfigureAwait(false)` → 装配落到线程池线程，而 WPF 窗口创建与 `Application.Current.Exit` 注册必须 UI 线程 | `await …AwaitAsync().ConfigureAwait(true)`（回到 Dispatcher），文件内注明"这不是笔误" |

修复后的服务日志（真机）：
```
[shell.desktop] 已嵌入桌面宿主 host=0x401CA 并提层 HWND_TOP（自绘层可交互，原生图标层被本插件隐藏）
[shell.desktop] 原生图标隐藏：ShowWindow listView=0x800B4 SW_HIDE
[shell.desktop] 图标恢复哨兵已拉起        ← 哨兵进程按预期自动派生（service pid + sentinel pid 两个进程）
=== 桌面服务已就绪（常驻；自绘桌面 + 桌面控制菜单 + 双击钩子） ===
Rebuild: items=80 … RebuildFreeLayout: cells=82 canvas=(420x1064)
```

**宿主侧仍需一次重建+重启**（用户当前运行的是 17:46 那版）：本轮的"宿主转交桌面服务"（`toggle-desktop` /
桌面自有 `toggle-key`）与新的定位器都在新构建里；旧构建会把自绘桌面开关翻在宿主内存里 → 服务听不到
（表现为"点了没反应"）。

**验收清单（用户执行）**
1. 关掉当前主程序 → 用新构建重新启动（壳会拉起桌面服务）；
2. 看：桌面图标/右键菜单是否与搬家前一致（右键「桌面控制」应仍能弹、勾选态正确）；
3. 关掉主程序（托盘「停止主程序」或菜单栏 Logo「退出」）→ **桌面应继续在**（这是 M2 的核心诉求）；
4. 失败回滚：把 `host/cordis.yml` 的 `desktop` 条目加回 + 重建宿主；或直接手动运行
   `BetterDesktop.DesktopControl.exe` 看 `desktop-control.log`。

**M2 收尾（2026-09-17 18:30 已完成）**

① **Agent 仲裁**（`agent/Capabilities/HostPresenceWatcher.cs` + `ExclusiveCapabilityHost.cs`）
   - 判据扩展：桌面图标的持有者 = **壳 或 桌面服务**（`IconOwnerPresent`），任一在场 agent 即让出；
     任务栏外观仍只以壳为判据（桌面服务不加载 `shell-taskbar`，见 `desktop.yml`）。
   - 门控由单个 `IsActive` 拆成 `_iconsActive` / `_taskbarActive`（两者判据不同，不能共用一个位）。
   - 类名沿用历史名（多份文档与格式基线按名引用）；语义扩展与理由写在文件头，避免后来者误读。
   - 观测点：`[Gate]` 日志会同时打印 `Host=…、桌面服务=…` 两路状态。

② **托盘启停 + 自启**（`tray/AppPaths.cs` / `ProcessBridge.cs` / `TrayApplicationContext.cs`）
   - 菜单新增「桌面服务（不依赖主程序）」▸ 启动 / 停止；「组件状态」多一行；打开菜单按运行态置灰。
   - 停止走**优雅路径**：命名管道 `BDDC1|stop|`（托盘零包引用，故只复刻 stop 这一个动作，其余走 CLI 契约）
     → 服务恢复 explorer 图标层 + 任务栏后自行退出；超时才强杀（强杀有哨兵兜底）。
   - 随托盘自启（照 `EnsureAgentAutoStart` 范式）：托盘是开机自启的常驻控制面，由它把桌面服务带起来 →
     **主程序不启动也能用**（本次需求的原话目标）。用户显式停止后写 `desktop-service-stopped.flag`，不复活。
   - 真机验证（发 `stop` → 进程消失 → 重新拉起）：
```
桌面服务退出
[shell.desktop] Application.Exit → 恢复 explorer 图标层 … SW_SHOW 已执行
[shell.desktop] ProcessExit → 恢复 explorer 图标层        ← 退出兜底双保险都生效
哨兵已复原：原生图标=True 任务栏=True    ；=== 退出：0 ===
```

## L1 / L2 / L3 归属表（用户 2026-09-17 给定）与现状对照

| 层 | 业务 | 生命周期 | 承载 | 现状对照 |
|---|---|---|---|---|
| **L1 系统接管** | 任务栏管理 / 系统右键菜单 / 自绘桌面 | 随卸载才恢复（**常态化接管**） | **Agent**（常驻能力宿主） | 部分达成：任务栏外观 ✓ 已由 Agent 轮值持有；桌面图标双击钩子 / 自绘桌面 / 系统右键菜单注册目前在**桌面服务**（M2 新开的第四个承载）✗ |
| **L2 工具常驻** | 剪贴板等工具类 | 不随主程序（独立进程常驻） | Rust 引擎 + 面板 exe | ✓ 已达成（clipboard-engine + panel，看门狗守护） |
| **L3 壳扩展** | 菜单栏 / Dock / 灵动岛 / 桌宠 | 随主程序启停 | Host（壳内插件） | ✓ 达成（灵动岛 / 桌宠未来以壳插件实现） |

落地调整（不变）：
- L1 → Agent 常驻自启、接管常驻化（**壳关不还原**）、右键菜单迁入；
- L2 → 剪贴板窗口生命周期根治 + 看门狗扩守护；
- L3 → 灵动岛 / 桌宠未来以壳插件实现（不是独立进程），随主程序。

**本轮按表修的一处（已改）**：`shell-taskbar/TaskbarAppearancePlugin` 原先在**壳退出与插件卸载两条路都还原**
任务栏外观 → 与"壳关不还原"相悖（还会与 Agent 接手之间闪一下）。现改为按"交班有没有人接"判定：
Agent 在运行 → 不还原（外观连续交班）；Agent 不在 → 仍还原（没人接手就别留着我们的外观）。
崩溃 / 强杀那条路由 explorer 的 `RestoreAllWhenProcessDies` 兜底，不受影响。

### ⚠️ 已登记的漂移（M2b 待收敛）

#### M2b 收敛（方案 A）—— 已落地部分（2026-09-17 18:53）

用户拍板：**"只要作用差不多就行了"** → 取 **A**：L1 的**归属 / 启停 / 守护 / 轮值收归 Agent**，
自绘桌面这个唯一需要建全屏 WPF 窗口的高危部分**下沉为 Agent 的专项执行体**（保留独立进程，不做 B 的进程合并）。

1. `agent/Capabilities/DesktopServiceSupervisor.cs`（新）+ `agent/Program.cs` 接线：
   5s 周期 + 启动检查，"需要服务"的判据 = `components.desktop` **或** `desktop.doubleClickHideIcons`（都为 false 就不白养进程）；
   尊重 `desktop-service-stopped.flag`（用户显式停止不复活）；路径用 `DesktopControlLocator`（与宿主/CLI 同一份）。
   Agent 退出时**只停监护、不停服务**（L1 常态接管，桌面继续在）。
2. `shell-taskbar/TaskbarAppearancePlugin`：壳退出 / 卸载时按"交班有没有人接"决定是否还原（见上文）。
3. 托盘那份「随托盘自启桌面服务」**保留为兜底**（与 Agent 监护幂等，任一路都能把服务带起来；避免"某一路失效就没桌面"）。

真机验证（2026-09-17 18:53，Agent 日志）：
```
[Gate] 持有者探测启动：Host=运行中（待命）、桌面服务=未运行 → 图标能力待命
[ContextMenu] 系统右键接管已注册/修复（L1 常驻）
[L1] 已拉起桌面服务（启动时检查）：…\DesktopControl\BetterDesktop.DesktopControl.exe
[Gate] 持有者状态变化：Host=已启动 → 待命、桌面服务=已启动 → 待命
→ 优雅停止 Agent 后：Agent 进程=空，桌面服务仍在（服务 pid + 哨兵 pid 两个进程）
```

#### L3 红线（用户 2026-09-17 再次确认）：菜单栏 / Dock / 灵动岛**随主程序关闭而关闭**

现状核对 —— **无违规**：
- 加载方只有壳：`host/cordis.yml` #10 `dock`、#19 `menu-bar`、#113 `island`；
- 常驻两份清单都没有它们：`agent/agent.yml`（status / app-source / pinning / search / convert + 动态独占能力）
  与 `desktop.yml`（vibrancy / settings / context-menu / convert / clipboard-history），
  两份清单头部都写明"视觉插件不得出现在此"；已在 `agent.yml` 补上本红线原文（含自绘桌面的例外去向）。
- "关闭的持续性"由看门狗豁免标记负责：`host-stopped.flag`（托盘「停止主程序」/ 壳自身退出写入）→ 看门狗不拉回。

**顺带修掉一处标记名不一致（真 bug）**：看门狗（`watchdog/Program.cs` 的 `Desktop` 目标）认 `desktop-stopped.flag`，
而本轮托盘与 Agent 监护写的是 `desktop-service-stopped.flag` → 名字对不上 = **托盘的"停止桌面服务"会被看门狗拉回**。
已统一为 `desktop-stopped.flag`（看门狗既有约定，与 `host-stopped` / `agent-stopped` 并列）。

仍待收口：**系统右键菜单双写**（风险已评估为低：两侧写的是同一份 HKCU 键与同一份 `shellmenu.json`，幂等同内容；
真正的规则目标是"注册只由 L1 轮值者做"）。收口需要理清 `ShellMenuConfigWriter` / `ComShellExtensionRegistrar`
的调用链（服务侧不该 ensure 注册），留到下一轮，避免动坏原生菜单内容。

1. **第四个承载**：M2 新开 `BetterDesktop.DesktopControl.exe` 承载「自绘桌面 + 桌面控制菜单 + 桌面图标接管」，
   而表里这三项属 **L1 = Agent**。两条收敛路线：
   - **A（建议）**：L1 **归属**收归 Agent（由 Agent 决定启停、守护、轮值），自绘桌面这个**唯一需要建窗口**
     的高危部分下沉为 Agent 的专项执行体 → 保住"窗口崩溃不拖垮常驻能力"（Agent 的设计红线是
     "绝不建窗口"，见 `agent/BetterDesktop.Agent.csproj` 头注释），也不违背 M1 原话（"像剪贴板历史一样的级别"）。
   - **B（严格按表）**：桌面层整体并入 Agent、删除 `DesktopControl.exe` → 一个进程、无需仲裁，
     但 Agent 从此创建全屏 WPF 窗口，桌面层一崩会带走剪贴板 / 索引 / 转换等**全部**常驻能力。
2. **系统右键菜单双写风险**：M2 的 `desktop.yml` 加载了 `context-menu` → 桌面服务会注册原生扩展 + 写
   `shellmenu.json`；而 `agent.yml` 同样加载 `context-menu`（L1"右键菜单迁入"的本意就是 Agent 干）→ 两进程同域。
   收敛方向：**注册权收归 L1 轮值者（壳在→壳；壳走→Agent）**，桌面服务只为自绘右键菜单取
   `ToolCatalog`/`IFileClassifier`，不注册系统项。

## 托盘可控性矩阵（用户 2026-09-17 追问"其他的是否都能被托盘拉起/关闭"）

对照：看门狗（`watchdog/Program.cs`）当前守护 **6 个目标** = Host / Agent / Clipboard.Panel /
Clipboard.Engine / Index.Engine / Desktop。

| 组件 | 常驻 | 托盘启动 | 托盘关闭 | 现状说明 |
|---|---|---|---|---|
| Host（主程序） | 可关 | ✓ 启动主程序 | ✓ 停止主程序 | 停止写 `host-stopped.flag`，看门狗不拉回 |
| Agent（常驻能力） | 是 | ✓ | ✓ | 同上（`agent-stopped.flag`）；托盘启动时自启 |
| Desktop（桌面服务 / L1 执行体） | 是 | ✓ | ✓ | 2026-09-17 本轮新增：停止走管道优雅路径 + `desktop-stopped.flag`；托盘启动时自启 |
| Settings（设置中心） | 否（关窗即退） | ✓ 打开设置中心 | n/a | 独立进程，不依赖宿主 |
| Updater | 否 | ✓ 检查 / 下载安装 | n/a | 安装流程含"退出主程序 → 替换 → 重启" |
| Recovery（应急恢复） | 否 | ✓ | n/a | 一次性工具 |
| Tray 自身 | 是 | — | ✓ 退出托盘 | |
| **Watchdog** | 是 | ✗ | ✗ | 托盘只显示运行状态；连 `watchdog-pause.flag` 应急开关都没有入口 |
| **Clipboard.Panel / Engine**（L2） | 是 | ✗ | ✗ | 由设置 `extensions.clipboard-history.enabled` 决定"是否守护"；⚠️ 关掉该设置**不会停掉已在运行的**两个进程（看门狗只停止守护，不杀进程） |
| **Index.Engine** | 是 | ✗ | ✗ | 无托盘项、无设置门控（看门狗无条件守护） |
| **Capture（截图）** | 视实现 | ✗ | ✗ | 无托盘项 |

**结论**：核心 7 项已在托盘控制面（Host / Agent / 桌面服务 / 设置中心 / 更新器 / 应急恢复 / 托盘自身）；
**仍有 4 项不在**：看门狗、剪贴板面板+引擎、索引引擎、截图。

**补法建议（按性价比排序）**
1. 看门狗：`启动看门狗 / 停止看门狗 / 暂停守护（watchdog-pause.flag）` 三项 —— 托盘已有 `IsRunning(WatchdogProcessName)`
   与 `AppPaths.WatchdogExe`，改动最小。
2. 索引引擎 / 截图：与剪贴板同款"设置门控 + 托盘开关"，但托盘纪律是"开关一律经 CLI 契约落地" →
   需给 CLI（`--toggle-key` 的目录单点）补条目，不能托盘直写设置。
3. 剪贴板"关闭"语义要定：现在关闭设置只是**不再守护**，已在跑的面板/引擎不会退出 →
   要么托盘关闭时显式停进程，要么让面板/引擎订阅该设置自行退出（后者更符合"设置是唯一真相源"）。

#### M2c 已补齐（2026-09-17，用户选定 1+2+3 全做）

**① 看门狗进托盘控制面**（`tray/ProcessBridge.cs` + `TrayApplicationContext.cs`）
菜单新增「看门狗（守护组件不消失）」▸ `启动看门狗` / `停止看门狗` / `暂停守护`（勾选，写 `watchdog-pause.flag`，
看门狗 3s 轮询内生效）；「组件状态」显示运行态 + 是否已暂停守护。启动看门狗时会清掉暂停标记。

**② "关掉开关但它还在"根治 —— 看门狗带 StopWhenDisabled**（`watchdog/Program.cs`）
`WatchTarget` 新增 `StopWhenDisabled`：设置显式 `false` 时**连进程一起停**（只按精确进程名杀，幂等、失败留痕）。
赋给工具类常驻三项（剪贴板面板 / 剪贴板引擎 / 索引引擎）+ 新增的截图目标。
⚠️ Host / Agent / Desktop **保持 false**：即使功能开关关闭它们也要活着（桌面服务还负责桌面控制菜单与
原生双击钩子），其关闭由托盘显式动作负责 —— 这是"能关"与"必须活着"的分界，别一刀切。

**③ 索引引擎 / 截图 纳入托盘 + 看门狗**
- `DesktopToggleCatalog`（命令名→设置键单点）新增 `index` / `capture` 两个条目；文件头已注明它自 2026-09-17
  起兼作**托盘「功能开关」与 CLI `--toggle-key` 的共享表**（托盘纪律：开关一律经 CLI 落地）。
- 新设置键：`extensions.index.enabled`（**新增**：此前索引引擎无任何门控键，看门狗无条件守护它）、
  `extensions.screenshot.enabled`（扩展中心 id = screenshot，遵循 `extensions.<id>.enabled` 约定）。
- 看门狗新增目标 `Capture`（`BetterDesktop.Capture.exe`）并给 `Index.Engine` 补上门控键。
- 托盘「功能开关」新增「索引引擎」「截图工具」两行（经 CLI `--toggle-key`）。
- 已核对：`DesktopControlMenu.Build` 是**显式列举**开关（不遍历 `Items`）→ 新条目不会出现在「桌面控制」右键菜单里。

**验证**：watchdog / tray / CLI 编译 0 错误；`shell-core-tests` 桌面控制相关 **14 项全过**（含目录单点用例）。
生效方式：托盘与看门狗需重建重启（当前托盘未运行，用户下次启动即为新版）。

#### 真机 bug 修复：「自绘右键菜单里点了没反应」（2026-09-17 19:17 修复并实测）

用户报告：自绘右键菜单里有「热键侧板显隐」，点了没有任何反应。

**根因**：自绘桌面的「桌面控制」子菜单在 **shell-desktop 包内**构建
（`DesktopIconsControl.BuildDesktopMenu` → `DesktopControlMenu.Build(_settings, openClipboardHistory)`），
只传了两个参数 → `hostRunning` 取默认 `true`、**`toggle` 取默认 `null`** ✗ →
`Execute` 走"宿主内"分支 `settings.Set(...)`，**只写进桌面服务自己的设置快照**；
而菜单栏 / Dock / 热键侧板都是**宿主内组件**，宿主收不到这次变更 → 表现为"点了没反应"。
（图标 / 任务栏那两项能用，是因为它们由桌面服务**本进程的插件**自行落地，掩盖了这个洞。）

证据链：宿主日志里**完全没有** `切换UI` 行 + 服务日志里**没有**执行器那行 `切换（命令桥热切）`
→ 排除"宿主没处理"与"禁用态"，定位到"点击根本没走到跨进程路由"。

**修法**（依赖方向不允许 shell-desktop 反向引用 DesktopToggleExecutor → 用注入点）：
- `DesktopControlMenu` 新增两个注入点：`HostRunningProbe`（宿主在线探测）+ `ToggleRouter`（跨进程翻转路由），
  默认 null = 宿主内运行（进程内直接翻设置，原有行为不变）；
- `DesktopIconsControl` 改用注入值构建子菜单；
- `DesktopControlEntry.InitializeServiceAsync` 装配时注入 `HostPresence.IsRunning` 与
  `name => DesktopToggleExecutor.Apply(name, _settings, HostPresence.IsRunning())`。

**实测**（服务端 `--toggle-key hotkey-panel` → 管道 → 服务 → 命令桥 → 宿主，来回各一次）：
```
服务：切换（命令桥热切）：hotkey-panel → 宿主在线处理
宿主：[hotkeys-panel] 侧板已关闭（可用切换热键或设置中心「热键」再打开）
      [hotkeys-panel] 侧板显隐应用（设置变更）：enabled=False
      [menu-cmd] 切换UI(命令桥): hotkeys-panel.enabled=False
      → 第二次：侧板已显示 / enabled=True
```
该修复在**桌面服务侧**（+ shell-desktop），部署即可生效，**不需要重建宿主**。

**遗留（同类风险，未修）**：桌面服务的 `SettingsService` 同样是"内存快照、外部改盘不重载"
（执行器文件头已写明）→ 宿主侧翻过的开关，桌面服务菜单里的**勾选态**可能滞后一拍。
彻底解需要"跨进程设置变更通知"（管道广播或文件监听），归入 M4 一起做。

#### 两个开关语义修正（2026-09-17 用户拍板）

**① 剪贴板历史 = 真开关**（`shell-clipboard/ClipboardPlugin` + `shell-clipboard-ipc/ClipboardEngineLauncher`）
- 此前：`extensions.clipboard-history.enabled` 只是 `apply_settings` 里的一个字段 → 关掉后面板 exe 与引擎
  **照样常驻**（"关了但它还在"），也没有"开启时打开面板"这一步。
- 现在：开关是**生命周期级动作**（新增 `ApplyEnabledState`）：
  · **开** → `EnsureEngine` + 按 `entry-style` `EnsurePanelEntry` + **`OpenPanel`（开启时顺手打开侧边面板）**；
    只在**翻转时**打开（`LoadAsync` 启动路径不打开，否则每次开宿主都弹面板）。
  · **关** → 新增 `ClipboardEngineLauncher.StopAll()`：停面板（连带侧边入口手柄）+ 停引擎
   （发布名 + cargo 开发名都杀），**不留进程**；再次开启走与宿主启动**完全相同**的 Ensure* 路径
    → 满足"下一次开启时要可以正常工作"，不残留半开状态。
- 启动路径按开关短路（`enabledAtLoad`）：功能关着就不起引擎、不装配入口。
- 双保险：看门狗对这两个目标带 `StopWhenDisabled`（见 M2c），覆盖"关掉时恰好不在守护视野"等漏杀场景。

**② 热键侧板显隐 = 关闭即销毁窗口**（`shell-hotkey-panel/HotkeyPanelPlugin`）
- 此前：`HidePanel()` 只停两个定时器 + Hide → **窗口对象常驻**（500ms/50ms 定时器虽停，窗口与场景监听还在）。
- 现在：关 → `Close()` **销毁窗口**（`OnClosed` 停 500ms 数据刷新 + 50ms 门控轮询、Dispose 场景监听器，
  连同作用域与外部点击钩子一并退出），引用置 null；开 → `EnsureWindow()` 新建干净窗口（幂等）。
- **刻意保留**：切换热键的**注册**（`RegisterToggleHotkey`）——那是关掉之后唯一还能把侧板叫回来的入口
  （另一个是设置中心「热键」勾选）。没有它，关掉侧板就再也开不回来。
- 已知取舍：热键"声明"（设置中心列表里的条目）挂在窗口上 → 关闭期间这些条目暂不出现，重开时重新声明。
- 边界说明：**侧板是宿主内窗口（L3：随主程序启停）**，所以"不留后台进程"落实为"不留后台资源"
  ——宿主进程本身不因关侧板而退出（菜单栏/Dock 等 L3 项还要靠它）。真要独立成进程是 M3 的活。

**验证**：`shell-clipboard` / `shell-hotkey-panel` / `桌面服务` / `宿主` 编译均 0 错误；
运行时验收需重建并重启宿主（两处都是宿主内插件）。

**判据备忘：「资源释放」vs「进程消失」（决定 L2 / L3）**

| | 资源释放 | 进程消失 |
|---|---|---|
| 谁回收 | 我们自己的代码（漏一个就残留） | 操作系统（无条件全收，不可能漏） |
| 粒度 | 精确可增量（关侧板只停 50ms 轮询，其余照常） | 全有或全无 |
| 优雅收尾 | 能（落盘设置 / 注销全局热键 / 恢复被改的系统状态） | 收尾代码**不一定跑**（强杀路径）→ 需另配哨兵（如桌面服务的 `--icon-restore-sentinel`） |

推论：**"关闭 = 进程消失"是架构特权**，只有住在独立 exe 里的功能才拥有（L2 工具类）。
宿主内插件（L3）最多做到"销毁窗口 + 归还它持有的资源"，宿主进程本身不会因它关闭而退出
（菜单栏 / Dock / 灵动岛还要靠它）。
⚠️ 且"关闭 = 进程消失"与"关掉后还能用热键叫回来"**互斥**（全局热键必须由活着的进程持有）——
要兼得，宿主只留"热键注册 + 按需拉起"，功能本体独立成 exe（M3 侧板独立化的目标形态）。

## 常驻进程内存预算（用户 2026-09-17 定调："≤5MB 可留，超了就即用即弃，本质是工作不是灯泡"）

**规则**：常驻（灯泡）只允许**极瘦进程**；做不到 5MB 的一律按"工作"处理 —— 需要时拉起、用完退场。

**实测基线**（2026-09-17，私有内存 ｜ 含调试期实例）：

| 进程 | 私有 MB | 判定 |
|---|---|---|
| BetterDesktop.Host（壳） | 337.7 | 超（L3，已按需启动 ✓） |
| Clipboard.Panel（WPF） | 193.4 | 超 → 应即用即弃 |
| DesktopControl（桌面服务，WPF） | 188.6 | 超（有可见职责：自绘桌面；可瘦身） |
| Capture（常驻听热键） | 84.0 | 超 → 应即用即弃 |
| Index.Engine | 66.4 | 超 → 应"空闲即退" |
| DesktopControl 哨兵 | 12.5 | 超（短命守护，可接受） |
| **Clipboard.Engine（Rust）** | **5.7** | **基本达标 —— 全场唯一够格的"灯泡"** |
| 合计 | **888.3** | |

**关键推论**：5MB 预算 ≈ **"常驻必须是 native/Rust"** —— .NET 光运行时 20~40MB、WPF 100MB 起，
**任何 .NET/WPF 常驻进程结构性做不到 5MB**。故规则落地为：
**驻留只放数据面/执行面的极瘦进程；其余功能一律"工作"模型（按需拉起 + 用完退场）。**

**逐项体检与改造建议（按性价比排序，待用户定）**

| # | 功能 | 现状 | 目标形态 | 预计回收 |
|---|---|---|---|---|
| ① | 剪贴板面板 | 193MB 常驻（只为挂侧边「›」手柄） | 面板**即用即弃** + **侧边手柄迁进 Rust 引擎**（引擎已有常驻循环底子，5.7MB 达标） | ~190MB |
| ② | 索引引擎 | 66MB 无条件常驻 | **空闲即退**（N 分钟无检索退场，检索时再拉） | 66MB |
| ③ | 截图 Capture | 84MB 常驻听热键 | 热键交瘦进程持有，截图本体按需拉起 | 84MB |
| ④ | 桌面服务 | 188MB 常驻（可见职责） | 保留常驻；先瘦身：`desktop.yml` 去掉用不到的 `convert`（省一份引擎探测 + 若干 MB） | 数十 MB |
| ⑤ | 壳 Host | 337MB（按需启动 ✓） | 已符合"工作"模型；337MB 本身是后续优化点 | — |

**合法例外**：剪贴板引擎（5.7MB）—— "捕获"必须一直在，它本身就是那颗灯泡；同理桌面服务是**可见的桌面层**
而非"后台灯泡"，不适用"即用即弃"，只适用"瘦身"。

### 内存改造落地（用户 2026-09-17："这一轮把内存相关的任务一并做了"）

**② 索引引擎"空闲即退" —— 已完成（Rust + .NET 双侧）**

- Rust（`engine-index/`）：
  · `engine.rs` 新增活动时钟：`init_idle_clock()` / `touch_activity()` / `idle_secs()`；
  · `ipc.rs`：**每收到一帧请求**记一次活动（后台周期补扫**刻意不算** —— 否则永远"非空闲"，机制形同不存在）；
  · `main.rs`：新增空闲监视线程（30s 粒度），超时走**既有** `engine::request_shutdown()`（与 IPC `shutdown`
    同一条优雅退出路径，不新增第二条退出语义）；`IDLE_EXIT_SECS = 600`（10 分钟），
    `BD_INDEX_IDLE_SECS=0` 关闭、或设小值用于验收（诊断缝）。
  · `cargo check` + `cargo build --release` 均通过；新 exe 已投放到 `%LOCALAPPDATA%\BetterDesktop\`。
- .NET：
  · `watchdog/Program.cs`：**移除 Index.Engine 目标** —— 守护它等于"退场 3 秒后又把它拉回来"，
    与空闲退场直接冲突（这是本次改造里最容易踩的坑）；
  · `shell-index-ipc/IndexEngineLauncher.cs`：`EnsureEngine` 加 `extensions.index.enabled` 门控
    （关掉功能后连按需拉起都不做，否则开关形同虚设），读法沿用 watchdog 同款轻量扁平键扫描。
  · 编译：watchdog / shell-index-ipc / 宿主 均 0 错误。
- 验收方式（观察式）：无检索 10 分钟后 `BetterDesktop.Index.Engine` 应从任务管理器消失；
  再打开开始菜单搜索，应由客户端按需拉起（引擎日志可见新实例启动 + 首建索引）。
  ⚠️ 诊断实例抢跑失败记录：手动带 `BD_INDEX_IDLE_SECS=20` 启动时**撞单实例互斥退出**（宿主里的
  客户端在重连循环里先把它拉起来了）—— 这同时印证了"客户端确实按需拉起"，但说明**手动短窗验收
  必须先让客户端停下来**，否则测不到。

**③ 截图按需 / ① 剪贴板面板迁手柄 —— 未做（本轮只完成 ②）**

两者都需要**引擎侧原生窗口/热键工程**，不是 .NET 侧改开关能解决的：
- ③：现在热键由 `BetterDesktop.Capture` 自己持有（所以它必须常驻 84MB）。要按需化，得把热键注册搬到
  常驻的 Rust 剪贴板引擎（`engine/src/hotkey.rs` 已有 RegisterHotKey 设施），按下时由引擎拉起截图 exe，
  截图用完即退。
- ①：面板 193MB 常驻只为挂右缘「›」侧边手柄（`EdgeHandleWindow`）。要按需化，得由 Rust 引擎绘制这个
  极瘦原生窗口（topmost + WS_EX_TOOLWINDOW + 点击 → 拉起面板 exe），面板本身改成"打开即拉起、关窗即退"。
- 预计回收：③ 84MB、① 190MB（合计约 274MB），且都不动 L1/L2/L3 分层。
- 顺序建议：③ 先（设施现成，改动小）→ ① 后（新增原生窗口，需真机验证点击/定位/DPI）。

#### 剪贴板开关的"关"入口（用户 2026-09-17 反馈："只有打开面板的入口，没有关闭这个功能的入口"）

查证：`extensions.clipboard-history.enabled` **此前没有任何 UI 写入口**（只有 panel/plugin/watchdog 在读）。
补齐（与 index / capture 同款路径，走共享目录单点）：
- `DesktopToggleCatalog` 新增 `clipboard` 命令名 + `ClipboardKey`；
- 托盘「功能开关」新增「**剪贴板历史**」行 → `--toggle-key clipboard`；
- **自绘右键 →「桌面控制」里那一项从"只能打开"改成开关**（用户 2026-09-17 追加反馈"只有打开功能，
  没有关闭功能"）：原来是 `Kind=Command` + 标题带省略号的「剪贴板历史…」（点了只会开面板）；
  现改为 `AddToggle`（目录单点驱动、勾选态 = 是否启用），与「热键侧板显隐」同构：
  关 → 面板/引擎退场 + 侧边「›」手柄消失；开 → 引擎/入口回来并**顺便打开侧边面板**。
  打开面板的另两条路仍在（全局热键、侧边手柄）。

**真机实测（2026-09-17 20:09，两个方向都过）**
```
关闭 → [Info] shell.clipboard: 已停止剪贴板面板与引擎（面板存活=False，引擎存活=False）
      切换（免宿主）：extensions.clipboard-history.enabled=False
开启 → [Info] shell.clipboard: 已拉起 BetterDesktop.Clipboard.Engine.exe
      [Info] shell.clipboard: 已拉起 BetterDesktop.Clipboard.Panel.exe
      [Info] shell.clipboard: 已直接拉起面板（--open）
      切换（免宿主）：extensions.clipboard-history.enabled=True
```
**重要副产品（设计上是好事）**：宿主当时**并没有运行**，走的是"免宿主"分支 —— 而动作照样完成了，
因为**桌面服务自己也加载了 `clipboard-history`**，`ApplyEnabledState` 就在服务进程内执行。
即：这条开关不需要宿主在线 ✓（关掉主程序后剪贴板功能依然能被开关控制）。

**真机 bug 修复："重新开启后没有侧边入口"（2026-09-17 20:13 修复并实测）**

用户反馈：关掉再开启剪贴板历史后，面板回来了但**右缘「›」侧边入口不见了**。

根因（面板自己的日志直接给出）：
```
[panel] 扩展中心 clipboard-history 未启用（enabled=false），面板不装配入口
```
**跨进程时序 bug**：面板是独立进程，它启动时**自己读 settings.json** 判断"要不要装配侧边手柄"
（`enabled=false` → 故意不装配，这段逻辑本身是对的，等于尊重扩展中心开关）；
而 `SettingsService` 落盘有 **debounce** → 开启流程里"翻设置 → 立刻拉起面板"时，文件里还是旧值 `false`
→ 面板读到旧值 → 静默不装配手柄（而且**不留任何错误日志**，只看日志根本查不出来）。

修法（`ClipboardPlugin.ApplyEnabledState` 的开启分支）：
```csharp
// 先把设置立即落盘，再拉起面板 —— 否则独立进程读到的还是旧值
(_settings as SettingsService)?.FlushNow();
ClipboardEngineLauncher.EnsureEngine(log);
...
```
配套：`EntryHost.Create` 补一条**正向日志**「侧边入口已装配（entry-style=…）」，
与既有的「不装配入口」互为对照（本次就是靠"日志里什么都没有"才绕了弯）。

实测（关闭 → 部署 → 开启）：
```
20:13:49.770 [panel] 侧边入口已装配（entry-style=sidebar）
```
同一修复对**托盘那条入口**同样有效（两条入口走的是同一个 `ApplyEnabledState`）。

**真机复测："用户手动关开后入口仍缺失"（2026-09-17 20:18 修复并实测）**

用户复测：自己关掉再开启剪贴板，叉掉面板后侧边入口仍不在。探针测量直接给出结论：
```
after OFF   fileTime=20:16:57.612  enabled=False
ON +420ms   fileTime=20:16:57.612  enabled=False   ← 设置根本没落盘（我的 FlushNow 没执行）
```
即上一条修复（`FlushNow()`）**代码是对的但没真正部署**：我改的是 `shell-clipboard` 工程、却只编译了它，
**桌面服务输出目录里那份 `BetterDesktop.Shell.Clipboard.dll` 仍是旧的** —— 于是我从服务 bin 复制过去的
"新 dll"其实还是旧代码（同一个坑第三次）。整份重建服务 + 整份投递后：
```
settings.json: True
20:18:50.862 [panel] 侧边入口已装配（entry-style=sidebar）   ✓
```

**为此改进了 `scripts/deploy-desktop-service.ps1`**（以后不要再手挑 dll）：
- **纯 ASCII**：Windows PowerShell 5.1 对**无 BOM** 文件按 ANSI 读，脚本里的中文会变乱码导致解析失败
  （我的旧版就是这样坏的，用户拿去也跑不通）——脚本内已注明"保持 ASCII"；
- **自动停服务 → 投递 → 重启**：服务运行时其装配件是锁定的，硬投递会得到"半份新半份旧"的目录
  （正是本次教训）；现在先优雅 `--stop`（顺带恢复 explorer 图标/任务栏），失败再强杀，投递完自动拉起。

**部署教训（本轮踩到三次，务必照做）**：桌面服务的插件是**多份 dll 组合**的（`Shell.Desktop` / `Shell.Core`（目录单点）/
`Shell.Clipboard` / `Shell.Clipboard.Ipc`）——只更新其中一份会出现"菜单有了但动作不认识"这类怪现象。
`scripts/deploy-desktop-service.ps1` 是整份产物投递，请用它，不要手挑 dll。

#### ③ 截图按需化：第一步已完成（一次性模式）

**关键发现（让 ③ 变便宜）**：截图 app 早就把"谁持热键"和"谁来截屏"解耦了 ——
`Local\BetterDesktop.Capture.Trigger`（命名事件）+ `--capture` CLI + "非首实例 → 通知首实例后自己退出"。
所以按需化只需要一个**常驻热键持有者**，按下时把截图 exe 拉起来即可。

已完成（纯增量，不带参数时行为完全不变）：
- `shell-capture/App.xaml.cs`：新增 `--capture-now` 一次性模式 —— 启动即进入框选流程，
  **流程结束（成功/失败/取消）即 Shutdown 退出**；`_oneShot` 标志 + `CompleteFlow` 收尾。
- `shell-hotkey-panel/HotkeyDeclarations.IsOwnerAlive`：`capture` 的探活目标改为 **Agent**
  （新的热键持有者；Agent 未跑才退回看截图进程）。
- 编译：shell-capture / shell-hotkey-panel 均 0 错误。

**剩余第二步（下一步做）**：把热键持有者落到 **Agent**（决定它是"本来就常驻的 L1 宿主"，边际成本 0；
而不是"再养一个灯泡"）：
1. Agent 读 `%LOCALAPPDATA%\BetterDesktop\capture\settings.json` 的 hotkey 节（单一写入口仍在该设置界面）；
2. 隐藏 HwndSource + `RegisterHotKey`（与热键侧板同款；用 shell-clipboard-ipc 的 `HotkeySpec` 解析，勿再写一份）；
3. 按下 → 拉起 `BetterDesktop.Capture.exe --capture-now`（定位沿用同目录 → `%LOCALAPPDATA%` 两段回退）；
4. 看门狗**移除 Capture 目标**（截图不再常驻，守护会把它又拉成常驻 —— 与索引引擎同一个坑）；
5. 端到端验收：按 Win+Shift+B → 框选覆盖层出现 → 截完截图进程消失（任务管理器可见）。

**为什么本轮停在这**：热键**换持有者**是"两个进程抢同一个键"的敏感改动 ——
截图 exe 自己的注册会失败（它已 catch 并降级"仅托盘可用"），所以 Agent 侧必须先具备
"注册成功 + 能拉起截图"两件事再切换；只改一半会让用户按热键时"面板在、截图不来"。
故本轮只交付**不会破坏现状**的第一步（一次性模式），第二步单独一轮完成 + 真机验收。

#### ③ 截图按需化 · 第二步（热键持有者落到 Agent）—— 已完成并真机验收（2026-09-17 20:38）

**为什么**（用户判据："常驻只允许极瘦进程，做不到 5MB 的一律当工作——用即弃"）：
截图 exe 私有内存实测 **84MB** 超预算 → 它只该在"一次截屏"期间存在；
但**全局热键必须由活着的进程持有** → 交给本来就常驻的 L1 宿主 **Agent**（边际成本 0，不再养第二个灯泡）。

| 位置 | 内容 |
|---|---|
| `agent/Capabilities/CaptureHotkeyOwner.cs`（新） | 读 `%LOCALAPPDATA%\BetterDesktop\capture\settings.json` 的 `hotkey{enabled,modifiers,key}` → 经 `HotkeySpec`（唯一解析器）解析 → 隐藏 `HwndSource` 上 `RegisterHotKey` → 按下即以 `--capture-now` 拉起截图（一次性模式，截完即退）。10s 轮询配置 → 改键/开关**无需重启 Agent**；关掉 `extensions.screenshot.enabled` 即交还键位 |
| `agent/Program.cs` | 启动时接管（UI 线程构造）、退出时交还 |
| `agent/BetterDesktop.Agent.csproj` | 引用 `shell-clipboard-ipc`：复用 `HotkeySpec` 与 `ClipboardEngineLauncher.LocateCaptureExe`，**不自持副本** |
| `watchdog/Program.cs` | **移除 Capture 目标**（守护会把它拉回常驻，与"用即弃"直接冲突——与索引引擎同一个坑） |

**两个真机踩到的坑（都写进代码注释，免得后人重踩）**
1. **`RegisterHotKey` 必须由 HwndSource 所属的 UI 线程发起**：配置轮询原本跑在线程池线程上，
   Windows 返回**假占用 `0x580`（已注册）**，而 PowerShell **同参数**注册却成功 → 极易误判成"键被占"。
   现排障手段：失败路径自动用**同线程同 ID 换一枚几乎不会被人占用的主键（F9）**再试一次，
   "键被占"与"注册机制异常"当场分开（本次正是靠它定位）。
2. **`Dispose` 也必须回 UI 线程**：否则 `HwndSource.Dispose()` 抛
   "调用线程无法访问此对象，因为另一个线程拥有该对象"——注册侧同一个坑的镜像。

**真机验收**
```
[20:35:13] [capture] 已接管截图热键：Shift+Win+B（启动）——按下即按需拉起截图（--capture-now，截完即退）
[20:37:54] [capture] 已按热键拉起截图（--capture-now）：…\BetterDesktop.Capture.exe
           截图进程 64588 / 51.8MB（一次性实例，停在框选覆盖层上；Esc 取消即消失）
```
→ 截图**不再常驻**：不按热键时任务管理器里没有它（省 84MB），按下才出现、截完即退。
另核对 `HKCU\Run`：截图没有自启项，开机不会把它带回来（与"按需"一致）。

**连带效果**：托盘「功能开关 → 截图工具」现在**真正控制热键归属**（关掉即交还键位）。

**⚠️ 验收时踩到的部署坑（用户报"按了热键没反应"的真因，20:45 修复）**

取证链：Agent 日志显示**已按热键拉起截图** ✓ → 但部署目录的 `BetterDesktop.Capture.dll` 是
**9/15 23:28 的旧构建** ✗（`--capture-now` 是 9/17 才加进源码的）→ 旧 exe 不认识该参数 →
按"常驻模式"启动、**不弹框选**。即 **Agent 侧完全正确，是 `%LOCALAPPDATA%\BetterDesktop` 里那份没跟着更新**
（`scripts/deploy-clipboard.ps1` 第 3 段的部署 9/15 之后没再跑）。旧构建还暴露出第二个坑：
部署目录里的 `Microsoft.Windows.SDK.NET` 与构建不一致（24877600 vs 26086944 字节）→ WGC 抓屏
`FileNotFoundException`。

**修复**：按仓库脚本第 3 段重做部署（`Copy-Item "$captureOut\*" $dst -Recurse -Force`）。
**三条纪律（写死在这）**
1. 改过截图代码后**必须重跑** `scripts/deploy-clipboard.ps1`（至少第 3 段），
   否则 `%LOCALAPPDATA%` 里那趟永远是上次跑脚本时的版本；
2. 复制**必须保留目录结构**（`"$out\*"` + `-Recurse`）：`runtimes\win-x64\native\` 里有原生依赖，
   按文件平铺（本次的误操作）会把结构拍平、丢掉原生件的探测路径；
3. **同目录两套投影**的隐患（本次实测）：截图要 `Microsoft.Windows.SDK.NET.Ref 10.0.22621.57`、
   面板要 `10.0.19041.57` —— **同一文件名、两个版本**，谁后部署谁赢。本次换完后面板实测仍正常
   （`主题引导完成 / 侧边入口已装配` ✓），属结构性风险，长期应像桌面服务那样各自子目录部署。
   另：Agent 现在会把截图 exe 的**文件版本**写进日志，陈旧/局部部署下次一眼可见。

**修复后验收证据**
```
[Agent]     已按热键拉起截图（--capture-now）：…\BetterDesktop.Capture.exe（版本 …）
[capture.log] 截图会话开始（全屏采集）→ 截图完成：2560×1440 @(0,0) 模式=FullScreen 后端=BitBlt
[capture.log] 一次性截屏结束，退出进程（按需模式）      ← 触发 → Esc → 进程自行退出 ✓
```

**仍未解决（既有问题，非本次引入）**：WGC / DXGI 两条后端都不可用，实际走 **BitBlt**（功能可用，
但 GPU 合成窗口 / 受保护内容可能截成黑屏）。WGC 报
`DllNotFoundException: windows.graphics.directx.direct3d11.interop.dll`，而该原生件
**不在构建产物、不在仓库、不在 NuGet 缓存**里 → 工程的 CsWinRT 原生资产没被发布出来（单独排查）；
DXGI 报 `IDXGIAdapter1` 的 `QueryInterface` 全适配器 `E_NOINTERFACE`。

**仍未做**：① 剪贴板面板手柄迁 Rust（190MB）。

**仍未做**
- M4（宿主侧「设置」入口收敛 + 热键分区）。
- ⚠️ 用户工作区里 `packages/shell/shell-menu-bar` 有一批未提交改动，导致宿主编译失败
  （`SearchPopupWindow.cs` CS0169 两个未使用字段）——与本计划无关，但会挡住宿主重建（验收第 1 步前需先处理）。
