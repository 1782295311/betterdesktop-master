# 电源管理缺失：休眠后风扇长转的定位与修复

- 日期：2026-09-18
- 现象：用户让系统进入休眠后**风扇一直转**，直到手动关闭所有 BetterDesktop 程序才停
- 影响：笔记本合盖后发烫耗电；即将分发给他人实地测试，属必须解决项

---

## 一、先排除一个方向：**不是我们锁住了睡眠**

全仓逐条核查（C# / Rust / C++ / PS1）：

| 检查项 | 结果 |
|---|---|
| `SetThreadExecutionState` 带 `ES_SYSTEM_REQUIRED` / `ES_DISPLAY_REQUIRED` | **零处**。唯一调用是 `PowerManagement.AllowSystemSleep()` 只传 `ES_CONTINUOUS`，是 MSDN 标准的"清除唤醒请求、放行睡眠"复位用法 |
| `PowerCreateRequest` / `PowerSetRequest` / `PowerRequestSystemRequired` | 零命中 |
| `PowerManager.WakeLock` / 相关 WinRT | 零命中 |
| `keepAwake` / `preventSleep` / 电源策略注册 | 零命中 |

⇒ 不存在"代码把睡眠锁死"。真实机制是 **Modern Standby（现代待机 / S0ix）**：笔记本"睡着"后 CPU 仍会被允许调度后台任务，
而我们有几处**永不停止的高频活动**在持续烧 CPU —— 表现就是"睡着了风扇还在转，关掉程序才停"。

---

## 二、根因（按影响排序，均已回读源码确认）

| # | 根因 | 位置 | 为什么烧 |
|---|---|---|---|
| 1 | **菜单栏帧率图标永久订阅每帧渲染** | `shell-menu-bar/Status/MenuBarStatusStrip.cs`（`FpsIcon` 构造里 `CompositionTarget.Rendering += OnFrame`） | WPF 语义：**只要存在 Rendering 订阅者，合成器就按刷新率持续出帧**（60/120/144Hz），与控件是否可见无关。菜单栏还是全屏宽的分层窗口 + DWM 材质，每帧代价远大于普通小窗；写 TextBlock 又制造新的渲染失效，形成自维持。**这是"关掉程序才停"的最直接解释** |
| 2 | **Dock 隐藏态反而钉在快档 60ms** | `shell-dock/DockTickPolicy.cs`（`statePending \|\| cursorNearDock`）+ `DockWindow.OnAutoHideTick` | "隐藏"发生在系统空闲阈值（默认 20 分钟无输入）之后 —— 即用户已离开、系统正准备进入待机的时刻，我们反而升到 16.7Hz；每拍还有 `GetCursorPos`/布局查询，以及**一次 COM 激活**（`CoCreateInstance(ImmersiveShell)`） |
| 3 | **剪贴板引擎每 800ms 无条件全量落盘** | `engine/src/engine.rs` `start_autosave` + `store.rs` `save` | 全量 JSON 序列化 → DPAPI 加密 → 写临时文件 → rename。历史数万条时是每秒 1.25 次持续 CPU + 磁盘写（代码注释自认"dirty 标志后续优化"） |
| 4 | **状态采集常驻 500ms** | `shell-status/Services/StatusPoller.cs`（音量/麦克风/输入法 500ms，CPU/内存/网络 1s） | 合计约 9 次/秒原生调用（CoreAudio 会话枚举、键盘布局注册表 + TSF…），永不停止 |
| 5 | **全局零电源事件处理** | 全仓 | 搜 `PBT_APMSUSPEND` / `PowerModeChanged` / `SessionSwitch` 均零命中。没有任何人在挂起时让路，恢复后所有高频轮询立刻以原频率重启 |

---

## 三、修复内容

### 3.1 新增进程内唯一的电源事件源
`packages/kernel/kernel/Core/SystemPowerMonitor.cs`（新）

- 专用线程 + 隐藏**顶层**窗口 + 消息泵，接收 `WM_POWERBROADCAST`
  （注：广播消息只投递给顶层窗口，`HWND_MESSAGE` 收不到 —— 故不能复用消息专用窗口方案）
- 处理 `PBT_APMSUSPEND`（挂起）/ `PBT_APMRESUMEAUTOMATIC|RESUMESUSPEND|RESUMECRITICAL`（恢复）
- 对外提供 `IsSuspended`、`ShouldPauseHighFrequencyWork`（含**恢复后 5 秒冷却期**：这段时间设备/COM/DWM 往往才刚就绪）
  与 `Suspended` / `Resumed` 事件
