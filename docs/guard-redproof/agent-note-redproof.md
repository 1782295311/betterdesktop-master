# agent-note-redproof.md — 门禁变红物证

> 门禁：`verify-agent-note.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 门禁版本：含类别目录存在性检查（2026-08-19 强化版）
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `f79f555`；注入 commit = `dcef433`；撤销 commit = `1c2ce40`

## ① 可复现的注入片段

```
dcef433 redproof(agent-note): 注入 Status 非法的决策记录

 .agents/notes/implemented/process/2026-01-01-bad-note.md | 15 +++++++++++++++
 1 file changed, 15 insertions(+)

```

注入文件完整内容：

```
# Agent Note: 坏记录示例

Status: done

## Problem
无

## Decision
无

## Alternatives considered
无

## Consequences
无

```

违规构造：文件位于合法的 `implemented/process/` 目录且文件名合法，但第 3 行 `Status: done` 不在封闭集合 {proposed, implemented, rejected} 内。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] agent-note — .agents\notes\implemented\process\2026-01-01-bad-note.md :3 — 第三行须为 `Status: <proposed|implemented|rejected>`

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit dcef433`，撤销 commit = `1c2ce40`。撤销后重跑（退出码 0）：

```
[PASS] agent-note — 活跃决策记录 2 篇，格式全部合规

```

**结论：该门禁红/绿双向验证通过。**
