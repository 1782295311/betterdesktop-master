# 无色纯模糊 + 三项真机回归：本批改动与验证指引（2026-09-18）

> 范围：①「无色纯模糊」做成真生效（保住特色，不改口径）；②虚拟机实测的三项回归；
> ③顺手收口此前审查里"长期占用 / 会改坏用户窗口"的既有坑。
> 状态：全仓 `dotnet build BetterDesktop.slnx -c Debug` **0 警告 0 错误**；
> 相关单测 138 + 21 + 5 + 53/54 + 86 + 34 绿（其中 1 例按新语义修正，见 §5；
> dock 那 1 例是**既有**的内存分配门槛抖动测试，单独跑 3/3 通过，见 §8.4）。

---

## 一、先定案：无色纯模糊到底哪种配方能用（实拍证据）

新增探针工具 `tools/BlurProbe`（实拍像素比对：黑白细条纹背景 + 抓屏后按「黑/白/中间灰」分类）。
本机 **Win11 build 26200** 自检结果（`blurprobe-result.txt`）：

| 组 | 结果 | 判读 |
|---|---|---|
| 分层 + `BLURBEHIND` | 黑 0% / 白 0% / **灰 100%** | ✅ **真正的无色纯模糊** |
| 分层 + `ACRYLIC(α=0)` | 黑 0% / 白 100% / 灰 0% | ❌ 并不是"无色"——把窗口刷成**不透明白** |
| 非分层 + `BLURBEHIND` | 黑 0% / 白 0% / **灰 100%** | ✅ 玻璃窗路径同样可用（但多数机器用不上） |
| 非分层 + `ACRYLIC(α=0)` | 黑 100% / 白 0% / 灰 0% | ❌ 不透明白/黑 |
| 对照（不设 accent） | 黑白各 ≈46% / 灰 7% | 测量基线（窗口真透明、看到清晰背景） |

**结论**：无色纯模糊 = `ACCENT_ENABLE_BLURBEHIND + GradientColor=0`，且**分层窗口就够用**；
"亚克力把 alpha 清零"这条流传做法实测会在窗口上糊一层白/黑 —— 已从默认配方链中剔除。

---

## 二、DWM 侧改了什么（保住特色，不改 Dark/Light 口径）

| 改动 | 位置 | 说明 |
|---|---|---|
| 能力探测（把"静默失败"变可判定） | `shell-core/Vibrancy/BlurCapability.cs`(新) | 读 DWM 合成 / 系统「透明效果」/ 省电模式 / 远程会话 / build，给出一条可直接落日志的结论；并提供**一键开启系统透明效果**（写 HKCU + 广播 `WM_SETTINGCHANGE`） |
| 无色配方链 + 强制重算 | `Vibrancy/DwmHelper.cs` | `EnableColorlessBlur`：先铺满玻璃区 → 依次 `BLURBEHIND` → `Mica`（**不再用 ACRYLIC**），每档留痕；应用后补 `SetWindowPos(SWP_FRAMECHANGED)` 让 DWM 重算（此前没这一步，是"设置成功但不刷新"的常见原因） |
| 档位可配（按机器 A/B） | `Vibrancy/BlurCapability.ReadBlurPreference` | `appearance.material.blur`（或环境变量 `BETTERDESKTOP_BLUR_IMPL`）= `blurbehind`(默认)/`acrylic0`/`mica` |
| 合成状态变化后重应用 | `Surface/ShellWindow.cs` | 新增 `WM_DWMCOMPOSITIONCHANGED` 处理：作废能力结论 + 重新应用材质（Win11 上从最小化恢复/切虚拟桌面会让模糊静默消失且不自己回来） |
| 非分层"玻璃窗"通路（**默认关**） | `Surface/ShellWindow.cs` + `api/Core/IAppearanceService.cs` | `appearance.material.glass=true` 时窗口改非分层 + DWM 玻璃区；默认 false = 现行行为，零回归。构造期读取，改动需重开窗口 |
| **无色模式零色**（按你的口径） | `shell-settings/Services/AppearanceService.cs` | 无色模式窗口层/面板层**不再铺 `#1F1F22 @0.35(或+0.05)`**（原来两层叠加≈61% 灰，把模糊整块吃掉，与同文件 `ThemeWindowBackground=#01000000` 自相矛盾）；Dark=透黑、Light=透白**一字未动**。零色带能力闸门：模糊不可用时退回中性深底，避免出现"桌面上的裸文字" |

> 零色实现细节：用的是 `argb(1,0,0,0)` 的"透明地板"而不是纯 `Transparent` —— 分层窗口下完全透明会导致**鼠标穿透**（`ShellWindow` 构造期注释里的既有纪律），1/255 视觉等同全透明但保留命中。

---

## 三、三项真机回归的修复

### 1. dock「运行区抽搐 + 窗口被关掉 + 重开变小」——最严重的一条

**根因**：`WindowPeek` 会把一个**可能是全零的** `WINDOWPLACEMENT` 写回**别人的窗口**。
读取侧 `RunningAppDetector.GetWindowPlacement` 丢弃了 native 返回值 → 读失败时得到零值
（`showCmd=0` 即 `SW_HIDE`、`rcNormalPosition` 全 0），而 `End()` 原样 `SetWindowPlacement` 写回：
- `showCmd=0` → 用户看到的"它自己把运行窗口关掉了"；
- `rcNormalPosition` 归零 → 之后任何 `SW_RESTORE` 都按被污染的矩形还原 → "重开变成很小的一块"。

**修复**：
- `RunningAppDetector.TryGetWindowPlacement`（新）：校验返回值（并保留"先填 length"的正确用法），旧签名转发；
- `WindowPeek`：`_minimizedPlacement` 改**可空**；**读不到 placement 就放弃本次 peek**（绝不先显示再无法收回）；`End()` 只在"确实读到过有效值"时才写回；
- `ThumbnailWindow` / `DockWindow`：peek 的 `callingHwnd` 改为传 **DockWindow 句柄**（语义 = "预览期间不被 DWM 透明化的窗口"；传浮层自身会让 dock 含运行区一起变暗/近乎消失，与运行区重建叠加就是"抽搐"）。这一条在 `docs/plans/2026-09-11-host-elevation-dock-peek.md §7 Q1` 早有结论，但代码里一直没落地。

### 2. 「剪贴板怎么都启动不了」+「主程序起来了但别的没起来」

- **打包缺口（直接原因）**：`03-剪贴板面板` 的 exe 从未进过 `01-主程序`，而程序只在"同目录 / 安装根 / `%LOCALAPPDATA%\BetterDesktop`"找它 → 永远找不到 → 侧边手柄、热键、托盘入口全部失效。
  - `scripts/publish-modules.ps1`：把 02/03 的模块自有文件（`<前缀>*`，即 exe/dll/deps.json/runtimeconfig.json）**同时复制进 01-主程序**（与 Rust 引擎同一处理方式）；共享程序集仍以 01 为单一来源，不覆盖。
  - `ClipboardEngineLauncher`：候选目录补上 **安装根**（`deployment.json` 的 `installRoot`，即 `app\<build>`），失败日志同步列出候选。
- **启动链缺口**：此前只有**托盘**负责拉起 Agent/桌面服务，而 README 把"直接启动 Host.exe"列为合法方式 → 走这条路 Agent 不在（截图热键、系统右键扩展自愈、桌面服务监护全缺）。
  - `host/Bootstrap.cs` 新增 `EnsureAgentRunning()`（与托盘同一语义：`agent-stopped.flag` 存在时不复活；已运行/未部署都只留一行日志）；顺带修掉该处 `GetProcessesByName` 不释放句柄的泄漏。

### 3. 「无色主题背景一直黑 + 生硬」

见 §2 最后一行：无色模式原本铺了两层灰（窗口层 35% + 面板层 40%），把模糊整块压掉；
现在口径是 **Dark=透黑 / Light=透白 / 无色=零色**。

---

## 四、顺手收口的"长期占用"既有坑

