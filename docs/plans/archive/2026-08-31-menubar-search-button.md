# Cairo 开发计划 · 菜单栏新增搜索按钮

> Task: 在菜单栏右区「通知中心」与「扩展中心(+)」之间新增搜索按钮，点击弹出搜索面板（复用 shell-search 聚合搜索服务）。
> 证据基于 commit `952f1170b17a4bc7b1eeca3b4ac3570ea7acabfb` 验证；技术力文档命中：02-搜索 201/202/203（算法资产，本次复用服务不重写）。
> 证据头 schema 2；global dirty digest N/A（工作区含本次 IME 修复未提交改动）。

## 1. Objective

菜单栏右区，在「通知中心」按钮（`MenuBarStatusButtonId.Notification`）与「扩展中心(+)」按钮（`MenuBarStatusButtonId.Extensions`）之间插入一个**搜索**按钮。左键点击弹出搜索面板，面板内输入关键字实时检索**程序 / 设置 / 文件**三类结果（复用 `shell-search` 的 `IStartMenuSearchService`），点击结果执行启动。遵循菜单栏既有模式：独立 `MenuBarPopupWindow` 弹窗、主题令牌外观、失焦自动收起、弹窗互斥。

## 2. Current Behaviour

- `MenuBarStatusStrip`（shell-menu-bar/Status）构造函数按固定顺序加按钮：… `Notification`(966-972) → `Extensions`(973-982) → `DateTime`(983-992) → `Desktop`。每个按钮注册进 `_buttons[MenuBarStatusButtonId]` 供设置分区显隐。[verified]
- 按钮点击 → `ButtonClicked` 事件 → `StatusBarMenuBarExtension.OnButtonClicked` switch 分发到各独立面板（`ShowPopup` 懒创建 + 弹窗互斥）。`Notification` 开控制中心，`Extensions` 开扩展中心。[verified]
- `shell-search` 包已实现聚合搜索：`IStartMenuSearchService.SearchAsync(query, ct)` 汇总 Program/Settings/File 三类 Provider（ProgramSearchProvider/SettingsSearchProvider/FileSearchProvider），按得分降序返回 ≤30 条。[verified]
- `SearchPlugin` 在 Bootstrap 4.7 注册并 `context.Provide<IStartMenuSearchService>`；`MenuBarPlugin` 在 6.7 注册，晚于搜索插件。[verified]
- `shell-menu-bar.csproj` **未引用** `shell-search`；`MenuBarPlugin` 未注入 `IStartMenuSearchService`。[verified]

## 3. Relevant Architecture

- 菜单栏扩展体系：右区所有按钮 = `MenuBarStatusStrip` 内建（非 `IMenuBarExtension` 插件化），点击经 `ButtonClicked` → `StatusBarMenuBarExtension` 分发到独立 `MenuBarPopupWindow` 子类。弹窗统一继承 `MenuBarPopupWindow`（ShellWindow：无边框/毛玻璃/失焦收起/弹窗互斥）。[verified]
- 搜索服务 = `shell-search` 包，`IStartMenuSearchService` 是唯一入口，`SearchResult` 承载 Title/Subtitle/Category/AppItem/LaunchPath/Execute。[verified]
- 设置分区：`MenuBarSection` 遍历 `ExtensionCatalog.SystemFeatures` 生成显隐开关；新增按钮若需可配置显隐，需在 SystemFeatures 登记。[verified]

## 4. Technical-Knowledge Findings

- 命中 02-搜索 201/202/203/204（搜索节流/分类并行/排名/系统设置解析）：这些是**搜索实现**资产，本次**不重写算法**——复用 `shell-search` 已落地服务，文档作参考。[inferred]
- 菜单栏弹窗模式已自洽（`MenuBarPopupWindow` + `PopupAnchor` 锚定 + `MenuBarTheme` 令牌），新增面板照搬既有模式即可，无需新机制。[verified]
- 无「菜单栏搜索按钮」现成资产 → 本计划新建 `SearchPopupWindow`。[verified]

## 5. Constraint Findings

