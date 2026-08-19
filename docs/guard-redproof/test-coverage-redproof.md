# test-coverage-redproof.md — 门禁变红物证

> 门禁：`verify-test-coverage.ps1` · 取证日期：2026-08-20 · 取证方式：真实 git 注入提交 + 撤销提交（仅暂存基线文件，符合门禁契约第 8 条）
> 取证环境 E1：**N/A 部分适用**（门禁自身执行 dotnet test 全量构建；两次运行间隔内无代码变更）；E2/E3：同一次 --no-build 收集，无拷贝产物；E4：注入前 HEAD = `69617b6`；注入 commit = `0c29003`；撤销 commit = `1647795`

## ① 可复现的注入片段

```
0c29003 redproof(test-coverage): 将内核基线抬到实际覆盖率之上

 scripts/manifests/coverage-ratchet.baseline.json | 3 ++-
 1 file changed, 2 insertions(+), 1 deletion(-)

```

注入 diff：

```
commit 0c290036d2ee26174d25d28a8428a5a942241bfb
Author: CairoShell Architect <architect@cairoshell.local>
Date:   Thu Aug 20 02:51:42 2026 +0800

    redproof(test-coverage): 将内核基线抬到实际覆盖率之上

diff --git a/scripts/manifests/coverage-ratchet.baseline.json b/scripts/manifests/coverage-ratchet.baseline.json
index 70be092..4417888 100644
--- a/scripts/manifests/coverage-ratchet.baseline.json
+++ b/scripts/manifests/coverage-ratchet.baseline.json
@@ -3,8 +3,9 @@
   "note": "覆盖棘轮：各生产程序集最低行覆盖率，只能升不能降（LOCKED-COUNT 与条目数逐值相等）。基线于 P1 首增实测后锁定。",
   "LOCKED-COUNT": 3,
   "baselines": {
-    "BetterDesktop.Kernel": 0.80,
+    "BetterDesktop.Kernel": 0.95,
     "BetterDesktop.Kernel.Loader": 0.90,
     "BetterDesktop.Kernel.Timer": 0.95
   }
 }
+

```

违规构造：把 `BetterDesktop.Kernel` 的棘轮基线从 0.80 抬到 0.95（高于实测 83%），模拟「放宽覆盖要求」的作弊。

## ② 原样拷贝的失败输出（退出码 1）

```
[FAIL] test-coverage — BetterDesktop.Kernel — 行覆盖率 82.89% 低于基线 95.00%（棘轮只升不降）

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 0c29003`，撤销 commit = `1647795`。撤销后重跑（退出码 0）：

```
[PASS] test-coverage — 覆盖棘轮全过：BetterDesktop.Kernel=83% BetterDesktop.Kernel.Loader=91% BetterDesktop.Kernel.Timer=98%

```

**结论：该门禁红/绿双向验证通过。**
