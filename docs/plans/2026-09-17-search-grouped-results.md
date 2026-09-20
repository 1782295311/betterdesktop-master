# 搜索展示改造：全量返回 + 分类排序 + 程序优先 + 用户筛选

> Task: 搜索面板从「全局 Score 降序 + Take(30) 截断」改为「引擎/Provider 全量返回 → 分类排序（程序最前、结果少类别放前）→ 用户主动筛选（类别 Tab + 组折叠展开）」。
> 证据基于当前工作区（2026-09-17）；技术力文档命中：203-search-rank（消费方语义需同步）、206-file-index-path-match（引擎 limit 语义同步）。

## 1. Objective

用户明确方向（MAA 反例修复后的新诉求）：
1. 搜索结果**按分类展示**（应用 / 设置 / 文件分组标题）。
2. **程序优先级提到最前**：App 组永远第一组，不因全局 Score 被文件/设置压下去。
3. **结果少的类别放前**：App 之外，设置 / 文件两组按各自命中数**升序**（少的在前）。
4. **不用桶**：引擎、各 Provider、聚合层都不做「概率截断 / 贪心取前 N」——**所有适配结果都返回**。
5. **给筛选**：UI 提供类别筛选（Tab 切换）与组内展开/折叠，**由用户主动减少显示结果**，而不是引擎替他丢。

用户可感知结果：搜「迅雷」时——应用组（若有命中）永远在最上；设置/文件按命中数排序；底部筛选条可一键只看「文件」；每类默认折叠为前 8 条、可展开全部；不再出现「明明有 D 盘命中却被前 100 条 C 盘结果挤掉」的情况。

## 2. Current Behaviour（现状，架构折叠）

- 引擎 `fileindex.rs search()`：已改「三轮桶全量收集，limit 只截返回体积」（上一轮 MAA 修复，71/71 通过）。
- `FileSearchProvider`（shell-search）：`MaxResults=20`；引擎路径 limit = `MaxResults*5` = **100**（:95）；Windows Search 上限 20（:175）；兜底扫描上限 20（:234/:304/:320）。→ 三处都是「桶」。
- `ProgramSearchProvider`：子序列打分 + **Take(20)**（:末尾）→ 桶。
- `StartMenuSearchService.SearchAsync`：**`OrderByDescending(Score).ThenBy(Category).ThenBy(Title).Take(30)`** → 全局 Score 排序 + 30 条截断。搜 `MAA` 时强匹配文件被无关应用组压下的问题源头（当前 SearchPopupWindow 注释亦自述）。
- `SearchPopupWindow.RenderResults`（:210）：按聚合层已排序结果平铺，类别切换处插分组标题；无筛选、无折叠、无计数。
- 备份（rollback-20260909）的 StartMenuSearchService 与 SearchPopupWindow 与当前逐处一致——用户点名的机制（分类/程序优先/少类别前置）在本仓库当前与 09-09 备份均不存在，属**新设计**（参照 Cairo/Open-Shell 分类思想 + 用户明确机制）。

## 3. Relevant Architecture

- 数据流：Provider（programs/files/settings）→ `IStartMenuSearchService.SearchAsync` 聚合 → `SearchPopupWindow.RenderResults`。
- 类别契约：`SearchResult.Category` ∈ App / Settings / File（SearchResult.cs 注释「消费方按 Category 分组展示」——分组本就是设计意图，当前实现没做全）。
- 打分量纲：`ProgramSearchProvider.Score`（前缀+50/整词+40/子序列）与 `FileSearchProvider.ScoreFileName`（55/30/20/30）同基准；Settings 静态列表打分。
- 技术库：203「分类权重 App > 设置 > 文件 + Top N 截断」——本次改动把「Top N 概率截断」升级为「全量 + 用户筛选」，203 红线需同步；206 引擎契约「limit 只截返回体积」不变，C# 传参从 100 → 1000。

## 4. Technical-Knowledge Findings

- 命中 203-search-rank（L2）：分类权重 + 匹配系数 + 频率排序、Top N 渲染上限防卡顿。本次把「Top N 截断」从**聚合层强制**改为 **UI 层展示控制（默认折叠 + 筛选）**，203 §5/§9 需同步语义（红线改为「聚合层不截断，UI 层分组折叠 + 筛选，保留安全上限防渲染爆炸」）。
- 命中 206-file-index-path-match（L2）：引擎三轮桶全量收集 + limit 截返回体积；C# 调用点 117 传 limit=MaxResults*5。本次引擎不改，C# 传参提为 1000 并注释「UI 分组折叠消费全量」。
- 无「搜索结果分组展示 + 筛选」现成文档 → 本次改动核心逻辑（组序 + 计数 + 折叠 + 筛选）为新增能力，落在 203 消费方侧。

