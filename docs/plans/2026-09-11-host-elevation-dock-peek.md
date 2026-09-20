# Host 提权 → 改为 DWM Live Preview：dock 缩略图 Peek 对高完整性窗口生效 — 2026-09-11

> 计划类型：功能修复（窗口操作链路替换）· form: compact
> 技术力检索：命中 `1414-window-activate-restore`（UIPI 回退先例）、`1413-window-peek-zorder`（Peek 本体）；
> **无现成「DwmActivateLivePreview / Aero Peek」资产 → 新建**（检索关键词：DwmActivateLivePreview / AeroPeekType / Live Preview / DwmActivatePeek）。
> 方案来源：cairoshell 标准做法（下载版 `D:\迅雷下载\cairoshell-master` + ManagedShell 源码/IL 反编译 + betterdt 版 cairoshell-master，2026-09-11 核验）。

## 1. 目标（场景语言）

dock 定位为任务栏替代品。现状：缩略图能弹出、能渲染（DwmReg/DwmUpd ok），但**悬停缩略图把窗口抬起来（Peek）对管理员权限窗口必然失败**（UIPI 拒绝 SetWindowPos，err=5）。

**验收场景**：任务管理器 / 绝区零 / 辅助工具（管理员运行）最小化 → 悬停 dock 图标弹出缩略图 → 鼠标移入缩略图 → **窗口以 Aero Peek 效果浮现（不激活）** → 移开 → 预览关闭、窗口还原；点选缩略图 → 窗口直接激活。

## 2. 根因与标准答案

- **根因**：`WindowPeek.cs` 用 `SetWindowPos` 改 Z 序抬窗 → UIPI 拒绝高完整性目标（宿主 IL8192 < 目标 IL12288）。
- **标准答案（cairoshell / ManagedShell，[verified]）**：Windows 任务栏的悬停预览是 **DWM 合成器层的 Aero Peek / Live Preview**，不是 Z 序操作，**天然免疫 UIPI**——中完整性进程即可对管理员窗口生效。API：**`DwmActivateLivePreview(enable, targetHwnd, callingHwnd, AeroPeekType.Window)`**（dwmapi.dll，正式文档化 API，Win7+）。

```csharp
// ManagedShell WindowHelper.PeekWindow（标准实现，IL 反编译 + 源码核验）
public static void PeekWindow(bool show, IntPtr targetHwnd, IntPtr callingHwnd)
{
    uint enable = show ? 1u : 0u;
    if (EnvironmentHelper.IsWindows81OrBetter)
        NativeMethods.DwmActivateLivePreview(enable, targetHwnd, callingHwnd, AeroPeekType.Window, IntPtr.Zero);
    else
        NativeMethods.DwmActivateLivePreview(enable, targetHwnd, callingHwnd, AeroPeekType.Window);
}
// 开始：DwmActivateLivePreview(1, targetHwnd, dockHwnd, Window)  鼠标进入缩略图
// 结束：DwmActivateLivePreview(0, IntPtr.Zero, IntPtr.Zero, Window) 鼠标离开
```

- 触发时机（cairoshell TaskThumbnail，[verified]）：`bdrThumbInner_MouseEnter → PeekWindow(true, hwnd, taskbarHwnd)`；`MouseLeave → PeekWindow(false, ...)`。
- 新版 cairoshell（betterdt 版）用等价未文档化 API `DwmActivatePeek(hwnd, taskbarHwnd, peek)`，同样 DWM 层免疫 UIPI（[verified] `CairoDesktop.Interop/DwmApi.cs:96`）。
- **结论：不需要提权。** 方案 A（manifest requireAdministrator）/ B（计划任务）/ C（helper）全部搁置，无 UAC、无架构改动、无跨完整性通信问题。

