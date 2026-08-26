# shell-menu-bar（顶部菜单栏插件 · 深度设计稿 v4）

> 状态：设计草稿（尚未实现）。
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

- [ ] 左区逐项：图标 / 程序菜单 / 位置 / 下载 / 文档（按用户指定顺序）
- [ ] 右区逐项：网络 / 内存占用 / CPU温度 / 麦克风 / **输入法** / 蓝牙 / WiFi / 电量 / 音量 / 系统托盘 / 功能托盘(控制中心) / 日期时间
- [ ] 右区组件均为自绘界面直观呈现内容（占用率/温度/信号/滑块等），非仅图标
- [ ] 功能托盘为下拉控制中心：快捷开关 + Mac 风格窗口显示功能（显示桌面/窗口平铺/窗口总览）
- [ ] 日期时间点击打开 Windows 原生通知中心
- [ ] 窗口继承 ShellWindow，主题/毛玻璃统一；卸载后菜单栏完全移除

## 14. 开放问题

- **功能托盘（控制中心）的具体清单**：快捷开关（WiFi/蓝牙/音量/亮度/飞行模式…）+ Mac 风格窗口显示功能（显示桌面/窗口平铺/窗口总览）的**具体开关与布局**待细化。
- CairoMenu 20 项命令在 cordis 内核的映射（复用 FrostedShellCommandService 或新建命令服务）。
- 右区自绘组件与 `shell-context-menu` 弹层（网络详情/控制中心面板）的复用边界。
