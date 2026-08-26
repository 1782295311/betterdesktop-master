# Open-Shell-Menu 集成与包结构重组 · 步骤与操作文档

> 日期：2026-08-25
> 配套目标文档：[OPEN_SHELL_INTEGRATION_GOALS.md](OPEN_SHELL_INTEGRATION_GOALS.md)
> 原则：小步快跑，每步独立提交、独立可运行、Dock 行为不回退。

---

## 总览：8 个步骤

> 状态图例：✅ 已落地并构建验证 · ◻ 剩余待办

| 步骤 | 内容 | 风险 | 依赖 | 状态 |
|------|------|------|------|------|
| 1 | 新建 `shell-window-tracker`，从 Dock 抽出运行检测 | 低 | 无 | ✅ |
| 2 | 新建 `shell-pinning`，从 Dock 抽出固定服务 | 低 | 无 | ✅ |
| 3 | 增强 `shell-app-source`：程序树 + 变化事件 | 中 | 无 | ✅ |
| 4 | 新建 `shell-notification`，迁出新装通知 | 低 | 步骤3 | ✅ |
| 5 | DWM 缩略图下沉到 window-tracker | 低 | 步骤1 | ✅ |
| 6 | 新建 `shell-search` + `shell-recent` | 中 | 步骤3 | ✅ |
| 7 | 改造 `shell-start-menu`：自绘窗口骨架 + Win 键钩子 | 中 | 步骤1-4 | ✅ |
| 8 | 废弃 AppGrabberWindow，开始菜单接管全应用浏览 | 中 | 步骤7 | ✅ |

> 8 步均已落地：`host/Bootstrap.cs` 已按依赖序注册全部基础服务与 UI 插件，`shell-app-center` 不再独立建包。剩余非阻塞项见 [GOALS 二·五](OPEN_SHELL_INTEGRATION_GOALS.md)——TTB 真实注入验证与 M6 主题收口。

**每步完成后必须跑**：
```powershell
dotnet build host/BetterDesktop.Host.csproj -c Debug -p:Platform=x64 -p:UseSharedCompilation=false -nodeReuse:false -m:1
```
要求 0 警告 0 错误（基线门禁）。

---

## 步骤 1：新建 shell-window-tracker（P0）

### 目标
把 `RunningAppDetector` 从 Dock 抽出为独立服务包，Dock 改为 inject 它。Dock 行为完全不变。

### 新建包

目录：`packages/shell/shell-window-tracker/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.WindowTracker.csproj` | TFM `net8.0-windows10.0.19041.0`；引用 kernel、shell-app-source（用 AppItem/AppItemId）；UseWPF=true（DWM 缩略图需要） |
| `README.md` | 按 packages/README.md 标准写：职责/依赖/扩展点/Known Limitations |
| `WindowTrackerPlugin.cs` | `IPlugin`，Name=`"shell.window-tracker"`，Inject=空；LoadAsync 中创建 WindowTrackerService 并 Provide |
| `Contracts/IWindowTrackerService.cs` | 服务接口（见下） |
| `Contracts/RunningAppInfo.cs` | 运行中应用模型 |
| `Contracts/WindowInfo.cs` | 单个窗口模型（Hwnd/Title/Pid/IsMinimized） |
| `Services/WindowTrackerService.cs` | 实现：枚举 + WinEvent 钩子 + 事件防抖 |
| `Native/RunningAppDetector.cs` | 从 Dock 原样搬入（internal static），命名空间改为 `BetterDesktop.Shell.WindowTracker.Native` |
| `Native/WinEventHook.cs` | 新建：SetWinEventHook 监听 EVENT_OBJECT_CREATE/DESTROY/FOREGROUND/NAMECHANGE |

### 关键接口

