# shell-desktop

自绘桌面插件：透明"文件显示器"窗口覆盖桌面，提供桌面图标网格（文件夹双击交给 explorer 打开）与独立文件夹浏览窗口；启用时隐藏 explorer 原桌面图标，退出时无条件恢复。

## 职责

- 维护 `DesktopWindow`（透明全屏窗口，壁纸归 explorer 不接管）与 `DesktopIconsControl`（自绘图标网格）。
- **图标手势（2026-09-17 改版 + 四轮补齐）**：① **左键拖动 = 原生桌面式**——拖到文件夹图标=移入（Ctrl=复制，`FileClipboard`→`SHFileOperation`）、拖到 exe/快捷方式=用该程序打开（`.lnk` 经 `ShellLinkResolver` 解析；目标是文件夹快捷方式则按移入处理）、**拖到空白=整组就落在那里**（`MoveDragSelectionToPlace` → `FinishFreeDrag`：拖动期间被拖整组**实时跟随鼠标**（与右键同一套群体动画：跟随 + 占用者让位 + 落点指示器），松手吸附落格 + 让位互斥 + `CommitLayout` 落盘）、拖到自身/回收站等 shell 虚拟项=无操作、拖到外部程序=交给系统（OLE `DoDragDrop`，原"拖出"能力保留）；拖动中落点是文件夹/程序时显示原生风格提示浮层（`→ 移动到 X` / `→ 复制到 X` / `+ 用 X 打开`），本窗口内一律给 `Move`/`Copy` 光标；② **右键长按（≥350ms）后拖动 = 自由摆放**——拖动中图标**实时跟随**鼠标、占用者让位、松手吸附对齐并落盘 `desktop.iconPositions`（自动排列下先固化瀑布布局退出自动排列再续拖，`desktop.autoExitArrangeOnDrag`），松手后仍弹右键菜单；③ 右键短按=自绘菜单，左键空白拖动=框选。
- **回收站无特殊处理（2026-09-17 用户拍板）**：拖上去既不删除也不高亮，它就是普通图标（与"此电脑/控制面板"同级）；删除走右键菜单「删除」/ Del 键。原 2026-09-07 的"拖到桌面回收站 / dock 栏回收站 = 移入回收站"链路（含 kernel 共享通道 `DockDropTargets`、dock 侧 `UpdateRecycleDropRect`/`UpdateRecycleHoverHighlight`、桌面侧 `UpdateRecycleDropState`）已整体拆除。
- 提供 `IDesktopBrowser` / `DesktopBrowser`：桌面目录枚举、文件条目模型（`BrowserEntry`）、Shell 命名空间解析（`ShellNamespaceHelper`）、选中集与文件操作（剪/复/贴/改名/删除/新建/导入）。**历史导航已退役、菜单栏文件夹工具条整条移除**（2026-09-17 用户拍板）：桌面文件夹交给 explorer 打开，自绘桌面不再站内导航，`Navigate/Back/Forward/Up/CanGoBack/CanGoForward/LocationChanged` 已从契约与实现中移除；桌面空白菜单的「后退」、菜单栏左区的整条文件夹工具条（折叠钮 + 路径显示 + ← → ↑ + 刷新 + 剪切/复制/粘贴/重命名/删除）一并删除——这些操作**全部已在自绘右键菜单内**（图标菜单 = 剪切/复制/粘贴/重命名/删除/属性；空白菜单 = 刷新/粘贴/新建/排序），工具条属重复入口。`shell-menu-bar` 因此不再消费 `IDesktopBrowser`。
- 桌面右键菜单（2026-09-07 拍板、2026-09-10 清理收口）：**图标与空白右键一律本进程自绘**（`DesktopIconsControl.ShowMenu` → `DesktopMenuPopup`，零 IContextMenu/跨进程依赖——原"跨进程委托 explorer DefView"设计因稳定性不达标已整体移除）；`DesktopSystemMenuRegistrar` 向 explorer 桌面右键注册「切换自绘桌面 / 自绘桌面 ▸」系统入口；消费 `shell-convert` 的 `IConvertMenuService`（右键「转换为 ▸」）。
- 图标网格底部避让以 **Shell_TrayWnd 实际可见高度**为基准（`desktop.reserveTaskbar`），任务栏隐藏时基准归 0。
- 新建文件模板（`NewFileTemplates`）与卸载程序解析（`UninstallResolver`）。
- 组件开关即时生效：订阅 `ISettingsService.Changed`，`components.desktop` 开→建窗口+隐藏原生图标，关→关窗口+恢复原生图标，无需重启。
- 退出兜底**三重保障**：`Application.Current.Exit` + `AppDomain.ProcessExit` 双钩子 + **`IconRestoreSentinel` 哨兵进程**（宿主以 `--icon-restore-sentinel <pid>` 拉起，覆盖 TerminateProcess/强杀/结束任务路径），均无条件恢复 explorer 图标（SW_SHOW），幂等防双触发。
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

- `IDesktopBrowser`：桌面枚举 + 选中集 + 文件操作契约（无导航），可替换实现以支持虚拟文件夹或云存储视图。
- `DesktopSection`：桌面分区扩展，可追加自定义内容区。
- 自绘菜单项通过 `MenuItemDef`（shell-context-menu 契约）声明扩展，转换挂点通过 `IConvertMenuService` 扩展。

## Known Limitations

- 壁纸归 explorer，不接管壁纸绘制；与 Wallpaper Engine 等第三方壁纸引擎共存但不控制其行为。
- 原生图标隐藏使用自实现幂等 ShowWindow（兼容 Progman 直子与 WorkerW 变体），但极端情况下（explorer 重启中）可能短暂可见。
- 原生图标隐藏依赖本进程窗口与 explorer DefView 可见性联动（IsWindowVisible 按祖先链与运算）；看门狗三分支（真失联重挂/宿主隐藏补宿主/窗口不可见补窗口）防振荡。
- 文件夹浏览器当前为基础实现，尚未支持库（Libraries）、搜索结果视图与多标签页。
