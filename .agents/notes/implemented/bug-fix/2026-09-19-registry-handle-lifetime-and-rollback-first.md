# Agent Note: 注册表句柄生命周期与「回滚先于自动写」的排序纪律

Status: implemented

## Problem

S4-2 第 2 步要给 core 加「检测到右键扩展注册漂移就自动触发修复」。这类代码首次**自动写** `HKCU\Software\Classes`，因此先补了「备份 / 回滚」能力（`--shellmenu-backup` / `--shellmenu-restore`）。回滚里的 `DeleteTree` 写成：

```csharp
using var parentKey = Registry.CurrentUser.OpenSubKey(parent, writable: true);
if (parentKey?.OpenSubKey(leaf) is null) { return false; }   // ← 探测句柄未释放
parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
```

`OpenSubKey(leaf)` 的返回值只用于 `is null` 判断、**从未释放**。句柄还开着就删掉那个键，该键进入「标记为删除」状态——随后同一路径的「删掉再重建」抛「试图在标记为删除的注册表项上进行不合法的操作」。何时真正释放取决于 **GC 何时回收那个句柄**，故失败是**间歇性**的：同一段代码有时通过、有时报错。

后果不是「少还原一个键」：备份/回滚的 5 个单测都用「开头拍照、`finally` 回滚」兜底，而一次失败的 `finally` 在回滚里抛了——**把开发机的注册表留在「扩展未注册」状态**。当时 `shellmenu` 巡检还是只读的，**没有任何东西会把它修回来**；直到下一轮 `--shellmenu-backup` 打印 `keysPresent=0 registered=False` 才被发现。

## Decision

1. **修句柄生命周期**：探测存在性用 `using` 当场释放；删除改用**完整路径**直接 `DeleteSubKeyTree`，不复用探测阶段的任何句柄——探测与删除是两次独立的句柄生命周期。
2. **确立排序纪律**：**「备份 / 回滚」这类改系统状态的能力，必须先于任何自动写系统状态的代码落地。** 本次是**测试自己**踩响的闸门；若换成自动自愈去写，同样的缺陷会落在用户机器上，而且没有任何回滚手段。
3. **规则未写进 `docs/defensive-patterns.md`**：该文档已顶在 1200 词预算上（`doc-budgets` 拦下），故规则落在本记录 + 计划 §13.14，并由计划承担「下次巡检复核」的义务。

## Alternatives considered

- **保留父键句柄、用 `parentKey.DeleteSubKeyTree(leaf)`**：父键句柄本身合法（父键没被删），但风险点在**子键**句柄上；分两次独立生命周期更简单，也不依赖「哪一层句柄会被 GC」这种运行时细节。
- **只删不探测**（直接 `DeleteSubKeyTree(throwOnMissingSubKey:false)`，放弃 `removed` 计数）：可行，但「回滚了但没有键可删」与「回滚了并删掉 N 个」就分不清——而回滚报告的计数正是人要看的。故保留探测，只修释放。
- **把规则塞进 `defensive-patterns.md` 并上调其预算**：该上限是「按常读文件」定的，为一条规则扩预算会开先例；决策记录正是「哪些路走过、为什么」的地方，且交接流程必读。
- **不做备份/回滚，直接写注册表**：本次失败就是这条路的代价实证。

## Consequences

- `DeleteTree` 探测句柄当场释放；回滚的 5 个单测可重复稳定通过（连跑两次 118/118）。
- 该能力经真机验收：改值 → 回滚 → 值回到原样；`reg delete` 到零态 → 回滚 → 6 个键全部回来。
- **「回滚先于自动写」成为后续任何自动改系统状态工作的前置条件**（S4-2 第 2 步之后还有 S7 的入口收口）。
- 顺带暴露并修掉 `verify-architecture-guard` 的 R1 盲区：其「拉起我们自己的 exe」模式表只有 C# 口径（`Process.Start` / `CreateProcessW` / `StartDetached`），**漏了 Rust 侧的 `spawn_detached` / `run_and_wait`**——core 拉起 CLI 做修复时两个标记只命中一个，整条棘轮对 Rust 侧是瞎的（现场表现是「清单条目已失效」）。已补模式 + 门禁自测。
