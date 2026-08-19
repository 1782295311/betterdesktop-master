# doc-budgets-redproof.md — 门禁变红物证

> 门禁：`verify-doc-budgets.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `304c760`；注入 commit = `3996a56`；撤销 commit = `77e8838`

## ① 可复现的注入片段

```
3996a56 redproof(doc-budgets): 将 AGENTS.md 灌超 900 词预算上限

 AGENTS.md | 4 ++++
 1 file changed, 4 insertions(+)

```

注入 diff（AGENTS.md 追加部分）：

```
commit 3996a567e6245b9577aaaa8367fb3a511d557e60
Author: CairoShell Architect <architect@cairoshell.local>
Date:   Thu Aug 20 01:23:28 2026 +0800

    redproof(doc-budgets): 将 AGENTS.md 灌超 900 词预算上限

diff --git a/AGENTS.md b/AGENTS.md
index 5ad5f59..8217bc2 100644
--- a/AGENTS.md
+++ b/AGENTS.md
@@ -43,3 +43,7 @@ host/           薄宿主 exe（P2 起）
 
 - 根命名空间 `BetterDesktop.*`；包目录 `packages/<域>/<名>`（如 `packages/kernel/kernel`）。
 - 术语表 `docs/TERMINOLOGY.md` 将在 P1 前落地，成为命名的唯一真相源。
+
+## 超限填充
+这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。这是字数预算门禁的超限填充文本，用于故意把文档推过预算上限以验证门禁真的会变红。
+

```

违规构造：向受预算约束的 `AGENTS.md`（上限 900 词）追加约 300 词填充文本，使总词数越过上限。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] doc-budgets — AGENTS.md — 1053 词超过 900 词上限 — 请精简，或按 AGENTS.md 在决策记录中说明理由后调整上限

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 3996a56`，撤销 commit = `77e8838`。撤销后重跑（退出码 0）：

```
[PASS] doc-budgets — 预算清单 4 个文档全部在限内

```

**结论：该门禁红/绿双向验证通过。**
