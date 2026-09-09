# 计划：explorer 桌面排序桥接 → 自绘网格跟随

> 日期：2026-09-06 · 类别：功能开发 · 深度：standard · 仓库：better-desktop-cordis
> 目标：用户在 explorer 原生菜单（桌面空白右键 → 排序方式）选择排序字段/方向后，自绘桌面网格自动跟随重排（≤1.5s），并持久化。

## 1. 目标（场景语言）
1. 桌面空白右键 → explorer 原生菜单 → 「排序方式 → 名称/大小/类型/修改日期」（含递增/递减）→ 自绘图标网格自动按同字段同方向重排。
2. 排序结果持久化：重启自绘桌面后仍保持该排序。
3. 设置分区提供开关「跟随系统排序」，关闭后自绘保持自身排序不受 explorer 影响。

## 2. 技术力检索结论
- `TECH-KNOWLEDGE/索引.md` 不存在 → 无现成功能文档，**新建**。
- 已核验既有实现：`DesktopBrowser.SetSort(string? key)`（name/size/type/modified + null 智能默认，方向硬编码：size/modified 降序、name/type 升序）；`desktop.sortKey` 持久化（构造时 DesktopIconsControl 恢复）；自绘菜单退役后 `InvokeSetSort` 无调用者。
- 已核验 explorer 桌面排序存储（MSFN 实测 + Volatility shellbags 取证 + 多来源交叉）：
  `HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop` → `Sort` DWORD（0=名称/1=大小/2=类型/3=修改日期）、`SortDir` DWORD（0=升序/1=降序）、`FFlags` bit0=自动排列。

## 3. 关键决策
| 决策 | 选择 | 理由 |
|---|---|---|
| 监听方式 | 后台线程轮询（1.5s）读 Sort/SortDir，变化才应用 | 无 P/Invoke 事件复杂度；explorer 菜单操作后注册表立即写入；延迟可接受 |
| 桥接开关 | 新设置键 `desktop.sortBridge`（默认 true），DesktopSection 加 ToggleRow | 用户可选择关闭 |
| 方向支持 | DesktopBrowser 新增 `SortDescending` + `SetSort(key, descending)`，持久化 `desktop.sortDesc` | 自绘排序引擎原无方向，桥接必须带方向 |
| 应用语义 | 排序仅驱动自动排列模式（自由布局位置优先，explorer 同款） | 与现有布局语义一致 |
| 装配 | DesktopBrowser 构造加可选 `ISettingsService?`，桥接线程内聚于 Browser；DesktopPlugin:209 传入 _settings | 最小侵入，无 settings 时桥接禁用 |

## 4. 改动清单
1. `Contracts/IDesktopBrowser.cs`：加 `bool SortDescending { get; }`；`SetSort(string? key)` → `SetSort(string? key, bool descending = false)`。
2. `Services/DesktopBrowser.cs`：
   - 构造 `DesktopBrowser(ISettingsService? settings = null)` 存 `_settings`；启动桥接线程。
   - `SortDescending` 属性；`SetSort` 支持方向并持久化 `desktop.sortDesc`。
   - `ApplySort` 按方向反转（name/type：升序默认；size/modified：降序默认，与 explorer 一致）。
   - 桥接：`SortBridgeLoop` 轮询 `ReadExplorerSort()`（Sort/SortDir，DWORD 容错）→ 变化且开关开启 → `_sync.Post → SetSort(key, desc)`。
3. `Controls/DesktopIconsControl.cs`：构造恢复（117 行）与 `InvokeSetSort` 传方向（`desktop.sortDesc`）。
4. `Sections/DesktopSection.cs`：「排列与拖动」卡片加 ToggleRow「跟随系统排序」（desktop.sortBridge，默认 true）。

## 5. 交互细节（拍板）
- 用户用 explorer 菜单改排序 → 隐藏层立即重排 + 注册表写入 → 自绘 ≤1.5s 跟随。
- 自由布局模式：排序键更新但位置优先不重排（explorer 关闭自动排列时同语义）；切回自动排列时按最新排序排布。
- 方向映射：explorer SortDir=0(升序) → desc=false；SortDir=1(降序) → desc=true。size 默认降序（explorer 惯例）。

## 6. 边界与异常
- 注册表键不存在 / Sort 非 DWORD（W11 个别版本二进制）→ 忽略本次，不应用、不打断轮询。
- 桥接线程后台 + 无 IDisposable（与 FileSystemWatcher 同项目风格）；进程退出自然终止。
- 排序变化与用户自由拖动并发：排序应用只发生在自动排列模式；自由布局下 sortKey 更新不影响当前位置。

## 7. 对外契约兼容
- `SetSort` 默认参数保持旧签名二进制兼容；新增属性/参数为增量。
- 新设置键 `desktop.sortBridge` / `desktop.sortDesc` 增量，无破坏。

## 8. 风险
| 风险 | 缓解 |
|---|---|
| Sort 值被其他程序改动误触发 | 1.5s 轮询 + 变化才应用 + 开关可关 |
| W11 Sort 二进制格式 | DWORD 容错，非 DWORD 忽略；桌面 Bag 键在 W10/11 均为 DWORD（多来源确认） |
| 方向覆盖用户手动 sortKey | 桥接是唯一排序入口（自绘菜单已退役），无冲突 |

## 9. 实现顺序
① 契约 → ② DesktopBrowser（方向+桥接线程）→ ③ DesktopIconsControl 恢复方向 → ④ DesktopSection 开关 → ⑤ 构建 → ⑥ 计划核销。

## 10. DoD（验收）
1. `dotnet build BetterDesktop.slnx -v q` 0 警告 0 错误。
2. 代码自查：方向映射正确；轮询变化检测；开关判定；线程回抛 UI。
3. 真机走查（用户）：explorer 菜单改排序（含升/降）→ 自绘网格 ≤2s 内跟随；重启保持；关闭开关 → 不再跟随。

## 11. beyond（可选增强，不进强制 scope）
- 轮询改 RegNotifyChangeKeyValue 事件驱动（省线程）。beyond
- 排序变化时图标动画过渡。beyond

## 12. 开放问题
- 无阻塞项。

## 13. 交接（§14）
- 交接对象：按 §4 改动清单直接实施（skills 资产未落地）。
- 注入文档：本计划 §3/§5/§6 约束、§4 改动清单。
- 适配参数：仓库根 `better-desktop-cordis/`；构建 `dotnet build BetterDesktop.slnx -v q`。
- DoD 核销表：§10 逐项核对；第 3 条真机走查由用户执行。

## 14. DoD 核销表
| # | 项 | 状态 |
|---|---|---|
| 1 | 构建 0 错误 0 警告 | ✅ `dotnet build BetterDesktop.slnx -v q`：0 警告 0 错误（2026-09-06） |
| 2 | 代码自查通过 | ✅ Sort/SortDir 映射核验（多来源）；方向反转正确；轮询变化检测 + 开关判定 + UI 回抛 |
| 3 | 真机走查（用户） | ☐ 待用户执行（清单见交付说明） |
