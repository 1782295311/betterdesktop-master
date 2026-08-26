# BetterDesktop.Shell.Pinning

> 角色：`shell.pinning` — 通用固定 / 收藏服务（按 zone 分区）

## 职责

- 以 zone（dock / startmenu / taskbar 等）为单位管理固定应用列表，供 Dock、未来开始菜单、任务栏共用。
- 固定项按 `AppItem` 快照持久化到 `%APPDATA%/BetterDesktop/pinning.json`，结构为分区字典 `{ "dock": [...], "startmenu": [...], "taskbar": [...] }`。
- 首次运行时若 `pinning.json` 不存在但旧 `dock-pinned.json` 存在，自动将旧文件迁移到 `"dock"` zone（旧文件保留为备份，不删除）。
- 提供 Pin / Unpin / Reorder / IsPinned / GetPinned，并在任意变更时发出 `PinnedChanged`（携带 zone 与新快照）。
- 所有方法 try-catch，异常记日志不冒泡；持久化失败保持内存状态不崩（M10）。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Shell.AppSource`（IAppSourceService / AppItem / AppItemId）

## 对外扩展点

- 消费方通过 `Inject` 声明依赖 `IPinningService` 即可获得服务，无需自行实现固定逻辑。
- 新增固定宿主（如开始菜单、任务栏）只需调用 `GetPinned("startmenu")` / `Pin("startmenu", appItem)` 等，互不干扰（zone 隔离）。
- 未来若需固定项元数据（图标/角标），可在 `PinnedItem.AppItem` 基础上扩展，不影响分区模型。

## Known Limitations

- `Pin` 接受 `AppItem` 快照而非仅 `AppItemId`：因 `IAppSourceService` 未暴露 GetById，且仅存 Id 无法在读取时零成本重建展示信息；快照在 Pin 时捕获，保证 Dock 名称/图标/快捷方式零失真。
- 旧 `dock-pinned.json` 的 `DockAppType.Url` 在迁移时归并为 `AppSource.Installed`；`.url` 扩展名会在 Dock 侧经 `AppSourceConverter.ToDockAppType` 重新识别为 Url，行为一致。
- 持久化采用全量覆盖写（无增量 diff）；固定列表规模小，性能可忽略。
