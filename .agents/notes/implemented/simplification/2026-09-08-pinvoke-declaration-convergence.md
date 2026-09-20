# Agent Note: P/Invoke 声明收口范围——桶 B 归零、桶 C 不主动上提

Status: implemented

## Problem

原生声明在各包内重复出现：同一函数被多个模块各自 `DllImport`，签名与结构体布局容易漂移；缺少权威层时改一处不会传播到其他调用方，崩溃与错误码表现因此不一致。

## Decision

按声明重复度分三桶，权威层固定为 `shell-core/Native`，各桶处置如下：

| 桶 | 定义 | 处置 |
|---|---|---|
| A | 跨模块出现 ≥2 次的同一函数 | 收口到权威层并改引（桶 A 清零） |
| B | 权威层已有、模块内重复声明 | 删除模块内声明（桶 B 清零） |
| C | 全仓唯一声明 | **不主动上提**；仅在出现第二使用方、或属高频通用函数时上提 |

非 packages 范围（T4）逐项裁决：

| 范围 | 决策 | 理由 |
|---|---|---|
| poc/system-status-poc | 豁免 | 参考演示工程，非生产装配；门禁只覆盖 packages |
| docs/ 草案 | 不处理 | 文档草案不参与编译 |
| tools/GdiAudit | 豁免 | 独立诊断工具，无 core 引用 |
| recovery/Program.cs | 豁免 | 单文件独立程序，引 core 的依赖成本大于收益 |
| kernel/SystemMemoryProbe.cs | 豁免 | kernel 是 shell 的下层，反向引用违反分层 |
| shell-app-source | 豁免 | 既定裁决：不改依赖拓扑，保留局部声明（签名已对齐权威口径） |
| shell-desktop `LVM_HITTEST` 变体 | 豁免 | 业务专用签名（`ref LVHITTESTINFO`），通用 `SendMessage` 无法表达 |

唯一声明审计（T5）：本批上提 `WindowFromPoint`（唯一调用方为 shell-desktop，属高频通用函数）；其余唯一声明维持现状。候选上提清单（menu-bar 蓝牙/电源簇、context-menu、status、IME 声明簇）在出现第二使用方时再处理。

## Alternatives considered

- **把桶 C 唯一声明也全部上提**：无收益的机械改动，且会把模块专用结构体（`LVHITTESTINFO`、`ACCENT_POLICY` 等）挤进通用层，污染权威声明集。
- **不收口、靠评审约束签名一致**：已出现真实签名漂移，评审无法在每次改动时穷举全仓调用点。
- **给非 packages 工程统一加 core 引用**：会改变依赖拓扑（如 kernel 反向依赖 shell），成本大于收益。

## Consequences

- 门禁 `verify-native-convergence` 只统计 `packages/**` 内非 `shell-core/Native` 的 `DllImport` 总数（当前 89 ≤ 281），保证收敛趋势而非绝对清零。
- 桶 B/C 的可见性由独立脚本 `Temp/dup_decl_compare.py` 提供，口径与门禁互补：门禁看 packages 趋势，脚本看全仓重复。
- 「唯一声明不主动上提」使权威层不会无限膨胀，代价是同类函数可能长期分散，需在出现第二使用方时及时上提。
- 已知残留：poc 的 `NativeDwm*` 命名与权威 `Dwm*` 不同名但互不引用；`GetSystemPowerStatus` 的 poc 引用与权威 `out` 版本存在真实差异（poc 豁免，若纳入生产再统一）。
