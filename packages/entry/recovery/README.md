# BetterDesktop.Recovery

紧急恢复入口：在异常状态下一次性拉起 Recovery 流程（与设置中心的应急恢复入口配合）。

## 依赖

- `BetterDesktop.Kernel`（进程拉起）
- 编译期共享 `shared/logging/*.cs`

## Known Limitations

- 设置中心 `SystemSection` / `SystemManagement` 仍是 recovery 的一次性拉起点（architecture-allowlist 登记，保留为终态所有者之一）
