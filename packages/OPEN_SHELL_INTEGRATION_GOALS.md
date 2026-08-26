# Open-Shell-Menu 集成与包结构重组 · 目标文档

> 日期：2026-08-25
> 状态：实施中（包结构重组 8 步已完成，见 [OPEN_SHELL_INTEGRATION_STEPS.md](OPEN_SHELL_INTEGRATION_STEPS.md)；剩余为 TTB 注入验证与 M6 收口）
> 上游：`参考/Open-Shell-Menu-master/`（C++ 行为参考，逻辑零复制，C# 重写）
> 关联：`docs/architecture/ADR-001.md`、`docs/MECHANISMS.md`、`docs/pluginization.md`、`docs/实现拆解/任务01-shell-app-center拆分.md`

---

## 一、背景与动机

当前 `shell-dock` 一个包承担了 6 类不同能力（Dock 栏 UI、运行中窗口检测、应用固定、应用浏览/管理中心、DWM 缩略图、新装应用通知），其中 3 类是开始菜单与未来任务栏都要共用的基础服务。

Open-Shell-Menu 的开始菜单能力加入后，若不先重组包结构，会出现：
- Dock 与开始菜单互相引用、重复实现（违反 M7 单一实现）；
- 运行中应用状态、固定项、搜索等能力被绑定在 Dock 包里，其他 UI 无法 inject；
- AppGrabberWindow（68KB 应用管理中心）与开始菜单功能重叠。

**结论：先拆基础服务包，再做开始菜单 UI。**

---

## 二、目标包结构

```
packages/
├── kernel/                         # 内核本体（零 UI 依赖，不动）
│   ├── kernel/
│   ├── kernel-loader/
│   ├── kernel-hmr/
│   └── kernel-timer/
│
└── shell/                          # 外壳能力（全部是插件）
    │
    ├── === 基础服务层（无 UI 或极薄 UI，可被任意 UI 包 inject）===
    │
    ├── shell-core/                 【已有】窗口基类 ShellWindow/PluginHostWindow、
    │                               #        IAppearanceService、IVibrancyService、IAnimationService
    ├── shell-plugin-sdk/           【已有】IShellPluginWindow 插件窗口契约
    ├── shell-app-source/           【增强】应用数据来源：程序树 + 图标 + 变化通知
    ├── shell-window-tracker/       ★ 新建：运行中窗口/应用追踪（从 Dock 抽出）
    ├── shell-pinning/              ★ 新建：通用固定/收藏，按 zone 分区
    ├── shell-search/               ★ 新建：统一搜索（程序/文件/设置）
    ├── shell-recent/               ★ 新建：最近项 / 跳转列表
    ├── shell-notification/         ★ 新建：通知弹窗（新装应用通知等）
    │
    ├── === UI 角色层（只做渲染与交互，inject 上面的服务）===
    │
    ├── shell-start-menu/           【改造】角色 shell.start：WPF 自绘菜单 + TTB 回退锚点
    ├── shell-dock/                 【瘦身】只剩 Dock 栏 UI + 布局 + 视觉
    ├── shell-taskbar/              【未来】角色 shell.taskbar
    ├── shell-menu-bar/             【未来】角色 shell.bar
    ├── shell-desktop/              【未来】角色 shell.desktop
    ├── shell-control-center/       【未来】控制面板
    │
    └── === 配置层 ===
        ├── shell-settings/         【已有】设置窗口 + ISettingsSectionRegistry
        └── shell-context-menu/     【已有】右键菜单
```

---

## 二·五、实施现状（2026-08-25 核对）

包结构重组 8 步已全部落地（对应 [STEPS](OPEN_SHELL_INTEGRATION_STEPS.md)）：

- ✅ 基础服务包已独立：`shell-window-tracker`（IWindowTrackerService）、`shell-pinning`（IPinningService，zone 分区）、`shell-search`（IStartMenuSearchService）、`shell-recent`（IRecentItemsService）、`shell-notification`（INotificationService）。
- ✅ `shell-app-source` 承载统一程序树 / 图标 / 变化通知；`shell-dock` 已瘦身，不再内嵌扫描与 AppGrabber。
- ✅ `shell-start-menu` 具备 WPF 自绘经典菜单 + Win 键钩子（WH_KEYBOARD_LL）+ IEventBus `shell.start.toggle` 契约（Dock / 未来任务栏开始按钮共用）；多屏/DPI 定位已落地（MonitorInterop + VisualTreeHelper.GetDpi）。
- ✅ `shell-taskbar.appearance` 已接管原生任务栏外观控制（Shell_TrayWnd，ARGB→ABGR 已修正）。
- ✅ `shell-app-center` 不再独立建包：AppGrabberWindow / FolderInputWindow / ShowAppGrabber 已废弃删除，其"全应用浏览 / 固定 / 管理员 / 位置 / 卸载"能力由开始菜单接管。

