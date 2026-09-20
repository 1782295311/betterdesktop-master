# 重复问题审计 · better-desktop-cordis

日期：2026-09-14
范围：`better-desktop-cordis/`（排除 `obj/`、`bin/`、`docs/`、`Temp/`；快照树单独统计）
方法：k=12 行滑窗克隆检测（归一化空白，跳过空行/纯注释/using）+ 语义重复人工核销（同一能力多处实现）

---

## 0. 结论摘要

| 类别 | 规模 | 处置 |
|---|---|---|
| A 快照式死拷贝（`backups/`、`packages/backups/`） | **798 个 .cs / 125,465 行** + 62 个 .cs / 8,846 行 | 移出仓库即可，零风险 |
| B 语义重复（同一能力 N 份实现） | 11 组，其中 2 组含已知**行为分叉** | 就近收口，逐批做 |
| C 文本级整块复制（≥12 行克隆） | 21 处，最长 42 行 | 多数不值得动 |

关键数字：活代码里**没有**大块复制粘贴（最长克隆 42 行，出现在两个 IPC 包）。真正的债在 B 类——画 5 条图标链、5 份 DefView 查找、2 份毛玻璃（且降级策略已跑偏）、6 套日志。A 类快照树的行数**超过全部活代码**，并且会污染 grep / 代码检索 / 任何静态分析（本次扫描第一轮就被它带偏）。

---

## 1. P0 · 快照树（先做，纯删除）

| 路径 | .cs | 行数 | 说明 |
|---|---|---|---|
| `backups/rollback-20260909/` | 639 | ~110k | 整个项目快照（host/packages/docs/scripts/Temp/.agents 全含） |
| `backups/stage1-stable/` | 194 | ~10k | 部分包快照 |
| `backups/stage1-pre-clean/` | 107 | ~5k | 部分包快照 |
| `packages/backups/stage1-pre-clean/` | 62 | 8,846 | **同名的第二份** pre-clean 快照，藏在包目录里 |

危害（都是实打实踩到的，不是洁癖）：

1. `packages/backups/stage1-pre-clean/` 里的 `shell-core.csproj` 仍引用**已不存在**的 `../shell-plugin-sdk/BetterDesktop.Shell.PluginSdk.csproj`——任何按目录树扫描的工具都会把它当活配置读。
2. 本次克隆检测第一轮把 `backups/stage1-*/shell-dock/DockWindow.xaml.cs` 当成"活代码重复"，误报 86k 条。
3. 全仓 grep / 代码检索 / 审计（含本次）都会被这些副本命中，结论污染。

处置：先打包成一个 zip 放仓库外（或留 `git tag`），然后删目录。全仓当前 **还没有任何 commit**（`No commits yet`），删掉不会丢历史——但正因如此，删之前必须留一份物理备份。

顺手：`Temp/`（`PerfBenchmark/`、`IndexBaselineProbe/`、若干 .ps1/.py 探针、日志）也在仓库内，属同类垃圾，一并移出。

> 不动：`docs/plans/` 100+ 份计划。按项目定规它们是**时间序留档**（后写的覆盖先写的），不是重复。

---

## 2. P1 · 语义重复（按性价比排序）

### 2.1 剪贴板面板：两套 UI 并存（最大单笔）

| 位置 | 体量 | 现行路径 |
|---|---|---|
| `packages/shell/shell-clipboard/ClipboardHistoryWindow.cs` | 62 KB / 1509 行 | 宿主内回退：`ClipboardManager.cs:1103` |
| `packages/shell/shell-clipboard-panel/PanelMainWindow.cs` + `RecentStrip.cs` | 125 KB + 44 KB | 引擎进程面板：`ClipboardIpcClient.cs:1612` → `ClipboardEngineLauncher.OpenPanel` |

