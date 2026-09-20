# 剩余功能批次落地计划（2026-09-15）

> Task: 按 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md) 落地剩余功能批次。已核销项（截图主体、OCR 引擎字段/动词/搜索 + L1）跳过，只落地未完成面。
> 证据基于 commit `b805f6e43c126b858d46988f3a9400737e997959` 验证；技术力文档命中：`3101` 全局热键 / `401` Toast 通知监听 / `2901` 屏幕采集 / `3002`+`3004` PaddleOCR+ONNX 工厂 / `301-305` 灵动岛参考 / `3302` Electron 穿透 / `island-hover-expand-state` / `1408` 单实例 / `7413` 热键注册纪律 / `7426` 透明悬浮窗 / `拆析-灵动岛-动态岛桌面浮层`。

## 1. Objective

按批次索引的阶段顺序（P0 → P5），把「未落地面」落成可交付代码。每篇功能文档自带判位/设计/红线/DoD/交接节，本计划只做**状态收敛 + 顺序编排 + 机制登记 + 可执行切片交接**，不重写各文档设计。

本轮会话落地切片（三块，按依赖排序）：

- **M1（P0-2 + P0-3）**：热键注册表服务（模型 + 冲突判定 + 作用域过滤 + `HWND_MESSAGE` 中心窗口 + 单钩子查表 + `settings.json` 持久化）+ 点击穿透两态窗口原语 + 右 Alt 门控（含 AltGr 处置）。
- **M2（P2 收尾一部分）**：OCR 后处理纯函数（版式/竖排/中英空格/表格→Markdown/去噪/低置信）+ `paths-only` 档降级路径。
- **M3（P3 第一步）**：`IActivityService` 契约 + 活动仲裁纯逻辑。

后续轮次（如实声明，不在本轮冒充完成）：热键侧板 UI 与既有热键迁移（P5）、灵动岛自渲染层与液体动效（P3 主体，依赖 Vortice 拍板）、系统通知采集（P4，硬前置稀疏包+签名）、OCR L2/L3 与准确率门禁（P2 余量）、商用配套（P6）。

## 2. Current Behaviour（核销后的真实状态）

| 面 | 状态 | 证据 |
|---|---|---|
| 截图 | 主体已落地 | capture exe 存在、M19 已登记、数据链实走（见截图文档 §10 核销表） |
| OCR | 引擎字段/动词/搜索 + L1 + 面板接线已落地 | 核销 T1/T2/D1-D3 通过；**T4 后处理、D4 paths-only、D5 低置信、L2/L3、准确率门禁未交付** |
| 热键 | **未实施** | 全仓唯一系统热键注册在 `PanelMainWindow.cs:2335 ApplyPasteBackHotkey`；引擎三键在 `engine/src/hotkey.rs`（V=面板/P=收藏/Backspace=暂停）；`packages/api` 无 `IHotkey*` 契约（verified） |
| 通知 | **未实施** | `INotificationService` 仅写侧（新装应用弹窗）；`NotificationCenterWindow.cs:7` 注释明写系统通知源待接入；稀疏包+签名链路未落地（STATUS 未落地节） |
| 灵动岛 | **未实施** | `MenuBarWindow.cs` root Grid 仅两列；`IMenuBarExtension.GetVisual()` 是右区按钮契约，岛不走它；`IAnimationService` 无几何形变原语（verified） |

## 3. Relevant Architecture

- 归属：能力（热键注册表、OCR、通知读取）常驻或随宿主；视觉表面（侧板、岛、覆盖层）留壳（[resident-architecture](../2026-09-11-resident-architecture.md)）。
- shell-core 是 shell 层公共域：所有 shell 包已依赖，零循环依赖；服务经 `IPlugin.LoadAsync` + `context.Provide/Get` 挂载（`ShellCorePlugin.cs:26-49` 先例）。
- 设置：`ISettingsService`（api/Settings）→ `%APPDATA%\BetterDesktop\settings.json`，键值 + 变更广播（`SettingsService.cs:31`）。
- 原生声明已就位：`NativeMethods.cs:243/248` `RegisterHotKey/UnregisterHotKey`、`:356` `CreateWindowExW`、`MessagePump.cs` 泵循环、`WindowStyleHelper.cs:15` `MakeFloatingNoActivate`。**缺** `WS_EX_TRANSPARENT`/`HTTRANSPARENT`/`HWND_MESSAGE` 声明（全仓零先例）。

