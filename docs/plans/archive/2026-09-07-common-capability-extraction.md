# Cairo 开发计划 · 公共能力提取与重复收口

> Task: 基于四轮分析（重复代码 / 自我重复 / 公共能力缺失 / 卡顿关联 / 扩展性）的 **P0 整改立项**——① shell-core 建 Win32 Native 统一收口；② 弹窗体系上提公共层；③ 面板注册机制贯彻。**只规划不实现**（用户明确「立项但不动手修改」）。
> 证据基于 commit `0ecd841` 验证；技术力文档命中：711（扩展系统）、3603（菜单栏弹窗模式）、601（任务栏定位）、602（无焦点窗口）、603（失焦关闭）、604（亚克力分支）、607（Flyout 状态机）、7434（DWM P/Invoke 声明纪律）、7435（WinEventHook OUTOFCONTEXT 泵线程纪律）、7437（Win32/COM 互操作纪律）、7438（事件退订配对纪律）、1402（AppBar 多屏生命周期）、1407（自绘托盘）、1408（单实例）、3101（全局热键）、709（MEF 插件加载）、710（命令系统）、拆析-cairoshell-最初开源版；**未命中**：统一 Win32 Native 平台层（P/Invoke 收口目录，检索关键词：Win32 P/Invoke 收口 / Native 平台层 / User32 统一封装）。
> 证据头 schema 2（个人单仓库任务，省略 digest/manifest，HEAD sha 真实）；目标仓库 `better-desktop-cordis`，工作区含 245 项未提交改动（205 项他人改动 + arch-violation 计划约 40 项实施改动，禁止覆盖/回滚）。
> **衔接状态（2026-09-07 更新）**：`2026-09-07-arch-violation-fix-and-performance.md` **已完成实施**——8 项违规全落地（含 Taskbar→shell-taskbar 改名、cordis.yml 接线、ShellEvents 常量类、verify-no-cross-assembly-event 门禁）+ 性能工作流完成（基线 12 项微基准、6 候选诊断、2 个 Delta ACCEPTED）；其遗留（DiagnosticLog 完整删除、C4/C7 完整 shell 测量、TaskbarAppearanceEngine 钩子未处理）见本计划 §3.2 / §12。

## 1. Objective

用户可感知的结果（场景语言，非机制语言）：

1. **新增原生能力不再自带互操作样板**：写一个新功能需要调用 Win32 API 时，直接引用 `shell-core/Native` 的单一权威声明（DllImport/结构体/常量），不再复制粘贴、不再各自声明同一函数；现有散落的 ~90 处 P/Invoke 重复声明收敛。
2. **窗口事件钩子只有一份正确实现**：3 份 WinEventHook（其中 shell-taskbar 一份违反泵线程纪律、回调可能永不触发）合并为单一 `WinEventPump`，全部钩子走 STA 泵线程 + 字段强引用 + finally Unhook；前台窗口/窗口创建销毁事件在真机上稳定送达。
3. **弹窗面板统一继承公共基类**：MenuBar / Dock / StartMenu / Desktop 四处独立弹窗定位逻辑收口为公共定位服务，面板外观（主题令牌）、失焦自动收起、无焦点显示由基类统一提供；新弹窗只需 `BuildContent()`。
4. **菜单栏面板可被任意包注册**：新增一个菜单栏面板（如某包想加一个状态按钮）不再修改 `shell-menu-bar` 包代码，注册进公共扩展契约即可；现有硬编码残留（LogoMenu / StacksPopup）迁移完毕。

验收标准：以上 4 条达成 + 门禁全绿 + 内核单测覆盖（泵线程回归 / 定位纯几何）+ 端到端场景走查通过（见 §13）。

## 2. Current Behaviour

### 2.1 P/Invoke 全局散落（重复簇①，Native 收口的依据）

- 10+ 模块各自建 Native 目录：[verified] `packages/shell/shell-status/Native/`（18 个文件：AudioInterop/BatteryInterop/ImeInterop/KeyboardLayoutInterop/MemoryInterop/NetworkInterop/WlanCoreNative/NativeLoader/InteropGuard/TsfInputProcessor 等）、`packages/shell/shell-window-tracker/Native/`、`packages/shell/shell-taskbar/Native/`。
- `shell-core` 作为公共层**无 Native 目录**：[verified] 仅 Animation/Surface/Vibrancy/Windowing 四个封装 + `Internal/ShellDisposable.cs`。
- 全库 P/Invoke 重复声明 ~90 处 [verified：分析脚本 `Temp/dup_extra.py`，报告第五章]：`EnumWindows`/`GetClassName` 各约 10 处重复声明；`SetWinEventHook` 3 份声明（shell-status/WinEventForegroundPump.cs:134、shell-window-tracker/Native/WinEventHook.cs:34、shell-taskbar/Native/WinEventHook.cs:116）。
- 弹窗基类自带互操作样板：[verified] `shell-menu-bar/Windows/MenuBarPopupWindow.cs:41-77` 内联 WH_MOUSE_LL 全套（`SetWindowsHookEx`/`UnhookWindowsHookEx`/`CallNextHookEx`/`GetModuleHandle` + `POINT`/`MSLLHOOKSTRUCT`/`RECT` 结构体）——**每个继承它的弹窗都携带一份**（9 个 PopupWindow 子类 [verified]：Wifi/Theme/Stacks/Search/Calendar/CalendarDay/Power/Bluetooth/Ime）。
- 文件级高相似 9 对 [verified]：`poc/system-status-poc/Native` 整批复制进 `shell-status`（4 对文件 Jaccard 0.68~0.85）。

### 2.2 WinEventHook 三份实现（公共缺失 + 性能风险交集）

| 实现 | 位置 | 泵线程纪律（7435） |
|---|---|---|
| 变体 A（正确范例） | `shell-status/Services/WinEventForegroundPump.cs` | ✅ 专用 STA 线程 + 注册在泵线程内 + 字段强引用 + finally Unhook [verified] |
| 变体 B（反例） | `shell-taskbar/Native/WinEventHook.cs` + `Services/TaskbarAppearanceEngine.cs` | ❌ 无专用泵线程，注册线程消息泵未获保障 → 回调可能永不触发（7435 文档已判定为反例教材）[verified] |
| 变体 C | `shell-window-tracker/Native/WinEventHook.cs` | ⚠️ 字段委托持有 ✓，注册线程是否带泵取决于调用方 [verified] |

- 性能关联 [verified：`Temp/perf_scan.py`，报告第七章]：`TaskbarAppearanceEngine` 挂 3 个事件（0x8000 / 0x0003 / 0x8008），回调**无防抖**直接 `FindWindow` + `SetWindowCompositionAttribute` 刷新系统任务栏（跨进程影响 Explorer）。7435 文档同时记录该实现「回调经 ThreadPool 派发」注释与实际同步调用不符。**arch-violation 已完成的性能 Delta（C5 UI 阻塞、C1 Provide O(N)）均未触碰该引擎**——钩子机制修复由本计划 P0-1 承接（§3.2）。
- 反例：8 个 Monitor 轮询间隔设计良好（Ime/Mic/Volume 500ms、Cpu/Memory/Network 1s、Battery 3s）[verified]——不属本计划 scope。

### 2.3 弹窗定位四处独立实现（重复簇②，弹窗体系依据）

| 实现 | 位置 | 能力 |
|---|---|---|
| `PopupAnchor.Compute`（较完善） | `shell-menu-bar/Contracts/PopupAnchor.cs:30`（Visual 重载）、`:51`（Point 重载） | 物理像素→逻辑单位换算、锚点所在显示器工作区、clamp 回钳、edge margin [verified] |
| `StartMenuPopup.BelowOf/ShowBelow` | `shell-start-menu/Services/StartMenuPopup.cs:20,49` | 元素下方定位 [verified] |
| `DockMenuPopup.AtCursor` | `shell-dock/Services/DockMenuPopup.cs:90` | 光标处定位 [verified] |
| `DesktopMenuPopup.Show` | `shell-desktop/Services/DesktopMenuPopup.cs:29` | 屏幕点定位 [verified] |