证据：`RecentStrip.cs:19` 自述"视觉对齐 ClipboardHistoryWindow.CreateEntryRow（shell-clipboard 1509 行迁移基准）"；两者存在 12 行级克隆（`ClipboardHistoryWindow.cs:155` ↔ `PanelMainWindow.cs:287`）。

两条路都活着（宿主本地 vs 引擎后端），所以这是**决策题而不是 bug**：
- 若本地回退已不需要 → 删 `ClipboardHistoryWindow.cs`（62 KB）+ `ClipboardManager.OpenHistoryWindow` 本地分支，`IClipboardService.OpenHistoryWindow` 只保留 IPC 实现。
- 若还要留着 → 把"条目行"抽成共享工厂（`RowFactory`）供两边用，别再各改各的。

### 2.2 图标提取：5 条链、5 份 SHFILEINFO

`shell-core/Native/NativeMethods.cs` 已经提供 `SHFILEINFO`(L708) + `SHGetFileInfo`(L689)，`shell-app-source` 也已经提供统一入口（`Win32ShellIconService`、`HighResIconExtractor`、`IconImageConverter.GetImageFromHIcon`——内部已 Freeze + DestroyIcon）。仍然各写一遍的：

| 位置 | 重复内容 |
|---|---|
| `shell-dock/DockWindow.xaml.cs:738-765` | 自建 `SHFILEINFO` + `SHSTOCKICONINFO` + `SHGetFileInfoPidl` + `SHGetStockIconInfo` |
| `shell-desktop/Services/ShellNamespaceHelper.cs:22,35` | 自建 `SHFILEINFO` + `GetIcon(clsid)` |
| `shell-dock/DockWindow.xaml.cs:807` | `GetShellIconByClsid(clsid)` —— **与上一行是同一个函数**（≈40 行 ×2） |
| `shell-desktop/Services/DesktopBrowser.cs:220` | 自建 `SHFILEINFO` |
| `shell-clipboard/Native/FileIconCache.cs:18` | 自建 `SHFILEINFOW` + `SHGetFileInfoW` |
| `shell-context-menu/Services/MenuItemIconCache.cs`、`shell-menu-bar`（6 处 `CreateBitmapSourceFromHIcon`） | 各自的 HICON→BitmapSource 转换 |

最小修：把"shell 命名空间项(CLSID/路径) → ImageSource"这**一个函数**收口到 `shell-core`（`NativeMethods` 同包，dock/desktop 都已引用），其余删自建结构体与 P/Invoke。不新建接口、不新建服务。

### 2.3 毛玻璃：两份实现且已行为分叉

`shell-core/Vibrancy/DwmHelper.cs` vs `shell-clipboard-panel/PanelVibrancy.cs`（18 处 `DWMWA_*` 常量全量复制）。

- 分叉事实：BlurBehind 失效时的降级目标，`DwmHelper` → Acrylic(`DWMSBT_TRANSIENTWINDOW`)；`PanelVibrancy` → **Mica**(`DWMSBT_MAINWINDOW`，2026-09-12 用户拍板)。同一系统、同一 API、两种观感。
- `PanelVibrancy` 的注释理由是"独立 exe 不引 kernel，故不复用"——**已经失效**：`shell-clipboard-panel.csproj` 现在同时引用 `shell-core` 与 `api`，`PanelVibrancy` 自己就在用 `BetterDesktop.Shell.Core.Native.NativeMethods`。

最小修：`DwmHelper` 放开可见性（或用 `VibrancyService` 的 public `Apply/Disable`），面板删掉 `PanelVibrancy`（≈150 行）。**前提是先把降级策略拍成一种**（统一 Mica，或统一 Acrylic）——这是行为变更，需要一次确认，不能默默统一。

### 2.4 桌面宿主(DefView)查找 + 嵌入 + 看门狗：5 份

`host/IconRestoreSentinel.cs:126,151`、`shell-desktop/DesktopPlugin.cs:551,790`、`shell-desktop/Windows/DesktopWindow.cs:682`、`shell-dock/DockWindow.DesktopLayer.cs:154`、`agent/Capabilities/DesktopIconsCapability.cs:312`。

