# BetterDesktop.Cli

桌面套件的命令行入口层（headless 模式）：负责一次性 ensure core、以命令行方式执行系统集成（status / register / repair / unregister）与更新/恢复等操作，可在无宿主时降级拉起 `DesktopControl` / `Host`。

## 依赖

- `BetterDesktop.Kernel`（core 拉起）
- `BetterDesktop.Shell.Core` / `BetterDesktop.Shell.Capture`（能力调用）

## 职责边界

- 命令一律经 core 触发；细节由 CLI 处理（core 只发命令）。
- `--system-integration` 系列命令的契约字面量由 verify-system-integration 门禁钉住（与 install/uninstall/publish 脚本一致）。

## Known Limitations

- headless 直写配置路径仍被 architecture-allowlist 登记（removeBy: S7），迁往 core 控制管道前不可新增同类写入
