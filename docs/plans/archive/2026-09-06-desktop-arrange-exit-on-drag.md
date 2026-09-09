# 计划：自绘桌面「拖动即退出自动排列，布局保持不变」（explorer 同款）

> 日期：2026-09-06 · 类别：功能开发 · 深度：standard · 仓库：better-desktop-cordis
> 目标：自动排列（desktop.autoArrange=true）模式下，用户拖动任意图标 → 自动固化当前瀑布布局、持久化退出自动排列、转为自由布局且视觉布局不变，并续接本次拖动让用户继续摆放（explorer 桌面同款行为）；提供设置开关让用户选择是否启用。

## 1. 目标（场景语言）
1. 用户开启「自动排列」后，直接按住一个图标拖动 → 该图标脱离网格跟随鼠标，其余图标保持原位（整体布局不变）。
2. 松手后图标落于新位置，此后桌面进入自由排布（可任意拖动），布局持久化（重启不变）。
3. 设置分区提供开关「拖动图标时自动退出自动排列」，关闭后拖动恢复为「拖出文件到资源管理器」（原行为）。

## 2. 技术力检索结论
- `TECH-KNOWLEDGE/索引.md` 不存在 → 无现成功能文档，**新建**。
- 已核验既有实现：`DesktopIconsControl` 双布局（`Rebuild()` 按 `AutoArrange` 分流）；自由布局 `RebuildFreeLayout` 初始位置 = 列优先分配（`rowsPerColumn = floor(可用高度/CellHeight)`，坐标 `col*CellWidth, row*CellHeight`）；拖动状态字段 `_dragStart/_dragPath/_itemOriginX/_itemOriginY/_dragPossible/_dragMoving/_draggingCell/_dragCells/_dragOrigins`；位置持久化 `desktop.iconPositions`（`Dictionary<string, double[]>`）；设置分区 `DesktopSection` 已有「自动排列/拖动后对齐网格」开关。

## 3. 关键决策
| 决策 | 选择 | 理由 |
|---|---|---|
| 固化算法 | 与 `RebuildFreeLayout` 初始分配同基准：`_browser.Items` 顺序 → 列优先 (col,row) → 坐标表 | 固化后 RebuildFreeLayout 读同一坐标表，视觉与自动排列一致 |
| 退出方式 | `settings.Set("desktop.autoArrange", false)` + `Rebuild()` | `AutoArrange` 属性按设置即时读取，重建即切自由布局 |
| 续接拖动 | Rebuild 后按 `Tag==dragPath` 反查新 cell，重建拖动上下文（_dragStart=当前鼠标位置、_dragPossible、_dragCells 重收集、newCell.CaptureMouse） | 旧 cell 随重建销毁；续接后下一次 MouseMove 走自由布局逻辑，用户无缝继续拖 |
| 开关 | 新设置键 `desktop.autoExitArrangeOnDrag`（默认 true），DesktopSection 加 ToggleRow | 用户可选择关闭（关闭后恢复「拖出文件」原行为） |

## 4. 改动清单
1. `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs`：
   - 新增属性 `AutoExitArrangeOnDrag`（读 desktop.autoExitArrangeOnDrag，默认 true）。
   - `AutoArrange` 分支 `cell.MouseMove`：越过拖出阈值后，开关开启 → `ExitArrangeAndContinueDrag(cell, _dragPath, pos)`；否则原 DoDragDrop 拖出。
   - 新增 `ExitArrangeAndContinueDrag`：固化布局 → 退出自动排列 → Rebuild → 反查新 cell → 恢复拖动上下文。
   - 新增辅助 `FindCellByPath` / `EnumerateCells`。
2. `packages/shell/shell-desktop/Sections/DesktopSection.cs`：「自动排列」开关下新增「拖动图标时自动退出自动排列」ToggleRow。

