# scripts/ — 门禁开发契约

> 本目录是「双重纠错」第二层的全部实现。每个脚本都按同一契约编写，否则门禁体系会先于业务腐化。

## 门禁契约（verify-*.ps1）

1. PowerShell 7；**禁止网络访问**；必须幂等（重复跑结果一致）。
2. 退出码：0 = 通过；1 = 失败。任何非 0 退出码在 run-gates 中一律视为失败。
3. 输出格式：通过输出一行 `[PASS] <gate-id> — 摘要`；失败逐条输出 `[FAIL] <gate-id> — <位置> <原因>` 后 `exit 1`。
4. 仓库根一律通过 `lib\common.ps1` 的 `Get-RepoRoot` 获取，禁止硬编码绝对路径。
5. 每条门禁必须有变红物证（`docs/guard-redproof/<gate-id>-redproof.md`，标准见该目录 README）；门禁行为改动后必须重跑红绿双向。
6. 新增门禁 = 新增 `verify-*.ps1` + 登记进 `run-gates.ps1` 的 `$gates` 注册表，同一变更落地。
7. 豁免规则必须写进脚本注释（例：`verify-md-links` 豁免 `docs/guard-redproof/`，因为物证内含指向已撤销文件的失效引用）。
8. **变红取证的注入提交只允许包含注入文件**：用 `git add <注入文件路径>` 精确暂存，禁止 `git add -A`。否则未提交的其它改动会被打包进注入提交、随 `git revert` 一起被撤销（P0 已实际发生一次：交接增强文件全部被误撤，后从误打包提交中恢复）。取证前必须确认工作区无其它未提交改动。

## 生成器契约（gen-*.ps1，P1 起）

- 生成物必须支持 `--check` 模式：生成物与源码不一致时退出 1（保新鲜度）。
- 生成物属于仓库（提交进 git），不属构建产物。

## 清单目录

`manifests/` 存放门禁数据：`readme-ratchet.baseline.json`（README 棘轮基线）、`doc-budgets.manifest.json`（字数预算）。清单的手改规则由对应门禁的锁（LOCKED-*）强制。
