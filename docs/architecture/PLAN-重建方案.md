# PLAN · Better Desktop Cordis 重建方案（已确认 v1.0）

> 状态：**已确认**（2026-08-19，用户拍板四项决策，并明确「脱离 cairoshell 结构、另起炉灶」）
> 上游：工作区《Cairoshell 重建方案》P0 草案 v0.1（本文件为其仓库内正式版，冲突以本文件为准）
> 依据：工作区《Cordis 内核实战调研报告》（cordis 4.0.1 源码级）；DSH 仓库实读

## 0. 决策摘要

| # | 决策 | 值 |
|---|---|---|
| D1 | 仓库 | `better-desktop-cordis`（新名，与 cairoshell 切割） |
| D2 | 技术基线 | 仅 net8.0-windows + WPF |
| D3 | 第一步 | 只做 P0 地基（门禁先于业务） |
| D4 | 旧仓资产 | 只读；逻辑零复制；主题/图标/XAML 不复制；仅白名单结构体布局可借鉴（逐条 ABI 核对） |

## 1. 为什么另起炉灶

旧仓三处结构性限制：外挂式扩展宿主（内核功能非插件）、net480 双 TFM（ALC 不可回收）、文档无人值守（README 1/32、断链/预算/格式全靠人肉）。Cordis 内核的「一切皆插件 + 服务图 + 依赖驱动重载」无法在旧结构上增量获得。

## 2. 目标形态

### 2.1 仓库布局（DSH 式，按 C# 现实调整）

```
docs/           架构、教程、事后分析、物证；决策记录独立于 .agents/notes/（刻意分层）
packages/       每个功能 = 一个包（csproj + README.md）
scripts/        verify-*.ps1 门禁 + gen-*.ps1 生成器；唯一入口 run-gates.ps1
host/           薄宿主 exe（P2）
.agents/notes/  决策记录：proposed/implemented/rejected + archived（SHA-256 密封）
```

### 2.2 内核（Cordis-in-C#，P1 落地）

Context 服务图（extend/isolate/intercept；显式 `Get<T>`，不用透明代理）→ Plugin（inject 依赖驱动重载）→ Fiber 状态机（PENDING→LOADING→ACTIVE→FAILED→UNLOADING→DISPOSED，epoch 去重）→ Effect（逆序并行清理、单条异常隔离）→ Events（五种分发）→ Loader（cordis.yml 插件树，无拓扑排序）→ HMR（双 ALC + 旧配置迁移 + 状态迁移接口 + 回滚）。WPF UI 操作经内核 Dispatcher 服务封送 STA 线程。

### 2.3 双重纠错（首期 12 项机制，P0 落地其中 5 道门禁）

| # | 机制 | 状态 |
|---|---|---|
| 1 | `run-gates.ps1` 统一门禁入口 | P0 ✅ |
| 2 | 包级 README 强制 + Known Limitations + 覆盖率棘轮 | P0 ✅ |
| 3 | 决策记录制度（.agents/notes 分类树 + 文件头 + 必需小节） | P0 ✅ |
| 4 | 归档密封（SHA-256 + manifest 追加保护） | P0 ✅ |
| 5 | 文档断链/锚点校验（Markdown 链接解析） | P0 ✅ |
| 6 | 文档字数预算（manifest + 超限报错） | P0 ✅ |
| 7 | 段落硬换行校验（一段一行） | P1（文档量上来后） |
| 8 | Mermaid 语法校验 | P1 |
| 9 | 源码注释中的文档引用校验（.cs 里引用 docs 路径） | P1 |
| 10 | 包不变式（每包 Invariant.cs） | P1 |
| 11 | 生成文档新鲜度（gen-* --check） | P1 |
| 12 | post-mortem 编号与索引一致性 | P1 |

> 中英配对（DSH i18n 三元组）不移植：新仓文档单一中文；其「结构一致性」思想由 README 固定小节与决策记录文件头检查承接。

## 3. 阶段划分

| 阶段 | 内容 | 完成判据 |
|---|---|---|
| P0 地基 | 宪法、门禁、决策记录制度、变红物证 | 门禁全绿 + 每道门禁有合格变红物证 + git 干净基线 |
| P1 内核 | kernel / kernel-loader / kernel-timer + 单测（无 UI）；ADR-002 冻结内核 API | 构建 0 错 0 警；内核单测全绿；服务图/epoch/effect 三机制各有物证 |
| P2 最小外壳 | shell-core + host 薄宿主接管桌面 + hello 插件 + HMR 打通 | 启动冒烟 PASS；改插件代码热重载生效（物证）；干净退出 |
| P3+ 功能切片 | ShellBar → Taskbar → Desktop → Explorer → Settings；每切片 = 包 + README + 决策记录 + 门禁全绿 | 每切片独立可运行、可回滚；行为对照清单逐项打勾 |

## 4. 风险与对策

| 风险 | 对策 |
|---|---|
| P0 无可见产出 | P0 极短；交付物是「会变红的门禁」，验收标准明确 |
| 内核过度设计 | 已按 C# 现实降级：显式 Get<T>、无透明代理、UI 走 Dispatcher 服务 |
| HMR 踩 ALC/静态引用坑 | HMR 推迟到 P2 才要求绿；内核静态引用清单纳入未来门禁 |
| 门禁脚本腐化 | 门禁也有变红物证；run-gates 失败即 CI 红 |
| 旧仓演进导致行为基准漂移 | 行为对照清单固定到旧仓 commit hash，记录于决策记录 |

## 5. P0 交付物清单（本阶段）

1. 仓库骨架 + `AGENTS.md` + `.gitignore` + 根 README
2. 宪法 [`ADR-001.md`](ADR-001.md)（七条红线 + 四项决策）
3. 机制注册表 [`../MECHANISMS.md`](../MECHANISMS.md)（M1–M5）
4. 决策记录制度（`.agents/notes/` 分类树 + 归档密封 manifest）
5. 首篇决策记录：另起炉灶决策
6. 五道门禁 + `run-gates` 入口 + 两份清单（棘轮基线 / 字数预算）
7. 五份变红物证（`docs/guard-redproof/<gate-id>-redproof.md`）
8. 初始提交（全绿基线）