**剩余待办（非阻塞，分先后）**：
1. ℹ️ 已定向 Direct-Use Open-Shell：`StartMenuDLL.dll` 已 staging 到程序目录 `external/openshell/`（`StartMenuDLL.dll` + `StartMenuL10N.ini` + `StartMenuHelperL10N.ini` + `Skins/`），`use-native-ttb` 默认为 `true`。剩余为实机验证注入后：开始按钮/Win 键/多屏是否被 Open-Shell 正确接管（见 3.7）。
2. ✅ 已收口：开始菜单各布局（AllApps/Classic/Win7/Win10）圆角/字号已统一到 IThemeTokens（`CornerRadius`/`FontSizeInput/Body/Caption/Small`，由 AppearanceService 承载），不再硬编码文字。
3. 未来：把 `is-game`/窗口激活等进一步下放到 window-tracker；`shell-menu-bar` / `shell-desktop` 完整角色实现。

---

## 三、各包职责与契约

### 3.1 shell-app-source（增强，不新建）

**职责**：统一的应用数据来源——扫描开始菜单/已安装程序/UWP，提供应用模型与图标。

**增强项**（参考 Open-Shell `ProgramsTree`/`MetroLinkManager`）：
- 新增 `GetProgramTree()`：返回文件夹层级模型（开始菜单需要按文件夹分类显示）。
- 新增 `AppSourceChanged` 事件：`FileSystemWatcher` 监控开始菜单目录 + 注册表卸载项变化，防抖后发出。
- UWP 枚举走 ManagedShell.UWPInterop（已引用），不重复实现。
- **删除** `DockAppsService` 中重复的 ScanStartMenu/ScanInstalledApps/GetNewlyInstalledApps（M7 单一实现）。

**新增模型**：
```csharp
public sealed record ProgramFolder(
    string Name,
    string Path,
    IReadOnlyList<ProgramFolder> SubFolders,
    IReadOnlyList<AppItem> Apps);
```

**提供服务**：`IAppSourceService`、`IAppIconService`。

---

### 3.2 shell-window-tracker（★ 新建）

**来源**：`shell-dock/Services/RunningAppDetector.cs`（348 行，含 P/Invoke）+ `DwmThumbnail*.cs`。

**职责**：
- 枚举可见顶层窗口（EnumWindows + WS_EX_TOOLWINDOW + DWM cloaked 过滤）。
- 按进程路径 / AppUserModelId 关联到 `AppItem`。
- WinEvent 钩子（`SetWinEventHook`）监听窗口创建/销毁/激活/标题变化，发出变更事件。
- 激活指定应用的首个窗口（还原最小化 + 置前）。
- DWM 缩略图注册与管理（DwmThumbnailInterop 收口于此）。

**契约**：
```csharp
public interface IWindowTrackerService
{
    IReadOnlyList<RunningAppInfo> GetRunningApps();
    bool IsRunning(AppItemId appId);
    bool Activate(AppItemId appId);
    IReadOnlyList<WindowInfo> GetWindowsOfApp(AppItemId appId);
    event EventHandler<RunningAppsChangedEventArgs>? RunningAppsChanged;
    event EventHandler<IntPtr>? ForegroundWindowChanged;
}
```

**谁 inject**：shell-dock（运行指示器）、shell-start-menu（运行标记/点击激活）、shell-taskbar（窗口列表/预览）。

**P/Invoke 收口**：EnumWindows / SetForegroundWindow / DwmGetWindowAttribute / SetWinEventHook / DwmRegisterThumbnail 全部在本包，其他包不直接写。

---

### 3.3 shell-pinning（★ 新建）

**来源**：`shell-dock/Services/DockPinnedService.cs` + `DockAppsService` 的固定部分（AddByPath/RemoveById/Reorder/PinnedChanged）。

**职责**：通用固定/收藏服务，按 **zone（分区）** 隔离，不同 UI 位置各用各的 zone。