- 由 `PowerManagement` 插件在 `LoadAsync` 启动、`UnloadAsync` 停止
- 窗口过程对电源广播返回 `1`（**绝不返回 -1 / BROADCAST_QUERY_DENY**，那是"拒绝挂起"）

### 3.2 逐项落实

| 改动 | 文件 | 效果 |
|---|---|---|
| 帧率图标改为**按需订阅** | `MenuBarStatusStrip.cs`（`FpsIcon.SetSampling` / `SetComponentVisible`） | 仅当"组件可见 且 系统未挂起/未在冷却期"才订阅每帧渲染；隐藏即退订。**这是本次最关键的省电修复** |
| Dock 隐藏态改慢档 | `DockTickPolicy.cs` | `statePending` 不再升快档；唤出及时性由 `cursorNearDock` 保证（光标进预唤出带立刻升快档，贴底唤出最坏 ≈250ms，低于 400ms 可感知门槛） |
| Dock tick 挂起闸门 | `DockWindow.xaml.cs` `OnAutoHideTick` | 挂起中/冷却期直接跳过该拍（只做一次布尔判断，无任何系统调用） |
| 状态采集挂起闸门 | `StatusPoller.cs` `OnTick` | 挂起中/冷却期跳过本轮采集，避免睡着时每秒 9 次原生调用 |
| 引擎改为**签名驱动落盘** | `engine/src/store.rs`（新增 `change_signature`）+ `engine/src/engine.rs` | 内容变化（入库/删除/置顶/驱逐）才落盘，另有 **60 秒强制兜底**。无变更时 800ms 周期直接跳过 |

> **为什么引擎用"签名"而不是在每个写方法里打 dirty 标志**：漏标记会导致用户剪贴板历史**不落盘**（数据丢失级风险）。
> 签名方案零侵入、且不可能让历史"永久不写盘"（有 60 秒兜底）。代价仅是"仅改 copy_count 这类变更"延后到兜底写入，不影响可读性。

---

## 四、验证

| 项 | 结果 |
|---|---|
| 全仓构建 | 0 警告 0 错误 |
| `cargo check`（剪贴板引擎） | 通过 |
| `Shell.Dock.Tests` | 54 例中 53 通过（`DockTickPolicy` 用例已按**新行为**更新；剩余 1 例为既有的 Dock 重建分配量门槛测试，与本次改动无关） |
| `Kernel.Tests` 架构守护 | 2 例失败为**既有**：54 个 csproj 继承 TFM（未显式声明）、4 处 `Debug.WriteLine` 违规（均在本次未触及的文件里） |

### 真机验证建议（发给测试好友前自测）
1. **看菜单栏 FPS 数字**：空闲不操作时若不再恒为 60/120/144，说明渲染订阅已按需生效。
2. **合盖/休眠后观察风扇**（本次核心）：进入睡眠前记下日志，唤醒后搜 `host-*.log` 里的
   `电源监听已启动` / `系统即将挂起` / `系统已恢复` 三条，确认电源事件确实到达。
3. `powercfg /sleepstudy`（管理员）：看 Modern Standby 期间是否还有进程持续占用（DRIPS 达成率）。
4. `powercfg /requests`：应**不再出现** BetterDesktop 相关条目。

---

## 五、本次未做 / 建议后续

| 优先级 | 项 | 说明 |
|---|---|---|
| 高 | **恢复后重建低级钩子** | 睡眠恢复后 `WH_MOUSE_LL` / `WH_KEYBOARD_LL` 可能被系统静默摘除，且全仓无重建逻辑 → 表现为"唤醒后 Win 键、桌面双击、按需粘贴失效"（不烧 CPU 但功能死）。`SystemPowerMonitor.Resumed` 事件已备好，接入即可 |
| 中 | IPC 客户端 20ms 轮询（50Hz） | `shell-clipboard-ipc` / `shell-index-ipc` 是**零 kernel 依赖**的底层包，拿不到挂起状态。要处理需用"共享源"方式接入电源监听，或改为自适应退避（空闲逐步放宽、有数据立刻回快） |
| 中 | `WindowTracker` 宽区间 WinEvent（`0x8000..0x800C`） | 恢复后窗口重建风暴下会每 500ms 触发"扫开始菜单 + 全量窗口枚举"；建议收窄事件区间 + 恢复后 5 秒内抑制刷新 |
| 低 | Dock 每秒两次全量窗口枚举 | 每个窗口都要 `OpenProcess` 取路径；建议一次枚举同时产出两份结果，并给 pid→path 加缓存 |
| 低 | 第三方不可读依赖 | `ManagedShell`（托盘服务线程）、`LibreHardwareMonitorLib`（传感器轮询）为二进制依赖，睡眠行为无法从源码确认，需用 `powercfg /sleepstudy` 真机观察 |
