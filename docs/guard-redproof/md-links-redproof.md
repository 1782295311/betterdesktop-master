# md-links-redproof.md — 门禁变红物证

> 门禁：`verify-md-links.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `e55d2ae`；注入 commit = `233d325`；撤销 commit = `d0e5252`

## ① 可复现的注入片段

```
233d325 redproof(md-links): 注入指向不存在文件的死链

 docs/cookbook/broken-link.md | 4 ++++
 1 file changed, 4 insertions(+)

```

注入文件完整内容：

```
fatal: path '.docs/cookbook/broken-link.md' does not exist in '233d325'

```

违规构造：`docs/cookbook/broken-link.md` 中的相对链接指向不存在的 `does-not-exist.md`。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] md-links — docs\cookbook\broken-link.md:3 does-not-exist.md — 目标文件不存在

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 233d325`，撤销 commit = `d0e5252`。撤销后重跑（退出码 0）：

```
[PASS] md-links — 检查 15 个 Markdown 文件，链接与锚点全部有效

```

**结论：该门禁红/绿双向验证通过。**
