# BetterDesktop.Kernel.Loader.Tests

kernel-loader 契约测试：装配启用条目、跳过禁用条目、未知工厂 fail-closed 不拖垮 loader。

## 依赖

- `BetterDesktop.Kernel.Loader`、`BetterDesktop.Kernel` 与测试框架（xUnit）。

## 扩展点

无。

## Known Limitations

- 覆盖 cordis.yml 装配语义；ALC 动态加载与 HMR 属 P2，不在本包测试范围