"Progman → DefView，否则遍历 WorkerW → DefView"这段循环、`SetParent` 嵌入、3 秒看门狗、`SW_SHOW` 兜底，被抄了 5 遍。克隆检测也命中了其中两处（`host/IconRestoreSentinel.cs:49/62` ↔ `DesktopPlugin.cs:271/186`）。

注意：`DockWindow.DesktopLayer.cs` 顶部明写"本文件自足，不动 shell-core"——那是**刻意的实验隔离**（该功能今天才落地、env 开关默认关）。这类隔离是可接受的重复，可以先不动；但 `host` 与 `shell-desktop` 之间已无隔离理由，收口到 `shell-core` 一个 `DesktopHost.FindDefView()/FindListView()` 即可（host、dock 都已引用 shell-core）。

### 2.5 日志：6 套实现

| 实现 | 位置 | 备注 |
|---|---|---|
| `DiagnosticLog` | `kernel/Core/DiagnosticLog.cs` | **唯一管道**（270+ 调用点，sink 由宿主注入） |
| `DebugLog` | `shell-window-tracker/DebugLog.cs` | 同形 API `Trace(tag,msg)`，却**直写桌面 `BetterDesktop_debug.log`**，与 M10 单管道改造直接冲突 |
| `PanelLog` | `shell-clipboard-panel/PanelLog.cs` | 独立 exe，可接受 |
| `AgentLog` / `TrayLog` / `UpdaterLog` | 各自 exe | 独立 exe，可接受 |
| `private Trace` | `ClipboardIpcClient.cs:64`、`IndexIpcClient.cs:61` | 小重复 |

最小修：只处理 `DebugLog`——window-tracker 已引用 kernel，删掉它改调 `DiagnosticLog`（先确认 `FileLogSink` 是同步落盘，否则会丢掉"崩溃前最后一帧"这个它存在的理由）。其余独立进程的日志不要造平台层。

### 2.6 监视器/工作区：6 处，4 处已经用了共享层

canonical = `shell-core/Native/NativeMethods.cs`（`MonitorFromPoint`/`MonitorFromWindow`/`GetMonitorInfo`/`GetDpiForMonitor`）。
正例：`shell-start-menu/Native/MonitorInterop.cs`（typed record 包装，可直接当模板）、`shell-menu-bar/Contracts/MenuBarScreen.cs`、`shell-window-tracker/Thumbnail/ThumbnailWindow.cs:437`、`DockAppBarReservation`（部分）。
仍自建结构体的：`shell-dock/Services/DockLayoutService.cs:207-241`（私有 `RECT`/`MONITORINFO` + 私有 `EnumDisplayMonitors`/`MONITOR_DEFAULTTONEAREST`）、`shell-dock/Native/DockAppBarReservation.cs:37-55`（私有 `NativeRect`/`MonitorInfo`，却调 `NativeMethods.GetMonitorInfo`）。

最小修：把 `MonitorInterop` 那套提成 shell-core helper，`EnumDisplayMonitors` 补进 `NativeMethods`（目前只有 Point/Window 两个入口），然后删 dock 的私有副本。

### 2.7 菜单弹层：3 份同形实现

`shell-desktop/Services/DesktopMenuPopup.cs`、`shell-dock/Services/DockMenuPopup.cs`、`shell-start-menu/Services/StartMenuPopup.cs`——`MenuItemDef → WPF ContextMenu` 的构建（含 `BuildItem`，克隆检测命中 12 行 ×3）、AbsolutePoint 定位、多屏钳制、Esc/失焦收敛，各写一遍。

这不只是行数问题：**右键菜单以后任何行为调整都要改三处，且已经出现三者策略漂移的风险**。抽一个 builder（放 `MenuItemDef` 契约旁）后，三个 `Show` 各剩一两行。

