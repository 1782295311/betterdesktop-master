# shell-dock

> 角色：`shell.dock` — 底部 Dock 条带（固定 + 运行中应用、AppBar 屏幕底边预留、自动隐藏）+ 应用提取器（AppGrabber，已融合原 Launchpad 能力）。深度设计见 `DESIGN.md`（v1.3 现行态）。

## 职责

- Dock 主窗口：固定应用 + 运行中应用呈现（真实图标经 `IAppIconService` 高清通道）、运行态/角标、点击启动/切换、拖拽重排（CaptureMouse 捕获式）、右键自管菜单（`DockMenuPopup`，全项目自研右键仅存 dock 与应用提取器两处）。
- AppBar 协商：**屏幕底边锚定**（`DockWindow.AppBar.cs` + `Native/DockAppBarReservation` 整屏矩形）；`components.dock` 启用时由 Bootstrap 联动隐藏原生任务栏，退出恢复。
- 自动隐藏：空闲阈值制（任何系统输入即不隐藏）；淡出完成先挂起 DWM 材质（`SetMaterialSuspended`）再隐藏，防毛玻璃残影；`DockTickPolicy` 快 60ms/慢 250ms 自适应节拍。
- 应用提取器 `AppGrabberWindow`：全应用列表/拼音搜索（`PinyinMatcher`）/分组（`AppGroupStore`）/批量排序/固定管理/「添加应用…」（手动纳入便携工具，S6 —— 便携/解压即用工具不在任何扫描范围，只能手动指认）。
- **条目右键菜单项集统一（S1–S4）**：应用提取器与菜单栏搜索共用 `packages/api/AppSource/AppEntryMenuBuilder`（构建器只判定「项集与可用性」，动作由调用方注入回调）；系统级动作共用 `shell-core/Services/AppEntryActions`。此前两处各拼一套 `if` 链、动作各写一份 —— 这是「右键功能时有时无」的另一半根因。项集：启动 → 以管理员身份运行 → 固定/移除 → 打开所在目录 → 复制路径 → 在终端中打开 →［分组］→ 属性 → 卸载。
- **固定项健康态（同删同更 · S8）**：`Pinned` 读取时按三态判定（`PinnedHealthState`：正常 / 已自愈 / 已失效）；「已自愈」用重绑后的新路径显示并**静默回写快照**（保留原主键），「已失效」渲染层让位但**保留快照**（重装同路径自动回来）。多级重绑见 `Services/PinnedRebindResolver`（Store AUMID → App Paths → 同名+同根 → 同根 → 唯一同名）。

## 依赖

- `BetterDesktop.Kernel`、`BetterDesktop.Api`（Dock/AppSource 契约）
- `BetterDesktop.Shell.Core`（ShellWindow 体系/Vibrancy/Animation/事件泵/NativeTaskbarManager 联动）
- `BetterDesktop.Shell.AppSource`、`BetterDesktop.Shell.ContextMenus`（IFileClassifier）、`BetterDesktop.Shell.Pinning`、`BetterDesktop.Shell.Settings`、`BetterDesktop.Shell.WindowTracker`
- NuGet：ManagedShell / System.Drawing.Common / TinyPinyin.Net

## 扩展点（均已实现，非占位）

- `IDockAppsService`：固定列表门面（经 `IPinningService("dock")` 持久化 `dock-pinned.json`）
- `IDockAppsService` 健康扩展（S8）：`GetPinnedHealth()`（只读三态报告）/ `RebindTo(id, path)`（用户手动指认，**保留原主键**）/ `PurgeOrphaned()`（**删除持久化数据**，与渲染层「让位」不同）
- 设置分区 `Sections/DockPinnedSection`：设置中心「Dock 固定项」——逐项健康态 + 当前路径 + 「重新绑定…」+「清理失效项」。分区签名只给 `(ISettingsService, IThemeTokens)`，故经 `DockAppsServiceBridge` 取门面（仓库既有桥接做法）。**此前固定项失效完全不可见**，用户只感知「图标没了」。
- `IDockIconService`：图标门面（包装上游 `IAppIconService`，缓存+预取）
- `IDockLayoutService`：布局度量/边缘触发/全屏隐藏判定
- Inject：`IVibrancyService` / `IAppSourceService` / `IAppIconService` / `IWindowTrackerService` / `IPinningService`
- 跨包事件：`shell.appgrabber.show`（IEventBus，菜单栏 Logo 菜单打开应用提取器）

## Known Limitations

- AppGrabber 仍在 DockPlugin 内（dock 卸载连带关闭；拆独立 shell-app-center 为开放项）。
- 运行检测来自 `IWindowTrackerService` 轮询（WinEvent 事件化为开放项）。
