# BetterDesktop.Tray

系统托盘常驻进程（旧实现）：承载托盘图标与菜单、设置桥接、进程桥接等，将被 Rust core 取代。

## 依赖

- `BetterDesktop.Kernel` / `BetterDesktop.Shell.Core`
- 编译期共享 `shared/logging/*.cs`

## Known Limitations

- `ProcessBridge.cs` / `TrayApplicationContext.cs` 在 architecture-allowlist 登记（removeBy: S5），随 Rust core 接管逐步删除
- 托盘自启值名 `BetterDesktop.Tray` 被 verify-system-integration 门禁钉住，改名需同步 install/uninstall 脚本
