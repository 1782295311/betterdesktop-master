# 剪贴板重复问题 · 深挖

日期：2026-09-14
上游：`docs/audits/2026-09-14-duplication-audit.md` §2.1
结论一句话：**剪贴板的重复不是"同一份代码写了两遍"，而是"一份被冻结的旧实现 + 一份持续演进的实现"并存；真正该修的不是重复，而是一条已经写好、却没人接线的双写守卫。**

---

## 1. 现状：一个功能，四个实现体

| 角色 | 位置 | 进程 | 是否主路径 |
|---|---|---|---|
| 数据面（监听/捕获/去重/存储/查询/热键） | `engine/`（Rust，108 个内联单测） | 常驻独立 exe | ✅ 默认（`backend=engine`） |
| 入口 + UI 面 | `shell-clipboard-panel/`（WPF exe） | 常驻独立 exe | ✅ 默认 |
| 宿主代理 | `shell-clipboard-ipc/ClipboardIpcClient` | 宿主内 | ✅ 默认（`Provide<IClipboardService>`） |
| 宿主内实现（"legacy"） | `shell-clipboard/ClipboardManager` + `ClipboardHistoryWindow` | 宿主内 | ⚠️ 仅 `backend=legacy`（设置页可见、需重启宿主） |

包体量（活代码行数，排除 obj/bin）：

| 包 | 行数 | 归属 |
|---|---|---|
| `shell-clipboard-ipc` | 2,606 | 两端共用（正例：`ClipboardEngineLauncher` 明确写了"避免两侧各自硬编码候选路径"） |
| `shell-clipboard` 共用部分 | 1,393 | 设置分区 894 + 插件 321 + 右键注册 110 + 菜单栏扩展 39 + 规则模型 29 |
| `shell-clipboard` **legacy 专属** | **3,920** | `ClipboardManager` 1857 + `ClipboardHistoryWindow` 1537 + `ClipboardSegmenter` 167 + `ClipboardNative` 176 + `ContentAnalyzer` 113 + `FileIconCache` 70 |
| `shell-clipboard-panel` **面板专属** | **4,945** | `PanelMainWindow` 2617 + `RecentStrip` 911 + `PanelTheme` 419 + `EdgeHandleWindow` 230 + `App.xaml.cs` 223 + `PanelUi` 165 + `PanelVibrancy` 139 + `EntryHost` 115 + `ClipboardImagePaths` 70 + `PanelLog` 41 + `EngineProber` 15 |
| `shell-clipboard-tests` | 1,228（82 用例 / 17 文件） | **15/17 文件直接引用 legacy 类型**；测试替身 `TestClipboardManager : ClipboardManager`（`TestSupport.cs:31`），即**测的就是 legacy 实现** |

---

## 2. 重复面 A：UI 做两遍（4,945 行 vs 1,537 行窗口）

`RecentStrip.cs:19` 自己写明了血缘：*"视觉对齐 ClipboardHistoryWindow.CreateEntryRow（shell-clipboard 1509 行迁移基准）"*——面板的条目行是从 legacy 窗口迁移过来的。逐项对照：

| 能力 | legacy（`ClipboardHistoryWindow`） | 面板（`PanelMainWindow` + `RecentStrip`） |
|---|---|---|
| 条目行（类型瓦片/3 行预览/meta/徽标） | `CreateEntryRow` + `CreateTypeChip` + `CreateMetaText` + `PreviewText` + `TypeGlyph` | `RecentStrip`（独立控件，994 行） |
| 头部 / 搜索框 / 筛选 chips | `CreateHeader` / `CreateSearchBox` / `CreateFilterChips` | `BuildHeader` / `BuildSearchBox` / `BuildFilters` |
| 暂停横幅 | `CreatePauseBanner` | `BuildPauseBanner` |
| 多选条 / 按序粘贴条 | `CreateMultiSelectBar` / `CreateSequentialBar` | `BuildMultiSelectBar` / `BuildSequentialBar` |
| 底栏 | `CreateFooter` | `BuildFooter` |
| 收藏 / 删除 / 粘贴 / 标签 | `TogglePin` / `DeleteEntry` / `PasteEntryAndClose` / `EditEntryTags` | `OnRowPin` / `OnRowDelete` / `PasteEntryToTargetWindow` / `BeginTagEdit` |
| 图标与图片缩略图 | `LoadFileIcon`（`FileIconCache`）+ `LoadImageThumbnail` | `RecentStrip` 内联 + `ClipboardImagePaths` |
| 空态 / 日期分组 | `CreateEmptyState` / `CreateDateGroupHeader` | `BuildEmptyState`（**无日期分组**） |
| 面板独有 | — | 引擎横幅、存储横幅、toast、按格粘编辑器、贴纸导入、分页 `LoadNextPage`、平滑滚动、中键贴回热键、状态槽、右缘滑出 |
| legacy 独有 | 来源筛选栏、日期分组、分隔线 chips、键盘选择索引、合并粘贴 | — |

