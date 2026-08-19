# CONTRIBUTING.md — 社区贡献指南

> Better Desktop Cordis 欢迎第三方贡献。本文件回答「怎么提交一个合格的 PR」，细节规则以仓库内文档为准。

## 开始之前

1. 读 `docs/architecture/STATUS.md` 确认当前阶段，再读 `AGENTS.md`（全仓规范）。
2. 设计类变更（机制、红线、契约）先看 `docs/MECHANISMS.md` 与最新 ADR，多数情况需要先开决策记录。
3. 环境要求：Windows + PowerShell 7 + .NET 8 SDK。门禁唯一合法解释器是 `pwsh`。

## 提交一个 PR 的最低门槛

1. 全量门禁绿：`pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`。
2. 非平凡决策附决策记录（格式见 `.agents/notes/README.md`）。
3. 改动门禁脚本时必须附带变红物证（`docs/guard-redproof/`）。
4. 提交信息中文 + 范围前缀（`gates:` / `docs:` / `kernel:` / `shell:`）。
5. 代码遵守 `docs/coding-standards.md`；UI 遵守 `docs/ui-foundation.md`；复用遵守 `docs/reuse-rules.md`；对外契约遵守 `docs/extension-rules.md`。

## 写插件

插件作者只需读 `docs/extension-rules.md`（清单草案、扩展点、兼容策略）与目标包的 README「扩展点」节。

## 反馈与问题

提 issue 时附上：期望行为、实际行为、复现步骤（含门禁输出）。事故类问题请参照 `docs/postmortem/README.md` 的编号制度。
