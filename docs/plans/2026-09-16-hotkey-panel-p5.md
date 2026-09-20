# Cairo 开发计划：热键侧板 P5（侧板 UI + 既有热键迁移）

> Task: 热键对用户可见的部分——宿主常驻热键侧板（只读实时表 + 右 Alt 门控可操作态 + 隐藏/停用/恢复 + 就地录键 + 冲突/被接管高亮 + 已忽略管理）+ 全部既有热键声明进注册表（侧板显示不分来源）+ 面板粘回热键改键/停用真生效；其余既有热键生效面标注"待接线"，不假装可改。
> 证据基于 commit `b805f6e4` 验证；技术力文档命中 `31-热键/3101-global-hotkey.md`（L2，红线已被 M1 覆盖）；无"热键侧板"现成资产 → 新建。
> 实现将交由 技术力应用 skill 按 §14 执行；本计划只规划不实现。

## 1. Objective（场景语言）

用户在屏幕上随时能看到"此刻可用的热键表"，不分来源（截图/面板/引擎/开始菜单/桌面）；冲突或被接管的项置顶高亮并给出原因；按住右 Alt 点击可进入操作态，能就地改键、隐藏（忽略）、停用、恢复默认；底部"已忽略 N 项 · 管理"可找回。既有的"粘贴回原窗口"用户热键在侧板改键/停用后**真实生效**（下次打开面板即按新键）。其余跨进程热键（截图/引擎/开始菜单）只展示与隐藏，改键/停用入口明确标注"生效通道接线中"，不产生"显示已改、实际没改"的欺骗。

## 2. Current Behaviour

- **M1 已落地（全绿）**：`HotkeyRegistryService`（api `IHotkeyRegistryService`：Register/Unregister/Rebind/SetEnabled/SetVisible/ResetToDefault/SetActiveScopes/GetActive/GetAll/GetConflicts；类级 `Changed` 事件，接口无事件——ADR-002 D4）；`HotkeyCenterWindow`（HWND_MESSAGE 中心窗口统一 RegisterHotKey）；`ClickThroughWindow`（穿透原语）；`RightAltGate`（右 Alt 门控 + AltGr 防护）；`HotkeysPlugin` 宿主 Provide `IHotkeyRegistryService`，`cordis.yml` 已挂 `hotkeys`。
- **既有热键现状（迁移目标）**：面板"粘回"热键由面板 exe 自注册（`PanelMainWindow.ApplyPasteBackHotkey`，每次打开读宿主 settings 键 `extensions.clipboard-history.paste-back-hotkey`，收起 `OnBeforeHide` 注销）；截图热键由 capture exe 自注册（`HotKeyManager`，默认 Win+Shift+B，读自己的 `capture/settings.json`）；引擎三枚全局热键（Rust，Ctrl+Shift+V/P/Backspace）；开始菜单 Win 键（StartKeyHook 低级钩子）。
- **侧板形态先例**：`PopupWindowBase`（NoActivate + AutoHideOnOutsideClick，侧板关掉 AutoHide 常驻）；`ClipboardSection.HotkeyRow`（录键 UI 模式：只读 TextBox + PreviewKeyDown 全拦 + Backspace 清除 + TryBuildHotkey 组合校验）——`TryBuildHotkey`/`DescribeHotkey` 为 **private static**，侧板复制模式到包内，不跨包重构。

## 3. Relevant Architecture

- 宿主进程内插件（IPlugin + context.Get/Provide），`Bootstrap.cs` Factories + `cordis.yml` 声明式装配；新侧板照此模式新建包。
- 跨进程热键（capture/面板/引擎）不在宿主进程内注册——**禁止宿主重复注册**（3101 红线 2：同组合键系统全局唯一，0x581）。声明条目必须"只展示不注册"。
- 侧板消费 shell-core 的 `Changed` 类事件（跨程序集直订阅——与工程现状一致；与文档 §4 注释"IEventBus 桥接"的差异记 §11/§12）。

## 4. Findings

- [verified] `IHotkeyRegistryService` 无"声明不注册"API；`Register` 必然 `EnsureRegistered` 真注册键位（HotkeyRegistryService.cs:114,383）。→ 需新增 `Declare`。
- [verified] `SetActiveScopes` 为**全量覆盖**语义（行 257-264）→ 多表面需聚合器（`HotkeyScopeTracker`）。
- [verified] `GetActive` 过滤：`!Enabled || !IsActive(scope)` 剔除；`!Visible && !mustShow`（OsConflict/Shadowed 强制显示）→ "隐藏≠静音"已内建。
- [verified] 面板粘回改键链路成立：宿主 settings 键 `extensions.clipboard-history.paste-back-hotkey`（ClipboardSection.cs:80）→ 面板 `ApplyPasteBackHotkey` 每次打开读取注册（PanelMainWindow.cs:2421-2481）→ **改键/停用写该键 = 真生效（下次打开面板）**，面板 exe 零改动。
- [verified] 录键 UI 模式可复制（ClipboardSection.cs:324-410）；测试构造先例 `FakeHotkeyWindow : IHotkeyWindow`（shell-core-tests，internal 构造对 tests 可见）。
- [verified] `HotkeysPlugin` Provide `IHotkeyRegistryService`（HotkeysPlugin.cs:40），侧板插件 `context.Get<IHotkeyRegistryService>()` 消费。
- 技术库：3101 命中（RegisterHotKey 全局唯一/消息循环/配对释放——M1 中心窗口已全部覆盖，无新增落地项）；3302/7426（穿透，ClickThroughWindow 已落地）；**无热键侧板/声明式热键资产**。

## 5. Constraint Findings（红线 → 等价断言）

| 红线（功能文档 §11） | 本计划等价断言 |
|---|---|
| 不双注册 | `Declare` 不调用 `IHotkeyWindow.Register`（测试断言 FakeHotkeyWindow 零调用） |
| 不静默（四类可见） | 侧板用 `HotkeyView.OsConflict/ShadowedBy` 置顶高亮；`GetConflicts().Reason` 显示原因 |
| 隐藏≠静音 | 走既有 `SetVisible`，不动 `Enabled`/注册状态 |
| 不假装可改 | 接线状态门：仅 `clipboard.paste-back` 开放改键/停用；未接线项按钮禁用 + tooltip"生效通道接线中" |
| 不改系统保留键 | 声明条目默认键位照既有现状，改键走 `Rebind` 冲突校验（内置冲突拒绝） |

## 6. Proposed Changes

**A. api `Hotkey/HotkeyContracts.cs` + shell-core `Hotkeys/HotkeyRegistryService.cs`：新增 `Declare`**
- 契约：`RegistrationResult Declare(HotkeyBinding binding)`——声明外部/跨进程热键：不注册键位、无 onTrigger、仅供展示/冲突检测/持久化。
- 实现：复用 Register 校验链（空 Id/非法 Chord/内置冲突/同作用域重复/跨作用域未声明 Shadows），跳过 `EnsureRegistered`；条目标记 `Declared`（内部字段）；`Rebind/SetEnabled/SetVisible/ResetToDefault` 对声明条目只更新内存 + 持久化 + `RaiseChanged`，不碰中心窗口/钩子；`GetActive/GetAll/GetConflicts` 照常（Enabled 过滤/冲突检测对声明条目不例外）。

**B. shell-core `Hotkeys/HotkeyScopeTracker.cs`（public 新类）**
- `EnterScope(string)/ExitScope(string)/SetScopes(IEnumerable<string>)`；内部 HashSet 去重；任何变化 → 调注册表 `SetActiveScopes(快照)`；作用域变化经注册表 `Changed` 传播。本批接线宿主内表面；跨进程（面板 exe）上报后置（§12）。