- 按钮插入位置：必须介于 `Notification` 与 `Extensions` 之间（用户指定）。枚举顺序与构造顺序同步调整。[verified]
- 搜索 UI 复用 `IStartMenuSearchService`：**不得**在菜单栏包内重新实现搜索逻辑（M7 复用）。[inferred]
- 面板必须继承 `MenuBarPopupWindow` 以获得失焦收起/主题外观/弹窗互斥。[verified]
- 防抖由 UI 层负责（shell-search README 明示）→ 搜索框需自行节流（DispatcherTimer 300-400ms），避免逐键同步调用。[verified]
- 单结果启动：程序类走 `AppItem`，设置/文件类走 `LaunchPath`（SearchResult.Execute 为空时消费方自启）。[verified]

## 6. Proposed Changes

| # | 文件 | 符号 | 职责 |
|---|------|------|------|
| 1 | `shell-menu-bar/Status/MenuBarStatusStrip.cs` | `MenuBarStatusButtonId.Search`（枚举，插在 Notification 与 Extensions 之间）；构造函数在通知后、扩展前插入 `CreateButton(Search, "搜索", 图标, 宽度)` | 新增搜索按钮，注册进 `_buttons` |
| 2 | `shell-menu-bar/Windows/SearchPopupWindow.cs`（新建） | `internal sealed class SearchPopupWindow : MenuBarPopupWindow` | 搜索面板：TextBox + 节流定时器 + `IStartMenuSearchService.SearchAsync` + 结果 ListBox（按 Category 分组），点击执行 |
| 3 | `shell-menu-bar/Services/StatusBarMenuBarExtension.cs` | 字段 `_searchPopup`；`OnButtonClicked` 新增 `case Search:` → `ShowPopup(ref _searchPopup, () => new SearchPopupWindow(search, ...), ...)` | 接线：搜索按钮点击打开面板，纳入弹窗互斥/失焦收起/Dispose |
| 4 | `shell-menu-bar/MenuBarPlugin.cs` | `LoadAsync` 增加 `var search = context.Get<IStartMenuSearchService>();`，传入 `StatusBarMenuBarExtension` 构造 | 注入搜索服务（M10 降级：null 时按钮仍显示但面板提示不可用或隐藏） |
| 5 | `shell-menu-bar/Services/StatusBarMenuBarExtension.cs` | 构造函数增加 `IStartMenuSearchService? search` 参数 | 接收注入 |
| 6 | `shell-menu-bar/BetterDesktop.Shell.MenuBar.csproj` | 新增 `<ProjectReference Include="..\shell-search\BetterDesktop.Shell.Search.csproj" />` | 编译期依赖 |
| 7 | `shell-menu-bar/Contracts/ExtensionCatalog.cs`（可选） | `SystemFeatures` 增加 `search` 条目映射 `MenuBarStatusButtonId.Search` | 设置分区可显隐搜索按钮 |

## 7. Implementation Sequence

1. csproj 加 `shell-search` 引用（编译基座，先就位）。
2. 枚举加 `Search` + `MenuBarStatusStrip` 构造函数插入搜索按钮（图标可复用自绘放大镜 Path）。
3. 新建 `SearchPopupWindow`（搜索框 + 节流 + 结果列表 + 启动）。
4. `StatusBarMenuBarExtension` 加 `_searchPopup` 字段、`Search` case、构造参数、Dispose/CloseAllExcept 覆盖。
5. `MenuBarPlugin` 注入 `IStartMenuSearchService`。
6. （可选）`ExtensionCatalog` 登记 `search` 系统功能。
7. `dotnet build -f net8.0-windows10.0.19041.0 -c Debug -r win-x64` 全绿验证。

## 8. Test Strategy

- 无既有菜单栏单测工程（壳层 UI，无 headless 测试基建）→ 以**编译 + 实机手测**为验收。
- 验证命令：`cd host && dotnet build BetterDesktop.Host.csproj -c Debug -r win-x64`（0 警告 0 错误）。
- 手测场景：① 搜索按钮出现在通知与扩展中心之间；② 输入关键字出现三类结果；③ 点击程序结果能启动；④ 失焦/点别处面板收起；⑤ 与其他面板弹窗互斥。

