# dotnet-format-redproof.md — 门禁变红物证

> 门禁：`verify-dotnet-format.ps1` · 取证日期：2026-08-20 · 取证方式：真实 git 注入提交 + 撤销提交（仅暂存注入文件，符合门禁契约第 8 条）
> 取证环境 E1–E3：**N/A**（格式门禁不产出构建产物；红跑基于同一次工作区状态）；E4：注入前 HEAD = `903def1`；注入 commit = `6e8b76a`；撤销 commit = `78f4a1c`

## ① 可复现的注入片段

```
6e8b76a redproof(dotnet-format): 注入缩进与大括号风格违规文件

 packages/kernel/kernel/Core/FormatViolation.cs | 9 +++++++++
 1 file changed, 9 insertions(+)

```

注入文件完整内容：

```
fatal: path '.packages/kernel/kernel/Core/FormatViolation.cs' does not exist in '6e8b76a'

```

违规构造：2 空格缩进 + K&R 大括号，违反根 `.editorconfig`（4 空格 + Allman）。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] dotnet-format — 格式与 .editorconfig 不一致（dotnet format --verify-no-changes 退出码 2）C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\FormatViolation.cs(4,3): error WHITESPACE: 修复空格格式。 插入“\s\s”。 [C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\BetterDesktop.Kernel.csproj] C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\FormatViolation.cs(5,3): error WHITESPACE: 修复空格格式。 插入“\s\s”。 [C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\BetterDesktop.Kernel.csproj] C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\FormatViolation.cs(6,5): error WHITESPACE: 修复空格格式。 插入“\s\s\s\s”。 [C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\BetterDesktop.Kernel.csproj] C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\FormatViolation.cs(7,3): error WHITESPACE: 修复空格格式。 插入“\s\s”。 [C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\BetterDesktop.Kernel.csproj] C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\Core\FormatViolation.cs(9,1): error ENDOFLINE: 修复行尾标记。 将 2 字符替换为 '\n'。 [C:\Users\17822\Desktop\betterdt\better-desktop-cordis\packages\kernel\kernel\BetterDesktop.Kernel.csproj]

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 6e8b76a`，撤销 commit = `78f4a1c`。撤销后重跑（退出码 0）：

```
[PASS] dotnet-format — 代码格式与 .editorconfig 一致

```

**结论：该门禁红/绿双向验证通过。**
