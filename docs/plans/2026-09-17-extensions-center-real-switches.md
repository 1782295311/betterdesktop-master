# Cairo 开发计划：扩展中心接入截屏/热键/灵动岛开关

> Task: 扩展中心去掉占位管理（weather/dynamic-desktop），把截屏、热键侧板、灵动岛三个真实开关接进去。
> 证据基于当前源码验证（小改动）；技术力文档未命中（检索词：扩展中心/开关/启停）。

## Objective (§1)

扩展中心只列出**已接入且可真实启停**的扩展；新增截屏、热键侧板、灵动岛开关，切换立即生效（三个板块的启停机制均已存在，扩展中心只做键接线，零新增机制）。

## Current Behaviour (§2–3)

- `ExtensionCatalog.External` 6 条：quick-note / programs-menu（已接入）、weather / screenshot / dynamic-desktop（**占位**，开关只存意图）、clipboard-history（已接入）。
- `ExtensionsCenterWindow`：`Implemented = { quick-note, programs-menu, clipboard-history }`；开关持久化 `ext.SettingsKey`（`extensions.<id>.enabled`），默认读 `false`；占位条目标注"规划中 · 开关将保存你的选择"。

## Findings (§4–5)

- `[verified]` **截屏**：`agent/Capabilities/CaptureHotkeyOwner.cs` 轮询 `extensions.screenshot.enabled`（10s）——关闭即交还截图热键、打开即接管（计划文档 2026-09-17-desktop-control-standalone.md:743）。`screenshot` 条目的默认键 `extensions.screenshot.enabled` 正好匹配 → 零新增。
- `[verified]` **热键侧板**：`HotkeyPanelSettings.EnabledKey = "hotkeys-panel.enabled"`（默认 true）；`HotkeyPanelPlugin` 订阅 `ShellEvents.SettingsChanged` 统一落地显隐（唯一应用点）。写键即实时生效。
- `[verified]` **灵动岛**：`IslandOptions.EnabledKey = "island.enabled"`（默认 true）；`IslandPlugin.cs:85-96` 已订阅 `island.*` 键变化 → `OnSettingsChanged()` → `Activate()`（禁用拆窗+退订来源，启用重建）。写键即实时生效。
- 验收等价断言：扩展中心开关初始态 = 板块当前实际态（热键/灵动岛默认开，开关显示开；截屏/quick-note 等默认关）；切换后板块即时响应（无需重启宿主）。

## Proposed Changes (§6)

**文件 1 `packages/shell/shell-menu-bar/Contracts/ExtensionCatalog.cs`**
- `ExtensionDescriptor` 加两个字段：`string? SettingsKeyOverride = null`（自定义设置键，默认走 `extensions.<id>.enabled` 约定）、`bool DefaultEnabled = false`（开关初始态）。
- `External` 更新：删除 `weather`、`dynamic-desktop`（占位）；`screenshot` 保留（已是真实开关，键约定匹配；**DefaultEnabled: true**，与 DesktopToggleCatalog.Capture 默认一致）；新增 `hotkeys-panel`（"热键侧板"，`SettingsKeyOverride: "hotkeys-panel.enabled"`，`DefaultEnabled: true`）、`island`（"灵动岛"，`SettingsKeyOverride: "island.enabled"`，`DefaultEnabled: true`）。顺序：quick-note / programs-menu / clipboard-history / screenshot / hotkeys-panel / island。

**文件 2 `packages/shell/shell-menu-bar/Windows/ExtensionsCenterWindow.cs`**
- `Implemented` 更新为 `{ quick-note, programs-menu, clipboard-history, screenshot, hotkeys-panel, island }`。
- `BuildRow` 开关：键用 `ext.SettingsKeyOverride ?? ext.SettingsKey`；默认值用 `_settings?.Get(key, ext.DefaultEnabled) ?? ext.DefaultEnabled`。

**文件 3 `packages/shell/shell-core/DesktopControl/DesktopToggleCatalog.cs`（同步闭环）**
- 新增 `Island` 命令名（"island"）+ `IslandKey = "island.enabled"` + 条目（Default: true, RequiresHost: true）——托盘功能开关 / CLI `--toggle-key` 由此获得灵动岛开关，与扩展中心同键。

