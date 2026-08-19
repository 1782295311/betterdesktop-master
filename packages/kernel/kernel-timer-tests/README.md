# BetterDesktop.Kernel.Timer.Tests

kernel-timer 契约测试：一次性/周期触发、注销取消、回调异常隔离。

## 依赖

- `BetterDesktop.Kernel.Timer`、`BetterDesktop.Kernel` 与测试框架（xUnit）。

## 扩展点

无。

## Known Limitations

- 覆盖 setTimeout / setInterval 语义；debounce / throttle 属 P1 后续版本，不在本包测试范围
