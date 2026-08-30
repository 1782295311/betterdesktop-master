# shell-menu-bar（顶部菜单栏插件 · 深度设计稿 v4）

> 状态：右区状态条已实现并接入主项目（2026-08-29，由 tools/ShellComponentsPlayground 验证成熟后移植）；左半区（Cairo 菜单等）仍为设计稿。
> v4 在 v3 精确布局基础上补齐确认项（2026-08-22）：MEU=内存占用；右区组件**均自绘界面直观呈现内容**；功能托盘=**类 Android 下拉控制中心**（含 Mac 风格窗口显示功能）。

## 1. 目标与边界

**做什么**：屏幕顶部全宽菜单栏，严格按下列分区：
- **左半区**（CairoShell MenuBar）：图标 | 程序菜单 | 位置 | 下载 | 文档
- **右半区**（MyDockFinder 状态区）：网络 | 内存占用 | CPU温度 | 麦克风 | **输入法** | 蓝牙 | WiFi | 电量 | 音量 | 系统托盘 | 功能托盘(控制中心) | 日期时间（点击 → Windows 原生通知中心）
- 右区组件**均自绘界面直观呈现内容**（非仅托盘图标），对齐 MyDockFinder 的自绘风格。

**不做什么**：不做底部任务栏（shell-dock）；不做文件右键（shell-context-menu）；通知中心**不自建**（指向 Windows 原生通知中心）。

