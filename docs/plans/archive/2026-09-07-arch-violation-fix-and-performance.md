# Cairo 开发计划 · 架构违规修复与性能工作流

> Task: 修复 8 项已核实架构违规（跨程序集裸 event / DiagnosticLog 静态旁路 / HMR 缺 ADR / cordis.yml 未接线 / 5 包缺 README / TFM 文档不一致 / Taskbar 目录大写 / host 缺 4 引用），并建立桌面 shell 性能优化工作流（流畅度与稳定性）。
> 证据基于 commit `0ecd841` 验证；技术力文档命中：2601（事件总线范式）、2401（插件系统范式）、63（桌面渲染 desktop-progman-embed / dwm-live-thumbnail）、74（7438 事件订阅退订纪律 / 7414 定时器驱动 UI 动画 / 7417 WASAPI 会话管理 / 7416 Accent 策略 / 7432 全屏前台检测）；未命中：C# 专用 ShellEvents 常量类（新建）、host FileLogSink（新建）、verify-no-cross-assembly-event 门禁（新建）。
> 证据头 schema 2；目标仓库 `better-desktop-cordis`，工作区含 205 项他人未提交改动（禁止覆盖/回滚）。

## 1. Objective

在不触碰他人 205 项未提交改动的前提下，分优先级修复 8 项架构违规，使代码与 ADR-001 宪法七条红线、ADR-002 D1-D5 冻结面、coding-standards.md 禁止清单、MECHANISMS.md M10 单管道完全对齐；同时建立可复跑的桌面 shell 性能优化工作流（establish-baseline → diagnose → optimize → guard），以可测量证据驱动流畅度与稳定性提升，不预设收益。

用户可感知结果：设置变更/外观变更不再因裸 event 泄漏导致插件卸载后幽灵回调；启动期日志从桌面散落文件收敛到 `%LocalAppData%\BetterDesktop\logs\` 单一管道；插件装配从硬编码时序迁移到声明式 cordis.yml；门禁全绿且新增跨程序集 event 机器校验；性能基线数据可复跑、回归可守卫。

## 2. Current Behaviour

### 2.1 跨程序集裸 C# event（违规1 🔴）

`ISettingsService.Changed` 定义于 `packages/shell/shell-settings/Contracts/ISettingsService.cs:28`（`event EventHandler<SettingsChangedEventArgs>? Changed`），被以下跨程序集订阅方以 `+=` 直接订阅：

- `packages/shell/shell-desktop/DesktopPlugin.cs:106`
- `packages/shell/shell-desktop/DesktopWindow.cs:201`
- `packages/shell/shell-desktop/DesktopIconsControl.cs:135`
- `packages/shell/shell-dock/DockWindow.xaml.cs:200`
- `packages/shell/shell-quick-note/QuickNotePlugin.cs:45`
- `host/Bootstrap.cs:209`（匿名 lambda，无退订）

`IAppearanceService.Changed` 定义于 `packages/shell/shell-core/Surface/IAppearanceService.cs:120`（`event EventHandler<AppearanceChangedArgs> Changed`），被以下跨程序集订阅：

- `packages/shell/shell-dock/DockWindow.xaml.cs:347`
- `packages/shell/shell-menu-bar/MenuBarPlugin.cs:75`

全库约 60 处 `event` 定义 [verified]；业务层几乎不使用内核 `IEventBus.EmitAsync`/`On`，唯一总线用例是 `packages/kernel/kernel-hmr/HmrEvents.cs`（常量类 `"kernel.plugin/loaded"` 等）+ HmrManager 内部 Emit [verified]。同程序集内 WPF 控件事件（ToggleSwitch、StatusStrip、BlankAreaDoubleClick 等）保留，不算违规 [verified]。

判定基准：coding-standards.md 禁止清单第 3 条「禁止跨程序集裸 C# event——跨插件通信走内核事件服务」+ ADR-002 D4.1「跨插件/跨包通信一律走内核 IEventBus」。

### 2.2 DiagnosticLog 静态旁路日志（违规2 🔴）

`packages/kernel/kernel/Core/DiagnosticLog.cs:11` 静态类，`File.AppendAllText` 直写桌面 `BetterDesktop_debug.log`（第 13-16 行路径），第 25 行含裸 `catch {}`（吞异常）[verified]。

调用点：
- `packages/kernel/kernel/Core/PluginHandle.cs:192`（插件加载失败旁路追踪）
- `host/Bootstrap.cs`：第 263、273、302、318、325、330、334、339、368、374、384、388、393、425、432、439、454、459、468、473 行共 20+ 处 `DiagnosticLog.Trace` [verified]

`host/Bootstrap.cs:41-44`：`CordisContext(logSink: (level, msg) => Console.WriteLine(...))` 用 `Console.WriteLine` 作内核日志 sink [verified]。

判定基准：MECHANISMS.md M10「日志单管道，禁止 Console.WriteLine、吞异常、第二套日志」+ coding-standards.md 禁止清单第 1 条（静态单例）、第 5 条（吞异常与 Console.WriteLine）。

### 2.3 HMR 提前进 v1 无 ADR（违规3 🟠）

`packages/kernel/kernel-hmr/` 完整实现（HmrManager 双 ALC 先立后破、IHmrManager、IResourceGovernor、AssemblyPluginSource）[verified]；`host/Bootstrap.cs:87-105` 装配 HmrManager + ResourceGovernor [verified]。

仅有 agent note `.agents/notes/implemented/feature/2026-08-20-hmr-hot-reload.md`，`docs/architecture/` 下只有 ADR-001/002 [verified]。ADR-002 D1 明确「HMR 不在 v1（P2 范畴）」，其 §五触发条件规定「把 v1 明确排除项提前进 v1 必须新开 ADR」。

### 2.4 cordis.yml 声明式插件树未接线（违规4 🟠）

仓库无 cordis.yml（全仓 glob 0 匹配）[verified]。`host/Bootstrap.cs:76-189` 全部 `context.Plugin(new Xxx())` 硬编码 + 手工顺序，注释大量「必须早于 X」（如第 107-110 行 AppSource、第 113-116 行 WindowTracker、第 118-121 行 Pinning）[verified]。

`packages/kernel/kernel-loader/LoaderService.cs` 已实现 cordis.yml 解析（YamlDotNet，第 37-39 行 `File.ReadAllText` + `DeserializerBuilder`），`LoaderOptions.Factories` 字典（`packages/kernel/kernel-loader/LoaderOptions.cs:15`）+ `LoaderConfig`（id/name/enabled，`packages/kernel/kernel-loader/LoaderConfig.cs`）[verified]。但 LoaderService 仅测试引用，host 未使用 [inferred]。

`LoaderService.cs:72` 使用 `handle.AwaitAsync().GetAwaiter().GetResult()` 同步阻塞（违反 coding-standards.md 五.2 禁止 sync-over-async）[verified]。

决策记录 `.agents/notes/implemented/feature/2026-08-20-shell-plugin-assembly-order.md` 曾计划 cordis.yml 装配 [verified]。architecture.md P1 目标表明确「Loader：声明式插件树（cordis.yml），不做拓扑排序」[verified]。

### 2.5 5 个 shell 包缺 README（违规5 🟡）

`packages/shell/` 下 31 个 csproj，其中以下 5 个包同目录无 README.md [verified]：
- `shell-desktop/`（`BetterDesktop.Shell.Desktop.csproj`）
- `shell-music/`（`BetterDesktop.Shell.Music.csproj`）
- `shell-plugin-sdk/`（`BetterDesktop.Shell.PluginSdk.csproj`）
- `shell-quick-note/`（`BetterDesktop.Shell.QuickNote.csproj`）
- `shell-start-menu/`（`BetterDesktop.Shell.StartMenu.csproj`）

`scripts/manifests/readme-ratchet.baseline.json`：`LOCKED-MAX-COUNT: 0`、`LOCKED-COUNT: 0`、`allowlist: []`（0 容忍）[verified]。`scripts/verify-package-readme.ps1` 强制每个 csproj 同目录 README.md 含 `## Known Limitations` + 至少一条 `- ` 列表项 [verified]。

### 2.6 TFM 文档与代码不一致（违规6 ⚪）

全部 csproj TFM 为 `net8.0-windows10.0.19041.0`（如 `host/BetterDesktop.Host.csproj:4`）[verified]。`docs/coding-standards.md:8` 写死 `net8.0-windows`「禁止任何包写其它 TFM」[verified]。ADR-001 D2 决策为「仅 net8.0-windows + WPF」[verified]。

### 2.7 Taskbar 目录名大写（违规7 ⚪）

`packages/shell/Taskbar/` 目录名大写（标准为小写 `shell-*`，其余 20+ 包均小写）[verified]。`host/BetterDesktop.Host.csproj:46` 引用路径 `..\packages\shell\Taskbar\BetterDesktop.Shell.Taskbar.csproj` [verified]。命名空间 `BetterDesktop.Shell.Taskbar`（不变）[inferred]。

### 2.8 host 未显式引用 4 个 shell 包（违规8 ⚪）

`host/BetterDesktop.Host.csproj` 的 ProjectReference 列表（第 33-50 行）未包含以下 4 个包 [verified]：
- `shell-app-source`（Bootstrap.cs:111 `new AppSourcePlugin()`）
- `shell-window-tracker`（Bootstrap.cs:116 `new WindowTrackerPlugin()`）
- `shell-pinning`（Bootstrap.cs:121 `new PinningPlugin()`）
- `shell-calendar`（Bootstrap.cs:178 `new CalendarPlugin()`）

当前靠传递引用编译通过（shell-dock 引用 shell-app-source 等）[inferred]，但显式引用缺失违反依赖单向原则（M7）且使传递链断裂时编译失败。

## 3. Relevant Architecture

### 3.1 内核架构（ADR-001 §2.3 + ADR-002 D1 冻结面）

- **一切皆插件**：loader、HMR、logger、ShellBar、Taskbar 全走同一 `IContext.Plugin()` 通道（ADR-001 §2.3）[verified]。
- **服务图**：`IContext`（`packages/kernel/kernel/Contracts/IContext.cs`）提供 `Get<T>()` / `Provide<T>()` / `Extend()` / `Plugin()` / `Effect()` / `Events` / `Logger` [verified]。
- **事件总线**：`IEventBus`（`packages/kernel/kernel/Contracts/IEventBus.cs`）——`On<T>` / `OnResult<T,TResult>` / `EmitAsync` / `ParallelAsync` / `SerialAsync` / `BailAsync` / `WaterfallAsync`，事件名「域/动作」+ 强类型载荷 + 单监听器异常隔离 [verified]。
- **托管生命周期**：`IContext.Effect(Func<IDisposable> execute)` 注册清理器，fiber 卸载时逆序并行执行、单条异常隔离 [verified]。
- **日志**：`IKernelLogger`（`packages/kernel/kernel/Contracts/IKernelLogger.cs`）——`Log/Info/Warn/Error`，`CordisContext` 构造接受 `logSink: Action<LogLevel,string>?`（`packages/kernel/kernel/Core/CordisContext.cs:22`）[verified]。