**文件 4 `tray/TrayApplicationContext.cs`（同步闭环）**
- 托盘「功能开关」新增 `new("灵动岛", "island.enabled", true, ["--toggle-key", "island"])`（CLI 契约落地，键与扩展中心一致）。

**不做**：Agent/CaptureHotkeyOwner、HotkeyPanelPlugin、IslandPlugin 均不改动（机制已存在）；桌面右键「桌面控制」菜单为手写清单，不加入灵动岛（非桌面控制语义）；删除的占位条目残留设置键（孤儿键，无副作用，不清理）。

## Implementation Sequence (§7)

1. ExtensionCatalog.cs：descriptor 字段 + External 清单更新。
2. ExtensionsCenterWindow.cs：Implemented + 开关键/默认值。
3. `dotnet build packages/shell/shell-menu-bar/BetterDesktop.Shell.MenuBar.csproj -c Debug`（0 警告 0 错误）。

## Test Strategy (§8)

- 无新增单测（键接线为 UI/配置逻辑；三板块响应机制已有各自测试）。
- 构建验证：shell-menu-bar 0 警告 0 错误。
- **真机场景走查（DoD D1）**：重启宿主 → 打开扩展中心 → 应见 6 条且全部标"已接入"（无"规划中"）→ 热键/灵动岛开关初始为开、截屏为关 → 关热键 → 侧板消失 → 开 → 恢复（Ctrl+Alt+H 仍可唤出）→ 关灵动岛 → 岛拆窗 → 开 → 恢复 → 开截屏 → 按截图热键可拉起截图 → 关 → 热键失效。

## Implementation Context (§11)

插入点：`ExtensionCatalog.cs`（record 定义 + External 数组）、`ExtensionsCenterWindow.cs`（Implemented 集合 + BuildRow 开关构造）。键字面量 `hotkeys-panel.enabled` 与 HotkeyPanelSettings / shell-desktop DesktopToggleCatalog 保持一致（既有"两处必须一致"模式新增第三处）；`island.enabled` 与 IslandOptions 一致。

## Assumptions and Open Questions (§12)

- 假设：截屏开关默认关（外部扩展默认关闭约定；Agent 当前是否默认接管热键不影响开关语义——打开后接管）。
- 孤儿设置键（extensions.weather.enabled 等）不清理（无副作用）；如未来要清理，走设置清理流程。

## Definition of Done (§13)

- D1 [场景走查] 扩展中心 6 条全部"已接入"；三开关初始态正确（热键/灵动岛/截屏=开）；关/开热键、灵动岛、截屏均实时生效（真机）。
- D2 [构建] `dotnet build shell-menu-bar` / `shell-core` / `tray` 0 警告 0 错误。
- D3 [回归] quick-note / programs-menu / clipboard-history 开关行为不变；菜单栏系统功能区（设置→菜单栏）不受影响。
- D4 [同步] 托盘「功能开关」出现"灵动岛"；`--toggle-key island` 合法；`DesktopToggleCatalog` 中热键/截屏/剪贴板/灵动岛四键与扩展中心/各板块 owner 一致（shell-core 测试 138/138 通过）。

## Handoff to 技术力应用 (§14)

| 项 | 填写 |
|---|---|
| 模式判定 | 无匹配库文档 → 工程代码权威（成熟工程增量，复用既有"写键→插件订阅"范式） |
| 注入清单 | 无（本次接线基于三板块既有机制，均不修改板块代码） |
| 适配参数 | 语言 C#/WPF；`ExtensionCatalog.cs` + `ExtensionsCenterWindow.cs`；descriptor 加 SettingsKeyOverride/DefaultEnabled；键字面量遵循既有同步模式 |
| 禁区 | 不改 Agent/CaptureHotkeyOwner、HotkeyPanelPlugin、IslandPlugin；不引入对板块包的新依赖（用字面量键） |
| DoD 核销表 | D1 真机（宿主重启后）；D2 构建命令；D3 真机回归 |
