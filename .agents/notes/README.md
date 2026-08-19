# 决策记录（Agent Notes）制度

> 任何非平凡设计决策必须在这里落一篇记录。没有决策记录的变更可以被打回。

## 位置与命名

- 路径：`.agents/notes/{proposed|implemented|rejected}/{class}/yyyy-mm-dd-topic.md`
- 生命周期（封闭集合）：`proposed`（提议中）/ `implemented`（已实施）/ `rejected`（已否决）
- 类别（封闭集合）：`architecture` / `feature` / `bug-fix` / `process` / `testing` / `simplification`
- **禁止 `INDEX.md`**：不设集中索引，靠目录树浏览与仓库搜索（索引必然腐烂，目录树不会）。
- 文件名必须匹配 `yyyy-mm-dd-主题.md`。

## 文件头（前四行固定）

```
# Agent Note: <标题>
（空行）
Status: <生命周期>[ — 补充说明]
（空行）
```

## 各生命周期的必需小节（由门禁 `verify-agent-note` 强制）

- `proposed`：`## Problem` / `## Proposal` / `## Alternatives considered` / `## Acceptance criteria` / `## Risks`
- `implemented`：`## Problem` / `## Decision` / `## Alternatives considered` / `## Consequences`（**禁止**残留 `## Proposal`、`## Plan` 等提议期标题）
- `rejected`：`## Problem` / `## Proposal` / `## Alternatives considered`

## 与 ADR 的分工

- 影响冻结项（TFM / 分层 / 资产边界）、机制替换、内核 API 方向 → 开 ADR（`docs/architecture/ADR-NNN.md`）。
- 其余非平凡设计决策 → 本目录 Agent Note。
- 拿不准时先写 Agent Note，经评审 / 主理人判定需要升格时再升格为 ADR。

## 归档

- 不再指导未来工作的记录 → 移到 `.agents/notes/archived/` 对应类别目录。
- 归档 = 冻结：`scripts/verify-archived-notes.ps1 -Write` 把文件 SHA-256 密封进 `manifest.json`；之后**不可修改、不可删除**，只可追加新条目。
- 归档规则详见 `.agents/notes/archived/AGENTS.md`。
