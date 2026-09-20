# Cairo 开发计划 · 文件索引增加完整路径匹配（中文路径可搜）

> Task: 让 `engine-index` 的 `search_files` 支持按**完整路径**（含中文目录段）匹配，恢复菜单栏搜索对「中文路径」的查找能力。
> 证据基于当前工作树（本仓 master 尚无 commit，行号钉当前源码）；技术力文档命中：`02-搜索/201`、`02-搜索/203`、`12-搜索匹配/1201`；「文件系统索引」未命中（沿用 `2026-09-13-native-index-service-rust.md` §4 检索记录：USN/MFT/Everything SDK/file index 均无现成文档）。

## 1. Objective

用户在菜单栏搜索框输入**中文目录名 / 路径片段**（如「迅雷下载」「自动化脚本」）时，能命中该目录下的文件；输入中文文件名仍命中；结果排序上「文件名命中」严格优先于「纯路径命中」。场景语言：**搜目录名能找到目录里的文件**。

## 2. Current Behaviour

- 引擎 `search_files`（`engine-index/src/fileindex.rs::search` :110-153）只对 `FileEntry.name_lower`（= `path.file_name()` 小写，:253-256）做 `contains`；**完整路径从不参与匹配**。
- 真机管道探针（2026-09-17，运行中引擎 PID 53768，协议 `BDIX1|` + JSON-RPC）：`query=网易` → `count 1`（`C:\Users\17822\Desktop\自动化脚本\网易云音乐.lnk`）；`query=迅雷下载` → `count 0`；`query=自动化脚本` → `count 0`。**中文文件名匹配正常，中文路径段全部落空** [verified]。
- 原 C# 兜底扫描（`backups/rollback-20260909/.../FileSearchProvider.cs:204`，`Directory.EnumerateFiles(root, "*q*")`）同样只匹配文件名（.NET 行为已实测：`*迅雷下载*` 不命中其下文件）[verified]；原版唯一能命中路径的是 Windows Search AQS 裸词全文分支（同文件 :154 `OR "query"`），依赖系统索引、不覆盖未索引目录，且现引擎路径仍保留该补充来源（当前 `FileSearchProvider.cs:59` `CollectWindowsSearch`）[verified]。

**结论**：引擎立项动机（覆盖 `D:\迅雷下载` 这类未索引中文目录）只复刻了「文件名」匹配，**路径段识别从未实现**——这正是用户感知的「重写后中文路径搜不到」。

## 3. Findings

- `[verified]` `FileEntry` 仅有 `name_lower`（`fileindex.rs:29-35`），`collect_files` 在 :253-256 只存文件名小写。
- `[verified]` 引擎返回 `FileHit { path, name, size, modified }`（:126-135），C# 侧本就持有 `path`，可参与排序，无需改协议。
- `[verified]` `FileSearchProvider.ScoreFileName(fileName, query)` 只按文件名打分（:261-264），三处调用点（引擎 :117 / WS :190 / 兜底 :215）均持有 path，签名可扩展。
- `[verified]` `ScoreFileName` 无既有单测（`shell-search-tests` 仅 `ProgramSearchProviderTests.cs`，grep `ScoreFileName|FileSearchProvider` 无命中）。
- `[verified]` `engine.rs:331-337` `search_files` 返回契约 `files/count/building/degraded/degradeReason`——路径匹配是**纯内部查询语义变化**，协议零改动。
- `[verified]` 内存预算：引擎 `status.rssBytes ≈ 40MB`（40169472）；`2026-09-13` 计划 §12 假设 100MB 内。76k 条目 × `path_lower`（~80B/条）≈ **+6MB**，在预算内。

## 4. Proposed Changes

**A. `engine-index/src/fileindex.rs`**
- `FileEntry` 新增 `path_lower: String`（完整路径小写，构建时预存——与 `name_lower` 同一理由：避免查询时对 76k 条全量小写化）。
- `collect_files` 构造 `FileEntry` 时填 `path_lower`（:261-266）。
- `search()` 匹配改两段：`name_lower.contains(needle) || path_lower.contains(needle)`；桶序改三段：**name-prefix**（name `starts_with`）→ **name-contains** → **path-contains**（name 未中但 path 中），依次合并后 `truncate(limit)`——保住「截断不丢最相关候选」纪律（name 命中永远先于纯路径命中）。空查询 / `limit=0` 行为不变。

**B. `packages/shell/shell-search/Services/FileSearchProvider.cs`**
- `ScoreFileName(fileName, query)` → `ScoreFileName(fileName, path, query)`：
  - name `StartsWith` → **55**（不变）；
  - name `Contains` → **30**（不变）；
  - name 不含但 path `Contains` → **20**（新增档，低于一切文件名命中）；
  - 其余 → **30**（WS 全文命中保持原量纲，避免内容命中降级）。
- 三处调用点传 path。

