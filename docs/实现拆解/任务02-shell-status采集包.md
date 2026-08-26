# 任务 02：新建 shell-status 纯采集包（P1）

## 目标

右区 / 任务栏 / 控制中心共用的状态采集服务，界面与数据分离，无 UI。

## 新建 `packages/shell/shell-status/`

| 接口 | 数据实现 |
|------|----------|
| `IMemoryMonitor` | P/Invoke `GlobalMemoryStatusEx`（零依赖） |
| `IVolumeMonitor` | P/Invoke Core Audio `IAudioEndpointVolume`（vtable，免 NAudio） |
| `IBatteryMonitor` | P/Invoke `GetSystemPowerStatus` |
| `INetworkMonitor` | P/Invoke `wlanapi.dll`（信号/连接状态） |
| `IBrightnessMonitor` | WMI `WmiMonitorBrightnessMethods`（System.Management） |
| `IBluetoothMonitor` | P/Invoke `BluetoothFindFirstRadio`（先做开关） |
| `IMicrophoneMonitor` | Core Audio 端点（复用音量路径） |
| `IImeMonitor` | P/Invoke `ImmGetDefaultIMEWnd` + `GetKeyboardLayoutNameW` |
| `ITemperatureMonitor` | LibreHardwareMonitor（第三方库，Phase 3 再引） |

- `StatusPlugin.cs`：`Name="shell.status"`，`Inject=[]`，`LoadAsync` 里 `Provide` 各 monitor。
- 值变化经 `IEventBus` 广播（如 `MemoryChanged`/`VolumeChanged`），UI 只订阅事件，不轮询采集。

## 关键接口（示例）

```csharp
public interface IMemoryMonitor
{
    int GetUsagePercent();                       // GlobalMemoryStatusEx
    ulong GetTotalPhysBytes();
    event EventHandler<MemoryStatusChangedArgs>? Changed;
}
```

## 依赖方向

shell-status → kernel（IEventBus）+ shell-core 可选；**不依赖**任何 UI 插件。

## 单测

每个 monitor 用可注入实现测（P/Invoke 层抽接口，可替换为 fake）；至少覆盖 内存 / 电量 / 音量。

## 验收

- [ ] 构建 0 警告 0 错误
- [ ] `dotnet test` 通过
- [ ] host 可加载 shell.status 并 `Get` 到各 monitor
