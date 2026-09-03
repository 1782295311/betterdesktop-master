# shell-context-menu（右键菜单插件 · 设计稿）

> 状态：设计草稿（尚未实现）。
> 右键菜单是**独立硬组件**，不能"随便设计就直接过"——它跨多个表面、要接 Windows Shell 的 COM 菜单、处理消息泵转发与定位/关闭语义。本文按完整深度结构设计。
> **分场景界面与功能布局见 [`MENU-SPECS.md`](MENU-SPECS.md)**（11 个触发场景各自的菜单结构/分组/置灰/动态项/弹层形态）。

## 1. 目标与边界

**做什么**：统一桌面环境所有表面的右键菜单——桌面空白处、任务栏、Dock 项、窗口标题栏扩展、以及第三方插件贡献的菜单项。
**不做什么**：
- 不接管系统资源管理器文件右键菜单的完整实现（原生 Shell 菜单优先，保证第三方扩展项完整）。
- 不做 MenuBar（顶栏）、不做任务栏缩略图。
- 不替换系统 Shell（吸取 FrostedShell 教训：插件贡献"能力"，不替换"宿主"）。

## 2. 为什么必须单独设计（难点清单）

1. **跨表面作用域**：`desktop` / `taskbar` / `dock.item` / `shell.file` 各自菜单不同，需统一路由。
2. **COM 集成**：文件/文件夹右键要走 `IContextMenu`，且 `IContextMenu2/3` 要求把 `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MENUCHAR` 转发回菜单对象——消息泵处理错即崩溃。
3. **定位语义**：菜单必须锚定在鼠标位置/目标矩形，且 DPI、多显示器、屏幕边缘反转都要正确。
4. **关闭语义**：Esc、外部点击、焦点丢失、再次右键都要幂等关闭，且 COM 句柄必须 Release（防泄漏）。
5. **插件贡献排序**：多个插件抢同一作用域时要有确定性优先级，不能靠注册顺序碰运气。
6. **主题适配**：菜单外观走 ThemeCenter 令牌，但不能阻塞菜单弹出（令牌就绪前用默认样式）。

## 3. 架构

```
shell-context-menu (Extension)
├── MenuService            # 构建/展示/关闭的统一入口（所有表面共用）
├── MenuHost               # 弹层渲染：WPF Popup / WebView2 overlay（抽象层，二选一）
├── ShellMenuAdapter       # Windows Shell 集成：IContextMenu/2/3 + IShellFolder + verb 枚举
├── FileClassifier         # ★ 文件属性精准识别：SFGAO 标志 + 扩展名 → FileKind/FileCapabilities
├── PluginMenuRegistry     # 插件贡献菜单项（IContextMenuContributor）+ 优先级排序
├── ThemeAdapter           # 通过 IThemeResourceProvider 取令牌，含默认回退
└── InputRouter            # 键盘导航 / Esc / 外部点击 / DPI / 多屏定位
```

依赖：`BetterDesktop.Shell.Core`（Surface / Windowing）、`theme-center`（令牌）、`BetterDesktop.Kernel`（事件总线）。

## 4. 领域模型

```csharp
enum MenuItemKind { Command, Submenu, Separator, Toggle, Radio }

record MenuItemDef(
    string Id,
    string Text,
    string? IconKey,
    MenuItemKind Kind,
    bool IsEnabled = true,
    bool IsChecked = false,
    ICommand? Command = null,
    IReadOnlyList<MenuItemDef>? Children = null,
    FileCapabilities RequiredCapability = 0);   // ★ 文件场景：所需文件能力，不满足则隐藏

enum MenuScope { Desktop, Taskbar, DockItem, ShellFile, Window }

record MenuRequest(MenuScope Scope, object? Target, Point ScreenPosition, Rect? AnchorRect);

interface IContextMenuContributor
{
    MenuScope Scope { get; }
    int Priority { get; }                       // 越大越靠前；同优先级按注册顺序稳定排序
    IReadOnlyList<MenuItemDef> Build(object? target);
}

// ★ 文件属性识别（用户要求：尽量不显示无法操作该文件的选项）
enum FileKind { Folder, Drive, File, Shortcut, Executable, Archive, Image, Video, Audio, Document, SourceCode, Config, SystemFile, InRecycleBin, Unknown }

[Flags] enum FileCapabilities
{
    Open, OpenInNewWindow, RunAsAdmin, OpenFileLocation, Edit, Print, Preview,
    Copy, Cut, PasteInto, Delete, Rename, Properties, Share, Extract,
    SetAsWallpaper, OpenWith, Restore, Browse, PinToDock
}

interface IFileClassifier
{
    FileIdentity Classify(string path);          // SFGAO 标志 + 扩展名 + 回收站/快捷方式目标解析
}
```

## 5. 内核集成

- `Name = "context-menu"`，`Inject: [typeof(IVibrancyService)]`（真实内核契约：无 ExtensionId/Manifest，依赖用 `Inject` 声明；`theme-center` 经 `IThemeResourceProvider` 可选消费）。
- 加载顺序：在 `shell-core` 之后，供所有 UI 插件调用。
- `LoadAsync(IContext)`：注册 `IMenuService` / 收集 `IContextMenuContributor`；挂全局右键路由（桌面/任务栏区域）。
- `UnloadAsync`：`Dismiss()` 所有活动菜单，释放全部 COM 句柄，再注销钩子。

## 6. 公共契约（语义）