## 4. Technical-Knowledge Findings

- `3101` 全局热键：`RegisterHotKey` 系统级唯一、0x581 冲突捕获、退出配对释放、需消息泵（中心窗口模式）——注册表服务直接套用。
- `401` toast-listener：`UserNotificationListener` 授权四态、非 UI 线程回调须 `Dispatcher.InvokeAsync`、权限拒绝优雅降级——通知采集按此实现。
- `2901` 屏幕采集（BitBlt/DwmSharedSurface/WGC）与 `3002` PaddleOCR（ONNX+DirectML）+ `3004` ONNX 会话工厂——OCR L2/L3 与截图后端的直接资产（本轮不落地，列为后续注入）。
- `301-305` 灵动岛参考（data-state 状态机/形变/玻璃降级/卡片布局/自动收起定时器）+ `island-hover-expand-state`（悬停展开/弹层互斥/单窗口双形态）——岛的状态机与仲裁语义参考（M3 仲裁先落地，渲染层后续）。
- `3302` Electron 穿透（hover 切换 + 弹层护栏）——两态窗口原语的行为参考；`7426` 透明悬浮窗；`7413` 热键注册纪律（MOD_NOREPEAT/原子 ID/线程约束）——引擎热键 `hotkey.rs` 已按此实现，注册表服务同口径。

## 5. Constraint Findings（生死线，取自功能文档红线）

- 热键：**不静默**（注册失败/被占用/冲突/被接管四类都要有可见位置）；**隐藏 ≠ 静音**（`Visible` 只过滤正常行，冲突/占用/失败不受隐藏影响）；**不双注册**（同绑定两个进程同时持有 = bug）；**不新增第二套钩子机制**；不劫持系统保留键。
- 热键持久化：注册表是 `settings.json` `hotkeys` 节**唯一读写者**；schema 变更按设置迁移纪律；默认值在代码 `DefaultChord`。
- 穿透两态：只读态完全穿透（点击/滚动落下方应用），可操作态去穿透位保留不抢焦点；门控必须"右 Alt 按住 **且** 鼠标点击落侧板"两个条件同时成立（AltGr = Ctrl+右 Alt 合成，一按就切态会误伤欧语布局输入）。
- OCR：识别只读已落盘图片、不落盘副本、不改图片语义；`paths-only` 档只能识别缩略图必须 UI 明示；失败绝不返回空文本冒充成功；默认不写正文诊断日志。
- 活动仲裁：高优先级可抢占低优先级、被抢占回队列不丢失；`Sticky` 常驻到显式 Complete/Dismiss；同 `Source` 连续事件折叠累计；全屏/游戏/DND 不弹出进队列。
- 机制登记即承诺：新机制先登记 `docs/MECHANISMS.md`（`MECHANISMS.md` 文档纪律），全仓唯一实现。

## 6. Proposed Changes

### M1 · 热键注册表（文档 1 §2-§7）+ 穿透原语（文档 1 §8）

| 文件 | 符号 | 职责 |
|---|---|---|
| `packages/api/Hotkey/HotkeyContracts.cs`（新，命名空间 `BetterDesktop.Shell.Hotkeys.Contracts`） | `HotkeySource` / `HotkeyScope` / `HotkeyChord`（复用 `HotkeySpec` 解析结果）/ `HotkeyBinding` / `RegistrationResult` / `HotkeyView` / `Conflict` / `IHotkeyRegistryService` | 契约面：注册/注销/改键/启停/显隐/恢复默认/作用域上报/查询/冲突列表/`Changed` 事件 |
| `packages/shell/shell-core/Hotkeys/HotkeyRegistryService.cs`（新） | `HotkeyRegistryService` | 模型 + 冲突判定 + 作用域过滤纯逻辑（可注入测试）；`HWND_MESSAGE` 中心窗口统一注册系统热键（`WM_HOTKEY` 按 Id 分派）；单键盘钩子查表（扩展 `KeyboardHook`）；`settings.json` `hotkeys` 节持久化（经 `ISettingsService`） |
| `packages/shell/shell-core/Hotkeys/HotkeyConflictPolicy.cs`（新） | 冲突判定纯函数 | 同作用域同键位拒绝 / 跨作用域需显式 `Shadows` / 内置键保护 / 作用域过滤 |
| `packages/shell/shell-core/Windowing/ClickThroughWindow.cs`（新） | 两态穿透原语 | `WS_EX_TRANSPARENT`/`WM_NCHITTEST → HTTRANSPARENT` 两态切换；与 `MakeFloatingNoActivate` 组合 |
| `packages/shell/shell-core/Hotkeys/RightAltGate.cs`（新） | 右 Alt 门控 | `WH_KEYBOARD_LL` 跟踪 `VK_RMENU` 起落；门控键可配置（默认右 Alt） |
| `packages/shell/shell-core/HotkeysPlugin.cs`（新，或并入 `ShellCorePlugin`） | `Provide<IHotkeyRegistryService>` | 挂载注册表服务 |