### 3.2 包分层

```
kernel/*     内核本体：kernel / kernel-loader / kernel-hmr / kernel-timer
shell/*      外壳能力（全部是插件）：shell-core / shell-settings / shell-dock / ...
host/        薄宿主 exe：装配内核 + 加载插件 + 启动自检
```

### 3.3 机制唯一化（MECHANISMS.md）

- M5 插件内核：Cordis 内核 C# 复刻 [verified]
- M10 运行时健康：日志单管道 / 崩溃三入口 / 禁止 Console.WriteLine / 吞异常 [verified]
- M7 功能复用与反臃肿：依赖单向 / 禁复制 [verified]
- M1 门禁执行入口：`scripts/run-gates.ps1` 注册表（当前 13 道门禁）[verified]

### 3.4 技术力文档关联

- 拆析 `拆析-cairoshell-最初开源版.md`：命令系统 + MEF 插件 DI 加载、多屏窗口生命周期、DWM 缩略图——本项目内核从零复刻而非复用旧代码（ADR-001 R5）。
- 拆析 `拆析-PaperTodo-桌面便签.md`：多窗口纸片 / 胶囊贴边多屏队列 / LMDB 图片存储——WPF 桌面 shell 参考。
- 功能文档 7438（event-subscription-unsubscribe-discipline）：`+=` 必须 `-=` 退订 / WeakEventManager / Dispose 幂等——违规1 迁移的生死线。
- 功能文档 7414（timer-driven-ui-animation）：SetTimer 成对 / 15ms 逐帧 / 100ms 去抖——性能调查候选区域。
- 功能文档 63 desktop-progman-embed：自绘桌面 WS_CHILD+SetParent→Progman/WorkerW——shell-desktop 性能调查核心区域。

## 4. Technical-Knowledge Findings

### 4.1 命中的机制文档

| 编号 | 文档 | 定位 | 与本计划关系 |
|---|---|---|---|
| 2601 | 响应式类型化事件总线 | eventa + alien-signals，跨进程适配（TypeScript） | 范式参考：事件名「域/动作」+ 类型化载荷 + 订阅返回注销句柄，与内核 IEventBus 设计同构。C# 内核已自实现，不注入代码，仅验证命名约定一致性。 |
| 2401 | 插件系统 | valibot manifest + 权限门控扩展点（TypeScript） | 范式参考：插件 manifest 声明式装配。本项目 cordis.yml（LoaderConfig）是 C# 等价物，违规4 接线时参照其「声明优先于硬编码」原则。 |
| 63 desktop-progman-embed | 桌面 Progman 嵌入 | WS_CHILD+SetParent→Progman/WorkerW，壁纸归 explorer，瀑布列图标（C#） | 直接相关：shell-desktop 自绘桌面实现的正确性不变量来源；性能调查区域（桌面图标渲染 / 壁纸合成）。 |
| 63 dwm-live-thumbnail | DWM 实时缩略图 | DwmRegisterThumbnail 画进 WPF 控件，等比缩放+DPI 换算（C#） | 性能调查候选：DWM 缩略图注册/注销纪律、资源泄漏。 |
| 7438 | 事件订阅退订配对纪律 | +=必须-= / 长持短泄漏 / WeakEventManager / Dispose 幂等（C#） | **违规1 生死线**：迁移到 IEventBus 后，所有 On 注册必须经 Effect 托管或显式 Dispose，杜绝插件卸载后幽灵回调。 |
| 7414 | 定时器驱动 UI 动画 | SetTimer 成对 / 15ms 逐帧 / 100ms 去抖 / 500ms 连点窗（C++） | 性能调查候选：WPF 动画/定时器是否存在未成对、过度重绘。 |
| 7417 | WASAPI 音频会话管理 | 单指针 QI 多接口 / Dispatcher marshal / 注册前初始化 / 防循环引用（C#） | shell-music 包性能/稳定性调查候选（若涉及音频会话）。 |
| 7416 | Accent 策略应用 | Acrylic alpha 被拒 / AccentFlags 分型 / Gradient 强制不透明 / blur 版本补丁（C++） | shell-taskbar / shell-core Vibrancy 性能调查候选。 |
| 7432 | 全屏前台检测 | DWM 边界 + CoversMonitor + 排除 ToolWindow/Cloaked/Shell（C#） | shell-window-tracker 性能调查候选（轮询频率 / 缓存）。 |

### 4.2 未命中（新建）

- **ShellEvents 常量类**：仿 HmrEvents 建 `"shell.settings/changed"` / `"shell.appearance/changed"` 等事件名常量。检索关键词：`ShellEvents`、`shell.settings`、事件名常量——全仓仅 HmrEvents 存在，无 shell 层等价物。
- **host FileLogSink**：`%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log` 文件日志 sink。检索关键词：`FileLogSink`、`logSink`、文件日志——内核仅有 `Action<LogLevel,string>` 委托，host 用 Console.WriteLine，无文件实现。
- **verify-no-cross-assembly-event 门禁**：扫描跨程序集 event 订阅的 PowerShell 门禁。检索关键词：`verify-no-cross-assembly`、跨程序集 event 门禁——run-gates.ps1 注册表 13 道门禁中无此项。

### 4.3 文档新鲜度判定

- 2601 / 2401 为 TypeScript 实现，本项目为 C#，属范式参考而非代码复用，不存在「源码依据不一致」问题 [verified]。
- 63 / 74 系列 C# 文档为其他项目（cairoshell / PaperTodo / TranslucentTB 等）沉淀的通用机制，本计划用作正确性不变量与性能调查参考，不直接注入代码 [verified]。
- 结论：无命中文档需更新；本计划完成后，ShellEvents 模式若稳定应按 tech-knowledge-accumulation skill 沉淀为新功能文档（§12 deferred）。

## 5. Constraint Findings（机制文档的验收标准与生死线）

### 5.1 违规1 约束：跨程序集 event → IEventBus 迁移

**验收标准**（ADR-002 D4 + coding-standards 禁止清单第3条 + 7438 生死线）：
1. 跨程序集通信全部走 `IEventBus`，接口定义中不得保留跨程序集 `event` 成员 [verified]。
2. 事件名遵循「域/动作」约定（如 `shell.settings/changed`），载荷强类型 record [verified]。
3. **生死线（7438）**：每个 `events.On<T>()` 注册必须返回 `IDisposable` 并经 `context.Effect(() => ...)` 托管或在 `UnloadAsync` 中显式 Dispose；插件卸载后不得有幽灵回调 [verified]。
4. 同程序集内 WPF 控件事件（ToggleSwitch.Click 等）保留，不迁移 [verified]。
5. 单监听器异常隔离由 IEventBus 内核实现保障（不阻断其余监听器，异常入内核日志）[verified]。
6. SettingsService 实现须注入 `IContext`（或 `IEventBus`），在 `Set<T>` 中调用 `Events.EmitAsync` 替代 `Changed?.Invoke` [inferred]。
7. 订阅方在 `LoadAsync` 中用 `context.Effect(() => context.Events.On<SettingsChangedEventArgs>(ShellEvents.SettingsChanged, (payload, ct) => { ... }))` 注册 [inferred]。

**等价源码断言**：
- `ISettingsService.cs:28` 的 `event EventHandler<SettingsChangedEventArgs>? Changed` → 移除，改为服务内部 EmitAsync。
- `IAppearanceService.cs:120` 的 `event EventHandler<AppearanceChangedArgs> Changed` → 移除，改为服务内部 EmitAsync。
- `Bootstrap.cs:209` 匿名 lambda 订阅 → 改为 Effect 托管的 On 注册，或移入 NativeTaskbar 管理插件的 LoadAsync。

**状态变更与错误分支**：
- EmitAsync 为 async，SettingsService.Set 当前为同步 `void`；需决定 Set 是否改为 async 或同步阻塞 Emit（coding-standards 五.2 禁止 sync-over-async）→ Set 改为 `async Task` 或 fire-and-forget 并记录异常（进 §12 开放问题）。
- 载荷类型不匹配属调用方契约错误（IEventBus 抛 InvalidCastException），订阅方必须用正确泛型参数 [verified]。

### 5.2 违规2 约束：DiagnosticLog → 内核 Logger 单管道

**验收标准**（M10 + coding-standards 禁止清单第1/5条）：
1. 删除 `DiagnosticLog.cs` 静态类，全仓无 `DiagnosticLog.Trace` 调用 [verified]。
2. `PluginHandle.cs:192` 改走 `_context.Logger.Error/Warn` [inferred]。
3. host 提供 `FileLogSink`：路径 `%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log`，限定 `catch(IOException)`（禁止裸 catch），按日期滚动 [inferred]。
4. `Bootstrap.cs:41-44` 移除 `Console.WriteLine` logSink，改为 FileLogSink [inferred]。
5. Bootstrap.cs 中 20+ 处 `DiagnosticLog.Trace("menu-cmd", ...)` 改为 `context.Logger.Info/Warn`（菜单命令桥的诊断信息）[inferred]。
6. 启动追踪（Bootstrap.cs:464-474 列举插件状态）改为 `context.Logger.Info` [inferred]。

