# STATUS.md — 当前阶段与交接指针

> 地位：**新会话接手的第一入口**。任何新 AI 会话/新成员动手前必须先读本文件。
> 更新义务：每阶段结束、每次重大决策落地后更新本文件（阶段、HEAD、下一步、待办）。

## 当前状态

| 项 | 值 |
|---|---|
| 当前阶段 | **P1 全部交付**：kernel / kernel-loader / kernel-timer + 27 例契约测试 + 架构守护 + 覆盖棘轮（83/91/98%），待用户验收 |
| 当前 HEAD | `1647795`（test-coverage 红证后） |
| 门禁 | 9 道（含 dotnet-format / test-coverage），9 份变红物证齐全 |
| 设计健康规则 | 十三份规范（清单见 `AGENTS.md` 文档纪律第 5 条索引，M6–M16 登记）+ `.editorconfig` + `CONTRIBUTING.md` + `docs/lessons.md`；机检状态逐条标注 |
| 更新时间 | 2026-08-20 |
| 下一步 | P1 验收（用户确认）→ P2：薄宿主 + 最小外壳 + HMR + 角色替换演练 |

## 交接检查清单（动手前按序完成）

1. 读本文件，确认阶段与下一步。
2. 读 `AGENTS.md`（全仓规范）、最新 ADR、`docs/MECHANISMS.md`、`docs/coding-standards.md`。
3. 确认 PowerShell 7（`pwsh -v`），跑 `pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`，**基线必须全绿**；红了先修再动手。
4. 查阅 `.agents/notes/implemented/` 最新决策记录与 `docs/architecture/P0-完成报告.md`。
5. 任务涉及机制/红线/冻结项变更？→ 先走 ADR 或注册表流程再写代码。

## 收工检查清单

1. 门禁全绿（`run-gates.ps1` exit 0）。
2. 非平凡决策已落决策记录；门禁改动已留变红物证。
3. 更新本文件的阶段/HEAD/下一步。
4. 提交信息：中文 + 范围前缀（`gates:` / `docs:` / `kernel:` / `shell:` 等）。

## 关键指针

- 宪法与红线：`docs/architecture/ADR-001.md` · 重建方案：`docs/architecture/PLAN-重建方案.md` · P0 验收：`docs/architecture/P0-完成报告.md`
- 门禁入口：`scripts/run-gates.ps1` · 门禁契约：`scripts/AGENTS.md` · 物证标准：`docs/guard-redproof/README.md`
