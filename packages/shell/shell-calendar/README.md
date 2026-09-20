# shell.calendar — 日历信息聚合

给菜单栏日历提供"某天有什么"的统一数据层：**农历/干支/生肖 + 法定节假日与调休补班 + 24 节气 + 传统节日 + 系统日程 + 天气**，并把**便签**做成预留扩展点。

> 铁律：**没有数据源的能力就隐藏，绝不显示编造数据。**（天气无源 → 隐藏；日程未授权 → 隐藏；年表未填 → 不显示休/班）

## 文件职责

| 文件 | 职责 |
|---|---|
| `packages/api/Calendar/ICalendarEntryProvider.cs` | **唯一扩展点**：条目提供者 + `ICalendarService` 聚合契约 + `CalendarDayInfo`（契约已迁 BetterDesktop.Api） |
| `packages/api/Calendar/CalendarEntry.cs` | 条目模型与种类（Holiday/Workday/SolarTerm/Festival/Event/Weather/**Note**） |
| `Services/SolarTermCalculator.cs` | 24 节气（Meeus 太阳视黄经 + 牛顿迭代，非查表常数） |
| `Services/LunarInfo.cs` | 农历月日 / 闰月 / 干支 / 生肖 |
| `Services/SolarTermProvider.cs` | 节气条目（按年缓存） |
| `Services/FestivalProvider.cs` | 传统节日（农历固定 + 公历固定 + 第 N 个星期几） |
| `Services/HolidayProvider.cs` + `IHolidayProvider.cs` | 法定节假日与调休（年表 JSON；在线源可替换实现） |
| `Services/SystemEventsProvider.cs` | 系统日程（WinRT AppointmentCalendar，未授权即禁用） |
| `Services/WeatherProvider.cs` + `IWeatherDayProvider.cs` | 天气（无数据源 → Null 实现，隐藏分组） |
| `Services/CalendarService.cs` / `CalendarServiceFactory.cs` | 聚合 / 排序 / 广播；默认提供者的唯一装配点 |
| `CalendarPlugin.cs` | Provide `ICalendarService`（Bootstrap 序：早于 menu-bar） |
| `Data/holidays-2026.json`、`holidays-2027.json` | 内置年表（EmbeddedResource，days 待官方安排录入） |

## 依赖

- `kernel`（IPlugin/IContext/IKernelLogger）、`BetterDesktop.Api`（Calendar 契约）、`shell-settings`（预留设置键）
- 被 `shell-menu-bar` 消费（`CalendarPopupWindow` 月视图 + `CalendarDayPopupWindow` 日详情）

## 节假日年表怎么填

`days` 数组需按**国务院办公厅放假安排**录入（调休补班无法推算，必须手工填）：

```json
{ "year": 2026, "meta": { "source": "国务院办公厅节假日安排", "updatedAt": "", "note": "..." }, "days": [ { "date": "2026-01-01", "name": "元旦", "workday": false }, { "date": "2026-01-04", "name": "元旦调休补班", "workday": true } ] }
```

- `workday=false` → 日历显示"休"；`workday=true` → 显示"班"
- 放置位置：`%LocalAppData%\BetterDesktop\Calendar\holidays-<年>.json`（**优先于内置年表**，改完重开日历即生效）
- 未填写 = 正常状态，日历只是不显示休/班标记

## 便签接口怎么用（未来的 quick-note）

```csharp
internal sealed class NoteEntryProvider : ICalendarEntryProvider { public string Id => "note"; public string DisplayName => "便签"; public bool IsEnabled => true; public event EventHandler? EntriesChanged;   // 便签增删改时触发

    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to) => _store.Query(from, to) .Select(n => new CalendarEntry(n.Date, CalendarEntryKind.Note, n.Title, n.Excerpt)); }

// quick-note 插件装配处： context.Get<ICalendarService>()?.Register(new NoteEntryProvider(_noteStore));
```

注册后：月视图该日出现小圆点，点开日详情出现「便签」分组。**日历与 UI 不需要任何改动。**

## Known Limitations

- 年表 `days` 待官方安排录入（内置 2026/2027 为空）；未录入时不显示休/班
- 天气：本仓无天气数据源 → 默认隐藏该分组；实现 `IWeatherDayProvider` 后自动出现
- 系统日程为**只读**；未授权时静默隐藏，不弹授权打扰用户
- 节气精度约 ±15 分钟；跨年边界（小寒/大寒在 1 月）按公历年归组