| 项 | 位置 | 处理 |
|---|---|---|
| 帧率图标默认开启 | `MenuBarStatusStrip` 启动应用显隐 | 改读目录的 `DefaultEnabled`（fps 默认关）—— 否则"每帧渲染订阅"默认就开着，断电审计的验收判据根本无法成立 |
| 空闲淡出仍在满帧出图 | `MenuBarWindow.SetIdleHidden` + 新增 `MenuBarShellVisibility` | 空闲隐藏时同步让帧率组件**退订** `CompositionTarget.Rendering` |
| 控制中心 2s SMTC 轮询常驻 | `ControlCenterWindow` | 补 `IsVisibleChanged`：隐藏即停表（面板走 `Hide()` 不触发 `OnClosed`） |
| CPU 面板 1s 原生采样 + LHM 句柄常驻 | `CpuPanelWindow` | 同上，新增 `Pause/Resume` |
| Dock 每秒 2 次全量窗口枚举 | `DockWindow._refreshTimer` | 接入 `ShouldPauseHighFrequencyWork`（挂起/恢复冷却期跳过本拍） |
| 桌面排序桥整机读注册表 | `DesktopBrowser.SortBridgeLoop` | `desktop.sortBridge=false` 时跳过读取 |
| 框选看门狗订阅累积 | `DesktopIconsControl` | Tick 处理器改为**命名方法 + 只挂一次**（原实现每框选一次多挂一个 lambda 且永不退订） |
| 托盘 UI 线程可能被永久挂住 | `tray/ProcessBridge` | `ReadToEnd`（**无超时**）改异步读 + 有界等待；三处超时分支补 `Kill(entireProcessTree)` 收尸，"只 Dispose 不 Kill"会留下孤儿子进程 |

---

## 五、验证指引（请在虚拟机上按序做）

### 第 1 步：跑一次探针（一次定案，最关键）

```powershell
dotnet build tools\BlurProbe -c Release -p:Platform=x64
tools\BlurProbe\bin\x64\Release\net8.0-windows10.0.19041.0\BlurProbe.exe
```
它会闪出几秒测试窗口，然后弹摘要并写出 `blurprobe-result.txt`。看两点：
- **顶部"系统前提"**：`系统透明效果` 是否为 True、是否省电模式 —— 这四项任一不满足，**任何配方都不可能出图**（这正是虚拟机最常见的情形）；
- **"分层 + BLURBEHIND"那一行是否 ✅**。若是 ✅ 而产品仍无模糊，问题就在我们代码侧（把结果发我）；
  若整体 ❌，先去「设置 → 个性化 → 颜色 → 透明效果」打开（我们的设置页也给了"一键开启"入口），并确认未处于省电模式。

### 第 2 步：日志确认

`%LocalAppData%\BetterDesktop\logs\host-*.log` 里搜 `[Vibrancy]`：
- `能力探测: 合成=… 透明效果=… 远程会话=… 省电模式=… → 无色模糊可用=…`（环境结论）
- `毛玻璃档位=BlurBehind；能力探测: …`（实际生效档位）

### 第 3 步：三项回归手测

1. **剪贴板**：确认安装目录里出现 `BetterDesktop.Clipboard.Panel.exe`（本批已修打包）；装完应有侧边「›」手柄、托盘「打开剪贴板历史」、全局热键三处入口。
2. **主程序拉起**：只启动 `BetterDesktop.Host.exe`，任务管理器里应能看到 `BetterDesktop.Agent.exe` 被一并拉起。
3. **dock 预览**：悬停运行中的窗口图标做预览——**不允许再出现窗口被关掉/重开变小**；dock 自身在预览期间不应整体变暗抽搐。出问题时日志里搜 `Peek`（`skip … reason=placement-unreadable` 表示我们主动放弃了那次 peek，属安全行为）。

### 第 4 步：无色主题观感

切到「无色」主题：窗口层应当**不再有灰底**，看到的是背景的无色模糊（Dark/Light 不受影响）。

---

## 六、追加批：「透白对菜单栏不生效、变纯黑」的根因与修复

**根因（一行代码，影响面却是全类）**：`MenuBarWindow` 的构造函数**漏了 `Events = events;`** ——
基类 `ShellWindow` 只有在 `Events != null` 时才订阅 `AppearanceChanged`，于是菜单栏
**只在首帧应用一次外观**：切「透白」时其它窗口都跟着变，唯独菜单栏停在旧主题（暗色/黑），
用户看到的就是"透白对菜单栏没成功、变成一块纯黑"。

顺带查出同一遗漏还有**两处**：
- `PopupWindowBase`（所有弹窗：声音/WiFi/蓝牙/控制中心/CPU/内存/麦克风/剪贴板面板…）都没订阅外观变更
  —— 弹窗是懒建后 `Hide()` 复用的，所以"关掉再开"才会跟随，只是不如菜单栏显眼；
- 基类 `DetachWindow()`（注释写着"卸载清理入口"）**从未被任何路径调用**（只有 `PluginHostWindow` 主动调）
  → 外观订阅**永不释放**，事件总线/静态广播会一直强引用已关闭窗口。

**修法（三层，互相兜底）**：
1. `MenuBarWindow` 补上 `Events = events;`（拿到事件总线的正规路径）；
2. 新增 `shell-core/Surface/AppearanceHub.cs` **进程内兜底广播**：`AppearanceService.Raise` 每次同时 Raise 一次，
   `ShellWindow` 在"没有事件总线"时回退订阅它 → 任何 ShellWindow 子类（含插件自建窗口）都不会再卡在旧主题；
3. `ShellWindow.OnClosed` 统一调用 `DetachWindow()`（幂等），并把静态订阅成对退订。

## 七、两个 P0

### 1. 更新器与看门狗无协调 → 更新失败 / 新旧混跑

新增 `updater/ResidentGate.cs`，`--apply` / `--rollback` 现在会：
1. 先写 `watchdog-pause.flag` **暂停看门狗守护**（3s 内生效）——否则它会用**旧 exe** 把刚被优雅停掉的
   Agent 在 8s 内拉回来，`WaitHostExit` 永远等不到"两者都退出"，60s 后以超时收场；
2. 停齐 **托盘 / 看门狗 / 桌面服务**（桌面服务走**命令管道优雅停止**，让它自己还原桌面图标与任务栏外观，
   超时才强杀，强杀另有图标恢复哨兵兜底）；
3. 替换完成后按"更新前是否在跑"逐一恢复（**看门狗最后起**，起晚才不会误判组件缺失而重复拉起）；
4. `finally` 保证**无论如何都撤掉豁免标记**（漏恢复 = 用户组件从此不再被自动拉起，比更新失败更糟）。

### 2. DesktopControl 判活按进程名 → 被同名短命进程骗过

「桌面控制菜单」是**短命进程、与常驻服务同名**（都是 `BetterDesktop.DesktopControl.exe`）。
按进程名判活会让"正在弹菜单"被当成"服务在运行"：① 真服务死了不被拉起；② 每弹一次菜单就制造
"在场→离场"抖动，驱动 Agent 反复装/摘桌面图标钩子与任务栏外观（用户观感"双击切换要按两次、
任务栏外观闪动"）。托盘一直用管道判活，现在统一收口：

- `shell-core/DesktopControl/DesktopControlPipe.IsServiceUp()` —— **权威实现**（托盘/宿主/CLI/Agent 共用）；
- `watchdog` 的 Desktop 目标新增 `UsePipeLiveness = true`；
- Agent `DesktopServiceSupervisor`：管道优先 + 进程名兜底（覆盖服务刚启动 1~2 秒管道未就绪的窗口）；
- Agent `HostPresenceWatcher`：管道判活 + **连续两次未命中才判离场**的迟滞（避免开机瞬间误判导致
  agent 抢装钩子、2 秒后又摘掉的抖动；方向选"先按在场"是安全侧，只会少干活不会双钩子）；
- 顺带修掉看门狗 `IsRunning` 只释放首个 `Process` 造成的句柄缓慢泄漏。

---

## 八、本批未做（建议下一批）

1. **注销不可验证**：`ComShellExtensionRegistrar.Unregister` 返回 void、CLI 恒 exit 0；且未清理
   `CommandStore`/逐扩展键 → 卸载后可能留下指向已删文件的孤儿右键项（届时 explorer 每次右键都要
   加载一个不存在的 in-proc COM 服务器）。
2. ~~**剪贴板 IPC 同步调用**~~ —— **本批已修**（见 §十）。
3. `appearance.material.glass` 目前默认关：本机实测"分层"路径已能出图，故无需开启；
   若 VM 上探针显示"分层 ❌ 而非分层 ✅"，再开它。
4. 已知**既有**且与本批无关的抖动：`shell-dock-tests` 的
   `ReflectionRtbAllocationTests.FullDockRebuild_RtbAllocation_UnderGate`（内存分配门槛断言，受 GC 影响，
   单独跑 3/3 通过；全量跑偶发 1 例红）——建议后续给它加容差或标记为不稳定。

