# BetterDesktop 外部扩展开发指南（extension-dev.md）

> **面向第三方创作者**：基于本指南 + 两个依赖包即可开发 BetterDesktop 的外部扩展。
> 信息源提供 API 已全部独立到 `BetterDesktop.Api`（2026-09-08 独立化），
> 插件只需引用 `BetterDesktop.Kernel`（生命周期）与 `BetterDesktop.Api`（全部信息源/扩展点契约）。

---

## 1. 依赖面：只需两个包

| 包 | 作用 | 位置 |
| --- | --- | --- |
| `BetterDesktop.Kernel` | 插件生命周期（`IPlugin`）、服务定位器（`IContext`）、事件总线（`IEventBus`）、资源治理（`IHmrManager`） | `packages/kernel/kernel/` |
| `BetterDesktop.Api` | **全部信息源提供 API**：状态监控、应用/日历/通知/搜索/窗口/最近/任务栏/dock/转换/设置/开始菜单/桌面/右键菜单/酷狗/外观/毛玻璃/菜单栏扩展点/插件窗口契约 | `packages/api/`（零项目依赖，纯契约） |

> 内部实现包（shell-status、shell-calendar 等）不可被插件引用——它们实现契约并注入服务，
> 插件只认接口。这保证「契约面稳定、实现可替换」。

---

## 2. 契约总览（按域速查）

### 2.1 状态信息源（shell-status 域，`BetterDesktop.Shell.Status.Contracts`）

| 接口 | 用途 |
| --- | --- |
| `IStatusMonitor` | 状态监控项基础契约（`MonitorId` / `GetSnapshot()` / `Changed` 事件） |
| `IMemoryMonitor` `ICpuMonitor` `IBatteryMonitor` `IVolumeMonitor` `IMicrophoneMonitor` `INetworkMonitor` `IImeMonitor` `IBrightnessMonitor` | 各状态项强类型标识（语义快照统一由 `StatusSnapshot` 承载） |
| `IStatusPoller` | 统一轮询器（`Start/Stop/PollNow`；各监控项独立调度） |
| `IUserAvatarService` | 当前用户头像（`GetUserPicturePathAsync`） |
| 模型 | `StatusSnapshot`（人话文本 + 严重级别 + 进度 + 图标键）、`StatusSeverity` |

**消费方式**：`context.Get<IMemoryMonitor>()` → `GetSnapshot()` 或订阅 `Changed`。**不要自行轮询**——统一轮询器已按监控项独立调度（异步采集、不阻塞 UI）。

### 2.2 服务信息源（按域）

| 域（命名空间 `BetterDesktop.Shell.*.Contracts`） | 接口 |
| --- | --- |
| AppSource | `IAppSourceService`（扫描开始菜单/已安装/Store/全盘/新装检测）、`IAppIconService`（图标提取+缓存+预取）；模型 `AppItem` / `AppItemId` / `ProgramFolder` |
| Calendar | `ICalendarService`、`ICalendarEntryProvider`（日历条目扩展点）、`IHolidayProvider`、`IWeatherDayProvider`；模型 `CalendarEntry` |
| Notification | `INotificationService`（当前：新装应用提醒） |
| Search | `ISearchResultProvider`（搜索扩展点）、`IStartMenuSearchService`；模型 `SearchResult` |
| WindowTracker | `IWindowTrackerService`（运行中窗口/应用追踪）；模型 `WindowInfo` / `RunningAppInfo` |
| Recent | `IRecentItemsService`；模型 `RecentItem` |
| Taskbar | `ITaskbarAppearanceService`；模型 `TaskbarAppearanceConfig` |
| Dock | `IDockService` / `IDockLayoutService` / `IDockAppsService` / `IDockIconService`；模型 `DockItemData` / `DockItemId` |
| Convert | `IConversionEngine` / `IConvertMenuService` / `IArchiveService` / `IDownloadableEngine`；模型 `ConversionResult` / `ArchiveResult` |
| Settings | `ISettingsService`（键值持久化+变更广播）、`ISettingsSection` / `ISettingsSectionRegistry` / `ISettingsWindowService` / `IComponentToggle` / `IThemeTokens` |
| StartMenu | `IStartMenuService`、`IStartMenuDataService`（布局数据契约）、`IStartMenuSectionProvider` / `IStartMenuLayoutProvider`（扩展点） |
| Desktop | `IDesktopBrowser`；模型 `BrowserEntry` |
| ContextMenu | `IFileClassifier`、`IMenuTemplate` / `IMenuTemplateBuilder`、`IShellExMenuPreview`；模型 `MenuRequest` / `MenuItemDef` / `FileIdentity` |
| Music | `IKugouMusicApi`（酷狗平台 API，返回原始 JSON） |

