# 全项目收口扫描（第二轮）

日期：2026-09-14（承接同日 `2026-09-14-duplication-audit.md` 与 `2026-09-14-clipboard-duplication-deepdive.md`）
前置状态：剪贴板 legacy 整层删除 + 契约清理 + 图标链收口 + 毛玻璃语义修正已完成；本文件是**重新全量扫描**的结果。
判断规则（用户给定）：**对业务功能无影响、且不拖累主程序 → 直接进删除/收口行列**。

## 0. 编译基线

全量重编（清 bin/obj 后）：**71 秒**，`error CS = 0`、`warning CS/CA = 0`；剩余 8 条 MSB3021/3027 文件锁错误全部来自
`packages/shell/shell-status-tests`，被一个 **10:44 遗留的僵尸 `testhost`(PID 17220)** 锁住输出目录
（`Process.Kill()` / `CIM Terminate` / `taskkill` 均无效：进程表里有它、按 PID 查不到 → 需重启机器才消失）。
其余 53 个工程全部生成成功。

## 1. 文本级克隆（k=12 行滑窗）

活代码 454 个 .cs，**≥12 行克隆 20 处**（清理前 21 处；剪贴板两套面板那条已随删除消失）。最长的仍是
`shell-clipboard-ipc/ClipboardTransport.cs:15` ↔ `shell-index-ipc/IndexTransport.cs:15`（42 行）。
其余 19 处集中在：菜单弹层 ×3、设置分区 UI 助手 ×6、开始菜单布局 ×3、host↔desktop 图标恢复 ×2、
Convert 引擎 ×1、Wifi/Bluetooth 弹窗 ×1、desktop 内部 ×2、ipc-tests ×1。**没有新增长块复制。**

## 2. 可立即收口（无业务影响、不拖累主程序）—— 2026-09-14 已执行

| 项 | 规模 | 收口动作 | 结果 |
|---|---|---|---|
| **csproj 公共属性重复** | 54 个 csproj × 5 个属性 = **270 行** | 上提到 `Directory.Build.props`（`Condition="'$(MSBuildProjectExtension)' == '.csproj'"`，C++/CMake 工程不受影响） | ✅ **61 个 csproj 删除 300 行**；`tools/bamldecode` 保留自己的 TFM（`net8.0-windows`）；全量编译 0 错 0 警告；4 个不在解决方案内的工程（bamldecode / poc / Temp×2）单独编译也 0 错 |
| **`NullVibrancy` 空壳 ×5** | desktop / quick-note / menu-bar ×2 / 组件演练场（各约 10 行；其中一处的注释写明"为了避免相互 internal 引用"才复制的） | 新增 `shell-core/Vibrancy/NullVibrancyService.cs`（public 单例），5 处全部改用它 | ✅ 删 5 份私有空壳；`internal 不可见`这个复制理由随之消失 |
| **`IsDesktopHostClass` ×2** | desktop/DesktopWindow:572、dock/DockWindow.DesktopLayer:180 | 新增 `shell-core/Native/DesktopHostWindow.cs`（`IsHostClass` + `ClassOf`） | ✅ 两处收口；顺带删掉 dock 自带的 `GetClassNameW` P/Invoke 声明 |

## 3. 需逐点核对后才能收口（不能当"零风险删除"）

**HICON→ImageSource 转换 21 处 / 11 文件**：`CreateBitmapSourceFromHIcon + Freeze` 是共同骨架，但**句柄所有权有三种模型**，抽一个 helper 会踩所有权：

| 模型 | 实例 | 语义 |
|---|---|---|
| 转完立即 `DestroyIcon` | `shell-core/Surface/ShellItemIcon`、`shell-dock` 的 `GetStockIcon`、`shell-context-menu/MenuItemIconCache:154`、`shell-desktop/DesktopIconsControl:1980`、`FolderBrowserWindow:447` | 调用方不管句柄 |
| 句柄**保留**、稍后 `ReleaseIcon` | `shell-menu-bar/Services/MenuBarExtensions.cs:124`（`_currentIconHandle` 延迟释放） | 不能立即销毁 |
| 由托管包装持有并 `Dispose` | `shell-menu-bar/Windows/ProcessAppInfo.cs:171`（`Icon.ExtractAssociatedIcon` → `Dispose`） | 不走 DestroyIcon |

要收口就得提供两个变体（`FromHIconAndDestroy` / `FromHIconKeepHandle`）并逐站点确认所有权 —— 属**重构**而非删死代码。

## 4. 需真机验证才能收口

