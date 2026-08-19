# md-wrap-redproof.md — 门禁变红物证

> 门禁：`verify-md-wrap.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交（仅暂存注入文件，符合门禁契约第 8 条）
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `671a538`；注入 commit = `caf89c0`；撤销 commit = `71fd172`

## ① 可复现的注入片段

```
caf89c0 redproof(md-wrap): 注入被手工硬换行的散文段落

 docs/cookbook/wrapped.md | 5 +++++
 1 file changed, 5 insertions(+)

```

注入文件完整内容：

```
fatal: path '.docs/cookbook/wrapped.md' does not exist in 'caf89c0'

```

违规构造：一个散文段落被拆成两条物理行（第一行以逗号结尾接续第二行），构成手工硬换行。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] md-wrap — docs\cookbook\wrapped.md:3 — 散文段落被硬换行（应一段一行物理行）

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit caf89c0`，撤销 commit = `71fd172`。撤销后重跑（退出码 0）：

```
[PASS] md-wrap — 检查 26 个 Markdown 文件，段落均为一段一行

```

**结论：该门禁红/绿双向验证通过。**
