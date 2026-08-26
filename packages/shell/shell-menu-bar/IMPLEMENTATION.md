# shell-menu-bar 右区 · 实现蓝图（如何复刻界面与功能）

> 配套 `README.md`（设计稿 v5）。本文回答"**我们的程序怎么实现**"：每个右区组件 = 界面（WPF 自绘）+ 数据（采集服务）分离，落到 better-desktop-cordis 的插件栈上。

## 1. 总体架构（复刻的关键：别学它的进程结构）

MyDockFinder 是"一个 C++ 程序 + 共享内存 + 独立进程"，我们 cordis 版改为：

```
shell-menu-bar（UI 插件，只画界面）
 ├── MenuBarWindow : ShellWindow      # 自绘 HWND 的 WPF 等价物
 │    └── StatusButton（统一自绘按钮基类：图标+hover 反馈+点击弹出面板）
 └── 组件 = IMenuBarExtension
       └── 每个组件自持 UI，数据来自 Inject 的采集服务

shell-status（数据插件，只采集，不画界面）★ 建议新增包
 ├── IVolumeMonitor / IBatteryMonitor / INetworkMonitor
 ├── IBrightnessMonitor / IMemoryMonitor / ITemperatureMonitor
 ├── IBluetoothMonitor / IMicrophoneMonitor / IImeMonitor
 └── 由 menu-bar / taskbar / control-center 共同 Inject 消费
```

- **界面与数据分离**：菜单栏只管画，采集服务可被任务栏/控制中心复用。
- **通信**：插件间 `Inject` + `IEventBus`（值变化发事件），不跨进程。
- 新增包建议：`packages/shell/shell-status`（纯采集，无 UI，kebab-case）。

## 2. 技术选型表（每组件：界面 + 数据源 + 依赖）

| 组件 | 界面实现（WPF 自绘） | 数据源（.NET 实现） | 依赖/风险 |
|------|---------------------|--------------------|-----------|
| 日期时间/日历 | 状态栏文本 + 点击弹自绘日历 Popup（可定制 WPF Calendar 皮肤） | `DispatcherTimer` 1s | 无 |
| 内存占用 | 自绘百分比 + 迷你走势图（Polyline） | **P/Invoke `GlobalMemoryStatusEx`** | 无（零依赖） |
| 音量 | 自绘音量条/滑块 + 静音 | **P/Invoke Core Audio** `IAudioEndpointVolume`（或 NAudio） | P/Invoke 约 60 行；NAudio 依赖重 |
| 电量 | 自绘电池图标 + 百分比 + 预计剩余 | **P/Invoke `GetSystemPowerStatus`** | 无 |
| 亮度 | 自绘亮度条（点击展开） | **WMI** `WmiMonitorBrightnessMethods`（System.Management）或 P/Invoke `SetMonitorBrightness`(dxva2) | System.Management 需 NuGet |
| 网络 | 自绘连接类型/信号图标 + Network Flyout 面板 | **P/Invoke `wlanapi.dll`**（信号强度）+ `NetworkInterface`（连接状态） | P/Invoke 中量 |
| 蓝牙 | 自绘蓝牙图标/设备数 | **P/Invoke `BluetoothFindFirstRadio`**（开关状态）；设备电量可选 CsWinRT | 开关简单，设备列表复杂 |
| 麦克风 | 自绘麦克风图标/静音 | P/Invoke Core Audio 端点（同音量路径） | 无 |
| 输入法 | 自绘语言/IME 图标 | **P/Invoke `ImmGetDefaultIMEWnd` + `GetKeyboardLayoutNameW`** | 无 |
| 搜索 | 自绘搜索图标 | 调起系统搜索：`explorer.exe "search-ms:query="`（或 Win+S） | 不接 UWP SearchPane |
| 系统托盘 | 自绘托盘区 | **`ManagedShell`**（已引用）或 `Hardcodet.NotifyIcon.Wpf` | ManagedShell 已有 |
| 功能托盘（控制中心） | **自绘下拉面板**：快捷开关网格 + 台前调度入口 | 开关复用上表监控服务 | 大件，独立块 |
| 台前调度 | **自绘窗口列表/平铺面板**（缩略图） | **Win32 `EnumWindows` + `DwmRegisterThumbnail`**（shell-dock 已有 DwmThumbnail 基础）+ `SetWindowPos` 平铺 | 最难，独立块 |
| 通知 | 自绘通知图标/气泡 | **降级方案**：调起系统通知中心（Win+N）；读系统通知需 UWP 权限，暂不做 | 见风险 |
| CPU/GPU 温度 | 自绘温度值 + 色阶 | **`LibreHardwareMonitor`**（开源库） | 需引入第三方库 |
| 天气 | 自绘温度/天气图标 | **和风天气 API**（需免费 key，中国区） | 需 key + 缓存 |

## 3. 关键组件实现骨架（可直接照抄）

### 3.1 内存占用（零依赖，P/Invoke）

```csharp
[StructLayout(LayoutKind.Sequential)]
struct MEMORYSTATUSEX
{
    public uint dwLength; public uint dwMemoryLoad;
    public ulong ullTotalPhys; public ulong ullAvailPhys;
    public ulong ullTotalPageFile; public ulong ullAvailPageFile;
    public ulong ullTotalVirtual; public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[DllImport("kernel32.dll")]
static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

public sealed class MemoryMonitor : IMemoryMonitor   // shell-status 提供
{
    public int GetUsagePercent()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return (int)m.dwMemoryLoad;
    }
}
```

