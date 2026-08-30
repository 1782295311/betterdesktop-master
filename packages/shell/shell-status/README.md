# BetterDesktop.Shell.Status

> 角色：`shell.status` — 系统状态采集（采集层）+ 用户可读语义快照（语义层）基础服务

菜单栏右区、任务栏、控制中心共用的状态数据来源。**界面与数据分离**：本包不含任何 UI，只负责
把裸系统值（P/Invoke 结果）翻译成用户能懂的语言与 UI 提示，并通过事件广播给 UI 层。

## 职责

- **采集层**：`Native/` 收口所有 P/Invoke（`GlobalMemoryStatusEx`、`GetSystemPowerStatus`、Core Audio COM、
  `wlanapi`、`GetKeyboardLayoutNameW`），其他包不直接写底层。
- **语义层**：每个 Monitor 把裸值归一为 `StatusSnapshot`（人话 `HumanText` + 短线 `ShortText` +
  严重级别 `Severity` + 进度 `Progress` + 图标键 `IconKey`），失败一律降级不抛异常。
- **分发**：`StatusPoller` 统一周期采集、检测变化，仅在变化时触发该监控项 `Changed` 事件 + 经
  `IEventBus` 广播 `status.changed`。UI 层**只订阅事件，不自行轮询**。

## 接口 / 实现对照

| 接口 | 数据实现 | 语义输出示例 |
|------|----------|--------------|
| `IMemoryMonitor` | `GlobalMemoryStatusEx`（零依赖） | `内存占用 72%（共 32 GB，可用 9 GB），建议关闭部分后台程序` |
| `IBatteryMonitor` | `GetSystemPowerStatus` | `剩余电量 60%，约 1 小时 20 分` / `正在充电，65%` / `未检测到电池` |
| `IVolumeMonitor` | Core Audio `IAudioEndpointVolume`（vtable，免 NAudio） | `音量 45%` / `已静音` |
| `IMicrophoneMonitor` | Core Audio 捕获端点 | `麦克风正常（可录音）` / `麦克风当前为静音状态` |
| `INetworkMonitor` | `wlanapi` + `NetworkInterface` | `已连接 Wi-Fi` / `已通过有线网络连接` / `未连接到网络` |
| `IImeMonitor` | `GetKeyboardLayoutNameW` | `当前为中文输入法` |
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

| 监控项 | 状态 | 原因 |
|--------|------|------|
| 亮度 `IBrightnessMonitor` | 未接入 | 需 WMI `WmiMonitorBrightnessMethods`（System.Management） |
| 蓝牙 `IBluetoothMonitor` | 未接入 | 设备列表需 CsWinRT，复杂 |
| 温度 `ITemperatureMonitor` | 未接入 | 需 LibreHardwareMonitor 第三方库（Phase 3 再引） |

## Known Limitations

- 采集基于定时轮询（默认 2s，可调），非系统事件驱动；对极快变化（如瞬时音量）可能略有延迟。
- Core Audio 端点在声卡拔出/驱动异常时激活失败，会自动降级为「没有可用的音频输出设备」。
- `GetSystemPowerStatus` 的剩余时间在接电/未知时返回哨兵值，已收敛为「剩余时长未知」。
- 输入法 KLID→名称仅为内置常见映射；完整映射应走注册表/系统 API（后续增强）。
- `Changed` 事件默认在加载线程（捕获的 SynchronizationContext）抛出；若插件在非 UI 线程加载且无法捕获
  同步上下文，则在轮询线程直接触发，订阅方需自行保证线程安全。