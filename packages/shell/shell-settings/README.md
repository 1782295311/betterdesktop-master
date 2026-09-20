# shell-settings

设置中心插件：统一设置服务 + 设置界面（分区导航），所有插件经此收敛配置。

## 职责

- `ISettingsService`：扁平键值 JSON 持久化（`%APPDATA%\BetterDesktop\settings.json`）+ 变更事件；`Get<T>/Set<T>` 带默认值，线程安全，落盘走"临时文件+替换"。
- `ISettingsSectionRegistry`：设置分区注册表（内核同类型服务单实例，故用注册表收集）；各插件在 `LoadAsync` 里 `context.Get<ISettingsSectionRegistry>()?.Register(new MySection(...))` 贡献分区。
- `ISettingsWindowService`：单实例打开/激活设置窗口，可定位分区（`ShowSection(title)`）。
- `SettingsWindow`：左侧分区导航 + 右侧内容区；**继承统一窗口基类 `ShellWindow`**，跟随壳面的无边框 + 毛玻璃材质（`ShellWindow.ApplyWindowMaterial` → `IVibrancyService.Apply(..., Transparent)` 即 DWM BlurBehind）+ 圆角外观（仅覆盖对话框所需的窗口行为属性：可缩放/激活/显示任务栏/居中）；不再自创不透明深色窗体，窗体外层透明、内层用半透明托盘（`AA/B3/80` alpha）托起内容，与全项目"不实现深/浅色模式、界面跟随统一窗口基类"的设计原则对齐。
- 内置**设置主分区**（`Sections/SystemSection.cs`，Title="设置"）：由原"通用"与"系统管理"合并而成，含 5 组：常规与外观 / 启动与自启 / 性能与资源 / 维护与重置 / 组件管理；本域另有 主题（ThemeSection）与 左侧 Dock（LeftDockSection）两个自带分区。

## 契约

| 接口 | 说明 |
|------|------|
| `ISettingsService` | `T? Get<T>(key, default)` / `void Set<T>(key, value)` / `Changed` |
| `ISettingsSection` | `Title` / `IconKey` / `UIElement Build(ISettingsService)`（内容被窗口缓存复用） |
| `ISettingsSectionRegistry` | `Register(section)`（标题去重，后注册覆盖）/ `Sections` |
| `ISettingsWindowService` | `Show()` / `ShowSection(title)` |

## 依赖

- `BetterDesktop.Kernel`（IPlugin/IContext）
- `BetterDesktop.Shell.Core`（主题/毛玻璃服务，非窗口基类；设置窗口用普通 Window）
- `Inject = []`（加载顺序不敏感）

## 落地状态（2026-08-22）

- ✅ 插件/服务/窗口全部实现；host Bootstrap 已接线。
- ✅ **设置窗口默认不弹**（入口在菜单栏 Logo 快捷菜单）；环境变量 `BETTERDESKTOP_SETTINGS_ONSTART=1` 仅供调试启动即开。
- ✅ **设置窗口继承统一窗口基类 `ShellWindow`**：跟随壳面无边框 + 毛玻璃材质 + 圆角外观，不再自创深色窗体；由 `SettingsPlugin` 注入 `IVibrancyService` 给基类应用材质；仅覆盖对话框行为属性（可缩放/激活/显示任务栏/居中）。与全项目"不实现深/浅色模式、界面跟随统一窗口基类"原则对齐。
- ✅ 内置**设置主分区**（`Sections/SystemSection.cs`，Title="设置"），由原来的"通用"与"系统管理"合并而成，含 5 组：常规与外观 / 启动与自启 / 性能与资源 / 维护与重置 / **组件管理**；全部经 `ISettingsService` 读写持久化（键前缀 `general.*` / `system.*` / `components.*`）。
- ✅ **真实逻辑已接入**：自启写 HKCU\Run 注册表（`SystemManagement.SetAutoStart`，初始状态回读注册表）、重置删 settings.json、导出复制到桌面、清缓存删目录、打开日志目录 ShellExecute（`Services/SystemManagement.cs`）。
- ✅ **全局外观服务 `AppearanceService`**：实现 `IAppearanceService`（shell-core 定义，一切外壳窗口外观单一来源）+ `IThemeTokens`（兼容旧消费者）。从 `appearance.*` 键持久化/读取，任意 setter 变更即写回设置并广播 `Changed`；`ShellWindow` 基类订阅后自动重绘（背景/字号/材质/皮肤），实现"一处改主题、全局窗口即时生效、无需重启"。
- ✅ **主题分区（导航栏新板块 `ThemeSection`，Title="主题"）**：统一管控程序一切外观，含 5 组真实可调项——①**外观模式（无色/亮色/暗色 三选一，`AppearanceService.ThemeMode` + `ApplyModeDefaults` 推送整套模式默认色）**；②窗体透明度与毛玻璃（不透明度滑块 + Acrylic/Transparent 材质开关）；③字号缩放（小/中/大）；④圆角与间距（圆角半径 + 间距缩放滑块）；⑤皮肤（选择本地图片作窗口背景 + 配色预设联动强调色）。所有控件经 Mac* 样式；改动经 `IAppearanceService` 实时套用并广播。
- ✅ 原 `ThemeTokensService` 已由 `AppearanceService` 取代（同时实现 `IThemeTokens`）。
- ✅ **组件启停开关（去重精简）**：原"系统集成"分组（接管任务栏/替换 Shell 为重复且破坏性的伪开关，多显示器策略已并入"性能与资源"）已移除；"组件管理"保留 6 个 `components.*` 开关，生效时机分两类——
  - **我方组件**（dock/menubar/companion/contextmenu）：禁用则下次启动不加载对应组件。`components.dock` 已真实生效（`DockPlugin.LoadAsync` 读 `components.dock` 决定是否加载）。
  - **Windows 原生部件管理**（wintaskbar/wincontextmenu）：本环境不实现原生部件，仅"管理"其显隐（参考 Cairo `ExplorerHelper.HideExplorerTaskbar`）。`components.wintaskbar` 已真实接入：`Bootstrap` 启动时按设置隐藏/显示 Explorer 原生任务栏（覆盖主屏 `Shell_TrayWnd` + 多屏 `Shell_SecondaryTrayWnd`，`shell-core/Windowing/NativeTaskbarManager.cs`），并在 `Application.Exit` 恢复显示；`components.wincontextmenu`（原生右键菜单）为同类管理开关，待接入注册表方式后即时生效。
- ✅ 构建实证：host 0 警告 0 错误（slnx 全量）。
- ⏳ 待办：图标键、ThemeCenter 替换轻量令牌、其他组件（菜单栏/扩展中心/右键菜单）落地时消费各自 `components.*` 开关、`components.wincontextmenu` 注册表方式落实、`IComponentToggle` 契约接入运行时动态启停。

## Known Limitations

- 设置项为扁平键值，无 schema 校验（Get 类型不匹配回默认值）。
- 外观经 `IAppearanceService`（契约在 packages/api/Core）+ `host/App.xaml` 令牌全集（Theme*/Control*/Popup*/Status*）+ `shell-core ThemeBrushes` 取色帮助器集中管理；外观三模式（无色/亮色/暗色）已实现。ThemeCenter 仅为设计提案（docs/design-proposals/）。
