# BetterDesktop.Shell.Notification

> 角色：`shell.notification` — 新装应用通知（右下角弹窗）

## 职责

- 订阅 `IAppSourceService.AppSourceChanged`（开始菜单变化已由 app-source 监控防抖 1s），检测真正新增的应用并弹窗提醒。
- 弹窗支持一键固定到 Dock（`IPinningService.Pin("dock", app)`）与全部忽略；关闭/忽略时自动标记"已见"，重复安装不重复弹。
- 启动时后台首查：首次调用建立"已见"基线（不把存量应用当新装），后续变化事件才触发提醒。
- 与 Dock 解耦：不引用 shell-dock，Dock 卸载后通知仍能弹出（步骤4 验收项）。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Shell.Core`（ShellWindow 基类 / IVibrancyService / IAppearanceService）
- `BetterDesktop.Shell.AppSource`（IAppSourceService / IAppIconService / AppItem）
- `BetterDesktop.Shell.Pinning`（IPinningService，一键固定）

## 对外扩展点

- 消费方通过 `Inject` 声明依赖 `INotificationService` 即可主动触发新装应用弹窗（`ShowNewAppsNotification`）。
- 未来若需通用通知（标题/正文/图标/回调），可在 `Contracts/` 增加 `NotificationDescriptor` 与通用 `Show` 方法；当前仅新装应用一类消费者，暂不引入死代码。

## Known Limitations

- 弹窗创建依赖 `IVibrancyService`；该服务由内核常驻提供，正常情况下不会缺失。
- `ShowNewAppsNotification` 必须从 UI 线程调用（服务内部已做 Dispatcher 切换，外部直接调用需自行保证）。
- 通知基于注册表"已安装程序"增量检测（复用 app-source 的"已见"持久化集合）；开始菜单目录内直接新增的快捷方式也会触发（监控的是 Programs 目录）。
- 固定按钮依赖 `IPinningService`（shell-pinning）；若该插件未加载则固定按钮静默失效，不影响弹窗本身。
