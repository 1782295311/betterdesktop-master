# Agent Note: 毛玻璃共享服务收敛至 shell-core

Status: implemented

## Problem

bar 与 dock 都需要毛玻璃效果，如果把 DwmHelper 与 Apply 各自实现一遍，会出现三份 P/Invoke 与三套参数映射，后续修 Windows 版本差异时要改多处，极易漂移。

## Decision

将统一的 IVibrancyService 与 DwmHelper（原样移植自 FrostedGlassDemo）放在 shell-core，bar/dock 仅通过 Inject 依赖 IVibrancyService 调用 Apply，不重复实现毛玻璃逻辑。

## Alternatives considered

- 每个部件各自内联 DwmHelper：实现最直接但重复三次，维护成本高，否决。
- 抽成独立 Vibrancy 包：隔离更彻底，但 v1 包数量已偏多，收敛到 shell-core 更简单，否决。
- 用 CommunityToolkit.WinUI 或第三方毛玻璃库：引入额外依赖与跨技术栈风险，否决。

## Consequences

shell-core 成为毛玻璃唯一真源；bar/dock 变小且只关心布局。代价是 shell-bar/shell-dock 必须依赖 shell-core（已通过 ProjectReference 满足），且 Windows 版本差异集中在一处修复。
