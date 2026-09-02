# Cairo 开发计划 · 桌面四改进：退出图标兜底 + .lnk 显示 + 自绘右键菜单 + dock/任务栏避让

> Task: 基于上一轮"透明文件显示器"回归，补齐 4 个体验缺口：(1) 退出程序自动恢复 explorer 桌面图标（兜底）；(2) 图标不显示 `.lnk` 后缀；(3) 桌面右键菜单改自绘主题风格；(4) 图标网格为 dock/原生任务栏让出底部空间。
> 证据基于 commit `952f117` + 上一轮未提交改动；技术力文档命中：`desktop-progman-embed.md`（红线 #4 退出恢复）+ `MENU-SPECS.md`（§1/§2 菜单规格）。
> 证据头 schema 2。

## 1. Objective

- 退出（含崩溃残留）后 explorer 桌面图标自动恢复——当前仅 `UnloadAsync` 恢复，进程被杀/崩溃/强制退出不触发。
- 桌面/文件夹浏览器图标显示名剥 `.lnk` 后缀（explorer 桌面惯例）。
- 桌面图标/空白右键菜单从 WPF 原生样式改为**自绘主题风格**（主题令牌 + 悬停高亮 + 分组），实现"自绘桌面用自绘菜单控制"。
- 桌面图标网格底部为 **dock（默认 iconSize 44+label 24+bottomMargin 10）+ 原生任务栏（components.wintaskbar=true 时）** 让出空间，图标不再被底栏遮挡。

## 2. Current Behaviour

- `DesktopPlugin.cs`：`LoadAsync` 隐藏图标、`UnloadAsync` 恢复；**无 Application.Exit / ProcessExit 兜底**——进程被 kill/崩溃时 explorer 图标残留隐藏。
- `BrowserEntry.cs`：`record BrowserEntry(string Name, string Path, bool IsDirectory)`——`Name` 含 `.lnk`；`DesktopIconsControl`/`FolderBrowserWindow` 直接显示 `entry.Name`。
- `DesktopIconsControl.cs`：`BuildIconMenu`/`BuildBlankMenu` 用 WPF 原生 `ContextMenu`（系统样式，非自绘）。
- `DesktopWindow.cs`：`_icons.Margin = (7, MenuBarSafeTop+13, 0, 0)`——仅顶部避让菜单栏，**无底部避让**（dock/任务栏会遮住底部图标）。

## 3. Relevant Architecture

- 依赖方向 [verified]：`shell-menu-bar → shell-desktop`（MenuBar csproj 引 shell-desktop），故**桌面不得反向引用 shell-menu-bar**（循环）。自绘菜单必须自包含于 shell-desktop。
- 主题令牌 [verified]：`AppearanceService.SyncAppResources` 推入 `PopupBackground/PopupBorder/PopupItemHover/ThemeSeparator/ThemeForeground` 等 App 级资源——自绘菜单可直接 `DynamicResource` 引用。
- `ToggleDesktopIcons(bool)` [verified]：ManagedShell `ShellHelper` 内部用 `IsDesktopVisible` 判断当前状态后**幂等设置**（非盲 toggle）——可安全"先恢复再隐藏"自愈崩溃残留。
- Dock 几何 [verified]：`DockLayoutService.Measure` → `contentHeight = iconSize + labelHeight + bottomMargin*2`，`y = screenHeight - contentHeight - bottomMargin`，占用高度 ≈ `iconSize + labelHeight + 3*bottomMargin`。设置键：`dock.iconSize`(44)/`dock.showLabel`(true)/`dock.bottomMargin`(10)/`components.dock`(true)/`components.wintaskbar`(true)。
- `ISettingsService` [verified]：`Get<T>(key, default)` + `Changed` 事件；`shell-settings` 仅依赖 kernel/kernel-hmr/shell-core，shell-desktop 引用无环。

## 4. Technical-Knowledge Findings

- `desktop-progman-embed.md` 红线 #4 [verified]："隐藏 explorer 原生图标……退出时必须恢复（再发一次 toggle）。程序崩溃残留隐藏状态是已知可接受风险（cairoshell 同款）"——本次把"可接受风险"升级为**可自愈兜底**（启动先恢复 + 退出钩子）。
- `MENU-SPECS.md` §1/§2 [verified]：桌面空白/图标右键菜单项规格（打开/剪切/复制/重命名/删除/属性 + 新建文件夹/粘贴/刷新/显示设置/个性化）——上轮已实现为原生 ContextMenu，本次只换**呈现**为自绘。
- `LogoMenuWindow`（shell-menu-bar 内）[verified]：自绘菜单范式 = Border 行 + MouseEnter/MouseLeave 高亮 + 分组分隔线 + 主题令牌。桌面侧自包含复刻同风格（不引用 shell-menu-bar）。

## 5. Constraint Findings（验收标准 + 生死线）