## 5. Constraint Findings

- 203 红线「无上限渲染全部结果——大数据量卡顿」**仍有效**：全量返回 ≠ 全量渲染。UI 必须分组折叠 + 默认每类前 8 条 + 展开入口。
- 206 红线「limit 只截返回体积、不参与候选选择」**保持**：引擎不动。
- M10 纪律：Provider 异常隔离、搜索失败降级——本次改动不触碰。
- 接口契约 `IStartMenuSearchService.SearchAsync` 返回类型不变（`IReadOnlyList<SearchResult>`）——**聚合语义变化（排序+不截断）是行为变更非契约变更**，消费方（SearchPopupWindow、StartMenuWindow 各布局）都只消费列表，兼容。

## 6. Proposed Changes

| 文件 | 符号 | 改动 |
|---|---|---|
| `packages/shell/shell-search/Services/FileSearchProvider.cs` | `MaxResults`、引擎 limit | `MaxResults 20 → 100`；引擎 limit 改显式常量 `EnginePageLimit = 1000`（`TrySearchFilesAsync(query, EnginePageLimit, ct)`）；更新 :93 注释「UI 分组折叠消费全量，limit 只防 IPC 体积」 |
| `packages/shell/shell-search/Services/ProgramSearchProvider.cs` | `Search()` 尾部 | 去掉 `.Take(20)` → 全量返回（排序保留） |
| `packages/shell/shell-search/Services/StartMenuSearchService.cs` | `SearchAsync` 排序段 | `.Take(30)` 删除；排序改 `GroupRank(category, count)`：App=(0,0,0) 固定第一，其余 `(1, count, Settings?0:1)`（结果数升序、同数 Settings 前）；`OrderBy(GroupRank).SelectMany(组内 Score 降序 → Title 升序)` |
| `packages/shell/shell-menu-bar/Windows/SearchPopupWindow.cs` | `RenderResults` / 新增 `_activeCategoryFilter`、`_expandedCategories`、筛选条构建、`CreateFilterBar` / `RenderCategory` | ①搜索框下加筛选条（全部/应用/设置/文件 + 计数，点击切换，选中高亮）；②按组渲染：组标题 = 名称 + (计数)，标题可点击展开/折叠；③组内默认前 8 条，尾部「展开全部 N 条」行；④空结果态保持 |
| `packages/shell/shell-search-tests/StartMenuSearchServiceGroupingTests.cs` | 新建 | 组序/组内序/全量不截断/空查询 4 类用例（Fake provider） |
| `packages/shell/shell-search-tests/ProgramSearchProviderTests.cs` | 补 1 用例 | >20 命中不截断回归 |

## 7. Implementation Sequence

1. `FileSearchProvider`：MaxResults/EnginePageLimit 常量与调用点（独立、可先行验证）。
2. `ProgramSearchProvider`：去 Take(20)。
3. `StartMenuSearchService`：组序排序 + 去 Take(30)（核心逻辑，配新测试）。
4. `SearchPopupWindow`：筛选条 + 分组渲染 + 折叠展开（依赖 3 的顺序语义）。
5. 测试补全 → `dotnet build shell-search` + `dotnet test shell-search-tests`。
6. 真机验证（引擎探针 + UI 走查）。
7. 文档：203 红线同步 + 206 limit 注释同步 + 索引。

## 8. Test Strategy

- 新增 `StartMenuSearchServiceGroupingTests`：App 固定第一（App 10 条 vs File 5 条 → App 仍在前）；File 少 → File 组在 Settings 前；组内 Score 降序；40 条输入不截断（全量）；空查询空结果。
- `ProgramSearchProviderTests` 补全量回归（mock IAppSourceService 出 25 个命中 → 返回 25）。
- `FileSearchProviderScoreTests` 不动（打分纯函数）。
- 验证命令：`dotnet build packages/shell/shell-search/BetterDesktop.Shell.Search.csproj -c Debug`、`dotnet test packages/shell/shell-search-tests/BetterDesktop.Shell.Search.Tests.csproj`。
- 端到端：真机 UI 走查（D3）。

## 9. Risk and Impact Analysis

