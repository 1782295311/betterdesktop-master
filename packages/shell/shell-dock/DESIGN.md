# shell-dock（Dock 插件 · 深度设计稿）

> 状态：**现行实现态（v1.3，2026-09-10 重写同步）**。本文与代码逐一核对过：契约/文件/行为均为当前真实状态。
> 文档时间序（早的在前）：第 1 节「版本沿革」按升序记录演进；与 2026-08-22 旧稿的差异已全部吸收进正文，旧稿可弃。
> 交叉引用：应用来源/图标上游见 `shell-app-source/DESIGN.md`；契约见 `packages/api/Dock/`。

## 1. 版本沿革（时间升序，早 → 新）

| 时间 | 事件 | 要点 |
|---|---|---|
| 2026-08-22 | 初版设计稿 + 复核收口 | 独立 `IPlugin`（`shell.dock`） Inject 消费 app-source（不再手动 new）；`DockService` 主键统一 `DockItemId`；图标栈去重（删 `IIconProvider`/`Win32IconProvider`/分叉 ShellLinkResolver，`DockIconService` 改包装 `IAppIconService`）；测试改 `FakeAppSource` mock |
| 2026-09-02 | AppBar 锚定重构 + 任务栏接管 | dock 底边锚定**屏幕底边**（非工作区底）：`DockAppBarReservation.GetMonitorBounds` 整屏矩形，AppBar 协商"抬升→再定位"免疫；Bootstrap 落定 `components.dock` 启用即隐藏原生任务栏（`NativeTaskbarManager`，退出恢复） |
| 2026-09-05 | 菜单自管化（中央管线退役） | dock 菜单收归自管：`DockMenuPopup`（WPF ContextMenu）+ `DockItemTemplate`（DockWindow ctor 无条件创建，规避插件装配顺序取 null）；全项目自研右键仅存 dock 图标 + 应用提取器两处 |
| 2026-09-06 | 交互修复 + 批量排序 + 跨包事件 | 拖拽重排"失灵"根因修复（容器 `CaptureMouse` + `FinishReorderDrag` 幂等收尾 + `LostMouseCapture` 兜底提交）；AppGrabber 恢复"批量排序"模式 + 分段控件重构；新增 IEventBus 事件 `shell.appgrabber.show`（菜单栏 Logo 菜单跨包打开应用提取器） |
| 2026-09-07 | 组件开关热建 | `components.dock=false` 启动也完成依赖前置与事件订阅，运行中开→关热建窗口（BuildDockWindow 复用），无需重启宿主 |
| 2026-09-10 | 文档重写 | 本 DESIGN.md 按当前代码全量重写（旧稿中 Launchpad/FolderInput/MinimalDock/通知/缩略图等已被拆分或合并，见 §3） |

## 2. 目标与边界

**做什么**：Dock 本体——底部条带（固定应用 + 运行中应用）、AppBar 屏幕底边预留、自动隐藏/贴边唤出、运行态/角标、缩略图交互、拖拽重排、右键菜单（自管）、点击启动/切换；附带**应用提取器**（AppGrabberWindow，已融合原 Launchpad 能力：全应用列表/搜索/分组/批量排序/固定管理）。

**不做什么**：不直接扫描应用（走 `IAppSourceService`）；不做通用右键菜单服务（中央管线已退役，右键全项目收口为「桌面/文件系统→系统原生，dock+应用提取器→自研」）；不替换系统 Shell。

## 3. 架构（当前真实文件清单）

