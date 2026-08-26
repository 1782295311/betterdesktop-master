# BetterDesktop.Shell.WindowTracker

> 角色：`shell.window-tracker` — 运行中窗口 / 应用追踪基础服务

## 职责

- 枚举当前可见的顶层窗口（排除工具窗口 / DWM cloaked / 自身 shell 进程）。
- 将窗口按可执行路径关联到 `IAppSourceService` 的 `AppItem`，输出聚合后的运行中应用列表。
- 通过 `SetWinEventHook` 监听窗口创建 / 销毁 / 前台 / 标题变化，防抖后发出 `RunningAppsChanged`。
- 提供窗口激活能力（还原最小化 + 置前）。
- 收口所有窗口枚举相关的 P/Invoke（EnumWindows / SetForegroundWindow / DwmGetWindowAttribute / SetWinEventHook），其他包不直接写。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Shell.AppSource`（IAppSourceService / AppItem / AppItemId）

## 对外扩展点

- 消费方通过 `Inject` 声明依赖 `IWindowTrackerService` 即可获得服务，无需自行实现窗口枚举。
- 当前无其他扩展点；未来若需分组/过滤策略，可在 `Contracts/` 增加接口并登记。

## Known Limitations

- `GetRunningApps` 每次调用都会重新 `ScanStartMenu` + `ScanInstalledApps` 构建路径索引，未在服务内做缓存（Dock 当前仍直接走 `RunningAppDetector`，不经过此方法；待 Step 5/7 改写 Dock 后可加缓存）。
- `RunningAppDetector` 关键成员目前为 `public`（供 shell-dock UI 层临时直接调用），待 Dock 改写为走 `IWindowTrackerService` 后可收敛为 `internal`。
- 窗口事件回调运行在 WinEvent 线程，事件统一通过捕获的 `SynchronizationContext`（UI 线程）回抛；若插件在非 UI 线程加载且无法捕获同步上下文，事件将在回调线程直接触发，订阅方需自行保证线程安全。