- **消费方**：`SearchPopupWindow`（菜单栏）+ `StartMenuWindow` 各布局（开始菜单）都消费聚合列表——排序语义变化影响两者展示顺序；两者都按「全局有序」渲染，改为「组序」后仍正确渲染（组标题位置变化是预期行为）。开始菜单布局（Classic/Win10/Win7 等）不新增筛选（scope 克制，用户点的是菜单栏搜索场景）；如用户后续要求再扩。
- **IPC 体积**：引擎 limit 100→1000，JSON 返回上限 ~200-400KB，一次性查询可接受；超长尾（>1000 命中）仍截（安全上限非概率桶，已知局限记录）。
- **渲染性能**：全量列表进 UI → 分组折叠控制（每类默认 8 条），不违反 203 红线。
- **全量构建阻塞**：testhost PID 62132 锁 shell-status-tests（MSB3021/3027）——shell-search 单项目 build/test 不受影响；全量 slnx build 需用户重启后重跑（环境遗留，不影响本改动验证）。

## 10. Files Expected to Change

见 §6 表格（6 文件 + 2 文档）。

## 11. Reusable Implementation Context

- 聚合排序参考代码（工程代码权威，非技术库）：
  ```csharp
  private static (int, int, int) GroupRank(string category, int count) =>
      category.Equals("App", StringComparison.OrdinalIgnoreCase) ? (0, 0, 0)
      : (1, count, category.Equals("Settings", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
  // groups.OrderBy(g => GroupRank(g.Key, g.Count()))
  //      .SelectMany(g => g.OrderByDescending(x => x.Score).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
  ```
- 引擎探针协议（重验 MAA 修复/全量）：NamedPipe `BetterDesktop.Index.Engine`、首帧 `BDIX1|`、`search_files` params `{query, limit}`；中文 query 用 JSON `\u` 转义；探针脚本存 `%TEMP%\bd_probe_*.ps1`。

## 12. Assumptions and Open Questions

- 「结果少的类别放前」= App 固定第一（程序优先）前提下，设置/文件按命中数升序；同数时 Settings 前（固定序保稳定）——若用户期望「全部类别含 App 都按命中数升序」，改动只在 `GroupRank` 一行，验收后按反馈微调。
- 「所有适配结果都放出来」= 各来源全量返回 + 聚合不截断；引擎 1000 为 IPC 安全上限（命中 >1000 的极端词仍截尾，非概率桶）。
- 筛选 UI 形态 = 类别 Tab（全部/应用/设置/文件 + 计数）+ 组标题点击展开/折叠 + 组内默认前 8「展开全部」——非文本框二次过滤；如需「在结果中再搜」文本框，列为后续增强。

## 13. Definition of Done

- D1 `dotnet build shell-search` 通过。
- D2 `dotnet test shell-search-tests` 全绿（新增分组 4 用例 + 程序全量回归）。
- D3 **端到端 UI 走查（真机）**：菜单栏打开搜索 → 输入「迅雷」→ 结果按组显示（App 组若有命中永远第一；设置/文件按命中数升序）；筛选条点「文件」只剩文件组；文件组 >8 条时默认折叠、点「展开全部」显示全部；MAA 场景 D 盘 `D:\迅雷下载\MAA` 命中可见（不被截断）。
- D4 **引擎探针（真机）**：搜 `MAA` limit=1000 → 返回条数 > 100（全量，C 盘 .sock 已滤、D 盘命中全出）。
- D5 文档同步：203 红线语义更新、206 limit 注释更新、双索引 0/0/0。

## 14. Handoff to 技术力应用

| 项 | 填写 |
|---|---|
| 模式判定 | 无匹配完整文档 → **工程代码权威**；203/206 作约束层参考（读其红线，不照抄 Top-N 截断语义） |
| 注入清单 | ① `TECH-KNOWLEDGE/02-搜索/203-搜索排名算法.md`（红线：Top N 语义将改，读后按 §6 更新）② `TECH-KNOWLEDGE/02-搜索/206-file-index-path-match.md`（引擎契约：limit 只截返回体积，C# 传参提 1000） |
| 适配参数 | 命名空间 `BetterDesktop.Shell.Search.Services` / `BetterDesktop.Shell.MenuBar.Windows`；聚合排序用 §11 GroupRank；UI 常量 `CollapsedGroupMax=8`；引擎 limit 常量 `EnginePageLimit=1000` |
| 禁区 | 引擎 `fileindex.rs` 不动（三轮桶全量已就绪）；`IStartMenuSearchService` 接口签名不改（行为变更非契约变更）；M10 降级路径（try-catch 吞 Provider 异常）保持 |
| DoD 核销表 | D1: `dotnet build packages/shell/shell-search/BetterDesktop.Shell.Search.csproj -c Debug`；D2: `dotnet test packages/shell/shell-search-tests/BetterDesktop.Shell.Search.Tests.csproj`；D3: 真机 UI 走查（搜「迅雷」组序/筛选/展开 + MAA D 盘可见）；D4: 探针 limit=1000 >100 条；D5: 203/206 更新 + sync-index.py 0/0/0 |