**边界与异常路径**：
- FileLogSink 写文件失败：仅 `catch(IOException)` 记录到备用（如 Debug.WriteLine 或静默），不阻断主流程；禁止裸 `catch {}` [verified]。
- 日志目录不存在：创建 `%LocalAppData%\BetterDesktop\logs\`，创建失败降级 [inferred]。
- 并发写：多线程日志需线程安全（lock 或 ConcurrentQueue+后台 flush）[inferred]。

### 5.3 违规3 约束：HMR ADR 追认

**验收标准**（ADR-002 §五触发条件 + ADR-001 R7）：
1. 新建 `docs/architecture/ADR-003.md`：追认 HMR 提前进 v1 的决策，含背景（为何 P2 排除项提前）、决策（双 ALC 先立后破 + 失败回滚 + IResourceGovernor）、后果、被否决方案、关联文档 [inferred]。
2. 修订 ADR-002 D1「明确不在 v1」清单：移除 HMR，标注「已由 ADR-003 追认提前进 v1」 [inferred]。
3. 修订 `docs/architecture.md` 内核要点表：HMR 行标注「v1 已实现（ADR-003）」 [inferred]。
4. 修订 `docs/architecture/STATUS.md`：已完成范围增加 HMR，更新时间 [inferred]。
5. 注意：`scripts/verify-architecture-guard.ps1:6` 注释引用「ADR-003 D2」（前向引用），ADR-003 内容须与此注释一致或修正注释 [verified]。

### 5.4 违规4 约束：cordis.yml 接线

**验收标准**（architecture.md P1 目标 + ADR-001 §2.3「loader 不做拓扑排序」）：
1. 新建 `host/cordis.yml`：声明当前 Bootstrap.cs 中全部 ~20 个插件条目（id/name/enabled），顺序与当前硬编码一致（迁移期保序）[inferred]。
2. `host/Bootstrap.cs`：构建 `LoaderOptions`，注册 `Factories` 字典（插件构造大多无参，直接 `() => new XxxPlugin()`），将硬编码 `context.Plugin()` 替换为 `context.Plugin(new LoaderService(options))` + `AwaitAsync` [inferred]。
3. `LoaderService.cs:72`：`handle.AwaitAsync().GetAwaiter().GetResult()` 改为 async（`LoadAsync` 已为 Task，直接 `await handle.AwaitAsync()`）[verified]。
4. 时序敏感依赖（AppSource→Dock、WindowTracker→Dock、Pinning→Dock、Settings→MenuBar 等）收敛为插件 `Inject` 声明，交给内核 Pending 机制，不再依赖手工顺序 [inferred]。
5. 特殊处理：Bootstrap.cs 中 `context.Provide<ISettingsService>(settingsSvc)`（第 70-72 行，提前 Provide 使所有插件 LoadAsync 可取到设置）须在 LoaderService 装配前执行，或迁移为 SettingsPlugin 的 Provide [inferred]。
6. 特殊处理：HmrManager 装配（第 87-105 行）含 `ResourceGovernorOptions` 环境变量读取 + `OnProcessCritical` 回调，工厂 lambda 需捕获这些参数 [inferred]。
7. 特殊处理：Bootstrap.cs 第 198-221 行（NativeTaskbar 显隐 + Application.Exit 钩子 + settingsSvc.Dispose + hmrManager.Dispose）不属于插件装配，保留在 Bootstrap 中 [inferred]。
8. 可先查 git 历史是否有被删的 cordis.yml（`git log --all --full-history -- cordis.yml`）[inferred]。

**边界与异常路径**：
- cordis.yml 解析失败：LoaderService 已 `context.Logger.Error` + `throw`（第 41-45 行），host 须捕获并 fail-fast 或降级为最小启动 [verified]。
- 未知工厂名：LoaderService 已 fail-closed（第 54-59 行），记录 UnknownFactory [verified]。
- 插件加载失败：LoaderService 记录 Failed 但继续后续插件（第 74 行），与当前硬编码行为一致 [verified]。

### 5.5 违规5 约束：5 包 README

**验收标准**（verify-package-readme.ps1 + AGENTS.md 文档纪律第2条）：
1. 每个缺 README 的包新建同目录 `README.md`，含四小节：一句话职责 / 依赖 / 扩展点 / `## Known Limitations`（≥1 条 `- ` 列表项）[verified]。
2. 不放宽 `readme-ratchet.baseline.json` 的 allowlist（保持 0）[verified]。
3. 参考模板：`packages/shell/shell-dock/README.md`（四小节格式已验证）[verified]。

### 5.6 违规6 约束：TFM 文档修正

**验收标准**：
1. 修改 `docs/coding-standards.md:8`：允许 Windows SDK 版本化 TFM（`net8.0-windows10.0.19041.0`），说明理由（需要 Windows 10 特定 API 如 WinRT、AppBar 等）[inferred]。
2. 此决策并入 ADR-003（作为 ADR-003 的附加决策项）或单独说明 [inferred]。
3. **不改代码**（全部 csproj 已正确使用版本化 TFM）[verified]。

### 5.7 违规7 约束：Taskbar 目录重命名

**验收标准**：
1. `git mv packages/shell/Taskbar packages/shell/shell-taskbar`（保留 git 历史）[inferred]。
2. 更新 `host/BetterDesktop.Host.csproj:46` 引用路径 [verified]。
3. 全仓搜索 `packages/shell/Taskbar` 引用路径并更新（其他 csproj 的 ProjectReference、README 链接等）[inferred]。
4. 命名空间 `BetterDesktop.Shell.Taskbar` 不变（仅目录名改）[inferred]。
5. 重命名后 `dotnet build` 通过 [inferred]。

### 5.8 违规8 约束：host 显式引用

**验收标准**：
1. `host/BetterDesktop.Host.csproj` 新增 4 个 ProjectReference：shell-app-source、shell-window-tracker、shell-pinning、shell-calendar [inferred]。
2. 引用顺序按依赖关系排列（app-source → window-tracker → pinning → calendar）[inferred]。
3. `dotnet build` 通过且无 NU1506（重复引用）警告 [inferred]。

## 6. Proposed Changes

### 6.1 违规1：跨程序集 event → IEventBus（🔴 最高优先级）

| 文件 | 符号 | 职责 | 期望行为变化 |
|---|---|---|---|
| `packages/shell/shell-settings/Contracts/ISettingsService.cs` | `Changed` event (L28) | 移除跨程序集 event | 接口不再暴露 event；设置变更由服务内部经 IEventBus Emit |
| `packages/shell/shell-settings/Services/SettingsService.cs` | `Set<T>` / 构造函数 | 注入 IContext，Set 中 EmitAsync | `Set<T>` 触发 `context.Events.EmitAsync<SettingsChangedEventArgs>("shell.settings/changed", ...)`；构造函数接受 IContext |
| `packages/shell/shell-core/Surface/IAppearanceService.cs` | `Changed` event (L120) | 移除跨程序集 event | 接口不再暴露 event；外观变更由服务内部经 IEventBus Emit |
| `packages/shell/shell-core/Surface/AppearanceService.cs`（实现） | 属性 setter / `NotifyChanged` | 注入 IContext，setter 中 EmitAsync | 属性变更触发 `context.Events.EmitAsync<AppearanceChangedArgs>("shell.appearance/changed", ...)` |
| 新建 `packages/shell/shell-core/ShellEvents.cs` | `ShellEvents` 静态类 | 事件名常量 | `public const string SettingsChanged = "shell.settings/changed"`；`public const string AppearanceChanged = "shell.appearance/changed"`；仿 HmrEvents 模式 |
| `packages/shell/shell-desktop/DesktopPlugin.cs:106` | 订阅代码 | 改为 Effect 托管的 On 注册 | `context.Effect(() => context.Events.On<SettingsChangedEventArgs>(ShellEvents.SettingsChanged, (e, ct) => { ... }))` |
| `packages/shell/shell-desktop/DesktopWindow.cs:201` | 订阅代码 | 同上 | 同上模式 |
| `packages/shell/shell-desktop/DesktopIconsControl.cs:135` | 订阅代码 | 同上 | 同上模式；WPF UserControl 需从 DataContext 或服务定位器取 IEventBus（进 §12） |
| `packages/shell/shell-dock/DockWindow.xaml.cs:200` | 订阅代码 | 同上 | 同上模式 |
| `packages/shell/shell-dock/DockWindow.xaml.cs:347` | 订阅代码 | 同上（IAppearanceService） | 同上模式，载荷 `AppearanceChangedArgs` |
| `packages/shell/shell-quick-note/QuickNotePlugin.cs:45` | 订阅代码 | 同上 | 同上模式 |
| `packages/shell/shell-menu-bar/MenuBarPlugin.cs:75` | 订阅代码 | 同上（IAppearanceService） | 同上模式 |
| `host/Bootstrap.cs:209` | 匿名 lambda 订阅 | 改为 Effect 托管或移入插件 | NativeTaskbar 显隐逻辑封装为独立插件或在 Bootstrap 中用 `context.Effect` 注册 |
| 新建 `scripts/verify-no-cross-assembly-event.ps1` | 门禁脚本 | 扫描跨程序集 event 订阅 | 检测 `Contracts/**/*.cs` 中的 `event` 声明 + 跨程序集 `+=` 订阅；同程序集 WPF 事件豁免 |
| `scripts/run-gates.ps1` | 注册表 | 登记新门禁 | 新增 `[pscustomobject]@{ Id = 'no-cross-assembly-event'; Script = 'verify-no-cross-assembly-event.ps1'; Needs = @() }` |
| 新建 `scripts/verify-no-cross-assembly-event.Tests.ps1` | 门禁单测 | Pester 测试 | 覆盖「非法输入 → 返回违规」：含跨程序集 event 的样例代码应被检测到 |

**实现备注**：
- SettingsService 当前在 Bootstrap.cs:70 直接 `new SettingsService()`（无构造参数），改构造函数后须更新 Bootstrap 传 `context` [verified]。
- AppearanceService 实现位置需确认（`shell-core/Surface/` 下），实现类名可能为 `AppearanceService` 或 `ThemeCenter` [assumed]。
- EmitAsync 为 async 方法，SettingsService.Set 为同步 void；方案：Set 改为 `async Task SetAsync<T>`（调用方 await），或 fire-and-forget `_ = EmitAsync(...)` 并 `ContinueWith` 记录异常。推荐前者但影响 ISettingsService 接口签名（D1 冻结面？ISettingsService 非内核冻结面，属 shell 契约，可改）[inferred]。

### 6.2 违规2：DiagnosticLog → 内核 Logger（🔴）

| 文件 | 符号 | 职责 | 期望行为变化 |
|---|---|---|---|
| `packages/kernel/kernel/Core/DiagnosticLog.cs` | 整个文件 | 删除 | 静态旁路日志消除 |
| `packages/kernel/kernel/Core/PluginHandle.cs:192` | `DiagnosticLog.Trace` | 改为 `_context.Logger.Error` | 插件加载失败走内核日志单管道 |
| 新建 `host/FileLogSink.cs` | `FileLogSink` 类 | 文件日志 sink 实现 | 路径 `%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log`；线程安全；按日期滚动；`catch(IOException)` 限定；实现 `Action<LogLevel,string>` 或独立接口 |
| `host/Bootstrap.cs:41-44` | `logSink: Console.WriteLine` | 改为 FileLogSink | `var logSink = new FileLogSink(); var context = new CordisContext(logSink: logSink.Invoke)` |
| `host/Bootstrap.cs`（20+ 处） | `DiagnosticLog.Trace("menu-cmd", ...)` | 改为 `context.Logger.Info/Warn` | 菜单命令桥诊断信息走内核日志；启动追踪走 `context.Logger.Info` |
| `host/BetterDesktop.Host.csproj` | — | 无需新依赖 | FileLogSink 纯 BCL（System.IO），零第三方依赖 |

**实现备注**：
- FileLogSink 须实现 `IDisposable`（flush 缓冲区 + 释放 FileStream），在 `Application.Current.Exit` 中 Dispose [inferred]。
- 日志级别映射：`LogLevel.Info/Warn/Error` → 文件前缀 `[INFO]/[WARN]/[ERROR]` [inferred]。
- Bootstrap.cs 中 `DiagnosticLog.Trace` 的 tag 参数（如 "menu-cmd"、"Bootstrap"）映射为日志消息前缀 `[menu-cmd]` [inferred]。