**已经发生的分叉（这是重复的真实代价）**：

1. `RecentStrip.cs:43-45` 注释直接点名旧实现：*"面板用有序 `List` 维护（不用 `HashSet`：legacy 面板正是用 HashSet，顺序只能靠 .NET 内部插入序碰巧成立，属脆弱实现）"* —— `ClipboardHistoryWindow` 里确实还是 `HashSet<ClipboardEntry> _selectedEntries`。**同一个功能，一边修了 bug，一边没修。**
2. 面板这侧还在长（09-12 侧边栏入口、09-13 贴纸/按格粘/敏感遮罩），legacy 那侧只剩维护债。
3. 克隆检测在两者间命中 12 行级克隆（`ClipboardHistoryWindow.cs:155` ↔ `PanelMainWindow.cs:287`）——采样而已，真实重叠面远大于这一处。

所以准确的说法是：**legacy 窗口是"迁移基准"，不是"等价实现"**。把它留着当回退，等于留了一份已知有 bug、且没人再修的同功能 UI。

---

## 3. 重复面 B：主题令牌推导（`PanelTheme` 419 行）

`PanelTheme.cs:11-17` 自证：*"独立 exe 不引宿主 shell-settings/kernel，直接读同一份 `%APPDATA%\BetterDesktop\settings.json`……复刻 `AppearanceService.SyncAppResources` 的令牌全集与推导口径，保证与宿主窗口视觉一致"*。

判断：**这是跨进程架构导致的合理重复**，不是随手复制——面板进程确实拿不到宿主的 `Application.Resources`。代价是两条口径会各自漂移（宿主换调色板/加令牌时面板不知道），且面板只在启动时读一次（已声明的限制）。

处置建议：**不动**。除非已经出现"宿主改了主题、面板还是旧色"的实际观感投诉，否则不值得抽公共推导（抽了也仍要各自 ApplyToAppResources）。

---

## 4. 重复面 C：热键 / 自注入抑制 / 入口注册，三处并行

| 事项 | engine 侧 | legacy 侧 | 面板侧 |
|---|---|---|---|
| 全局热键 | 引擎注册 Ctrl+Shift+V / P / Backspace | `ClipboardManager.cs:1282-1284` 自己 `RegisterHotKey` | `PanelMainWindow.ApplyPasteBackHotkey`（贴回原窗口） |
| 自注入抑制（别把自己粘的内容又抓回来） | — | 自有 `_suppressCapture` + `ClipboardSuppressionTests` | `OnPasteHotkeyHook` + shell-core `NativeMethods.PasteInjectionTag` |
| 右键 4 场景注册 | — | `ClipboardShellMenuRegistrar.EnsureRegistered(null)`（指回宿主） | `EnsureRegistered(panelExePath)`（指回面板 exe） |

热键与抑制逻辑现在同时存在于三个地方，只有 `HotkeySpec.cs`（291 行，ipc 包）是共用的。这一块的重复**只在 legacy 启用时才会真正打架**（两套热键注册同一组合键，后注册者失败），所以它和下面 §5 是同一个问题。

---

## 5. 真正的问题：双写同一份历史库（不是行数，是数据）

### 证据链