### M2 · OCR 后处理与降级（文档 3 §4/§6）

| 文件 | 符号 | 职责 |
|---|---|---|
| `packages/shell/shell-capture/Ocr/OcrPostProcessor.cs`（新） | 纯函数集 | 段落合并、多栏切分、竖排断行、中英空格归一、标点归一、表格→Markdown、屏幕伪影行过滤、低置信标记 |
| `packages/shell/shell-capture/Ocr/PathsOnlyPolicy.cs`（新） | `paths-only` 档降级 | 缩略图识别路径 + 可读原因 + 改档提示（D4） |

### M3 · 活动服务（文档 5 §3）

| 文件 | 符号 | 职责 |
|---|---|---|
| `packages/api/Activity/ActivityContracts.cs`（新，命名空间 `BetterDesktop.Activity.Contracts`） | `ActivityPriority` / `ActivityKind` / `ActivityAction` / `ActivityItem` / `IActivityService` | 契约面 |
| `packages/shell/shell-core/Activity/ActivityService.cs`（新） | `ActivityService` | 仲裁纯逻辑：优先级/抢占/同源合并/TTL/粘性/抑制；`Changed` 节流；零订阅者零开销 |

## 7. Implementation Sequence

1. **M1.1** 契约 + 模型 + 冲突/作用域纯逻辑 + 单测（无窗口无钩子）。
2. **M1.2** `HWND_MESSAGE` 中心窗口 + 单钩子查表 + `settings.json` 持久化 + 单测（假注册器/假键事件注入）。
3. **M1.3** 穿透两态原语 + 右 Alt 门控 + 单测。
4. **M2** OCR 后处理纯函数 + `paths-only` 降级 + 单测（合成语料实跑）。
5. **M3** 活动契约 + 仲裁 + 单测。
6. **登记**：`docs/MECHANISMS.md` v0.9 增 M20 热键注册表、M21 点击穿透两态窗口、M22 活动服务（按登记即承诺）。

任一步停下树仍一致；每步后 `dotnet build BetterDesktop.slnx` 保持 0 警告 0 错误。

## 8. Test Strategy

- **M1**：`HotkeyConflictPolicyTests`（同作用域拒绝/跨作用域 Shadows 解析/内置键冲突/作用域过滤）；`HotkeyRegistryServiceTests`（T1-T5：冲突判定、作用域过滤、键表查表假键事件、settings 往返与默认回退、`RegisterHotKey` 失败注入 → 冲突可见不抛异常）；`ClickThroughWindowTests`（样式位断言/命中测试两态）；`RightAltGateTests`（AltGr 双条件判定纯逻辑）。
- **M2**：`OcrPostProcessorTests`（多栏切分、竖排断行、中英空格、表格→Markdown、去噪、低置信）；合成语料实跑断言输出。
- **M3**：`ActivityServiceTests`（优先级抢占、同源合并、TTL 过期、粘性不淘汰、抑制补播、零订阅者零开销）。
- 验证命令：`dotnet build BetterDesktop.slnx`（0 警告 0 错误）+ `dotnet test`（新测试全绿）；引擎 `cargo test` 不回归。

## 9. Risk and Impact Analysis