### 6.3 违规3：HMR ADR 追认（🟠）

| 文件 | 符号 | 职责 | 期望行为变化 |
|---|---|---|---|
| 新建 `docs/architecture/ADR-003.md` | ADR-003 | 追认 HMR 提前进 v1 + TFM 决策 | 含背景/决策/后果/被否决方案/关联文档/触发条件；决策项：D1 HMR 双 ALC 先立后破进 v1；D2 桌面主窗口归 shell.desktop（与 verify-architecture-guard 注释一致）；D3 TFM 允许版本化 `net8.0-windows10.0.19041.0` |
| `docs/architecture/ADR-002.md` | D1「明确不在 v1」清单 | 修订 | 移除 HMR，标注「已由 ADR-003 追认」 |
| `docs/architecture.md` | 内核要点表 HMR 行 | 修订 | 标注「v1 已实现（ADR-003）」 |
| `docs/architecture/STATUS.md` | 已完成范围 / 更新时间 | 修订 | 增加 HMR；更新时间为实施日期 |
| `scripts/verify-architecture-guard.ps1:6` | 注释「ADR-003 D2」 | 确认一致 | ADR-003 D2 内容须与「桌面主窗口归 shell.desktop」一致 |

### 6.4 违规4：cordis.yml 接线（🟠）

| 文件 | 符号 | 职责 | 期望行为变化 |
|---|---|---|---|
| 新建 `host/cordis.yml` | 插件树声明 | 声明 ~20 个插件条目 | `plugins:` 列表，每项 `id`/`name`/`enabled`；顺序与当前 Bootstrap.cs 一致 |
| `host/Bootstrap.cs` | `Build()` 方法 | 重构装配逻辑 | 构建 `LoaderOptions` + `Factories` 字典；替换硬编码 `context.Plugin()` 为 `context.Plugin(new LoaderService(options))`；保留 Provide（SettingsService/HmrManager）、NativeTaskbar、Exit 钩子、MenuCommandPipe |
| `packages/kernel/kernel-loader/LoaderService.cs:72` | `GetAwaiter().GetResult()` | 改为 async | `await handle.AwaitAsync(cancellationToken)`；LoadAsync 签名已支持 CancellationToken |
| 各插件 `Inject` 属性 | 依赖声明 | 审计并补全 | 确保时序敏感依赖（AppSource→Dock 等）已在 Inject 中声明，交给 Pending 机制 |

**实现备注**：
- 插件构造函数参数：大部分无参；PowerManagement 需 `context.Logger`（Bootstrap.cs:76 `new PowerManagement(context.Logger)`），HmrManager 需 context + governorOptions，须在 Factories lambda 中捕获 [verified]。
- SettingsService 提前 Provide（Bootstrap.cs:65-73）：保持在 LoaderService 装配前，使所有插件 LoadAsync 可取到 ISettingsService [verified]。
- HmrManager 装配（Bootstrap.cs:87-105）：含环境变量读取 + OnProcessCritical 回调，工厂 lambda 需捕获；HmrManager 同时 Provide 为 IHmrManager + IResourceGovernor [verified]。
- cordis.yml 设为 `host/cordis.yml`，csproj 中需 `<Content Include="cordis.yml" CopyToOutputDirectory="PreserveNewest" />` [inferred]。

### 6.5 违规5：5 包 README（🟡）

| 文件 | 职责 |
|---|---|
| 新建 `packages/shell/shell-desktop/README.md` | 自绘桌面插件：全屏壁纸 + 可导航桌面/文件夹浏览器 + IDesktopBrowser |
| 新建 `packages/shell/shell-music/README.md` | 音乐插件（当前实现状态需读代码确认）[assumed] |
| 新建 `packages/shell/shell-plugin-sdk/README.md` | 插件 SDK：IPlugin 基类 / 插件契约辅助类型 |
| 新建 `packages/shell/shell-quick-note/README.md` | 快速笔记浮窗插件：extensions.quick-note.enabled 驱动启停 |
| 新建 `packages/shell/shell-start-menu/README.md` | 开始菜单插件：Open-Shell 风弹出菜单 + 外观控制 |

每个 README 含四小节：一句话职责 / 依赖 / 扩展点 / `## Known Limitations`（≥1 条）。

### 6.6 违规6：TFM 文档修正（⚪）

| 文件 | 改动 |
|---|---|
| `docs/coding-standards.md:8` | 改为「目标框架：`net8.0-windows10.0.19041.0`（允许 Windows SDK 版本化 TFM，ADR-003 D3 裁定；禁止任何包写其它 TFM）」 |

### 6.7 违规7：Taskbar 目录重命名（⚪）

| 操作 | 详情 |
|---|---|
| `git mv` | `packages/shell/Taskbar` → `packages/shell/shell-taskbar` |
| 更新引用 | `host/BetterDesktop.Host.csproj:46` + 全仓其他 csproj 引用路径 |
| 不变 | 命名空间 `BetterDesktop.Shell.Taskbar`、程序集名 `BetterDesktop.Shell.Taskbar` |

### 6.8 违规8：host 显式引用（⚪）

| 文件 | 新增 ProjectReference |
|---|---|
| `host/BetterDesktop.Host.csproj` | `..\packages\shell\shell-app-source\BetterDesktop.Shell.AppSource.csproj` |
| 同上 | `..\packages\shell\shell-window-tracker\BetterDesktop.Shell.WindowTracker.csproj` |
| 同上 | `..\packages\shell\shell-pinning\BetterDesktop.Shell.Pinning.csproj` |
| 同上 | `..\packages\shell\shell-calendar\BetterDesktop.Shell.Calendar.csproj` |

### 6.9 性能工作流（独立节，见 §6.10）

## 7. Implementation Sequence

按依赖排序，任一步后停下树仍一致（可构建、可启动）。每步完成后跑 `dotnet build` + 相关门禁。

### Phase A：🔴 红色违规（架构红线，先修）

**Step 1 — 违规2：DiagnosticLog 移除 + FileLogSink**（先于违规1，因为违规1 的 EmitAsync 异常需走内核日志）
1. 新建 `host/FileLogSink.cs`（纯 BCL，线程安全，日期滚动，catch(IOException)）。
2. `host/Bootstrap.cs:41-44`：Console.WriteLine → FileLogSink。
3. `host/Bootstrap.cs`：20+ 处 DiagnosticLog.Trace → context.Logger.Info/Warn（批量替换）。
4. `packages/kernel/kernel/Core/PluginHandle.cs:192`：DiagnosticLog.Trace → _context.Logger.Error。
5. 删除 `packages/kernel/kernel/Core/DiagnosticLog.cs`。
6. `dotnet build` 验证 0 错误 0 警告。
7. 验证：启动后 `%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log` 有内容；桌面无 `BetterDesktop_debug.log` 生成。

**Step 2 — 违规1：跨程序集 event → IEventBus**
1. 新建 `packages/shell/shell-core/ShellEvents.cs`（事件名常量）。
2. `ISettingsService.cs`：移除 `Changed` event；`SettingsService` 构造函数注入 IContext，Set 中 EmitAsync。
3. `IAppearanceService.cs`：移除 `Changed` event；实现类注入 IContext，setter/NotifyChanged 中 EmitAsync。
4. 逐个迁移订阅方（DesktopPlugin、DesktopWindow、DesktopIconsControl、DockWindow×2、QuickNotePlugin、MenuBarPlugin、Bootstrap NativeTaskbar）为 Effect 托管的 On 注册。
5. 新建 `scripts/verify-no-cross-assembly-event.ps1` + `.Tests.ps1`，登记进 run-gates.ps1。
6. `dotnet build` + `pwsh scripts/run-gates.ps1 -Filter no-cross-assembly-event` 验证。
7. 端到端验证：修改设置 → 所有订阅方收到回调；卸载插件 → 无幽灵回调（日志无 ObjectDisposedException / NullReferenceException）。

### Phase B：🟠 橙色违规（架构对齐）

**Step 3 — 违规3：HMR ADR 追认**
1. 新建 `docs/architecture/ADR-003.md`（含 HMR 追认 + TFM 决策 + 桌面主窗口归属）。
2. 修订 ADR-002 D1 排除清单、architecture.md 内核要点表、STATUS.md。
3. 确认 verify-architecture-guard.ps1 注释与 ADR-003 D2 一致。
4. `pwsh scripts/run-gates.ps1 -Filter md-links,md-wrap,doc-budgets,agent-note` 验证文档门禁。

**Step 4 — 违规4：cordis.yml 接线**（依赖 Step 3 ADR-003 追认 loader 方向）
1. `git log --all --full-history -- cordis.yml` 检查历史中是否有被删的 cordis.yml。
2. 新建 `host/cordis.yml`（按当前 Bootstrap.cs 顺序声明 ~20 插件）。
3. `host/BetterDesktop.Host.csproj`：cordis.yml 设为 Content CopyToOutputDirectory。
4. `LoaderService.cs:72`：sync-over-async → async await。
5. `host/Bootstrap.cs`：构建 LoaderOptions + Factories，替换硬编码 Plugin 调用；保留 Provide/NativeTaskbar/Exit 钩子/MenuCommandPipe。
6. 审计各插件 Inject 声明，补全时序敏感依赖。
7. `dotnet build` + 启动验证：所有插件状态为 Active（与迁移前一致）；插件加载顺序日志可比对。

### Phase C：🟡 黄色违规（文档治理）

**Step 5 — 违规5：5 包 README**
1. 逐个新建 5 个 README.md（四小节格式）。
2. `pwsh scripts/run-gates.ps1 -Filter package-readme` 验证全绿。

### Phase D：⚪ 白色违规（工程整洁）

**Step 6 — 违规6：TFM 文档修正**（并入 Step 3 ADR-003 D3，若已在 Step 3 完成则跳过）
1. `docs/coding-standards.md:8`：允许版本化 TFM。

**Step 7 — 违规7：Taskbar 目录重命名**
1. `git mv packages/shell/Taskbar packages/shell/shell-taskbar`。
2. 全仓更新引用路径（host csproj + 其他 csproj）。
3. `dotnet build` 验证。

**Step 8 — 违规8：host 显式引用**
1. `host/BetterDesktop.Host.csproj`：新增 4 个 ProjectReference。
2. `dotnet build` 验证无 NU1506。

### Phase E：性能工作流

**Step 9 — 建立性能基线（establish-baseline）**
1. 定义 Measurement contract（revision + deps + runtime + config + workload + raw samples）。
2. 在受控/短时/测试入口下采集基线数据（启动时间、UI 帧率、内存、CPU）。
3. 无法安全运行时交付「最小可复跑实验合同」。
4. 详见 §8 性能工作流节。

