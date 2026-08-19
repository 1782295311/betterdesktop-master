# MECHANISMS.md — 机制唯一化注册表

> 地位：「一个关注点一套机制」裁决的**唯一真相源**。任何新增/替换机制的提案必须先登记或引用本表。
> 与 ADR-001 的关系：ADR-001 是宪法，本表是宪法的机制清单；任一方改动时必须当场确认另一方仍指得通。
> 版本：v0.2 · 2026-08-20（v0.2: 补 M6–M8 设计健康规则；M3 枚举改为指向 run-gates 注册表）

## 登记表

| # | 关注点 | 选用机制（唯一） | 禁止的重复机制 | 关联护栏 |
|---|---|---|---|---|
| M1 | 门禁执行入口 | `scripts/run-gates.ps1`（门禁注册表 + 依赖 + 失败聚合） | 各处自写门禁跑法、绕过 run-gates 的 CI 脚本 | run-gates 自身即护栏；每条门禁须有变红物证 |
| M2 | 决策记录 | `.agents/notes/{proposed,implemented,rejected}/{class}/`（格式与归档由门禁强制） | 另立 `decisions/`、根目录散落的决定文档、任何 INDEX 索引 | `verify-agent-note` / `verify-archived-notes` |
| M3 | 文档机器校验 | `scripts/verify-*.ps1` 门禁集 + `run-gates.ps1` 注册表（**注册表是唯一清单，本文不复述枚举**） | 手写一次性检查脚本、人肉核对文档一致性 | run-gates 注册表 |
| M4 | 旧仓资产借用 | 白名单登记制：仅个别**原生结构体布局**可借鉴（逐条 ABI 核对后登记） | 复制旧仓逻辑代码 / 主题 / 图标 / XAML 资源 | ADR-001 红线 R5；白名单见本表附录 A |
| M5 | 插件内核 | Cordis 内核 C# 复刻（P1 落地，此处为规划登记） | 第二套插件机制、MEF、外挂式扩展宿主模型 | P1 起 `kernel` 包 + 内核测试集 |
| M6 | 统一 UI 地基 | 单一 `packages/shell/shell-foundation` 包 + [`docs/ui-foundation.md`](ui-foundation.md)（字体三令牌 / 统一 ShellWindow / 语义令牌 / 公共控件唯一来源） | 各包自建窗口体系、硬编码字体/颜色/圆角、第二套主题令牌、重复控件/转换器 | P2 机检（`verify-ui-hardcoded` / `verify-token-coverage`） |
| M7 | 功能复用与反臃肿 | [`docs/reuse-rules.md`](reuse-rules.md)（单一实现 / 共享层上移 / 包粒度判据 / 禁复制 / 依赖单向） | 复制粘贴、同义实现并存、越层依赖、包无节制膨胀 | P1 机检（`verify-code-duplication` + 架构测试） |
| M8 | 扩展性与社区契约 | [`docs/extension-rules.md`](extension-rules.md)（扩展点显式化 / plugin.json v1 草案 / API 兼容与弃用 / 文档义务）+ [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | 隐式扩展点、破坏性变更静默、无文档能力、第二套扩展机制 | `package-readme` 已强制扩展点节；其余 P1/P2 逐步机检 |

## 流程

1. 新增关注点：在本表登记一行（关注点 / 选用 / 禁止 / 护栏），同一变更落地。
2. 替换已登记机制：**新开 ADR**，不得静默替换。
3. 登记即承诺：登记「禁止」清单后，对应护栏必须在门禁中可执行，否则在表中如实标注「暂无机检」。

## 附录 A · M4 白名单（旧仓可借鉴的结构体布局）

| 条目 | 来源（旧仓文件） | ABI 核对状态 |
|---|---|---|
| （空） | 尚无任何条目获准 | — |

> 每借用一条，先在此登记并完成「逐条核对 ABI」的物证，才能进入新仓代码。