**契约**：
```csharp
public interface IPinningService
{
    IReadOnlyList<PinnedItem> GetPinned(string zone);
    void Pin(string zone, AppItemId appId);
    void Unpin(string zone, AppItemId appId);
    void Reorder(string zone, IReadOnlyList<AppItemId> order);
    bool IsPinned(string zone, AppItemId appId);
    event EventHandler<PinnedChangedEventArgs>? PinnedChanged;
}
```

**zone 约定**：
- `"dock"` — Dock 栏固定项
- `"startmenu"` — 开始菜单固定/磁贴
- `"taskbar"` — 任务栏固定项
- 第三方插件可定义新 zone（扩展点）

**持久化**：一个 JSON 文件按 zone 分组（兼容旧 `dock-pinned.json`，迁移时读取旧文件写入 `"dock"` zone）。

**谁 inject**：shell-dock、shell-start-menu、shell-taskbar。

---

### 3.4 shell-search（★ 新建）

**来源**：参考 Open-Shell `SearchManager`（55KB）的匹配算法与搜索源组织，C# 重写。

**职责**：统一搜索入口 + 可扩展搜索源。

**契约**：
```csharp
public interface IStartMenuSearchService
{
    Task<SearchResultSet> SearchAsync(string query, CancellationToken ct = default);
    void RegisterProvider(ISearchResultProvider provider);
}

public interface ISearchResultProvider
{
    string Category { get; }
    int Priority { get; }
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct);
}
```

**内置搜索源**：
- `ProgramSearchProvider`：对 IAppSourceService 的内存模糊匹配（子序列匹配，<50ms）。
- `SettingsSearchProvider`：枚举 ms-settings: 已知项。
- `FileSearchProvider`：Windows Search API（ISearchQueryHelper），失败降级（M10 降级上报）。

**谁 inject**：shell-start-menu、shell-control-center、shell-settings（设置内搜索）。

---

### 3.5 shell-recent（★ 新建）

**来源**：参考 Open-Shell `JumpLists`（20KB）。

**职责**：最近使用程序/文档追踪、固定项（跳转列表内的固定，区别于 shell-pinning 的区域固定）。

**契约**：
```csharp
public interface IRecentItemsService
{
    IReadOnlyList<RecentItem> GetRecentPrograms(int count = 10);
    IReadOnlyList<RecentItem> GetRecentDocuments();
    IReadOnlyList<RecentItem> GetJumpList(AppItemId appId);
    void PinToJumpList(AppItemId appId, RecentItem item);
    void UnpinFromJumpList(AppItemId appId, RecentItem item);
    event EventHandler? RecentItemsChanged;
}
```

**实现要点**：SHAddToRecentDocs 追踪 + 固定项存 ISettingsService。

---

### 3.6 shell-notification（★ 新建）

**来源**：`shell-dock/NewAppsNotificationWindow.cs` + DockPlugin 的 60 秒轮询。

**职责**：
- 订阅 IAppSourceService 的 AppSourceChanged 事件，检测新装应用并弹窗。
- 提供通用通知弹窗能力（毛玻璃，走 PluginHostWindow）。
- 不再由 Dock 轮询。

**契约**：
```csharp
public interface INotificationService
{
    void Show(NotificationDescriptor descriptor);
    void ShowNewAppsNotification(IReadOnlyList<AppItem> newApps);
}
```

---

### 3.7 shell-start-menu（改造）

**角色**：`shell.start`（开始菜单默认提供者）。

**现状**：Direct-Use Open-Shell `StartMenuDLL.dll`（对齐 TTB 直接复用原生 `ExplorerTAP.dll` 的路线）——
`StartMenuBridge` 以 `LoadLibrary` + `GetProcAddress`（C++ 修饰名）加载安装在程序目录 `external/openshell/`
的 Open-Shell 能力 DLL，`InitManagers(false)` 完成对 explorer 的注入接管，菜单本体由 DLL 渲染。

**目标**：Direct-Use Open-Shell 开始菜单为**默认提供者**（系统级保真：开始按钮/ Win 键 / 多屏 / 热角全对），
自绘 WPF 菜单保留为**可选后端**（设置开关 `startmenu.use-native-ttb=false` 时启用，含熔断回退能力）。

**内部分块**（插件内部类，不拆包）：

| 类 | 职责 |
|---|---|
| `StartMenuPlugin` | IPlugin 入口，Provide IStartMenuService，注册设置分区 |
| `StartMenuService` | 菜单开关状态、布局选择、数据聚合（inject app-source/search/recent/pinning） |
| `StartKeyHook` | Win 键低级钩子接管（WH_KEYBOARD_LL），发 IEventBus `shell.start.toggle` |
| `StartMenuWindow` | WPF 弹出窗口，实现 IShellPluginWindow，走 PluginHostWindow |

