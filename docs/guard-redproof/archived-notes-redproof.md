# archived-notes-redproof.md — 门禁变红物证

> 门禁：`verify-archived-notes.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `babfb3c`；注入 commit = `3fd88ce`；撤销 commit = `6ec0f9e`

## 取证前情（本次取证抓出的真实缺陷）

本门禁首次取证时，撤销注入后绿跑意外变红，报「`.agents/notes/archived/process — 缺少类别目录`」。根因：**git 不跟踪空目录**，注入撤销后类别目录随之消失，而基线提交从未包含空目录。这证明该门禁能拦截「新鲜克隆下类别目录缺失」的真实缺陷。修复：为全部空类别目录补 `.gitkeep` 并提交（commit `8743056` 等）。本物证为修复后的重取。

## ① 可复现的注入片段

```
3fd88ce redproof(archived-notes): 注入未登记 manifest 的孤儿归档记录

 .agents/notes/archived/process/2026-01-01-orphan.md | 16 ++++++++++++++++
 1 file changed, 16 insertions(+)

```

注入文件完整内容：

```
# Agent Note: 未密封的孤儿归档记录

Status: implemented

## Problem
无

## Decision
无

## Alternatives considered
无

## Consequences
无


```

违规构造：归档目录下出现一个 `.md`，但未运行 `-Write` 将其 SHA-256 密封进 `manifest.json`。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] archived-notes — process/2026-01-01-orphan.md — 未登记进 manifest.json（先运行 -Write 追加密封）

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 3fd88ce`，撤销 commit = `6ec0f9e`。撤销后重跑（退出码 0）：

```
[PASS] archived-notes — 归档密封清单 0 条，全部哈希一致

```

**结论：该门禁红/绿双向验证通过。**