```
packages/api/Dock/                        # 契约 + 模型（公共 API 包）
├── IDockAppsService / IDockIconService / IDockService / IDockLayoutService
└── DockItemData / DockItemId / DockAppType / DockLayoutMetrics

packages/shell/shell-dock/                # 实现（引用：Kernel + Api + shell-core + shell-app-source）
├── DockPlugin.cs                         # 入口：Inject 5 服务；Provide 3 门面；components.dock 热建
├── DockTickPolicy.cs                     # 自适应节拍（快 60ms / 慢 250ms，纯函数裁决）
├── DockWindow.xaml(.cs) + DockWindow.AppBar.cs   # 主窗口（124KB）+ AppBar 协商 partial
├── Controls/DockItem.xaml(.cs)           # 单项控件（悬停放大/标签/角标/运行指示）
├── Native/
│   ├── DockAppBarReservation.cs          # AppBar 登记 + GetMonitorBounds（整屏，底边锚定基准）
│   ├── MultitaskingViewVisibilityService.cs   # Win+Tab 任务视图可见性检测（隐藏配合）
│   └── KeyboardInterop.cs
├── Services/
│   ├── DockAppsService.cs                # 固定列表门面（IPinningService("dock") + dock-pinned.json）
│   ├── DockIconService.cs                # 图标门面（包装 IAppIconService + 视觉设置）
│   ├── DockService.cs                    # 运行项单一事实来源（主键 DockItemId）
│   ├── DockLayoutService.cs              # 布局度量（bottomMargin 随屏底边）
│   ├── DockMenuPopup.cs                  # 自管右键弹层（WPF ContextMenu，光标/指定坐标）
│   ├── DockItemTemplate.cs               # 条目模板（DockWindow ctor 无条件创建）
│   ├── DockVisualSettings.cs             # 视觉设置（设置键订阅 + 事件广播）
│   ├── AppSourceConverter.cs             # AppItem→DockItemData 映射
│   ├── AppGroupStore.cs                  # 应用分组持久化（Groups{Name,Members}）
│   ├── PinyinMatcher.cs                  # 搜索拼音匹配（AppGrabber）
│   └── NullIconService.cs                # 未注入时的无操作实现
└── Windows/AppGrabberWindow.cs           # 应用提取器（57KB；已融合 Launchpad；批量排序/分段控件/搜索）
```

**已不在本包**（旧稿读者注意）：`NewAppsNotificationWindow` → `shell-notification`；`DwmThumbnail` → `shell-window-tracker`；`RunningAppDetector` → `IWindowTrackerService`（window-tracker）；`LaunchpadWindow`/`FolderInputWindow`/`MinimalDockWindow` → 已删/被 AppGrabber 融合；`IDockPinnedService` → 由通用 `IPinningService`（shell.pinning）承载。

## 4. 内核集成（当前真实接线）

```csharp
public IReadOnlyList<Type> Inject => new[]
{
    typeof(IVibrancyService),      // 毛玻璃
    typeof(IAppSourceService),     // 应用扫描
    typeof(IAppIconService),       // 图标
    typeof(IWindowTrackerService), // 运行窗口检测
    typeof(IPinningService),       // 固定列表（"dock" 作用域）
};
// 另经 context.Get 取用：ISettingsService / IEventBus / IAppearanceService / IFileClassifier（可空降级）
// Provide：IDockAppsService / IDockIconService / DockVisualSettings
```

- `components.dock`（默认 true）关闭：LoadAsync 只做依赖前置与事件订阅，不建窗口；运行中切回开启经 `BuildDockWindow` 热建（服务/窗口全部重建，事件先退订再订阅防重）。
- `components.dock` 启用 → Bootstrap 联动隐藏原生任务栏（`NativeTaskbarManager.SetTaskbarVisible`），Exit 无条件恢复；桌面图标避让基准 = Shell_TrayWnd 实际可见高度（desktop 侧联动，dock 不参与估算）。

## 5. 公共契约（语义，与 api/Dock 逐字对齐）

```csharp
interface IDockAppsService     // 固定列表门面（经 IPinningService("dock") 持久化 dock-pinned.json）
interface IDockIconService     // 图标门面（GetIconAsync / 缓存 / 预取，IAppIconService 包装）
interface IDockService         // 运行项单一事实来源（SetRunning 整体替换语义，防竞态）
interface IDockLayoutService   // 布局度量 / 边缘悬停 / 全屏隐藏判定
```

- 固定变更链：`PinnedChanged` → DockWindow 重绘 + AppGrabber 同步；运行合并（固定项显示运行态、未固定运行项追加）在 DockWindow 内完成。
- 跨包 UI 事件：`shell.appgrabber.show`（IEventBus）——菜单栏 Logo 菜单 → DockPlugin 订阅 → 切 Dispatcher 打开 AppGrabberWindow；`components.dock` 关闭时订阅不挂，emit 无害 no-op。

