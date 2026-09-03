# DynamicDesktop（插件设计稿 · 深度版）

> 状态：设计草稿（尚未实现）。取代 `分析稿/方案-Shell替代程序优秀特性集成.md` 中"动态桌面"章节的浅层描述。

## 1. 目标与边界

**做什么**：桌面小部件框架、壁纸管理、桌面图标布局优化。
**不做什么**：文件管理器（Finder / Launchpad 属于其他扩展）、窗口缩略图、系统托盘接管。

## 2. 架构

```
DynamicDesktop (Extension)
├── WidgetHost         # 承载小部件容器，管理生命周期与命中测试
├── WidgetRegistry     # 注册可用小部件类型（扩展点给第三方）
├── WallpaperService   # 静态 / 轮播 / Bing 每日
├── IconLayoutService  # 桌面图标网格布局与持久化
└── DesktopOverlay     # 在系统桌面之上合成覆盖层（不拦截输入）
```

依赖：`BetterDesktop.Shell.Core`（Surface / Vibrancy / Animation）。

## 3. 领域模型

```csharp
interface IDesktopWidget
{
    string Name { get; }
    Size Size { get; }
    Point Position { get; }
    UIElement CreateControl();
    void Update();
    void Dispose();
}

record WidgetManifest(string Id, string DisplayName, Size DefaultSize, string Category);

class WallpaperEntry { string Path; DateTime? ExpiresAt; }
```

## 4. 内核集成

- `Name = "dynamic-desktop"`，`Inject: [typeof(IVibrancyService)]`（真实内核契约：无 ExtensionId/Manifest，依赖用 `Inject` 声明）。
- `LoadAsync(IContext)`：注册 `IDesktopService` / `IWallpaperService` / `IWidgetService`；扫描启用清单；恢复图标布局。
- `UnloadAsync`：卸载所有 widget，恢复系统桌面，避免残留覆盖层。

## 5. 公共契约（语义）

```csharp
interface IWidgetService
{
    void AddWidget(IDesktopWidget w);
    void RemoveWidget(string name);
    IReadOnlyList<IDesktopWidget> Active { get; }
}

interface IWallpaperService
{
    Task SetWallpaperAsync(string path);
    Task SetSlideshowAsync(IEnumerable<string> paths, TimeSpan interval);
    event EventHandler<WallpaperChangedArgs> Changed;
}
```

- `AddWidget` 需校验 `WidgetManifest` 已注册，避免未知类型崩溃。

## 6. 配置

- 小部件清单：`enabled / position / size / refreshInterval`。
- 壁纸策略：`static | slideshow | bing-daily`，`interval` 可配（slideshow）。

## 7. 数据流

`WidgetHost` 创建控件 → 订阅 `WallpaperService.Changed` 重绘背景 → `Update` 按 `refreshInterval` 节流刷新。图标布局变更 → `IconLayoutService` 持久化 JSON。

## 8. 跨插件协作

- 与 ThemeCenter：Widget 外观走令牌，不直接写颜色。
- WidgetHost / 桌面覆盖层窗口继承 `shell-core.Surface.ShellWindow` 统一基类（统一窗口属性 + 毛玻璃入口），不另起窗口样式。
- 与 Dock / Taskbar：共享 `shell-core` 的 Surface 与边缘热区策略（避免重复）。

## 9. 错误处理

- 单个小部件异常：捕获并卸载该 widget，桌面其他部分不受影响。
- 壁纸路径失效：回退上一有效壁纸，记录诊断事件。
- 覆盖层需 `IsHitTestVisible=false` 区域透传点击到 explorer 桌面。

## 10. 性能

- 小部件更新走节流（如 1s）；壁纸解码在后台线程，避免主线程卡顿。
- 图标布局仅在变更时落盘，不轮询。

## 11. 验收

- [ ] 小部件可拖拽、可持久化
- [ ] 壁纸轮播生效
- [ ] 卸载后系统桌面完全恢复

## 12. 开放问题

- 覆盖层与 `explorer.exe` 桌面窗口的 z-order 与输入穿透策略需实测。
- 高 DPI 下图标网格对齐规则。