```csharp
namespace BetterDesktop.Shell.WindowTracker.Contracts;

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

### 操作

1. 建 csproj，引用 `BetterDesktop.Kernel`、`BetterDesktop.Shell.AppSource`（Contracts 用 AppItemId）。
2. 把 `shell-dock/Services/RunningAppDetector.cs` 复制到 `shell-window-tracker/Native/`，改命名空间，保持 internal。
3. 写 `WindowTrackerService`：
   - 内部用 `RunningAppDetector.GetRunningWindows()` 枚举。
   - 用 `RunningAppDetector` 已有的 ExePath 匹配 AppItem（通过 IAppSourceService.ResolveFromPath 或 TargetPath 比对）。
   - 启动 WinEvent 钩子，窗口变化时防抖 500ms 后重新枚举并发 RunningAppsChanged。
   - 所有方法 try-catch，异常记日志不冒泡（M10）。
4. `WindowTrackerPlugin.LoadAsync`：`context.Provide<IWindowTrackerService>(new WindowTrackerService(context.Get<IAppSourceService>(), context.Logger))`。
5. 加入解决方案（`Cairo Desktop.sln` 或 cordis 的 sln——以实际为准）。
6. 在 `host/Bootstrap.cs` 中 AppSourcePlugin 之后、DockPlugin 之前注册：
   ```csharp
   context.Plugin(new BetterDesktop.Shell.WindowTracker.WindowTrackerPlugin());
   ```

### 修改 shell-dock

- `DockPlugin.cs`：
  - Inject 数组加 `typeof(IWindowTrackerService)`。
  - 删除对 `RunningAppDetector` 的直接调用，改用 `_windowTracker.GetRunningApps()` / `IsRunning()`。
  - LoadAsync 中 `context.Get<IWindowTrackerService>()`。
- 删除 `shell-dock/Services/RunningAppDetector.cs`。
- csproj 加 ProjectReference 到 WindowTracker（只引用 Contracts 所在程序集）。

### 验收

- [ ] 构建 0 警告 0 错误
- [ ] Dock 运行指示器正常显示/消失（打开/关闭记事本验证）
- [ ] 点击 Dock 图标能激活已运行窗口
- [ ] 卸载 window-tracker 插件后 Dock 停在 Pending（依赖未满足），不崩溃

---

## 步骤 2：新建 shell-pinning（P0）

### 目标
把 Dock 的固定/收藏能力抽成通用服务，按 zone 分区。兼容旧 `dock-pinned.json`。

### 新建包

目录：`packages/shell/shell-pinning/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.Pinning.csproj` | TFM net8.0-windows；引用 kernel、shell-app-source |
| `README.md` | 标准四小节 |
| `PinningPlugin.cs` | IPlugin，Provide IPinningService |
| `Contracts/IPinningService.cs` | 服务接口 |
| `Contracts/PinnedItem.cs` | 固定项模型（AppItemId + Zone + Order） |
| `Contracts/PinnedChangedEventArgs.cs` | 变更事件参数 |
| `Services/PinningService.cs` | 实现：JSON 持久化 + zone 分组 + 旧文件迁移 |

### 关键接口

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

### 操作

1. 持久化文件：`%APPDATA%/BetterDesktop/pinning.json`，结构 `{ "dock": [...], "startmenu": [...], "taskbar": [...] }`。
2. **迁移逻辑**：启动时若 `pinning.json` 不存在但旧 `dock-pinned.json` 存在，读取旧文件写入 `"dock"` zone，然后保留旧文件（不删除，备份）。
3. `PinningService` 注入 IAppSourceService 用于把 AppItemId 解析回 AppItem（Pin 时只存 Id，GetPinned 时联查显示信息）。
4. Bootstrap.cs 在 WindowTrackerPlugin 之后注册。

### 修改 shell-dock

- `DockPinnedService.cs` 删除，其职责由 IPinningService("dock") 承担。
- `DockAppsService`：
  - 删除 AddByPath/RemoveById/Reorder/Pinned/PinnedChanged（迁到 pinning）。
  - 删除 ScanStartMenu/ScanInstalledApps/ScanAllPrograms/GetNewlyInstalledApps（步骤3 统一到 app-source）。
  - 本步骤先保留 DockAppsService 作为 Dock 内部的门面（聚合 app-source + pinning），步骤3 再清理。
- `DockPlugin`：
  - Inject 加 `typeof(IPinningService)`。
  - 固定项读写改调 `_pinning.GetPinned("dock")` / Pin/Unpin/Reorder。
  - 订阅 `PinnedChanged`，zone=="dock" 时刷新 Dock。

### 验收

- [ ] 构建 0/0
- [ ] Dock 固定/取消固定/拖拽排序正常
- [ ] 重启后固定项不丢失（旧 dock-pinned.json 自动迁移）
- [ ] 手动往 pinning.json 写 "startmenu" zone 项不影响 Dock

---

## 步骤 3：增强 shell-app-source（P0）

### 目标
加程序树（层级）和变化通知事件；删除 DockAppsService 的重复扫描代码。

### 修改 shell-app-source

| 文件 | 操作 |
|------|------|
| `Contracts/IAppSourceService.cs` | 加 `GetProgramTree()`、`event EventHandler? AppSourceChanged`、`ScanAllPrograms()` |
| `Models/ProgramFolder.cs` | 新建：文件夹层级模型 |
| `Services/AppSourceService.cs` | 实现 GetProgramTree（递归构建文件夹树）；加 FileSystemWatcher 监控两个开始菜单目录；防抖 1s 发 AppSourceChanged；加 ScanAllPrograms（从 DockAppsService 搬入） |
| `Services/StartMenuWatcher.cs` | 新建：FileSystemWatcher 封装（监控创建/删除/改名） |

### 关键接口变更

```csharp
public interface IAppSourceService
{
    IReadOnlyList<AppItem> ScanStartMenu();
    IReadOnlyList<AppItem> ScanInstalledApps();
    IReadOnlyList<AppItem> ScanAllPrograms();              // 新增
    ProgramFolder GetProgramTree();                         // 新增
    IReadOnlyList<AppItem> GetNewlyInstalledApps();
    void MarkAppsSeen(IReadOnlyList<AppItem> apps);
    AppItem? ResolveFromPath(string path);
    void InvalidateCache();
    event EventHandler? AppSourceChanged;                   // 新增
}
```

### 操作

1. GetProgramTree：遍历开始菜单目录，按子目录递归构建 ProgramFolder 树，.lnk 解析为 AppItem（复用 ShellLinkResolver）。
2. FileSystemWatcher：
   - 监控 `%APPDATA%\Microsoft\Windows\Start Menu\Programs` 和 `%ProgramData%\...\Programs`。
   - NotifyFilter = FileName | DirectoryName | LastWrite。
   - Created/Deleted/Renamed 事件 → 防抖 1s → InvalidateCache() → 触发 AppSourceChanged。
3. ScanAllPrograms 从 DockAppsService 搬入（遍历常见程序目录，排除系统目录）。

### 修改 shell-dock

- `DockAppsService` 删除所有 Scan* 方法，改为转发 IAppSourceService。
- `DockPlugin`：
  - 订阅 `AppSourceChanged`，刷新 Dock 固定项图标/名称。
  - CheckNewApps 改调 IAppSourceService.GetNewlyInstalledApps()（步骤4 会把通知本身迁走）。
- 新增应用通知逻辑暂留 DockPlugin，步骤4 迁出。

### 验收

- [ ] 构建 0/0
- [ ] Dock 应用列表正常
- [] 安装/卸载程序后 Dock 自动刷新（无需重启）
- [ ] GetProgramTree 返回正确的文件夹层级（手动单元测试或日志验证）

---

## 步骤 4：新建 shell-notification（P1）

### 目标
把新装应用通知从 Dock 独立出来。

### 新建包

目录：`packages/shell/shell-notification/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.Notification.csproj` | 引用 kernel、shell-core、shell-app-source |
| `README.md` | 标准四小节 |
| `NotificationPlugin.cs` | IPlugin，订阅 IAppSourceService.AppSourceChanged |
| `Contracts/INotificationService.cs` | Show/ShowNewAppsNotification |
| `Contracts/NotificationDescriptor.cs` | 通知模型（标题/正文/图标/回调） |
| `Services/NotificationService.cs` | 实现 |
| `Windows/NewAppsNotificationWindow.xaml(.cs)` | 从 shell-dock 搬入，改命名空间 |

### 操作

1. 把 `shell-dock/NewAppsNotificationWindow.cs` 搬到 notification 包，改命名空间和 using。
2. NotificationPlugin.LoadAsync：
   - inject IAppSourceService。
   - 订阅 AppSourceChanged，防抖后调 GetNewlyInstalledApps，有新增则 ShowNewAppsNotification。
   - 启动时后台首查（从 DockPlugin.CheckNewApps 搬入）。
3. DockPlugin：
   - 删除 `_newAppsTimer`、`CheckNewApps`、`NotifyNewApps`、`_newAppsNotification`。
   - 删除 NewAppsNotificationWindow.cs。
4. Bootstrap.cs 注册 NotificationPlugin。

### 验收

- [ ] 构建 0/0
- [ ] 新装程序后右下角弹窗正常
- [ ] 卸载 Dock 后新装通知仍能弹出（通知不依赖 Dock）
- [ ] 重复安装同一程序不重复弹（已见机制正常）

---

## 步骤 5：DWM 缩略图下沉（P1）

### 目标
把 DwmThumbnail 从 Dock 搬到 window-tracker，供任务栏/开始菜单未来使用。

### 操作

1. 把以下文件从 shell-dock 搬到 shell-window-tracker：
   - `Services/DwmThumbnail.cs`
   - `Services/DwmThumbnailInterop.cs`
   - `DockThumbWindow.cs` → 重命名为 `ThumbnailWindow.cs`（通用化，去掉 Dock 命名）
2. 在 IWindowTrackerService 加缩略图注册方法（或新建 `IWindowThumbnailService` 由 WindowTrackerPlugin 同时 Provide）：
   ```csharp
   public interface IWindowThumbnailService
   {
       IntPtr RegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource);
       void UpdateThumbnail(IntPtr thumbnail, Rect destinationRect, byte opacity);
       void UnregisterThumbnail(IntPtr thumbnail);
   }
   ```
3. DockWindow 中调用 DwmThumbnail 的地方改为 inject IWindowThumbnailService。
4. 命名空间统一为 `BetterDesktop.Shell.WindowTracker.Thumbnail`。

### 验收

- [ ] 构建 0/0
- [ ] Dock 鼠标悬停缩略图预览正常
- [ ] 缩略图位置/透明度/尺寸正常

---

## 步骤 6：新建 shell-search + shell-recent（P1）

### 目标
建立搜索和最近项两个通用服务包。这两个包可并行开发。

### 6a. shell-search

目录：`packages/shell/shell-search/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.Search.csproj` | 引用 kernel、shell-app-source |
| `SearchPlugin.cs` | Provide IStartMenuSearchService，注册内置 Provider |
| `Contracts/IStartMenuSearchService.cs` | SearchAsync + RegisterProvider |
| `Contracts/ISearchResultProvider.cs` | 搜索源扩展点 |
| `Contracts/SearchResult.cs` | 统一结果模型（名称/图标/类型/执行动作/Category） |
| `Services/StartMenuSearchService.cs` | 聚合所有 Provider，子序列匹配排序 |
| `Services/ProgramSearchProvider.cs` | 内存模糊匹配 IAppSourceService |
| `Services/SettingsSearchProvider.cs` | ms-settings: 枚举（硬编码约 80 项 canonical name） |
| `Services/FileSearchProvider.cs` | Windows Search API（ISearchQueryHelper），try-catch 降级 |

**实现要点**：
- 子序列匹配算法参考 Open-Shell `SearchManager`：查询字符按顺序出现在名称中即匹配，连续匹配加分，首字母匹配加分。
- FileSearchProvider 用 `ISearchQueryHelper`（OLE DB 连接 Windows Search 索引），失败时记日志并返回空结果（降级，不抛异常）。
- SearchAsync 接受 CancellationToken，输入防抖由 UI 层做。

### 6b. shell-recent

目录：`packages/shell/shell-recent/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.Recent.csproj` | 引用 kernel、shell-app-source |
| `RecentPlugin.cs` | Provide IRecentItemsService |
| `Contracts/IRecentItemsService.cs` | 接口 |
| `Contracts/RecentItem.cs` | 模型 |
| `Services/RecentItemsService.cs` | SHAddToRecentDocs 追踪 + 固定项持久化 |

**实现要点**：
- 最近文档：读 `%APPDATA%\Microsoft\Windows\Recent` 目录 + SHAddToRecentDocs 通知。
- 最近程序：维护本地使用计数（监听窗口前台变化，注入 IWindowTrackerService）。
- 跳转列表固定项存 ISettingsService（key: `recent.jumplist.{appId}`）。

### Bootstrap 注册

在 WindowTracker/Pinning 之后注册 Search、Recent。

### 验收

- [ ] 构建 0/0
- [ ] 单元测试：ProgramSearchProvider 输入"not"能匹配"Notepad"
- [ ] FileSearchProvider 在 Windows Search 服务禁用时不崩溃，返回空
- [ ] RecentItemsService 能记录和返回最近程序

---

## 步骤 7：改造 shell-start-menu（P1）

### 目标
从纯 TTB 桥接改为 WPF 自绘菜单骨架，TTB 保留为回退。本步骤只做骨架 + 经典单列布局 + Win 键。

### 修改包

目录：`packages/shell/shell-start-menu/`（已有）

| 文件 | 操作 |
|------|------|
| `StartMenuPlugin.cs` | 改造：Inject 加所需服务；创建 StartMenuService 并 Provide；创建 StartKeyHook；TTB 桥保留 |
| `Services/StartMenuService.cs` | 新建：菜单状态、布局管理、数据聚合 |
| `Services/StartKeyHook.cs` | 新建：WH_KEYBOARD_LL 拦截 Win 键 |
| `Services/StartMenuBridge.cs` | 保留（现有 TTB 桥） |
| `Windows/StartMenuWindow.xaml(.cs)` | 新建：WPF 弹出窗口，IShellPluginWindow |
| `Windows/Layouts/ClassicLayout.xaml(.cs)` | 新建：单列经典布局 |
| `Contracts/IStartMenuService.cs` | 已有，扩展 Toggle/Show/Hide/OpenStateChanged |
| `Contracts/IStartMenuSectionProvider.cs` | 新建：栏目扩展点 |
| `Contracts/IStartMenuLayoutProvider.cs` | 新建：布局扩展点 |
| `Sections/StartMenuSection.cs` | 已有，加"使用经典 Open-Shell 菜单"开关 |

### 关键实现约束

1. **窗口**：
   - 继承 PluginHostWindow 或走 IShellPluginWindow（参考 shell-core 的 PluginHostWindow.cs）。
   - 单例 Hide/Show，不每次 new。
   - 定位：贴开始按钮上方，多显示器/DPI 感知（参考 Open-Shell MenuContainer 的定位逻辑，但用 WPF 重写）。
   - Deactivated 关闭，不用低级鼠标钩子。
   - 毛玻璃用 IVibrancyService / IAppearanceService，不自己写 DWM。
2. **Win 键钩子**：
   - SetWindowsHookEx(WH_KEYBOARD_LL)。
   - 拦截 VK_LWIN/VK_RWIN（单独按下时切换菜单；组合键如 Win+R/Win+D 放行 CallNextHookEx）。
   - 钩子回调内 try-catch，异常绝不冒泡。
   - 菜单打开时按 Win 键关闭。
3. **Open-Shell Direct-Use（默认）**：
   - StartMenuBridge 复用原生 `StartMenuDLL.dll`（LoadLibrary + 修饰名 P/Invoke，对齐 TTB 复用 ExplorerTAP）。
   - 能力 DLL 已 staging 到程序目录 `external/openshell/`；`startmenu.use-native-ttb` **默认 true**。
   - 自绘窗口创建连续失败 3 次 → 熔断 → 自动切 Open-Shell（M14）。
4. **经典单列布局**：
   - 程序树（IAppSourceService.GetProgramTree）渲染为可展开文件夹列表。
   - 点击程序：IWindowTrackerService.Activate（已运行）或 IAppSourceService 启动。
   - 键盘导航：上下箭头、回车、Esc 关闭。
   - 样式全部 DynamicResource 走 IThemeTokens，不硬编码颜色/字号/圆角。

### Bootstrap 注册

StartMenuPlugin 已在 Bootstrap.cs 注册（第126行），确认其 Inject 依赖在它之前注册：
AppSourcePlugin → WindowTrackerPlugin → PinningPlugin → SearchPlugin → RecentPlugin → SettingsPlugin → StartMenuPlugin。

### 验收

- [ ] 构建 0/0
- [ ] 按 Win 键弹出自绘菜单，再按关闭
- [ ] 菜单显示程序列表（文件夹可展开）
- [ ] 点击程序启动/激活
- [ ] 点击菜单外部关闭（Deactivated）
- [ ] 毛玻璃/圆角/主题色正常
- [ ] 设置"使用经典 Open-Shell 菜单"开关能切回 TTB 菜单
- [ ] 自绘窗口异常时不崩溃（熔断回退 TTB）

---

## 步骤 8：废弃 AppGrabber，开始菜单接管全应用浏览（P2）

### 目标
AppGrabberWindow 的能力由开始菜单 + 搜索接管，删除冗余窗口。

### 操作

1. 在开始菜单加"所有应用"视图（IStartMenuLayoutProvider 的第二种布局，或经典布局内的搜索结果视图）：
   - 搜索框输入时显示 IStartMenuSearchService 结果（程序/设置/文件分组）。
   - 右键程序项菜单：固定到 Dock（IPinningService.Pin("dock")）、固定到开始菜单（Pin("startmenu")）、固定到任务栏（Pin("taskbar")）、以管理员运行、打开文件位置、卸载。
2. Launchpad 全屏网格布局作为第三种 IStartMenuLayoutProvider（可从 Dock 上的启动台按钮触发，发 IEventBus 事件让开始菜单以 Launchpad 布局显示）。
3. 删除 shell-dock 中的：
   - `AppGrabberWindow.xaml.cs`
   - `FolderInputWindow.cs`
   - DockPlugin 中 `_grabberWindow` / `ShowAppGrabber()` 相关代码。
4. Dock 上原"打开应用管理中心"入口改为 IEventBus `shell.start.toggle`（已在 Dock 右键"开始菜单"项落地；与 Win 键钩子共用同一契约）。
5. 更新 `docs/实现拆解/任务01-shell-app-center拆分.md`，标记为"被 Open-Shell 集成方案取代"。

### 验收

- [ ] 构建 0/0
- [ ] 开始菜单搜索能找到程序并启动
- [ ] 右键"固定到 Dock"后 Dock 实时出现该图标
- [ ] AppGrabberWindow 不再被引用（全局搜索无残留）
- [ ] Dock 上应用中心入口能打开开始菜单的所有应用视图

---

## 附录 A：命名空间与 csproj 模板

### csproj 模板（新服务包）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <RootNamespace>BetterDesktop.Shell.$(MSBuildProjectName.Replace('BetterDesktop.Shell.',''))</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\kernel\kernel\BetterDesktop.Kernel.csproj" />
  </ItemGroup>
</Project>
```

