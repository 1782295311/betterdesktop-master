# Agent Note: 文档预算调整与机制注册表同步

Status: implemented

## Problem

收尾阶段三份规范文档超出字数预算：`docs/MECHANISMS.md`、`docs/extension-rules.md`、`docs/build-release.md`。其中 MECHANISMS 仍需按变更纪律登记新增横切机制，但已无余量。同时 `extension-rules.md` 与 `build-release.md` 各有一张「机检状态」表，复述门禁清单并标注"规划机检"这类未来状态——按 `docs/doc-standards.md` 反模式第 3 条（实现状态标注会腐烂，仓库布局与 manifest 已承载）与 MECHANISMS M3（门禁注册表是唯一清单，本文不复述枚举），该表属该删的内容。

## Decision

先精简、后放宽：

- **删除两处「机检状态」表**（`extension-rules.md`、`build-release.md`）。哪些规则有机检由 `scripts/run-gates.ps1` 注册表与 MECHANISMS 登记的「关联护栏」列承载，规范文档不再复述。
- **上调三处上限**（`scripts/manifests/doc-budgets.manifest.json`）：MECHANISMS 1400→1550、extension-rules 850→1050、build-release 550→600，理由均为"新增真实契约/机制条目"：
  - MECHANISMS：登记 M17（原生与跨语言承载）与 M18（对外命令入口）两个新横切机制，属变更纪律强制项。
  - extension-rules：新增 M3.1 `contextMenus` 三路同源契约（自绘项 / 注册表 verb / CLI 路由键）与 CLI 动作分派语义。
  - build-release：新增 CLI 部署契约（发布物必须含 CLI，缺失即无宿主场景右键不可用）与内置 pandoc 引擎目录分发契约。

## Alternatives considered

- **继续压缩正文以满足原上限**：可压缩的主要是 `extension-rules.md` 的 `contextMenus` 示例代码块；但该示例是对外契约（社区自助写插件的前提）的直观载体，字段表无法完整表达形态，删掉得不偿失。
- **把「机检状态」表迁到其他文档**：门禁注册表与 MECHANISMS 登记表已是这两类事实的唯一家，迁移等于造第二份清单。
- **拆分文档以回避上限**：三份文档已按域划分（机制 / 扩展契约 / 构建发布），再拆会出现同域两个家，违反"一个事实一个家"。

## Consequences

- 三份上限提高后仍留有约 5% 以上余量；后续新增内容需在下一次全库巡检时复核，避免上限成为新的膨胀借口。
- 读者若需确认某规则是否有机检，须查 `scripts/run-gates.ps1` 注册表与 MECHANISMS 的护栏列，而非规范文档正文。
- 本批同批完成的状态同步：仓库 `README.md` 运行形态与阶段、`docs/architecture/STATUS.md`、`docs/architecture.md` 分层、决策记录补齐（4 篇架构 + 1 篇 P/Invoke 收口）、`.gitignore` 补 Rust `target/` 与 `dist/`、文档门禁（md-links / md-wrap）转绿。