**Step 10 — 全量门禁 + 端到端走查**
1. `pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1` 全量 14 道门禁全绿。
2. `dotnet test` 全绿。
3. 端到端场景走查（见 §13 D1）。

## 8. Performance Optimization Workflow（性能工作流）

> **声明**：「无测量，不结论；无同合同证据，不授权。」本节定义工作流与候选调查区域，**不预设任何收益**。所有优化须经 establish-baseline → diagnose → optimize → guard 四阶段递进，不得跳级。

### 8.1 工作流阶段

| 阶段 | 目标 | 产出 | 进入下一阶段条件 |
|---|---|---|---|
| **establish-baseline** | 建立可复跑的性能基线 | Measurement contract + raw samples + 基线报告 | 基线数据可复跑（同合同 ≥3 次变异系数 < 10%） |
| **diagnose** | 定位瓶颈机制 | profile 数据 + 瓶颈假设 + 影响半径 | 瓶颈机制有证据链（profile + 源码 + 可复现实验） |
| **optimize** | 单变量优化验证 | A/B 对比数据 + 优化补丁 + 回归验证 | 同合同 A/B 有统计显著差异且无回归 |
| **guard** | 性能回归守卫 | benchmark 门禁 + 阈值 + CI 集成 | 门禁可执行且有变红物证 |

### 8.2 关注指标

| 指标 | 测量方法 | 目标 |
|---|---|---|
| UI 帧率/卡顿 | WPF 渲染线程 FPS 计数器 / Perception 帧时间 | 桌面交互 ≥60fps，无 >100ms 卡顿 |
| 启动时间 | 进程启动 → 首帧渲染（Stopwatch + 日志时间戳） | 冷启动 < 3s（目标值待基线确认）[assumed] |
| 内存（增长/泄漏） | 启动后 idle 5min 内存快照对比 / dotnet-dump | idle 内存无持续增长；插件卸载后内存可回收 |
| CPU 占用 | idle / 交互时 CPU 采样（dotnet-trace / 任务管理器） | idle CPU < 2% [assumed] |
| 稳定性（崩溃/挂起） | 崩溃日志计数 / 挂起检测（UI 线程响应） | 24h soak 0 崩溃 0 挂起 [assumed] |

### 8.3 安全约束

- BetterDesktop 为 WPF 桌面 shell，运行时会接管用户桌面（隐藏任务栏、自绘桌面、AppBar 注册）。
- 性能测量须在**受控/短时/测试入口**下进行：
  - 优先使用测试入口（如 `--headless` / `--benchmark` 模式，若不存在则需新增 [assumed]）。
  - 短时测量（<5min）后立即退出，恢复原生任务栏。
  - 测量机为独立环境或虚拟机，不影响用户日常使用。
- **无法安全运行时**：交付「最小可复跑实验合同」（Measurement contract 模板 + 预期 workload + 采集命令），由用户在受控环境执行后回传数据。
- 禁止在用户日常使用的桌面环境上长时间运行性能测试（可能导致桌面不可用）。

### 8.4 候选调查区域（基于静态分析预判，**非已授权收益**）

以下区域基于源码静态分析预判为可能的性能热点，须经 diagnose 阶段验证后方可进入 optimize：

| # | 区域 | 预判依据 | 关联技术力文档 |
|---|---|---|---|
| C1 | 自绘桌面渲染（shell-desktop） | DesktopWindow 全屏 WPF 窗口 + 壁纸 + 图标瀑布列；WPF 全屏透明窗口渲染开销大 | 63 desktop-progman-embed |
| C2 | DWM 缩略图注册/注销 | 若 shell-desktop 或 dock 使用 DwmRegisterThumbnail，未成对注销导致资源泄漏 | 63 dwm-live-thumbnail / 7434 dwm-thumbnail 声明纪律 |
| C3 | 设置变更事件广播 | 违规1 修复前裸 event 可能导致多次重复回调；修复后 EmitAsync 并发广播需确认无风暴 | 2601 / 7438 |
| C4 | 定时器/动画 | WPF 动画 + 可能的 DispatcherTimer 未成对；桌面图标悬停动画 | 7414 timer-driven-ui-animation |
| C5 | 插件加载串行阻塞 | LoaderService.cs:72 sync-over-async（违规4 修复项）；Bootstrap 硬编码串行装配 | — |
| C6 | 内存治理器采样 | HmrManager ResourceGovernor 5s 采样（Bootstrap.cs:88-89）；确认采样开销可忽略 | — |
| C7 | 窗口追踪轮询 | shell-window-tracker 可能使用 SetWinEventHook 或轮询；确认 OUTOFCONTEXT 泵线程纪律 | 7435 winevent-outofcontext-message-pump / 7432 fullscreen-foreground-detection |
| C8 | 任务栏外观 COM 注入 | shell-taskbar TaskbarAppearancePlugin 可能注入 COM 控制任务栏外观；确认注入/还原成对 | 7436 taskbar-appearance-service-injection / 7416 accent-policy-application |
| C9 | 日志 I/O | FileLogSink（违规2 修复项）若同步写文件可能阻塞调用线程；需确认异步 flush 或批量写 | — |
| C10 | 图标缓存/预加载 | shell-app-source 图标提取 + 缓存；确认 LRU 淘汰 + 后台预加载不阻塞 UI | 502 多尺寸图标 LRU 缓存 / 503 后台图标预加载 |

### 8.5 Measurement Contract 模板要素

每个性能实验须记录以下要素（同合同 = 除被测变量外全部一致）：

```
revision: <git commit sha>
deps: <.NET SDK version / 操作系统版本 / 硬件配置>
runtime: <启动参数 / 环境变量 / 配置文件>
config: <被测配置项（如 components.dock=true）>
workload: <操作序列（如 启动→等待30s→打开开始菜单→关闭→等待30s→退出）>
raw samples: <原始数据文件路径 / 采样次数 / 采样间隔>
```

### 8.6 性能工作流与违规修复的关系

- 违规1（event 总线）、违规2（日志单管道）、违规4（cordis.yml + async）的修复本身可能影响性能（C3/C5/C9），须在修复后重新 establish-baseline。
- 违规修复完成后，性能工作流从 Step 9（establish-baseline）独立执行，不与违规修复混在同一提交。
- 性能优化的具体补丁不在本计划 scope 内（本计划仅定义工作流与候选区域），优化实施须另开计划或在 diagnose 阶段后按单变量优化执行。

## 9. Risk and Impact Analysis

### 9.1 高风险符号

| 符号 | 风险 | 缓解 |
|---|---|---|
| `ISettingsService.Set<T>` | 改为 async 后影响所有调用方（Set 调用点多） | 评估调用方：若大部分在 UI 线程，fire-and-forget + 异常记录可能更安全；进 §12 开放问题 |
| `IAppearanceService` 属性 setter | 每个属性 setter 改为 EmitAsync，WPF 绑定可能触发大量事件 | 合并去抖：setter 中标记 dirty，Dispatcher.BeginInvoke 批量 Emit（进 §12 可选增强） |
| `Bootstrap.Build()` | 违规4 重构装配逻辑，可能改变插件加载顺序导致 Pending 死锁 | 迁移期 cordis.yml 顺序与硬编码完全一致；Inject 审计确保依赖声明完整；加载日志可比对 |
| `LoaderService.LoadAsync` | 改为 async 后，host 调用方须 await | Bootstrap 中 `context.Plugin(new LoaderService(...)).AwaitAsync()` 已支持 await |
| `FileLogSink` | 并发写文件可能损坏日志 | lock 或 ConcurrentQueue + 后台 flush；Dispose flush |
| `git mv Taskbar` | 重命名目录可能导致其他 csproj 引用断裂 | 全仓 grep `packages/shell/Taskbar` 更新所有引用；build 验证 |

### 9.2 下游消费者（d=1 直接依赖）

违规1 影响的直接消费者（订阅方）：
- shell-desktop（DesktopPlugin / DesktopWindow / DesktopIconsControl）
- shell-dock（DockWindow）
- shell-quick-note（QuickNotePlugin）
- shell-menu-bar（MenuBarPlugin）
- host（Bootstrap NativeTaskbar）

违规2 影响的直接消费者：
- kernel（PluginHandle）
- host（Bootstrap 20+ 处）

违规4 影响的直接消费者：
- 所有插件（加载方式从硬编码改为 cordis.yml）
- host（Bootstrap 重构）

### 9.3 兼容性风险

| 风险 | 等级 | 说明 |
|---|---|---|
| ISettingsService 接口签名变更 | 中 | 移除 Changed event + Set 可能改 async；非内核冻结面（shell 契约），但调用方多 |
| IAppearanceService 接口签名变更 | 中 | 移除 Changed event；属性 setter 行为不变（内部 Emit） |
| cordis.yml 装配顺序变化 | 高 | 若 Inject 声明不完整，插件可能停在 Pending；迁移期保序 + 加载日志比对 |
| FileLogSink 路径变化 | 低 | 日志从桌面移到 %LocalAppData%，用户可能找不到日志；文档说明 |
| Taskbar 目录重命名 | 低 | git mv 保留历史；命名空间不变；仅路径引用更新 |
| host 新增 ProjectReference | 低 | 仅显式化已有传递依赖；无功能变化 |

### 9.4 并发/线程风险

- IEventBus.EmitAsync 为并发广播（emit 模式），订阅方 handler 可能在后台线程执行；WPF UI 更新须经 Dispatcher 封送（coding-standards 禁止清单第4条）[verified]。
- FileLogSink 多线程并发写须线程安全 [inferred]。
- SettingsService.Set 在 UI 线程调用，EmitAsync 若 await 可能阻塞 UI 线程；fire-and-forget 更安全但异常需记录 [inferred]。

### 9.5 可观测性要求

- 违规1 迁移后：插件加载/卸载日志须包含事件订阅注册/注销信息（便于排查幽灵回调）[inferred]。
- 违规2 后：所有 DiagnosticLog 信息须可在 host-yyyyMMdd.log 中找到（tag 映射为消息前缀）[inferred]。
- 违规4 后：LoaderService Reports 须输出每个插件的加载状态（Loaded/Failed/Skipped/UnknownFactory）[verified]。

### 9.6 他人未提交改动风险

- 工作区有 205 项他人未提交改动 [verified]。本计划所有改动须在独立分支或独立提交中进行，**禁止覆盖/回滚/修改他人改动**。
- 若他人改动涉及同一文件（如 Bootstrap.cs、ISettingsService.cs），实施时须 rebase 或 merge 后手动解决冲突，不得强制覆盖 [inferred]。
- 建议：实施前 `git stash` 他人改动（需他人同意）或在独立分支工作，完成后合并 [assumed]。

