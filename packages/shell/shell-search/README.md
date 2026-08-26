# BetterDesktop.Shell.Search

> 角色：`shell.search` — 开始菜单搜索（程序 / 设置 / 文件）

## 职责

- 聚合多类搜索源（Provider）为统一的开始菜单搜索入口：程序（子序列模糊匹配）、设置（ms-settings 硬编码清单）、文件（Windows Search 索引）。
- 提供 `IStartMenuSearchService.SearchAsync(query, ct)`：汇总所有 Provider 结果，按得分降序 + 类别/标题稳定排序，截断返回。
- Provider 是扩展点：任何模块 `RegisterProvider` 即可参与搜索（同名单覆盖）。

## 依赖

- `BetterDesktop.Kernel`（IContext / IPlugin / IKernelLogger）
- `BetterDesktop.Shell.AppSource`（IAppSourceService / AppItem，程序语料）

## 对外扩展点

- `ISearchResultProvider`：第三方可注册自定义搜索源（`Name` + `Search(query, ct)`）。
- `SearchResult`：统一结果模型（Title / Subtitle / Category / AppItem / LaunchPath / IconPath / Score / Execute）。
- 消费方（Step 7 开始菜单）按 `Category`（App / Settings / File）分组展示，程序类用 `AppItem` 启动/固定，设置与文件类用 `LaunchPath`（`Execute` 已内置打开动作）。

## Known Limitations

- 程序语料 = `ScanStartMenu()` + `ScanInstalledApps()`（app-source 带 2min 缓存）；**不含** `ScanAllPrograms()`（磁盘深度遍历无缓存，逐键调用会卡输入）。
- 文件搜索依赖 Windows Search 索引（COM：SearchManager → SystemIndex → ADODB）；服务禁用 / 未索引时**降级为空**（不崩溃）。AQS 查询为「文件名子串 OR 全文」。
- `SearchAsync` 同步执行各 Provider；输入防抖由 UI 层负责。
- 设置清单为硬编码约 80 项 canonical ms-settings URI；个别 Win11 版本 URI 可能失效（打开失败静默，不影响搜索本身）。