## 2. 布局（用户指定，逐项对齐）

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [图标] [程序菜单] [位置] [下载][文档] │ … │ 网络 内存占用 CPU温度 麦克风 输入法 蓝牙 WiFi 电量 音量 系统托盘 功能托盘 日期时间 │
│ ─────────── 左半区：CairoShell MenuBar ──────────   ───── 右半区：MyDockFinder 状态区 ─────
└────────────────────────────────────────────────────────────────────────────────────┘
```

## 3. 左半区设计（CairoShell MenuBar）

### 3.1 图标（Cairo 按钮）
- Cairo 标志；左键打开 CairoMenu，右键窗口菜单，激活有反馈。

### 3.2 程序菜单
- 分组 Tab + 固定项；**拖放添加程序**（Drop → AddByPath）；**Win 键打开**（LWin/RWin → ToggleProgramsMenu）。

### 3.3 位置（PlacesMenu）
- 11 个系统位置；用户/计算机加粗；回收站强制开窗口。

### 3.4 下载 / 文档
- Places 中的高频入口直接上栏（`下载`、`文档`），点击打开对应系统文件夹；可配置显隐。

### 3.5 外观与行为（真源设置项）
- 阴影、模糊、自动隐藏、AppBar 事件（MouseEnter/Leave 工作区联动）。

## 4. 右半区设计（MyDockFinder 状态区，用户指定逐项）

> **自绘原则**：右区所有组件均**自绘界面直观呈现内容**（对齐 MyDockFinder），不是"图标+系统默认提示"；颜色/字体/动画走 ThemeCenter 令牌。

| # | 项 | 行为 |
|---|-----|------|
| 1 | 网络 | 自绘网络状态（上行/下行、连接类型）；点击展开详情 |
| 2 | 内存占用 | **自绘占用率 + 迷你走势图**（直观呈现内存使用） |
| 3 | CPU温度 | 自绘温度数值/色阶（如 <60° 绿 / 60-80° 黄 / >80° 红）；点击展开详情 |
| 4 | 麦克风 | 自绘麦克风状态/静音开关 |
| 5 | 输入法 | **自绘当前语言/IME 图标**（对齐 MyDockFinder `systemIME`）；数据用 `ImmGetDefaultIMEWnd` + `GetKeyboardLayoutNameW` |
| 6 | 蓝牙 | 自绘蓝牙开关/已连接设备数 |
| 7 | WiFi | 自绘 WiFi 信号/开关 |
| 8 | 电量 | 自绘电池图标 + 百分比；低电量提示 |
| 9 | 音量 | 自绘音量滑块/静音 |
| 10 | 系统托盘 | SystemTray 图标区 |
| 11 | 功能托盘 | **控制中心**（类 Android 下拉功能栏）：快捷开关（WiFi/蓝牙/音量/亮度…）+ **Mac 风格窗口显示功能**（如显示桌面、窗口平铺/排列、窗口总览 Mission Control 式） |
| 12 | 日期时间 | 点击 → **Windows 原生通知中心**（不自建通知中心） |

- 视觉反馈（悬停变亮/点击缩放 95%、60 FPS 合成层动画）按 MyDockFinder 基准。

### 4.1 右区真实实现情报（逆向，MyDockFinder 1.9.7.4，2026-08-22）

> 无源码，对 `D:\迅雷下载\MyDockFinder\MyDockFinder\Dock_64.exe` 等二进制做**只读静态分析**（PE 头/字符串/配置/语言文件/主题资源）得出的实现事实，供 cordis 复刻参考。

**实现形态（如何实现）**：
- 原生 **C++（MSVC）+ Win32**，版本 1.9.7.4（PDB 路径 `F:\Dock1.9.7.4\x64\Release\Dock_64.pdb`）。
- 渲染栈：**Direct2D（direct.cpp）+ WinUI（WINUI.cpp）+ C++/WinRT UWP API（uwpapi.cpp）**，窗口全部自绘。
- 右区（MyFinder）是**独立自绘 HWND**：`MyFinderApp` / `MyFinderAPPCwnd` / `MyFinderAPP_hwnd` / `MyFinderCwnd`。
- 与 Dock 跨进程通信：**共享内存**（`MyDockFinderShareMemoryTask` / `MyDockFinderShareKey` / `MyDockFinderShareTemp`）+ HWND 消息。
- 辅助独立进程：`Temperature.exe`（CPU/GPU 温度采集，共享内存回传）、`trayico.exe` / `SystemTrayico64_update.exe`（托盘）、`UiAccess.exe`（UIPI 提权点按托盘）、`ScreenRound.exe`（圆角屏）。
- 托盘集成：**子类化系统托盘窗口**（`Shell_TrayWnd` / `TrayNotifyWnd` / `TrayClockWClass` / `TrayInputIndicatorWClass`）。
- 设置：注册表 `HKCU\SOFTWARE\MyDockFinder` + `config.ini`（UTF-16LE，`[finder]` 段为右区组件开关与顺序）。

**绘制的组件与数据来源（真源存在，含未列入用户默认清单的可选项）**：

| 组件 | config 键 | 数据/绘制来源（逆向证据） |
|------|-----------|--------------------------|
| 日期时间 | systemtime | 点击弹**自绘日历**（Calendar skin；`themes/default/clock/` 有模拟表盘皮肤 bg/hour/minute/second） |
| 功能托盘 | systemcontrol | 系统控制面板（类安卓下拉） |
| 系统托盘 | systemtray | `SystemTray_Main` / `TrayButton`，子类化系统托盘 |
| 音量 | audio | **Core Audio COM**：`CVolumeMonitor` / `IAudioEndpointVolumeCallback` / `IMMNotificationClient` |
| 电量 | battery | `BatteryEstimatedTime`（预计剩余时间） |
| 显示器 | display | **DDC/CI + WMI**：`GetMonitorBrightness`/`SetMonitorBrightness`/`WmiMonitorBrightnessMethods`/`EnumDisplayDevicesW` |
| 网络 | network | **WLAN API + WinRT**：`WlanRegisterNotification`/`NetworkRssiInDecibelMilliwatts`；自绘 **Network Flyout**；`ms-settings:network-wifi` |
| 蓝牙 | bluetooth | **C++/WinRT `Windows.Devices.Bluetooth`**（含 BLE/设备电量 `FromBluetoothAddressAsync`）+ `BluetoothApis.dll` |
| 麦克风 | microphone | 音频端点采集 |
| 输入法 | systemIME | **IME 钩子**：`ImmGetDefaultIMEWnd` / `TrayInputIndicatorWClass` / `IMEModeButton` |
| 搜索 | search | **UWP `Windows.ApplicationModel.Search.SearchPane`** |
| 台前调度 | stagemanager | **StageManager + `stageico` 类**（自绘窗口列表；显示桌面/窗口标题悬停/黑名单/窗口尺寸间距） |
| 通知 | — | **C++/WinRT `Windows.UI.Notifications`** + `Microsoft.Explorer.Notification`（读系统通知中心） |
| 内存监控 | findermonitor.MENON | `GlobalMemoryStatusEx` / `K32GetProcessMemoryInfo` / WMI `Win32_PhysicalMemory` |
| CPU/GPU 温度 | findermonitor.TEMPON/TEMPGPU | **`Temperature.exe` 独立进程**采集（共享内存 `MyDockFinderShareTemp` 回传） |
| 天气 | — | **和风天气 HeWeather6 API** + bing weather UWP + `weather/` 图标缓存（00.png~06.png） |

**对 cordis 设计的直接结论**：
1. 右区组件**全部自绘**（Direct2D/WinUI），不是系统控件——与"自绘界面直观呈现内容"一致。
2. 各组件数据来源分散：Core Audio（音量）、C++/WinRT（蓝牙/网络/通知）、WMI+DDC/CI（亮度/内存/温度）、独立进程共享内存（温度）、UWP SearchPane（搜索）。
3. 与 Dock 用共享内存 + HWND 消息协作——cordis 里应改为插件间 `Inject`/事件总线，不跨进程。

## 5. 架构

```
shell-menu-bar (IPlugin)
├── MenuBarPlugin                 # 入口：装配左右半区
├── MenuBarWindow : ShellWindow   # 菜单栏本体（统一窗口基类）
│   ├── LeftPane                  # 图标 / 程序菜单 / 位置 / 下载 / 文档
│   └── RightPane                 # 网络…日期时间 状态组件组
├── CairoMenuService              # CairoMenu 系统命令
├── ProgramsMenuService           # 程序菜单（分组/拖放/Win 键，数据来自 IAppSourceService）
└── StatusComponents              # 右区状态组件组（IMenuBarExtension 轻量契约）
```

依赖：`BetterDesktop.Kernel`、`shell-core`（ShellWindow/Vibrancy/IDesktopSurface）、`theme-center`（令牌）、`shell-app-source`（程序/图标）。

## 6. 领域模型

```csharp
interface IMenuBarExtension
{
    string Id { get; }                    // "network" / "memory" / "cpu-temp" / "mic" / "ime" / "bluetooth" / "wifi" / "battery" / "volume" / "tray" / "control-center" / "datetime"
    int Order { get; }                    // 从左往右排
    UIElement CreateControl(MenuBarContext ctx);   // 自绘控件：组件各自直接呈现内容（占用率/温度/信号/滑块…）
}
```

## 7. 内核集成（真实契约 IPlugin）

```csharp
public sealed class MenuBarPlugin : IPlugin
{
    public string Name => "shell.menu-bar";
    public IReadOnlyList<Type> Inject => new[]
    {
        typeof(IVibrancyService),
        typeof(IAppSourceService)
    };