## 5. 交互细节（拍板）
- 拖动阈值沿用现有 `MinimumHorizontal/VerticalDragDistance`（与拖出判定一致，不误触单击）。
- 固化布局 = 当前瀑布排列：排序顺序（desktop.sortKey）保留——退出自动排列后 sortKey 仍生效于后续手动整理入口。
- 回切自动排列：设置分区开关切回（explorer 右键「查看→自动排列」不在自绘表面提供，避免与原生菜单委托冲突）。
- 多选拖动：按下时图标在选中集内 → 固化后整个选中集一并续接（新 cell 集合重收集）。

## 6. 边界与异常
- `_settings` 为空 / 已释放：直接返回，不切布局。
- Rebuild 后找不到被拖图标新 cell（极端：条目刚被外部删除）：放弃续接，布局已退出自动排列（可接受）。
- 拖出文件与退出自动排列互斥：开关开启时拖动不再触发 FileDrop 拖出（与 explorer 一致：自动排列下拖图标=移动图标，不拖出）；需要拖出文件 → 关闭开关或在自由布局下拖出（自由布局无拖出？自由布局拖动=重排。拖出仅在自动排列。开关关闭后恢复）。

## 7. 对外契约兼容
- 新设置键增量，不影响既有设置；`DesktopSection` 内部 API。
- `AutoExitArrangeOnDrag` 私有属性，无外部消费者。

## 8. 风险
| 风险 | 缓解 |
|---|---|
| Rebuild 在事件处理器内销毁 sender | WPF 事件派发后移除节点安全（处理器持有 cell 局部引用）；Rebuild 后所有拖动状态重建 |
| 固化位置与 WrapPanel 实际渲染有行尾差异 | 两者同用「可用高度/ CellHeight」基准，且自由布局有吸附/整理兜底 |
| 用户误拖 3px 即退出自动排列 | explorer 同款行为（拖即切手动）；开关可关闭 |

## 9. 实现顺序
① DesktopIconsControl 属性+MouseMove 分流 → ② ExitArrangeAndContinueDrag+辅助 → ③ DesktopSection 开关 → ④ 构建 → ⑤ 计划核销。

## 10. DoD（验收）
1. `dotnet build BetterDesktop.slnx -v q` 0 警告 0 错误。
2. 代码自查：固化算法与 RebuildFreeLayout 同基准；续接拖动状态完整（_dragCells/_dragStart/_itemOrigin/CaptureMouse）。
3. 真机走查（用户）：自动排列下拖一个图标 → 布局不变、该图标跟随鼠标、松手落位；其余图标原位；重启后保持自由布局；设置开关关闭 → 拖动恢复拖出文件。

## 11. beyond（可选增强，不进强制 scope）
- 拖动到屏幕边缘自动滚屏。beyond
- 退出自动排列时给一次 toast 提示。beyond

## 12. 开放问题
- 无阻塞项。自由布局下「拖出文件到资源管理器」不提供（与 explorer 一致：手动排布模式拖动=移动图标）。

## 13. 交接（§14）
- 交接对象：`skills/ability-reuse-alignment/SKILL.md` 按本节执行（仓库中未落地，按改动清单直接实施）。
- 注入文档：本计划 §3/§5/§6 约束、§4 改动清单。
- 适配参数：仓库根 `better-desktop-cordis/`；构建 `dotnet build BetterDesktop.slnx -v q`；日志走 DiagnosticLog.Trace。
- DoD 核销表：§10 逐项核对；第 3 条真机走查由用户执行。

## 14. DoD 核销表
| # | 项 | 状态 |
|---|---|---|
| 1 | 构建 0 错误 0 警告 | ✅ shell-desktop 构建 0 警告 0 错误（2026-09-06） |
| 2 | 代码自查通过 | ✅ 固化算法与 RebuildFreeLayout 同基准；续接拖动状态完整（_dragCells/_dragStart/_itemOrigin/CaptureMouse） |
| 3 | 真机走查（用户） | ☐ 待用户执行（清单见交付说明） |