## 3. 改动清单（文件级）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `packages/shell/shell-window-tracker/Native/*`（或现有 NativeMethods） | 新增 P/Invoke：`DwmActivateLivePreview(uint enable, IntPtr targetHwnd, IntPtr callingHwnd, int type, IntPtr unknown)` + `AeroPeekType` 常量（Window=2，以 ManagedShell 反射值为准） |
| 2 | `packages/shell/shell-window-tracker/Thumbnail/WindowPeek.cs` | `Begin(hwnd)`：抬窗改为 `DwmActivateLivePreview(1, hwnd, callingHwnd, Window)`；`End()/Cancel()`：`DwmActivateLivePreview(0, 0, 0, Window)`；**移除 SetWindowPos Z 序操作与 topmost 回退**（不再需要）；`callingHwnd` 取缩略图宿主窗口句柄（ThumbnailWindow 句柄，参照 ManagedShell 用 taskbarHwnd 的语义） |
| 3 | `packages/shell/shell-window-tracker/Thumbnail/ThumbnailWindow.cs` | 视情况：把宿主句柄传给 WindowPeek；最小化窗口的 `ShowWindowAsync(SW_SHOWNOACTIVATE)` 恢复逻辑**保留**（Aero Peek 对最小化窗口的浮现由 DWM 处理，先显式恢复更稳，实测后按日志定） |
| 4 | 不改：`RunningAppDetector.cs`（1414 激活链路）、`DwmThumbnail.cs`、`DockWindow.xaml.cs` | 激活继续走 AttachThreadInput + SetForegroundWindow + relaunch 回退 |

## 4. 约束与红线

1. **只替换 Peek 抬窗机制**，不动激活链路（1414）与缩略图渲染链路（DwmThumbnail）。
2. `DwmActivateLivePreview` 结束调用必须成对（Begin 后必然 End，含 MouseLeave/浮层关闭/宿主退出路径），防"预览卡住"。
3. 失败不抛：DwmActivateLivePreview 返回 HRESULT（0=成功），失败打日志不阻断主流程（参照 cairoshell "Peek失败不影响主程序"）。
4. **不动任何提权相关**：manifest、Tray/watchdog 启动链路、管道 DACL 均零改动。
5. `TreatWarningsAsErrors=true` 保持。

## 5. 验证与 DoD（核销表）

| # | DoD | 验证方式 | 核销结果（2026-09-11 晚） |
|---|---|---|---|
| 1 | `dotnet build BetterDesktop.slnx` 0 错 0 警 | 构建 | ✅ 通过（0 错 0 警；另修复 DesktopControlMenu.cs 缺 using 阻塞项） |
| 2 | shell-window-tracker 12/12、shell-dock 11/11 单测仍绿（Peek 相关测试按新机制断言更新） | `dotnet test` | ✅ 通过（core-tests 37/37；WindowPeekTests 3 Fact 边界测试；dock 11/11） |
| 3 | **场景走查**：管理员窗口（任务管理器）最小化 → hover 图标 → 移入缩略图 → **窗口 Aero Peek 浮现** → 移开 → 预览关闭 | 真机手动（关键 DoD） | ✅ 机制闭环（对照实验 22:3x）：对高完整性窗口 hwnd=0x1016A(IL12288) 实调 `DwmActivateLivePreview #113` begin hr=0 / end hr=0；同窗口 `SetWindowPos(TOP)` → False err=5（复现 UIPI 根因）→ 证明 DWM 层免疫 UIPI。**GUI 悬停走查待用户确认**（见下） |
| 4 | 普通窗口（Chrome）与最小化窗口同样生效；点选缩略图 → 窗口激活（1414 链路） | 真机手动 | ⏳ 待用户悬停确认（1414 激活链路未动，机制不受影响） |
| 5 | 日志判据：无 `SetWindowPos failed err=5`；Peek begin/end 成对（DwmActivateLivePreview 调用记录） | 桌面 BetterDesktop_debug.log | ✅ 代码层无 SetWindowPos 路径（已删除）；P/Invoke 探测 begin/end hr=0 |
| 6 | 浮层关闭/宿主退出时预览必被 End（无残留"假预览"） | 真机手动 + 日志 | ✅ 代码层 Begin/End/Cancel 全路径成对（含回滚）；单测 End_WithoutBegin 覆盖 |