- 退出恢复：正常退出（Application.Exit）与进程退出（ProcessExit）都必须恢复；崩溃残留由"启动先 ToggleDesktopIcons(true) 恢复再隐藏"自愈。**绝不双 toggle 反转**（依赖幂等语义）。
- `.lnk`：仅剥**显示名**（`.lnk` 大小写不敏感），`Path` 保持完整供文件操作；重命名初始值对齐 explorer（去扩展名）。
- 自绘菜单：用主题令牌（`PopupBackground/PopupBorder/PopupItemHover/ThemeForeground/ThemeSeparator`），悬停高亮、分组分隔、点击执行后关闭；菜单视觉随主题模式即时切换。
- 底部避让：`bottomReserve = (components.dock ? dockHeight : 0) + (components.wintaskbar ? taskbarHeight : 0)`；dockHeight 从 `dock.*` 设置实时计算；任务栏高度 = `PrimaryScreenHeight - WorkArea.Height`。设置变更（`dock.*`/`components.*`）时刷新避让。

## 6. Proposed Changes

| # | 文件 | 符号 | 职责 |
|---|------|------|------|
| 1 | `DesktopPlugin.cs` | `DesktopPlugin` | 兜底恢复：LoadAsync 注册 `Application.Current.Exit`/`AppDomain.ProcessExit` → 恢复图标；启动先 `ToggleDesktopIcons(true)` 自愈再隐藏；UnloadAsync 移除钩子 |
| 2 | `Contracts/BrowserEntry.cs` | `BrowserEntry` | 新增 `string DisplayName` 计算属性（剥 `.lnk`，大小写不敏感；非 .lnk/目录返回 Name） |
| 3 | `Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | label 用 `entry.DisplayName`；右键菜单改自绘（`DesktopMenuStyling`） |
| 4 | `Windows/FolderBrowserWindow.cs` | `FolderBrowserWindow` | 标签用 `entry.DisplayName`（与桌面一致） |
| 5 | `Controls/DesktopMenuStyling.cs`（新） | 静态类 | 自绘 ContextMenu 模板 + MenuItem/Separator 样式（主题令牌） |
| 6 | `Windows/DesktopWindow.cs` | `DesktopWindow` | 增加 `bottomReserve` 计算（读设置）+ `_icons.Margin` 底部避让 + 订阅 `settings.Changed` 刷新 |
| 7 | `DesktopPlugin.cs` / `DesktopWindow` | 构造 | 传入 `ISettingsService`（DesktopPlugin 从 context 取）；csproj 加 shell-settings 引用 |

## 7. Implementation Sequence

1. `DesktopPlugin.cs`：兜底恢复钩子 + 启动自愈（需求 1）。
2. `BrowserEntry.cs`：`DisplayName` 属性（需求 2）。
3. `DesktopIconsControl` / `FolderBrowserWindow`：label 用 `DisplayName`。
4. `Controls/DesktopMenuStyling.cs`：自绘菜单样式（需求 3），`DesktopIconsControl` 接入。
5. `DesktopWindow`：底部避让计算 + 设置订阅（需求 4）。
6. csproj 加 `shell-settings` 引用。
7. 全仓 `dotnet build` 0 警告 0 错误。

## 8. Test Strategy

- 无 shell-desktop 测试工程；以编译 + 真机验收为准。
- 真机验收：
  - [ ] 正常退出（`Application.Current.Shutdown`）→ explorer 桌面图标恢复。
  - [ ] 任务管理器强制结束进程 → 重启程序 → 图标先恢复再隐藏（自愈，不残留）。
  - [ ] 桌面 `.lnk` 图标只显示名不显示后缀；双击仍正常打开。
  - [ ] 桌面右键 = 自绘主题菜单（悬停高亮、分组分隔、随主题模式变色）；图标右键含 打开/剪切/复制/重命名/删除/属性；空白右键含 新建文件夹/粘贴/刷新/显示设置/个性化。
  - [ ] 桌面图标网格底部不被 dock 和原生任务栏遮挡（`CopyFromScreen` 验证）。
- 验证命令：`dotnet build BetterDesktop.slnx -c Debug`（0 警告 0 错误）。

## 9. Risk and Impact Analysis

- **d=1 下游**：`shell-menu-bar`（MenuBarLeftZone/FolderToolbar 消费 `IDesktopBrowser`）——BrowserEntry 只加属性不删改，编译零影响。
- **循环依赖**：桌面引用 shell-menu-bar 会成环（menu-bar→desktop）——自绘菜单自包含，**不**新增 shell-menu-bar 引用。
- **幂等语义依赖**：`ToggleDesktopIcons(true)` 必须先于隐藏调用（启动自愈）；`Application.Exit` 与 `ProcessExit` 都可能触发 → 恢复函数需幂等（内部标记 `_iconsHidden` 防重复）。
- **设置变化**：dock 尺寸滑块/组件开关变化 → 订阅 `settings.Changed` 重算底部避让，避免图标瞬时被遮。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-desktop/DesktopPlugin.cs` | `DesktopPlugin` | 退出兜底 + 启动自愈 + 传 settings |
| `packages/shell/shell-desktop/Contracts/BrowserEntry.cs` | `BrowserEntry` | DisplayName 属性 |
| `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | DisplayName + 自绘菜单接入 |
| `packages/shell/shell-desktop/Controls/DesktopMenuStyling.cs` | 新 | 自绘菜单样式工厂 |
| `packages/shell/shell-desktop/Windows/DesktopWindow.cs` | `DesktopWindow` | 底部避让 + 设置订阅 |
| `packages/shell/shell-desktop/Windows/FolderBrowserWindow.cs` | `FolderBrowserWindow` | DisplayName |
| `packages/shell/shell-desktop/BetterDesktop.Shell.Desktop.csproj` | — | 加 shell-settings 引用 |
| `docs/plans/2026-09-01-shell-desktop-exit-fallback-menu-reserve.md` | — | 本计划 |

## 11. Reusable Implementation Context

- 已读上下文：`DesktopPlugin.cs` / `DesktopIconsControl.cs` / `DesktopWindow.cs` / `BrowserEntry.cs` / `FolderBrowserWindow.cs` / `MenuBarPopupWindow.cs` / `LogoMenuWindow.cs` / `AppearanceService.SyncAppResources`（令牌键）/ `DockLayoutService.cs`（几何）/ `DockVisualSettings.cs`（键）/ `ISettingsService.cs` / `NativeTaskbarManager.cs`。
- 主题令牌键（SyncAppResources）：`PopupBackground`/`PopupBorder`/`PopupItemHover`/`ThemeForeground`/`ThemeSeparator`/`ThemePanelBackground`。
- Dock 几何：`dockHeight = iconSize + labelHeight(24 if showLabel) + 3*bottomMargin`；任务栏高 = `SystemParameters.PrimaryScreenHeight - SystemParameters.WorkArea.Height`。
- 设置键：`components.dock`(bool,true) / `components.wintaskbar`(bool,true) / `dock.iconSize`(44) / `dock.showLabel`(true) / `dock.bottomMargin`(10)。

## 12. Assumptions and Open Questions

- [assumed] `ToggleDesktopIcons(bool)` 幂等（ManagedShell IL 实证 `IsDesktopVisible` 判断）——恢复/隐藏可安全重复调用。
- [assumed] 自绘菜单 = WPF `ContextMenu` + 自定义 ControlTemplate/ItemContainerStyle（保留定位/失焦/键盘行为，视觉全主题化）——不新建独立窗口（避免焦点/钩子复杂度与 shell-menu-bar 内 `MenuBarPopupWindow` 的复制成本）。
- [deferred] 菜单键盘导航/子菜单 → P2（shell-context-menu 全量实现时）。
- [deferred] 多显示器 dock/任务栏避让差异化 → P2（当前按主屏计算）。

## 13. Definition of Done

- 退出恢复兜底：`Application.Exit`/`ProcessExit` 恢复 + 启动自愈；崩溃残留不出现。
- `.lnk` 显示名剥离（桌面 + 文件夹浏览窗口一致）。
- 桌面右键菜单 = 自绘主题风格（悬停/分组/主题变色），功能项与 MENU-SPECS §1/§2 对齐。
- 图标网格底部避让 dock + 原生任务栏；设置变化实时刷新。
- 全仓 `dotnet build` 0 警告 0 错误。

## 14. Post-Mortem（回归修复记录）

- **回归**：首版自绘菜单用 `FrameworkElementFactory.SetValue(dp, DynamicResourceExtension.ProvideValue(null))` 构建模板 → 运行抛 `ArgumentException: ResourceReferenceExpression 不能作为 Background 的有效值` → `DesktopIconsControl` 构造失败 → **`DesktopWindow` 构造失败 → 自绘桌面整体未创建**，而 explorer 图标已被 `ToggleDesktopIcons(false)` 隐藏 → 桌面空白无图标。
- **根因**：`FrameworkElementFactory.SetValue` 不做延迟求值（`ShellWindow.ApplyFontScale` 的同类写法能用是因实例 `SetValue` 延迟求值）；`ProvideValue(null)` 返回的是 `ResourceReferenceExpression`，被模板工厂拒绝。
- **修复**：`DesktopMenuStyling` 改用 `XamlReader.Parse` 解析 XAML 字符串模板/样式，`{DynamicResource}` 由 XAML 解析器原生处理；已验证 `shell.desktop 已加载` 无 Error + CopyFromScreen 截图图标恢复。
- **教训**：模板工厂/样式里绑定主题令牌一律用 `XamlReader.Parse` 或 `SetResourceReference`，**禁止** `ProvideValue(null)` 直传。
