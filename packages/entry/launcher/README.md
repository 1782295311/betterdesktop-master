# BetterDesktop.Launcher

一次性启动入口：确保 core 已就绪后按目标状态装配并立即退出（ApplyDesired 即退），不常驻。

## 依赖

- `BetterDesktop.Kernel` / `BetterDesktop.Kernel.Loader`

## Known Limitations

- 仍被 architecture-allowlist 的 lifecycle-owner 棘轮登记（removeBy: 保留为终态所有者之一），后续拉起职责收敛到 core 后需同步收缩清单