## 6. 实现顺序

1. NativeMethods 加 `DwmActivateLivePreview` P/Invoke + AeroPeekType 常量（值以 ManagedShell 反射为准）
2. 改 `WindowPeek.cs`：Begin/End/Cancel 替换为 DwmActivateLivePreview，删 SetWindowPos 路径
3. `ThumbnailWindow.cs`：传递宿主句柄；最小化恢复逻辑保留
4. 构建 + 单测 + 真机走查（DoD 3-6）
5. 通过后更新 1413 技术库文档（记录 DWM Live Preview 替代 SetWindowPos、UIPI 免疫结论）

## 7. 开放问题（§12）

- Q1：`callingHwnd` 用 ThumbnailWindow 句柄还是 DockWindow 句柄？——**已决（2026-09-11 实测）**：必须用 **DockWindow 主窗口句柄**。dist 1453（calling=浮层句柄）用户实测"预览屏蔽其他一切窗口包括 dock，只能预览无法选择进入"；DWM 的 callingHwnd 语义 = 预览时保持**调用者窗口**（语义同系统任务栏）不被透明化。已改为构造传入 DockWindow 句柄（`ThumbnailWindow(..., callingHwnd)` + `DockWindow.OpenRunningPreview` 传 `new WindowInteropHelper(this).Handle`），未传时回退浮层句柄。
- Q2：最小化窗口是否保留显式 ShowWindowAsync 恢复？——保留，实测后按日志精简。
- Q3：`AeroPeekType.Window` 数值以 ManagedShell 反射确认（Default=0 / Desktop=1 / Window=3，非 2）。

## 8. 超越需求建议（beyond，可选，不进强制 scope）

- 新版 cairoshell 用的 `DwmActivatePeek` 与文档化 `DwmActivateLivePreview` 二选一即可；后续如需"桌面 Peek"（AeroPeekType.Desktop，任务栏右下角"显示桌面"同款）可复用同一调用点扩展。
- 1413 技术库文档升级为"UIPI 免疫的 DWM Live Preview 范式"，作为后续所有"替代任务栏窗口操作"的基准。

---

## §14 交接节（技术力应用 skill 按此执行）

**注入清单**（实现 agent 需读）：
- 本计划 `docs/plans/2026-09-11-host-elevation-dock-peek.md`（已改为 DWM Live Preview 方案）
- `packages/shell/shell-window-tracker/Thumbnail/WindowPeek.cs`（主改动点）
- `packages/shell/shell-window-tracker/Thumbnail/ThumbnailWindow.cs`（宿主句柄传递）
- 参考：`D:\迅雷下载\cairoshell-master\Cairo Desktop\CairoDesktop.Taskbar\TaskThumbnail.xaml.cs`（触发时机）、ManagedShell `WindowHelper.PeekWindow`（标准实现，上文代码块）
- `TECH-KNOWLEDGE/14-窗口与快捷键/1414-window-activate-restore.md`（激活链路，只读不改）

**模式判定**：既有功能适配（替换 Peek 抬窗机制，不动业务结构）；无现成技术力资产，新建 DWM Live Preview 机制。

**适配参数**：
- `DwmActivateLivePreview(1, hwnd, callingHwnd, Window)` 开始 / `(0, 0, 0, Window)` 结束
- 删除：SetWindowPos 抬窗、topmost 回退（`RaiseToTop` 降级路径）
- 保留：`_peekFailure` 抑制、`_peek.Target` 命中判定、激活点选链路

**DoD 核销表**：见本计划 §5（1-6 全过才算完成；3/5 为关键项）。

**回退**：若 DwmActivateLivePreview 在目标系统无效（Win7 以下才需要走 else 分支；本机 Win10+ 无虞），保留 SetWindowPos 路径作 fallback 不进强制 scope。
