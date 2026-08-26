# BetterDesktop.Shell.Core.Tests

shell-core 单元测试工程：覆盖 VibrancyMapping 纯函数映射等可测逻辑。

## 依赖

- `BetterDesktop.Shell.Core`
- `BetterDesktop.Kernel`
- xunit 三件套（Microsoft.NET.Test.Sdk / xunit / xunit.runner.visualstudio）

## Known Limitations

- 仅覆盖纯函数映射，P/Invoke 与窗口行为留集成测试（P1）
