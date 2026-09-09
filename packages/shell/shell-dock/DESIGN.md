# shell-dock（Dock 插件 · 深度设计稿）

> 状态：**核心已实现**（见 §0 现状盘点），本文给出完整目标设计，标 ★ 为待办/修复项。

## 0. 现状盘点（2026-08-22 实地核查）

已实现（真实代码）：

- `DockPlugin`（`IPlugin`，`Name="shell.dock"`，`Inject=[IVibrancyService]`）：托管 DockWindow + **AppGrabberWindow（应用管理中心）+ LaunchpadWindow + NewAppsNotificationWindow**；启动即显示 Launchpad；新装应用 3s 首查 / 60s 轮询。
- `DockAppsService`（门面）：消费 `IAppSourceService`，固定列表持久化 `dock-pinned.json`，去重/排序/`PinnedChanged`。
- `DockIconService` + `Win32IconProvider`：真实图标异步加载/缓存/预取。
- `DockLayoutService`：布局度量 / 底部边缘悬停 / 全屏隐藏判定。
- `RunningAppDetector`：`EnumWindows` 枚举可见顶层窗口（过滤 TOOLWINDOW/cloaked/无标题/自身进程），`ActivateWindow`。
- `DockService`：运行项单一事实来源。
- 模型：`DockItemData`（含 `AppUserModelId`）/ `DockItemId` / `DockAppType` / `DockLayoutMetrics`。
- `DwmThumbnail` + `DwmThumbnailInterop`：DWM 窗口缩略图雏形。
- `FolderInputWindow` / `MinimalDockWindow` / `DockFlyoutWindow`。
- **已统一（正面事实）**：全部 6 个窗口（Dock / AppGrabber / Launchpad / DockFlyout / MinimalDock / NewAppsNotification）均继承 `shell-core.Surface.ShellWindow` 统一基类（无边框/透明/置顶 + `ApplyWindowMaterial` 毛玻璃入口 + `OnLoadedCore` 钩子）。
- 测试：`DockAppsServiceTests` / `DockIconServiceTests` / `DockLayoutServiceTests`。

## 0.1 发现的问题（★ 修复项）

| # | 问题 | 级别 | 处理 |
|---|------|------|------|
| P1 | `IDockPinnedService`/`DockPinnedService` 是**死代码**（无任何接线） | P1 | 复活为"固定列表"公共契约，供 AppGrabber/Launchpad 消费 |
| P1 | `DockService.RemoveItem/ReorderItems/SetItemActive` 按 **Name** 操作，与 `AddItem/UpdateItem` 按 **Id** 不一致，与主键规则冲突 | P1 | 统一改为 `DockItemId` |
| P2 | shell-dock README 声称"图标/自动隐藏仅定义接口"，实际已实现（文档脱节） | P2 | 更新 README |
| P2 | `DockAppsServiceTests` 用单参构造 `new DockAppsService(storage)`，与现实现（2 参构造）不匹配，**实跑确认编译失败（CS7036 ×5）** | P2 | 更新测试为 2 参构造（或补单参便捷构造） |
| P3 | AppGrabber / Launchpad 长在 DockPlugin 内，Dock 卸载会连带关闭所有 UI 表面 | P3 | 拆分（见 §12） |
| P2 | **残留重复实现**：①`Services/ShellLinkResolver.cs` 与 app-source 版重复（8 处使用，返回 `DockAppType` 分叉）；②图标提取栈 `IIconProvider`/`Win32IconProvider`/`DockIconService` 本属 app-source 职责（README 声明的 `IAppIconService` 从未实现） | P2 | 上移合并至 shell-app-source（方案见 `shell-app-source/DESIGN.md §0.2`） |

## 1. 目标与边界

**做什么**：Dock 本体——固定应用展示、运行中应用呈现（运行检测/激活/缩略图）、布局/自动隐藏/全屏感知、固定项交互（点击启动/切换、拖拽排序、右键菜单调用 `IMenuService`）。 **不做什么**：不直接扫描应用（走 `IAppSourceService`）；不自绘右键菜单（走 `shell-context-menu`）；应用管理中心与 Launchpad 拆出独立插件（§12）；不替换系统 Shell（FrostedShell 教训）。

## 2. 架构