### 3.2 音量（P/Invoke Core Audio，零依赖）

```csharp
// 接口 IAudioEndpointVolume 的 GUID: {5CDF2C82-841E-4546-9722-0CF74078229A}
// CoCreateInstance(CLSID_MMDeviceEnumerator) → IMMDeviceEnumerator
//   → GetDefaultAudioEndpoint(eRender, eConsole) → Activate(IAudioEndpointVolume)
// 关键方法：GetMasterVolumeLevelScalar(out float) / SetMasterVolumeLevelScalar(float) / GetMute / SetMute
```

> 用 `AudioEndpointVolume` P/Invoke 接口（约 60 行 vtable 布局），避免引 NAudio；事件用 `IAudioEndpointVolumeCallback` 收到变化再刷新 UI。

### 3.3 温度（LibreHardwareMonitor，最省力）

```csharp
// 引入 LibreHardwareMonitor（NuGet），后台线程更新
var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
computer.Open();
var cpuTemp = computer.Hardware
    .Where(h => h.HardwareType == HardwareType.Cpu)
    .SelectMany(h => h.Sensors)
    .FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core"));
```

### 3.4 台前调度（简化版 = 窗口总览 + 平铺）

- **窗口枚举**：复用 `shell-dock` 的 `RunningAppDetector`（EnumWindows + 过滤）。
- **缩略图**：复用 `DwmThumbnail`（shell-dock 已有），面板里每个窗口一个缩略图。
- **平铺动作**：`SetWindowPos(hwnd, ...)` 按网格重排（如 2x2/3xN），对齐"窗口总览/平铺"。
- 先不做 macOS 台前调度的"窗口分组+侧栏"完整语义，做"缩略图总览 + 一键平铺/显示桌面"。

### 3.5 输入法（IME，零依赖 P/Invoke）

```csharp
[DllImport("imm32.dll")]
static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);          // 取 IME 窗口句柄

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetKeyboardLayoutNameW(StringBuilder pwszKLID); // 当前键盘布局名（如 "00000804"=中文）

// 方案：订阅前台窗口变化 + 轮询 GetKeyboardLayoutNameW → 显示 "中/英" 或语言缩写
// 对齐 MyDockFinder：systemIME + ImmGetDefaultIMEWnd + TrayInputIndicatorWClass（子类化系统输入法指示器）
// 界面：自绘"中/英"小图标，点击可切换（可选调起语言栏切换）
```

### 3.6 功能托盘（控制中心）

- 自绘下拉面板（毛玻璃，复用 `shell-core.Vibrancy`），内容：
  - 快捷开关网格：WiFi / 蓝牙 / 音量 / 亮度 / 麦克风 / 静音 / 飞行模式（复用 shell-status 服务，Toggle 即调对应 API）。
  - 台前调度入口：显示/隐藏窗口总览面板。
  - 亮度/音量滑块。

## 4. 分阶段实施（小步快跑）

- **Phase 1（低风险，先出可用右区）**：日期时间+日历、内存、音量、电量、网络、系统托盘、功能托盘骨架。→ 产出：右区肉眼可见、可开关。
- **Phase 2（中风险）**：亮度、蓝牙、麦克风、输入法、搜索、天气。
- **Phase 3（独立块）**：台前调度（窗口总览+平铺）、通知（降级 Win+N）、CPU/GPU 温度（引 LibreHardwareMonitor）。
- 每阶段完成即 `dotnet build` + 手动验证，不堆积。

## 5. 风险与简化决策

1. **通知读取**：读 Windows 系统通知中心需 UWP 权限，桌面版代价高 → **降级**：调起系统通知中心（Win+N）+ 应用内自维护通知。
2. **蓝牙设备列表**：完整设备枚举需 C++/WinRT（CsWinRT 引入成本高）→ 先做**开关状态**（P/Invoke BluetoothFindFirstRadio），设备列表后续。
3. **台前调度**：完整 macOS 语义工作量极大 → 做**简化版**（窗口总览缩略图 + 平铺 + 显示桌面），与用户"Mac 里面窗口显示等功能"对齐的最小闭环。
4. **搜索**：不接 UWP SearchPane（桌面无法直接调）→ 调起系统搜索。
5. **天气**：需要免费 key（和风天气），实现前先确认 key 或换用无需 key 的公开源（如 Open-Meteo 需外网）。
6. **自绘统一**：所有组件继承 `StatusButton` 基类（图标/悬停变亮/点击缩放 95%/弹出面板），保证高度对齐（23px 基线）与交互一致。

## 6. 建议的下一步

1. 建 `packages/shell/shell-status`（纯采集包，Phase 1 先做 Memory/Volume/Battery/Network 四个 P/Invoke 采集器 + 单测）。
2. 在 `shell-menu-bar` 内先落地 **日期时间 + 内存 + 音量 + 电量** 四个自绘组件（最小可见闭环）。
3. 验证手感（悬停/点击/弹出）后再进入 Phase 2。