- ✅ **DefView 查找 5 份 → 1（2026-09-14 已执行）**：收口到 `shell-core/Native/DesktopHostWindow.cs`
  （`FindDefView` / `FindMountPoint` / `FindIconListView`）；`host/IconRestoreSentinel`、`shell-desktop/DesktopPlugin` ×2、
  `shell-desktop/Windows/DesktopWindow`、`shell-dock/DockWindow.DesktopLayer` 全部改走它。
  **过程中发现并修掉一个真实缺陷**：其中 3 份（`DesktopPlugin` ×2、`IconRestoreSentinel` ×2）的 WorkerW 回退分支
  以 Progman 为父去 `FindWindowEx("WorkerW")`，而 WorkerW 是**顶层窗口**（agent 侧真机实测：15 个顶层 WorkerW
  的父窗口全为 0）→ 壁纸引擎/自绘桌面接管后 DefView 被迁到顶层 WorkerW 下，这些回退**永远找不到**，
  正是注释里那句"隐藏成功、恢复失效"的根因。统一采用顶层遍历（= agent 侧已验证写法）。
  `agent/Capabilities/DesktopIconsCapability.cs` 保留自足实现（该进程刻意不引 shell-core，且它本来就是正确写法）。
  ⚠️ 仍待真机：宿主强杀 → 桌面图标恢复（`IconRestoreSentinel` 那条安全网）；有壁纸引擎时图标显隐切换。
- **菜单弹层 3 份**（`DesktopMenuPopup` / `DockMenuPopup` / `StartMenuPopup`，全部活跃且锚点语义不同）：
  合并不等于删除，改错直接坏三处右键菜单。
- **设置分区 UI 助手 ×6 组克隆**（`SettingsUi` 已收口 `GroupCard`，但 `TitleBlock`/说明/开关行仍各写各的）：
  往 `SettingsUi` 加 3–4 个原语，逐分区肉眼对照。

## 5. 校正：上一轮两处判断过头，撤回

1. **`shell-window-tracker/DebugLog` 不是死代码**（我上一轮说"与 M10 单管道冲突、建议回归 `DiagnosticLog`"——**错**）。
   它**同步**追加写桌面 `BetterDesktop_debug.log`，注释写明理由正是崩溃取证："崩溃前最后一帧"；
   而 `DiagnosticLog → host/FileLogSink` 是**异步批量**（`ConcurrentQueue` + 50ms 轮询 + 批后 Flush，
   `FileLogSink.cs:52-81`）——硬崩会丢队列。两者**不等价**，删它等于丢能力。**保留。**
2. **6 套进程级日志不是重复**：`PanelLog` / `AgentLog` / `TrayLog` / `UpdaterLog` 分属 4 个独立 exe，
   两个 IPC 客户端内部 `Trace` 只写自己的诊断文件；抽象收益为负。**保留。**

## 6. 明确不进收口行列

- **IPC 传输 42 行克隆**（clipboard / index）：两端超时、分帧、心跳策略不同，抽公共层要动两条活跃 IPC 链路。
- **Convert 引擎样板**（Calibre/Tesseract）、**开始菜单 4 种布局**（Win7/Win10/Classic/AllApps）、
  **Wifi/Bluetooth 弹窗**：差异即价值。
- **引擎 autosave 800ms**：性能项，不是死代码。
- **面板侧 0 单测**：缺测试，不是多代码。

## 7. 执行顺序与状态

1. ✅ **批 4**：`NullVibrancy` ×5 + `IsDesktopHostClass` ×2 —— 已收口（见 §2），受影响测试全绿。
2. ✅ **批 5**：csproj 公共属性上提 —— 已收口（61 个文件 / 300 行 → 5 行），全量编译 0 错 0 警告。
3. ✅ **批 6-1**：DefView 查找 5 份 → 1（见 §4，含 WorkerW 顶层遍历缺陷修复）。
4. ⏳ **批 6-2 起**（需定时机/真机）：HICON helper（逐站点核对句柄所有权，见 §3）→ 菜单弹层三合一 → 设置分区 UI 助手。
   注：图标链主路径（`ShellItemIcon` + dock/desktop 两个调用方）**在本轮之前已完成**，§3 指的是余下 21 处转换站点。

## 8. 验证记录（批 4 + 批 5 + 批 6-1）

- 全量编译（重启后）：`error CS = 0`、`error MSB = 0`、`warning CS/CA = 0`（僵尸 `testhost` 随重启消失，之前的 8 条文件锁已清零）。
- 单测：`shell-core-tests` 39/39、`shell-clipboard-tests` 10/10、`shell-clipboard-ipc-tests` 86/86、`shell-menu-bar-tests` 34/34、`shell-dock-tests` 54/54、`shell-window-tracker-tests` 5/5 —— 合计 **228 通过 / 0 失败**。
- 单列编译（不在解决方案内）：`tools/bamldecode`、`poc/system-status-poc`、`Temp/IndexBaselineProbe`、`Temp/PerfBenchmark` —— 均 0 错 0 警告。
- 批 6-1 净删除：`DesktopHostWindow` 查找实现 5 份（≈120 行）→ 1 份；dock 侧再删 2 条自建 P/Invoke 声明。
