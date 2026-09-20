# BetterDesktop.Shell.Status



> 角色：`shell.status` — 系统状态采集（采集层）+ 用户可读语义快照（语义层）基础服务



菜单栏右区、任务栏、控制中心共用的状态数据来源。**界面与数据分离**：本包不含任何 UI，只负责 把裸系统值（P/Invoke 结果）翻译成用户能懂的语言与 UI 提示，并通过事件广播给 UI 层。


## 职责



- **采集层**：`Native/` 收口原生 DLL 薄封装（经 `NativeLoader` 转发）：AudioCore / WlanCore / PowerCore / DisplayCore / NetworkCore / CpuCore / MemoryCore / MediaCore 八件（csproj 随包分发）；WlanCore 具备持续扫描三件套（`Wlan_ScanStart/ScanCollect/ScanGetItem`）；IME 为 `KeyboardLayoutInterop` + TSF（`TsfInputProcessor`）。其他包不直接写底层。
- **语义层**：每个 Monitor 把裸值归一为 `StatusSnapshot`（人话 `HumanText` + 短线 `ShortText` + 严重级别 `Severity` + 进度 `Progress` + 图标键 `IconKey`），失败一律降级不抛异常。
- **分发**：`StatusPoller` 统一周期采集、检测变化，仅在变化时触发该监控项 `Changed` 事件 + 经 `IEventBus` 广播 `status.changed`。音频/网络已**事件驱动**（`SetChangeCallback`，150ms 防抖合并），轮询降为兜底；UI 语义快照只订阅事件。面板级枚举（WiFi 列表/蓝牙设备等明细）由 UI 包自采，不在本包语义快照范围。


## 接口 / 实现对照



| 接口 | 数据实现 | 语义输出示例 |

|------|----------|--------------|

| `IMemoryMonitor` | `GlobalMemoryStatusEx`（零依赖） | `内存占用 72%（共 32 GB，可用 9 GB），建议关闭部分后台程序` |

| `IBatteryMonitor` | `GetSystemPowerStatus` | `剩余电量 60%，约 1 小时 20 分` / `正在充电，65%` / `未检测到电池` |

| `IVolumeMonitor` | Core Audio `IAudioEndpointVolume`（vtable，免 NAudio） | `音量 45%` / `已静音` |

| `IMicrophoneMonitor` | Core Audio 捕获端点 | `麦克风正常（可录音）` / `麦克风当前为静音状态` |

| `INetworkMonitor` | `wlanapi` + `NetworkInterface` | `已连接 Wi-Fi` / `已通过有线网络连接` / `未连接到网络` |

| `IImeMonitor` | `KeyboardLayoutInterop` + TSF（`TsfInputProcessor`） | `当前为中文输入法` |

| `ICpuMonitor` | CpuCore.dll（WMI MSAcpi_ThermalZoneTemperature 含温度 detail） | `CPU 占用 23%，温度 52°C` |

| `IBrightnessMonitor` | DisplayCore.dll（DDC/CI + WMI，手动/事件驱动） | `亮度 70%` |

| `ISystemSource` | 生产的原始值读取缝（供测试替换 fake） | 本包测试注入 `FakeSystemSource` 稳定验证 |



## 分层与依赖方向



```

shell-status → kernel（IEventBus / IContext / IKernelLogger）

```



- **不依赖**任何 UI 插件；`shell-menu-bar`（UI 层）可 `Get<IMemoryMonitor>` 等类型化服务并订阅事件。

- UI 层不感知 `Native/` 底层协议，只消费 `StatusSnapshot`。



## 外部扩展点 / 使用方式



- 消费方通过 `Inject` 声明 `IMemoryMonitor` 等即可获得服务。

- 订阅广播：`events.On<StatusSnapshot>("status.changed", (s, _) => ...)`。



## 未实现的监控项（见技术选型表 / 任务文档）

> 2026-09-10 更新：亮度（IBrightnessMonitor，DisplayCore.dll）与 CPU 温度（CpuCore.dll WMI，附于 ICpuMonitor detail）已接入；独立 `ITemperatureMonitor` 契约仍未建。



| 监控项 | 状态 | 原因 |

|--------|------|------|

| 亮度 `IBrightnessMonitor` | 未接入 | 需 WMI `WmiMonitorBrightnessMethods`（System.Management） |

| 蓝牙 `IBluetoothMonitor` | 未接入 | 设备列表需 CsWinRT，复杂 |

| ~~温度 `ITemperatureMonitor`~~ | **部分接入**：CPU 温度经 CpuCore.dll（WMI）附于 ICpuMonitor detail；独立契约与 GPU 温度仍未建（LibreHardwareMonitor 引用在 shell-menu-bar，Phase 3 再评估） |



## Known Limitations



- 采集以系统事件驱动为主（音频/网络 `SetChangeCallback`，150ms 防抖合并），定时轮询（默认 2s，可调）降为兜底；对极快变化仍可能略有延迟。
- Core Audio 端点在声卡拔出/驱动异常时激活失败，会自动降级为「没有可用的音频输出设备」。
- `GetSystemPowerStatus` 的剩余时间在接电/未知时返回哨兵值，已收敛为「剩余时长未知」。
- 输入法 KLID→名称仅为内置常见映射；完整映射应走注册表/系统 API（后续增强）。
- `Changed` 事件默认在加载线程（捕获的 SynchronizationContext）抛出；若插件在非 UI 线程加载且无法捕获 同步上下文，则在轮询线程直接触发，订阅方需自行保证线程安全。