> 注意：RootNamespace 需按实际包名调整（如 WindowTracker 对应 `BetterDesktop.Shell.WindowTracker`）。

### 命名空间约定

| 包 | 命名空间前缀 |
|----|-------------|
| shell-window-tracker | `BetterDesktop.Shell.WindowTracker` |
| shell-pinning | `BetterDesktop.Shell.Pinning` |
| shell-search | `BetterDesktop.Shell.Search` |
| shell-recent | `BetterDesktop.Shell.Recent` |
| shell-notification | `BetterDesktop.Shell.Notification` |

---

## 附录 B：Bootstrap.cs 注册顺序（最终）

```csharp
// 1. 内核基础（已有）
context.Plugin(new PowerManagement());
context.Plugin(new TimerService());
context.Plugin(new VibrancyService());
// HMR（已有）

// 2. 应用数据来源（已有，步骤3增强）
context.Plugin(new AppSourcePlugin());

// 3. 基础服务（新建）
context.Plugin(new WindowTrackerPlugin());    // 步骤1
context.Plugin(new PinningPlugin());          // 步骤2
context.Plugin(new NotificationPlugin());     // 步骤4
context.Plugin(new SearchPlugin());           // 步骤6
context.Plugin(new RecentPlugin());           // 步骤6

// 4. UI 插件
context.Plugin(new DockPlugin());             // 瘦身后
context.Plugin(new SettingsPlugin());         // 已有
context.Plugin(new TaskbarAppearancePlugin());// 已有
context.Plugin(new StartMenuPlugin());        // 步骤7改造
```