四处几何逻辑近似（锚点 + 边界钳制）但各写一套，无公共定位服务；均未实现 601 纪律的「ABM_GETTASKBARPOS 双路径 + 翻转」（MenuBar 顶部条带场景暂无任务栏贴靠需求，但 Dock/StartMenu 未来贴任务栏时需要）。

### 2.4 面板注册机制半扩展化（扩展性立项依据）

- 扩展点**已存在**：[verified] `shell-menu-bar/Contracts/IMenuBarExtension.cs:16`——`Id` / `GetVisual()` / `OpenPopup(Point anchorScreenTopLeft)` / `ClosePopup()`，设计注释明确「每个右区按钮 = 一个独立插件实现」。
- 实现类 5 个，**全部 internal 于 shell-menu-bar**：[verified] `Services/MenuBarExtensions.cs`（ImeMenuBarExtension:22 / CpuMenuBarExtension:148 / CalendarMenuBarExtension:207 / ControlCenterMenuBarExtension:267）+ `Services/StatusBarMenuBarExtension.cs:22`（**12 个弹窗** ShowPopup 门控：ime/power/network/memory/cpu/microphone/sound/wifi/bluetooth/theme/calendar/search + control-center）。
- `Services/ControlCenterFeatureCatalog.cs:62-100`：控制中心功能目录（wifi/bt/power/theme/audio 5 项）[verified]。
- **硬编码残留**：[verified] `shell-menu-bar/Windows/MenuBarLeftZone.cs:184`（LogoMenuWindow）、`:240/:281`（StacksPopupWindow）直接 new；`Windows/CalendarPopupWindow.cs:388`（CalendarDayPopupWindow）、`Windows/WifiPopupWindow.cs:444`（WifiPasswordWindow）为窗口内二级弹窗。
- 窗口文件 27 个 [verified]：`shell-menu-bar/Windows/` 下 27 个 .cs（约 10 个 PopupWindow + 6 个 PanelWindow + 基类 + 左区/右区/工具控件）。
- 跨包注册**不可能**：接口与实现均 internal 于 menu-bar 包内；`shell-plugin-sdk/IShellPluginWindow.cs`（插件窗口契约：Content/Config/OnThemeChanged + PluginWindowConfig 白名单 [verified]）与 IMenuBarExtension 是两套并存轨道，关系未澄清。

### 2.5 其他重复（本计划 §12 deferred 依据，不扩 scope）

- NullVibrancy 4 处 [verified]：`QuickNotePlugin.cs:162` / `DesktopPlugin.cs:675` / `MenuBarPlugin.cs:167` / `PlaygroundPreviewFactory.cs:203`（NullVibrancyForPreview）。
- 行级大段自我复制 96 块（≥8 行连续相同）[verified：`Temp/dup_self.py`]：集中在 `WifiEnumerator.cs`（987 行，当前工作区实测）、`DockWindow.xaml.cs`（2888 行）、`Win7Layout.cs`、`BluetoothPopupWindow.cs` 等巨型文件。
- 跨文件块体方法重复 73 组（其中 21 对 = 7 个设置页 Section 的 TitleBlock 构建两两组合）；同文件方法级重复 0 对（修正后真实值）[verified：`Temp/dup_cross2.py` / `dup_self2.py`]。
- 弹窗定位逻辑（§2.3）与 Native 目录（§2.1）为**方法级重复的主要真实来源**。

## 3. Relevant Architecture

### 3.1 包分层与内核架构（ADR-001 §2.3 + ADR-002 D1 冻结面）

```
kernel/*     内核本体：kernel / kernel-loader / kernel-hmr / kernel-timer
shell/*      外壳能力（全部是插件）：shell-core（公共基础设施）/ shell-settings / shell-dock / 其余 shell-* 包
host/        薄宿主 exe：装配内核 + 加载插件 + 启动自检（**已接线**：Bootstrap.cs:99-101 经 LoaderService 按 host/cordis.yml 声明式装配 20 插件，违规4 修复后 [verified]）
```

- **一切皆插件**：loader、HMR、logger、ShellBar、Taskbar 全走同一 `IContext.Plugin()` 通道（ADR-001 §2.3）[verified，arch-violation 计划 §3.1]。
- **shell-core 定位**：公共基础设施包（`BetterDesktop.Shell.Core`），现有四个封装域 Animation/Surface/Vibrancy/Windowing + `ShellCorePlugin.cs` + `ShellEvents.cs`（违规1 修复新建的事件名常量类：`SettingsChanged="shell.settings/changed"` / `AppearanceChanged="shell.appearance/changed"`，跨程序集通信唯一通道 [verified]）；Surface 域含 `ShellWindow`（弹窗基类的祖先，`shell-core/Surface/ShellWindow.cs`）与 `PluginHostWindow`（插件外壳托管）[verified]。
- **M7 依赖单向 / 禁复制**：`packages/shell/shell-menu-bar` 引用 11 个模块（分析报告：MenuBar 是全库引用最多的包）[verified：csproj 引用扫描]；10 个模块不引 shell-core 却自建 Native [verified：csproj 引用扫描]。
- **M10 运行时健康**：日志单管道、禁吞异常、禁 Console.WriteLine（本计划不触碰日志，仅约束 Native 层错误处理遵循同一纪律）[verified：arch-violation 计划 §3.3]。

### 3.2 已有计划的衔接（arch-violation 已完成实施）

`docs/plans/2026-09-07-arch-violation-fix-and-performance.md` **已于本计划修订前完成落地**（用户完成总结 + 工作区实测验证）：

**已完成的 8 项违规**（本计划的现状前提）：
- 违规1 跨程序集裸 event → ShellEvents 常量类 + IEventBus.EmitAsync + 14 订阅点迁移 + verify-no-cross-assembly-event 门禁 PASS [verified]
- 违规2 DiagnosticLog → host/FileLogSink.cs 薄包装路由（违规三要素消除；**完整删除遗留 270+ 引用 → 本计划 §12 D13**）
- 违规3 HMR 缺 ADR → ADR-003.md（含 **D3 TFM 版本化追认**：net8.0-windows10.0.19041.0 文档一致）
- 违规4 cordis.yml 未接线 → host/cordis.yml（20 插件保序）+ LoaderService 全 async 化 + Pending 检测 [verified]
- 违规5-8（README / TFM 文档 / **Taskbar→shell-taskbar 改名** / host 显式引用）→ 全部落地 [verified：Taskbar 目录已不存在，shell-taskbar 存在]

**已完成的性能工作流**（与 §2.2 钩子问题的关系）：
- 基线：内核层 12 项微基准 + 4 项 shell 指标实验合同；诊断 6 候选裁决（C1 NotifyDependents O(N) CONFIRMED、C2 EventBus Snapshot 低影响、C3 ParallelAsync 高方差、C8 插件串行 REJECTED、C5 Thread.Sleep UI 阻塞 CONFIRMED、C4/C6/C7 EVIDENCE_REQUIRED）
- 优化 2 Delta ACCEPTED：C5（MenuManagerSection.RestartExplorer / TwoStepButton → async，UI ping 528ms→4.7ms）、C1（CordisContext._injectCache 依赖缓存，20 插件 53%、50 插件 57% 改善）
- **未包含 TaskbarAppearanceEngine**：其钩子机制（变体 B 反例）与防抖均未处理 → **本计划 P0-1 承接机制修复（§6 P0-1/1.2），防抖进 §12 D9**

**遗留项归属**：DiagnosticLog 完整删除（D13）、C4/C7 完整 shell 测量（D14）、端到端 shell 手动验证（§13 D1 注）——均不阻塞本计划，但实施时须与 245 项未提交改动共存。

### 3.3 技术力文档关联（扫地僧横向通览）

- 拆析-cairoshell：双轨扩展系统（711 实证来源）+ MEF 插件 DI 加载（709）+ 多屏 AppBar（1402）——本项目的 IMenuBarExtension/PopupAnchor 即该范式的本项目落地 [verified]。
- 拆析-EarTrumpet：自制无边框 Flyout 任务栏定位（601 实证来源）+ 自绘托盘（1407）——弹窗定位服务的参照。
- 74 域纪律文档族（7434/7435/7437/7438/7414）——Native 收口的「正确性不变量」来源。

## 4. Technical-Knowledge Findings

### 4.1 命中的机制文档（direct）