## 6. 菜单体系（自管，中央管线已退役）

- 09-05 拍板：全项目自研右键菜单**仅存两处**——dock 图标、应用提取器；其余表面一律系统原生（桌面/文件系统右键转系统）。
- 实现：`DockItemTemplate` 在 `DockWindow` ctor **无条件创建**（不依赖插件装配顺序，`IFileClassifier` 可空降级——"dock 菜单全失"根因即装配顺序，已根治）；弹层 = `DockMenuPopup`（WPF ContextMenu，光标/指定坐标两入口）；「固定到 Dock」中央贡献者随管线退役。

## 7. AppBar / 隐藏 / 唤出

- **底边锚定屏幕底边**：AppBar 缓存 `_appBarScreen`（`DockAppBarReservation.GetMonitorBounds` 整屏矩形，不受工作区抬升影响），纵向基准 = screen.Bottom − `dock.bottomMargin`；"协商→抬升→再定位"循环免疫。
- **原生任务栏**：dock 启用即被 Bootstrap 隐藏（互斥共建底部条带）；桌面图标避让基准不依赖 dock。
- **自动隐藏**：空闲阈值制（任何系统输入即不隐藏）；淡出完成后**先 `SetMaterialSuspended(true)`（清 DWM 背景）再 Hide + ReleaseAppBar**，唤出 Show 后恢复材质再淡入——防止隐藏后原地残留半透明玻璃带（DWM 材质不随 WPF Opacity 变化）。
- **任务视图**：`MultitaskingViewVisibilityService`（QueryService Shell → IsViewVisible 轮询）配合 Win+Tab 期间的处理。
- **节拍**：`DockTickPolicy.NextIntervalMs(statePending, cursorNearDock)` 快 60ms / 慢 250ms 自适应，纯函数可测。

## 8. 交互红线（踩坑沉淀，勿回退）

1. **拖拽重排**：进入拖拽（>6px 阈值）必须 `container.CaptureMouse()`；收尾统一 `FinishReorderDrag`（先清状态再 ReleaseMouseCapture，幂等）；`LostMouseCapture` 也要提交（防系统夺捕获丢排序）。
2. **模板创建**：`DockItemTemplate` 必须在 DockWindow ctor 无条件创建（跨插件 Provide/Get 不可假设装配顺序）。
3. **运行检测**：`IDockService.SetRunning` 整体替换语义；运行/固定合并在 DockWindow 内完成。
4. **持久化失败**：保持内存状态，记录诊断，不崩。

## 9. 配置

- 设置键（经 `DockVisualSettings` 订阅实时生效）：`dock.bottomMargin`、视觉参数等；
- 持久化：`dock-pinned.json`（固定列表，经 IPinningService）、应用分组（AppGroupStore）。

## 10. 性能

- 图标异步 + 缓存 + 预取（上游 IAppIconService）；
- 运行检测节流 + DockTickPolicy 自适应拍；
- 缩略图延迟生成（窗口可见才更新）；DWM 缩略图实现已上移 shell-window-tracker。

## 11. 验收现状

- ✅ 固定 + 运行应用呈现、真实图标（上游高清通道）
- ✅ 点击启动/切换、拖拽重排（捕获修复后）持久化
- ✅ 屏幕底边锚定 AppBar、空闲自动隐藏（材质挂起防残影）、全屏/任务视图配合
- ✅ 右键自管菜单（DockMenuPopup）；AppGrabber 批量排序/搜索/分组
- ✅ 原生任务栏联动隐藏/恢复；单测 11 例全绿（DockAppsService/FakeAppSource 等）
- ⏳ 开放：运行检测轮询 vs WinEventHook 事件驱动；AppGrabber 拆独立插件（当前在 DockPlugin 内，dock 卸载连带关闭）

## 12. 开放问题

1. AppGrabber 拆分为独立 `shell-app-center` 插件（§11 待办，需先抽 AppCenterHost 生命周期）；
2. 运行检测事件化取舍；
3. 缩略图浮层与窗口预览的进一步交互（peek 语义已在 window-tracker 侧）。