**C. 新包 `packages/shell/shell-hotkey-panel`（宿主插件库，非独立 exe）**
- `BetterDesktop.Shell.HotkeyPanel.csproj`（照 shell-clipboard 库依赖：引用 shell-core / api；目标框架 net8.0-windows*）。
- `HotkeyPanelPlugin : IPlugin`：`context.Get<IHotkeyRegistryService>()`，Load 时创建常驻侧板窗口 + 注册全部声明条目（见 D），Unload 注销。
- `HotkeyPanelWindow`（继承 `PopupWindowBase`，`AutoHideOnOutsideClick=false` 常驻；构造注入 IVibrancyService/IAppearanceService 按宿主内窗口先例锚定）：
  - 位置：主屏 WorkArea 右侧中部，置顶（Topmost），初始化 `ClickThroughWindow.SetClickThrough(hwnd, true)`（只读穿透态）。
  - 只读态内容：`GetActive()` 渲染键位 + `Description` + `Owner` 来源徽标；`OsConflict || ShadowedBy` 项置顶 + 高亮 + 原因行；`_activeScopes` 为空显示"当前无界面上下文，仅全局键可用"；底部"已忽略 N 项 · 管理"。
  - 两态门控：`RightAltGate`（钩子更新 IsDown）→ 点击侧板区时 `RightAltGate.ShouldSwitchToInteractive(gateDown, ctrlHeld, onTarget, GateVk)` → 真则 `SetClickThrough(false)` 进可操作态；松门控键回只读。
  - 可操作态：行内"隐藏/停用/恢复默认"（SetVisible/SetEnabled/ResetToDefault）；改键就地录制（复制 ClipboardSection.HotkeyRow 模式：只读 TextBox 聚焦 + PreviewKeyDown 全拦 + Backspace 清除 + TryBuildHotkey 校验 → `Rebind`，结果 `RegistrationResult.Reason` 行内提示）；**接线状态门**：仅 `clipboard.paste-back` 开放改键/停用，未接线项禁用 + tooltip。
  - "已忽略管理"浮层：`GetAll().Where(!Visible)` 列表 + 恢复（SetVisible(true)）。
  - 订阅 `HotkeyRegistryService.Changed` 刷新；Dispose 退订。

**D. 声明迁移注册（`HotkeyPanelPlugin.Load` 内一次性 `Declare` 全部既有热键）**

| Id | 来源 | 默认键位 | 接线状态 |
|---|---|---|---|
| `clipboard.paste-back` | 面板（Owner=panel） | 未配置不声明；配置后 = 设置值 | **真接线**：改键/停用写 `extensions.clipboard-history.paste-back-hotkey`（Rebind 经注册表持久化到该键——需在 Rebind 对声明条目时委托写该配置键；或由侧板改键逻辑直接写 settings 再 Rebind，实现时取更简路径并保一致性） |
| `capture.toggle` | 截图（Owner=capture） | Win+Shift+B | 展示+隐藏；改键/停用禁用（capture 读自己的 settings.json，接线后置） |
| `engine.panel-toggle` / `engine.favorites` / `engine.pause` | 引擎（Owner=engine） | Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace | 展示+隐藏；改键禁用（apply_settings 通道后置） |
| `start-menu.win-key` | 开始菜单（Owner=start-menu） | Win | 展示+隐藏；改键禁用（StartKeyHook 查表迁移后置） |

- 桌面双击切换**不声明**（非键盘热键，展示语义不符；列入 §12）。
- 声明条目的持久化走注册表既有 `hotkeys.<Id>` 键（enabled/visible/chord），与 `paste-back-hotkey` 配置键的关系在实现时定一致来源（单一真相源：注册表 Rebind 时同步写 paste-back-hotkey 配置键，或读侧板改键直写——实现时二选一并记录）。

**E. 挂载**：`BetterDesktop.slnx`（Folder+Project 条目）、`host/cordis.yml`（`hotkeys-panel` 条目，依赖 hotkeys/settings）、`host/Bootstrap.cs` `Factories["hotkeys-panel"]`（照 hotkeys 条目模式）。

**F. 测试**
- shell-core-tests 新增：`Declare`（成功/重复 Id/非法 Chord/内置冲突/同作用域重复/声明项 GetActive 可见/GetConflicts/声明项 Rebind+SetEnabled+SetVisible 不触发 FakeHotkeyWindow.Register 且持久化）；`HotkeyScopeTracker`（Enter/Exit 聚合去重/SetScopes 覆盖/空集）。
- 新包 `packages/shell/shell-hotkey-panel-tests`（最小）：侧板纯逻辑（只读表排序：冲突置顶、隐藏过滤、忽略计数、接线状态判定）；窗口 smoke（构造 + BuildContent + 初始穿透态）。

## 7. Implementation Sequence

1. **Declare API**（契约 + 服务实现 + shell-core-tests）→ build/test 绿。
2. **HotkeyScopeTracker**（+ 测试）→ 绿。
3. **shell-hotkey-panel 包骨架 + 挂载**（slnx/cordis.yml/Bootstrap）→ build 绿。
4. **侧板窗口**：只读表 → 两态门控 → 可操作管理 → 录键 → 已忽略管理。
5. **声明迁移注册**（D 表 + 接线状态门 + paste-back 真链路）。
6. **测试补全 + 门禁 + 真机走查**（D1-D4 标注"待用户真机走查"）。

## 8. Test Strategy

- 新增测试文件：`shell-core-tests/Hotkeys/HotkeyRegistryServiceDeclareTests.cs`、`shell-core-tests/Hotkeys/HotkeyScopeTrackerTests.cs`、`shell-hotkey-panel-tests/...`（纯逻辑 + smoke）。
- 验证命令（真实）：`dotnet build BetterDesktop.slnx`（0 警告 0 错误）、`dotnet test packages/shell/shell-core-tests/BetterDesktop.Shell.Core.Tests.csproj`、`dotnet test packages/shell/shell-hotkey-panel-tests/...`、`pwsh -File scripts/run-gates.ps1`（期望除 dotnet-format 既有 82 项红外全绿）。
- 边界：Declare 冲突分支、声明项操作不触碰中心窗口、录键 Backspace 清除、AltGr 门控防护、忽略计数。

## 9. Risk and Impact Analysis