**C. 测试**
- `fileindex.rs` tests 新增：中文目录路径段命中（`C:/x/迅雷下载/完蛋.txt` 查「迅雷下载」）；path 大小写不敏感；path-only 落第三桶（name 命中条目在前）；name+path 同中时 name 优先。
- `shell-search-tests` 新增 `ScoreFileName` 分档小测（55/30/20 分档；新文件 `FileSearchProviderScoreTests.cs`）。

## 5. Implementation Sequence

1. `fileindex.rs`（`FileEntry` + `collect_files` + `search` + tests）→ `cargo test` 全绿。
2. `FileSearchProvider.cs`（`ScoreFileName` 签名 + 3 调用点 + 新测试）→ `dotnet build` / `dotnet test` 全绿。
3. 真机走查：`scripts/deploy-index.ps1` 重建部署 → 管道探针复测（见 DoD）。

## 6. Test Strategy

- `cargo test`（cwd=`engine-index`，`& "$env:USERPROFILE\.cargo\bin\cargo.exe"`）：新增路径匹配 ~5 例；既有 46+ 例全绿。
- `dotnet test`（`shell-search-tests`）：新增分档用例；既有 9 例全绿。
- 构建：`dotnet build BetterDesktop.slnx -c Debug` **0 警告 0 错误**；`cargo build --release` 0 警告。

## 7. Impact & Files

| File | Symbols | Reason |
|---|---|---|
| `engine-index/src/fileindex.rs` | `FileEntry` / `collect_files` / `search` | 核心改动 |
| `packages/shell/shell-search/Services/FileSearchProvider.cs` | `ScoreFileName` + 3 调用点 | 排序分档 |
| `packages/shell/shell-search-tests/FileSearchProviderScoreTests.cs` | 新文件 | 分档测试 |

影响：`search_files` 协议、`SearchResult` 契约、`shell-index-ipc`、Windows Search / 兜底路径行为**均不变**（排序函数共用但输入语义不变）。

## 8. Assumptions & Open Questions

- `[assumed]` `path_lower` 内存增量在预算内 → 真机走查时以 `status.rssBytes` 复核。
- deferred：TECH-KNOWLEDGE「文件系统索引」新文档仍归 `2026-09-13` 计划 §13 D8（M5 文档回写），不并入本改动。
- deferred：拼音/首字母搜索（`12-搜索匹配/1201`）不属本改动（`no-trigger`：未要求）。

## 9. Definition of Done

- **D1** `cargo test` + `cargo build --release`（`engine-index`）全绿 0 警告。
- **D2** `dotnet build BetterDesktop.slnx -c Debug` 0 警告 0 错误；`dotnet test`（含新 `FileSearchProviderScoreTests`）全绿。
- **D3 场景走查（真机）**：deploy 后管道探针 `query=迅雷下载` 与 `自动化脚本` 均返回其下文件（`count>0`）；`query=网易` 仍命中；空查询仍空集。
- **D4 排序**：探针返回中「文件名含 query」的条目先于「纯路径命中」条目。

## 10. Handoff to 技术力应用

| 项 | 内容 |
|---|---|
| **模式判定** | 成熟工程增量：无匹配库文档（「文件系统索引」未命中）→ **工程代码权威**；约束注入 `02-搜索/203`（排名留 C#，本改动遵守：引擎不排序只出候选）与 `02-搜索/201`（查询取消纪律，不新增取消点） |
| **注入清单** | 代码侧（工程权威，逐份读全）：`engine-index/src/fileindex.rs` 全文；`packages/shell/shell-search/Services/FileSearchProvider.cs` 全文；`engine-index/src/model.rs`（`FileHit` 契约）；`engine-index/src/engine.rs:300-339`（`search_files` 契约，改动不得触碰）。技术力文档：`TECH-KNOWLEDGE/02-搜索/203-搜索排名算法.md`（排序量纲基准） |
| **适配参数** | Rust：`FileEntry` 增 `path_lower: String`；`search()` 三段桶合并；C#：`ScoreFileName` 增 path 参数（55/30/20/30）；`shell-search-tests` 新文件 `FileSearchProviderScoreTests.cs`（命名空间 `BetterDesktop.Shell.Search.Tests`，xUnit，风格对齐 `ProgramSearchProviderTests.cs`） |
| **禁区** | 不改 `search_files` 返回契约与 `building/degraded` 语义；不动 IPC/`IndexIpcProtocol`；不动 `ProgramSearchProvider` / 拼音逻辑；不引入新依赖；不新增排序逻辑第二份实现（排名仍在 C#） |
| **DoD 核销表** | D1（cargo test/build 命令 + 0 警告）；D2（dotnet build/test 命令 + 0 警告）；D3（deploy-index.ps1 + 管道探针 JSON 结果，`迅雷下载`/`自动化脚本` count>0、`网易` 命中、空查询空集）；D4（探针返回顺序抽查：name 命中在前） |