---

## 九、追加批的验证补充

| 项 | 怎么验 |
|---|---|
| 菜单栏跟随主题 | 切「透白 / 透暗 / 无色」→ 菜单栏应**立刻**变化，不需要重启也不需要重开菜单栏 |
| 弹窗跟随主题 | 打开一次控制中心/声音面板 → 切主题 → 面板（保持可见时）颜色应立即跟随 |
| 更新让路 | 手动跑 `BetterDesktop.Updater.exe --apply --quiet`，更新器日志里应依次出现：已暂停看门狗守护 / 已停止看门狗 / 已停止托盘 / 已优雅停止桌面服务 / …（替换后）已恢复托盘 / 已恢复桌面服务 / 已恢复看门狗 / 已恢复看门狗守护 |
| 桌面服务判活 | 右键桌面弹一次「桌面控制」菜单 → 不应出现图标钩子/任务栏外观抖动；把桌面服务杀掉 → 看门狗应（按管道）把它拉回来 |

---

## 十、剪贴板 IPC 同步阻塞（用户点名的最后一项，本批修复）

### 四条根因

1. **同步 RPC 最坏冻结调用线程 7 秒**：`Call` = `WaitConnected(2s)`（内部 `Thread.Sleep(10)` 忙轮询）
   + `tcs.Task.Wait(5000ms)`。面板/宿主在 **UI 线程**上大量调用本类 →
   引擎半死时界面直接"卡住不动"（用户观感：点了没反应）。
2. **超时后连接永不恢复**：RPC 超时只抛异常，连接状态仍是"已连接"，于是之后**每一次**调用都必然再超时
   → 用户只能重启面板/宿主（这正是此前审计里那条"连接不再恢复"）。
3. **写没有超时**：消息模式管道的 `WriteFile` 在对端不读（引擎卡死/被挂起）时会**一直阻塞**，
   直到管道缓冲写满 → 客户端唯一的工作线程被卡在写里：心跳停发、所有在途请求超时、
   而且因为"状态仍显示已连接"，**永远不会触发重连**。
4. **50Hz 常驻唤醒**：`_wake.Wait(20)` 无论有没有活都 20ms 醒一次（本包零 kernel 依赖，接不到电源门控）。

### 修法

| 项 | 实现 |
|---|---|
| 异步优先（新增能力） | `ClipboardIpcClient.CallAsync(method, payload, timeoutMs, ct)`：不占调用线程，UI 路径推荐用它 |
| 等连接就绪 | `WaitConnected` → `WaitConnectedAsync`（`Task.Delay`），上限 **2s → 1s**，去掉忙轮询 |
| **超时自愈** | 连续 **2** 次 RPC 超时 → 主动 `OnDisconnected` 触发重连；**任何**入站响应（含错误响应）清零计数 |
| UI 线程保护 | `Call` 检测到 UI 线程（`SynchronizationContext.Current != null`，纯 BCL 判据）时把超时压到 **3s**；并对每个方法名**只记一次** `UI 线程同步 RPC（建议改 CallAsync）：<method>` 痕迹 → 这就是"还有哪些调用还锁着 UI 线程"的迁移清单（避免刷屏，也避免盲目改调用方） |
| 点击路径快失败 | `OpenHistoryWindow` 改用 **800ms** 超时：引擎半死时菜单不再卡 5 秒，立刻走"直接拉起面板 exe"降级 |
| **写超时** | `NamedPipeTransport`：连接改用 `PipeOptions.Asynchronous`，写改为 `WriteAsync(...).AsTask().Wait(3000)`；超时即 `Disconnect()`（解除底层挂起）并抛出 → 工作线程走既有重连路径 |
| 空闲降频 | 20ms ↔ 50ms 自适应：有在途请求或近 1 秒有入站流量用 20ms，否则 50ms（对剪贴板事件的观感延迟仍不可感知） |
| Dispose | 工作线程 Join **2s → 5s**：覆盖最长 3s 的写超时，避免线程在已释放的 `_wake`/`_transport` 上继续跑 |

### 验证

- 单测：`shell-clipboard-ipc-tests` **86** 全绿 + `shell-clipboard-tests` **10** 全绿。
- 真机复现（把"引擎半死"造出来）：任务管理器 → 挂起（Suspend）`BetterDesktop.Clipboard.Engine.exe`，
  然后在面板里搜索 / 点条目 / 点托盘「打开剪贴板历史」：
  - 界面**不应**再出现长时间无响应（最多一次 3s 上限 + 连接等待 1s）；
  - `%LOCALAPPDATA%\BetterDesktop\logs\ipc-client.log` 应能看到
    `rpc timeout x2: <method>` → `disconnected: rpc timeout x2: …` → 恢复引擎后自动 `connected`；
  - 恢复引擎（Resume）后**不需要重启面板/宿主**即可继续用（这就是"自愈"的验收点）。

---

## 十一、交付物溯源：`dist\modules\BetterDesktop-2026.09.18.0305-modules` 不包含本批修复

有人问"这个目录里的问题解决了吗" —— **没有**。实测时间线如下（务必按此判断，别被目录名误导）：

**这个目录名写的是 `0305`，但内容在今天 11:05–11:09 被"就地覆盖重新生成"过**：

| 包内文件（示例） | 时间 |
|---|---|
| `BetterDesktop.Kernel.dll` / `Shell.Core.dll` / `Shell.WindowTracker.dll` / `Shell.Clipboard.Ipc.dll` / `Shell.MenuBar.dll` / `Shell.Dock.dll` | 09-18 **10:57** |
| `BetterDesktop.Tray.dll` / `DesktopControl.dll` / `Updater.dll` | 09-18 **10:58** |
| `02-截屏` / `03-剪贴板面板` 的程序集 | 09-18 **11:05** |
| `install-betterdesktop.ps1` | 09-18 **11:33** |

→ 所以它**已包含 09-18 上午那批热修**（电源/Dock tick/状态栏等），这也是为什么此前比对发现包里的安装脚本与 `scripts/` 下的完全一致。

但本批修复全部发生在 **13:53 之后**，包里**一条都没有**：

| 问题 | 包内程序集时间 | 对应源码修复时间 | 包里是否有 |
|---|---|---|---|
| 问题 1a：剪贴板面板/截屏 exe 没进 01（"怎么都启动不了"） | `01-主程序` 中 `BetterDesktop.Clipboard.Panel.exe` **不存在**（实测 `False`） | `scripts/publish-modules.ps1` **13:57** | ❌ |
| 问题 1b：只起 Host 时 Agent 不被拉起 | `Host.dll` **10:57:11** | `host/Bootstrap.cs` **13:58** | ❌ |
| 问题 2：DWM 模糊失效（背景全黑） | `Shell.Core.dll` **10:57:06** | `VibrancyService.cs`/`BlurCapability.cs` **13:54**、`DwmHelper.cs` **14:03** | ❌ |
| 问题 3：dock 抽搐 / 关掉别人的窗口 / 重开变小 | `Shell.WindowTracker.dll` **10:57:06**、`Shell.Dock.dll` **10:57:09** | `WindowPeek.cs`/`RunningAppDetector.cs`/`ThumbnailWindow.cs` **13:56**、`DockWindow.xaml.cs` **13:59** | ❌ |
| 剪贴板 IPC 同步阻塞 / 超时不自愈 | `Shell.Clipboard.Ipc.dll` **10:57:05** | `ClipboardIpcClient.cs`/`ClipboardTransport.cs` **14:22** | ❌ |
| P0-2：更新链路与看门狗零协调 | `Watchdog.dll` **09-18 00:12**、`Updater.dll` **10:58:30** | `watchdog/Program.cs` **14:13**、`updater/Program.cs`+`ResidentGate.cs` **14:14** | ❌ |
| P0-3：同名短命进程骗过看门狗 | `DesktopControl.dll` **10:58:23** | `DesktopControlPipe.cs`/`DesktopServiceSupervisor.cs` **14:13** | ❌ |

### 由此暴露的打包卫生问题（与"稳定版反而更差"直接相关）

1. **目录名与内容时间不一致**：`...0305-modules` 的内容是 11:05–11:09 生成的 → 交付物**无法从路径自证版本**。
2. **同一个输出目录被就地覆盖重建**：包内 mtime 横跨 `09-17` → `09-18 00:12` → `10:57` → `11:05` → `11:33` 五个时段。
   一个文件夹里同时存在五个"版本的残留"，事后无法判断用户装的到底是哪一批。