## 10. Files Expected to Change

| 文件 | 符号/改动 | 原因 | 违规 |
|---|---|---|---|
| `packages/shell/shell-core/ShellEvents.cs` | 新建 | 事件名常量类 | 1 |
| `packages/shell/shell-settings/Contracts/ISettingsService.cs` | 移除 Changed event (L28) | 跨程序集 event 违规 | 1 |
| `packages/shell/shell-settings/Services/SettingsService.cs` | 注入 IContext + Set 中 EmitAsync | event → 总线 | 1 |
| `packages/shell/shell-core/Surface/IAppearanceService.cs` | 移除 Changed event (L120) | 跨程序集 event 违规 | 1 |
| `packages/shell/shell-core/Surface/AppearanceService.cs`（或实现类） | 注入 IContext + setter EmitAsync | event → 总线 | 1 |
| `packages/shell/shell-desktop/DesktopPlugin.cs` | 订阅迁移 (L106) | Effect 托管 On | 1 |
| `packages/shell/shell-desktop/DesktopWindow.cs` | 订阅迁移 (L201) | 同上 | 1 |
| `packages/shell/shell-desktop/DesktopIconsControl.cs` | 订阅迁移 (L135) | 同上 | 1 |
| `packages/shell/shell-dock/DockWindow.xaml.cs` | 订阅迁移 (L200, L347) | 同上 | 1 |
| `packages/shell/shell-quick-note/QuickNotePlugin.cs` | 订阅迁移 (L45) | 同上 | 1 |
| `packages/shell/shell-menu-bar/MenuBarPlugin.cs` | 订阅迁移 (L75) | 同上 | 1 |
| `host/Bootstrap.cs` | 订阅迁移 (L209) + DiagnosticLog 替换 + cordis.yml 重构 | 多项 | 1,2,4 |
| `scripts/verify-no-cross-assembly-event.ps1` | 新建 | 机器校验门禁 | 1 |
| `scripts/verify-no-cross-assembly-event.Tests.ps1` | 新建 | 门禁单测 | 1 |
| `scripts/run-gates.ps1` | 登记新门禁 (L13-26) | 注册表 | 1 |
| `packages/kernel/kernel/Core/DiagnosticLog.cs` | 删除 | 静态旁路日志 | 2 |
| `packages/kernel/kernel/Core/PluginHandle.cs` | DiagnosticLog → Logger (L192) | 单管道 | 2 |
| `host/FileLogSink.cs` | 新建 | 文件日志 sink | 2 |
| `docs/architecture/ADR-003.md` | 新建 | HMR 追认 + TFM + 桌面归属 | 3,6 |
| `docs/architecture/ADR-002.md` | 修订 D1 排除清单 | HMR 追认 | 3 |
| `docs/architecture.md` | 修订内核要点表 | HMR 标注 | 3 |
| `docs/architecture/STATUS.md` | 修订已完成范围 | HMR + 更新时间 | 3 |
| `host/cordis.yml` | 新建 | 声明式插件树 | 4 |
| `host/BetterDesktop.Host.csproj` | cordis.yml Content + 4 个 ProjectReference + Taskbar 路径 | 多项 | 4,7,8 |
| `packages/kernel/kernel-loader/LoaderService.cs` | sync-over-async → async (L72) | 禁止 sync-over-async | 4 |
| `packages/shell/shell-desktop/README.md` | 新建 | 缺 README | 5 |
| `packages/shell/shell-music/README.md` | 新建 | 缺 README | 5 |
| `packages/shell/shell-plugin-sdk/README.md` | 新建 | 缺 README | 5 |
| `packages/shell/shell-quick-note/README.md` | 新建 | 缺 README | 5 |
| `packages/shell/shell-start-menu/README.md` | 新建 | 缺 README | 5 |
| `docs/coding-standards.md` | 修订 TFM (L8) | 文档与代码一致 | 6 |
| `packages/shell/shell-taskbar/`（git mv 自 Taskbar） | 目录重命名 | 命名规范 | 7 |
| 其他 csproj（引用 Taskbar 路径的） | 更新引用路径 | 目录重命名 | 7 |

## 11. Reusable Implementation Context

### 11.1 证据头

- 固定 commit：`0ecd841`（2026-09-03）
- 目标仓库：`C:\Users\17822\Desktop\betterdt\better-desktop-cordis`
- 工作区状态：205 项未提交改动（他人开发中，禁止覆盖）
- 技术栈：C# / .NET 8 / WPF，TFM `net8.0-windows10.0.19041.0`
- 验证命令：`dotnet build -c Debug`（解决方案根目录）、`pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`、`dotnet test`

### 11.2 内核 API 速查（实现时直接消费，无需重新调研）

**IEventBus**（`packages/kernel/kernel/Contracts/IEventBus.cs`）：
```csharp
IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull;
Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;
// 其他：OnResult / ParallelAsync / SerialAsync / BailAsync / WaterfallAsync
```

**IContext**（`packages/kernel/kernel/Contracts/IContext.cs`）：
```csharp
T? Get<T>() where T : class;
IDisposable Provide<T>(T service) where T : class;
IPluginHandle Plugin(IPlugin plugin);
IDisposable Effect(Func<IDisposable> execute, string? label = null);
IEventBus Events { get; }
IKernelLogger Logger { get; }
```

**IKernelLogger**（`packages/kernel/kernel/Contracts/IKernelLogger.cs`）：
```csharp
void Log(LogLevel level, string message);
void Info(string message);
void Warn(string message);
void Error(string message);
```

**CordisContext 构造**（`packages/kernel/kernel/Core/CordisContext.cs:22`）：
```csharp
public CordisContext(CordisContext? parent = null, Action<LogLevel, string>? logSink = null)
```

**HmrEvents 参考模式**（`packages/kernel/kernel-hmr/HmrEvents.cs`）：
```csharp
public static class HmrEvents {
    public const string Loaded = "kernel.plugin/loaded";
    // ...
}
```

**LoaderOptions**（`packages/kernel/kernel-loader/LoaderOptions.cs`）：
```csharp
public sealed class LoaderOptions {
    public string ConfigPath { get; set; } = "cordis.yml";
    public Dictionary<string, Func<IPlugin>> Factories { get; } = new(StringComparer.Ordinal);
}
```

**LoaderConfig / LoaderEntry**（`packages/kernel/kernel-loader/LoaderConfig.cs`）：
```csharp
public sealed class LoaderConfig {
    public List<LoaderEntry> Plugins { get; set; } = new();
}
public sealed class LoaderEntry {
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
}
```

### 11.3 事件名约定

- 格式：`<域>/<动作>`（ADR-002 D4.2）
- 示例：`shell.settings/changed`、`shell.appearance/changed`、`kernel.plugin/loaded`
- 载荷：强类型 record（如 `SettingsChangedEventArgs`、`AppearanceChangedArgs`）
- 常量类：`ShellEvents`（新建，仿 `HmrEvents`）

### 11.4 订阅迁移模板

```csharp
// 在插件 LoadAsync 中：
context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
    ShellEvents.SettingsChanged,
    (payload, ct) =>
    {
        // 处理逻辑；UI 更新须经 Dispatcher 封送
        return Task.CompletedTask;
    }));
```

### 11.5 门禁开发模板

- 脚本位置：`scripts/verify-*.ps1`
- 单测位置：`scripts/verify-*.Tests.ps1`（Pester）
- 注册表：`scripts/run-gates.ps1` 的 `$gates` 数组
- 公共库：`scripts/lib/common.ps1`（`Get-RepoRoot`、`Get-RelPath`、`Write-GatePass`、`Write-GateFail`）
- 契约：`scripts/AGENTS.md` 第 5 条（每条门禁须有单测覆盖「非法输入 → 返回违规」）

## 12. Assumptions and Open Questions

### 12.1 开放问题（须在实施前或实施中确认）

| # | 问题 | 影响 | 建议处理 |
|---|---|---|---|
| Q1 | `ISettingsService.Set<T>` 是否改为 `async Task SetAsync<T>`？当前为同步 void，EmitAsync 为 async。 | 违规1 接口签名变更范围；调用方数量 | 选项A：Set 改 async Task，所有调用方 await（影响大但正确）。选项B：Set 保持 void，内部 fire-and-forget `_ = EmitAsync(...)` + ContinueWith 记录异常（影响小但可能丢失异常）。推荐选项B（设置变更不阻塞 UI），但须确认调用方语义。进实施时与用户确认。 |
| Q2 | `IAppearanceService` 实现类名与位置？接口在 `shell-core/Surface/IAppearanceService.cs`，实现类可能为 `AppearanceService` 或 `ThemeCenter`。 | 违规1 实施定位 | 实施时 `grep -r "IAppearanceService"` 找实现类；本计划不编造符号名。 |
| Q3 | `DesktopIconsControl`（WPF UserControl）如何获取 IEventBus？UserControl 无插件 LoadAsync，可能通过 DataContext / 服务定位器 / 依赖属性传入。 | 违规1 订阅迁移 | 选项A：UserControl 的 DataContext（ViewModel）持有 IEventBus 引用。选项B：通过 `IContext` 服务定位器（违反禁止清单第1条？静态单例禁止，但从服务获取 IEventBus 实例可行）。推荐选项A，实施时确认 DesktopIconsControl 的 DataContext 类型。 |
| Q4 | cordis.yml 中 HmrManager 工厂如何捕获 `ResourceGovernorOptions` + `OnProcessCritical` 回调？ | 违规4 实施复杂度 | Factory lambda 中内联构建 HmrManager（与当前 Bootstrap.cs:87-105 逻辑一致），lambda 捕获 context + 环境变量。 |
| Q5 | SettingsService 提前 Provide（Bootstrap.cs:65-73）是否迁移为 SettingsPlugin 的 Provide？ | 违规4 装配重构 | 保持提前 Provide（使所有插件 LoadAsync 可取到设置），不迁移；SettingsPlugin 复用此实例（当前注释已说明）。 |
| Q6 | 性能测试入口（`--headless` / `--benchmark`）是否存在？ | 性能工作流 establish-baseline | 实施时检查 host 入口参数；若不存在，性能基线采集须在受控环境手动运行或新增测试入口（不在本计划 scope，进 §12 deferred）。 |
| Q7 | 他人 205 项未提交改动中是否涉及本计划将修改的文件？ | 冲突风险 | 实施前 `git status` + `git diff --name-only` 确认重叠文件；若重叠，与开发者协调或在独立分支工作。 |

### 12.2 假设（[assumed]，须实施时验证）