1. 两个实现写**同一个文件**：引擎 `engine/src/store.rs:20` `STORAGE_FILE = "clipboard_history.json"`；legacy `ClipboardManager.cs:54` 同名同路径 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`。
2. 设计意图是"从根上杜绝双写"：`ClipboardPlugin.cs:20-23` —— 默认 engine 时"宿主绝不再持有 `ClipboardManager`，从根上杜绝双写同一份历史库造成的历史互相覆盖"。
3. 但 legacy 分支只做了一件事：把 `ClipboardManager` 装上，并 `Warn` 一句"引擎若也在跑会双写同一历史库"（`ClipboardPlugin.cs:104-105`）——**没有做任何阻止动作**。
4. 而引擎和面板**都是常驻进程，宿主不会关它们**：`ClipboardEngineLauncher.cs:132` "面板是常驻入口进程：宿主退出后应继续存活……故宿主 Unload 不关它"；`ClipboardPlugin.cs:192` "引擎是独立常驻进程，宿主卸载不关停引擎"。
5. 面板自己还会**主动拉起引擎**：`shell-clipboard-panel/App.xaml.cs:213-237` `EnsureEngineRunning()`（且宿主启动时默认就拉起面板入口，`ClipboardPlugin.cs:139`）。
6. **守卫写好了却没接线**：引擎侧有 `exit` 方法（`engine/src/engine.rs:606-610`，会 flush store 后退出），C# 侧有 `ClipboardIpcClient.RequestExit()`，注释写的正是用途——*"请求引擎退出（更新模式或切回 legacy 后端用）"*（`ClipboardIpcClient.cs:1635-1646`）。**全仓调用点：0**。
7. 设置页对这一切**零提示**：`ClipboardSection.cs:289-295` 的"后端模式"选项只提示"切换后端需重启宿主生效"。

### 可复现的破坏路径

> 用户在设置里选 `legacy` → 重启宿主 → 宿主装载 legacy（监听剪贴板、写 history JSON）；此时**上一轮 engine 模式的引擎与面板仍在运行**（面板还会自己把引擎拉起来）→ 引擎也在监听、也在写同一个 history JSON → 历史互相覆盖（正是插件注释想杜绝的情形）。

### 最小修复（不删任何代码，接线已有函数）

1. **宿主 legacy 分支加守卫**（`ClipboardPlugin.cs` legacy 分支，约 5-10 行）：切 legacy 前先停掉引擎——`ClipboardEngineLauncher` 加一个 `StopEngine()`（连一次 IPC → `RequestExit()` → dispose），legacy 分支先调它再 `new ClipboardManager(...)`；停不掉就**不激活 legacy**并告警（宁可不回退，也不要双写）。
2. **面板侧阻止回拉**（约 10 行）：`PanelTheme.ExtensionConfig()` 目前只读 `Enabled/EntryStyle/Capacity/DismissMode`，补读 `backend`；`App.EnsureEngineRunning()` 在 `backend=legacy` 时直接跳过（面板本身也可选择退出，因为此时它已无数据源）。
3. **设置页补一句真话**（1 行文案）："切到 legacy 会停用 Rust 引擎；若引擎/面板仍在运行，将出现双写，请先关闭它们"。

这三处加起来 < 30 行，且第 1 项是**把已写好、已写文档说明的守卫接上线**，属于修根因不是加抽象。

---

## 6. 三个选项与成本

| 选项 | 动作 | 成本 | 风险 |
|---|---|---|---|
| **A 冻结 + 接线守卫**（推荐） | legacy 只保命不演进（README/注释写死边界）；落地 §5 的 3 处小改；"后端模式"选项移到诊断区或加"仅排障"前缀 | < 1 小时 | 无（不删代码、不改主路径行为） |
| **B 删除 legacy** | 删 `ClipboardManager`/`ClipboardHistoryWindow`/`ClipboardSegmenter`/`ContentAnalyzer`/`ClipboardNative`/`FileIconCache`（3,920 行）+ 插件 legacy 分支 + 设置项 | 1-2 天 | ① 82 个 C# 用例整批删除——它们的 SUT 就是 legacy（`TestClipboardManager` 继承它）。好消息：迁走的核心逻辑在 Rust 侧有 **108 个内联单测**兜底（store 31 / html 14 / analyzer 12 / rules 6 / listener 6 / capture 5 / privacy 5 / settings 5 / engine 5 / model 4 / fingerprint 4 / dpapi 3 / formats 3 / thumb 3 / hotkey 2）。坏消息：**预览/遮罩/筛选这类 UI 相邻断言在 C# 侧没有替代者**（面板侧是纯 UI，无单测）；② 失去"引擎 exe 缺失/损坏时仍可用"的能力 |
| **C 抽共享实现给两边用** | 把条目行抽成共享控件 | 高 | 跨进程（独立 exe 与宿主）**无法共享 WPF 控件实例**，只能共享源码级复制——收益低于成本。不推荐 |

---

## 7. 决议与执行记录（2026-09-14）

**用户决议**：不要"留着死代码 + 加守卫"（选项 A），要**去掉死皮** —— legacy 整层删除（选项 B）。

### 已执行

| 动作 | 内容 |
|---|---|
| 删代码 | `shell-clipboard`：`ClipboardManager.cs`(1857) / `ClipboardHistoryWindow.cs`(1537) / `ClipboardSegmenter.cs`(167) / `ContentAnalyzer.cs`(113) / `Native/ClipboardNative.cs`(176) / `Native/FileIconCache.cs`(70) —— **3,920 行**，`Native/` 目录一并清空 |
| 改插件 | `ClipboardPlugin.cs` 359 → 231 行：删 legacy 分支、`BackendKey`、`_legacyManager`/`_sync`/`_activated`、`Activate`/`Deactivate`/`RunOnUi` 与 legacy 版 `ApplyConfiguration`；只剩"连 IPC + 拉起引擎/面板入口 + 推 apply_settings" |
| 改设置页 | `ClipboardSection.cs`：删「后端模式」选项与 `BackendKey`，诊断块只报引擎/面板状态；`BuildEngineCard`/`EngineStatusBlock` 去掉无用参数 |
| 删死函数 | `ClipboardIpcClient.RequestExit()`（0 调用）+ `IpcProtocol.M_Exit`（唯一消费者是前者） |
| 删测试 | `shell-clipboard-tests` 删 15 个 legacy 依赖文件（含 `TestSupport.cs` 的 `TestClipboardManager`）；`ClipboardServiceContractTests.cs` 重写为只保留 2 条与实现无关的结构断言；保留 `ClipboardEntryPreviewTests`/`ClipboardMaskedPreviewTests`（它们测 `ClipboardEntry.Preview`，属 api 契约） |
| 文档 | `shell-clipboard/README.md`（架构/配置表/Known Limitations 去 legacy 化，补"800ms 重写"事实）+ `shell-clipboard-panel/README.md` 一行 + `RecentStrip.cs` 的悬空血缘注释 |

### 验证

- `dotnet build BetterDesktop.slnx`：**0 个 `error CS`**（扫描全量输出）；40 条 MSB3026/3027 文件锁错误全部来自 `shell-status-tests` —— 由**上午 10:44 遗留的孤儿 `testhost`(PID 17220)** 持有其输出 DLL 导致的环境问题（`taskkill` 报"进程不存在"却仍在进程表里，无法清除），与本次改动无关（该工程不引用剪贴板任何包）。
- `shell-clipboard-tests`：**10/10 通过**；`shell-clipboard-ipc-tests`：**88/88 通过**（活路径 C# 侧覆盖仍在）。
- 未跑：`shell-status-tests`（被上述文件锁阻断，与改动无关）。

### 第二批（2026-09-14，按用户规则"无业务影响 + 不拖累主程序 → 进删除行列"）

**已执行**：

| 项 | 内容 | 验证 |
|---|---|---|
| 图标链收口 | 新增 `shell-core/Surface/ShellItemIcon.cs`（唯一实现）；删 dock 的 `GetShellIconByClsid` + 自建 `SHFILEINFO` + 自建 P/Invoke、删 desktop `ShellNamespaceHelper.cs`、删 `DesktopBrowser` 的自建 `SHFILEINFO` | core/dock/desktop 三包 0 警告 0 错（图标观感需真机看一眼） |
| 契约零消费者成员 | `IClipboardService` 删 12 个成员：`GetFilteredEntries`(非分页) / `GetSourceApps` / `TogglePin` / `ToggleSticker` / `PasteEntryAsPlainTextToActiveWindow` / `OpenFileLocation` / `ResetSequentialPaste` / `ImportEntries` / `IsMonitoringEnabled` / `CloseHistoryWindow` / `ShowFavoritesOnly` / `MonitoringStateChanged`；连带删 `ClipboardImportItem.cs`、`M_ListSources`/`M_Import`/`N_MonitoringChanged` 常量、客户端字段与分支、4 处相关测试 | 全仓 0 编译错；ipc-tests 88→**86 绿**、clipboard-tests **10 绿** |
| 引擎侧死代码 | `engine.rs` 删 `exit` 分支 + `flush_store()` + `unused_map_placeholder` + 配套的 `list_sources`/`import` 分支与 `cmd_list_sources`/`source_apps`/`cmd_import`（含 1 个 `source_apps` 单测）+ 失效 `use HashMap` | `cargo test` **107/107 绿**；`cargo build --release` 通过（发布件在 `engine/target/release/`，**未部署**，运行中的引擎未受影响） |

**进删除行列但需拍板/配合时机**：

1. ~~**毛玻璃降级策略**~~ → 见下节：不是"两个立场"，是**判断写反了**，已修。
2. **引擎发布件部署**：上面删掉的 Rust 代码要等一次"停引擎 → 拷 exe → 起引擎"才生效；部署会让采集短暂停止，需挑时机（且现在已无优雅停机入口，只能强杀 —— autosave 800ms 使最多丢 800ms 数据）。

### 第三批（2026-09-14）：毛玻璃"两个立场"背后的真因 —— 返回语义判断写反了

**用户直觉正确**：「正常情况下 DWM 模糊都能用，不会崩，就怕它直接跳过 DWM 用降级」。

**实证**：`SetWindowCompositionAttribute` 是 **BOOL 语义（非零 = 成功）**，不是 HRESULT。而 `DwmHelper` / `PanelVibrancy`（以及 `shell-taskbar/DwmapiHelper`）都写成 `if (hr != 0) → 失败`，于是：

- 每次调用（**含 `Disable`**）都返回 1、都被当失败 → 日志里 **1000+ 条 `失败 hr=0x00000001`**（09-07 → 09-14 全量日志里从未出现过 0x0），这也是"Win11 24H2+ 已失效"这个错误结论的来源；
- 控制流随之**正好反了：成功 → 记失败 + 叠加降级材质（host=Acrylic / 面板=Mica）；真失败（0）→ 什么都不做**；
- 副作用：非分层窗口（WCA 生效但 DWM backdrop 也生效的那类）会被硬套成**亚克力**，即 2026-09-12"不要亚克力"抱怨的根因。
- 旁证：技术库 `TECH-KNOWLEDGE/74-Windows内部接口逆向/7416` 里移植的 C++ 参考写法是 `if (!SetWindowCompositionAttribute(...))` —— **库是对的，代码写错了**。

**修正**：声明改为 `[return: MarshalAs(UnmanagedType.Bool)] bool`；三处调用改 `if (!ok)`；降级材质统一 **Mica**（仅真失败时使用）；删 `PanelVibrancy.cs`（面板改用 shell-core 的 `VibrancyService`）；面板把 `DiagnosticLog` 接到 `panel.log`（共用实现后失败仍可诊断）；任务栏 `DwmapiHelper` 同错一并修正（Win10 路径，此前潜伏）；加守卫测试 `SetWindowCompositionAttribute_ReturnType_IsBool_NotHresult` + `DwmSetWindowAttribute_ReturnType_IsHresult`（把 BOOL / HRESULT 两种相反语义钉住）。

**观感预期**：分层窗口上降级材质本来就被 DWM 忽略，所以今天的观感 = BlurBehind 本身；改完是**同一种模糊**，只是少了那次无意义的降级调用与假失败日志。若某类窗口此前是亚克力观感，改完会回到"透亮模糊"。

**不动（有实际作用 / 非死代码）**：

- **菜单弹层 3 份**（desktop/dock/start-menu 全部活跃，各自锚点语义不同）：属重构而非删死代码，改错会直接坏三处右键菜单。
- **DefView 查找 5 份**：host 的哨兵用于强杀后恢复桌面图标，属安全网，收敛后需一次"强杀宿主 → 看图标恢复"真机验证。
- **`ClipboardChanged` 事件链 + `push-events` 设置**：引擎侧事件与用户可见开关都在，缺的只是订阅方（预留，如灵动岛）——不是死代码。
- **引擎 autosave 800ms**：功能无害的性能项（≈1 MB/s 常态写盘），不是死代码。
- **面板 0 单测**：面板 UI 逻辑（`PanelMainWindow` 2,617 行）无覆盖；这是"缺测试"而非"多代码"，补测试属新增工作。