3. `01-主程序/version.json` 的 informational **是同一个 commit**（脏工作区构建），**不携带"包含哪些热修"的信息**。

**建议**：每次交付都出新目录（脚本本来就带时间戳，别就地覆盖），并让 `version.json` 额外记录
**构建时刻 + 工作区是否脏 + 是否含上午热修**；本包请直接标记为**旧包、不可再发测试**。

### 要拿到"已修复"的包，必须重新出包

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1          # 全量 publish（含 Rust 引擎 / native / 第三方引擎拷贝，耗时较长）
powershell -ExecutionPolicy Bypass -File scripts\publish-modules.ps1  # 整理成 01..07 模块目录
```

第二条会生成**新目录** `dist\modules\BetterDesktop-<新时间戳>-modules`；可以加 `-Source dist\BetterDesktop-<某次>` 指定源。
出包后建议先做一次冒烟：`01-主程序` 里应**同时**存在 `BetterDesktop.Clipboard.Panel.exe` 与 `BetterDesktop.Capture.exe`
（本批新增的"并入 01"逻辑），否则问题 1a 在安装后仍会复现。

### 已执行的出包（2026-09-18 14:39–14:50）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1                                        # 14:39–14:42
powershell -ExecutionPolicy Bypass -File scripts\publish-modules.ps1 -Source dist\BetterDesktop-2026.09.18.0639  # 14:49
```

产物：
- 完整 dist：`dist\BetterDesktop-2026.09.18.0639`（**20549 文件 / 2913 MB**，`manifest.json` 5.65 MB）
  - `version.json`：`build = 2026.09.18.0639`、`informational = 1.3.0-2026.09.18.0639+b805f6e…`、`publishedAt = 2026-09-18T06:42:49Z`
- **模块包：`dist\modules\BetterDesktop-2026.09.18.0649-modules`** ← 这才是可以发测试的包

| 模块 | 文件数 | 大小 |
|---|---|---|
| 01-主程序 | 186 | 104 MB |
| 02-截屏 | 15 | 65 MB |
| 03-剪贴板面板 | 10 | 25 MB |
| 04-剪贴板引擎 | 1 | 3 MB |
| 05-搜索索引引擎 | 1 | 1 MB |
| 06-格式转换引擎 | 20374 | 2819 MB |
| 07-右键菜单扩展 | 1 | 0 MB |

冒烟结果（对照旧包实测）：

| 检查项 | 新包 | 旧包 `0305-modules` |
|---|---|---|
| `01-主程序\BetterDesktop.Clipboard.Panel.exe`（+.dll/.deps.json/.runtimeconfig.json 共 4 个） | ✅ 存在 | ❌ 不存在 |
| `01-主程序\BetterDesktop.Capture.exe`（同上 4 个） | ✅ 存在 | ❌ 不存在 |
| `Host.dll` / `Watchdog.dll` / `Updater.dll` / `DesktopControl.dll` | 14:39–14:41 | 00:12–10:58 |
| `Shell.Core.dll` / `Shell.WindowTracker.dll` / `Shell.Clipboard.Ipc.dll` / `Shell.MenuBar.dll` / `Shell.Dock.dll` | 14:39 | 10:57 |
| 输出目录是否就地覆盖 | 新目录（`0649`） | 就地覆盖（内容 11:05） |

**两条出包认知（避免下次踩坑）**：

1. `engines/` **不需要手工装配** —— 全新 `publish.ps1` 的产物自带 `engines/`（2858 MB，`06-格式转换引擎` 由 `publish-modules.ps1` 从源 dist 搬过来）。
   本次一开始按"engines 得自己拷"准备，实测后取消（本仓库该目录亦存在于仓库根，可作为对账基准）。
2. 只剩一个**需要真机确认**的点：`01-主程序` 里的面板 exe 是从 `03-剪贴板面板`（self-contained 发布）拷来的**自有 4 个文件**，
   它运行时用的是 **01 的**共享程序集与运行时（而不是 03 目录里的那份）。
   在虚拟机装完后请立刻试一次「打开剪贴板历史」（侧边手柄 / 热键 / 托盘三处都试），确认面板能拉起引擎并出界面。

---

## 十二、第二轮真机测试反馈（菜单栏入口消失 / 右键菜单不可用 / 托盘不在）

### 12.1 菜单栏右侧功能入口全部消失 —— **本批修复**（我方回归，教训必须记住）

**症状**：菜单栏右侧只剩「扩展中心」（+）的功能 UI，托盘 / CPU / 内存 / 音量 / 时间 / 搜索等入口全不见。

**根因**（`Contracts/ExtensionCatalog.cs` 的 `SystemFeatures`）：

1. `ExtensionDescriptor` 的 `DefaultEnabled` 是 record 可选参数，**默认 `false`**；
2. `SystemFeatures` 的 16 个系统功能条目**全部漏写** `DefaultEnabled` → 全是 `false`；
3. 此前没暴露，是因为消费方都把"缺省"**写死成 true**，这个字段等于没用；
4. 而本批为了修「**帧率默认开启 → `CompositionTarget.Rendering` 订阅常驻（风扇长转）**」这条电源红线，
   把两处消费方改成了"**取目录里的 `DefaultEnabled`**"：
   - `Status/MenuBarStatusStrip.cs`（启动时应用持久化显隐）
   - `Sections/MenuBarSection.cs`（「设置 → 菜单栏」页的开关初始态）

两处叠加 → **除帧率（本就该关）外，所有系统功能按钮默认一律折叠** → 真机表现就是"右区只剩扩展中心"。

**修法**（在单一真相源层修，而不是把消费方改回写死 true）：给 `SystemFeatures` 每个条目显式 `DefaultEnabled: true`，
**只有 `fps` 保持 false**（它是全库唯一订阅每帧渲染的组件，默认关是电源红线），并在目录里写清"为什么必须显式写"。

> **教训（建议进纪律）**：把一个"从来没人读过、但所有消费方都写死了安全默认值"的字段接上电，
> 等于**用回归换修复** —— 接线前必须先把该字段的**全部取值补成"当前实际生效值"**，再接线。

### 12.2 托盘程序不在 —— **本批补齐**（上一批只补了 Agent，漏了托盘）

上一批补了「宿主启动时确保 Agent 在跑」，但**托盘本身**同样只由"开机自启"或用户手动双击 `Tray.exe` 带起。
而 README 把"直接启动 `BetterDesktop.Host.exe`"列为合法入口 → 走这条路时任务栏没有托盘图标
（用户观感："托盘程序不在"，与"程序没装上"无法区分），且托盘菜单里的入口（设置中心 / 剪贴板面板 /
系统集成状态 / 应急恢复）全部无从触达。

**修法**：`host/Bootstrap.cs` 新增 `EnsureTrayRunning()`，与 `EnsureAgentRunning` 同一套幂等写法
（按进程名判活 → 同目录找 exe → 未运行才 `Process.Start`）；不做"复活抑制"，因为托盘的 `ExitTray`
只隐藏图标、不写留痕文件，而"用户主动启动宿主"= 要一个完整可用的外壳。

**另一个真机高概率原因（同批处理）**：Win11 默认把**新出现**的托盘图标折叠进任务栏「^」溢出区 ——
这一条仓库里早有记录（`TrayApplicationContext.ShowFirstRunTrayHint` 的注释就是为它写的），
但提示受**一次性标记文件**门控 → **升级安装的用户永远不会再被提示**，于是每轮测试都会重复反馈"托盘不在"。
修法：安装脚本 step 7 在启动托盘前**重新武装提示**（删 `tray-icon-hint.flag`），并在启动后**校验托盘进程真的起来了**
（静默启动失败与"没装"对用户完全一样，必须留下可见判据）。

### 12.3 右键菜单"注册了但功能无法使用" —— 需要现场取证

这一条**无法靠读代码定论**，它属于"注册态 / 快照 / 路径漂移"三类现场问题。仓库里已内置恰好为它设计的取证入口
（安装脚本与托盘「系统集成」菜单共用）：

```powershell
# 在出问题的那台机器上执行（用实际安装目录）
& "$env:LOCALAPPDATA\BetterDesktop\app\<build>\BetterDesktop.Cli.exe" --system-integration status
```

它逐行打印可解析的 `键=值`，其中三条直接指向本症状：