## 9. Risk and Impact Analysis

- **下游消费者**：`MenuBarStatusStrip` 构造函数改动影响按钮顺序/宽度布局（右区总宽增长约 20px，需确认不与多屏宽度冲突）。`MenuBarStatusButtonId` 枚举增项：`ExtensionCatalog.SystemFeatures` 引用需同步（若登记）。
- **依赖**：`MenuBarPlugin` 新增对 `IStartMenuSearchService` 的注入——搜索插件先加载（已确认），风险低；但若缺失需 M10 降级不崩。
- **搜索性能**：FileSearchProvider 走 Windows Search 索引；`SearchAsync` 同步执行各 Provider，需防抖 + 后台 Task 执行避免卡 UI 线程。

## 10. Files Expected to Change

| File | Symbols | Reason |
|------|---------|--------|
| `shell-menu-bar/Status/MenuBarStatusStrip.cs` | `MenuBarStatusButtonId.Search`、构造函数 | 加按钮 |
| `shell-menu-bar/Windows/SearchPopupWindow.cs`（新） | `SearchPopupWindow` | 搜索面板 |
| `shell-menu-bar/Services/StatusBarMenuBarExtension.cs` | `_searchPopup`、`OnButtonClicked`、构造 | 接线 |
| `shell-menu-bar/MenuBarPlugin.cs` | `LoadAsync` | 注入服务 |
| `shell-menu-bar/BetterDesktop.Shell.MenuBar.csproj` | ProjectReference | 依赖 |
| `shell-menu-bar/Contracts/ExtensionCatalog.cs`（可选） | SystemFeatures | 显隐控制 |

## 11. Reusable Implementation Context

- 搜索服务契约：`IStartMenuSearchService.SearchAsync(query, ct)` → `IReadOnlyList<SearchResult>`；`SearchResult` = Title/Subtitle/Category(App|Settings|File)/AppItem/LaunchPath/IconPath/Score/Execute。[verified]
- 弹窗基类：`MenuBarPopupWindow` 提供 `ShowAt(Point)`（锚点）、`BuildContent()`（子类实现 UI）、失焦收起、`SetThemeBinding` 主题令牌。[verified]
- 面板锚定：`PopupAnchor.Compute(visual, buttonWidth, popupSize, MenuBarMetrics.MenuBarHeight)`，单位逻辑像素。[verified]

## 12. Assumptions and Open Questions

- [assumed] 用户期望的"搜索功能"= 弹窗式搜索面板（程序/设置/文件），而非命令面板/搜索框内嵌菜单栏。若期望不同（如 Win+S 全局搜索/仅程序搜索），需先澄清。**建议实施前向用户确认一次搜索面板形态**。
- [assumed] 搜索按钮图标用自绘放大镜（与 `WifiGlyph` 等自绘一致），不引字体码位。
- [assumed] 搜索按钮默认启用，可通过设置分区显隐（可选第 7 步）。
- [deferred] 搜索历史/热门搜索等增强不在本次 scope。
- 技术力索引 02-搜索 201-204 为算法资产，本次未消费其实现细节（复用 shell-search 服务）；可后续将「菜单栏搜索按钮」沉淀为新功能文档。

## 13. Definition of Done

- [ ] 菜单栏右区在通知中心与扩展中心之间出现搜索按钮（自绘放大镜图标）。
- [ ] 点击搜索按钮弹出搜索面板，输入关键字实时（节流后）显示程序/设置/文件三类结果。
- [ ] 点击结果能启动对应程序/设置/文件。
- [ ] 面板继承 `MenuBarPopupWindow`：失焦自动收起、与其他面板互斥、主题令牌外观。
- [ ] `IStartMenuSearchService` 缺失时优雅降级（不崩溃，按钮可隐藏或面板提示）。
- [ ] `dotnet build BetterDesktop.Host.csproj -c Debug -r win-x64` 0 警告 0 错误。