### 2.3 核心能力契约（`BetterDesktop.Shell.Core.*`）

| 接口 | 用途 |
| --- | --- |
| `IAppearanceService` | 全局外观单一来源（强调色/色调/圆角/皮肤；`Changed` 事件一处改动全局生效） |
| `IVibrancyService` / `IAdvancedVibrancyService` | 窗口毛玻璃（Transparent/Acrylic/None） |
| `IThemeTokens` | 主题令牌（配色/字号） |
| `IDesktopSurface` / `IWindowHandleService` | 桌面窗口句柄/原生窗口服务 |
| `IAnimationService` | 动画服务 |
| `IMenuBarExtension` / `IMenuBarExtensionRegistry` | 菜单栏扩展点（右区按钮 / 左区弹窗提供者） |

### 2.4 插件窗口契约（`BetterDesktop.Shell.PluginSdk`）

| 类型 | 用途 |
| --- | --- |
| `IShellPluginWindow` | 插件窗口：提供 `Content`（UI）+ `Config`（外壳基础配置白名单）+ `OnThemeChanged`（可选） |
| `PluginWindowConfig` | 窗口基础配置（WindowKey / AllowTopmost / ShowInTaskbar / AllowResize / 默认尺寸）——仅声明，最终由内核权限校验后生效 |
| `ThemeSnapshot` | 只读主题快照（IsDarkMode / FontScale / 前景/描边/强调色 / 皮肤状态）——插件只能查看，不能修改全局主题 |
| `SkinKind` | 皮肤种类（None / Image / Preset） |

### 2.5 事件总线（跨域通信唯一通道）

- `IEventBus`（kernel）——事件名常量在 `ShellEvents`（`BetterDesktop.Shell.Core`）：
  - `ShellEvents.SettingsChanged` → 载荷 `SettingsChangedEventArgs`（设置键值变更）
  - `ShellEvents.AppearanceChanged` → 载荷 `AppearanceChangedArgs`（外观令牌变更）
- **规则（ADR-002 D4）**：跨程序集通信一律走事件总线，禁止跨程序集裸 C# event。

---

## 3. 插件生命周期与装配

```csharp
// 插件基础接口（kernel）
public interface IPlugin
{
    string Name { get; }
    IReadOnlyList<Type> Inject { get; }                 // 声明所需依赖；全部可用前内核保持 PENDING
    Task LoadAsync(IContext context, CancellationToken ct = default);
    Task UnloadAsync(CancellationToken ct = default);
}
```

```csharp
// 服务定位器（kernel）
context.Get<T>();             // 取已注入的服务（T 是 BetterDesktop.Api 里的接口）
context.Provide<T>(svc);      // 向内核注册自己的服务（供其他插件消费）
context.Effect(cleanup);      // 注册卸载清理器（UnloadAsync 之外的一切副作用）
```

**装配三段式**（参考 shell-menu-bar/MenuBarPlugin.cs）：

```csharp
public sealed class MyPlugin : IPlugin
{
    public string Name => "my-extension";
    public IReadOnlyList<Type> Inject => [typeof(IMemoryMonitor), typeof(ISettingsService)];

    public async Task LoadAsync(IContext context, CancellationToken ct)
    {
        var mem = context.Get<IMemoryMonitor>();          // 1. 取信息源
        var settings = context.Get<ISettingsService>();
        using var sub = settings.Subscribe(...);           // 2. 订阅事件（若有）
        using var effect = context.Effect(() => Cleanup()); // 3. 注册卸载清理
    }
}
```

> `Inject` 声明依赖 → 内核自动等待依赖就绪 → `LoadAsync` 时才保证可 `Get<T>()`。这是「按需加载」的落点：插件依赖不满足时自动 PENDING，不阻塞系统启动。

---

## 4. 信息源消费示例

### 4.1 状态监控（同步出数、异步采集）

```csharp
var mem = context.Get<IMemoryMonitor>();
var snap = mem.GetSnapshot();                       // 语义快照：72% / 内存占用 72%... / Warning
mem.Changed += (_, s) => UpdateUi(s.ShortText, s.Severity);  // 差异变化才触发，无需自行轮询
```

### 4.2 事件订阅（跨域同步）

```csharp
var bus = context.Get<IEventBus>();
using var s1 = bus.Subscribe(ShellEvents.SettingsChanged, e =>
    ((SettingsChangedEventArgs)e).Key == "my-plugin.flag");
using var s2 = bus.Subscribe(ShellEvents.AppearanceChanged, e =>
    ApplyTheme((AppearanceChangedArgs)e));
```

### 4.3 大列表预取（异步 + 取消）

```csharp
var icons = context.Get<IAppIconService>();
await icons.PrefetchAsync(appList, ct);              // 后台批量预取，不阻塞 UI
var icon = await icons.GetIconAsync(app, ct);        // 高清优先，带缓存
```