- A1：shell-music 包当前实现状态为雏形或空（README 内容须读代码后确认）。
- A2：AppearanceService 实现类注入 IContext 后不会引入循环依赖（shell-core 不依赖 shell-settings）。
- A3：FileLogSink 按日期滚动的实现复杂度可控（纯 BCL，无需第三方库）。
- A4：cordis.yml 迁移后插件加载顺序与当前硬编码一致（Inject 声明完整时 Pending 机制自动排序）。
- A5：git mv Taskbar 后全仓引用路径可通过 grep 完整定位（无动态路径拼接）。

### 12.3 Deferred Follow-ups（不扩 scope，相邻重构）

| # | 项 | 原因 |
|---|---|---|
| D1 | ShellEvents 模式稳定后按 tech-knowledge-accumulation skill 沉淀为新功能文档（26-事件总线域，C# 专用） | 技术力回写 |
| D2 | IAppearanceService 属性 setter 合并去抖（dirty flag + Dispatcher.BeginInvoke 批量 Emit） | 性能优化，待性能工作流 diagnose 后决定 |
| D3 | 新增性能测试入口（`--headless` / `--benchmark` 模式） | 性能工作流基础设施，不在本违规修复 scope |
| D4 | 全库约 60 处 event 的完整审计（本计划仅处理 ISettingsService.Changed + IAppearanceService.Changed 两个跨程序集 event；其余同程序集 event 保留，但须确认无其他跨程序集订阅） | verify-no-cross-assembly-event 门禁会自动覆盖 |
| D5 | kernel-hmr 的 HmrEvents 总线用例补充订阅方示例（当前仅 Emit，无 On 订阅示例，可作为 ShellEvents 迁移的参考测试） | 文档/测试增强 |
| D6 | 性能优化具体补丁（待 establish-baseline + diagnose 后按单变量优化执行，另开计划） | 本计划仅定义工作流 |

## 13. Definition of Done

### D1（端到端场景走查 · 必含）

**场景：设置变更驱动全外壳响应 + 插件卸载无幽灵回调**

1. 启动 BetterDesktop（受控环境，短时运行）。
2. 打开设置中心，修改 `components.dock`（关闭 Dock）。
3. 验证：Dock 窗口隐藏 / 原生任务栏显示（NativeTaskbar 联动）；DesktopWindow / DesktopIconsControl / QuickNotePlugin 收到设置变更回调并正确响应。
4. 修改外观设置（如主题色 Accent）。
5. 验证：DockWindow / MenuBarPlugin 收到外观变更回调并重绘。
6. 通过设置中心卸载 QuickNotePlugin（或模拟插件卸载）。
7. 验证：QuickNotePlugin 的事件订阅已注销（Effect 清理器执行）；再次修改设置后日志无 ObjectDisposedException / NullReferenceException / 幽灵回调。
8. 退出 BetterDesktop，验证原生任务栏恢复显示（Application.Exit 钩子执行）。
9. 检查 `%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log`：包含启动/设置变更/插件卸载/退出的完整日志；桌面无 `BetterDesktop_debug.log`。

**通过标准**：步骤 3/5/7/9 全部验证通过，无异常日志。

### D2（内核逻辑单测计划）

| 测试项 | 场景 | 预期 |
|---|---|---|
| D2.1 | SettingsService.Set 触发 IEventBus.EmitAsync | mock IEventBus，Set 后 Verify EmitAsync 被调用一次，载荷 Key/Value 正确 |
| D2.2 | SettingsService.Set 不触发 Changed event | 接口已移除 event，编译期保证 |
| D2.3 | AppearanceService 属性 setter 触发 EmitAsync | mock IEventBus，设置 Accent 后 Verify EmitAsync 被调用，载荷 AccentChanged=true |
| D2.4 | 事件订阅 Effect 托管清理 | 注册 On → Dispose Effect → Emit →  handler 不被调用 |
| D2.5 | 单监听器异常隔离 | 一个 handler 抛异常，其余 handler 仍被调用；异常入 Logger |
| D2.6 | FileLogSink 写文件 | 调用 logSink → 文件存在且内容正确；并发写不损坏 |
| D2.7 | FileLogSink IOException 降级 | mock File.AppendAllText 抛 IOException → 不阻断主流程，无裸 catch |
| D2.8 | LoaderService async 加载 | cordis.yml 含 3 插件 → LoadAsync 后 Reports 均为 Loaded；无 sync-over-async 死锁 |
| D2.9 | LoaderService 未知工厂 fail-closed | cordis.yml 含未知工厂名 → Reports 含 UnknownFactory，不抛异常 |
| D2.10 | LoaderService cordis.yml 解析失败 | 无效 YAML → Logger.Error + throw |
| D2.11 | verify-no-cross-assembly-event 门禁 | 含跨程序集 event 的样例代码 → 门禁返回违规；同程序集 WPF event → 通过 |

**测试框架**：xUnit（项目已有），mock 用 Moq 或 NSubstitute（确认项目测试依赖）[assumed]。测试工程：`packages/kernel/kernel-tests/`（内核逻辑）+ `packages/shell/shell-settings-tests/`（设置服务）+ `packages/shell/shell-core-tests/`（外观服务）。

### D3（门禁全绿验证）

1. `pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`：全量 14 道门禁（原 13 道 + 新增 no-cross-assembly-event）全部 PASS。
2. 新增门禁 `verify-no-cross-assembly-event.ps1` 有对应 `.Tests.ps1`（Pester），覆盖「非法输入 → 返回违规」。
3. `verify-package-readme`：5 个缺 README 的包已补，allowlist 仍为 0。
4. `verify-gate-registry`：新门禁已登记，注册表一致。
5. `verify-md-links` / `verify-md-wrap` / `verify-doc-budgets`：ADR-003 + 文档修订通过。

### D4（构建与测试）

1. `dotnet build -c Debug`：0 错误 0 警告（`TreatWarningsAsErrors=true`）。
2. `dotnet test`：全部测试通过（含 D2 新增测试）。
3. 无 `DiagnosticLog` 引用（全仓 grep 0 匹配，除 backups/ 目录）。
4. 无跨程序集 `event` 声明在 `Contracts/` 接口中（verify-no-cross-assembly-event 门禁保证）。

### D5（cordis.yml 装配验证）

1. `host/cordis.yml` 存在且包含当前全部插件条目。
2. 启动后 `context.GetHandles()` 列举的插件状态与迁移前一致（全部 Active，无新增 Failed/Pending）。
3. 插件加载顺序日志可比对（LoaderService Reports 输出）。
4. `LoaderService.cs` 无 `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`（sync-over-async 消除）。

### D6（工程整洁验证）

1. `packages/shell/shell-taskbar/` 目录存在（原 `Taskbar/` 已 git mv）。
2. 全仓无 `packages/shell/Taskbar` 路径引用（grep 0 匹配，除 .git 历史）。
3. `host/BetterDesktop.Host.csproj` 包含 4 个新增 ProjectReference（app-source / window-tracker / pinning / calendar）。
4. `docs/coding-standards.md:8` TFM 描述与代码一致（`net8.0-windows10.0.19041.0`）。
5. ADR-003 存在且内容完整（HMR 追认 + TFM + 桌面归属）；ADR-002 / architecture.md / STATUS.md 已同步修订。

### D7（性能工作流交付）

1. Measurement contract 模板已定义（revision + deps + runtime + config + workload + raw samples）。
2. 候选调查区域清单（C1-C10）已记录，标注为「非已授权收益」。
3. 若 establish-baseline 已执行：基线数据文件存在且可复跑（同合同 ≥3 次）。
4. 若无法安全运行：「最小可复跑实验合同」已交付（含采集命令 + 预期 workload）。

## 14. Handoff to 技术力应用（交接节，必填）

> 计划就绪即本 skill 终点；实现由 `skills/ability-reuse-alignment/SKILL.md` 按本节执行。本节是它唯一需要的输入，必须可独立执行、零重新调研。计划 agent 禁止顺手写实现代码。

### 14.1 模式判定

| 功能点 | 模式 | 判定依据 |
|---|---|---|
| 违规1：IEventBus 迁移（ShellEvents + 订阅方改造） | `无匹配→工程代码权威` | 技术力 2601 为 TypeScript 实现，非 C# 可注入代码；内核 IEventBus 已自实现（ADR-002 D4 冻结面），HmrEvents 为工程内参考模式。以工程代码（IEventBus 接口 + HmrEvents 常量类 + 各订阅方源码）为权威执行。 |
| 违规1：verify-no-cross-assembly-event 门禁 | `无匹配→工程代码权威` | 技术力库无 PowerShell 门禁文档；以工程内现有门禁（verify-package-readme.ps1 / verify-host-log-sink.ps1）为模板参考。 |
| 违规2：FileLogSink + DiagnosticLog 移除 | `无匹配→工程代码权威` | 技术力库无 C# 文件日志 sink 文档；以工程内 IKernelLogger 接口 + CordisContext logSink 参数为权威。 |
| 违规3：ADR-003 撰写 | `无匹配→工程代码权威` | ADR 为项目治理文档，以工程内 ADR-001/002 格式 + .agents/notes/implemented/feature/2026-08-20-hmr-hot-reload.md 内容为权威。 |
| 违规4：cordis.yml 接线 | `命中但不可用→工程代码权威（第三态）` | 技术力 2401（插件系统）为 TypeScript valibot manifest，范式可参考但代码不可注入；工程内 LoaderService/LoaderOptions/LoaderConfig 已完整实现，以工程代码为权威。2401 列入「禁区」不注入代码，仅借鉴「声明优先于硬编码」原则。 |
| 违规5：5 包 README | `无匹配→工程代码权威` | 以工程内 shell-dock/README.md 四小节模板为权威。 |
| 违规6-8：文档/目录/引用修正 | `无匹配→工程代码权威` | 纯工程整洁操作，以当前源码状态为权威。 |
| 性能工作流 | `标准文档注入` | doubao-coding-optimize-performance skill 为标准流程文档（establish-baseline → diagnose → optimize → guard），注入其工作流阶段定义与 Measurement contract 要素。不注入代码。 |

### 14.2 注入清单（按应用 skill「调用协议」需完整注入的文档路径）