    public Task LoadAsync(IContext context, CancellationToken ct = default)
    {
        // 右区组件组（按用户指定顺序）：网络 / 内存占用 / CPU温度 / 麦克风 / 输入法 / 蓝牙 / WiFi / 电量 / 音量 / 系统托盘 / 功能托盘(控制中心) / 日期时间
        var extensions = new IMenuBarExtension[]
        {
            new NetworkExtension(), new MemoryUsageExtension(),   // 内存占用：自绘占用率+走势图
            new CpuTempExtension(), new MicrophoneExtension(),
            new ImeExtension(),                                    // 输入法：自绘语言/IME 图标（ImmGetDefaultIMEWnd）
            new BluetoothExtension(), new WifiExtension(),
            new BatteryExtension(), new VolumeExtension(),
            new SystemTrayExtension(),
            new ControlCenterExtension(),                          // 功能托盘：类安卓下拉控制中心（含 Mac 窗口显示功能）
            new DateTimeExtension()                                // 点击 → Windows 原生通知中心
        };
        _menuBarWindow = new MenuBarWindow(/* left items + extensions */);
        _menuBarWindow.Show();
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken ct = default) { _menuBarWindow?.Close(); return Task.CompletedTask; }
}
```

## 8. 公共契约（语义）

- 左区：`CairoMenuService`（20 项系统命令，硬件不可用项禁用）；`ProgramsMenuService`（分组数据来自 `IAppSourceService`）。
- 右区：`IMenuBarExtension` 按 `Order` 从左往右排；`DateTimeExtension` 点击**调起 Windows 原生通知中心**（如 `Win+N` 等效命令），不自建。

## 9. 数据流

```
Win 键 / Cairo 按钮 → CairoMenu / ProgramsMenu 弹出 → 命令执行（复用内核命令）