```
shell-dock (IPlugin)
├── DockPlugin            # 入口：Inject IVibrancyService + IAppSourceService；Provide Dock 服务
├── DockWindow            # Dock 主窗口（固定区 + 运行区 + 缩略图）
├── DockService           # 运行项单一事实来源（★ 修复主键）
├── DockAppsService       # 固定列表门面（★ 改为 Inject 消费 IAppSourceService）
├── DockPinnedService     # ★ 复活：公共"固定列表"契约实现（AppGrabber/Launchpad 消费）
├── DockLayoutService     # 布局/边缘/全屏
├── RunningAppDetector    # 运行窗口枚举 + 激活
├── DwmThumbnail*         # DWM 窗口缩略图
└── Controls/DockItem     # 单项控件（悬停放大/标签/角标）
```

依赖：`BetterDesktop.Kernel`、`shell-core`（Vibrancy/Animation/Surface）、`shell-app-source`（扫描/图标）；不再直接依赖 AppSource 实现类。

## 2.1 落实跟踪（2026-08-22 复核）

- ✅ `DockPlugin.Inject = [IVibrancyService, IAppSourceService, IAppIconService]`，`context.Get<>()` 消费，不再手动 new。
- ✅ **`IDockPinnedService` 死代码复活**：`new DockPinnedService(...)` + `context.Provide<IDockPinnedService>`。
- ✅ **`DockService` 主键统一 `DockItemId`**（RemoveItem/ReorderItems/SetItemActive 均按 Id）。
- ✅ 图标去重：`DockIconService(IAppIconService)`，`IIconProvider`/`Win32IconProvider`/`ShellLinkResolver` 已删；新增 `AppSourceConverter`（AppSource→DockAppType 映射）。
- ✅ 测试重写为 `FakeAppSource` mock（原 P2 编译失败已修复）；新增 `WindowCreationReproTests`。
- ✅ 构建实证：0 警告 0 错误（连带 kernel/core/app-source 全过）。
- ⏳ 仍待办（建议项，非阻塞）：AppGrabber / Launchpad 拆独立 `shell-app-center` 插件（当前仍在 `DockPlugin` 内，Dock 卸载会连带关闭）。

## 3. 领域模型

```csharp
sealed record DockItemData   // 已存在，保持
{
    required DockItemId Id;          // 稳定主键
    required string Name;
    required string ShortcutPath;
    required string TargetPath;
    required DockAppType AppType;    // Win32 | Uwp | Url
    string? AppUserModelId;
    string? IconCacheKey;
    bool IsRunning;
    bool IsPinned = true;
    int BadgeCount;
}

record RunningItem(DockItemId SourceId, IntPtr Hwnd, string ExePath, string Title);  // ★ 运行模型
```

## 4. 内核集成（真实契约 IPlugin）

```csharp
public sealed class DockPlugin : IPlugin
{
    public string Name => "shell.dock";
    public IReadOnlyList<Type> Inject => new[]
    {
        typeof(IVibrancyService),        // 毛玻璃
        typeof(IAppSourceService)        // 应用扫描/图标
    };

    public async Task LoadAsync(IContext context, CancellationToken ct = default)
    {
        var appSource = context.Get<IAppSourceService>()!;
        var apps = new DockAppsService(appSource, /* dock-pinned.json */);
        context.Provide<IDockAppsService>(apps);
        context.Provide<IDockPinnedService>(new DockPinnedService(appSource, /* path */));  // ★ 复活
        context.Provide<IDockIconService>(new DockIconService(/* IAppIconService */));      // ★ 图标来自 app-source
        // 创建 DockWindow（不含 AppGrabber/Launchpad——它们已是独立插件）
        _dockWindow = new DockWindow(..., this);
        _dockWindow.Show();
    }

    public Task UnloadAsync(CancellationToken ct = default) { _dockWindow?.Close(); return Task.CompletedTask; }
}
```

## 5. 公共契约（语义）

```csharp
interface IDockAppsService      // 固定列表门面（保留现状）
{
    IReadOnlyList<DockItemData> Pinned { get; }
    void Load(); void Save();
    void AddByPath(string path);
    void RemoveById(DockItemId id);
    void Reorder(IReadOnlyList<DockItemId> order);
    event EventHandler? PinnedChanged;
}

interface IDockPinnedService    // ★ 复活：AppGrabber/Launchpad 只读消费
{
    IReadOnlyList<DockItemData> Pinned { get; }
    event EventHandler? PinnedChanged;
}

interface IDockService          // 运行项单一事实来源（★ 统一主键）
{
    IReadOnlyList<DockItemData> Items { get; }
    void SetRunning(IEnumerable<DockItemId> runningIds);   // 由运行检测结果驱动
    event EventHandler? RunningChanged;
}

interface IDockLayoutService    // 保留现状
{
    DockLayoutMetrics Measure(int screenWidth, int screenHeight, int iconCount);
    bool ShouldShowOnEdgeHover(Point cursorScreenPoint);
    bool ShouldHideOnFullscreen();
}
```