| 文档 | 定位 | 对本计划的作用 |
|---|---|---|
| 711-extension-system（L2） | 双轨扩展 IShellExtension / IMenuBarExtension\<T\>、容错装配、登记配对、设置驱动重建 | P0-3 面板注册机制的**验收标准与生死线**（逐个 try/catch、Stop 顺序、OnSourceInitialized 后装载） |
| 3603-菜单栏插入按钮弹窗模式（L1） | 菜单栏加按钮+弹窗标准做法：复用 MenuBarPopupWindow 基类 + SetThemeBinding 令牌 + M10 降级 | P0-2 弹窗体系的模式依据（注意 L1 待对照实时源码，已对照确认 MenuBarPopupWindow 真实存在） |
| 601-任务栏定位算法（L2 复合） | ABM_GETTASKBARPOS 双路径 + 翻转 + clamp + 锚 Shell_TrayWnd 不锚 TrayNotify | P0-2 定位服务的**增强约束**（当前 PopupAnchor 无 ABM 路径） |
| 602-无焦点窗口（L2） | NOACTIVATE/TOOLWINDOW 弹出不抢焦点 | P0-2 基类契约（MenuBarPopupWindow 已用 ShowActivated=false，需对齐文档） |
| 603-失焦与外部点击关闭（L2） | 全局钩子判断外部点击 + Esc/失焦关闭 | P0-2 基类已内建 WH_MOUSE_LL，上提时按文档核对钩子生命周期 |
| 604-亚克力背景分支（L2） | Win10/Win11 亚克力 API 分支 + 回落 | P0-2 基类外观（上提不改行为） |
| 607-Flyout 状态机（L2） | 6 态生命周期 + Cloak 防白闪 + 300ms 去抖 + Opening 态忽略失焦 | P0-2 增强参考（MenuBar 弹窗暂无该状态机，进 §12 增强） |
| 7434-dwm-thumbnail-declaration-discipline（L2） | DwmIsCompositionEnabled 必须 out bool + 四函数签名 + 结构对齐 | P0-1 Native 声明纪律（DWM 域） |
| 7435-winevent-outofcontext-message-pump（L2，本项目实证） | OUTOFCONTEXT 回调必须送达注册线程消息队列；STA 泵线程三件套；字段强引用；Unhook 在注册线程 | P0-1 WinEventPump 的**生死线**（本项目 shell-status 为变体 A 正确范例，shell-taskbar 为反例） |
| 7437-win32-interop-error-discipline（L2） | 返回值检查 + 失败诊断 + COM 释放配对 + 分配释放对应 | P0-1 Native 层错误处理纪律 |
| 7438-event-subscription-unsubscribe-discipline（L2） | += 必须 -=；WeakEventManager；Dispose 幂等 | P0-1 钩子/事件收口的退订纪律 |
| 1402-appbar-window-multiscreen（L2） | AppBar 多屏窗口生命周期 | P0-2 定位服务多屏参考（关联） |
| 1407-tray-icon-selfdraw（L2） | Shell_NotifyIconW v4 全套 | Native 收口的用户（Shell32 域相关） |
| 1408-single-instance-mutex（L2） | Mutex 单实例 | Native 收口的用户（Kernel32 域相关） |
| 3101-global-hotkey（L2） | RegisterHotKey 组合键 | Native 收口的用户（User32 域相关） |
| 709-mef-dependency-load（L2） | MEF 插件 dll 加载 + IDependencyRegistrant 桥进 MS.DI | P0-3 扩展装配参照（关联） |
| 710-command-system（L2） | DI 命令注册 + IsAvailable 门控 + 审计 | P0-3 扩展门控参照（关联） |

### 4.2 命中的机制文档（related / 拆析）

- 64-bluetooth-radio-state、69-wlanapi-wifi-status：Monitor 轮询/互操作文档（§12 P1 MonitorBase 依据）。
- 63-dwm-live-thumbnail、7432-fullscreen-foreground-detection：钩子回调业务参考。
- 拆析-cairoshell-最初开源版：711/709/1402 的源码实证来源。

### 4.3 未命中（新建）

- **统一 Win32 Native 平台层**（P/Invoke 收口目录）：索引按「Win32 / P/Invoke / Native / 平台层」检索无现成文档；74 域纪律文档族是「纪律」不是「统一封装」。→ 本计划 P0-1 即该资产的创建，完成后按 tech-knowledge-accumulation 沉淀为 11-原生桥接域新文档（§12 记录回写项）。
- **公共弹窗基类上提**：601-607 是 Flyout 域（贴任务栏场景），MenuBarPopupWindow 是「锚按钮弹窗」场景——模式同源但无合并文档 → 以工程代码（MenuBarPopupWindow）为权威，3603 L1 待升级。

### 4.4 文档新鲜度判定

- 7435/601/602/603/604/607/711 均有真实源码实证（[verified] 标注），与当前源码一致（7435 甚至直接以本项目为实证）→ strict 通过。
- 3603 标注 L1「待对照实时源码校验」→ 已对照（MenuBarPopupWindow / SetThemeBinding 均在）→ 本计划按源码权威引用，并在 §12 记录「升级 3603 为 L2」回写项。

## 5. Constraint Findings（机制文档的验收标准与生死线）

### 5.1 P0-1 Native 收口约束

| 约束 | 来源 | 内容 |
|---|---|---|
| 泵线程三件套（生死线） | 7435【必须遵守】1-4 | 专用 STA Thread + 注册必须在泵线程内 + 回调 delegate 字段强引用 + Unhook 在注册线程（PostThreadMessage(WM_QUIT) 退出）；**禁止**在线程池/async 线程注册 OUTOFCONTEXT 钩子；只监听需要的事件（前台追踪只挂 0x0003） |
| P/Invoke 声明纪律 | 7434 | `DwmIsCompositionEnabled` 必须 `out bool`（PreserveSig=false 无 out 恒 false）；四函数签名；结构体对齐（Pack=0 默认/LayoutKind.Sequential） |
| 互操作错误纪律 | 7437 | 返回值检查 + 失败诊断（SetLastError/GetLastError）；COM 接口释放配对（ReleaseComObject）；Advise/Unadvise 成对；LocalFree/CoTaskMemFree 分配释放对应 |
| 退订配对 | 7438 | 钩子/事件 += 必须 -=；Dispose 幂等；长持短泄漏判定 |
| 日志纪律 | M10（约束层） | Native 层失败不吞异常、不 Console.WriteLine，走内核 IKernelLogger 或返回错误码 |
| 构建基线 | — | TFM `net8.0-windows10.0.19041.0`（**ADR-003 D3 已追认，coding-standards 文档一致** [verified]）；`dotnet build -f net8.0-windows10.0.19041.0 -c Debug`（host csproj:4 [verified]） |

### 5.2 P0-2 弹窗体系约束

| 约束 | 来源 | 内容 |
|---|---|---|
| 复用优先（红线） | 3603 红线 1-2 | 先查既有服务；弹窗用既有基类，不另起炉灶；令牌绑定走主题系统，不硬编码颜色 |
| 定位双路径（增强） | 601【必须遵守】 | ABM_GETTASKBARPOS 优先、失败窗口矩形推断边；翻转 + clamp 到工作区；auto-hide 补偿；监听 WM_SETTINGCHANGE/WM_DISPLAYCHANGE 重算 |
| 禁止锚 TrayNotify（红线） | 601【已知坑 1-2】 | 锚 Shell_TrayWnd（副屏 Shell_SecondaryTrayWnd），不锚 TrayNotifyWnd 链；不只信 Screen.GetWorkingArea |
| 无焦点（红线） | 602 | 弹出不抢焦点（NOACTIVATE 语义；MenuBarPopupWindow 已用 ShowActivated=false [verified]） |
| 失焦关闭（红线） | 603 | 外部点击/失焦关闭；Esc；钩子生命周期成对（现 WH_MOUSE_LL 在基类内 [verified]） |
| 视觉降级 | 604 | Win10/Win11 亚克力分支 + 回落（VibrancyService/DwmHelper 已封装于 shell-core [verified]，上提时沿用） |
| 单位纪律（红线） | PopupAnchor.cs:4-6 [verified] | 对外一律 WPF 逻辑单位；PointToScreen 物理像素 ↔ Window.Left/Top 逻辑单位不可直接相加；取「锚点所在显示器」非主屏 |

