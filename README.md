# Better Desktop Cordis

> Windows 桌面外壳的**从零重写**：Cordis 风格插件内核（C# 复刻）+ DeepSeek Harness 式双重纠错工程纪律。
> 旧仓 `cairoshell原版` 保持只读，仅作行为参考，逻辑代码零复制。

- 状态：**P0 地基**（门禁先于业务）
- 技术基线：net8.0-windows + WPF（已冻结，见 `docs/architecture/ADR-001.md`）

## 快速导航

| 想看什么 | 去哪里 |
|---|---|
| 全仓行为规范 | `AGENTS.md` |
| 架构总览 | `docs/architecture.md` |
| 宪法（七条红线 + 四项基线决策） | `docs/architecture/ADR-001.md` |
| 重建方案（已确认 v1.0） | `docs/architecture/PLAN-重建方案.md` |
| 机制唯一化注册表 | `docs/MECHANISMS.md` |
| 决策记录制度 | `.agents/notes/README.md` |
| 门禁唯一入口 | `scripts/run-gates.ps1` |

## 一句话说明

这个仓库第一阶段没有任何业务代码。P0 的全部产出是：宪法、门禁、决策记录制度、以及证明每条门禁「真的会变红」的物证。它们是后续所有代码的保险丝。
