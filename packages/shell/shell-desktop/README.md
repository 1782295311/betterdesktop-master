# shell-desktop

自绘桌面插件：透明"文件显示器"窗口覆盖桌面，提供可导航桌面图标网格与文件夹浏览器；启用时隐藏 explorer 原桌面图标，退出时无条件恢复。

## 职责

- 维护 `DesktopWindow`（透明全屏窗口，壁纸归 explorer 不接管）与 `DesktopIconsControl`（自绘图标网格）。
- 提供 `IDesktopBrowser` / `DesktopBrowser`：桌面与文件夹导航、文件条目模型（`BrowserEntry`）、Shell 命名空间解析（`ShellNamespaceHelper`）。
- 桌面右键菜单：`DesktopMenuPopup` + `DesktopSystemMenuRegistrar`，集成 `shell-context-menu` 的 `IMenuService` / `IFileClassifier`，并消费 `shell-convert` 的 `IConvertMenuService`（右键「转换为 ▸」）。
- 新建文件模板（`NewFileTemplates`）与卸载程序解析（`UninstallResolver`）。
- 组件开关即时生效：订阅 `ISettingsService.Changed`，`components.desktop` 开→建窗口+隐藏原生图标，关→关窗口+恢复原生图标，无需重启。
- 退出兜底：`Application.Current.Exit` + `AppDomain.ProcessExit` 双钩子无条件恢复 explorer 图标（SW_SHOW），幂等防双触发，用户环境不可破坏。
- 双击桌面空白切换图标显隐（`desktop.iconsHidden`）：自绘模式由窗口级双击处理，原生模式由 WH_MOUSE_LL 钩子感知。

## 依赖

- `BetterDesktop.Kernel`
- `BetterDesktop.Shell.Core`（毛玻璃 `IVibrancyService`、外观 `IAppearanceService`）
- `BetterDesktop.Shell.AppSource`（应用源）
- `BetterDesktop.Shell.Settings`（设置服务，组件开关驱动）
- `BetterDesktop.Shell.ContextMenu`（统一右键菜单）
- `BetterDesktop.Shell.Convert`（转换挂点）
- NuGet：`ManagedShell`（Shell 命名空间与桌面图标互操作）

## 扩展点

- `IDesktopBrowser`：桌面/文件夹导航契约，可替换实现以支持虚拟文件夹或云存储视图。
- `DesktopSection`：桌面分区扩展，可追加自定义内容区。
- 右键菜单通过 `IMenuService` 扩展，转换挂点通过 `IConvertMenuService` 扩展。

## Known Limitations

- 壁纸归 explorer，不接管壁纸绘制；与 Wallpaper Engine 等第三方壁纸引擎共存但不控制其行为。
- 原生图标隐藏使用自实现幂等 ShowWindow（兼容 Progman 直子与 WorkerW 变体），但极端情况下（explorer 重启中）可能短暂可见。
- 进程被 kill / 崩溃时依赖 `AppDomain.ProcessExit` 兜底恢复图标，若进程被强制终止（如任务管理器「结束任务」超时），兜底可能不触发，需手动刷新桌面恢复图标。
- 文件夹浏览器当前为基础实现，尚未支持库（Libraries）、搜索结果视图与多标签页。