### 5.3 P0-3 面板注册机制约束

| 约束 | 来源 | 内容 |
|---|---|---|
| 逐个 try/catch（红线） | 711【必须遵守】1-3 | 单个扩展失败只记日志跳过，禁止整体失败拖垮宿主；StartControl/OpenPopup 返回 null 允许；异常吞掉记日志含 TargetSite 模块定位 |
| Stop 顺序（红线） | 711【必须遵守】2 | 先移除 UI 子项 → 逐个 Stop → 最后清登记表；设置驱动的重建 Stop→Setup 成对 |
| 装载时机（红线） | 711【已知坑 1】 | 禁止 OnSourceInitialized 之前装载扩展控件（宿主 hwnd 未就绪） |
| 登记配对（红线） | 711【已知坑 2】 | 不清登记字典 → 重复装载；扩展只见宿主 IMenuBar 面，禁止直接操作其它扩展控件 |
| 扩展点契约 | IMenuBarExtension.cs [verified] | Id / GetVisual / OpenPopup(anchor) / ClosePopup；重复调用只置前；数据走 Inject 服务 + Binding，禁止硬编码假数据 |
| 克制项 | skill 工具型侧重 | UI 美化、无关花哨、过早大规模架构**不得进强制 scope**（进 §12） |

## 6. Proposed Changes

> 全部命名符号已 `source_verified`（见 §2/§3/§5 行号）。

### P0-1：shell-core 建 Win32 Native 平台层（唯一权威互操作层）

| # | 文件/符号 | 职责 | 备注 |
|---|---|---|---|
| 1.1 | 新 `packages/shell/shell-core/Native/NativeMethods.cs` | User32 / Kernel32 / Shell32 / Ole32 / Dwmapi / Wlanapi 分区静态类：全部重复声明的 DllImport + 常量 + 结构体（EnumWindows/GetClassName/SetWindowsHookEx/SetWinEventHook/ABM/…） | 迁移来源：shell-status/Native/18 文件、shell-window-tracker/Native/、shell-taskbar/Native/、MenuBarPopupWindow.cs:41-77、DockWindow.xaml.cs 等；**迁移不改签名/不重命名**（行为等价优先） |
| 1.2 | 新 `packages/shell/shell-core/Native/WinEventPump.cs` | 单例窗口事件泵（7435 变体 A 范式）：专用 STA 泵线程、注册在泵线程内、字段强引用回调、finally Unhook + PostThreadMessage(WM_QUIT)；支持按事件区间注册多个订阅方 | 替代 3 份实现；**TaskbarAppearanceEngine 的钩子注册点改接本泵**（修复变体 B 回调不触发，7435 反例消除） |
| 1.3 | 新 `packages/shell/shell-core/Native/MouseHook.cs` | WH_MOUSE_LL 低级鼠标钩子封装（字段强引用 + 成对 Unhook + 回调委托） | 替代 MenuBarPopupWindow.cs:41-77 内联声明（P0-2 基类上提后由基类统一调用） |
| 1.4 | 新 `packages/shell/shell-core/Native/MessagePump.cs` | GetMessage/TranslateMessage/DispatchMessage 三件套（当前 3 处重复声明）| 供 WinEventPump 及未来泵需求 |
| 1.5 | 改 `packages/shell/shell-core/BetterDesktop.Shell.Core.csproj` | 保持 TFM `net8.0-windows10.0.19041.0`，无新依赖 | 结构变更，非契约变更 |
| 1.6 | 改 10+ 模块的 Native 目录 | shell-status/Native 等删除重复声明，保留业务封装（如 CpuCoreNative 的读取逻辑），DllImport/结构体改引 shell-core | 迁移后各模块 csproj 增 ProjectReference shell-core；**逐模块验证**（§7 Phase A 分步） |
| 1.7 | 新门禁 `scripts/verify-native-convergence.ps1` | 机器校验：`packages/` 下（除 shell-core/Native 与 poc 目录）禁止出现 `[DllImport` 与 `struct.*LayoutKind` 新声明；已有残留计数基线可收敛 | 注册进 scripts/run-gates.ps1 门禁表（verify-gate-registry.ps1 校验） |

### P0-2：弹窗体系上提公共层

| # | 文件/符号 | 职责 | 备注 |
|---|---|---|---|
| 2.1 | 新 `packages/shell/shell-core/Windows/PopupWindowBase.cs` | 由 `MenuBarPopupWindow` 上提：继承 ShellWindow；主题令牌外观；失焦自动收起（WH_MOUSE_LL 改接 P0-1 MouseHook）；ShowActivated=false 无焦点；防二次 Show；子类 BuildContent() | 上提时**依赖参数化**：MenuBarStripHeight/MenuBarMetrics 依赖改为虚属性/构造参数（默认值保持现有行为） |
| 2.2 | 改 `shell-menu-bar/Windows/MenuBarPopupWindow.cs` | 改为继承 PopupWindowBase 的薄壳（保留 MenuBar 特定行为）或删除（子类直改继承 PopupWindowBase） | 9 个子类（Wifi/Theme/Stacks/Search/Calendar/CalendarDay/Power/Bluetooth/Ime）继承链迁移；保留 `internal` 可见性变更评估 |
| 2.3 | 新 `packages/shell/shell-core/Windows/PopupPositioningService.cs` | 由 PopupAnchor.Compute 上提 + 601 增强：Compute(anchorVisual/物理点, size) → Point；**新增** ABM_GETTASKBARPOS 双路径（贴任务栏场景）+ 翻转；单位纪律（逻辑单位）保留 | StartMenuPopup/DockMenuPopup/DesktopMenuPopup 改调本服务（消除 4 处重复几何）；多屏工作区逻辑（MenuBarScreen）一并参数化上提 |
| 2.4 | 改 `shell-menu-bar/Contracts/PopupAnchor.cs` | 删除（逻辑并入 2.3）或保留为转发壳 | 迁移后全库仅 1 份定位实现 |

### P0-3：面板注册机制贯彻

| # | 文件/符号 | 职责 | 备注 |
|---|---|---|---|
| 3.1 | 改 `shell-menu-bar/Contracts/IMenuBarExtension.cs` | 保持契约形态（Id/GetVisual/OpenPopup/ClosePopup），**上提到公共契约位置**（shell-core/Contracts 或 shell-plugin-sdk，见 §12 OQ1），去掉跨包限制 | 现有 5 个实现类改引公共接口；其余包可注册自己的菜单栏面板 |
| 3.2 | 新 `packages/shell/shell-core/Contracts/IMenuBarExtensionRegistry.cs`（或并入现有装配点） | 注册表：Register(IMenuBarExtension) / 按 Id 查询；装配遵循 711 纪律（逐个 try/catch、登记配对、Stop 顺序） | 由 MenuBarWindow 在 OnSourceInitialized 后消费（711 红线 3） |
| 3.3 | 改 `shell-menu-bar/Windows/MenuBarLeftZone.cs` | LogoMenuWindow（:184）与 StacksPopupWindow（:240/:281）迁移为 IMenuBarExtension 实现 | 消除硬编码残留；迁移后 MenuBarLeftZone 不再 new 弹窗 |
| 3.4 | 改 `shell-menu-bar/Services/StatusBarMenuBarExtension.cs` / `MenuBarExtensions.cs` | 现有 12 弹窗 + 4 扩展改造为走公共接口 + 注册表（门控逻辑保留） | 行为不变优先；重构装配方式 |
| 3.5 | 改 `shell-menu-bar/Windows/MenuBarWindow.cs` | 右区装配改走注册表；扩展失败不拖垮（711 红线 1） | MenuBarWindow 是装配消费端 |

### 明确不做（强制 scope 边界）

- 不清理 96 块行级重复 / 73 组方法重复 / 9 对文件重复（§12 Deferred——行级重复清理风险高收益低，且会与 245 项未提交改动冲突）。
- 不统一 NullVibrancy / SettingsSectionBase / MonitorBase / ConversionEngineBase（P1，§12 Deferred）。
- 不做 TaskbarAppearanceEngine 防抖 / 性能补丁（arch-violation 性能工作流已完成且未含防抖；本计划 P0-1 只修钩子机制，防抖归属 §12 D9）。
- 不迁移 ISettingsService（44 文件使用者，位置漂移问题 §12 Deferred）。