网络/电量/音量/蓝牙/WiFi/麦克风：系统状态事件驱动更新
内存占用/CPU温度：轮询（如 1-5s）驱动自绘走势图与色阶
日期时间：1s 走秒
```

## 10. 跨插件协作

- 窗口继承 `ShellWindow`；材质参数来自 ThemeCenter 令牌；屏幕/DPI 用 `IDesktopSurface`。
- 程序菜单数据/图标经 `IAppSourceService` / `IAppIconService`。
- 弹出菜单（CairoMenu/PlacesMenu/ProgramsMenu）复用 `shell-context-menu` 的 `IMenuService`（若弹层可承载 WPF 菜单）。

## 11. 错误处理

- 单个右区组件异常：隐藏该组件，其余正常。
- Hibernate/特定系统命令不可用：菜单项禁用而非报错。
- CPU温度/传感器不可读：显示 "—"，不报错。

## 12. 性能

- 时钟 1s / CPU温度 5s / 系统状态事件驱动，不空转轮询。
- 菜单弹出复用统一弹层，动画走合成层（60 FPS 目标）。

## 13. 验收

- [x] 左区：程序菜单入口（`◈`，复用 `IStartMenuService` 打开开始菜单）+ 位置 / 下载 / 文档（交 explorer 打开）
      —— 注意：这里是**导航入口**已通，左区自有的 CairoMenu（20 项命令）仍属第 14 节开放问题，未实现。
- [ ] 右区逐项：网络 / 内存占用 / CPU温度 / 麦克风 / **输入法** / 蓝牙 / WiFi / 电量 / 音量 / 系统托盘 / 功能托盘(控制中心) / 日期时间
- [ ] 右区组件均为自绘界面直观呈现内容（占用率/温度/信号/滑块等），非仅图标
- [ ] 功能托盘为下拉控制中心：快捷开关 + Mac 风格窗口显示功能（显示桌面/窗口平铺/窗口总览）
- [ ] 日期时间点击打开 Windows 原生通知中心
- [x] 窗口继承 ShellWindow，主题/毛玻璃统一；卸载后菜单栏完全移除
- [x] 主题自适应：自绘图标与文字随 `IAppearanceService` 前景色实时换色（原为硬编码白，亮色主题下不可读）
- [x] 多显示器/高 DPI：弹窗锚定按**锚点所在显示器**回钳，并做物理像素 → 逻辑单位换算

## 14. 开放问题

- **功能托盘（控制中心）的具体清单**：快捷开关（WiFi/蓝牙/音量/亮度/飞行模式…）+ Mac 风格窗口显示功能（显示桌面/窗口平铺/窗口总览）的**具体开关与布局**待细化。
- CairoMenu 20 项命令在 cordis 内核的映射（复用 FrostedShellCommandService 或新建命令服务）。
- 右区自绘组件与 `shell-context-menu` 弹层（网络详情/控制中心面板）的复用边界。

## Known Limitations

- ~~左半区尚未实现~~ **已实现**：`MenuBarLeftZone` 提供程序菜单入口（复用 `IStartMenuService`）+ 位置/下载/文档。
  服务未注入时该入口自动不呈现（M10 降级）。左区自有的 CairoMenu 20 项命令仍未实现（见第 14 节）。
- 状态条组件为紧凑自绘（按钮高 14px，随菜单栏 16px 高度），非全尺寸面板；对应详情面板为独立 ShellWindow 弹窗，失焦自动关闭。
- 监控服务（shell.status）不可用时对应图标降级为占位、不报错；CPU 温度/传感器不可读时显示占位。
- 菜单栏本体**仍只驻留主屏**（每显示器一个菜单栏未实现）。但**弹窗锚定已按锚点所在显示器**回钳，
  并修正了物理像素/逻辑单位混用（原实现把 `PointToScreen` 的物理点直接当逻辑点用，非 100% DPI 下弹窗会偏移）。
- 通知按钮为本地开关，尚未对接 Windows 原生通知中心（不自建通知中心）。
- 各面板（IME/电池/网络/内存/CPU/麦克风/声音/WiFi/蓝牙/亮度/日历/控制中心）为第一版实现，功能清单细化见上文第 14 节开放问题。

## 15. 主题与屏幕几何（设计要点）

- **前景色单一真相源 `MenuBarTheme`**：右区是纯代码自绘（`Rectangle`/`Path`/`TextBlock` 直接赋 `Fill`/`Stroke`），
  历史上有 55 处硬编码 `Brushes.White`，亮色主题下整条菜单栏不可读。
  改法不是把 30+ 个对象初始化器拆成 `SetResourceReference` 三段式，而是让 `MenuBarTheme` 持有**一个未冻结的
  `SolidColorBrush` 共享实例**——WPF 中未冻结画刷改 `Color` 会自动通知所有引用者重绘，
  于是"改一次 = 全菜单栏换色"，效果等同 DynamicResource，但对调用方零结构改动。
  保留的 3 处语义白：CapsuleSwitch 滑块、ToggleSwitch 圆钮、静音红底白字（这些是控件自身配色，不随主题前景走）。
- **屏幕几何统一在 `MenuBarScreen`**：所有对外 API 一律**逻辑单位**（与 `Window.Left/Top` 同域）。
  `Visual.PointToScreen()` 给物理像素，`SystemParameters.WorkArea` 给逻辑单位，二者不能直接运算。
- **弹窗互斥**：`StatusBarMenuBarExtension.ShowPopup` 打开新面板前先 `CloseAllExcept(popup)`，
  仿 macOS 同一时刻只开一个面板（原实现点了 CPU 再点声音会叠两层窗口）。
- **显示器变化自适应**：`MenuBarWindow` 挂 `HwndSource` 钩子监听 `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`，
  分辨率/缩放/任务栏位置变化后自动重排并收起已开面板（旧锚点已失效）。

## 16. 实机反馈修复（2026-08-30，7 项）

用户实跑后提出的 7 个问题，已全部修复并通过全量构建门禁（0 警告 0 错误）。本节记录**根因与结论**，
避免后续重犯同类错误。

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | 系统托盘多余显示音量/电池 | ManagedShell 把 Win11 内置系统图标（音量/网络/电源/操作中心）也当作普通托盘图标转发 | `SystemTrayIcon.HideSystemIcons`：按 **Win11 固定 GUID**（`7820ae73/74/75/76-…`）+ **Win10 宿主 dll 后缀**（`sndvolsso.dll`/`pnidui.dll`/`batmeter.dll`/`actioncenter.dll`）**双路识别**；开关持久化到 `menubar.tray.hideSystemIcons`（默认 true） |
| 2 | WiFi 图标画得不好 | 画布只有 16×16、最外弧半径 7 而圆点落在 y 10.4~12.6 → **圆点溢出被裁**；半径 3/5/7 间距仅 2，缩到 16px 糊成一团；且**永远满格**，断网/有线/弱信号都长一样 | 新建 `Contracts/WifiGlyph.cs`（24×24 栅格，半径 5/9.5/14 间距 4.5，0~3 级真实强度，有线走水晶头） |
| 3 | 麦克风滑杆风格不统一 + 声音偏好打不开 | 滑杆样式各写一份；偏好链接是纯 `TextBlock` 且**漏挂点击事件** | 滑杆统一走 `NativePanelStyles.CreateCircleThumbSliderStyle`；链接统一走 `NativePanelStyles.CreateSettingsLink`（唯一实现，内部挂了点击） |
| 4 | 上下行速率面板同 WiFi 图标问题 | 面板自己抄了一份扇形几何（与状态条不同源） | 改为复用 `WifiGlyph`，并读 `WifiEnumerator.ReadCurrentConnection()` 的真实信号强度 |
| 5 | 电池面板图标与功能不符 + 偏好设置字体不白 | 电源方案图标用 Segoe MDL2 码位 `\uE74E/\uE9D2/\uE840/\uE783`，字形与语义对不上；偏好链接用 `AccentBrush` 蓝色 | 新建 `Contracts/PowerGlyph.cs` 自绘（节能=叶/平衡=天平/高性能=速度表/卓越=闪电）；偏好链接统一改 `ThemeForeground`（暗色即白、亮色自动转深） |
| 6 | 控制中心显示不佳 | 图标同病根（9 个字体码位）；麦克风按钮与三个媒体按钮**无点击事件**；歌名写死空格；描边硬编码半透明白 | 见下方 16.1 |
| 7 | 扩展中心职责错配 + 桌面多出快速笔记图标 | 「+」同时管系统功能与外部插件；quick-note 默认开启 | `ExtensionCatalog` 拆 `External` / `SystemFeatures`；「+」只列外部插件；**设置左侧新增「菜单栏」分区**（`Sections/MenuBarSection.cs`）管 15 项系统功能 + 托盘图标过滤开关；quick-note 默认关闭 |

### 16.1 控制中心重构要点（问题 6）

- **图标自绘**：新建 `Contracts/ControlCenterGlyph.cs`（24×24 栅格，11 个图标 + 4 个媒体控制几何），
  取代全部 Segoe MDL2 Assets 码位。Wi‑Fi 直接复用 `WifiGlyph`，保证**菜单栏 / NETWORK 面板 / 控制中心三处一致**。
- **信息对齐**：三张模块卡片共用 `BuildModuleCard`（头部「标题左 / 数值右」+ 内容），卡片内控件垂直居中；
  大瓦片图标由 `VerticalAlignment.Top` 改为 `Center`（此前图标顶挂、文字居中 → 视觉错位）。
- **布局升级**：开关网格行高 58 → 60；瓦片/卡片描边由硬编码 `Color.FromArgb(120/60,255,255,255)`
  改走主题令牌 `ThemeSeparator`（亮色模式下原描边几乎看不见）。
- **功能对接**：
  - 麦克风按钮 → `AudioCoreNative.SetCaptureVolume(vol, !muted)`（保持音量、只翻静音位），
    橙底=已静音，直接读 `GetStatus(AudioFlow.Capture)` 而非等监控轮询。
  - 媒体区 → 新建 `Services/MediaSessionController.cs`（SMTC 薄壳，2s 轮询 + 命令后即时回读），
    显示真实 Title/Artist/AppName，Prev / Play-Pause / Next 下发真实媒体命令；
    播放状态切换只改同一个 `Path` 的 `Data`，不重建按钮（避免闪烁与丢失悬停态）。
  - `ControlCenterFeatureCatalog.ShowAt` 修正单位纪律违例：物理锚点经 `MenuBarScreen.ToLogical`
    换算，钳制边界改取**锚点所在显示器**的 `GetWorkArea`（原用 `SystemParameters.WorkArea`，只描述主屏）。

### 16.2 本轮沉淀的两条通用纪律

1. **字体码位不可信**：Segoe MDL2 Assets 的码位凭印象填 → 字形与语义不符、字体缺失显示方块。
   凡是"图标 + 语义"的场景，一律**自绘几何**（`WifiGlyph` / `PowerGlyph` / `ControlCenterGlyph`）。
2. **有入口必须有行为**：纯 `TextBlock` + 无点击事件 = 看起来能点、点了没反应。
   所有"偏好设置/跳转"统一走 `NativePanelStyles.CreateSettingsLink`，
   所有"看起来是按钮"的元素统一挂点击处理 + `Cursors.Hand`。

### 16.3 一个易踩的命名空间坑

`MediaPlaybackState` 是**本项目自定义枚举**（`shell-status/Native/MediaCoreNative.cs`），
**不是** WinRT 的 `Windows.Media.Control.MediaPlaybackState`。
且在 `BetterDesktop.Shell.MenuBar.Windows` 命名空间下，裸写 `Windows.Media.Control.X`
会被就近解析成 `BetterDesktop.Shell.MenuBar.Windows`（CS0234）——需要引用 WinRT 类型时必须写 `global::Windows.…`。