| 键 | 异常时的含义 |
|---|---|
| `comRegistered=False` | COM 扩展没注册上（菜单根本不出现） |
| `snapshotPresent=False` | **`%APPDATA%\BetterDesktop\shellmenu.json` 缺失 → 原生侧解析到 0 项**（注册了但菜单是空的） |
| `comPathDrifted=True` | 注册表里记的 DLL 路径 ≠ 当前部署路径（换过安装目录 / 残留旧注册）→ 用 `--system-integration repair` 修 |

配套取证日志：
- `%TEMP%\bdt-cli.log` —— CLI 侧痕迹（`--menu-batch` 是否被调用、动作是什么）；
- `%APPDATA%\BetterDesktop\shellmenu.json` —— 原生菜单的**唯一内容来源**（由宿主/桌面服务写入）；
- 原生 DLL 自身日志（`MenuModel.cpp` 的 `LogLine`）。

**下一步**：拿到 `status` 输出与上述日志后按上表逐条定位（大概率落在 `snapshotPresent` 或 `comPathDrifted`）。

> **同日实测证据（开发机）**：跑 `Cli.exe --system-integration status` 得到
> `comRegistered=True` 但 `comRegisteredPath=C:\...\dist\modules\BetterDesktop-2026.09.18.0649-modules\01-主程序\native\BetterDesktopShellMenu.dll`
> （= **打包目录**），而当前部署根是 `...\AppData\Local\BetterDesktop\app\2026.09.17.1610` → **`comPathDrifted=True`**。
> 这正是"右键菜单注册了但点了没反应"的典型形态：注册表里的 in-proc COM DLL 路径指向了一个**不在安装目录里**的位置
> （根因：安装时用了 `-SkipCopy`，`$target = $src` → 注册指向"源文件夹"，源文件夹一挪/一删，菜单即失效）。
> 修法：`--system-integration register`（幂等重注册）——**启动器已把它做成自动步骤**（见 §十三）。

---

## 十三、新增：一次性启动器 `BetterDesktop.exe`（本批实现，用户需求）

### 13.1 需求与定位

用户原话："做一个 betterdesktop.exe 程序，启动拉起所有的服务和确保所有功能正常可用，然后再让用户根据自己喜好决定的功能开关。
而这个程序，也只是一个启动，后续就没有他的事情了。"

它填的是此前**入口混乱**的坑（三条真机反馈都源于此）：
- 双击 `Host.exe` → 只保证壳自己（托盘 / Agent / 看门狗靠自启或用户手动）；
- 双击 `Tray.exe` → 有托盘但壳不一定起来；
- 装完第一次由安装脚本代劳，之后用户"不知道该点哪个"。

定位：**一次性**（跑完即退、不常驻、不注册自启）；常驻那一份仍然是托盘（自启值 `BetterDesktop.Tray`）。

### 13.2 启动流程（顺序有理由，不是随手排的）