### 2.8 其余（低优先，可批量做）

- `NullVibrancy` 测试替身 ×3：`shell-desktop/DesktopPlugin.cs:1205`、`shell-menu-bar/MenuBarPlugin.cs:203`、`shell-quick-note/QuickNotePlugin.cs:168`（提到 api/共享位置）。
- 设置分区样板 ×6：`SettingsUi` 已经收口了 `GroupCard`（其注释自述"此前 8 个分区各复制一份"），但 `TitleBlock`/说明文字/开关行仍在各分区重写——克隆命中 `AppSourceSection.cs:102↔DockPinnedSection.cs:91`、`DesktopSection.cs:115/141↔MenuBarSection.cs:93/LeftDockSection.cs:177`、`LeftDockSection.cs:125/139↔ThemeSection.cs:264/52`、`StartMenuSection.cs:142↔TaskbarAppearanceSection.cs:161`。往 `SettingsUi` 再加 3–4 个原语即可，**不要一次重写 8 个分区**。
- 开始菜单 4 种布局：`AllAppsLayout.cs:196/221↔ClassicLayout.cs:75/103`、`Win10Layout.cs:586↔Win7Layout.cs:477`。
- IPC 传输/客户端：`shell-clipboard-ipc/ClipboardTransport.cs:15` ↔ `shell-index-ipc/IndexTransport.cs:15`（**42 行，活代码最长克隆**）、`ClipboardIpcClient.cs:69↔IndexIpcClient.cs:174`（14 行）。收口前先确认长度前缀/超时差异是刻意的。
- "定位引擎 exe + 拉起 + 探活"三件套：`ClipboardEngineLauncher` / `IndexEngineLauncher` / `MenuBrokerClient`（注释自述"同款"）。
- `Convert` 引擎样板：`CalibreEngine.cs:59↔TesseractEngine.cs:69`（12 行）。

---

## 3. 明确不做

- **不为图标/毛玻璃/监视器造接口或 DI 服务**。现成函数就近收口 = 删代码；加抽象 = 加代码 + 加间接层。
- **不删 `docs/plans/`**：时间序留档是定规（后写覆盖先写）。
- **不统一 5 个独立 exe 的单实例 Mutex / 日志**（host/tray/agent/recovery/watchdog/panel）：它们进程隔离，天然各自一份，抽象收益为负。
- **不合并 Win10/Win7/Classic/AllApps 四种开始菜单布局**：差异就是它们的价值。
- **不为了克隆数字好看而动那 21 处 12–42 行小块**：多数是"守卫子句 + WPF 工厂"，抽出去反而更难读。

---

## 4. 建议执行顺序

1. **批 1（零风险）**：备份 zip → 删 `backups/**`、`packages/backups/**`、`Temp/**`；补 `.gitignore`。
2. **批 2（删代码型）**：毛玻璃统一（需先拍板降级策略）→ 图标提取收口到 shell-core → `DebugLog` 回归 `DiagnosticLog` → 监视器收口。
3. **批 3（结构型）**：菜单弹层三合一 → DefView 查找收口 → 设置分区原语 → IPC framing → 剪贴板两套面板的取舍决策。

每批验收：`dotnet build BetterDesktop.slnx`（0 警告 0 错误）+ 对应包单测（批 2/3 至少覆盖 shell-core / shell-dock / shell-desktop / shell-context-menu / *-ipc）。

---

## 5. 复现方法

滑窗克隆检测（K=12 行，去空白/注释/using，合并极大块后报告）：

```
对所有活 .cs 取每 K 行连续窗口 → 归一化为字符串 → 按内容分组
→ 组内按 (file, start) 排序，合并 s+1 相邻项成链 → 向右延伸直到内容不再相同
→ 已覆盖行打标避免重复报告 → 输出长度 ≥ MIN 的块
```

本次结果：活文件 473 个（≥12 行有效代码者），≥12 行克隆 **21 处**，最长 **42 行**。
