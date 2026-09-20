# Cairo 开发计划：菜单栏 z-order 自愈看门狗

> Task: 菜单栏被置顶层浮层（热键面板）覆盖时"可见但不可用"，增加自愈能力，1 秒内抬回置顶层顶。
> 证据基于当前源码验证（小改动，不钉 commit）；技术力文档未命中（检索词：菜单栏/可用性/看门狗/置顶/输入）。

## Objective (§1)

菜单栏作为 shell 常驻条带**永不被任何窗口盖住**：周期性自检顶部条带命中测试，被置顶层内其他窗口（如热键面板）覆盖时自动 `SetWindowPos(HWND_TOPMOST)` 抬回，恢复可点击。用户感知：菜单栏"卡一两秒后自动恢复可用"，不再依赖修完其他板块。

## Current Behaviour (§2–3)

- 菜单栏 `MenuBarWindow : ShellWindow`，`DefaultTopmost = true`（置顶），贴主屏顶部（`Reposition`：Left=0/Top=0/宽=PrimaryScreenWidth），注册顶部 AppBar。
- 热键面板 `HotkeyPanelWindow : PopupWindowBase`，基类 `PopupWindowBase` **第 54 行 `Topmost = true` 强制置顶**；340px 宽可停靠任意位置（`RestorePosition` 记忆位置），可操作态（右 Alt 悬停）解除穿透（`SetPassThrough(false)`）。
- 置顶层内 z-order：**后置顶的在上面**。热键面板停在屏幕右上 + 非穿透态 → 面板矩形盖住菜单栏右区 → 用户点击落在面板：hover 无高亮、点击无效；菜单栏可见（面板只盖右区）；dock（底部）可点；面板自身正常（"对自身影响不大"）。

## Findings (§4–5)

- `[verified]` `shell-core/Windows/PopupWindowBase.cs:54`：`if (CanSetProperty("Topmost")) Topmost = true;` —— 热键面板置顶。
- `[verified]` `shell-menu-bar/Windows/MenuBarWindow.cs` 状态机：空闲隐藏（`SetIdleHidden`）时 `IsHitTestVisible=false` + Opacity=0（**不可见**，与用户"可见但不可用"不符，排除）。
- `[verified]` 全仓无 `EnableWindow`/`DisableAllWindows`（排除原生禁用）；`FileSearchProvider` 引擎 IPC 已包 `Task.Run`（排除 UI 阻塞）；菜单栏自身 0 处 `CaptureMouse`（排除自身捕获被打断）。
- `[verified]` `shell-core/Native/NativeMethods.cs`：`WindowFromPoint`(:828) / `SetWindowPos`(:211) / `POINT`(:456) 均已存在，可复用。
- 验收等价断言：菜单栏应可见时，顶部条带采样点 `WindowFromPoint` **必须命中菜单栏自身 hwnd**；命中其他窗口 = 被盖 = 应抬回。空闲隐藏态（`_idleHidden==true`）**不抬**（隐藏态本就不该可见/命中）。

## Proposed Changes (§6)

**文件：`packages/shell/shell-menu-bar/Windows/MenuBarWindow.cs`**（唯一改动文件）

1. 新增 `_watchdogTimer`（`DispatcherTimer`，1s 间隔），与 `_idleTimer` 并列（职责分离，可独立启停）。
2. 新增 `WatchdogTick()`：
   - `_idleHidden` 或 `_hwndSource` 为 null → return（空闲隐藏态不抬）。
   - 取 `GetWindowRect(hwnd)` 物理矩形，采样**顶部条带左/中/右三点**（各内缩 2px，避开边框）：
     - 命中 == hwnd → 正常（继续）；
     - 命中 != hwnd（其他窗口/桌面）→ 被盖 → `SetWindowPos(hwnd, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` 抬回置顶层顶，break。
   - **位置自愈**：`rect.Left != 0 || rect.Top != 0`（物理贴顶检查，容差 1px）→ `SyncAppBarPosition()` 重新贴顶。