## 7. Implementation Sequence

按依赖排序、每步独立可交付（任一步停下树仍一致）：

### Phase A：Native 收口（P0-1，先行——其余两项依赖其互操作层）

1. **A1 建 NativeMethods 骨架**：shell-core/Native/NativeMethods.cs 建 User32 分区（EnumWindows/GetClassName/SetWindowsHookEx/SetWinEventHook/UnhookWinEvent/PostThreadMessage/GetMessage/TranslateMessage/DispatchMessage/FindWindow/SetWindowCompositionAttribute/SHAppBarMessage 等），从 MenuBarPopupWindow.cs:41-77 与 shell-status/Native 迁移声明；**迁移不改签名**。→ `dotnet build`（新增文件不破坏现有）。
2. **A2 MessagePump + WinEventPump**：按 7435 变体 A 实现；WinEventPump 支持多订阅方事件区间注册。→ 单测（泵线程回归）。
3. **A3 shell-window-tracker 迁移**：WinEventHook.cs 调用点改接 WinEventPump（window-tracker 只挂 0x0003 前台 [inferred：7435 变体 C 用途]）。→ `dotnet build shell-window-tracker`。
4. **A4 shell-taskbar 迁移**：TaskbarAppearanceEngine 的 3 事件钩子改接 WinEventPump（**修复变体 B**）；行为变化显式记录（回调从「可能永不触发」变为「会触发」→ 需要真机确认无副作用风暴，见 §9.3）。→ 单测 + 真机验证。
5. **A5 shell-status 迁移**：WinEventForegroundPump 业务保留，声明改引 shell-core/Native（变体 A 实现本身可整体上提为 WinEventPump 本体，shell-status 只留订阅逻辑）。
6. **A6 其余模块 Native 声明迁移**（Audio/Battery/Ime/KeyboardLayout/Memory/Network/Wlan/Display 等）：逐模块改 ProjectReference shell-core + 删重复声明。每模块 `dotnet build`。
7. **A7 MouseHook 封装 + MenuBarPopupWindow 钩子改接**（为 P0-2 铺路）。
8. **A8 门禁**：verify-native-convergence.ps1 落库注册。

### Phase B：弹窗体系上提（P0-2）

9. **B1 PopupPositioningService**：PopupAnchor.ComputeCore 上提 + 601 双路径/翻转（先纯几何单测；ABM 路径作为可选注入，MenuBar 场景不启用以保行为不变）。
10. **B2 PopupWindowBase**：MenuBarPopupWindow 上提 + 依赖参数化；MenuBarPopupWindow 变薄壳；9 子类继承链迁移。→ shell-menu-bar-tests 全绿。
11. **B3 三处弹窗定位改调公共服务**：StartMenuPopup / DockMenuPopup / DesktopMenuPopup 消除重复几何。→ 各包 build + 真机弹出验证。

### Phase C：面板注册机制（P0-3）

12. **C1 IMenuBarExtension 公共化**：接口上提（位置见 §12 OQ1 裁决）；现有 5 实现类改引。
13. **C2 注册表**：IMenuBarExtensionRegistry 实现（711 纪律）；MenuBarWindow 装配改走注册表。
14. **C3 硬编码迁移**：LogoMenu / StacksPopup → 扩展实现；MenuBarLeftZone 不再 new 弹窗。
15. **C4 端到端走查 + 门禁全绿**（§13 D1）。

> 改动受指纹/基线保护的输出（若有）只在最后一步重新生成一次。

## 8. Test Strategy

### 8.1 新增测试

- `packages/shell/shell-core-tests/`（csproj 已存在 [verified]）：
  - `NativeMethodsTests`：签名与结构体对齐契约测试（7434 纪律：DwmIsCompositionEnabled out bool；结构体 Marshal.SizeOf 断言）。
  - `WinEventPumpTests`：泵线程回归（7435）——注册在无泵线程 → 回调不触发；专用泵线程 → 触发；Unhook 后不再收到；Dispose 后线程退出无泄漏。
  - `PopupPositioningServiceTests`：601 纯几何——任务栏四边方向正确；近角翻转 + clamp 全可见；auto-hide 补偿；单位纪律（逻辑单位断言）。
- `packages/shell/shell-menu-bar-tests/`（csproj 已存在 [verified]）：
  - `MenuBarExtensionRegistryTests`：711——2 扩展注册 → 装配 2 → Stop 全清；第 1 个 OpenPopup 抛异常 → 第 2 个仍工作；返回 null 跳过。

### 8.2 更新测试

- 现有引用被迁移符号的测试（shell-status-tests / shell-window-tracker-tests [verified 存在]）：改引 shell-core/Native 后保持全绿。
- 当前全库基线：~447 测试通过 / 1 预存失败（Convert.Tests PDF/Poppler 矩阵登记，与任何一方无关）；Kernel 42/42、Loader 7/7、ContextMenu 98/98 [verified：arch-violation 完成总结]——本计划实施后此基线不恶化。

### 8.3 边界与失败路径

- WinEventPump 注册失败（hook==0）：记 GetLastError，不静默吞掉（7435 日志纪律），返回失败状态。
- 弹窗基类 Dispose 幂等；钩子 Unhook 成对（7438）。
- 扩展装配：单点异常不拖垮宿主（711）。

### 8.4 验证命令（真实存在 [verified]）

```
dotnet build BetterDesktop.slnx -f net8.0-windows10.0.19041.0 -c Debug
dotnet test packages/shell/shell-core-tests/BetterDesktop.Shell.Core.Tests.csproj -f net8.0-windows10.0.19041.0
dotnet test packages/shell/shell-menu-bar-tests/BetterDesktop.Shell.MenuBar.Tests.csproj -f net8.0-windows10.0.19041.0
powershell -File scripts/run-gates.ps1
```

## 9. Risk and Impact Analysis

### 9.1 高风险符号

| 符号 | 风险 | 缓解 |
|---|---|---|
| `TaskbarAppearanceEngine`（shell-taskbar） | 钩子改接 WinEventPump 后回调从「可能不触发」变「会触发」→ 3 事件高频回调直刷系统任务栏（FindWindow+SWCA），真机可能出现此前未暴露的行为 | Phase A4 单独真机验证；回调侧加**日志埋点**而非防抖（防抖属性能补丁，进 §12）；若风暴严重 → 暂停 A4 并回报，由用户裁决是否提前开性能计划 |
| `MenuBarPopupWindow` 继承链 | 上提基类改动 9 个弹窗子类，主题令牌/失焦行为回归风险 | B2 分步：先加薄壳转继，全部 shell-menu-bar-tests + 真机弹出验证后删旧类 |
| `IMenuBarExtension` 位置变更 | 公共化后 5 个实现类 + MenuBarWindow 装配点引用变更 | 保持契约签名不变，仅移动位置；编译期强制验证 |
| `shell-status/Native` 18 文件迁移 | 迁移声明可能引入行为差异（CharSet/MarshalAs 丢失） | 逐文件对照迁移；A6 每模块 build + 真机 Monitor 功能验证 |

### 9.2 下游消费者（d=1 直接依赖）

- shell-core 新增 Native：影响全部引用 shell-core 的包（+10 个原本不引的模块变为引用，M7 方向符合——shell-core 为公共层，依赖单向指向基础设施，不构成环）。
- PopupWindowBase 上提：shell-menu-bar 9 个弹窗子类；未来 shell-dock/shell-start-menu/shell-context-menu 弹窗可复用（行为不变）。
- IMenuBarExtension 公共化：5 个实现类 + MenuBarWindow 右区装配。

### 9.3 兼容性风险

- **对外契约**：本计划不修改任何公开 API 的参数/返回/错误码（P/Invoke 迁移不改签名；IMenuBarExtension 契约形态不变仅位置变更）。`internal→public` 的可见性扩大不破坏现有调用方。
- **行为变化唯一例外**：TaskbarAppearanceEngine 钩子（变体 B→A），是修复而非回归，但需真机确认（§9.1）。
- 与 245 项未提交改动共存：只新增文件 + 改引用点，不覆盖/回滚既有文件内容（迁移删除的重复声明文件需逐一确认无他人正在修改）。

