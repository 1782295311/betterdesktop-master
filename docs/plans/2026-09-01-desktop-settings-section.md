# Cairo 开发计划 · 自绘桌面独立设置分区（导航栏）

> Task: 为自绘桌面在设置中心新增**独立导航栏分区**（侧栏一项"桌面"），集中控制自绘桌面的开关/图标/布局避让/交互，所有设置项**真正生效**（非仅 UI）。
> 证据基于 commit `952f117` + 前两轮未提交改动；技术力文档命中：`docs/plans/2026-09-01-shell-desktop-exit-fallback-menu-reserve.md`（桌面既有实现）。
> 证据头 schema 2。

## 1. Objective

- 在设置中心侧栏新增独立「桌面」导航项（`ISettingsSection` 分区，由 shell-desktop 插件自贡献）。
- 分区内控制项全部读写 `desktop.*` 设置键并**真实驱动桌面行为**：桌面开关、图标（.lnk 后缀/格尺寸/字号）、布局避让（Dock/任务栏/菜单栏）、交互（右键菜单/拖放）。
- 避免"只加 UI 不生效"：渲染层订阅 `desktop.*` 键变更即时刷新。

## 2. Current Behaviour

- `shell-settings` 提供 `ISettingsSectionRegistry`；`SettingsPlugin` 注册 `SystemSection`/`ThemeSection`/`LeftDockSection` 三个分区；侧栏即分区列表。
- 契约 [verified]：`ISettingsSection { string Title; string? IconKey; UIElement Build(ISettingsService, IThemeTokens); }`；各插件在 `LoadAsync` 里 `context.Get<ISettingsSectionRegistry>()?.Register(...)`。
- `LeftDockSection` [verified]：自包含分区模板（GroupCard/CardBody/TitleBlock/Labeled/SliderRow/ToggleRow/WithStyle helper），直接读写 `dock.*` 键、不依赖渲染层程序集。
- 桌面当前行为 [verified]：`DesktopPlugin` 未读任何 `components.desktop` 开关（必开）；`.lnk` 剥离硬编码（`BrowserEntry.DisplayName`）；图标格 `CellWidth=86/CellHeight=92`、字号 11 硬编码；底部避让硬编码读 `components.dock`/`components.wintaskbar`；右键菜单/拖放无条件启用。

## 3. Relevant Architecture

- 插件化分区贡献 [verified]：`shell-desktop` 已引用 `shell-settings`（上一轮为底部避让加），可自贡献分区，无需改 `SettingsPlugin`。
- 依赖方向 [verified]：`shell-menu-bar → shell-desktop`，桌面不得反向引用菜单栏（保持无环）。
- 设置键约定：分区键与渲染层**同键同默认**（对齐 `DockVisualSettings` 与 `LeftDockSection` 的分层约定）。

## 4. Technical-Knowledge Findings

- `ISettingsSection` 契约 + `LeftDockSection` 自包含 helper 模式 [verified]——新分区直接复刻该风格，保持设置界面视觉/交互统一（MacToggle/MacSlider/MacCombo/MacButton 样式键 + 主题令牌取色）。
- `ISettingsService.Changed` 事件 [verified]：`Set` 时触发，渲染层订阅后可即时应变（无需重启）。

## 5. Constraint Findings（验收标准）

- 分区必须经 `ISettingsSectionRegistry.Register` 注册，不修改 `SettingsPlugin`（插件自贡献，避免耦合）。
- 每个设置项必须落到渲染层实际读取的键；键变更后桌面**即时刷新**（图标类改 Rebuild，避让类改 Margin）。
- `TreatWarningsAsErrors=true`：新增文件不得有未使用 using/变量。
- 主题取色一律经 `IThemeTokens`（Foreground/MutedForeground），禁止硬编码颜色。

## 6. Proposed Changes

