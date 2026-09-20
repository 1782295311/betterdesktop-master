# BetterDesktop.Kernel.Tests

内核三机制契约测试包（服务图 / 依赖驱动重载 / effect 逆序清理）+ 事件服务契约测试 + 电源管理（PowerManagementTests）与资源治理（ResourceGovernorTests）契约测试（ADR-002 P1 验收判据）。

## 依赖

- 本包仅依赖 `BetterDesktop.Kernel` 与测试框架（xUnit），不依赖其他 BetterDesktop 包。

## 扩展点

无。

## Known Limitations

- 契约测试覆盖 P1 冻结面的行为语义；HMR / 进程外 / 拦截合并属 P2，不在本包测试范围
- 覆盖率棘轮与 CI 接入在 P1 后续版本落地
