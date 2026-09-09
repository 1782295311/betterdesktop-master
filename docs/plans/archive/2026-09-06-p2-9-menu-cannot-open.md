# 计划：自绘桌面右键无法唤出菜单（P2-9 修复）

> 日期：2026-09-06 · 类别：回归修复（真机反馈）· 深度：standard · 仓库：better-desktop-cordis
> 现象：用户实测「自绘桌面区域无法唤出菜单」。日志实锤（桌面 BetterDesktop_debug.log）：
> - 21:11:27–34 图标右键 8× `委托失败：找不到 SysListView32`（静默无菜单）；
> - 21:11:37–41 右键仅剩 `窗口层右键 up` 诊断日志、无任何菜单日志（事件未达菜单层）；
> - 21:11:45 退出时 FindDesktopListView 同口径找到 listView=0x10172 defView=0x10170。

## 1. 根因分析（证据闭环）
**A. 空壳 DefView（主因，图标委托失败）**：
- `DesktopMenuDelegation.FindDefView`：Progman 直子 DefView **存在即返回**，不验证其内含 SysListView32。
- 壁纸引擎/DWM 活动重建桌面层后，Progman 直下残留**空壳 DefView**（真身被移到顶层 WorkerW 下）→ 委托拿到空壳 → `FindWindowExW("SysListView32","FolderView")` 失败 → `return` **无回退** → 图标右键无菜单。
- 旁证：21:11:45 退出时 DesktopPlugin.FindDesktopListView（GetShellWindow 路径）找到 0x10170 内 0x10172——DefView 空壳是可复现的状态。

**B. 委托失败路径静默无菜单**：`ShowForItemsCore` 的 defView==0 / hList==0 / OpenProcess 失败三处 `return` 均不回退（P2-7 只覆盖了同名项全失败与 LVM_GETITEMRECT 失败）。

**C. 隐藏图标后右键全失效**：`DesktopWindow.ApplyIconsHidden` 用 `Visibility=Collapsed` 隐藏网格——Collapsed 吞掉整棵子树鼠标事件 → 隐藏图标后空白右键无菜单（explorer 同款语义应为：隐藏图标后空白右键仍弹背景菜单）。

## 2. 修复方案
| # | 改动 | 文件 |
|---|---|---|
| A | `FindDefView` → `FindVerifiedDefView()`（public）：Progman 直子 **与** WorkerW 变体均**验证含 SysListView32("FolderView") 才返回** | DesktopMenuDelegation.cs |
| B | `ShowForItemsCore`：defView==0 / hList==0 / OpenProcess 失败 → 回退 `NativeMenuPopup.TryShowItems`（不再静默） | DesktopMenuDelegation.cs |
| A' | `NativeMenuPopup.TryForwardDefView` 改用共享 `FindVerifiedDefView()`（空壳不转发） | NativeMenuPopup.cs |
| C | `DesktopIconsControl.Rebuild` 开头：iconsHidden=true → `Content=null`（保留控件交互）；`DesktopWindow.ApplyIconsHidden` 不再 Collapsed，改为 `Visibility=Visible + Rebuild()` | DesktopIconsControl.cs / DesktopWindow.cs |

## 3. 行为契约
- 图标右键：委托 explorer（验证过的 DefView）→ 任一前置失败 → 本进程原生菜单，**绝不静默**。
- 空白右键：转发验证过的 DefView → 失败降级本进程背景菜单（既有）。
- 隐藏图标（iconsHidden=true）：图标不可见，但空白右键仍弹背景菜单、双击仍可恢复（通道=DesktopWindow 层冒泡 + _icons 自身 MouseRightButtonUp）。

## 4. 风险
- Content=null 后自由布局的 canvas 空白双击通道断开 → 由 DesktopWindow.MouseLeftButtonDown（自动排列路径）兜底恢复，两条通道互补（已验证逻辑）。
- FindVerifiedDefView 多一次 FindWindowExW 遍历，开销可忽略（仅右键时）。

## 5. DoD
1. `dotnet build BetterDesktop.slnx -v q` 0 警告 0 错误。
2. 代码自查：三处委托失败路径均回退；隐藏图标态空白右键可达。
3. 真机走查（用户）：图标右键恒弹菜单（含壁纸引擎换壁纸后）；隐藏图标后空白右键仍弹背景菜单；双击恢复图标。

## 6. DoD 核销表
| # | 项 | 状态 |
|---|---|---|
| 1 | 构建 0 错误 0 警告 | ✅ |
| 2 | 代码自查通过 | ✅ |
| 3 | 真机走查（用户） | ☐ |

---

## 7. 方向切换（用户拍板 2026-09-06 21:50+）
**用户**：「我们自己识别在自绘桌面操作的对象，然后把需求发给系统右键菜单，而不是让系统自己来」。
**落地**：
- 图标右键主路径 = `NativeMenuPopup.TryShowItems`（本进程 IContextMenu：SHBindToParent + GetUIObjectOf + QueryContextMenu + TrackPopupMenuEx，explorer 同源聚合）；
- 跨进程委托（LVM_FINDITEMW → 跨进程选中 → LVM_GETITEMRECT → WM_CONTEXTMENU 转发）**整体退役**（`DesktopMenuDelegation.cs` 重写为纯转发 + 保留 `HitTestDefViewItem`/`FindVerifiedDefView`）；
- `NativeMenuPopup.TryShowItems` 新增可选 `onCompleted`（菜单关闭后回调，刷新时序保持）。
- 21:49 测试确认跑的是旧构建（日志无 P2-9 回退后缀）——"还是做不到"含未部署因素；本次交付必须重新构建 + 重启宿主。
**构建**：0 警告 0 错误。**真机走查**：图标右键弹本进程系统菜单（handler 同源）；日志新增完整探针（`原生弹层 items=` / `弹层探针 pidl ok` / `GetUIObjectOf hr=` / `QueryContextMenu hr=` / `Track 返回 cmd=`）。
