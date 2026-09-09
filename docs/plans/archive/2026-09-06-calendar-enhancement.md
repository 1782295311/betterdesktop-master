# 计划：日历增强（主流信息接入 + 便签接口预留）

> 日期：2026-09-06 · 类别：功能开发 · 深度：standard · 仓库：better-desktop-cordis
> 需求（用户已确认范围）：① 中国法定节假日 + 调休补班；② 24 节气 + 传统节日；③ 系统日程事件；④ 天气；⑤ **留接口给未来的便签功能**。数据策略：离线年表 JSON + 预留联网更新接口。

## 1. 目标（场景语言）
1. 打开菜单栏日历，月视图每格能直接看出：哪天**放假**（休）、哪天**调休补班**（班）、哪天是**节气/传统节日**、哪天有**日程**，未来哪天有**便签**。
2. 点某一天 → 日详情面板：该日全部信息分组列出（放假/补班、节气与节日、日程、天气、便签[占位]）。
3. 日历信息全部走"条目提供者"接口，未来便签/其它数据源只需实现接口即可自动出现在日历里（零 UI 改动）。

## 2. 技术力检索结论
- 未命中「农历/节气/节假日」功能文档 → **新建**，实现稳定后回写 `TECH-KNOWLEDGE/19-桌面部件/` 或新建 `76-日历/`（待定域）。
- 命中工程事实：仓内**无真实天气数据源**（菜单条「天气」仅为扩展占位，grep 全仓无 Weather/Forecast 实现）→ 天气走 provider 接口 + Null 降级，**不编造天气数据**（M10）。
- WinRT 可用：项目已用 `Windows.Devices.Radios`（记忆 27968108 实证）→ `Windows.ApplicationModel.Appointments` 可调用，需 try-catch 权限降级。
- 复用：`ChineseLunisolarCalendar`（现有 CalendarPopupWindow 已在用）。

## 3. 关键决策
| 决策 | 选择 | 理由 |
|---|---|---|
| 承载方式 | **新包 `shell-calendar`** | 日历数据与 UI 无关、未来便签/任务复用；符合一功能一包惯例 |
| 扩展点 | 统一 `ICalendarEntryProvider`（区间取条目） | 便签将来实现它即可入日历，UI 零改动；避免堆 if-else |
| 节气 | **Meeus 天文算法算太阳视黄经**（不查常数表） | 可长期正确、可单测（二分二至），不依赖逐年数据 |
| 节假日 | 离线年表 JSON（2026/2027 骨架）+ `IHolidayProvider` 接口 | 用户选定策略；**调休补班无法推算**，必须人工填官方安排 → 年表先给结构+示例，绝不编造日期 |
| 天气 | provider 接口 + Null 降级 | 仓内无天气源，接入即显示，未接入不显示占位假数据 |
| 日程 | WinRT AppointmentCalendar，权限失败 → 该 provider 自动禁用 | 桌面应用可能弹隐私授权/被拒，必须可降级 |

## 4. 改动清单
**新包 `packages/shell/shell-calendar/`**
1. `Contracts/CalendarEntry.cs` + `CalendarEntryKind`（Holiday/Workday/SolarTerm/Festival/Event/Weather/Note）
2. `Contracts/ICalendarEntryProvider.cs`（Id/DisplayName/IsEnabled/GetEntries(from,to)/EntriesChanged）
3. `Contracts/ICalendarService.cs`（按日聚合、注册 provider、变更事件）
4. `Services/LunarInfo.cs`（农历月日/干支/生肖，复用 ChineseLunisolarCalendar）
5. `Services/SolarTermCalculator.cs`（Meeus 太阳视黄经 → 24 节气，UTC+8）
6. `Services/SolarTermProvider.cs`、`Services/FestivalProvider.cs`（公历固定 + 农历固定 + 第 N 个星期几）
7. `Services/HolidayProvider.cs` + `IHolidayProvider.cs` + `Data/holidays-2026.json`、`holidays-2027.json`（结构 + meta，days 待填官方安排）+ `README.md` 填写规范
8. `Services/SystemEventsProvider.cs`（WinRT 日程，降级）
9. `Services/WeatherDayProvider.cs`（接口 + Null 降级）
10. `Services/CalendarService.cs`（聚合/排序/去重/变更广播）
11. `CalendarPlugin.cs`（Provide ICalendarService）
12. `README.md`（含"未来便签如何接入"示例）

**接线**：`BetterDesktop.slnx`（+calendar、+calendar-tests）、`host/Bootstrap.cs`（早于 menu-bar）、`shell-menu-bar` csproj 引用。

**menu-bar UI**
13. `CalendarPopupWindow` 改造：格子加「休/班」角标、节气或节日名、日程/便签小圆点；点日期开日详情
14. 新 `Windows/CalendarDayPopupWindow.cs`：日详情（分组列出该日全部条目；便签区占位说明"接口已预留"）

**测试 `packages/shell/shell-calendar-tests/`**
15. 节气：二分二至落在常识日期 ±1 天、24 个节气单调递增、相邻间隔 15-16 天
16. 农历/节日：自洽性断言（春节当天农历正月初一等），**不写死公历日期**
17. HolidayProvider：文件缺失/空 days → 空列表不抛；结构正确 → 正确解析休/班
18. CalendarService 聚合：多 provider 合并、排序、变更广播

## 5. 边界与异常（工具型项目强制项）
- 年表缺失/损坏 → 不显示休/班，其余信息照常（不抛、不假数据）
- WinRT 日程权限被拒 → provider `IsEnabled=false`，日历不再显示"日程"分组
- 天气无数据源 → Null 降级，不显示
- 日期越界（DateTime/年表范围）→ 返回空条目
- 资源：无句柄/无流常驻；provider 变更事件在插件卸载时退订
- 便签：本轮**只留接口**，UI 显示占位说明，不做实现

## 6. 风险
| 风险 | 缓解 |
|---|---|
| 年表数据需人工填（调休无法推算） | JSON 结构 + meta(source/updatedAt) + README 填写规范；缺数据时降级不显示，绝不编造 |
| 节气算法精度 | 二分二至单测校验；相邻间隔断言 |
| WinRT 日程弹隐私授权 | try-catch + 可禁用；文档说明 |
| 天气无源 | 明确 Null 降级，UI 不显示占位假数据 |

## 7. DoD
1. `dotnet build BetterDesktop.slnx -warnaserror` 0 警告 0 错误
2. shell-calendar-tests 全绿
3. 真机：日历格子显示休/班/节气/节日；点日期弹日详情；系统日程能读到就显示、读不到静默不显示
4. 便签接口文档可被第三方（未来的 quick-note）照做接入

## 8. 交接（§14）
- 交接对象：`skills/ability-reuse-alignment/SKILL.md`
- 注入文档：无现成库文档（成熟工程增量 → 工程代码权威：复用 `MenuBarPopupWindow`、`SetThemeBinding` 主题令牌、M10 降级惯例）
- 适配参数：仓库根 `better-desktop-cordis/`；包命名 `BetterDesktop.Shell.*`；构建 `dotnet build BetterDesktop.slnx -warnaserror`
- 禁区：不编造节假日/天气数据；不改 `shell-quick-note` 现有行为
- DoD 核销：第 3 条必须真机实走
