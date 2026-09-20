# BetterDesktop.Shell.Core

shell 层的共享内核：统一窗口基类与弹窗基座、毛玻璃服务、动画服务、事件泵/钩子、主题帮助器与原生任务栏管理，为宿主与全部 UI 插件提供装配基础。

## 职责

- **窗口体系**：`Surface/ShellWindow`（全部壳面窗口统一基类：无边框/透明/置顶、WM_NCHITTEST 自建 resize、`ApplyWindowMaterial` 毛玻璃、`ChromeBorder` 描边/阴影、字号缩放、`SetThemeBinding`）、`Surface/PluginHostWindow`（插件宿主）、`Windows/PopupWindowBase`（弹出面板统一基类：外点收起钩子/NOACTIVATE 双层纪律/统一面板 chrome）+ `Windows/PopupPositioningService`。
- **毛玻璃**：`IVibrancyService`/`VibrancyService` + `DwmHelper`（DwmApi 直接 P/Invoke，FrostedGlassDemo 基准移植）+ `AdvancedVibrancyService`。
- **动画**：`Animation/AnimationService`（补间服务）。
- **桌面几何**：`IDesktopSurface`（主屏几何，WPF 逻辑坐标）、`IWindowHandleService`（宿主主窗句柄）、`Windowing/NativeTaskbarManager`（原生任务栏显隐，Bootstrap 按 components.dock/wintaskbar 联动）。
- **主题**：`Surface/ThemeBrushes`（令牌取色帮助器：Get/Tint/AccentTint/语义状态色）、`Surface/ThemeResourceProvider`（插件共享字典）。
- **独立入口 exe 的主题引导**：`Surface/EntryTheme`（**唯一实现**）。剪贴板面板（`BetterDesktop.Clipboard.Panel`）与截图入口面（`BetterDesktop.Capture`）都不引 shell-settings，而是直接读同一份 `%APPDATA%\BetterDesktop\settings.json`，把 `appearance.*` 推导成 `Application.Resources` 令牌。**设计刻度 `EntryTheme.Scale`（字号 11/12/13/17、间距 4/8/12/16、圆角、强调色透明度、动效时长）与 `Blend` 也在这里** —— 两个 exe 必须共用同一份数字，各写一遍必然漂移（2026-09-15 从 `PanelTheme` 上提）。用法：启动时 `EntryTheme.Load()` + `ApplyToAppResources()` 各一次。
- **细滚动条**：`Surface/SlimScrollBar`（无箭头、6px 圆角拇指、内容侧留白、随主题）。`Install(app)` 装成隐式样式即对全进程滚动条生效；令牌 `ScrollThumb`/`ScrollThumbHover` 由 `EntryTheme.ApplyToAppResources` 定义，故必须在主题引导**之后**安装。
- **Native**：`MouseHook`（WH_MOUSE_LL 外点收起）、`WinEventPump`（WinEvent 统一单泵多订阅）、`MessagePump`、`NativeMethods`。
- **其它**：`Surface/PermissionService`、`Services/MenuBarExtensionRegistry`、`DesktopSurface`。
- **应用条目菜单的系统级动作**：`Services/AppEntryActions`（定位 `RevealInExplorer` / 提权 `RunAsAdmin` / 属性 `ShowProperties` / 终端 `OpenInTerminal` / 卸载 `RunUninstaller` / 复制路径 `CopyToClipboard`）。应用提取器与菜单栏搜索**共用同一实现**，此前两处各写一份（同一动作行为漂移正是「右键功能时有时无」的一半根因）。项集的**出现条件**不在这里，而在 `packages/api/AppSource/AppEntryMenuBuilder`（构建器只判定、不执行）。

## 依赖

- `BetterDesktop.Kernel`（插件与运行时契约）
- 运行时仅 BCL + WPF（UseWPF）；毛玻璃为 Win32 DwmApi 直接 P/Invoke，零第三方库

## 扩展点

- `IVibrancyService`（bar/dock 仅 Inject 依赖）、`IDesktopSurface`、`IWindowHandleService`
- `ShellWindow` / `PopupWindowBase`：所有 UI 插件窗口/弹窗的统一基类（子类只实现内容）
- `WindowStyleHelper.MakeFloatingNoActivate`：浮动不抢焦点样式
- `WinEventPump`：全项目 WinEvent 钩子统一收口（单泵多订阅）
- `ThemeBrushes`：主题令牌取色唯一入口

## Known Limitations

- `IDesktopSurface` 仅主屏，多显示器场景留后续
- 毛玻璃依赖 Win11 DWM；非受支持系统降级为普通透明窗