### 9.4 并发/线程风险

- WinEventPump 泵线程回调在泵线程执行，耗时逻辑必须回抛业务线程（7435 纪律）；回调异常 try/catch（泵线程抛异常杀死消息泵 → 后续事件全丢）。
- MouseHook 回调同理；Dispose 与回调竞态用 _disposed 标志 + 幂等（7438）。

### 9.5 可观测性要求

- Native 层失败路径记内核日志（不吞异常）；WinEventPump 注册成败埋点；扩展装配 Start/Stop 埋点（711）。

### 9.6 未提交改动风险

- 禁止覆盖/回滚 **245 项未提交改动**（205 项他人改动 + arch-violation 约 40 项实施改动，均已增量编辑未覆盖 [verified：git status 245 项]）；本计划新增文件为主，迁移删除需逐文件 diff 确认。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-core/Native/NativeMethods.cs`（新） | User32/Kernel32/Shell32/Ole32/Dwmapi/Wlanapi 静态类 | P0-1 唯一权威互操作层 |
| `packages/shell/shell-core/Native/WinEventPump.cs`（新） | WinEventPump | 3 份 WinEventHook 收口（7435） |
| `packages/shell/shell-core/Native/MouseHook.cs`（新） | MouseHook | WH_MOUSE_LL 收口 |
| `packages/shell/shell-core/Native/MessagePump.cs`（新） | MessagePump | 消息泵三件套收口 |
| `packages/shell/shell-core/Windows/PopupWindowBase.cs`（新） | PopupWindowBase | P0-2 弹窗基类上提 |
| `packages/shell/shell-core/Windows/PopupPositioningService.cs`（新） | PopupPositioningService | P0-2 定位服务（601 增强） |
| `packages/shell/shell-core/Contracts/IMenuBarExtension.cs`（迁入） | IMenuBarExtension | P0-3 公共化 |
| `packages/shell/shell-core/Contracts/IMenuBarExtensionRegistry.cs`（新） | Registry | P0-3 注册表 |
| `packages/shell/shell-status/Native/*`（18 文件） | 各 Interop | 声明迁出，业务保留 |
| `packages/shell/shell-window-tracker/Native/WinEventHook.cs` | WinEventHook | 删除，改接 WinEventPump |
| `packages/shell/shell-taskbar/Native/WinEventHook.cs` | WinEventHook | 删除，改接 WinEventPump |
| `packages/shell/shell-taskbar/Services/TaskbarAppearanceEngine.cs` | 钩子注册点 | 修复变体 B |
| `packages/shell/shell-menu-bar/Windows/MenuBarPopupWindow.cs` | MenuBarPopupWindow | 变薄壳/删除 |
| `packages/shell/shell-menu-bar/Contracts/PopupAnchor.cs` | PopupAnchor | 删除/转发壳 |
| `packages/shell/shell-menu-bar/Services/MenuBarExtensions.cs` | 4 扩展类 | 改引公共接口 |
| `packages/shell/shell-menu-bar/Services/StatusBarMenuBarExtension.cs` | StatusBarMenuBarExtension | 改引公共接口 |
| `packages/shell/shell-menu-bar/Windows/MenuBarLeftZone.cs` | LogoMenu/StacksPopup 注册点 | 硬编码迁移 |
| `packages/shell/shell-menu-bar/Windows/MenuBarWindow.cs` | 右区装配 | 走注册表 |
| `packages/shell/shell-start-menu/Services/StartMenuPopup.cs` | ShowBelow/BelowOf | 改调公共定位 |
| `packages/shell/shell-dock/Services/DockMenuPopup.cs` | AtCursor | 改调公共定位 |
| `packages/shell/shell-desktop/Services/DesktopMenuPopup.cs` | Show | 改调公共定位 |
| `scripts/verify-native-convergence.ps1`（新） | — | P/Invoke 收敛门禁 |
| `scripts/run-gates.ps1` | 门禁注册表 | 注册新门禁 |

## 11. Reusable Implementation Context

**证据头**：commit `0ecd8416380f83b71353131a71603815eda37df5`（工作区含 245 项未提交改动，行号以当前工作区为准，锚点同 arch-violation 计划）。

**关键 API 速查（实现时直接消费，零重新调研）**：

- `IMenuBarExtension`（`packages/shell/shell-menu-bar/Contracts/IMenuBarExtension.cs:16`）：`string Id` / `FrameworkElement GetVisual()` / `void OpenPopup(Point anchorScreenTopLeft)` / `void ClosePopup()`；重复调用只置前 [verified]。
- `PopupAnchor`（`packages/shell/shell-menu-bar/Contracts/PopupAnchor.cs:14`）：`Compute(Visual anchorVisual, double buttonWidth, Size popupSize, double menuBarHeight)` 与 `Compute(Point anchorPhysicalPoint, double buttonWidth, Size popupSize, double menuBarHeight)` 重载；常量 VerticalGap=4、EdgeMargin=4；依赖 `MenuBarScreen.ToLogical/GetWorkArea/GetScale` 与 `MenuBarMetrics.MenuBarHeight`（同目录）[verified]。
- `MenuBarPopupWindow`（`packages/shell/shell-menu-bar/Windows/MenuBarPopupWindow.cs:31`）：abstract，: ShellWindow；自带 WH_MOUSE_LL（:41-77）；`BuildContent()` 抽象；ShowActivated=false；防二次 Show；MenuBarStripHeight 引用 MenuBarMetrics [verified]。
- 7435 变体 A 正确范式：`packages/shell/shell-status/Services/WinEventForegroundPump.cs`（专用 STA 线程 + 注册在泵线程 + 字段 `_hookProc` + finally Unhook + PostThreadMessage(WM_QUIT)）[verified]。
- 7435 反例：`packages/shell/shell-taskbar/Native/WinEventHook.cs`（无泵线程）+ `Services/TaskbarAppearanceEngine.cs`（3 事件 0x8000/0x0003/0x8008，回调直刷系统任务栏）[verified]。
- 门禁：`scripts/run-gates.ps1` 注册表（verify-gate-registry.ps1 校验，13 脚本全登记 [verified：arch-violation 完成总结]），新增门禁照 verify-no-cross-assembly-event.ps1 模式（含 .Tests.ps1，该门禁已随 arch-violation 落地并 PASS）；verify-native-convergence 需自带基线文件（当前 P/Invoke 残留计数，A1 时记录）。
- 内核日志：`IKernelLogger`（kernel/Contracts）——Native 层失败路径用 `IContext.Logger`，不吞异常（M10）[verified：arch-violation 计划 §3.1]。

## 12. Assumptions and Open Questions

### 12.1 开放问题（须在实施前或实施中裁决）

| # | 问题 | 影响 | 建议 |
|---|---|---|---|
| OQ1 | IMenuBarExtension 上提到 **shell-core/Contracts** 还是 **shell-plugin-sdk**？ | shell-core 是包内公共层；plugin-sdk 是外部插件面。当前 5 个实现全在包内，无外部插件用例 | 本计划默认 shell-core/Contracts（零新依赖、改动最小）；SDK 化进 §可选增强 |
| OQ2 | Win32 平台层放 **shell-core/Native** 还是独立 `shell-native` 包？ | shell-core 已含 Windowing（NativeTaskbarManager）等互操作，放 shell-core 依赖最小；独立包更解耦但新增 csproj | 默认 shell-core/Native（与既有 Windowing 域一致） |
| OQ3 | MenuBarPopupWindow 上提后 `MenuBarMetrics`/`MenuBarScreen` 依赖如何参数化？ | 基类不能依赖 shell-menu-bar 的 Contracts | 虚属性 + 构造注入，默认值保持现有行为；MenuBarScreen 的 ToLogical/GetWorkArea 上提为 PopupPositioningService 内部件 |
| OQ4 | 601 ABM 双路径是否纳入本计划强制 scope？ | MenuBar 顶部条带无任务栏贴靠需求；Dock/StartMenu 场景未来需要 | 本计划只做**纯几何服务 + 可选 ABM 注入点**（接口预留，不实现 ABM 路径）；ABM 实现进 §可选增强 |

### 12.2 假设（[assumed]，须实施时验证）

- `shell-window-tracker` 的 WinEventHook 只挂前台事件 0x0003（7435 变体 C 用途推断）——A3 迁移前先读调用方确认。
- 迁移删除的 Native 声明文件无他人未提交改动——A6 前 git status 逐文件确认。
- `MenuBarWindow` 右区装配在 OnSourceInitialized 之后（711 红线 3 的满足）——C2 前读 MenuBarWindow 确认。

### 12.3 Deferred Follow-ups（不扩 scope，相邻重构；P1 整改项）

| # | 项 | 依据（分析报告章节） | 承接 |
|---|---|---|---|
| D1 | NullVibrancy 4 处统一（shell-core 提供空实现单例） | 公共能力缺失清单 P1 | 本计划完成后另立 |
| D2 | SettingsSectionBase（7 个设置页 Section 的 TitleBlock 等构建方法统一，73 组重复的主要来源） | 自我重复专项（21/73 组） | 另立 |
| D3 | MonitorBase（8 个 Monitor 硬注册统一 + IMonitor 注册表） | 公共能力缺失 P1 / 扩展性 P1 | 另立 |
| D4 | ConversionEngineBase（shell-convert 5 个引擎复制样板） | 重复簇③ | 另立 |
| D5 | StartMenuLayoutBase（开始菜单 4 个布局类 LoadIconAsync） | 重复簇④ | 另立 |
| D6 | 行级重复块 96 块清理（WifiEnumerator 1124 行 / DockWindow.xaml.cs 2888 行等巨型文件拆分） | 自我重复专项 | 另立（风险高，需先拆分文件再消重） |
| D7 | 文件级高相似 9 对（poc/system-status-poc Native 与 shell-status） | 重复簇① | 随 P0-1 迁移自然消解大部分；poc 目录归档另议 |
| D8 | ISettingsService 位置漂移（44 文件使用者 vs shell-settings 包） | 公共能力缺失核心洞察 | 另立（牵动大，须先评估依赖方向） |
| D9 | TaskbarAppearanceEngine 防抖（钩子已由本计划 P0-1 收口修复后，回调高频刷系统任务栏的防护） | 性能风险 P0 | arch-violation 性能工作流已完成（Delta 1/2 均未含本项）→ **无承接者**；本计划 P0-1 收口后按需另立性能计划 |
| D10 | IShellPluginWindow（plugin-sdk）与 IMenuBarExtension 轨道关系澄清/合并 | 扩展性视角 | 本计划 §12 OQ1 裁决后另议 |
| D11 | 升级 3603 为 L2（对照实时源码补全） | 文档新鲜度 | 回写项：按 tech-knowledge-accumulation |
| D12 | 沉淀新功能文档：shell-core/Native 平台层（11-原生桥接域）、PopupWindowBase/PopupPositioningService（06-Flyout 域） | 技术力回写 | 本计划完成后立即回写（AGENTS.md 闭环） |
| D13 | DiagnosticLog **完整删除**（arch-violation 违规2 偏离遗留：270+ 引用遍布 50+ 文件，现为薄包装路由 FileLogSink，违规三要素已消除） | arch-violation 完成总结「遗留」 | 独立任务（全仓引用清理 + 删除静态类） |
| D14 | C4/C7 完整 shell 测量（Dispatcher.Invoke 5 处 + 定时器 ~9 线程池 + ~8 DispatcherTimer 的稳态 CPU 采样） | arch-violation 诊断 EVIDENCE_REQUIRED | 其性能工作流已交付实验合同；待 shell 场景真机测量 |

## 13. Definition of Done

### D1（端到端场景走查 · 必含）

> **手动真机走查**：WPF shell 会接管用户桌面，无法自动化实跑（arch-violation 实施同款限制——仅 smoke-test 确认 4 秒存活）。以下场景须在受控环境手动执行并记录结果。

**场景 A：新面板注册不碰 MenuBar 代码（P0-3）**
1. 在 `shell-status`（或任意非 menu-bar 包）新增一个测试用 IMenuBarExtension 实现（Id="dod-test"），经注册表注册。
2. 启动 BetterDesktop，验证：右区出现该按钮；点击 → 弹窗锚在按钮正下方、不超工作区；点击窗口外 → 自动收起；重复点击 → 只置前不重复开窗。
3. 实现类 OpenPopup 抛异常 → 仅该扩展失败，其余 12 个弹窗按钮仍正常。
4. 通过设置关闭该扩展（若接入设置驱动）→ Stop→Setup 重建无重复控件。
5. 验证 MenuBarLeftZone 已无 LogoMenu/StacksPopup 硬编码（grep `new LogoMenuWindow|new StacksPopupWindow` = 0）。

**场景 B：钩子事件真机送达（P0-1）**
6. 启动 BetterDesktop，打开任务栏外观功能；切换前台窗口 10 次 → shell-taskbar 侧钩子回调每次均触发（日志埋点计数=10，此前变体 B 可能为 0）。
7. 观察任务栏外观引擎行为：无异常风暴（回调频率合理）；退出时钩子全部 Unhook（无泄漏告警）。
8. 窗口追踪功能（shell-window-tracker）：前台切换事件稳定送达 → 停靠/Dock 高亮正确。

**场景 C：弹窗定位统一（P0-2）**
9. 在 100%/125%/150% DPI 与副屏场景：MenuBar 弹窗 / StartMenu 右键菜单 / Dock 右键菜单 / Desktop 右键菜单弹出位置均正确（锚点 + 不越工作区）；移动任务栏/改分辨率后重开位置正确。
10. 验证四处弹窗定位调用同一 PopupPositioningService（grep 引用点 = 4 处 + 0 处内联几何）。

**通过标准**：A1-A5、B6-B8、C9-C10 全部通过。

### D2（内核逻辑单测计划）

- WinEventPump：注册无泵线程 → 不触发；专用泵线程 → 触发；Unhook 后静默；Dispose 幂等 + 线程退出（7435 回归，见 §8.1）。
- PopupPositioningService：四边方向 / 翻转 / clamp / 单位纪律（601 纯几何）。
- MenuBarExtensionRegistry：容错装配（711）。
- 新增测试文件命名：`WinEventPumpTests.cs` / `PopupPositioningServiceTests.cs` / `MenuBarExtensionRegistryTests.cs`。

### D3（门禁全绿验证）

- `scripts/run-gates.ps1` **相对基线不新增失败**：新增 verify-native-convergence PASS；既有 verify-no-cross-assembly-event / host-log-sink / doc-budgets / gate-registry / smoke-test 已 PASS（arch-violation 落地后）[verified：完成总结]。
- **预存失败 4 项**（非本次引入、保持不恶化）：package-readme（test 项目 + 4 包）、md-wrap（18 历史文档）、architecture-guard（SplashWindow.xaml）、dotnet-format（剩余预存文件）。
- P/Invoke 收敛计数：`packages/` 下非 shell-core/Native 的 `[DllImport` 声明数 < 基线（基线 = 实施前计数，A1 时记录）；本计划结束时不再新增。

### D4（构建与测试）

- `dotnet build BetterDesktop.slnx -f net8.0-windows10.0.19041.0 -c Debug` 成功。
- 受影响包测试全绿：shell-core-tests / shell-menu-bar-tests / shell-status-tests / shell-window-tracker-tests。

### D5（技术力回写）

- 新功能文档入库：shell-core/Native 平台层（11-原生桥接域）、PopupWindowBase/PopupPositioningService（06-Flyout 域）；3603 升级 L2；索引更新（§12 D11/D12）。

## 14. Handoff to 技术力应用（交接节，必填）

> 计划就绪即本 skill 终点；实现由 `skills/ability-reuse-alignment/SKILL.md` 按本节执行。本节是其唯一需要的输入，须可独立执行、零重新调研。**禁止计划 agent 顺手写实现代码。**

### 14.1 模式判定（逐功能点三选一）

| 功能点 | 判定 | 依据 |
|---|---|---|
| P0-1a NativeMethods 平台层 | **无匹配 → 工程代码权威** | 索引无「统一 Win32 平台层」文档；纪律文档 7434/7435/7437/7438 注入作约束，结构以本计划 §6 P0-1 为准 |
| P0-1b WinEventPump | **命中但部分不可用 → 工程代码权威（第三态）** | 7435 命中且为 L2 正确范式（变体 A 即 shell-status 源码），但 shell-taskbar 变体 B 是反例——以变体 A 源码为权威模板，反例禁止照抄 |
| P0-2 弹窗体系上提 | **命中但不可用 → 工程代码权威（第三态）** | 3603 L1 待校验（已对照确认 MenuBarPopupWindow 真实存在）；601/602/603/604 为约束参考；基类本体以 `MenuBarPopupWindow.cs` 源码为权威 |
| P0-3 面板注册机制 | **标准文档注入** | 711 L2 双轨扩展 + 容错装配直接覆盖；契约以 `IMenuBarExtension.cs` 源码为权威 |

### 14.2 注入清单（按应用 skill「调用协议」需完整注入的文档路径，逐份列全）

| 文档 | 注入用途 | 关联功能点 |
|---|---|---|
| `TECH-KNOWLEDGE/74-Windows内部接口逆向/7435-winevent-outofcontext-message-pump.md` | **生死线**：泵线程三件套/字段强引用/Unhook 在注册线程；含本项目正反例 | P0-1b |
| `TECH-KNOWLEDGE/74-Windows内部接口逆向/7434-dwm-thumbnail-declaration-discipline.md` | P/Invoke 声明纪律（out bool / 签名 / 结构对齐） | P0-1a |
| `TECH-KNOWLEDGE/74-Windows内部接口逆向/7437-win32-interop-error-discipline.md` | 返回值检查 / COM 释放配对 / 分配释放对应 | P0-1a |
| `TECH-KNOWLEDGE/74-Windows内部接口逆向/7438-event-subscription-unsubscribe-discipline.md` | 退订配对 / Dispose 幂等 | P0-1b, P0-2 |
| `TECH-KNOWLEDGE/06-Flyout/601-任务栏定位算法.md` | 定位服务双路径/翻转/clamp（复合文档：含正确性不变量清单） | P0-2 |
| `TECH-KNOWLEDGE/06-Flyout/602-无焦点窗口.md` | 弹窗不抢焦点契约 | P0-2 |
| `TECH-KNOWLEDGE/06-Flyout/603-失焦与外部点击关闭.md` | 失焦收起钩子生命周期 | P0-2 |
| `TECH-KNOWLEDGE/06-Flyout/604-亚克力背景分支.md` | 视觉降级（沿用 shell-core Vibrancy 域，不新增实现） | P0-2 |
| `TECH-KNOWLEDGE/07-扩展自动化/711-extension-system.md` | **双轨扩展 + 容错装配 + Stop 顺序 + 装载时机（生死线）** | P0-3 |
| `TECH-KNOWLEDGE/36-设计系统/3603-菜单栏插入按钮弹窗模式.md` | 模式参考（L1，注入仅作模式说明，行为以源码权威） | P0-2 |

### 14.3 适配参数

| 项 | 值 |
|---|---|
| 目标语言 | C#（WPF，net8.0-windows10.0.19041.0） |
| 命名空间 | `BetterDesktop.Shell.Core.Native`（NativeMethods/WinEventPump/MouseHook/MessagePump）、`BetterDesktop.Shell.Core.Windows`（PopupWindowBase/PopupPositioningService）、`BetterDesktop.Shell.Core.Contracts`（IMenuBarExtension/IMenuBarExtensionRegistry） |
| 插入点 | shell-core 现有包内新增目录（不新建 csproj）；迁移删除的文件：shell-taskbar/Native/WinEventHook.cs、shell-window-tracker/Native/WinEventHook.cs、shell-menu-bar/Contracts/PopupAnchor.cs |
| 迁移纪律 | P/Invoke 迁移**不改签名/不重命名**；行为等价优先；删除文件前 git diff 确认无他人未提交改动 |
| 依赖 | 无新增 NuGet；shell-status 等模块增 ProjectReference `shell-core`（M7 单向） |
| 门禁 | `scripts/verify-native-convergence.ps1` + `.Tests.ps1`，注册进 run-gates.ps1 |
| 行为变化唯一例外 | TaskbarAppearanceEngine 钩子修复（变体 B→A），真机确认（§9.1/§13 D1 场景 B） |

### 14.4 禁区（不可触碰符号 / 第三态命中的占位/过时文档）

- **7435 变体 B（shell-taskbar/Native/WinEventHook.cs + TaskbarAppearanceEngine.cs 现有注册方式）**：反例，禁止照抄；仅迁移其注册点到 WinEventPump。
- **245 项未提交改动**（205 项他人 + arch-violation 约 40 项实施改动）：禁止覆盖/回滚（git status 清单为准）。
- `backups/`、`packages/backups/` 目录：备份资产，禁止修改。
- 3603 文档：L1 待校验，注入时只作模式说明，**不得**以其类名/签名为实现依据（以 `MenuBarPopupWindow.cs` 源码为权威）。
- 索引中 601/602/603/604 描述之外的 Flyout 域文档（605/606/608）：与本计划无关，不注入。
- 性能补丁（防抖/轮询调整）：不在本计划 scope（arch-violation 性能工作流已完成且未含防抖，归属 §12 D9）。

### 14.5 DoD 核销表（实现完成后逐条勾销，即「对齐评审」凭据）

| DoD | 验证方式 | 勾销 |
|---|---|---|
| D1 场景 A（面板注册） | 真机走查步骤 1-5 + grep 硬编码=0 | ☐ |
| D1 场景 B（钩子送达） | 真机走查步骤 6-8 + 日志计数 | ☐ |
| D1 场景 C（弹窗定位） | 真机走查步骤 9-10（3 档 DPI × 副屏） | ☐ |
| D2 单测 | `dotnet test shell-core-tests / shell-menu-bar-tests` 新增 3 测试文件全绿 | ☐ |
| D3 门禁 | `run-gates.ps1` 相对基线不新增失败（预存 4 项除外）+ P/Invoke 收敛计数不增 | ☐ |
| D4 构建 | `dotnet build BetterDesktop.slnx -f net8.0-windows10.0.19041.0 -c Debug` 成功 | ☐ |
| D5 回写 | 新文档入库 + 3603 升级 L2 + 索引更新 | ☐ |

## §可选增强 / 超越需求建议（beyond_scope，不混入强制 scope）

| # | 增强 | 收益 | 依赖资产 |
|---|---|---|---|
| B1 | **ISettingsService 上提 shell-core**：44 文件使用者却定义于 shell-settings——上提后基础设施定位归位，消除跨包依赖倒挂 | 长期架构健康；依赖方向简化 | 本计划 P0-1 建立的公共层模式 |
| B2 | **IMonitor 注册表**：8 个 Monitor（Ime/Mic/Volume/Cpu/Memory/Network/Battery/Display）统一为注册式，MonitorBase 化 | 新监控项零样板注册（复用 P0-3 注册表范式） | 本计划 P0-3 Registry + 64/69 域文档 |
| B3 | **601 ABM 双路径落地**：PopupPositioningService 接 ABM_GETTASKBARPOS + Shell_TrayWnd 锚定，Dock/StartMenu 弹窗贴任务栏场景启用 | Flyout 定位完整化（当前仅纯几何） | 本计划 P0-2 服务 + 601 文档 |
| B4 | **主题令牌 SDK 化**：IShellPluginWindow.ThemeSnapshot 与 SetThemeBinding 令牌体系对外部插件开放 | 插件外观一致性 | plugin-sdk + 16-主题域 |
| B5 | **Win32 平台层沉淀后按 74 域模式扩展**：Native 收口稳定后，将 7434-7438 纪律族收编为 Native 层内置校验（如源生成器校验结构体布局） | 纪律从「文档约束」升级为「编译期强制」 | 本计划 P0-1 + 74 域纪律族 |
| B6 | **607 Flyout 状态机接入 PopupWindowBase**：Cloak 防白闪 + 300ms 去抖 + Opening 态忽略失焦 | 弹窗弹出稳定性提升 | 本计划 P0-2 + 607 文档 |

> 以上增强均为可选、可独立交付；强制 scope 保持 §6 最小集。
