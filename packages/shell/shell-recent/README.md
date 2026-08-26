# BetterDesktop.Shell.Recent

> 角色：`shell.recent` — 最近程序 / 最近文档 / 跳转列表固定项

## 职责

- **最近程序**：订阅 `IWindowTrackerService.ForegroundWindowChanged`，把前台 hwnd 映射到运行中应用（经 `GetRunningApps` + `ResolveFromPath`）并自增本地使用计数；持久化 `%APPDATA%/BetterDesktop/recent-programs.json`（上限 30 条，超限丢弃最不常用）。
- **最近文档**：读 `%APPDATA%\Microsoft\Windows\Recent` 下 `.lnk`（按最近写入时间降序），可直接 ShellExecute 打开。
- **跳转列表固定项**：按宿主应用分区持久化 `%APPDATA%/BetterDesktop/recent-jumplist.json`（`appId → 固定项 AppItemId 列表`）。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Shell.AppSource`（IAppSourceService / AppItem）
- `BetterDesktop.Shell.WindowTracker`（IWindowTrackerService，前台变化追踪）

## 对外扩展点

- 消费方经 `Inject` 依赖 `IRecentItemsService` 即可读取最近程序/文档、读写跳转列表固定项。
- 前台追踪随服务构造自动开启（`RecentPlugin` 注入 `IWindowTrackerService` 后 Provide），卸载时退订。

## Known Limitations

- 前台变化 → `GetRunningApps()` 全量窗口枚举（映射 hwnd→exe），前台切换频繁时有一定开销；当前仅按「前台 hwnd / 应用」变化去重。若后续卡顿，可给 window-tracker 增加 hwnd→exe 直查。
- 最近文档依赖 Windows Recent 目录（启用「在开始菜单和任务栏显示最近打开的项目」时由系统维护）；关闭该功能则目录为空。
- 跳转列表持久化用自有 JSON 文件（未走 ISettingsService，避免 shell-recent 依赖 shell-settings 的注册顺序问题）。
- 计数按「切换到不同应用」而非「启动事件」——同一应用 alt-tab 不重复计数，语义上等价于使用会话。
