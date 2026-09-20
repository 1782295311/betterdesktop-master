# packages/ — 包目录标准

> 每个包 = 一个 csproj + 同目录 `README.md`。本目录现承载 kernel / shell / api 全部包（30+ csproj）。

## 包是什么

包是仓库的最小交付单元：Cordis 内核与外壳的每个能力都落为一个包，对应 `BetterDesktop.<域>.<名>` 命名空间。目录布局 `packages/<域>/<名>/`（如 `packages/kernel/kernel/`）。

## README.md 必备小节（由门禁 `verify-package-readme` 强制）

1. 一句话职责（包名下方直接写）
2. `## 依赖`：声明它 inject 的内核服务
3. `## 扩展点`：它对其他插件开放的接口/服务（无则写「无」）
4. `## Known Limitations`：**必须存在**，且其下至少一条 `- ` 列表项（哪怕写「当前无已知限制」也算一条，但鼓励写真实边界）

## 未来标准（P1 起）

- 每个包一个 `Invariant.cs`：声明包名 + 一条运行时关系断言，或说明豁免理由（对应 DSH 的 package-invariants 机制）。
- 包间依赖只允许沿分层方向（分层定义见 ADR-001 与未来的架构测试）。