**对外扩展点（Contracts/）**：
```csharp
public interface IStartMenuSectionProvider   // 第三方注入菜单栏目
{
    string SectionId { get; }
    string Title { get; }
    int Order { get; }
    object? BuildSection(IStartMenuContext context);
}

public interface IStartMenuLayoutProvider    // 第三方提供菜单布局（经典/双列/Metro/Launchpad）
{
    string LayoutId { get; }
    string DisplayName { get; }
    Control CreateLayout(IStartMenuContext context);
}
```

**Direct-Use 策略（默认）**：
- `StartMenuBridge`（复用 Open-Shell `StartMenuDLL.dll`）为**默认提供者**：`settings.startmenu.use-native-ttb` 默认 `true`，
  `Enable()` 即 `LoadLibrary` + `InitManagers(false)` 注入接管开始按钮。
- 能力 DLL 定位优先级：程序目录 `external/openshell/StartMenuDLL.dll` → 注册表安装目录 → `C:\Program Files\Open-Shell`。
  DLL 缺失时优雅降级（`IsDllAvailable=false`，设置面板橙色提示，切自绘）。
- 自绘 WPF 菜单作为**可选后端**：开关 `startmenu.use-native-ttb=false` 启用；其窗口连续崩溃 3 次 → 熔断 → 自动切回 Open-Shell（M14）。

**与任务01（shell-app-center）的关系**：
- 原规划的 `shell-app-center` 不再独立建包。
- AppGrabberWindow 的"全应用浏览"能力由开始菜单接管（开始菜单本身就是应用浏览器）。
- Launchpad（全屏启动台网格）作为 `IStartMenuLayoutProvider` 的一种布局实现，留在 shell-start-menu 内。
- NewAppsNotification 迁入 shell-notification。
- 任务01 文档标记为"被本方案取代"。

---

### 3.8 shell-dock（瘦身）

**保留**：
- `DockWindow.xaml.cs` / `Controls/DockItem.xaml.cs`：Dock 栏 UI
- `DockLayoutService` / `DockVisualSettings`：Dock 布局与视觉
- `DockIconService`：Dock 图标渲染（依赖 IAppIconService）
- `DockService` / `IDockService`

**迁出**：

| 文件 | 去向 |
|---|---|
| `Services/RunningAppDetector.cs` | shell-window-tracker |
| `Services/DwmThumbnail.cs` / `DwmThumbnailInterop.cs` / `DockThumbWindow.cs` | shell-window-tracker |
| `Services/DockPinnedService.cs` | shell-pinning |
| `Services/DockAppsService.cs` | 拆分：固定部分→shell-pinning；扫描部分删除（用 IAppSourceService） |
| `AppGrabberWindow.xaml.cs` | 废弃（由开始菜单接管） |
| `NewAppsNotificationWindow.cs` | shell-notification |
| `FolderInputWindow.cs` | 随 AppGrabber 废弃 |

**瘦身后 DockPlugin 的 Inject**：
```csharp
public IReadOnlyList<Type> Inject => new[]
{
    typeof(IVibrancyService),
    typeof(IAppearanceService),
    typeof(IAppSourceService),
    typeof(IAppIconService),
    typeof(IWindowTrackerService),   // 新
    typeof(IPinningService),         // 新
};
```

---

## 四、服务图依赖关系

```
shell-app-source ─────────────────────────────┐
   │ Provide IAppSourceService, IAppIconService│
   ▼                                          │
shell-window-tracker                          │
   │ Provide IWindowTrackerService            │
   │ (枚举窗口时用 IAppSourceService 关联)     │
   ▼                                          ▼
shell-pinning ──────────► shell-search ◄── shell-recent
   │ Provide IPinningService     │ inject IAppSourceService
   │ (持久化独立)                │ Provide IStartMenuSearchService
   ▼                            │
shell-notification             │
   │ inject IAppSourceService   │
   ▼                            ▼
        ┌─────────────────────────────────┐
        │      shell-start-menu           │
        │  inject: app-source, icon,      │
        │    window-tracker, pinning,     │
        │    search, recent, vibrancy,    │
        │    appearance, settings         │
        └─────────────────────────────────┘
        ┌─────────────────────────────────┐
        │      shell-dock（瘦身）          │
        │  inject: app-source, icon,      │
        │    window-tracker, pinning,     │
        │    vibrancy, appearance         │
        └─────────────────────────────────┘
```

