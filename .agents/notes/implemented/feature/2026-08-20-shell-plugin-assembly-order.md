# Agent Note: 宿主插件装配顺序

Status: implemented

## Problem

shell-core 依赖 IWindowHandleService 与 IVibrancyService，bar/dock 又依赖 IDesktopSurface（由 shell-core 提供）；若顺序或 Provide 时机不对，依赖注入会停在 PENDING 或报空。

## Decision

宿主先 Provide<IWindowHandleService> 再 Plugin(loader)；VibrancyService 由宿主直接 Plugin 先于 loader；cordis.yml 严格按 shell-core → shell-bar → shell-dock 顺序装配，确保依赖链在加载前就绪。

## Alternatives considered

- 所有插件都走 loader 工厂、不手动 Plugin VibrancyService：依赖顺序更难保证，否决。
- 在 shell-core 内 new VibrancyService 并 Provide：耦合更紧，否决当前更解耦的宿主直挂方案。
- 用 Extend 子上下文隔离：v1 单上下文即可，过度设计，否决。

## Consequences

依赖链在 UI 线程同步就绪，bar/dock 的 LoadAsync 可在主线程同步创建并显示窗口；代价是 cordis.yml 与宿主 Factories 键必须严格一致（已对齐 builtin.shell-*）。
