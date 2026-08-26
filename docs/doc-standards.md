# doc-standards.md — 文档治理标准

> 地位：全仓文档的分层（tier）、写作规则与反模式清单的唯一真相源。与根 `AGENTS.md` 文档纪律互补——那里列「要做什么」，这里讲「事实放哪个家、怎么写、别写什么」。
> 来源：`参考/deepseek-harness-master/docs/AGENTS.md`，已适配当前项目（无双语 i18n 契约部分）。

## 一、tier taxonomy：一个事实一个家

每个事实只有一个家——职责归它所属的层级；其他地方一律链接过去，不复制、不复述。

| 层级 | 职责（事实的家） | 不属于这里 |
|---|---|---|
| 根 `AGENTS.md` | 全仓常备命令：文档/门禁/变更/命名纪律，每条一至三行并链接归属 | 故事、示例、情景化流程、从归属文档复述的内容 |
| `docs/architecture.md` | 架构总览：插件化整体、分层、扩展点、内核 API 方向 | 类型定义（→ MECHANISMS/分域规范）、单包细节（→ 包 README） |
| `docs/MECHANISMS.md` | 机制唯一化注册表（一个关注点一套机制 + 禁止清单 + 护栏） | 每域「怎么做」（→ 对应分域规范） |
| `docs/architecture/ADR-*.md` | 冻结决策：TFM / 分层 / 资产边界 / 机制替换 / 内核 API 方向 | 其余非冻结决策（→ Agent Note） |
| `docs/*.md` 分域规范 | 单域编写规则：testing / security / runtime-health 等 | 跨域通用铁律（→ `engineering-conventions.md`） |
| `docs/coding-standards.md` | 结构、风格与「禁止清单」 | 通用工程铁律（→ `engineering-conventions.md`）、防御性模式（→ `defensive-patterns.md`） |
| `packages/**/README.md` | 包契约：config、语义、限制、扩展点 | JSDoc/XML doc 复述、生成目录复述、别的包的关注点 |
| `.agents/notes/` | 决策记录：why + 放弃了什么 | 迁移计划、验收清单、spec-speak（已实施记录） |
| `docs/postmortem/` | 事故故事（唯一允许 war-story 叙事的地方） | 设计决策（→ Agent Note） |
| `docs/cookbook/` | 分步 how-to，含编号的验证步骤 | 设计 rationale（→ 它链接的 Agent Note） |
| `scripts/` | 门禁（`verify-*.ps1`）与生成器 | — |
| `scripts/verify-*.Tests.ps1` | 门禁单测（Pester） | — |

归属分流：bug → postmortem；rationale → Agent Note；步骤 → cookbook；类型/机制 → MECHANISMS 或分域规范；包契约 → 包 README；常备命令 → 根 `AGENTS.md` 加 rationale 链接。

## 二、写作规则

1. **写当前状态，不写变更历史**：避免「之前 / 现在 / 不再 / 曾用名 / 已改名」以及 PR、commit、堆栈位置；直接写现行机制。变更故事放进 commit、PR、Agent Note 或 postmortem。
2. **一段一行物理行**（`verify-md-wrap` 机检）：用编辑器软换行；代码块、表格、列表保留自身格式。
3. **机器可校验的交叉引用**：仓库内引用一律用相对 Markdown 路径 + 锚点，不用裸文件名或编号（`verify-md-links` 机检）。
4. **注释与文档陈述完整契约，不写推理转录**：保留行为、失败、时序、所有权、模态、异常、后果与非显而易见的方向；删除叙述、测试走查、评审分析与代码复述。
5. **直接命名**：名命主体与事实，用「响应字段」「JSON 校验」这类具体词，不用「形状」「边界」等隐喻泛词；「契约」只留给前置/后置/不变量/兼容承诺。

## 三、slop checklist（反模式清单）

在任何文档里排查以下反模式：

1. **同一规则多处重复**：用独特短语 grep，只保留一个家，其余链接。
2. **叙述历史 / war story**：「之前」「现在」「不再」「曾用名」「已改名」，PR 或 commit。改为当前事实，链接 Agent Note 或 postmortem。
3. **实现状态标注**（「已实现!」「未来: …」）：状态会腐烂，仓库布局与 manifest 已承载它。
4. **手抄目录 / JSDoc / 清单**：测试、包、状态的清单在 source 或生成器是权威时，不手抄。
5. **推理转录**：一步步实现叙述、显而易见分支的证明、测试走查、被否的局部备选方案。保留最终契约或持久 rationale，删除推导路径。
6. **段落墙**：一段塞多条规则 + 括号补充，拆段或把细节下放到其归属。
7. **强调通胀**：到处加粗 / 大写 / 「关键」，等于什么都没强调；强调留给真正改变行为的那个从句。
8. **spec-speak 出现在已实施记录里**：「应该」、迁移计划、验收清单。已实施的 Agent Note 只写「是什么」。

## 四、字数预算

关键文档字数上限由 `scripts/manifests/doc-budgets.manifest.json` 设定（`verify-doc-budgets` 机检）。超限流程：先**迁移**到所属层级 → 再**精简** → 最后才**放宽**上限并在决策记录说明理由。上限是护栏，不是缩减目标；低于上限时保留至少 5% 余量。