依赖方向：UI 层 → 基础服务层 → shell-core/kernel，不反向。

---

## 五、Win 键 / 开始按钮接管

| Open-Shell 做法 | Cordis 做法 | 位置 |
|---|---|---|
| WH_KEYBOARD_LL 拦截 Win 键 | C# SetWindowsHookEx(WH_KEYBOARD_LL)，收口到 StartKeyHook | shell-start-menu 内部 |
| Subclass 原生任务栏拦截点击 | 不 hook 原生任务栏；Dock/任务栏的开始按钮点击 → IEventBus.Emit("shell.start.toggle") | Dock/Taskbar 发事件 |
| 抑制原生开始菜单 | Win 键钩子拦截后不 CallNextHookEx | StartKeyHook |

Win 键钩子回调内必须 try-catch，异常绝不冒泡（M10）。未来若内核提供 IInputHookService，本能力上移到内核（登记为后续事项）。

---

## 六、Open-Shell 模块处置总表

| Open-Shell 源文件 | 处置 | 去向 |
|---|---|---|
| ProgramsTree.cpp | C# 重写 | shell-app-source 增强 GetProgramTree |
| ItemManager.cpp | 拆分 | 数据→app-source；排序/分组→start-menu 内部 |
| MetroLinkManager.cpp | 复用 | ManagedShell.UWPInterop + app-source |
| SearchManager.cpp | C# 重写 | shell-search |
| JumpLists.cpp | C# 重写 | shell-recent |
| CustomMenu.cpp | C# 重写 | shell-start-menu + IStartMenuSectionProvider |
| MenuCommands.cpp | 复用 | kernel IPowerManagement + Win32 对话框 |
| MenuContainer.cpp | 丢弃重写 | WPF ShellWindow/PluginHostWindow |
| MenuPaint.cpp | 丢弃 | WPF + IAppearanceService 主题令牌 |
| SkinManager.cpp | 丢弃 | cordis 主题系统 |
| StartButton.cpp | C# 重写 | StartKeyHook + IEventBus |
| SettingsUI.cpp | 丢弃 | ISettingsSectionRegistry |
| DragDrop.cpp | 后期重写 | WPF DragDrop（P3） |
| Accessibility.cpp / TouchHelper.cpp | 丢弃 | WPF 内置 |
| StartMenuDLL.dll（原生能力 DLL） | 直接复用（Direct-Use，默认） | 程序目录 external/openshell/+ StartMenuBridge.cs P/Invoke |

---

## 七、合规检查

| 红线/机制 | 合规点 |
|---|---|
| ADR-001 D4 | Open-Shell 是 C++ 参考源码，只参考行为算法，C# 重写，不复制代码/资源 |
| M4 | 借用的原生结构体（HOOKPROC、COM 接口布局等）逐条登记白名单 |
| M5 | 全部走 IContext.Plugin/provide/inject，不引入第二套插件机制 |
| M6 | 窗口用 ShellWindow/PluginHostWindow，颜色/字号/圆角走 IThemeTokens 语义令牌 |
| M7 | 消除 DockAppsService 与 IAppSourceService 重复扫描；通用服务独立成包 |
| M8 | 扩展点（IStartMenuSectionProvider/IStartMenuLayoutProvider/ISearchResultProvider/IPinningService zone）写在 Contracts/ 并在包 README 声明 |
| M10 | 所有服务方法 try-catch + 降级上报；Win 键钩子异常不冒泡；文件搜索失败降级 |
| M14 | shell.start 是角色插槽，Open-Shell Direct-Use 为默认提供者；自绘 WPF 为可选后端，3 次崩溃熔断切回 Open-Shell |
| L0/L1 隔离 | UI/搜索/钩子为 L0（PluginGuard）；文件索引如需进程外可声明 L1 |

---

## 八、非目标（本期不做）

- 不做 ClassicExplorer / ClassicIE（不在开始菜单范围）。
- 不做 Open-Shell 的 .skin 皮肤格式支持（用 cordis 主题系统）。
- 不做 32 位 thunk helper（仅 net8.0-windows x64）。
- 不做任务栏外观替换（Taskbar 包已有 TaskbarAppearanceEngine，与开始菜单无关）。
- 不在本期做 shell-taskbar / shell-menu-bar / shell-desktop 的完整实现，只保证服务层能被它们未来 inject。
