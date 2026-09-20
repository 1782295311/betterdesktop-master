# shell-start-menu

开始菜单插件：纯自绘 WPF 开始菜单（唯一后端），支持多种布局风格（Win11 / Win10 / Win7 / 经典两栏 / 所有应用），由 Win 键钩子或 Dock 开始图标触发切换。

## 职责

- `StartMenuPlugin`：插件入口，注册 5 种布局（Win11Layout / AllAppsLayout / Win10Layout / Win7Layout / ClassicLayout）与 3 个分区（RecentSectionProvider / PlacesSectionProvider / PowerSectionProvider）。
- `StartMenuService`：数据聚合核心，整合 AppSource（应用源）、WindowTracker（窗口追踪）、Pinning（固定应用）、Search（搜索）、Recent（最近项目）等服务，构建菜单数据模型。
- `StartMenuPopup` / `StartMenuWindow`：弹出窗口管理，支持多显示器定位（`MonitorInterop`）与毛玻璃效果。
- `StartKeyHook`：Win 键全局钩子，拦截并切换开始菜单显隐。
- `StartMenuServiceBridge`：与 Dock / 菜单栏等外部触发方的桥接，订阅内核事件总线 `shell.start.toggle` 与 `shell.start.show-all-apps`。
- `PowerCommands`：电源操作（关机/重启/睡眠/注销），`AppItemActions`：开始菜单条目**右键分流到系统原生菜单**（`AttachNative` + `NativePaths`：优先 .lnk、回退目标路径；UWP 无路径项不弹菜单，2026-09-05 收口）。
- 布局系统：`IStartMenuLayoutHost` + `IStartMenuLayoutProvider` 契约，`StartMenuAppRowBuilder` 构建应用行，`PinnedFolder` 固定文件夹视图。

## 依赖

- `BetterDesktop.Kernel`
- `BetterDesktop.Shell.Core`（外观 `IAppearanceService`、毛玻璃 `IVibrancyService`）
- `BetterDesktop.Shell.Settings`（设置服务，布局风格 `startmenu.style` 等）
- `BetterDesktop.Shell.ContextMenu`（统一右键菜单）
- `BetterDesktop.Shell.AppSource`（应用源服务）
- `BetterDesktop.Shell.WindowTracker`（窗口追踪服务）
- `BetterDesktop.Shell.Pinning`（固定应用服务）
- `BetterDesktop.Shell.Search`（搜索服务，`IStartMenuSearchService`）
- `BetterDesktop.Shell.Recent`（最近项目服务）

## 扩展点

- `IStartMenuLayoutProvider`：新增布局风格（如 Windows XP 风格），实现后通过 `StartMenuService.RegisterLayout` 注册。
- `IStartMenuSectionProvider`：新增菜单分区（如「下载」「游戏」），实现后通过 `RegisterSection` 注册。
- `IStartMenuService`：菜单服务契约，可替换实现以支持完全不同的菜单数据模型。
- 布局风格通过设置键 `startmenu.style` 切换，新增布局须同步设置中心选项。

## Known Limitations

- Win 键钩子为全局低级钩子（WH_KEYBOARD_LL），与其他全局快捷键工具（如 PowerToys Keyboard Manager）可能冲突。
- 多显示器定位基于 `MonitorInterop`，当前默认在主显示器弹出，未实现「在光标所在显示器弹出」的智能定位。
- 布局切换通过设置键 `startmenu.style`，不支持运行时即时预览切换（需关闭后重新打开菜单生效）。
- 所有应用列表基于 AppSource 扫描，UWP AppX 完整枚举依赖 AppSource 实现，可能存在缺失。
- 电源操作（关机/重启等）直接调用 Windows API，无确认对话框；误触可能导致数据丢失。
- 搜索集成依赖 `IStartMenuSearchService`（由 shell-search 提供），该服务缺失时搜索框不可用但菜单其余功能正常。
