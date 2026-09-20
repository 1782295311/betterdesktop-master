# BetterDesktop.Shell.WindowTracker

> 角色：`shell.window-tracker` — 运行中窗口/应用追踪 + DWM 缩略图（Aero Peek）基础服务

## 职责

- 枚举当前可见的顶层窗口（排除工具窗口 / DWM cloaked / 自身 shell 进程），将窗口按可执行路径关联到 `IAppSourceService` 的 `AppItem`，输出聚合后的运行中应用列表。
- 提供 `IWindowTrackerService`：`RunningAppsChanged` / `ForegroundWindowChanged` 事件、`GetRunningApps` / `IsRunning` / `GetWindowsOfApp` / `GetWindowTitle`、窗口激活（还原最小化 + 置前）。
- **Thumbnail 子系统**：`DwmThumbnail`（DWM 实时缩略图控件）/ `ThumbnailWindow`（缩略图浮层）/ `WindowPeek`（悬停临时置顶），供 Dock 等包直接消费。
- 窗口枚举 P/Invoke 集中在 `Native/RunningAppDetector`；WinEvent 钩子统一走 shell-core `WinEventPump`（STA 泵线程，7435 收口），本包不再直接 `SetWinEventHook`。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Api`（AppSource 域契约：IAppSourceService / AppItem / AppItemId）
- `BetterDesktop.Shell.Core`（WinEventPump 统一事件泵）

## 对外扩展点

- 消费方通过 `Inject` 声明 `IWindowTrackerService` 即可获得服务，无需自行实现窗口枚举。
- Thumbnail 子系统（DwmThumbnail / ThumbnailWindow / WindowPeek）为公共消费面（shell-dock 缩略图交互、shell-window-tracker-tests 覆盖）。

## Known Limitations

- `GetRunningApps` 每次调用都会重新 `ScanStartMenu` + `ScanInstalledApps` 构建路径索引，未在服务内做缓存（Dock 当前仍直接调用 `RunningAppDetector`，其成员暂为 `public`；待 Dock 改走 `IWindowTrackerService` 后收敛为 `internal` 并加缓存）。
- 窗口事件回调运行在 WinEvent 泵线程，事件统一通过捕获的 `SynchronizationContext`（UI 线程）回抛；若插件在非 UI 线程加载且无法捕获同步上下文，事件将在回调线程直接触发，订阅方需自行保证线程安全。
