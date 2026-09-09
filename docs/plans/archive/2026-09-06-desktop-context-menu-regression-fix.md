# 计划：自绘桌面右键菜单重构回归修复（09-04~09-06 工作区未提交改动）

> 日期：2026-09-06 · 类别：bug 修复 · 深度：standard · 仓库：better-desktop-cordis
> 目标：修复 09-04~09-06 未提交重构（自绘菜单管线退役 → explorer 原生菜单委托）引入的 7 项回归：P0-1 UI 线程冻结、P0-2 explorer 选中态污染、P1-3 网格不同步、P1-5 多选降级单选、P2-6 custom 模式无菜单、P2-7 匹配失败无菜单、P2-8 空白右键弹错菜单类型。
> 口径（用户拍板 2026-09-06）：桌面右键 = explorer 原生菜单是**既定目标**，不恢复自绘菜单；只修目标实现过程中的能力偏移与工程质量问题。

## 1. 目标（场景语言）
1. 右键自绘桌面图标（含多选）→ 弹 explorer 原生条目菜单，**菜单打开期间桌面不冻结**，关闭后网格与 explorer 同步。
2. 右键自绘桌面空白 → 恒弹 explorer 背景菜单（光标处恰落在 explorer 隐藏图标上时也不得弹成条目菜单）。
3. 委托结束后 explorer 桌面原有选中态不被改写（切回原生桌面无残留）。
4. 任何 `context-menu.mode` 配置下桌面右键都有菜单（无"完全无菜单"状态）。
5. explorer 端找不到同名项时，回退本进程系统原生菜单（内容等价，仍为系统渲染）。

## 2. 技术力检索结论
- `TECH-KNOWLEDGE/索引.md` 不存在（仓库技能资产未落地）→ 无现成功能文档命中，**新建修复**。
- 既有事实（代码已实证）：跨进程 ListView 消息必须走远程内存（VirtualAllocEx+WriteProcessMemory，见 `DesktopMenuDelegation.FindItemIndex`）；`LVM_GETITEMCOUNT/LVM_GETITEMSTATE` 为返回值式消息可直发；`StaComWorker.Begin` 是模态菜单的既定异步通道（StaComWorker.cs 注释"UI 线程同步 Run 会把菜单模态周期变成宿主卡死"）。

## 3. 关键决策
| 决策 | 选择 | 理由 |
|---|---|---|
| 图标委托线程 | `StaComWorker.Begin` 异步（与 NativeMenuPopup 同通道） | 消除 P0-1：SendMessageW 阻塞的是 STA worker 线程，UI 线程立即返回 |
| explorer 选中态 | 委托前 `LVM_GETITEMSTATE` 枚举保存 → 菜单关闭后恢复（index 裁剪到当前 count） | P0-2：委托必须选中目标项（机制必需），但结束即还原，不污染用户环境 |
| 多选委托 | `TryShowForItems`：逐项找 explorer 索引 → 全选命中集 → 转发（坐标=首个命中项中心） | P1-5：多选右键 = 整集操作，explorer 对多选弹交集菜单 |
| 匹配失败回退 | 全部匹配失败 → `NativeMenuPopup.TryShowItems`（本进程 IContextMenu，系统原生渲染） | P2-7：消除"宁可无菜单"；内容与 explorer 同源（同一 shell 引擎聚合） |
| 空白菜单类型 | 转发前 `LVM_HITTEST`（远程内存）检查光标是否命中 explorer 隐藏图标；命中 → 不转发，走本进程桌面背景 IContextMenu | P2-8：explorer 按坐标 hit-test 决定菜单类型，命中检查保证恒弹背景菜单 |
| custom 模式 | `IsNativeMode` 恒 true（custom 与 native 同路，配置键保留兼容） | P2-6：自绘管线已退役，custom 不应再产生"无菜单"状态 |
| 网格同步 | DesktopBrowser 挂 FileSystemWatcher（用户桌面+公共桌面，500ms 防抖，滤 desktop.ini）+ 委托完成回调 `_browser.Refresh()` | P1-3：根治数据源分叉（自绘网格 vs explorer 隐藏列表），外部变化也能追上 |

## 4. 改动清单
1. `packages/shell/shell-context-menu/Services/DesktopMenuDelegation.cs`：
   - 新增 `record DesktopIconRef(string Path, string? Label)`。
   - `TryShowForItem` → `TryShowForItems(IReadOnlyList<DesktopIconRef>, Point, Action? onCompleted)`：StaComWorker.Begin 异步；核心 `ShowForItemsCore`：找 DefView/SysListView32 → 逐项候选名匹配 → 全部失败回退 `TryShowItems` → 保存选中态 → 清空+选中命中集 → `LVM_GETITEMRECT` 取首命中项中心转发 WM_CONTEXTMENU → 恢复选中态 → 回调 onCompleted。
   - 新增 `GetSelectedIndices`/`RestoreSelection`（LVM_GETITEMCOUNT/LVM_GETITEMSTATE 直发）。
   - 新增 `HitTestDefViewItem(int x, int y)`（LVM_HITTEST 远程内存 + ScreenToClient），供 NativeMenuPopup 空白转发前检查。
   - 常量补 LVM_GETITEMCOUNT/LVM_GETITEMSTATE/LVM_HITTEST + ScreenToClient P/Invoke。
2. `packages/shell/shell-context-menu/Services/NativeMenuPopup.cs`：
   - `IsNativeMode` 恒 true（custom 同路），注释更新。
   - `ShowBackgroundCore`：转发前 `!HitTestDefViewItem(x,y)` 才转发，命中 → 直接降级本进程背景菜单。