| # | 文件 | 符号 | 职责 |
|---|------|------|------|
| 1 | `Sections/DesktopSection.cs`（新） | `DesktopSection : ISettingsSection` | 独立分区 UI：五张卡片（桌面/图标/布局避让/交互/系统入口），复刻 LeftDockSection helper 风格 |
| 2 | `DesktopPlugin.cs` | `DesktopPlugin` | 读 `components.desktop` 开关（关闭则不建窗口）；`LoadAsync` 注册 `DesktopSection` |
| 3 | `Windows/DesktopWindow.cs` | `DesktopWindow` | 底部/顶部避让改读 `desktop.reserveDock/reserveTaskbar/reserveMenuBar`；订阅 `desktop.*` 变更 → `UpdateIconsReserve` + `Rebuild` |
| 4 | `Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | 接收 `ISettingsService`；读 `desktop.iconCellWidth/iconCellHeight/labelFontSize/hideLnkExtension/contextMenuEnabled/dragDropEnabled` |
| 5 | `Windows/FolderBrowserWindow.cs` | `FolderBrowserWindow` | `.lnk` 剥离改读 `desktop.hideLnkExtension`（与桌面一致） |

## 7. Implementation Sequence

1. `Sections/DesktopSection.cs`：分区 UI（自包含 helper）。
2. `DesktopPlugin.cs`：`components.desktop` 开关 + 注册分区。
3. `DesktopWindow.cs`：避让读独立键 + 订阅 `desktop.*`。
4. `DesktopIconsControl.cs`：注入 settings，读全部桌面键（含开关与尺寸）。
5. `FolderBrowserWindow.cs`：.lnk 开关接线。
6. `dotnet build BetterDesktop.slnx`（0 警告 0 错误）+ 实机验证。

## 8. Test Strategy

- 无 shell-desktop 测试工程；编译 + 实机验收。
- 真机验收：
  - [ ] 设置中心侧栏出现「桌面」导航项，点击进入分区。
  - [ ] 关闭「启用自绘桌面」→ 重启后不再创建桌面窗口，explorer 图标恢复。
  - [ ] 切换「隐藏 .lnk 后缀」→ 桌面/文件夹窗口图标名即时（或刷新后）变化。
  - [ ] 调整图标格宽/高、字号滑块 → 桌面图标网格即时重排。
  - [ ] 关闭「为 Dock/任务栏/菜单栏让出空间」→ 底部/顶部避让即时变化。
  - [ ] 关闭右键菜单/拖放 → 对应交互停用。
- 验证命令：`dotnet build BetterDesktop.slnx -c Debug`（0 警告 0 错误）。

## 9. Risk and Impact Analysis

- **d=1 下游**：仅 shell-desktop 内部 + 设置中心侧栏多一项；`shell-menu-bar` 不受影响（无环）。
- **开关语义**：`components.desktop` 关闭时直接 return，与 `DockPlugin` 的 `components.dock` 同机制（下次启动生效），需在分区内注明。
- **即时刷新**：`desktop.*` 变更触发 Rebuild 会重建整个图标网格（含右键菜单），与内联重命名竞态按既有 M10 处理（丢弃编辑态）。
- **默认值一致性**：分区默认与渲染层默认必须一致（86/92/11/true…），避免"设置显示 A、实际 B"。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-desktop/Sections/DesktopSection.cs` | `DesktopSection` | 新分区 UI |
| `packages/shell/shell-desktop/DesktopPlugin.cs` | `DesktopPlugin` | 开关 + 注册分区 |
| `packages/shell/shell-desktop/Windows/DesktopWindow.cs` | `DesktopWindow` | 避让键 + 变更订阅 |
| `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | 注入 settings + 读桌面键 |
| `packages/shell/shell-desktop/Windows/FolderBrowserWindow.cs` | `FolderBrowserWindow` | .lnk 开关 |
| `docs/plans/2026-09-01-desktop-settings-section.md` | — | 本计划 |

## 11. Reusable Implementation Context

- 已读：`ISettingsSection.cs`、`ISettingsSectionRegistry.cs`、`LeftDockSection.cs`（helper 全套）、`SettingsPlugin.cs`、`ISettingsService.cs`（Get/Set/Changed）。
- 样式键（Application.Resources）：`MacToggle`/`MacSlider`/`MacCombo`/`MacButton`。
- 主题取色：`tokens.Foreground` / `tokens.MutedForeground`。
- 设置键清单（新增）：`components.desktop`、`desktop.hideLnkExtension`、`desktop.iconCellWidth`、`desktop.iconCellHeight`、`desktop.labelFontSize`、`desktop.reserveDock`、`desktop.reserveTaskbar`、`desktop.reserveMenuBar`、`desktop.contextMenuEnabled`、`desktop.dragDropEnabled`。

## 12. Assumptions and Open Questions

- [assumed] 侧栏图标（`IconKey`）暂为 null，待主题图标令牌齐备后再补。
- [deferred] 图标排序方式/自动排列/对齐网格 → P2（当前瀑布列固定）。
- [deferred] 桌面壁纸相关设置 → 壁纸归 explorer，本环境不提供自绘壁纸（生死线 #1）。

## 14. 用户反馈修复（第二轮）

| 反馈 | 根因 | 修复 |
|---|---|---|
| 自绘桌面开关没反应 | 开关设计为"下次启动生效"，用户点击无即时反馈 | `DesktopPlugin` 订阅 `ISettingsService.Changed`，`components.desktop` 变更即时 `StartDesktop()`/`StopDesktop()`（建/关窗口 + 隐藏/恢复原生图标），抽成 Start/Stop 方法，无需重启 |
| 调行间距后桌面下方空一大块 | 底部避让只读 `desktop.reserveDock` 默认 true，**未检查 `components.dock` 组件是否真的启用** → Dock 关闭时仍预留约 98 DIP 空白 | 避让改为双重条件：`desktop.reserveDock && components.dock`、`desktop.reserveTaskbar && components.wintaskbar` |
| 需要图标大小控制 | 图标尺寸硬编码 44×40，无控制入口 | 新增 `desktop.iconSize`（24-96，默认 44）；格尺寸改为由「图标大小 + 行/列间距」推导：`格宽=iconSize+spacingX+30`、`格高=iconSize+spacingY+36`（默认 86/92，与历史一致）；新增 `desktop.itemSpacingX`/`desktop.itemSpacingY` 行/列间距滑块，移除固定的 `iconCellWidth/iconCellHeight` |

### 关键教训：构建输出目录陷阱
`host` 存在两个输出目录且**不互相同步**：`bin/Debug/...`（单独构建 host 工程产出）与 `bin/x64/Debug/...`（全仓 slnx 构建产出，因 `<Platforms>x64</Platforms>`）。
曾因从 `bin/Debug` 旧副本启动，运行的是写 DesktopSection 之前的代码，表现为"桌面分区在设置中心看不到"。
- 排查：对比两目录 DLL 的 `LastWriteTime`/大小
- 确证：桌面 `BetterDesktop_debug.log` 中 `Settings: BuildAllSections 分区数=N：标题列表`
- 构建前必须 `Stop-Process` 停掉 `BetterDesktop.Host`，否则 DLL 被锁触发 MSB3027

## 13. Definition of Done

- 设置中心侧栏出现「桌面」独立导航项，分区含五组控制。
- 所有 `desktop.*` 键被渲染层真实读取；键变更即时刷新。
- `components.desktop` 开关生效（关闭→重启后无桌面窗口）。
- 全仓 `dotnet build` 0 警告 0 错误；实机验收项全通过。