3. `OnSourceInitialized` 中 `_idleTimer.Start()` 旁启动看门狗（tick 包 try/catch，防 Dispatcher 未处理异常）；`OnClosed` 中停止。
4. 局部常量：`HWND_TOPMOST = (IntPtr)(-1)`、`SWP_NOMOVE=0x0002`、`SWP_NOSIZE=0x0001`、`SWP_NOACTIVATE=0x0010`（不扩散到共享 NativeMethods，改动面最小）。

**不做**（§12 deferred）：热键面板侧根因（RestorePosition 后避开顶部条带、可操作态穿透恢复强化）——用户诉求是菜单栏侧韧性，跨板块改动另立计划。

## Implementation Sequence (§7)

1. MenuBarWindow.cs 加常量 + `_watchdogTimer` 字段 + `WatchdogTick`/采样方法。
2. OnSourceInitialized 启动、OnClosed 停止。
3. `dotnet build packages/shell/shell-menu-bar/BetterDesktop.Shell.MenuBar.csproj -c Debug`（0 警告 0 错误）。

## Test Strategy (§8)

- 无新增单测（判定逻辑为 2 行 UI 行为，真机验证更有效；`WindowFromPoint`/`SetWindowPos` 为既有 P/Invoke）。
- 构建验证：shell-menu-bar 0 警告 0 错误。
- **真机场景走查（DoD D1）**：重启宿主 → 热键面板拖到屏幕右上 → 右 Alt 悬停进可操作态 → 菜单栏右区应不可点（复现）→ 等 1–2s → 菜单栏右区**自动恢复可点**（看门狗抬回）→ 面板仍可用。

## Implementation Context (§11)

插入点：`MenuBarWindow` 字段区（`_idleTimer` 旁）、`OnSourceInitialized`（`_idleTimer.Start()` 后）、`OnClosed`（`_idleTimer.Stop()` 旁）。复用 `NativeMethods.WindowFromPoint/GetWindowRect/SetWindowPos`。

## Assumptions and Open Questions (§12)

- 假设：菜单栏被盖 = 异常（用户无"故意让窗口盖菜单栏"的合理场景）；全屏应用打开时菜单栏本就在顶部（看门狗不引入新行为）。
- deferred：热键面板侧根因修复（面板避开顶部条带）——无触发条件不自动做，用户确认后另立计划。
- 技术库回写（z-order 看门狗模式 + TOPMOST 浮层覆盖菜单栏的坑）——交付且真机验证通过后按积累 skill 入库，本次不动库。

## Definition of Done (§13)

- D1 [场景走查] 热键面板停右上 + 可操作态 → 菜单栏右区不可点 → ≤2s 自动恢复可点（真机）。
- D2 [构建] `dotnet build shell-menu-bar -c Debug` 0 警告 0 错误。
- D3 [回归] 正常使用菜单栏（左区/右区按钮、弹窗）无行为变化；空闲隐藏逻辑不受影响。

## Handoff to 技术力应用 (§14)

| 项 | 填写 |
|---|---|
| 模式判定 | 无匹配库文档 → 工程代码权威（成熟工程增量，复用既有 P/Invoke 与状态机） |
| 注入清单 | 无（不注入技术库文档；本次改动以 `MenuBarWindow.cs` 既有模式为唯一标准） |
| 适配参数 | 语言 C#/WPF；插入点 `MenuBarWindow`（字段区/_idleTimer 旁、OnSourceInitialized、OnClosed）；常量局部定义；间隔 1s |
| 禁区 | 不碰热键面板（HotkeyPanelWindow/PopupWindowBase）；不动空闲隐藏状态机；不扩散常量到共享 NativeMethods |
| DoD 核销表 | D1 真机（宿主重启后）；D2 构建命令；D3 真机正常使用走查 |
