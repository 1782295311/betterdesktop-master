# AGENTS.md — Better Desktop Cordis 全仓规范

> 本文件是仓库所有参与者（人与 AI 代理）的统一行为规范。修改本文件须走决策记录（见 `.agents/notes/README.md`）。

## 这是什么仓库

Better Desktop Cordis（`better-desktop-cordis`）是 Windows 桌面外壳的从零重写：

- **内核**：Cordis 风格插件内核的 C# 复刻——一切皆插件、服务图、依赖驱动重载、托管生命周期、HMR。
- **工程纪律**：复刻 DeepSeek Harness 的「双重纠错机制」——第一层解释性文档贴近代码（包级 README、决策记录、事后分析），第二层机器校验对齐（`scripts/` 门禁）。
- **旧仓只读**：`cairoshell原版` 仅作行为参考。逻辑代码零复制；唯一允许借用的资产是白名单内的原生结构体布局（登记于 `docs/MECHANISMS.md` M4，须逐条核对 ABI）。主题、图标、XAML 一律重建。

## 仓库布局

```
docs/           架构、教程、事后分析、物证（决策记录在 .agents/notes/，刻意分层）
packages/       每个功能 = 一个包（csproj + README.md），P1 起逐步落地
scripts/        门禁（verify-*.ps1）与生成器（gen-*.ps1），唯一入口 run-gates.ps1
host/           薄宿主 exe（P2 起）
.agents/notes/  决策记录（proposed/implemented/rejected + archived 密封归档）
```

## 文档纪律

> 分层（一个事实一个家）、写作规则与 slop 反模式清单见 `docs/doc-standards.md`。

1. 一段一行物理行（不手工硬换行，`verify-md-wrap` 机检）；相对链接只指向仓库内文件（`verify-md-links` 机检）；锚点链接先确保目标标题存在。
2. 每个包（`packages/**/*.csproj`）必须有同目录 `README.md`，且含 `## Known Limitations` 小节（至少一条 `- ` 列表项）。覆盖率 100%，由 `verify-package-readme` 棘轮强制。
3. 关键文档有字数预算（`scripts/manifests/doc-budgets.manifest.json`）。超限先精简；确实要放宽上限，必须在决策记录中说明理由。
4. 决策记录写进 `.agents/notes/`；事故写进 `docs/postmortem/NNNN-title.md`。两者格式由门禁强制。
5. 规则文档索引（均强制，按域取用）：`docs/coding-standards.md`（代码）/ `docs/engineering-conventions.md`（工程铁律）/ `docs/defensive-patterns.md`（防御性模式）/ `docs/TERMINOLOGY.md`（命名）/ `docs/ui-foundation.md`（UI）/ `docs/reuse-rules.md`（复用）/ `docs/extension-rules.md`（扩展契约）/ `docs/pluginization.md`（插件化行为）/ `docs/ai-control.md`（AI 控制面）/ `docs/testing.md`（测试）/ `docs/runtime-health.md`（运行时）/ `docs/security.md`（安全）/ `docs/build-release.md`（构建发布）/ `docs/product-quality.md`（质量）/ `docs/code-review.md`（评审）。

## 门禁纪律

1. 唯一入口：`scripts/run-gates.ps1`。新增门禁 = 新增 `verify-*.ps1` + 登记进 `run-gates.ps1` 注册表，同一变更落地（注册一致性由 `verify-gate-registry` 机检，漏登记即红）。
2. **唯一合法解释器是 PowerShell 7（`pwsh`）**：Windows PowerShell 5.1 会把仓库内 UTF-8 无 BOM 脚本读成乱码导致解析失败。全量检查命令：`pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`。每次改动收工前必须全绿。
3. 每条门禁必须有门禁单测（`scripts/verify-*.Tests.ps1`，Pester），覆盖「非法输入 → 返回违规」；契约见 `scripts/AGENTS.md` 第 5 条。
4. 门禁失败即阻断：退出码非 0 一律视为失败；禁止注释掉门禁、禁止在 CI 中跳过门禁。

## 会话交接（新会话接手的第一件事）

新 AI 会话 / 新成员动手前按序完成：① 读 `docs/architecture/STATUS.md`（当前阶段与下一步）；② 读本文件、最新 ADR、`docs/MECHANISMS.md`、`docs/coding-standards.md`；③ 用 PowerShell 7 跑一遍全量门禁确认基线绿；④ 浏览 `.agents/notes/implemented/` 最近决策记录。标准流程见 `docs/cookbook/会话交接.md`。

## 变更纪律

1. 新增/替换横切机制 → 先登记 `docs/MECHANISMS.md`；替换已登记机制 → 新开 ADR。
2. 非平凡设计决策必须伴随决策记录；没有决策记录的变更可以被打回。
3. 提交信息用中文，前缀标明范围（如 `gates:` / `docs:` / `kernel:`）。
4. 分支与合并：main 受保护——禁止直接 push、禁止 force push；合入须经 `docs/code-review.md` 评审；主干破坏优先回滚再前修（ADR-001 R2 同口径）。

## 命名

- 根命名空间 `BetterDesktop.*`；包目录 `packages/<域>/<名>`（如 `packages/kernel/kernel`）。
- 术语表 = `docs/TERMINOLOGY.md`（已落地，命名的唯一真相源）。