```csharp
interface IMenuService
{
    Task<MenuResult> ShowAsync(MenuRequest request, CancellationToken ct = default);
    void Dismiss();                                   // 幂等：关闭全部活动菜单
    event EventHandler<MenuOpeningArgs> Opening;      // 展示前注入/修改菜单项
}

enum MenuResultKind { CommandExecuted, Cancelled, None }
record MenuResult(MenuResultKind Kind, string? ExecutedCommandId);
```

- `ShowAsync` 必须**异步且可取消**：菜单项可能来自 COM 枚举或插件异步构建，不能阻塞右键线程。
- `Dismiss()` 幂等，可在任意线程调用（内部 marshal 到 UI 线程）。
- `Opening` 事件允许插件在展示前增删改项，失败者仅被跳过，不影响整体。
- **文件能力过滤**：文件场景在 `Opening` 后、展示前，用 `IFileClassifier` 识别目标并**移除能力不满足的项**（`MenuItemDef.RequiredCapability`），满足"尽量不显示无法操作该文件的选项"（隐藏优先，少数受限项置灰见 MENU-SPECS §8.4）。

## 7. Shell 集成（核心难点）

**三档呈现**（文件/文件夹场景，执行均走 `IContextMenu.InvokeCommand`，仅呈现不同）：
- **native 档**：直接调用原生 `IContextMenu` 原样显示（注册表第三方扩展项完整），本插件只负责宿主窗口与消息泵转发；用于兼容/回退。
- **unified + full（完整模式）**：`IShellFolder.GetUIObjectOf` → 枚举 verb（`GetCommandString`）→ 转 `MenuItemDef` 重绘；**第三方工具项直接平铺**在主菜单，功能全覆盖。
- ~~**unified + grouped（收纳模式）**~~：**已于 2026-09-03 删除**（Win10 方针：第三方项一律平铺，低频项走 Shift 扩展，不做二级收纳、不设"展开/收起"切换）。配置键 `shell.displayMode` 与相关代码同步移除，**勿复活**。
- **文件属性精准识别**：`FileClassifier` 用 `IShellFolder.GetAttributesOf`（SFGAO）+ 扩展名 + 回收站/快捷方式目标解析，输出 `FileKind/FileCapabilities`；系统与第三方 verb 均按能力过滤（exe 不出现"编辑"，Archive 才出解压 verb）。
- 消息泵：宿主窗口在 `WndProc` 中将 `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM` / `WM_MENUCHAR` 转发给 `IContextMenu2/3`，这是"不能随便设计"的关键点。
- 完整菜单结构、分类策略、两模式切换见 [`MENU-SPECS.md §8`](MENU-SPECS.md)。

## 8. 配置

- `context-menu.ini`：`shell.integration = native | unified`（默认 `unified`）。
  （~~`shell.displayMode = full | grouped`~~ 已于 2026-09-03 删除，见 §模式 说明。）
- `filters.ini`：第三方 verb 分类关键词表（可覆盖默认类别规则）。
- 键盘导航、弹出延迟、动画开关；unified 模式外观由主题令牌接管。

## 9. 数据流

```
右键事件
  → InputRouter 判定作用域 + 屏幕位置
  → MenuService.ShowAsync
  → 插件 Contributors 构建（Priority 排序）+ 可选 ShellMenuAdapter 枚举
  → ThemeAdapter 取样式（令牌缺失回退默认）
  → MenuHost 定位（DPI/多屏/边缘翻转）
  → 展示；交互期间消息泵转发 COM 消息
  → 用户选择 / Esc / 外部点击 → Dismiss → 返回 MenuResult
  → 调用方执行命令
```

## 10. 跨插件协作

- `shell-dock`（Dock 项右键）与 `taskbar`（任务栏右键）**调用 `IMenuService`，绝不各自自绘菜单**。
- `theme-center` 提供令牌；`shell-core` 提供弹层宿主与 Vibrancy（菜单也可以毛玻璃）。
- FrostedShell 教训：本插件只贡献"菜单能力"，不替换系统 Shell。
- MenuHost 基于 `shell-core.Surface.ShellWindow` 统一基类（窗口属性 + 毛玻璃入口复用）；菜单定位用 `IDesktopSurface`（含 DPI/多屏几何），不自造窗口/度量实现。

## 11. 错误处理

- COM 失败（权限/句柄失效/枚举超时）：回退到纯插件菜单，不白屏、不崩。
- 单项命令抛异常：仅该项失败并记录诊断，其余项正常。
- `Dismiss` 后 COM 句柄必须 `Release`；`MenuHost` 关闭后释放资源。
- 菜单构建超时（如 500ms）：展示已就绪部分，标注"加载中…"。

## 12. 性能

- 菜单构建异步化；先展示骨架、再填充异步项。
- Shell 枚举加超时（200ms）并缓存常见目录的 verb。
- 长菜单虚拟化；定位计算不依赖布局线程。

## 13. 验收

- [ ] 桌面 / 任务栏 / Dock 项三处右键均出菜单
- [ ] 插件可贡献菜单项，`Priority` 排序稳定
- [ ] native 模式：文件右键完整（含注册表第三方扩展项）
- [ ] unified 模式：外观跟随主题令牌，verb 执行正确
- [ ] Esc / 外部点击 / 再次右键均幂等关闭，COM 无泄漏（可重复开合 100 次验证）

## 14. 开放问题

- MenuHost 用 WPF Popup 还是 WebView2 overlay（需与 HtmlShell 桥确认）。
- unified 模式会丢失部分第三方 Shell 扩展项——是否可接受，或仅对受控表面启用。