---

## 5. 扩展点实现示例（第三方能力接入）

### 5.1 菜单栏扩展（右区按钮 / 左区弹窗）

```csharp
// 实现 IMenuBarExtension（BetterDesktop.Api）
public sealed class WeatherButton : IMenuBarExtension
{
    public string Id => "weather";
    public FrameworkElement? GetVisual() => new TextBlock { Text = "☁" };
    public void OpenPopup(Point anchor) { /* 打开独立弹窗（ShellWindow） */ }
    public void ClosePopup() { /* 统一收起 */ }
}
// 装配：在 LoadAsync 里把实例提供给 IMenuBarExtensionRegistry（或内核装配处注册）
```

### 5.2 搜索 Provider（开始菜单搜索即插即用）

```csharp
public sealed class MySearchProvider : ISearchResultProvider
{
    public string Name => "my-notes";
    public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct)
    {
        // 同步返回、遵守 ct；内部异常记日志不冒泡（M10）
    }
}
```

### 5.3 日历条目提供者

```csharp
public sealed class TodoProvider : ICalendarEntryProvider
{
    public string Id => "todo";
    public string DisplayName => "待办";
    public bool IsEnabled => true;
    public event EventHandler? EntriesChanged;            // 数据变化时触发
    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to) { /* 同步出数 */ }
}
```

### 5.4 开始菜单布局 / 栏目（只依赖数据契约，不依赖实现类）

```csharp
// 布局扩展：只拿到 IStartMenuDataService（数据契约），看不到 StartMenuService 实现
public sealed class MyLayout : IStartMenuLayoutProvider
{
    public string Name => "my-layout";
    public FrameworkElement BuildLayout(IStartMenuDataService service)
        => /* 用 service.GetAllApps() / GetProgramTree() / ThemeTokens 构建 UI */;
}
```

### 5.5 插件窗口（由内核托管外壳，插件只提供内容）

```csharp
public sealed class MyPanel : IShellPluginWindow
{
    public UIElement Content => new TextBlock { Text = "我的面板" };
    public PluginWindowConfig Config => new() { WindowKey = "my-panel", AllowResize = ResizeMode.CanResize };
    public void OnThemeChanged(ThemeSnapshot theme) { /* 自定义绘制时响应主题变化 */ }
}
```

---

## 6. 三原则（本仓库一切代码的铁律）

1. **功能复用**：同一能力只在一处实现。接口在 `BetterDesktop.Api`，实现只在对应 shell-* 包，
   任何第三方/内部 UI 一律经 `IContext.Get<T>()` 消费，禁止复制实现。
2. **数据同步**：单一数据源。如内存/CPU/电量等状态统一由 `I*Monitor` 产出、`IStatusPoller` 广播，
   所有面板共享同一份快照——一处采集、处处同步；亮度读写走 `IBrightnessMonitor` 单一入口。
3. **功能异步**：重活不阻塞 UI。采集/扫描/压缩/联网一律后台（`Task.Run` / 异步 API + CancellationToken），
   UI 只订阅结果。扩展的 `Search` / `GetEntries` 等同步方法须轻量（内部缓存预取，同步出数）。

---

## 7. 代码规范提醒（开源前必读）

- **命名空间遮蔽陷阱**：`BetterDesktop.Api` 带入了 `BetterDesktop.Shell.Convert` / `BetterDesktop.Shell.Dock`
  等**根命名空间**，引用 api 的包内写 `Convert.ToXxx(...)`（System.Convert）或 `Dock.Left`（WPF 枚举）会被
  命名空间链优先解析而报错。**一律全限定**：`System.Convert.ToXxx(...)`、`System.Windows.Controls.Dock.Left`。
- 外部扩展同理：不要在你的插件里 `using BetterDesktop.Shell.Convert.Contracts;` 后写裸 `Convert.ToByte`。
- 注释要求：每个公开接口/模型必须有 XML 文档注释（生成文档 + 0 警告门禁）；文件头写「白话 → 位置」导航。
- 命名空间与目录：新契约放 `packages/api/<域>/`，**保留文件内命名空间不变**（使用方 using 零改动）。

---

## 8. 构建与验证

```powershell
dotnet build BetterDesktop.slnx -v q --nologo      # 0 警告 0 错误（TreatWarningsAsErrors）
dotnet test  BetterDesktop.slnx -v q --nologo --no-build  # 345 用例全过
```

门禁：`BetterDesktop.Api` 是**零项目依赖**契约包（仅 BCL + WPF 基础类型）——
任何接口新增依赖都必须收敛到 API 包内部类型，不得反向引用实现包（防循环依赖）。

---

*BetterDesktop · 一阶段收尾定型（2026-09-08）· 信息源 API 独立化产物*