3. `packages/shell/shell-desktop/Services/DesktopBrowser.cs`：
   - 构造挂 FileSystemWatcher（用户/公共桌面，FileName|DirectoryName|LastWrite，不递归，滤 desktop.ini），500ms 防抖 Timer → Post(Refresh)。
4. `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs`：
   - `ShowMenu`：删除 `IsNativeMode` 无菜单门控；target 非空时按"右键项∈选中集→整集/否则单选"构造 `DesktopIconRef` 列表 → `TryShowForItems(refs, physical, onCompleted: Dispatcher.BeginInvoke(_browser.Refresh))`。
5. `packages/shell/shell-context-menu/ContextMenuPlugin.cs`：custom 语义注释同步（低优先，可选）。

## 5. 交互细节（拍板）
- 多选右键：右键项已在选中集内 → 整集委托；不在 → 先单选再委托（沿用 explorer 语义与既有 OnMenuServiceMouseUp 逻辑）。
- 委托完成回调：菜单关闭后立即触发（STA 线程 → Dispatcher.BeginInvoke → UI 刷新），保证 explorer 侧删除/重命名/新建立刻反映到自绘网格。
- 恢复选中态只恢复 SELECTED（不追 FOCUSED），已删除项的 index 越界自动跳过。

## 6. 边界与异常
- explorer 未运行/DefView 不可用：委托返回，回退 `TryShowItems` 本进程菜单（仍有菜单）。
- `LVM_GETITEMRECT` 失败：不转发光标坐标（布局不同必弹错类型），回退 `TryShowItems`。
- 多选含虚拟项（此电脑/回收站）：explorer 端按显示名匹配，匹配失败的项跳过，命中项照常委托；全失败回退。
- FileSystemWatcher 不可用（权限/网络桌面）：try-catch 忽略，退化为仅委托后刷新。
- 高频桌面变化：500ms 防抖合并；desktop.ini 过滤。
- 跨进程恢复选中态：菜单内删除项后 index 越界 → 裁剪跳过，不越界写。

## 7. 对外契约兼容
- `TryShowForItem`（internal 使用方仅 DesktopIconsControl.ShowMenu）→ 替换为 `TryShowForItems`，同仓同批修改，无外部消费者。
- `IsNativeMode` 恒 true：外部仅读此属性判定门控，行为等价"始终原生"。
- DesktopBrowser 新增字段/逻辑，接口不变。
- 不新增设置键、不新增文件。

## 8. 风险
| 风险 | 缓解 |
|---|---|
| 多选委托时 explorer 菜单作用于隐藏图标集 | 与单选同机制（选中+转发），多选为 explorer 原生能力，风险同源 |
| 恢复选中态与菜单内操作竞争 | SendMessageW 同步返回（菜单已关闭）后才恢复，无并发窗口 |
| StaComWorker 线程被模态菜单长时间占用 | 与 NativeMenuPopup 现有通道一致，UI 不阻塞；菜单期间 worker 线程自泵消息 |
| FileSystemWatcher 高频触发 | 500ms 防抖 + desktop.ini 过滤 |

## 9. 实现顺序
① DesktopMenuDelegation 重构 → ② NativeMenuPopup 门控+命中检查 → ③ DesktopBrowser watcher → ④ DesktopIconsControl.ShowMenu → ⑤ 构建 shell-desktop + shell-context-menu → ⑥ 全量构建 → ⑦ 计划核销。

## 10. DoD（验收）
1. `dotnet build`（shell-desktop/shell-context-menu 及依赖）0 错误 0 警告。
2. 代码自查：多选委托路径、选中态保存/恢复、命中检查、watcher 防抖、门控移除逐项核对。
3. 真机走查（需用户执行）：右键单图标/多选图标 → 弹 explorer 条目菜单且桌面不冻结；空白右键 → 恒背景菜单；菜单内删除 → 自绘网格同步消失；切回原生桌面 → explorer 选中态无残留；`context-menu.mode=custom` → 仍有菜单。

## 11. beyond（可选增强，不进强制 scope）
- explorer 多选菜单项差异（如仅单选的"固定到开始屏幕"）按交集自然过滤，无需额外处理。beyond
- watcher 监听"桌面位置变更"设置键迁移。beyond

## 12. 开放问题
- 无阻塞项。LVM_HITTEST 跨进程可靠性按远程内存模式与现有 FindItemIndex 同构，风险低。

## 13. 交接（§14）
- 交接对象：`skills/ability-reuse-alignment/SKILL.md` 按本节执行（仓库中该 skill 尚未落地，按计划内改动清单直接实施）。
- 注入文档：本计划 §3/§5/§6 约束、§4 改动清单。
- 适配参数：仓库根 `better-desktop-cordis/`；构建 `dotnet build`（先构建受影响包再全量）；日志走 `DiagnosticLog.Trace("shell.contextmenu"/"shell.desktop", ...)` 既有通道。
- DoD 核销表：§10 逐项核对；第 3 条真机走查由用户执行，代码侧完成 1/2 并给出走查清单。

## 14. DoD 核销表
| # | 项 | 状态 |
|---|---|---|
| 1 | 构建 0 错误 0 警告 | ✅ `dotnet build BetterDesktop.slnx -v q`：0 警告 0 错误（2026-09-06） |
| 2 | 代码自查通过 | ✅ 多选委托/选中态保存恢复/命中检查/watcher 防抖/门控移除/回退路径逐项核对（见 §3/§6） |
| 3 | 真机走查（用户） | ☐ 待用户执行（清单见交付说明：右键单图标/多选图标/空白/菜单内删除/切回原生桌面/custom 模式） |