- **跨包订阅类事件**：侧板包订阅 shell-core `Changed`（cross-asm 门禁只查接口事件、不查类事件；与工程现状一致）；差异记录于 §12，后续可迁 IEventBus。
- **双注册红线**：Declare 必须零调用 `IHotkeyWindow.Register`（测试断言兜底）。
- **假可改红线**：接线状态门兜底；paste-back 真链路以面板下次打开生效为验收（真机项）。
- **既有红门禁**：dotnet-format ~82 既有文件违规（非本批引入）——不纳入本批修复面。
- **并发编辑会话**：动手前 `git status --short` + 相关文件 mtime 复核。
- 兼容性：`IHotkeyRegistryService` 增方法 = 接口扩展，既有实现（仅 HotkeyRegistryService）同步加；无外部消费者破坏。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/api/Hotkey/HotkeyContracts.cs` | `IHotkeyRegistryService.Declare` | 声明 API |
| `packages/shell/shell-core/Hotkeys/HotkeyRegistryService.cs` | `Declare`/`_declared`/Rebind 分支 | 声明实现 |
| `packages/shell/shell-core/Hotkeys/HotkeyScopeTracker.cs` | 新类 | 作用域聚合 |
| `packages/shell/shell-hotkey-panel/*` | 新包（Plugin/Window/录键工具） | 侧板 UI |
| `packages/shell/shell-hotkey-panel-tests/*` | 新测试包 | 侧板逻辑 |
| `BetterDesktop.slnx` / `host/cordis.yml` / `host/Bootstrap.cs` | 挂载条目 | 装配 |
| `packages/shell/shell-core-tests/Hotkeys/*Declare*` | 新测试 | 声明/聚合 |

## 11. Reusable Implementation Context

- 命中文档：`TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md`（红线：RegisterHotKey 全局唯一 0x581/需消息循环/退出配对释放——M1 中心窗口已覆盖）。
- 契约：api `HotkeyContracts.cs`（HotkeyBinding/RegistrationResult/HotkeyView/GetActive 语义）。
- 先例：`PopupWindowBase`（shell-core/Windows，构造 IVibrancyService+IAppearanceService）、`ClickThroughWindow`、`RightAltGate`、`ClipboardSection.HotkeyRow`（录键模式，private static 需复制）、`HotkeysPlugin`（Provide 模式）、`FakeHotkeyWindow`（测试构造）。
- 配置键：`extensions.clipboard-history.paste-back-hotkey`（面板真链路）。

## 12. Assumptions and Open Questions

- **假设**：侧板默认位置主屏右侧中部（位置/尺寸记忆后置）；门控键默认右 Alt（AltGr 防护已内建）。
- **后置（deferred，本批不做）**：设置中心热键管理分区；面板内按键（Ctrl+V 重定向/Esc/按格粘）迁移；开始菜单 Win 键查表迁移；桌面双击切换登记（非键盘热键，需单独语义）；引擎三枚 apply_settings 改键通道；截图/引擎/开始菜单改键停用生效面；跨进程作用域上报（面板打开→侧板显示面板键）；paste-back 未配置时的"添加热键"入口（现依赖设置中心配置）。
- **待实现时定夺**：paste-back 改键的单一真相源路径（注册表 Rebind 同步写配置键 vs 侧板直写配置键 + 声明条目同步）；侧板窗口基类构造注入按宿主内窗口先例锚定。
- **差异记录**：侧板直订阅 shell-core 类事件（文档 §4 注释原拟 IEventBus 桥接）——与工程现状一致，后续可迁。

## 13. Definition of Done

- **T1** `dotnet build BetterDesktop.slnx` 0 警告 0 错误。
- **T2** 全部测试绿：shell-core-tests（含新增 Declare/ScopeTracker）、shell-hotkey-panel-tests、shell-capture-tests。
- **T3** Declare 不双注册：新增测试断言 FakeHotkeyWindow 对声明条目零 Register 调用。
- **T4** 接线状态门：仅 `clipboard.paste-back` 可改键/停用；其余声明项按钮禁用 + tooltip。
- **T5** 门禁 `run-gates.ps1`：除 dotnet-format 既有 82 项红外全绿（doc-budgets/cross-asm/agent-note/md-links/md-wrap/native-convergence）。
- **D1**（真机·待用户走查）侧板常驻穿透：只读态点击/滚动穿透到下方应用，不抢焦点。
- **D2**（真机·待用户走查）右 Alt 按住 + 点击侧板 → 可操作态；松开右 Alt → 回只读穿透。
- **D3**（真机·待用户走查）侧板改"粘贴回原窗口"热键 → 关闭面板重开 → 新键生效；停用 → 重开面板后该键不再响应。
- **D4**（真机·待用户走查）冲突/被接管项置顶高亮并显示原因；隐藏项从列表消失且"已忽略 N"计数正确、可恢复。

## 14. Handoff to 技术力应用（交接节）

| 项 | 内容 |
|---|---|
| 模式判定 | 无匹配库文档（成熟工程增量）→ **工程代码权威**：按 §6 逐项实现，所有既有符号先源码锚定再写；技术库 3101 红线并入（不双注册/消息循环/配对释放） |
| 注入清单 | ① 本计划全文（§6/§7/§13 为执行依据）② `docs/plans/2026-09-14-hotkey-registry-and-sheet.md`（§9 迁移清单/§10 界面分工/§11 红线/§12 实现顺序/§13 DoD——P5 设计权威，逐节对照）③ api `HotkeyContracts.cs`（契约全文）④ `TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md`（红线） |
| 适配参数 | 宿主内插件库；命名空间 `BetterDesktop.Shell.HotkeyPanel`；插入点 `BetterDesktop.slnx` / `host/cordis.yml` / `host/Bootstrap.cs` Factories；新包引用 shell-core + api + shell-clipboard-ipc（HotkeySpec 解析用）；目标框架照 shell-* 包现状；配置键 `extensions.clipboard-history.paste-back-hotkey` |
| 禁区 | 不碰 capture/面板 exe 的既有注册逻辑；不动 Rust 引擎；不引入 Vortice.Windows；不重构 ClipboardSection（录键模式复制到包内私有静态工具）；不清理 dotnet-format 既有 82 项；不做 §12 后置项；不改 `IHotkeyWindow`/中心窗口内部 |
| DoD 核销表 | T1: `dotnet build BetterDesktop.slnx` 0/0 → 实测记录；T2: 三个测试项目全绿 → 实测记录；T3: DeclareTests 断言零 Register；T4: 接线状态门测试；T5: `pwsh -File scripts/run-gates.ps1` 结果记录；D1-D4: **未真机走查不得宣称通过**，交付时逐条标注"待用户真机走查" |

## 15. 落地核销记录（2026-09-16 实现后回写）

| DoD | 验证 | 实测结果 |
|---|---|---|
| T1 | `dotnet build BetterDesktop.slnx -c Debug` | ✅ 0 警告 0 错误（含 shell-hotkey-panel / shell-hotkey-panel-tests / host） |
| T2 | 三个测试项目 | ✅ shell-core-tests 122/122（含新增 Declare 10 + ScopeTracker 6）、shell-capture-tests 48/48、shell-hotkey-panel-tests 12/12 |
| T3 | Declare 不双注册 | ✅ `HotkeyRegistryServiceDeclareTests`：声明条目对 FakeHotkeyWindow 零 Register/Unregister 调用（10 例断言） |
| T4 | 接线状态门 | ✅ `HotkeyListModelTests.Wired_flag_marks_paste_back_only`：仅 `clipboard.paste-back` Wired=true |
| T5 | `pwsh -File scripts/run-gates.ps1` | ✅ 13 项 PASS（package-readme/agent-note/archived-notes/md-links/md-wrap/doc-budgets/gate-registry/architecture-guard/host-log-sink/cross-asm-event/native-convergence 等）；❌ dotnet-format 唯一 FAIL——违规全在**既有文件**（agent/AgentLog.cs、shell-desktop/DesktopControlMenu.cs 等，非本批引入，专项验证 shell-hotkey-panel/Hotkeys/api-Hotkey 零违规），按计划 §9 不纳入本批修复面 |
| D1-D4 | 真机走查 | ⏳ **待用户真机走查**：穿透/门控/改键生效/冲突高亮四项场景未实走，不得宣称通过 |

**落地内容**：① `IHotkeyRegistryService.Declare`（api + shell-core，声明不注册，冲突链同 Register）；② `HotkeyScopeTracker`（shell-core，作用域聚合）；③ 新包 `shell-hotkey-panel`（插件 + 常驻透明窗：只读表/两态门控/隐藏/停用/改键/已忽略管理）；④ 声明迁移 5 项（clipboard.paste-back / capture.toggle / engine.*×3 / start-menu.win-key），paste-back 真接线（写配置键，面板下次打开生效）；⑤ 挂载 slnx / cordis.yml / Bootstrap。

**偏离记录**：
- 侧板数据刷新用 **500ms DispatcherTimer 轮询**注册表纯读接口，替代原拟的"订阅 shell-core Changed 类事件"——轮询零跨包事件、零契约改动（更贴合 ADR-002 D4 精神；Changed 跨包消费仍留待 IEventBus 桥接）。
- paste-back 同步链路：侧板 `SetPasteBack`/`ClearPasteBack` 写配置键 `extensions.clipboard-history.paste-back-hotkey` + 注册表声明/注销/改键双写一致（计划 §6-D 预留的实现时二选一，取"侧板一处双写"）。
- 声明条目 5 项（桌面双击切换非键盘热键不声明，按计划 §12）。
- `clipboard.paste-back` 作用域为 `Surface.ClipboardPanel`：面板未打开时列表视图不显示（语义正确：仅面板打开期间有效），管理视图/改键仍可用。
- MECHANISMS.md 未登记新机制（词数 1952/2000 预算紧）；机制记录以本计划 §15 为准，如需入库后续按预算调整。

## 16. 评审修复核销记录（2026-09-16 用户代码评审：P0→P1→P2）

| 项 | 修复内容 | 验证 |
|---|---|---|
| P0-1 作用域零上报 | ①可操作态全量列表：`HotkeyListModel.BuildAllRows`（非活跃作用域灰显 + 作用域徽标，只读态仍按上下文过滤）；②宿主内接线：`ShellWindow`/`PopupWindowBase` 基类加 `SurfaceScopeId` + `SurfaceScopeBridge`（静态桥）show/hide 上报；MenuBarPopupWindow（Surface.MenuBar，一处覆盖全部弹层）/ SettingsWindow（Surface.Settings）/ StartMenuService.Show/Hide（Surface.StartMenu）/ AppGrabberWindow（Surface.DockGrabber）；hotkeys-panel 插件 Attach tracker | BuildAllRows 3 测试 + 门禁 14/14 |
| P0-2 Inject 空 | `Inject => new[] { typeof(IHotkeyRegistryService) }`（声明式依赖，消除与 hotkeys 竞态）；降级仍 Warn + 不创建窗口 | 编译验证 |
| P0-3 已忽略死链 | footer 挂 MouseLeftButtonDown → `HotkeyPanelState.ToggleManage` → 管理浮层（`BuildManageRows` 隐藏项 + 恢复按钮 `SetVisible(id,true)`；hidden==0 自动退出） | BuildManageRows 2 测试 |
| P1-1 轮询打断录键 | 停表（`HotkeyPanelState.CanTimerRefresh`：仅只读可刷新）；Refresh 快照比对（`_lastSnapshot` 无变化不重建）；录键框 `Loaded += Focus()` | 状态机测试覆盖 |
| P1-2 paste-back 双真相源 | 声明条目 Rebind **不落 chord 持久化**（清残留 override）；`SyncDeclaredEntries` 对配置键变化的已声明条目 Rebind 同步；测试更新为新语义 | Declare 测试（Rebind 内存更新、重建后 chord 回声明值） |
| P1-3 按住右 Alt 解除穿透 | 保持穿透；新增全局 `MouseHook`（WM_LBUTTONDOWN + GetCursorPos 在 GetWindowRect 内 + `ShouldSwitchToInteractive` AltGr 防护）→ `EnterInteractive`；移除"按住即切" | 门控逻辑在状态机（仅只读可进可操作） |
| P1-4 声明条目无活性 | `HotkeyDeclarations.IsOwnerAlive`（capture=BetterDesktop.Capture / engine=betterdesktop-clipboard-engine 进程探活，未知不误报）；行上标"未运行" | 逻辑内建 |
| P2-1 MECHANISMS 登记 | **并发会话已登记** M20 热键注册表 / M21 点击穿透两态 / M22（v0.9）——评审建议的两行已存在，无需重复 | doc-budgets PASS（26 文档限内） |
| P2-2 dotnet-format 棘轮 | `verify-dotnet-format.ps1` 改基线棘轮：违规集相对 `scripts/manifests/dotnet-format-baseline.txt`（12908 处既有）比对，**仅新增违规红**；本批文件专项零违规；单测 5/5（新增 Get-FormatViolations/Get-NewViolations） | dotnet-format PASS（门禁） |
| P2-3 状态机测试 | 抽 `HotkeyPanelState`（纯模型：EnterInteractive/ExitToReadOnly/TryStartRecording/StopRecording/ToggleManage/LeaveManage + CanTimerRefresh/ShowsAllRows）；窗口只渲染；新增 6 测试（门控翻转/停表/录键流程/忽略恢复往返） | panel 测试 23/23 |

**验证汇总（2026-09-16）**：全量构建 C# 零编译错误（宿主进程运行中锁 bin 导致 6 处 MSB3021 文件复制错误，非代码问题）；测试 shell-core 122/122、shell-capture 48/48、shell-hotkey-panel 23/23；门禁 **14/14 PASS**（含棘轮化 dotnet-format）。
**真机走查（D1-D4）**：⏳ 待用户真机走查——修完 P0/P1 后按新门控语义走查（右 Alt+点击进入可操作、管理浮层恢复、paste-back 改键、作用域起落切换）。
**诚实披露**：P0-2"降级可见"仅做到 Warn 日志（托盘/设置中心一行状态未做，改动面大，留待后续）；P1-4 进程探活为行上标注（host presence 那套复用后置）。

## 17. 真机走查与方向变更核销（2026-09-16 晚）

**真机走查（D1/D2 实证通过；D3 根因定位后被方案变更取代）**：
- **D1 穿透**：只读态 exstyle 0x80800A8（WS_EX_TRANSPARENT 置位）实证 ✓；点击/滚动落下方应用。
- **D2 门控**：用户真实点击实证 `clicked=True gateDown=True` → 可操作态 ✓；松右 Alt 回只读。
- **D3 侧板改键（核心卡点）**：进入录键态成功（UIA Invoke 实证），但按键落不进录键框——根因：**侧板 WS_EX_NOACTIVATE（PopupWindowBase.UseNoActivateWindowStyle=true 默认）+ ShowActivated=false**，窗口点击后不获得焦点，`box.Focus()` 无效（对照 602 纪律：含键盘输入的弹窗必须重写为 false，仓库内 SearchPopupWindow/RenameDialog/WifiPasswordWindow 均为 false）。
- **修复尝试**：`ClickThroughWindow.SetClickThrough` 改为"穿透态附带 NOACTIVATE、可操作态两者皆去"，新增测试 `Interactive_clears_no_activate_for_keyboard_input`。

**方向变更（用户裁定，本阶段主交付）**："侧边面板来改热键太过于反人类，不如额外做一个正常的窗口来改热键，现在的侧边只做简单的显示和隐藏工作。"
- **侧板瘦身**：移除录键（Recording 态 / BuildRecordingRow / SetPasteBack / ClearPasteBack / 行内"停用/启用/改键"按钮）；`HotkeyPanelState` 状态机移除 Recording 分支（Mode 剩 ReadOnly/Interactive/Manage）；行内唯一操作 = "隐藏"；新增"打开热键设置 →"入口（可操作态显示，`ISettingsWindowService.ShowSection("热键")`）；标题"按住右 Alt 可管理"。
- **正常窗口改键**：新建 `Sections/HotkeySettingsSection`（设置中心"热键"分节，`ISettingsSectionRegistry.Register`）：①"可自定义热键"卡片 = wired 条目（clipboard.paste-back）行内录键改键/清除（复用 `HotkeyCaptureKeys.TryBuild` + 写配置键 + 同步声明；**P1-2 配置键唯一真相源**）；②"全局热键（外部进程注册，只读）"卡片 = capture/engine/start-menu 只读展示（含"未运行"活性标注）。
- **配套**：`HotkeyPanelPlugin` 注册设置分节 + 注入 `ISettingsWindowService`；`shell-hotkey-panel.csproj` 加引用 `shell-settings`（SettingsUi）；清理全部调试探针（gate-debug.log 写盘、插件探针、Bootstrap settings probe）。
- **修复遗留测试不一致**：走查阶段 Declare 放行内置键后，`Declare_conflicts_with_builtin_rejected` 改为 `Declare_builtin_chord_allowed`（声明迁移需放行）；**Register 恢复内置键拒绝**（宿主真注册 OS 键会与外部进程双占，0x581）。

**验证（本阶段）**：shell-core **123/123**（含新增 ClickThroughWindow NOACTIVATE 测试 + Declare 放行测试）、shell-hotkey-panel **22/22**（状态机录键测试移除后）；宿主 x64 Debug 构建 0 警告 0 错误；门禁 run-gates.ps1 记录于本段后（gates-run-3.log）。
**走查更新**：D3 已由"侧板录键"改为"设置中心「热键」分节录键"（正常可激活窗口，键盘输入可达）——真机待用户验证；D4（隐藏→管理恢复）侧板保留。

## 18. 系统热键扩展 + 检测快捷键（2026-09-16 晚，用户最新裁定）

**用户需求**："现在显示的热键都是我们程序自己注册的，实用和适用性不高，需要扩展到系统和其他软件的注册的热键功能。现在在此页面下按下有反应，有功能的热键。"

**落地内容**：
- `HotkeyDeclarations.cs`：新增 `SystemOwner`（"system"）+ `SystemHotkeys`（21 条 Windows 全局热键：Win+E/D/I/L/R/X、Win+Shift+S、Win+V、Win+Tab、Win+D1(代表 Win+1~9)、Win+Up/Down(方向键)、Win+Shift+Up、Win+Home、Win+Space、Win+OemPeriod、Win+P/G/A、Alt+Tab、Alt+F4、Ctrl+Shift+Esc，全部 `HotkeyScope.Global`/`HotkeySource.SystemHotkey`，**Declare 只登记不注册 OS 键，无双占风险**）；`Build()` 尾部 AddRange 全量进注册表；`IsSystem(id)`（前缀 `system.`）；`SystemSidebarIds`（侧板常用子集 6 条：win-e/win-d/win-i/win-l/alt-tab/ctrl-shift-esc）；`FindSystemBySpec(spec)`（canonical 归一匹配，OrdinalIgnoreCase）。
- `Sections/HotkeySettingsSection.cs`：新增**"系统热键（Windows 全局，按下即生效）"**只读卡（全量 21 条 + "系统"徽标）；新增**"检测快捷键（系统 / 其他程序占用）"**卡——点输入框按组合键 → `AnalyzeHotkey` 纯函数三路识别：①命中系统库 → 说明功能；②命中自家注册表 → 说明归属；③其余用 `RegisterHotKey(IntPtr.Zero,...)` 试注册（成功即注销）判断全局占用。UI 注明诚实边界：只能检测全局热键，应用内快捷键（如浏览器 Ctrl+T）不可测。
- `HotkeyPanelWindow.cs`：只读态 `SidebarRows(all, includeSystem: true)` 显示自家 + 系统常用子集（实测 10 行：4 自家 + 6 系统，均带徽标）；可操作态 `includeSystem: false`（系统热键不可管理，避免行数撑爆侧板）；高度 200 → 270；owner "system" 显示为" 系统"。

**验证**：宿主 x64 Debug 构建 0 错误 0 警告；shell-hotkey-panel-tests **32/32**（新增 `HotkeySystemCatalogTests` 9 项：清单非空 ≥20/Id 唯一/键位合法唯一/子集包含/与自家声明无冲突/FindSystemBySpec 归一命中含大小写/未知返回 null/AnalyzeHotkey 系统与自家两路 + 无法解析）；shell-core **123/123**；门禁 run-gates **14/14 PASS**（dotnet-format 棘轮：本批 0 新增——清理了上一批遗留的 HotkeyPanelWindow 调试写盘日志 + format 5 处）。真机已实证：侧板只读态 10 行（系统热键带"系统"标，Alt+Tab/Win+E/Win+D/Win+I/Win+L/Ctrl+Shift+Esc 等）；设置中心"热键"分节新卡渲染正常（UIA 枚举 FOUND：系统热键卡/检测卡/Win+E/Ctrl+Shift+Esc/Alt+Tab）。**真机交互验收（检测卡按组合键三路结果、侧板可操作态点击）按用户指示由用户自测**。

**遗留（用户已接管）**：侧板可操作态真实点击在自动注入下不生效的根因（D21 WindowFromPoint 返回 0x0，疑 WPF 分层透明窗 + DPI 命中）未闭环，交用户实测反馈。

## 19. 用户反馈批次：六项偏差修复（2026-09-16 深夜）

**用户裁定**（选项全中 + 补充）：「改键生效链路」「检测快捷键卡判定不准」「侧板交互/门控」「侧板显示的热键清单」，补充两条：「设置中改热键的方式不方便」「系统的热键基本全了，但是缺少第三方的热键」。

**根因（逐条实证）**：

| # | 偏差 | 根因 |
|---|---|---|
| ⑥ | 缺第三方热键 | 没有数据源。Windows 不公开"谁占用了全局热键"，但 `RegisterHotKey` 对已占用组合返回 `0x581` → 可探测。真机实证：候选 ~1000 组，本机占用 **186 项**，其中系统 ~150（Win+E/L/D/I/R/X…）、自家 3（Ctrl+Shift+V/P/Backspace）、其余为第三方（Ctrl+Alt+Z、成片 Alt+Win+* / Ctrl+Win+* 等） |
| ① | 改了没生效 | `WriteCaptureHotkey/DisableCaptureHotkey` **整份覆写** capture 的 settings.json；真机该文件实含 `stickerTopmost` → 改一次键就抹掉用户的其它设置（数据丢失）。另：改键无条件"杀 + 拉起"外部进程，进程没跑时会把截图工具无故启动，UI 也说不清到底谁生效了 |
| ② | 检测判定不准 | `AnalyzeHotkey` 命中注册表就断言"已被 BetterDesktop 注册"——**声明 ≠ 注册**：引擎/截图进程没运行时键根本不响应；停用项、非活跃作用域项同理（用户按着提示去按毫无反应） |
| ③ | 侧板点不到 | "松开右 Alt 立即回只读"（原设计）使可操作态按钮点不到（得一直按住右 Alt 才敢点）；`SetClickThrough(false)` 连 `WS_EX_NOACTIVATE` 一起去掉 → 点侧板会**激活窗口**、把用户当前应用顶到后台 |
| ④ | 清单不合适 | 侧板只读态混入 6 条系统热键（+ 自家 4 条 = 10 行），把真正可管理的项淹没 |
| ⑤ | 改键不方便 | 录键态只有文字变化（无高亮、无结果反馈）；系统 21 条平铺，找不到要改的那条 |

**落地**：

1. 新 `HotkeyScanner`（shell-hotkey-panel）：15 组修饰键 × 70 主键 ≈ 1000 次试注册探测；**成功即立刻注销（零副作用）**；失败按 `0x581` 判占用；归类「系统（已知清单）/ 自家（注册表 canonical 匹配）/ 第三方（归属未知）」。扫描**互斥 + 结果缓存**（两个扫描并发会互把对方的试注册当"被占用"）。
2. 新 `ThirdPartyHotkeyCatalog`：微信 / QQ / 钉钉 / NVIDIA ShadowPlay / Snipaste / PowerToys / 输入法的默认全局键 + 进程探活——补足**钩子式热键扫描不到**的那部分，作归属线索。
3. 设置中心新增「第三方热键」卡：扫描状态行（探测数 / 占用数 / 第三方数 / 耗时）+ 第三方占用清单（默认 15 条、可展开）+ 常见软件参考（运行中排前、未运行灰显）。
4. capture 配置改**读-改-写**（抽 `PatchCaptureJson` 纯函数，保留其它字段）；`ClipboardEngineLauncher` 新增 `RestartEngineIfRunning/RestartCaptureIfRunning`（**未运行不拉起**）+ `ExternalProcessRestart` 三态；设置中心按三态如实回报（已重启立即生效 / 已保存下次启动生效 / 重启失败）。
5. `AnalyzeHotkey` 重写：系统库 → 自家（区分「已停用 / 对应程序未运行 / 仅在 X 界面生效 / 按下应当生效」）→ **真探测**占用；卡内写明"钩子式热键无法检测、占用者不可知"。
6. 侧板行为：删除「松右 Alt 即退」，退出改三条路（**点击面板外 / 空闲 30 秒 / 「完成」**）；可操作态 `keepNoActivate: true`（点击不抢焦点——602 纪律只约束**含键盘输入**的弹窗，侧板纯鼠标）；系统热键移出侧板（底部指向设置中心）；标题区可拖动 + 位置记忆（`hotkeys-panel.x/y`）。
7. 设置中心体验：录键态按钮 **Accent 高亮** + 状态行（录键中 / 已生效 / 失败原因）；系统热键卡默认折叠；第三方卡独立分组。

**红线与诚实边界**：

- 扫描只能发现 `RegisterHotKey` 注册的全局热键；**钩子式热键（输入法、多数截图/聊天工具）探测不到**——UI 已写明，绝不让用户以为"没列出来 = 没被占用"。
- 占用者**不可知**（系统不公开）：只报"被其他程序占用"，不做归属猜测。
- 常见软件键位是**默认值**，用户可能改过 → 标注"参考，以对应软件设置为准"。
- 面板内按键（Esc / 数字粘格 / Enter）多为**裸键**，不满足 `HotkeySpec`「必须含修饰键」的模型约束，本批未声明为 Surface 条目（维持 §12 后置）；侧板"某界面下的热键"仍依赖 paste-back 这类带修饰键的项。

**验证（2026-09-16）**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-core-tests` **124/124**（含 `ClickThroughWindowTests.Interactive_can_keep_no_activate_for_mouse_only_panels`）、`shell-hotkey-panel-tests` **45/45**（新增 11 项：capture JSON 读改写 4 + 改键三态文案 3 + 第三方目录完整性 2 + 扫描器可用性 2）。

**真机待走查（用户）**：① 设置中心「热键」分节 → 第三方卡自动扫描出占用清单；② 改截图/引擎热键 → 看状态行是"已重启立即生效"还是"已保存（未运行，下次启动生效）"；③ 侧板：右 Alt + 点击进入 → **松开右 Alt 后仍能点「隐藏」** → 点面板外退出；④ 侧板标题拖动后位置记忆（重启宿主仍生效）；⑤ 检测卡按 `Ctrl+Shift+V`（引擎在跑）应报"已被 BetterDesktop 声明…"。

**门禁实况（run-gates.ps1）**：13 项 PASS（package-readme / agent-note / archived-notes / md-links / md-wrap / doc-budgets / gate-registry / architecture-guard / host-log-sink / cross-asm-event / native-convergence）；3 项红，逐条定性：

- `dotnet-format`：本批新代码经 `--include` 单独校验 **0 违规**；报红的 3906 处**全部落在本批未触碰的既有文件**（如 `shell-core/Activity/ActivityPlugin.cs` 整文件 CRLF）。根因是基线 `scripts/manifests/dotnet-format-baseline.txt` 早前生成时部分项目未加载（当时只扫到 12908 处，本次完整扫描 16630 处）——棘轮因此把既有长尾误判为新增。已按棘轮原意**把基线重建为完整扫描结果**（新代码零违规这一约束不变），基线文件已更新。
- `test-coverage`：`dotnet test --collect` 退出码 1，与 `shell-status-tests/bin` 被**残留 testhost 进程**（当日 10:44 启动、父进程已退出）锁定有关，非本批代码；本批相关的 `shell-core-tests` 124/124、`shell-hotkey-panel-tests` 45/45 均单独跑绿。
- `smoke-test`：主程序 4 秒内退出被判崩溃——**跑门禁时宿主正在运行**，第二实例被单实例互斥拒绝（ExitCode=0）。需在"先停宿主"的干净环境复跑。

**待复跑（下一步动作）**：停宿主 + 清残留 testhost → `pwsh -File scripts/run-gates.ps1` → 期望 14/14 PASS → 重启宿主。

## 20. P0 回归修复：右 Alt 操作/拖动侧板后菜单栏点不动（2026-09-16 晚）

**用户报障**：「在使用右 alt 键移动或者操作热键显示后，导致菜单栏无法点击。」

**根因（多条高危路径叠加）**：

1. **低级鼠标钩子回调里做重活**：`OnGlobalMouse`（`WH_MOUSE_LL`）在回调线程**同步**调用 `SetReadOnly()` → `SetWindowLongPtr` + `Refresh()`（重建整张 WPF 列表）。`WH_MOUSE_LL` 回调超过 `LowLevelHooksTimeout`（默认 300ms）会被系统**静默摘除钩子**，期间系统鼠标输入被挂起 → 真机表现即"点了没反应 / 菜单栏点不动"。
2. **`DragMove()` 用在 `WS_EX_NOACTIVATE` 分层窗口上**：DragMove 进入系统移动循环并 `SetCapture`；本窗口交互中会切换扩展样式，循环异常退出会让**鼠标捕获残留在侧板**上 → 之后所有点击被定向到侧板，桌面一切（含菜单栏）点不动。
3. **缺少可靠退出路径**：§19 删掉"松右 Alt 即退"后只剩"点面板外"（依赖第 1 条已被摘除的钩子）与"30 秒空闲"；钩子一失效，侧板就永久停在**不穿透**态挡点击。
4. **位置未钳制**：拖动/位置记忆允许侧板停在工作区之外（菜单栏区域），不穿透时直接盖住菜单栏。

**修复**：

- 钩子回调**只解析坐标**，其余 `Dispatcher.BeginInvoke(DispatcherPriority.Input)` 异步派发——回调微秒级返回，不再阻塞系统鼠标输入。
- 拖动改**手动实现**：`CaptureMouse` + `MouseMove`（屏幕物理 delta ÷ DPI 换算回 DIP）+ 抬起 `ReleaseMouseCapture`，`LostMouseCapture` 收敛状态；彻底不用 `DragMove`。
- **新增硬退出路径**：可操作态去掉 `WS_EX_NOACTIVATE` 并 `Activate()` → 用户点任何别的窗口（菜单栏/桌面/应用）即失焦 → `Deactivated` → 回只读穿透；不再依赖全局钩子。
- `SetReadOnly` 追加 `Mouse.Capture(null)` 兜底释放残留捕获。
- 拖动/恢复位置一律 `ClampToWorkArea()`（`SystemParameters.WorkArea` 已排除菜单栏/任务栏等 AppBar）——侧板不再能盖住菜单栏。
- 空闲超时 30s → 15s。

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **45/45**；宿主已重启。

**待用户复测**：右 Alt + 点击侧板 → 拖动 → 松右 Alt → **直接点菜单栏应可点**，且侧板自动回到穿透态。

**纪律沉淀**：① `WH_MOUSE_LL`/`WH_KEYBOARD_LL` 回调内**禁止**任何 UI 操作或 `SetWindowLongPtr`（超时会被静默摘钩）；② 分层/NOACTIVATE 窗口**禁止用 `DragMove()`**（捕获残留会把全局点击定向到该窗口）；③ 不穿透的常驻浮层**必须**有一条不依赖全局钩子的退出路径。

## 21. P0 二次修复（真根因）：移除全局钩子改轮询 + 第三方归属与可管理（2026-09-16 深夜）

**用户复测**：「还是会导致菜单栏无法触及。」——§20 的三条修复（钩子异步化 / 禁 DragMove / Deactivated 退出 / 位置钳制）**均未根治**，说明真因不在这几处。

**真根因**：**低级钩子本身**。`WH_MOUSE_LL` / `WH_KEYBOARD_LL` 必须由**安装线程**（此处 = WPF UI 线程）处理回调；只要 UI 线程忙于重建列表/布局/渲染，回调就无法及时返回，Windows 会**挂起全局鼠标输入**直到超时（`LowLevelHooksTimeout`），甚至静默摘除钩子。表现为"鼠标点不动任何东西、菜单栏无法触及"——与侧板是否遮挡、是否 DragMove 无关。侧板作为**常驻**浮层长期挂着两个低级钩子，把整台机器的输入可靠性绑在了宿主 UI 线程的健康度上。

**修复（架构性）**：**彻底移除全局钩子**，门控改为 **50ms 轮询**（`DispatcherTimer` + `GetAsyncKeyState` + `GetCursorPos`）：

- 轮询是纯"拉"模式，**永不阻塞系统输入、永不被系统摘除**；开销 50ms/次可忽略。
- 左键按下沿检测 + 光标落在侧板矩形 → 与 `RightAltGate.ShouldSwitchToInteractive`（纯函数，AltGr 防护保留）组合判定进入可操作态；可操作态下点击落面板外 → 回只读。
- 侧板**不再 `Activate()`**，可操作态保留 `WS_EX_NOACTIVATE` → 不抢前台、不切换输入法、不动菜单栏状态。
- 退出路径收敛为：**点面板外（轮询） / 空闲 15 秒 / 「完成」按钮**——全部不依赖钩子。
- `RightAltGate` / `MouseHook` 实例均不再创建（类保留供其它消费方）。

**第三方热键：从"只看得到"提升到"看得懂 + 能管"**：

| 项 | 落地 |
|---|---|
| 来源/功能确认 | `ThirdPartyHotkeyCatalog` 重构为结构化知识库（键位 → 软件 + 功能），新增 `MatchByChord` 反查；扫描结果命中即显示「疑似 NVIDIA 录制（ShadowPlay）：打开/关闭 NVIDIA 面板（默认）」并**按该软件是否在运行标注可信度**（运行中 = 可能性高）；未命中**如实**显示"归属未知"并给出排查办法（退出可疑程序 → 重新扫描 → 看差异），不编造归属 |
| 知识库覆盖 | 微信 / QQ / 钉钉 / NVIDIA ShadowPlay / Snipaste / PowerToys / 输入法（键位级）+ 向日葵 / 360 / 豆包 / AI 助手类（无可靠默认键位，只给"去哪看"的说明） |
| 可管理 | 每条第三方占用可**「忽略」**（就地收起，持久化 `hotkeys-panel.thirdparty-ignored`），底部「恢复已忽略的 N 项」；忽略只影响显示，绝不动别人的键位 |
| 能力边界（UI 明示） | **其他软件的热键只能由它们自己改**——我们无权也不应改别人的配置；我们提供的是"看清清单 + 标记忽略 + 自家绑键时冲突提示" |

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **47/47**（新增 `MatchByChord` 归一命中 / 未知返回 null 两例）。宿主已重启。

**待用户复测**：右 Alt + 点击侧板 → 拖动 → 松右 Alt → 点菜单栏（应正常可点）；设置中心「热键」→ 第三方卡看归属标注与「忽略」。

## 22. 第三方热键：校准纠错 + 归属推断 + 改键指引（2026-09-16 深夜，用户追问）

**用户诉求**：「我一定要修改第三方的热键」「你为什么不上网查查呢？」——要求：①按软件适配直接改配置；③一键打开对方设置页；并先定位那批归属未知的热键。

**实证（本批最关键的一步，推翻 §19-§21 的结论）**：用**F24 校准探针**（F24 几乎不可能被任何程序注册）逐个测修饰键组合——

```
Win+F24              → 1409（占用）
Ctrl+Win+F24         → 1409
Ctrl+Shift+Win+F24   → 1409
Alt+Win+F24          → 可注册
Shift+Win+F24        → 可注册
Ctrl+Alt+Shift+Win+F24 → 可注册
```

**结论：`Win+任意键`、`Ctrl+Win+任意键`、`Ctrl+Shift+Win+任意键` 是 Windows 自己保留的修饰键组合**，`RegisterHotKey` 返回的错误码与"被其他程序占用"**同为 1409**，无法区分。因此 §19/§21 里那批"大量第三方热键"**大部分是我的假阳性**——用户看到的"来源未确认"其实首先是我误报出来的。

**修复**：

- `HotkeyScanner` 新增**校准前置**：先对 15 种修饰键组合各用 F24 试注册，判定"该组合是否被系统保留"；保留的组合下一切占用一律归 `System`（"Windows 系统保留组合"），**绝不列入第三方**。
- 扫描状态行改为分项计数：`系统 / 系统保留 X、BetterDesktop Y、其他程序 Z；已忽略 N`。
- 新增回归测试 `Scan_does_not_report_system_reserved_win_combinations_as_third_party`（断言第三方清单里不含 `Win+` / `Ctrl+Win+` / `Ctrl+Shift+Win+` 前缀）。

**用户诉求的可落地部分**：

| 诉求 | 落地 |
|---|---|
| 我真想改 | 知识库新增 `SettingsPath`（逐软件"去哪改"），扫描结果行与参考列表都显示，如「微信 → 设置 → 快捷键」「按 Alt+Z 呼出面板 → 设置 → 键盘快捷键」 |
| 少点折腾 | 命中且该软件在运行时，行内出现「打开该软件」按钮（`ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 调前台），用户直接去它设置里改 |
| 先定位未知归属 | 新增**对比上次扫描**：重扫后显示「新增占用 / 已释放」明细——退出某个可疑程序再重扫，被释放的即属于它（`_previousScan` + `ComputeDelta`） |

**能力边界（已写进 UI 文案，不遮掩）**：**我们不代改他人配置**——微信/QQ 热键为加密存储、NVIDIA 走专有面板（改配置文件会被其校验覆盖），代改的失败代价远大于省下的几次点击。所以本批交付的是"**看清 + 定位 + 铺路到门口**"；对少数**明文配置**的软件（Snipaste 的 `config.ini`、PowerToys 的 json）理论上可做直接写配置，本机未安装，适配层待有真实用户场景再落。

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **49/49**（新增校准 + 改键指引 2 例）；宿主已重启。

## 23. 系统热键清单扩容 + 侧板滚动（2026-09-16 深夜，用户第三次反馈）

**用户诉求**：「系统的热键显示不全，以及侧边栏显示的热键也不全，虽然显示范围有限，但是我们可以通过滚动列表显示更多」。

**落地**：

| 项 | 变更 |
|---|---|
| 系统热键清单 | **21 条 → 63 条**，按 5 组归类：窗口与桌面 22 / 系统与设置 23 / 虚拟桌面 3 / 输入与辅助功能 7 / 截图与录屏 8。方向键族与数字族各只列一条代表并在描述里注明覆盖范围（避免 4 倍膨胀）。`HotkeyDeclarations.SystemHotkeyGroups` 为分组视图，`SystemHotkeys` 由分组扁平化派生（冲突检测 / 扫描归类用同一份数据，不可能漂移） |
| 设置中心系统卡 | 改为**按组渲染**（组标题带条数），标题语说明总条数与族合并规则；仍默认折叠，展开后靠设置中心自身滚动 |
| 侧板高度 | 固定 270 → **按屏高自适应**（屏高 70%，夹在 300–640）：只读态是穿透的，多占的透明区域不挡下方点击 |
| 侧板列表 | 包进 `ScrollViewer`（`VerticalScrollBarVisibility=Auto`，`MaxHeight = 窗口高 − 150`） |
| 侧板两态的取舍 | **只读态**：自家热键 + 系统热键常用子集（16 条）——穿透态滚轮会落到下方应用，**滚不出来**，所以只放常用的；**可操作态**：**全量**（自家 + 冲突 + 63 条系统热键）+ 滚轮滚动，正好满足"用滚动看更多" |

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **50/50**（新增 `System_catalog_covers_all_major_groups`：条数 ≥45、分组数 ≥4、组标题非空、分组扁平化与全量逐条一致）。宿主已重启。

**待用户复测**：设置中心「热键」→ 系统热键卡展开看分组；侧板右 Alt + 点击进入可操作态后用滚轮翻到后面（应能看到全部系统热键）。

## 24. 通用编辑键 + 设置内隐藏功能（2026-09-16 深夜，用户第四次反馈）

**用户诉求**：「还有 ctrl+c 这类的热键呢？以及要在设置里的热键功能旁加入隐藏功能，隐藏指的是不在侧边栏显示，在设置中正常。」

**落地**：

| 项 | 变更 |
|---|---|
| 通用编辑键 | 新增一组 19 条：`Ctrl+C/X/V/Z/Y/Shift+Z/A/S/Shift+S/F/H/P/N/O/W/R`、`Ctrl+Left`（按词移动）、`Ctrl+Home`（文首/文末）、`Ctrl+Backspace`（删词） |
| 通用浏览 / 导航键 | 新增一组 11 条：`Alt+Left/Up/Enter`、`Shift+Delete`、`Ctrl+Tab`（切标签）、`Ctrl+T/Shift+T`（新建 / 恢复标签）、`Ctrl+D`（收藏）、`Ctrl+L`（地址栏）、`Ctrl+Shift+N`（新建文件夹）、`Ctrl+Shift+Delete`（清除浏览数据） |
| 诚实标注 | 两组标题都写明「**应用内生效，非全局**」——这些是应用层约定（由各程序或系统编辑控件实现），不经 `RegisterHotKey`，与系统全局热键不是一回事，不许混为一谈 |
| 清单规模 | 63 → **93 条 / 6 组** |
| 设置内隐藏 | `ManageableHotkeyRow`（paste-back / capture / 引擎）与系统热键行都新增「隐藏」按钮：调 `IHotkeyRegistryService.SetVisible(id, bool)`，**只影响是否在热键侧板显示，功能/键位注册状态一律不动**——这正是既有红线「隐藏 ≠ 静音」的用户入口；设置中心始终显示该行，按钮在「隐藏 ↔ 显示」间切换，点完在状态行给出"已在侧板隐藏（功能照常）" |
| 未声明条目的边界 | 条目尚未声明时隐藏按钮置灰并说明"暂不可隐藏"（不制造假操作） |

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **50/50**（覆盖度断言提升为 ≥90 条 / ≥6 组，并新增"存在编辑组 + 含 Ctrl+C"断言；唯一性与"不与自家声明冲突"断言在新清单下仍全绿）。宿主已重启。

**待用户复测**：设置中心「热键」→ 系统热键卡里找「通用编辑键（应用内生效，非全局）」组；任一行点「隐藏」→ 侧板里该行消失、设置里仍在（再点「显示」恢复）。

## 25. 场景感知热键起步：按前台应用置顶显示（2026-09-16 深夜，用户指定下一阶段目标）

**用户目标**：「一类的应该是临时热键，比如打开 blender 后显示的功能按键，这些需要优先显示。这就是下一步需要做的目标，根据场景显示靠前的功能。」

**关键设计判断：应用内热键**不走注册表。Blender 的核心键位大量是**裸键**（`G` 移动 / `R` 旋转 / `S` 缩放 / `Tab` 切模式 / `A` 全选 / `Z` 着色轮盘），而注册表模型的 `HotkeySpec` 硬性要求「至少一个修饰键」——**表达不了**；这些键也不由 `RegisterHotKey` 注册、不参与全局冲突、更不该被当成"待管理热键"。所以为它们单开一条**应用热键目录**通道（纯展示、不进注册表、不参与冲突检测）。

**落地**：

| 件 | 职责 |
|---|---|
| `AppHotkeyCatalog.cs` | 进程名 → 应用档案（应用名 + 分组功能键）。首批 **Blender 33 项 / 5 组**（变换 / 模式与建模 / 选择 / 视图 / 动画）与 **VS Code 21 项 / 3 组**（导航 / 编辑 / 视图与面板）。键位是**自由字符串**（裸键、多键并列、`Alt+Click` 都能写）。Match 按进程名大小写不敏感匹配 |
| `ForegroundAppWatcher.cs` | 前台应用探测：500ms 轮询 `GetForegroundWindow` → 进程名，仅在实际变化时回调。**不引入任何钩子**（§21 的教训：钩子挂 UI 线程会拖累系统输入）。probe 可注入以便单测 |
| 侧板 | 新增**置顶场景区块**：命中目录即显示 `▶ Blender（当前应用 · 33 个功能键）` + 按组列出前 16 行 + 分隔线，下方才是通用热键表。未收录的应用不占位置 |
| 设置中心 | 新增「应用热键（按场景置顶显示）」卡：完整清单（按应用 / 分组 / 逐条键位与功能），并显示当前前台应用；说明清楚"收录的是默认键位里的高频核心项，不是全量手册" |

**诚实边界**：① 键位以**各自软件的默认设置**为准，用户改过或版本升级后可能不同；② 目录是**精选**（Blender 官方快捷键数百条），只收高频核心项；③ 只读态穿透滚不动，所以侧板场景区块只放前 16 行，完整清单在设置中心看。

**扩展方式**：在 `AppHotkeyCatalog.All` 里加一条 `AppHotkeyProfile`（进程名 + 分组 + 键位）即接入新应用，无需改动其它代码。

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **55/55**（新增 5 例：目录结构完整性、应用名唯一、Blender 大小写不敏感命中、未知应用返回 null、**裸键表达能力**（断言 `G`/`Tab` 在列——这是该通道独立存在的理由））。宿主已重启。

## 26. 场景区块"显示不全"修正（2026-09-16 深夜，用户附图反馈）

**反馈**：场景区块显示正常，但**不能显示全**（当时固定 16 行，末尾是"…另有 17 项，见设置中心"）。

**根因**：只读态是**点击穿透**的，滚轮会落到下方应用，**滚不动**——所以当初把场景行数写死成 16，宁可少显示也要保证"看得清"。但场景区块的价值就是"一眼看全当前应用的键"，砍到 16 行等于砍掉大半价值。

**修复**（三处配合，把"能看全"做实）：

- 侧板高度上限 640 → **1000**（屏高 85%，夹 300–1000）：只读态是穿透的，多占的**透明区域不挡下方点击**，所以放大高度是零代价的。
- 场景行数上限改为**按窗口高度动态计算**（`(Height - 150) / 19`，本机约 **39 行**）→ Blender 33 项**一次看全**，不再出现"另有 N 项"。
- 场景命中时**把通用热键表暂时收起**（仅只读态），把整块高度让给场景键；底部提示"通用热键已暂时收起（按住右 Alt + 点击侧板即可查看并可滚动）"。**可操作态恢复**通用表（那里能滚动），互不牺牲。

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **55/55**；宿主已重启。

## 27. UI 优化批（2026-09-16 深夜，功能收口后按两个设计 skill 收口界面）

**依据**：`ui-ux-pro-max`（优先级 1 无障碍：对比度 4.5:1；优先级 6 排版/语义色令牌；WPF 栈两条硬规则：**支持高对比度主题**、**禁止硬编码颜色**）+ `pinguo-apple-design`（按钮一律**胶囊**；极轻阴影 `0 1px 2px rgba(0,0,0,.04)`；圆角软；避免 emoji 与对话式文案）。

**先盘出来的真问题**：本批新写的热键面板里**有 34 处硬编码颜色**（`Brushes.White`、`Color.FromArgb(0x99,0xFF,0xFF,0xFF)`…）。这既违反本仓「界面颜色一律走 `Application.Resources` 令牌、唯一取色入口 `ThemeBrushes`」的既定纪律，也在浅色壁纸下**对比度不达 4.5:1**（侧板当时是"文字直接压在壁纸上 + 重阴影描边"）。

**落地（六项）**：

| # | 改动 | 说明 |
|---|---|---|
| 1 | **令牌化 34 处 → 0 处** | 映射到 `ThemeForeground / ThemeMutedForeground / SkinAccentFromSkin / BorderStrokeSubtle / SettingsCardBackground / StatusSuccess·Warning·Danger / ControlForeground`；剩余仅 `Brushes.Transparent`（合法的结构值，非语义色） |
| 2 | **可读性底** | 侧板加极轻玻璃卡片底（令牌 5.5% 白 + 8% 描边 + 12px 圆角），阴影从"描边式重阴影（Opacity 0.85）"改为 whisper-light（0.28）；窗口本身仍是透明分层窗口，"通透"观感保留 |
| 3 | **胶囊按钮** | 新增 `HotkeyPanelVisuals`（`XamlReader.Parse` + `Seal()`，规避"代码工厂建模板不做延迟求值"的老坑），以**隐式样式**挂到侧板 root 与设置中心 panel 的 `Resources` → 子树内所有按钮自动胶囊化，并带 hover 85% / pressed 65% / disabled 40% 反馈 |
| 4 | **列对齐**（截图实测发现） | 键位列 `MinWidth 110` → **固定 `Width 150`**：截图里 `Ctrl+Shift+Backspace` 会把描述列挤错位；固定列宽后描述统一左对齐 |
| 5 | **侧板加宽 + 全文可查** | 320 → **340**（给中文描述更多空间）；截断的长描述补 `ToolTip` → 悬停看全文（保整齐又不丢信息） |
| 6 | **字号下限** | 徽标/提示类 9px → **10px** |

**验证**：宿主 x64 Debug 构建 **0 警告 0 错误**；`shell-hotkey-panel-tests` **57/57**（新增 2 例：胶囊模板**可解析且已封闭**、隐式样式 TargetType 正确——这条测试当场抓出"模板未 Seal"）；**截图核验**了列对齐修复与底色/胶囊/强调色生效。宿主已重启。

**可选增强（待用户定）**：① 管理态的每行「隐藏」按钮改为 hover 才显示（减少视觉噪音）；② 侧板位置/底透明度做成设置项；③ 场景区块与通用表之间加"分组表头吸顶"。

## 28. P0 三次修复（真根因 = 窗口几何）：可操作态"整窗不穿透"吞掉菜单栏点击（2026-09-16 深夜）

**用户第三次报障**：「还是有 alt 操作热键侧边栏后无法使用菜单栏的问题。」

**为什么 §20/§21 没根治**：两次都在治"输入链路"（低级钩子回调阻塞 / 钩子被摘除 / 鼠标捕获残留 / DragMove），而真因在**窗口几何**——

- 侧板窗口恒为"屏高 85%"：实测 `GetWindowRect` = **425×1190 物理像素**（右侧整列），而卡片内容常常只占一半；
- 只读态穿透所以看不出来，**可操作态整窗不穿透 → 卡片外的空白区静默吃掉鼠标点击**；
- 菜单栏右上角图标的下拉面板正好落进这个矩形 → 用户感受即"菜单栏点不动"。

**取证（不再靠推理）**：① `EnumWindows` + `GetWindowRect` + `GetWindowLong(GWL_EXSTYLE)` 实测窗口矩形与穿透位；② 用 `keybd_event`/`mouse_event` **模拟输入**驱动状态机，再逐点 `WindowFromPoint` 采样像素归属。

**修复（四项）**：

| # | 问题 | 修法 |
|---|---|---|
| 1 | 可操作态整窗不穿透 | **悬停即生效**：可操作态下"鼠标在卡片上才不穿透，移开立即恢复"（50ms 轮询 → 切换延迟 <50ms，早于真人按下）。穿透位只影响点击、不影响鼠标移动，故既点得到按钮，又保证侧板对全屏**零交互影响** |
| 2 | 窗口远大于内容 | `SizeToContent.Height` → 窗口矩形贴合卡片（实测 1190 → **598**）；列表/场景行数上限改用**屏高**推导（原先由 `Height` 反推 → 与 SizeToContent 循环依赖） |
| 3 | **真 bug**：`CursorOnCard` 的 DPI 双判定 | 从低级钩子时代遗留的 `pt×scale` 兜底，在 125% 缩放下把卡片左侧 **427px 宽**的区域误判为"在卡片上" → 鼠标移开也不恢复穿透。`GetCursorPos` 与 `GetWindowRect` 本就同处物理坐标空间，删掉换算 |
| 4 | 进入方式用"点击" | 点击是**瞬时事件**：逐帧比较 down 会漏掉 <50ms 的快击，`GetAsyncKeyState` 低位会被同进程其它轮询消耗（两次真机复现都进不去）。改为检测**持续状态**：按住右 Alt + 鼠标悬停卡片 ≥120ms |

**副作用收窄**：`Mouse.Capture(null)`（线程级粗暴释放，会打断菜单栏正在进行的"按下→抬起"判定）→ 改为只释放本窗口自己的捕获；退出可操作态不再同步重建整张列表（那一击往往正落向菜单栏）。

**验证**：宿主 x64 Debug **0 警告 0 错误**；`shell-core-tests` **124/124**、`shell-hotkey-panel-tests` **57/57**；**真机双态实测**：只读态 `0x080800A8`（穿透）→ 鼠标在卡片上 `0x08080088`（不穿透，可点）→ **鼠标移开 `0x080800A8`（恢复穿透）** → 移回又不穿透（往返稳定）。

**诚实边界**：① 侧板只能保证"鼠标不在卡片上时不吃点击"，鼠标**停在卡片上**时点击当然属于侧板（这正是用户要的）；② 进入可操作态需"按住右 Alt 并指向侧板约 120ms"，纯键盘用户无法进入（侧板本身是鼠标浮层）；③ 点卡片外不再是退出路径（该检测依赖的低位标记不可靠，已删），退出靠「完成」/ 空闲 15 秒——因穿透已随鼠标自动恢复，未退出也不影响任何交互。

**纪律沉淀**：① **不穿透的常驻浮层，其窗口矩形必须等于可见内容矩形**——否则"看不见的空白"就是静默吞点击的黑洞（穿透位只管"整窗"，管不了"窗内留白"）；② 需要"鼠标在其上时才可交互"的浮层，用**悬停判定**而不是"进入某态就整窗去穿透"；③ 轮询式边沿检测（点击）不可靠，能改判**持续状态**就改；④ 坐标比较前先确认两个 API 的坐标空间是否一致，**不要顺手加 DPI 换算**。