| 风险 | 处置 |
|---|---|
| `HWND_MESSAGE` 窗口必须创建在跑消息泵的线程（否则收不到 `WM_HOTKEY`） | 中心窗口挂在宿主 UI 线程（WPF 已有消息泵）；`MessagePump.cs` 供专用线程场景 |
| 单钩子查表与现有 `StartKeyHook`/`DesktopPlugin` 钩子并存（迁移前） | 注册表服务先只接管新登记项；既有钩子在 P5 逐项迁移，迁移后回归 |
| `settings.json` 新增 `hotkeys` 节 | 注册表为唯一读写者；缺字段/损坏值回退默认（T4 覆盖）；不动既有节 |
| 右 Alt 门控与 AltGr 布局 | 双条件（按住+点击）才切态；门控键可配置（右 Ctrl/右 Shift/仅当 BD 表面前台） |
| 跨进程热键（壳/Agent/面板 exe/截图 exe/引擎） | M1 只做壳内注册表；跨进程独占归属（`HostPresenceWatcher` + `ExclusiveCapabilityHost`）留 P5 迁移期接入；引擎三键先在注册表内声明（Owner=clipboard-engine）防误抢 |
| 机制唯一性（M6 自绘动画） | M3 只做活动服务纯逻辑；自渲染层后续单独立项，登记 MECHANISMS 后才动 |

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/api/Hotkey/HotkeyContracts.cs`（新） | `IHotkeyRegistryService` 等 | 新契约（索引 §四 api 契约） |
| `packages/shell/shell-core/Hotkeys/*`（新 4 文件） | 服务 + 策略 + 门控 | 注册表实现（文档 1 §4/§8） |
| `packages/shell/shell-core/Windowing/ClickThroughWindow.cs`（新） | 两态原语 | 文档 1 §8 |
| `packages/shell/shell-core/ShellCorePlugin.cs` 或 `HotkeysPlugin.cs`（新） | `Provide` 挂载 | 服务入图 |
| `packages/shell/shell-capture/Ocr/*`（新 2 文件） | 后处理 + paths-only 策略 | 文档 3 §4/§6 未交付项 |
| `packages/api/Activity/ActivityContracts.cs`（新） | `IActivityService` 等 | 索引 §四 api 契约 |
| `packages/shell/shell-core/Activity/ActivityService.cs`（新） | 仲裁 | 文档 5 §3 |
| `packages/shell/shell-core-tests/*`（新测试） | 上述各单测 | DoD T 项 |
| `docs/MECHANISMS.md` | v0.9 M20/M21/M22 | 登记即承诺 |

## 11. Reusable Implementation Context

- 契约命名先例：`BetterDesktop.Capture.Contracts` / `BetterDesktop.Ocr.Contracts`（api 包，域目录 + `Contracts` 后缀）。
- 服务挂载先例：`ShellCorePlugin.LoadAsync` → `context.Provide<T>(new T(...))`；依赖经构造注入。
- 热键解析唯一来源：`packages/shell/shell-clipboard-ipc/HotkeySpec.cs`（`TryParse` → modifiers/mainKey/canonical；`ConflictsWithBuiltIn`；`BuiltInHotkeys` 三键）。
- 钩子封装：`KeyboardHook`（`WH_KEYBOARD_LL`，回调返回 true 吞键，回调内不得耗时）。
- 设置：`ISettingsService.Get/Set<T>(key, defaultValue)`（api/Settings），键值 `hotkeys.<id>` 或 `hotkeys` 节（实现时按契约选型，默认 `hotkeys` 节）。
- 原生：`NativeMethods.RegisterHotKey/UnregisterHotKey/CreateWindowExW/GetWindowLong/SetWindowLong`；`WindowStyleHelper.MakeFloatingNoActivate`。

## 12. Assumptions and Open Questions

- 门控键默认右 Alt（文档 1 开放问题 1）：可配置；若用户会输入 AltGr 字符则改右 Ctrl——实现以"双条件 + 可配置"兜底，不阻塞。
- 侧板位置/尺寸（开放问题 3）、改键入口（开放问题 2）：P5 侧板 UI 阶段定，本轮不涉及。
- 截图 exe 跨进程注册：P5 迁移期接独占机制，本轮只做壳内。
- 岛渲染层依赖 Vortice.Windows（索引待拍板 6）：**M3 不引入**（纯逻辑），渲染层开工前须用户拍板。
- 60 张语料准确率门禁（文档 3 §8）：P2 余量，需真实截图语料，不在本轮。
- 后续轮次（deferred）：P3 自渲染层与动效、P4 通知 + 稀疏包签名、P5 侧板 UI 与迁移、P6 商用配套。

## 13. Definition of Done

- D1（M1 机制）：`dotnet build` 0 警告 0 错误；`HotkeyConflictPolicyTests` / `HotkeyRegistryServiceTests` / `ClickThroughWindowTests` / `RightAltGateTests` 全绿。
- D2（M1 场景）：注册两条同作用域同键位绑定 → 第二条被拒且冲突可见；跨作用域显式 `Shadows` 后双方可见"接管中/被接管"；改键后旧键立即失效新键生效；重启后保持（settings 往返）——**UI 侧板未建，场景走查以服务级集成测试 + 真机命令行探针为准，如实标注**。
- D3（M2）：`OcrPostProcessorTests` 全绿；合成语料（中英混排/表格/竖排）实跑输出断言；`paths-only` 路径返回可读原因。
- D4（M3）：`ActivityServiceTests` 全绿（优先级/抢占/合并/TTL/粘性/抑制）。
- D5（登记）：`docs/MECHANISMS.md` v0.9 含 M20/M21/M22，条目含禁止项与唯一性声明。

## 14. Handoff to 技术力应用（交接节）

| 项 | 填写 |
|---|---|
| 模式判定 | 全部走 **`无匹配→工程代码权威`**（成熟工程增量）：技术库无"热键注册表/穿透两态/活动仲裁/OCR 后处理"文档；以本项目代码模式为事实标准（复用优先，不重写既有钩子/设置/契约范式）。`3101`/`401` 等仅作红线参考，不按文档照抄 |
| 注入清单 | ① [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)（批次顺序）；② [2026-09-14-hotkey-registry-and-sheet.md](2026-09-14-hotkey-registry-and-sheet.md)（M1 决策/红线/DoD）；③ [2026-09-14-ocr-recognition.md](2026-09-14-ocr-recognition.md)（M2 §4/§6/§8）；④ [2026-09-14-dynamic-island.md](2026-09-14-dynamic-island.md)（M3 §3 仲裁）；⑤ 源码锚点：`HotkeySpec.cs`（解析唯一来源）、`KeyboardHook.cs`、`NativeMethods.cs`、`WindowStyleHelper.cs`、`MessagePump.cs`、`ShellCorePlugin.cs`、`SettingsService.cs`（`ISettingsService`）、`PanelMainWindow.cs:2335`（迁移参照）、`engine/src/hotkey.rs`（引擎声明参照）、`OcrContracts.cs`（命名先例） |
| 适配参数 | 目标语言 C#/.NET 8；命名空间 `BetterDesktop.Shell.Hotkeys.Contracts`（api）/`BetterDesktop.Shell.Core.Hotkeys`（实现）/`BetterDesktop.Activity.Contracts`（api）/`BetterDesktop.Shell.Core.Activity`（实现）；新契约文件进 `packages/api` 对应域目录；实现挂载走 `IPlugin` + `context.Provide`；设置经 `ISettingsService`；测试进 `packages/shell/shell-core-tests` 与 `shell-capture-tests`（M2） |
| 禁区 | 不新增第二套钩子/热键解析器（一律走 `HotkeySpec` + 注册表）；不新建剪贴板 IPC 动词；不动 `MenuBarWindow`/`PanelMainWindow` 既有逻辑（P5 才迁移）；不引入 Vortice 或任何新第三方依赖（M1-M3）；不实现宏/脚本/跨设备同步；不劫持系统保留键；`paths-only` 档不得静默降级（必须可读原因） |
| DoD 核销表 | D1-D5 见 §13；每条给验证命令（`dotnet build` / `dotnet test` / 服务级集成探针）+ 实测结果；**UI 场景未实走（侧板/岛表面未建）必须如实标注「待真机」，不得声称通过** |

---

## 15. 本计划的状态收敛结论（供批次索引回写）

- P0-1（OCR 引擎侧）✅ 已核销（OCR 文档 §11）。
- P0-2/P0-3（热键注册表 + 穿透原语）→ 本计划 M1。
- P1（截图）✅ 主体已核销，GUI 路径待真机（截图文档 §10）。
- P2（OCR 收尾）→ 本计划 M2 部分；L2/L3 + 准确率门禁留后续。
- P3（灵动岛）→ 本计划 M3 仲裁；自渲染层/动效留后续（依赖 Vortice 拍板）。
- P4（系统通知）→ 后续（硬前置：稀疏包 + 签名链路）。
- P5（侧板 UI + 迁移）→ 后续（依赖 M1）。
- P6（商用配套）→ 后续。

---

## 16. M1-M3 落地核销记录（2026-09-15）

> 本计划 §14 交接节的执行结果，由技术力应用侧按 DoD 逐条核销。**真机未走场景一律标注「待用户真机走查」，不声称通过。**

| DoD | 验证命令 / 方式 | 实测结果 | 状态 |
|---|---|---|---|
| D1（M1 机制） | `dotnet build BetterDesktop.slnx`；`dotnet test packages/shell/shell-core-tests/BetterDesktop.Shell.Core.Tests.csproj` | 0 警告 0 错误；shell-core-tests 106/106（含 HotkeyConflictPolicy / HotkeyRegistryService / ClickThroughWindow / RightAltGate） | ✅ 通过 |
| D2（M1 场景） | 服务级集成测试（同作用域重复拒绝 + 冲突可见；跨作用域显式 Shadows 接管；改键即生效；settings 往返持久化） | 集成断言全绿；**侧板 UI 未建（P5），真机走查待用户真机** | ⚠️ 机制通过 / 场景待真机 |
| D3（M2） | `dotnet test packages/shell/shell-capture-tests/BetterDesktop.Shell.Capture.Tests.csproj` | shell-capture-tests 48/48（OcrPostProcessor + PathsOnlyPolicy，合成语料含中英混排/表格/竖排） | ✅ 通过 |
| D4（M3） | 同上（shell-core-tests） | ActivityServiceTests 全绿（优先级/抢占/合并/TTL/粘性/抑制/Changed 节流） | ✅ 通过 |
| D5（登记） | `scripts/verify-doc-budgets.ps1`、`scripts/verify-agent-note.ps1` | MECHANISMS.md v0.9 登记 M20/M21/M22；词数 1952 ≤ 2000（预算经决策记录 `.agents/notes/implemented/process/2026-09-15-mechanism-registration-budget.md` 放宽，理由=新增机制条目）；agent-note 21 篇合规 | ✅ 通过 |

**记录在案的偏离（相对各功能文档草图）**：
1. **Changed 事件位置**：岛文档 §3 与热键文档草图的 `event Action? Changed` 原在 api 接口上；落地时移至具体类（`ActivityService` / `HotkeyRegistryService`，shell-core）——遵守 ADR-002 D4「禁止跨程序集裸 C# event」，跨包消费（岛渲染 P3 / 热键侧板 P5）届时走 IEventBus 桥接。接口面保持数据操作契约不变。`verify-no-cross-assembly-event` 门禁实测 PASS。
2. **门禁状态快照**：doc-budgets ✅ / cross-asm-event ✅ / agent-note ✅ / `dotnet build` 0 警告 0 错误 ✅；**dotnet-format 仍红**——约 82 个**既有未提交工作区文件**（agent/tray/updater/api/shell-* 等前批累积）存在 ENDOFLINE/CHARSET/IMPORTS 违规，非本批引入；本批已机械修复其中 8 个（ShellMenuContentBuilder / DesktopSystemMenuRegistrar / WgcCapture / HandlerCrashGuardTests / MenuBrokerClientTests / ComShellExtensionRegistrar / DockAppsService / StatusBarMenuBarExtension），其余留待格式债清理批次（避免越界改动既有未提交工作区）。
3. **并发编辑观察**：本会话期间监测到工作区存在另一活跃会话正在编辑 shell-capture UI（CaptureUi/OverlayWindow/OcrPanelWindow）并同步迁移 api 接口事件（与此处偏离 1 同向）。本批未触碰其正在编辑的文件；后续接续者注意以 `git status` + 文件 mtime 复核最新状态。
