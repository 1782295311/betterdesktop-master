# package-readme-redproof.md — 门禁变红物证

> 门禁：erify-package-readme.ps1 · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物，不涉及 dotnet build / --no-build / 拷贝产物）
> 取证环境 E4：注入前 HEAD = `2a23e47`；注入 commit = `df047a6`；撤销 commit = `24eaf12`（git revert --no-edit）

## ① 可复现的注入片段

```
df047a6 redproof(package-readme): 注入无 README 的探针工程

 packages/probe/Probe.csproj | 1 +
 1 file changed, 1 insertion(+)

```

注入文件 packages/probe/Probe.csproj 完整内容：

```
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>

```

违规构造：packages/ 下存在 csproj，但同目录无 README.md。

## ② 原样拷贝的失败输出

门禁单跑（退出码 1）：

```
[FAIL] package-readme — packages\probe\Probe.csproj — 缺少 README.md

```

统一入口 un-gates -Filter package-readme（退出码 1）：

```
[FAIL] package-readme — packages\probe\Probe.csproj — 缺少 README.md
[FAIL] package-readme — exit 1（486 ms）

门禁失败: package-readme

```

## ③ 撤销证明与恢复验证

撤销方式：git revert --no-edit df047a6，撤销 commit = `24eaf12`。
撤销后重跑门禁（退出码 0）：

```
[PASS] package-readme — 包 0 个，缺失 README 0 个，白名单 0 条，违规 0 条

```

**结论：该门禁红/绿双向验证通过。**