---

## 附录 C：风险与回滚

| 风险 | 缓解 |
|------|------|
| 旧 dock-pinned.json 迁移失败 | 迁移前备份为 dock-pinned.json.bak；迁移失败则回退到空固定列表并记日志，不崩 |
| Win 键钩子影响系统快捷键 | 只拦截单独 Win 键，组合键一律 CallNextHookEx 放行；钩子异常立即卸载 |
| FileSystemWatcher 事件风暴 | 防抖 1s；多次变化只刷新一次 |
| Windows Search API 不稳定 | FileSearchProvider 整体 try-catch，失败返回空并降级上报 |
| 自绘开始菜单崩溃 | 3 次熔断回退 TTB；StartKeyHook 异常时菜单不弹但不影响 Shell |
| 包迁移期间引用断裂 | 每步只搬一类能力，搬完即构建验证；不跨步骤混合改动 |

---

## 附录 D：对既有任务拆解文档的影响

- `docs/实现拆解/任务01-shell-app-center拆分.md`：**作废**，AppCenter 不独立建包，由开始菜单 + notification 取代。
- `docs/实现拆解记录.md`：任务清单需更新，任务1替换为本方案的步骤1-8。
- 任务02-05（status/menu-bar/context-menu/控制中心）不受影响，但 menu-bar 的"打开应用中心"命令改为发 `shell.start.toggle` 事件。