| # | 文档路径 | 注入内容 | 用途 |
|---|---|---|---|
| 1 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Contracts\IEventBus.cs` | 完整接口签名（On/EmitAsync/五种分发） | 违规1 迁移的 API 权威 |
| 2 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Contracts\IContext.cs` | 完整接口签名（Get/Provide/Plugin/Effect/Events/Logger） | 违规1/2/4 的服务图权威 |
| 3 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Contracts\IKernelLogger.cs` | 完整接口签名（Log/Info/Warn/Error） | 违规2 日志单管道权威 |
| 4 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel-hmr\HmrEvents.cs` | 事件名常量类模式 | 违规1 ShellEvents 的参考模板 |
| 5 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel-loader\LoaderOptions.cs` | LoaderOptions + Factories 字典 | 违规4 cordis.yml 接线 |
| 6 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel-loader\LoaderConfig.cs` | LoaderConfig + LoaderEntry（id/name/enabled） | 违规4 cordis.yml schema |
| 7 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel-loader\LoaderService.cs` | 完整实现（含 LoadAsync/UnloadAsync） | 违规4 async 改造 + host 调用方式 |
| 8 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\shell\shell-settings\Contracts\ISettingsService.cs` | 当前接口（含 Changed event L28） | 违规1 改造对象 |
| 9 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\shell\shell-core\Surface\IAppearanceService.cs` | 当前接口（含 Changed event L120 + AppearanceChangedArgs） | 违规1 改造对象 |
| 10 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\host\Bootstrap.cs` | 完整文件（20+ DiagnosticLog + 硬编码 Plugin + NativeTaskbar 订阅） | 违规1/2/4 的核心改造文件 |
| 11 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\PluginHandle.cs` | L180-195（DiagnosticLog.Trace L192） | 违规2 改造点 |
| 12 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\DiagnosticLog.cs` | 完整文件（待删除） | 违规2 删除对象 |
| 13 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\scripts\run-gates.ps1` | 注册表格式（$gates 数组） | 违规1 新门禁登记 |
| 14 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\scripts\verify-package-readme.ps1` | 门禁脚本模板（Get-*Violation 函数 + dot-source 模式） | 违规1 新门禁开发模板 |
| 15 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\scripts\lib\common.ps1` | 公共函数（Get-RepoRoot/Get-RelPath/Write-GatePass/Write-GateFail） | 门禁开发依赖 |
| 16 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\shell\shell-dock\README.md` | 四小节模板 | 违规5 README 模板 |
| 17 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\docs\architecture\ADR-001.md` | ADR 格式 + 宪法七条红线 | 违规3 ADR-003 格式参考 |
| 18 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\docs\architecture\ADR-002.md` | D1 冻结面 + v1 排除清单 | 违规3 修订对象 |
| 19 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\.agents\notes\implemented\feature\2026-08-20-hmr-hot-reload.md` | HMR 决策内容 | 违规3 ADR-003 内容来源 |
| 20 | `C:\Users\17822\Desktop\betterdt\better-desktop-cordis\host\BetterDesktop.Host.csproj` | 当前 ProjectReference 列表 + TFM | 违规4/7/8 改造对象 |
| 21 | `C:\Users\17822\AppData\Local\Doubao\User Data\Default\.doubao\agent_mode\workspace\.skills\doubao-coding-optimize-performance\SKILL.md` | 性能优化工作流（establish-baseline/diagnose/optimize/guard + Measurement contract） | 性能工作流标准流程注入 |
| 22 | `C:\Users\17822\Desktop\betterdt\TECH-KNOWLEDGE\74-Windows内部接口逆向\7438-event-subscription-unsubscribe-discipline.md` | 事件订阅退订生死线（+=必须-= / Dispose 幂等） | 违规1 生死线约束 |

**生死线清单**（复合功能，必须逐条遵守）：
- L1：每个 `events.On<T>()` 注册必须经 `context.Effect()` 托管或在 `UnloadAsync` 中显式 Dispose（7438）。
- L2：EmitAsync handler 中 UI 更新须经 Dispatcher 封送（coding-standards 禁止清单第4条）。
- L3：FileLogSink 禁止裸 catch，仅 `catch(IOException)`（coding-standards 禁止清单第5条）。
- L4：LoaderService 禁止 sync-over-async（coding-standards 五.2）。
- L5：禁止覆盖/回滚他人 205 项未提交改动。
- L6：ISettingsService / IAppearanceService 接口移除 event 后，所有订阅方必须同步迁移，不得残留 `+=` 订阅（编译错误会暴露，但须确认无反射订阅）。

### 14.3 适配参数

| 参数 | 值 | 说明 |
|---|---|---|
| 目标语言 | C#（.NET 8 / WPF） | 全部生产代码 |
| 脚本语言 | PowerShell 7（pwsh） | 门禁脚本 |
| 文档语言 | 中文 | ADR / README / 计划 |
| 根命名空间 | `BetterDesktop.*` | 遵循 TERMINOLOGY.md |
| ShellEvents 命名空间 | `BetterDesktop.Shell.Core` | 放在 shell-core 包（事件名常量跨 shell 包共享） |
| FileLogSink 命名空间 | `BetterDesktop.Host` | 放在 host 项目 |
| ShellEvents 事件名 | `shell.settings/changed`、`shell.appearance/changed` | 遵循「域/动作」约定（ADR-002 D4.2） |
| FileLogSink 路径 | `%LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log` | 按日期滚动；目录不存在则创建 |
| cordis.yml 路径 | `host/cordis.yml`（CopyToOutputDirectory=PreserveNewest） | LoaderOptions.ConfigPath 默认 "cordis.yml" |
| TFM | `net8.0-windows10.0.19041.0` | 不改代码，仅改文档 |
| 任务栏目录 | `packages/shell/shell-taskbar/` | git mv 自 `packages/shell/Taskbar/`；命名空间 `BetterDesktop.Shell.Taskbar` 不变 |
| 新增门禁 ID | `no-cross-assembly-event` | 脚本 `verify-no-cross-assembly-event.ps1` |
| 构建命令 | `dotnet build -c Debug` | 解决方案根目录 |
| 测试命令 | `dotnet test` | 全部测试工程 |
| 门禁命令 | `pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1` | 全量门禁 |
| 依赖版本 | YamlDotNet（kernel-loader 已引用） | cordis.yml 解析已用，无需新增 |
| 禁止新增依赖 | 内核零第三方运行时依赖（ADR-002 D2） | FileLogSink 纯 BCL |

### 14.4 禁区（不可触碰符号 / 第三态命中的占位/过时文档）

| 禁区项 | 原因 |
|---|---|
| `packages/kernel/kernel/Contracts/IEventBus.cs` 接口签名 | ADR-002 D1 冻结面，禁止修改 |
| `packages/kernel/kernel/Contracts/IContext.cs` 接口签名 | ADR-002 D1 冻结面，禁止修改 |
| `packages/kernel/kernel/Contracts/IPlugin.cs` / `IPluginHandle.cs` | ADR-002 D1 冻结面，禁止修改 |
| `packages/kernel/kernel/Contracts/IKernelLogger.cs` 接口签名 | ADR-002 D2 冻结面，禁止修改 |
| 技术力 2601（reactive-event-bus）代码 | TypeScript 实现，不可注入 C# 代码；仅范式参考 |
| 技术力 2401（plugin-sdk-system）代码 | TypeScript valibot manifest，不可注入 C# 代码；仅「声明优先」原则参考 |
| `backups/` 目录下所有文件 | 历史备份，禁止修改 |
| 他人 205 项未提交改动 | 禁止覆盖/回滚/修改 |
| `docs/coding-standards.md` 禁止清单 | 规则本身不可放宽（仅 TFM 描述修正，违规6） |
| `readme-ratchet.baseline.json` allowlist | 保持 0，禁止放宽（违规5） |
| WPF 同程序集控件事件（ToggleSwitch.Click 等） | 不属于跨程序集 event，保留不迁移 |

### 14.5 DoD 核销表（实现完成后逐条勾销）

| DoD 编号 | 验证方式 | 状态 |
|---|---|---|
| D1 | 端到端场景走查（设置变更→全外壳响应→插件卸载无幽灵回调→日志检查），手动执行 + 日志物证 | ☐ |
| D2.1 | xUnit 测试：SettingsService.Set 触发 EmitAsync（mock IEventBus Verify） | ☐ |
| D2.2 | 编译期保证：ISettingsService 无 Changed event | ☐ |
| D2.3 | xUnit 测试：AppearanceService setter 触发 EmitAsync | ☐ |
| D2.4 | xUnit 测试：Effect 托管 On 注册，Dispose 后 handler 不被调用 | ☐ |
| D2.5 | xUnit 测试：单监听器异常隔离（IEventBus 内核已有测试，确认覆盖） | ☐ |
| D2.6 | xUnit 测试：FileLogSink 写文件 + 并发安全 | ☐ |
| D2.7 | xUnit 测试：FileLogSink IOException 降级（不阻断 + 无裸 catch） | ☐ |
| D2.8 | xUnit 测试：LoaderService async 加载（无死锁，Reports 正确） | ☐ |
| D2.9 | xUnit 测试：LoaderService 未知工厂 fail-closed | ☐ |
| D2.10 | xUnit 测试：LoaderService cordis.yml 解析失败（Logger.Error + throw） | ☐ |
| D2.11 | Pester 测试：verify-no-cross-assembly-event 门禁（非法输入→违规） | ☐ |
| D3.1 | `pwsh scripts/run-gates.ps1` 全量 14 道门禁 PASS | ☐ |
| D3.2 | 新门禁有 .Tests.ps1 且覆盖非法输入 | ☐ |
| D3.3 | verify-package-readme：5 包已补，allowlist=0 | ☐ |
| D3.4 | verify-gate-registry：新门禁已登记 | ☐ |
| D3.5 | md-links/md-wrap/doc-budgets：ADR-003 + 文档修订通过 | ☐ |
| D4.1 | `dotnet build -c Debug` 0 错误 0 警告 | ☐ |
| D4.2 | `dotnet test` 全部通过 | ☐ |
| D4.3 | 全仓 grep `DiagnosticLog` 0 匹配（除 backups/） | ☐ |
| D4.4 | Contracts/ 接口中无跨程序集 `event` 声明（门禁保证） | ☐ |
| D5.1 | host/cordis.yml 存在且含全部插件条目 | ☐ |
| D5.2 | 启动后插件状态与迁移前一致（无新增 Failed/Pending） | ☐ |
| D5.3 | LoaderService Reports 输出可比对 | ☐ |
| D5.4 | LoaderService.cs 无 sync-over-async（grep `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`） | ☐ |
| D6.1 | packages/shell/shell-taskbar/ 目录存在（git mv） | ☐ |
| D6.2 | 全仓无 `packages/shell/Taskbar` 路径引用（除 .git） | ☐ |
| D6.3 | host csproj 含 4 个新增 ProjectReference | ☐ |
| D6.4 | coding-standards.md TFM 与代码一致 | ☐ |
| D6.5 | ADR-003 存在且完整；ADR-002/architecture.md/STATUS.md 已同步 | ☐ |
| D7.1 | Measurement contract 模板已定义 | ☐ |
| D7.2 | 候选调查区域 C1-C10 已记录（标注非已授权收益） | ☐ |
| D7.3 | 基线数据已采集（若可安全运行）或「最小可复跑实验合同」已交付 | ☐ |

---

*本计划于 2026-09-07 基于 commit 0ecd841 撰写。实现交由 `skills/ability-reuse-alignment/SKILL.md` 按 §14 交接节执行。计划 agent 不实现任何代码。*