| # | 步骤 | 为什么这么排 |
|---|---|---|
| 1 | 组件就位检查（16 个必需文件） | 先回答"这份安装是不是完整的"——缺件正是历史上"装了但功能不存在"的来源 |
| 2 | 清 `host/agent/desktop-stopped.flag` | 本次是「启动」语义（同托盘菜单里的"启动 X"），且**逐条报给用户看**，绝不静默 |
| 3 | 系统整合：`status` → 需要时 `register` | 修"注册了但不可用"（路径漂移 / 未注册） |
| 4 | 托盘（`Tray.exe`） | 它是**常驻控制面**，构造时就会 `EnsureAgentAutoStart` + `EnsureDesktopServiceAutoStart` → 先起它，顺手带起 Agent 与桌面服务 |
| 5 | 主程序（`Host.exe`） | 壳 UI；它自身启动路径还会补齐 Agent/托盘（本批新增），此处已是幂等 |
| 6 | 常驻驻留件核对（Agent / 桌面服务） | 只核对不重复拉起（重复拉起会与单实例互斥打架） |
| 7 | 看门狗（`Watchdog.exe`）**最后** | 它的职责是"组件消失后拉回来"，先起会与本次拉起抢时序（历史上出现过"反复拉/反复杀"） |
| 8 | 功能件核对（`engines\` 等） | 缺件只降级告警，不阻断启动 |

**尊重用户意图的两处例外**（不擅自覆盖）：
- `shellmenu-unregistered.flag`（用户显式注销过系统右键菜单）→ 不自动重注册，只在界面里提示"可重新启用"；
- `watchdog-pause.flag`（更新器接管期间写下）→ **不清除**、跳过启动看门狗，避免打断更新。

### 13.3 开关落地：为什么必须走 CLI（实证，不是设计偏好）

- `SettingsService` **没有文件监视**（`packages/shell/shell-settings/Services/SettingsService.cs` 全文无 `FileSystemWatcher`），
  外部进程直写 `settings.json` → **已经开着的**菜单栏 / Dock / 桌面**不会重载**（真机原话："点了没反应"）。
- 唯一正确通道是 CLI：`--toggle-key` 在宿主在线时**管道转发** → 宿主进程内改设置并广播 `SettingsChanged` → 立即生效；
  宿主不在时退回本地落盘（下次启动生效）。
- **翻转语义的陷阱**：`--toggle-key` / `--toggle-desktop` 是"取反"而非"设为"。所以启动器固定算法为
  **读当前值 → 与目标比对 → 只对不一致的调用翻转 → 复查落盘结果**；绝不"按勾选直接翻转"
  （那会在"本来就开着"时把功能关掉）。
- 当前值读不到（文件正被原子替换 / 解析失败）时**一律不动**并逐行标注：翻转式动作在"当前值未知"下可能反着切，
  对用户是"我明明没改，它自己变了"——比"没生效"更糟。

### 13.4 工程与打包接线

| 项 | 位置 |
|---|---|
| 新工程 | `launcher/BetterDesktop.Launcher.csproj`（`AssemblyName=BetterDesktop` → 出货 `BetterDesktop.exe`） |
| 依赖 | 只引 `Kernel`（DeploymentInfo）+ `Shell.Core`（DesktopControlLocator）——其余能力一律走既有跨进程契约 |
| 解决方案 | `BetterDesktop.slnx` 新增 `/launcher/` 与 `/launcher-tests/` |
| 打包 | `publish.ps1`：`$components` 里 `Launcher` 放**最前**；`$required` 新增 `BetterDesktop.exe` |
| 安装器 | `install-betterdesktop.ps1` 的 `$required` 同步新增（门禁 `verify-system-integration` 校验两者一致 → 已 PASS） |
| 单测 | `launcher-tests`（9 个）：开关目录 vs `DesktopToggleCatalog` 的**命令名/键/默认值逐字一致**（防静默错翻）、
状态解析（缺字段必须为"未知"而不是 false，否则会覆盖用户的注销意图）等 |

### 13.5 真机验证要点

1. 双击 `01-主程序\BetterDesktop.exe` → 应看到逐条出现的启动步骤（✓/!/×），结束后列出 12 个开关；
2. 关掉启动器 → 托盘图标应在（**Win11 新图标默认被折叠进「^」溢出区**，先点「^」看一下，
   安装脚本本批已把"首次提示"重新武装，升级安装也会提示一次）；
3. 若"系统右键菜单"那一步显示"已修复：注册路径漂移"→ 说明正是 §12.3 的漂移问题，修完右键立刻可用；
4. 改任意开关 → 点「完成」→ 每行会显示"已生效 / 未确认（原因）"；**有未确认项时窗口不会自动关闭**（失败不静默）；
5. 仅起 `Host.exe`（不用启动器）时，托盘/Agent 也应该在（本批 `Bootstrap` 补齐）。

### 13.6 本批出包

| 产物 | 路径 | 规模 |
|---|---|---|
| 完整 dist | `dist\BetterDesktop-2026.09.18.0757` | 20553 文件 / 2913 MB |
| **模块包（发这个）** | `dist\modules\BetterDesktop-2026.09.18.0807-modules` | 7 模块：01=189 文件/99MB、06=20374 文件/2819MB |

冒烟（`01-主程序` 内，与新包 `0649` 逐文件比对）：

| 检查项 | 结果 |
|---|---|
| `BetterDesktop.exe`（启动器本体） | ✅ 新增 |
| `BetterDesktop.deps.json` / `.dll` / `.runtimeconfig.json`（启动器运行必需，缺一不可） | ✅ 新增 |
| `BetterDesktop.Clipboard.Panel.exe` / `BetterDesktop.Capture.exe`（问题 1a 的修复） | ✅ 仍在 |
| 其余 186 个文件 | ✅ 一个不少（唯一差异是当时尚未生成的 `manifest.json`，已补齐） |

> **过程记录**：本次 `publish.ps1` 的会话在后半段（合并完文件、写 `version.json` 之后、算 `manifest.json` 之前）
> 被 shell 会话结束连带杀掉 —— 之后改用 **WMI 创建脱离会话的进程**（`Win32_Process.Create`）才能跑完。
> `manifest.json` 用与 `publish.ps1` **完全相同的算法**补齐（同字段顺序、同 `ConvertTo-Json -Depth 6`、
> 同 UTC `publishedAt`、`fileCount` 与逐文件 sha256），并已同步复制进 `01-主程序`。下次完整 publish 会正常生成。
> 供后续参考：在这个环境里跑长任务，`Start-Process` 起的子进程会随会话死亡，必须用 `Win32_Process.Create`。

---

## 十四、第三轮真机反馈（黑命令行窗口 / Host 未响应 / 右键菜单不稳）

### 14.1 点开关时"时不时"冒出一个黑色命令行窗口 —— **本批修复**

**现象**（真机截图）：一个标题为 exe 全路径的黑框，内容指向
`...\01-主程序\BetterDesktop.Clipboard.Engine.exe`。

**根因**：`watchdog/Program.cs:367`

```csharp
Process.Start(new ProcessStartInfo(t.ExePath) { UseShellExecute = true });   // 旧
```

`UseShellExecute = true` 对 **console subsystem** 程序等于"请系统给它开一个控制台窗口"。
而看门狗守护的目标里恰好有 `Clipboard.Engine`（`:163`，Rust 控制台程序）→
**每次"引擎不在 → 拉起"就弹一个黑框**。所谓"时不时"就是看门狗的轮询节奏：
刚开关功能（引擎被杀/被停）、引擎空闲自退、用户手动结束进程，都会触发一次。

**修法**（`TryLaunch`）：改用 `UseShellExecute = false` + `CreateNoWindow = true` + `WorkingDirectory = exe 所在目录`。

> **纪律教训**：这条"拉起纪律"（console 程序必须隐藏启动）仓库里已经写过**两遍**
>（`ClipboardEngineLauncher` / `IndexEngineLauncher` 的文件头），看门狗是**第三个拉起方**，此前漏了。
> 说明这类规则要按**拉起方**清点（谁 Start 进程），而不是按组件清点（哪个进程是控制台程序）。

### 14.2 点功能开关时 `BetterDesktop.Host 未响应` —— **本批修复**

**现象**（真机截图）：Windows 的"程序未响应"对话框，出现在做功能开关的过程中。

**根因链**：

1. 设置变更事件在宿主 **UI 线程**上派发（命令桥与设置中心都在 UI 线程改设置）；
2. `ClipboardPlugin.ApplyEnabledState` 在该事件处理里**同步**执行生命周期动作：
   `StopAll()` → 逐进程 `Kill()` + `WaitForExit(3000)`（面板 + 引擎）；
   `EnsureEngine / EnsurePanelEntry / OpenPanel` → `Process.Start`；
3. 另外 `WatchKeys` 分支里的 `ApplyConfiguration()` 是**同步 IPC**（引擎半死时最坏秒级）。

→ 每点一次开关，UI 线程被钉住数秒，Windows 直接给出"未响应"。

**修法**：`ClipboardPlugin` 新增 `RunOffUiThread(what, work)` —— 用**任务链串行**把重活挪到线程池，
UI 只负责"发出去"；`ApplyEnabledState` 变成薄包装（`ApplyEnabledStateCore` 才是原实现），
`ApplyConfiguration()` 也走同一条链。

> **为什么是任务链而不是各自 `Task.Run`**：顺序即语义 —— "先关再开"必须按序落地，
> 并发会留下"关了又开一半"的半开状态（引擎/面板互抢管道，是历史上修过的坑）。

**同类未修（记录在案，下一批）**：`packages/shell/shell-hotkey-panel/Sections/HotkeySettingsSection.cs:234/244/253/259`
在**设置窗口的 UI 线程**上直接调 `RestartEngineIfRunning / RestartCaptureIfRunning`
（改热键时会卡一下，最坏 ~3s）。改法同 14.2，但改动面在设置窗口，需一并评估其"应用结果提示"的时序。

### 14.3 右键菜单"时有时无"+"状态更新不及时" —— 分析（前两条已治，第三条为遗留）

三条线索叠加，一条一个层级：

| # | 线索 | 状态 |
|---|---|---|
| 1 | **注册路径漂移**（§13 实测 `comPathDrifted=True`：注册表指向**打包目录**）→ explorer 有时加载得到、有时加载不到该 in-proc COM DLL → "时有时无" | 启动器第 3 步已**自动重注册**（`--system-integration register`） |
| 2 | **宿主被卡住时，转发命令必然"时有时无"**：系统右键动作经 CLI → 命名管道 → 宿主；宿主 UI 线程冻住时，管道读取 2s 超时（`MenuCommandPipe.ReadTimeoutMs`）→ **该次命令被静默丢弃** → 用户看到"点了没反应"，下次又好了 | §14.2 已修（UI 线程不再被钉住） |
| 3 | **菜单快照只有唯一写入者**：`shellmenu.json` 只由**桌面服务进程**的 `DesktopPlugin.ApplyShellMenuRegistration` 写（宿主不写）。于是"桌面服务不在运行"时（例如把「自绘桌面」关掉），系统右键菜单的**项集与勾选态不会刷新** → "状态更新不及时"；而自绘菜单本身也依赖该进程 → 两边一起时好时坏 | **遗留**：需要设计决策（是否让宿主在服务缺席时兜底写快照），未在本批动 |

**若 1/2 修完后仍复现，请取这三样证据**：
- `%APPDATA%\BetterDesktop\shellmenu.json` 的最后写入时间（做一次开关，看它有没有跟着变）；
- `Cli.exe --system-integration status`（看 `comRegistered` / `comPathDrifted` / `snapshotPresent`）；
- 任务管理器里 `BetterDesktop.DesktopControl.exe` 是否在运行（不在运行 = 线索 3 成立）。

### 14.4 本批出包

| 产物 | 路径 | 说明 |
|---|---|---|
| 完整 dist | `dist\BetterDesktop-2026.09.18.1002` | 20553 文件；**缺 `manifest.json`**（见下方脆弱点 2） |
| **模块包（发这个）** | `dist\modules\BetterDesktop-2026.09.18.1011-modules` | 01=189 文件/99MB，含 `BetterDesktop.exe` |

冒烟（`01-主程序`）：`BetterDesktop.Watchdog.dll` 18:06、`BetterDesktop.Shell.Clipboard.dll` / `BetterDesktop.Host.dll` 18:03
= 本批两处修复均已进包；与上一包 `0807` 逐文件比对，**唯一差异是 `manifest.json`**（上一包有、本包暂无）。

### 14.5 发布链的两个脆弱点（本批实测踩到，已修其一）

1. **`publish.ps1` 的 manifest 步骤（已修）**：
   - `$files += ...` 在 2 万+ 文件上是 **O(n²)**（每次追加整数组复制）→ 该步要跑数分钟，看起来像卡死；改为 `List`。
   - 单文件 `Get-FileHash` 被瞬时占用（杀软/索引器）抛异常 → `$ErrorActionPreference='Stop'` 把它放大成
     **整个发布在最后一步中止**；改为重试 3 次，仍失败才终止。
2. **仍未解决 —— 该步骤在本机环境会被"无声终止"**：今天两次（一次是 shell 会话结束连带杀掉，
   一次即使已用 `Win32_Process.Create` 分离成独立进程也没能跑完），而脱离会话后 **stdout 无处可看**，
   现场表现就是"文件都合并好了、`version.json` 也写了，却没有 `manifest.json`"。
   **建议**：`publish.ps1` 应在关键阶段写**自己的进度日志文件**，而不是只写控制台 —— "发布没跑完"必须永远有据可查。

   **本次影响范围**：`1002` 这份 dist 没有 `manifest.json`。**对安装与测试零影响**
   （安装脚本与 `publish-modules.ps1` 都不读它，只用于"把这份 dist 当更新源"）。
   需要时重跑一次 `publish.ps1` 即可补齐。

---

## 十五、第四轮真机反馈（格式转换大面失效 / 右键开关键不生效）

反馈原话要点："比之前好多了，但右键的快捷功能还是部分无法使用，**特别是热键侧边面板的开关**和格式转换功能，
**格式转换 80% 无法正常使用，也缺少很多的功能选项**。"

### 15.1 格式转换 80% 不可用 + 选项缺失 —— **本批修复**（根因是"引擎树从没装进安装目录"）

**证据链**（三步，全部可在仓库里核对）：

1. 所有转换引擎都按 **`AppContext.BaseDirectory\engines\...`** 定位
   （`shell-convert/Services/ConvertEngine.cs:55` 的 soffice、各 `Engines\*.cs` 同款）。
2. `ConvertMenuService.AddConvertSubmenu`（`:228-231`）对不可用目标

   ```csharp
   if (!ready) { continue; }   // 只显示引擎就绪可用的目标（排除无关/缺失），不置灰展示不可用项
   ```

   → **不可用的目标整项隐藏**（不是置灰）→ 用户看到的正是"缺少很多功能选项"，而不只是"灰了一片"。
3. 打包把引擎树**排除在 `01-主程序` 之外**（`publish-modules.ps1` 的 `Where-Object { $_.Name -ne 'engines' }`），
   单独放成 `06-格式转换引擎`；而安装脚本只拷 `01-主程序\*` → **装完没有 engines**
   → 除纯托管图片/文本转换（ManagedEngine）外全部探不到 ≈ 只剩 20% 可用。
   另外 `EngineDownloader` 的"按需下载"是**刻意禁用**的（`IsSpecConfigured` 要求大小+SHA256，而两个 spec 都留空），
   所以也不能靠联网补齐。

**修法（三处，覆盖三种使用形态）**：

| # | 改动 | 作用 |
|---|---|---|
| 1 | `scripts/install-betterdesktop.ps1` 新增 step 3b：安装 engines | 安装时自动装引擎，两种布局都认（完整 dist 的 `engines\`、模块包的 `06-*\engines`）；找不到只 Warn（主程序仍可用），找到则拷进安装根 |
| 2 | `scripts/publish-modules.ps1` 的 06 模块改为**自解释布局**：`06-格式转换引擎\engines\` + 随附 `install-engines.ps1` | 让"脚本与 engines 同目录"这一契约（`scripts/install-engines.ps1:40`）成立 → 用户单独运行该脚本也有效 |
| 3 | 启动器 `VerifyFeatureFiles` | 缺失时不再只说"缺失"，而是给出**从哪儿拷到哪儿**（自动探测 `..\06-*\engines` 并写明目标路径） |

> 备注：`01-主程序` 仍不含 engines（2.8GB 不应塞进主模块）——所以**必须**靠上面第 1 条把它装到位。

### 15.2 右键菜单里的开关键点了不动（用户点名"热键侧板"）—— **本批修复**

**根因**：右键菜单的动作走 `--menu-batch`，而 batch 通道是**刻意免宿主**的（原生多选入口不走管道）。
`HeadlessExecutor.ToggleKey` 里那句注释的前提（"走到本方法即说明宿主缺席，宿主在线时命令行早已转发走掉了"）
**只对顶层 `--toggle-key` 成立**，在 batch 路径上不成立 → 只写 `settings.json`；
而 `SettingsService` **没有文件监视**（无 `FileSystemWatcher`）→ **正在运行的宿主不会重载** →
热键侧板 / 灵动岛 / 菜单栏 / Dock 这些"宿主内消费"的键点了就是没反应
（只有图标 / 任务栏这类"原生层当场生效"的键看起来正常，极具迷惑性）。

**修法**：`ToggleKey` 先 `MenuCommandPipeClient.TrySend("toggle-key", key)` 转交宿主
（在线则宿主进程内翻转 + 广播 `SettingsChanged`，插件立即响应），失败再回退本地直写（与旧行为一致，不会更差）。

### 15.3 顺带发现（**未修，需决策**）：`shell-convert` 在途改动让 6 个单测失败

`packages/shell/shell-convert-tests` 有 **6 个失败**，全部是 `docx → pdf` + 注入假引擎的用例，典型断言：

```
转换：Expected: ConversionFailed   Actual: EngineMissing
```

排查结论：
- 这 6 个失败**不是本批引入的**：`packages/shell/shell-convert/**` 有大量**未提交的在途改动**
  （`ConversionMatrix.cs` +299 行 / 45 处、`ConversionService.cs` ±92 行 / 24 处），且 12 小时内无人动过；
  我的改动一行都没碰 shell-convert。
- `EngineRegistry.cs` / `ConversionServiceTests.cs` **没有改动**（与 HEAD 一致）→ 测试是旧口径，
  而矩阵/服务是新口径：`docx→pdf` 已由 `PdfViaSoffice()` 表达（注释口径"pdf 全部 soffice→COM 兜底"），
  而旧测试只注入一个 `EngineKind.Soffice` 的假引擎 → 候选链里找不到它 → `EngineMissing`。
- **两种可能，需真机一句话判定**：
  - ① 测试过时（架构有意改成"两跳/COM 链"，产品功能不受影响）→ 装了 engines 后 `docx→pdf` 能成功；
  - ② 真回归（链里丢了可达引擎）→ 装了 engines 后仍报"引擎未就绪"。
- **我没有动它**：改动不是我做的、意图不明，盲目"改绿"的风险大于收益。

### 15.4 验证方法（装完新包后）

1. 跑安装（安装脚本会自动装 engines；注意会有"installing conversion engines (large...)"一步，耗时较长）；
   若看到 `no conversion engines found` 的 Warn → 说明手工解压的目录结构不标准，按提示把 `06-*\engines` 拷到安装根。
2. 右键一个 `.docx` → 「格式转换」：选项应明显变多（之前被整项隐藏的 soffice/pandoc/poppler/ffmpeg/tesseract/calibre 目标回来了）。
3. 若仍有个别转换报"引擎未就绪"→ 看 `%TEMP%\bdt-cli.log`（headless 转换痕迹）与
   `%LOCALAPPDATA%\BetterDesktop\logs` 里的 `引擎探测 X: 可用/缺失` 行，那就是 §15.3 的 ② 情形。
4. 右键「桌面控制」→ 点「热键侧板」：宿主在线时应**立即**显隐（不再只写盘不生效）。

---

## 十六、交付物依赖审计：包能不能"拷到别的电脑就跑"

用户问："这个压缩包是不是没把依赖放进去？导致在别的电脑上做不到百分百的效果？"
**答案是：是。有 2 项运行环境依赖没有进包，1 项大体积资产靠安装器补（本批已修）。**

### 16.1 实测证据

| 依赖 | 是否必需 | 包内是否自带 | 证据 |
|---|---|---|---|
| **.NET 8 Desktop Runtime** | **必需**（缺了整个程序起不来） | ❌ **没有** | `BetterDesktop.Host.runtimeconfig.json` 声明 `frameworks: Microsoft.NETCore.App 8.0.0` + `Microsoft.WindowsDesktop.App 8.0.0`（= **框架依赖发布**）；`01-主程序` 内 `coreclr.dll`/`clrclr`/`hostfxr.dll`/`hostpolicy.dll`/`PresentationFramework.dll` **全部不存在**；`BetterDesktop.Host.exe` 的导入表含 `hostfxr`（apphost 特征） |
| **VC++ 2015-2022 Redistributable (x64)** | **必需**（缺了 Rust/C++ 产物加载失败） | ❌ **没有** | 导入表实测：`convert-engine.exe` / `Clipboard.Engine.exe` / `Index.Engine.exe` → `VCRUNTIME140.dll`；`native\BetterDesktopShellMenu.dll` → `VCRUNTIME140.dll` + `MSVCP140.dll` + `VCRUNTIME140_1.dll`；而包内这些 DLL **一个都没有** |
| **`engines/` 第三方转换引擎树（2.8GB）** | 格式转换/OCR 必需 | ⚠️ 独立模块 `06-格式转换引擎` | 见 §15.1：安装器此前只拷 `01-主程序` → 装完没有 engines → 转换大面失效（**本批已让安装器自动安装**） |
| **Windows 版本下限** | 平台约束 | — | `Directory.Build.props` 的 TFM = `net8.0-windows10.0.19041` → **Win10 需 2004 (19041) 及以上**；更旧的系统上 apphost 直接拒绝启动 |

第三方引擎抽样也确认需要 VC 运行时的：`ffmpeg.exe`、`soffice.bin` → 依赖 `VCRUNTIME140.dll`
（`pandoc.exe`/`tesseract.exe`/`ebook-convert.exe` 只依赖系统 UCRT，不需要）。

### 16.2 本批已做的事（让失败不再无声）

`scripts/install-betterdesktop.ps1` 新增 **step 1b 运行环境预检**：
- 查 `.NET 8 Desktop Runtime`（`%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`，回退 `dotnet --list-runtimes`）；
- 查 `VC++ 2015-2022 x64`（`HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` 的 `Installed=1`，回退 System32 的 `vcruntime140.dll`/`msvcp140.dll`）；
- 缺失时打印**确切的下载链接**并 `Warn`；加 `-RequirePrereqs` 可升级为 `Fail`（无人值守安装用）。
- 两项都属于"装了但打不开"的高发原因，**必须在装之前说清楚**，而不是让用户面对一个静默失败的程序。

> 注意：这个检查**只能放在安装脚本里**，不能放在启动器里 —— 启动器本身是 .NET 程序，缺运行时时它根本跑不起来。

### 16.3 还没做：让包真正"拷过去就能跑"（需要你选一个方案）

| 方案 | 做法 | 代价/影响 |
|---|---|---|
| **A. 自包含发布（推荐）** | `publish.ps1` 的每个组件加 `-r win-x64 -p:SelfContained=true` | 免装 .NET 运行时；`01-主程序` 从 104MB → ~250MB（每个组件各自带一份运行时） |
| **B. 随包带运行时安装包** | 包内放 `windowsdesktop-runtime-8.x-win-x64.exe` + 安装器静默装 | 体积小；但需要一次安装动作（VC++ redist 还需管理员） |
| **C. 随包带 VC 运行时 DLL** | 把 `vcruntime140.dll`/`vcruntime140_1.dll`/`msvcp140.dll` 放进 `01-主程序`（微软允许随应用再分发） | 免装 VC++ redist；与 A 组合即"零依赖" |
| **D. 静态链接 CRT** | Rust 侧 `.cargo/config.toml` 加 `rustflags = ["-C","target-feature=+crt-static"]`（engine / engine-index / native\convert-engine 三处）+ C++ 侧 `MSVC_RUNTIME_LIBRARY=/MT` | 彻底不再依赖 VC++ 运行时；但要重编全部原生产物，且需重新验证 |

**建议 A + C（或 A + D）**：A 解决 .NET，C/D 解决 VC++，再叠加本批的 engines 自动安装 →
"干净电脑上解压/安装即可 100% 可用"。

### 16.4 方法论教训（今天连踩两次，值得记）

1. **二进制里查字符串必须忽略大小写**：导入表里的名字通常是 `VCRUNTIME140.dll`（大写）。我第一次用大小写敏感搜索 → 结论"无 VC 依赖"（**错**）；改成 `OrdinalIgnoreCase` 才拿到真值。
2. **`[IO.File]` 不认 PowerShell 的 `cd`**：它按**进程工作目录**解析相对路径，于是那批相对路径全部 `FileNotFound`，
   而我的 helper `catch { return false }` 把异常吞成了"没有此项" → **一整批假阴性**。凡是用 .NET API 读文件，
   一律 `Resolve-Path` 先取绝对路径（cmdlet 如 `Get-ChildItem` 才跟着 PowerShell 的位置走）。
   两条合起来就是今天的教训：**"没找到"必须先怀疑自己的检索方法，再下结论**。

---

## 十七、方案 A + D 实施：让包真正"拷过去就能跑"（用户拍板）

用户在 §十六 的方案表里选了 **A + D**（自包含发布 + 静态链接 CRT），并明确"第三方引擎显示缺失不用在意"。
第三方引擎相关的收尾（`engines/` 自动安装）已在 §15.1 完成，本节只做 A 与 D。

### 17.1 改了什么

| 方案 | 文件 | 改动 |
|---|---|---|
| **A. 自包含发布** | `scripts/publish.ps1`（10 个组件）、`scripts/publish-modules.ps1`（02/03） | 每条 `dotnet publish` 加 **`-r win-x64 -p:SelfContained=true`**（.NET 6+ 起"只给 RID"**不再**隐含自包含，必须显式；这一条容易漏） |
| **D. Rust 静态 CRT** | `engine/.cargo/config.toml`、`engine-index/.cargo/config.toml`、`native/convert-engine/.cargo/config.toml`（**新增**） | `[target.x86_64-pc-windows-msvc] rustflags = ["-C", "target-feature=+crt-static"]`（三个 crate 各自一份：cargo 从当前目录向上找 config，独立成 crate 就各自持有，不污染仓库其它 cargo 工程） |
| **D. C++ 静态 CRT** | `packages/shell/shell-context-menu/native/CMakeLists.txt` | `set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")`（= /MT；`cmake_minimum_required 3.20` 已隐含 CMP0091=NEW 才生效） |
| 安装器预检**自适应** | `scripts/install-betterdesktop.ps1` | ① 检测到**自包含**构建（同目录有 `hostfxr.dll`/`coreclr.dll`）→ 跳过 .NET 检查（A 之后它不再是前置）；② VC++ 检查**降级**为"仅影响少数第三方转换（ffmpeg/LibreOffice）"的提示（我们自己的产物已静态 CRT）；③ `-RequirePrereqs` 只对致命项（.NET 且非自包含）硬失败 |

### 17.2 验收（实测，不是推断）

- **A**：`dotnet publish watchdog -c Release -p:Platform=x64 -r win-x64 -p:SelfContained=true` →
  `runtimeconfig.json` 出现 **`includedFrameworks`**（自包含标志，而不是 `frameworks`），
  产物含 `coreclr.dll` / `hostfxr.dll` / `hostpolicy.dll` / `System.Private.CoreLib.dll`
  （零依赖小工具 95 MB / 189 文件）。
- **D**：三个 Rust 引擎 + `BetterDesktopShellMenu.dll` + `BetterDesktopMenuBroker.exe` **5/5**
  都不再含 `vcruntime140` / `msvcp140` / `concrt140`，**连 `api-ms-win-crt-runtime-l1-1-0`（UCRT api-set）都没有** —— 完全静态。
  体积变化就是证据：原生 DLL **116 KB → 274 KB**、broker **79 KB → 230 KB**、剪贴板引擎 **3.0 MB → 3.5 MB**（CRT 被编进去了）。
- **原生侧回归**：cmake 构建自带的 `ShellMenuSmoke` **40 项断言全过**、broker 自检全 `[ok]`、
  `[shellmenu] done.` → 静态 CRT 没有引入行为回归。

> **验收方法（可复用，务必忽略大小写）**：把文件按 ASCII 读成字符串，`IndexOf(needle, StringComparison.OrdinalIgnoreCase)`。
> 导入表里的名字是**大写** `VCRUNTIME140.dll` —— 这正是 §16.4 第 1 条踩过的坑。

### 17.3 体积影响（预计）

| 产物 | 之前 | 之后 |
|---|---|---|
| 01-主程序 | 104 MB | ~250–350 MB（10 个组件共享同一份运行时，合并时会去重） |
| 02-截屏 / 03-剪贴板面板 | 65 / 25 MB | 各 ~150 MB 级 |
| 模块包合计 | 3.0 GB | ~3.4–3.6 GB |

### 17.4 残留（已知、有意保留）

第三方引擎树里 **`ffmpeg.exe`、`soffice.bin` 仍导入 `VCRUNTIME140.dll`** → 音视频与 LibreOffice 系的少数格式
在**没装** VC++ 可再发行组件的机器上会失败。用户已明确这部分先不在意，故未处理。
若将来要彻底消除：把 `vcruntime140.dll`/`vcruntime140_1.dll`/`msvcp140.dll` 随包放进 `01-主程序`（微软允许随应用再分发），
或换用静态构建的 ffmpeg。（`pandoc.exe`/`tesseract.exe`/`ebook-convert.exe` 只依赖系统 UCRT，本来就没这个问题。）

### 17.5 顺带踩到的两个工具坑（留给后续长任务）

1. **日志编码混用**：先用 `Out-File -Encoding UTF8` 写头，再用 `Tee-Object -Append` 追加（PS5.1 的 Tee 默认 UTF-16）→
   同一文件里两种编码，读出来是乱码。长任务日志应**一次成型**：`*>&1 | Out-File -FilePath x.log -Encoding utf8`。
2. **批处理 wrapper 会无声卡死**：我那个"串行跑三个 cargo + cmake"的 wrapper 最后卡在未知状态
   （无任何子进程、日志不再增长、进程仍存活）。长批处理**不要用 Tee 管道串多个重命令**，
   宁可每个子步骤写自己的日志文件、各起各的进程。