- `SetRunning` 应为**整体替换**语义（以检测结果为准），而非逐项 toggle，避免竞态。
- 运行检测结果与固定列表的合并（固定项显示运行态、未固定运行项追加到运行区）在 DockWindow 内完成，`IDockService` 只存运行项。

## 6. 配置

- `dock.ini`：位置（底部居中）、图标尺寸（48 基线）、间距、自动隐藏开关、全屏隐藏开关、动画参数（悬停 1.2×/200ms、点击 0.95×/100ms）。
- 持久化：`dock-pinned.json`（固定列表）+ 布局偏好。

## 7. 数据流

```
运行检测（RunningAppDetector 周期/事件）
  → IDockService.SetRunning(整体替换)
  → DockWindow 合并固定+运行 → DockItem 渲染（运行态/角标）

固定变更（AddByPath/RemoveById/Reorder）
  → IDockAppsService.PinnedChanged
  → DockWindow 重绘 + AppGrabber/Launchpad（经 IDockPinnedService）同步

交互（点击/右键/拖拽）
  → 启动：ShellLinkResolver 目标 / ActivateFirstWindowOf
  → 右键：IMenuService.ShowAsync（shell-context-menu）
```

## 8. 跨插件协作

- 消费 `IAppSourceService` / `IAppIconService`（shell-app-source）。
- 窗口一律继承 `ShellWindow`（shell-core）；屏幕/DPI 几何用 `IDesktopSurface`（主窗 HWND 用 `IWindowHandleService`），Dock 布局不再自算屏幕边界。
- 调用 `IMenuService`（shell-context-menu）渲染右键，不自绘。
- 消费 ThemeCenter 令牌控制外观（★ 待接入）。
- AppGrabber / Launchpad 拆为独立插件后经 `IDockPinnedService` 只读联动。

## 9. 错误处理

- 运行检测/图标加载失败：单项跳过，Dock 不崩（现状已满足）。
- 持久化失败：保持内存状态，记录诊断（现状已满足）。
- 全屏判定：无边框全屏窗口可能需要额外校准（已知限制保留）。

## 10. 性能

- 图标异步 + 缓存 + 预取（现状已满足）。
- 运行检测节流（如 1-2s 周期），避免高频 EnumWindows。
- 缩略图延迟生成，Dock 可见时才更新。

## 11. 验收

- [ ] Dock 显示固定应用 + 运行中应用，真实图标
- [ ] 点击固定项：未运行→启动；运行→切换高亮（可激活首窗口）
- [ ] 边缘悬停显示、全屏隐藏
- [ ] 拖拽排序持久化
- [ ] 右键菜单经 `IMenuService` 弹出（而非自绘）
- [ ] `IDockPinnedService` 复活并被 AppGrabber/Launchpad 消费
- [ ] 卸载 Dock 不连带关闭 AppGrabber/Launchpad（拆分后）

## 12. 结构决策：AppGrabber / Launchpad 拆分（★ 建议）

- **现状**：AppGrabber（应用管理中心）与 Launchpad 是独立 UI 表面，却长在 `DockPlugin` 内，Dock 卸载会连带关闭全部。
- **建议**：拆为独立插件 `shell-app-center`（应用管理中心 + Launchpad），`Inject: [IAppSourceService, IDockPinnedService, IVibrancyService]`；Dock 只保留 Dock 本体。这符合"一切皆插件、按插件独立设计"原则，也让 AppGrabber 可被右键菜单"打开应用中心"等入口独立调用。
- 若暂不拆分（小步快跑），至少把 AppGrabber/Launchpad 的窗口管理与生命周期从 `DockPlugin` 抽到独立 `AppCenterHost` 类，为拆分留缝。

## 13. 开放问题

- 运行检测用轮询还是 `WinEventHook` 事件驱动（性能/复杂度取舍）。
- 缩略图浮层是否复用 `shell-context-menu` 的 MenuHost 弹层机制。
