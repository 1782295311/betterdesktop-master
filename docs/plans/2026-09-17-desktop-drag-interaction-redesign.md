# 自绘桌面拖动交互改版：左键=图标间文件拖放，右键长按=自由摆放

> 状态：**已实施（2026-09-17 真机走查进行中）**；日期 2026-09-17；触发：用户需求（自绘桌面优化）
> 目标文件：`packages/shell/shell-desktop/Controls/DesktopIconsControl.cs`（2764 行）、`Sections/DesktopSection.cs`、`README.md`
> 用户拍板（2026-09-17）：
> 1. 左键长按拖动**不是让位**，而是「拖到文件夹图标 = 把文件移进去；拖到软件图标 = 用这个软件打开它」（原生桌面同款语义）。
> 2. 「自由拖拽排序（自由摆放）」改到**右键长按**触发；**松手后仍弹右键菜单**（自由摆放与菜单都要）。
> 3. 模式关系用户未补充 → 本计划给出方案（见 §3），实施后在回执里显式标注，便于用户纠正。

## 1. Objective

自绘桌面上，鼠标手势按「**原生桌面的直觉**」分工：

| 手势 | 语义 |
|---|---|
| 左键按住拖动 | 把文件拖到**另一个图标**上：文件夹 → 移入（Ctrl=复制）；可执行/快捷方式 → 用该程序打开；回收站（桌面/dock）→ 移入回收站；空白处 → 无操作；拖到外部窗口 → 交给系统（OLE 拖放） |
| 右键长按（≥350ms）后拖动 | **自由摆放**（原左键的自由拖拽排序）：图标跟随鼠标、占用者让位、松手吸附并持久化坐标 |
| 右键短按 | 右键菜单（不变） |
| 左键空白拖动 | 框选（不变） |

## 2. Current Behaviour（改动前）

- `AutoArrange=true`（WrapPanel 瀑布列）：左键拖动 = 拖出文件（OLE `DoDragDrop`）；若 `desktop.autoExitArrangeOnDrag`（默认 true）→ `ExitArrangeAndContinueDrag` 固化布局、`autoArrange=false`、重建后 `ResumeDragAfterRebuild` 续接**自由拖动**。
- `AutoArrange=false`（Canvas 自由布局）：左键拖动 = 自由摆放（`CaptureLayoutSnapshot`/`ApplyAvoidance`/`SnapTarget`/`ShowDropIndicator`/`CommitLayout` + 松手 `desktop.iconPositions` 落盘）；拖到回收站 → `DeleteToRecycleBin`。
- 右键按下只做选中（`cell.MouseRightButtonDown`），松手由控件级 `OnMenuServiceMouseUp` 弹自绘菜单（`DesktopMenuPopup`）。
- 外部拖入：`AllowDrop=true` + `OnDragOver`/`OnDrop` → `DesktopBrowser.ImportFiles`。

**问题**：左键拖动同时承担「拖出文件」「重排位置」「自动退出自动排列」三种语义，用户想要的「拖到文件夹/软件上交互」完全缺失。

## 3. Proposed Changes

### 3.1 左键拖动 → 图标间文件拖放（OLE DoDragDrop + 自绘落点解析）

命名符号（均在 `DesktopIconsControl`）：
- `TryStartInteractionDrag(Border cell, string path)`：快照 `_dragCells` 中**磁盘上真实存在**的路径（shell 虚拟项 CLSID 排除）；置 `_internalDragActive=true`（使本控件 `OnDragOver` 返回 `None`，从而 OLE 落回本窗口时 `DoDragDrop` 返回 `None`）；挂 `DragDrop.GiveFeedback` 做实时高亮 + 记修饰键；`DragDrop.DoDragDrop(cell, FileDrop, Copy|Move)` 返回后复位并调用 `CompleteInteractionDrop()`。
- `CompleteInteractionDrop()`：`Mouse.GetPosition(this)` 定位落点 cell（排除源图标自身）→ 解析动作。
- `FindCellAt(Point pInControl, ISet<string>? excludePaths)`：复用 `TransformToAncestor(this).TransformBounds` 命中判定（与 `IsPointOverIcon` 同法），两种布局通用。
- `OpenFilesWith(string appPath, IReadOnlyList<string> files)`：`.lnk` 经 `ShellLinkResolver.Resolve`（shell-app-source，已引用）取目标，`Process.Start(exe, "文件" 逐个加引号)`；失败落 `DiagnosticLog`。
- `IsExecutableApp(string path)`：白名单 `.exe/.lnk/.bat/.cmd/.com`（`.url/.msc/.appref-ms` 不做「用其打开」）。
- 文件夹移动/复制：`FileClipboard.Move/Copy`（`SHFileOperation`：系统进度框 + 长路径），完成后 `_browser.Refresh()`。

不变量（红线）：
1. **源集合必须是磁盘真实路径**（`File.Exists || Directory.Exists`）：`::{CLSID}` 虚拟项参与 `FileDrop` 会投毒。
2. **自嵌套护栏**：目标目录 == 源 或 目标目录位于源目录之内 → 跳过并记日志（避免把文件夹移进自己）。
3. **拖到自身/其它被拖图标 → 无操作**（原生同款），不得回退成"移动到自己目录"。
4. `_internalDragActive` 必须在 `finally` 复位；`OnDragOver` 的 None 分支必须先于外部拖入判定（否则自绘桌面会吃掉自己的内部拖放）。
5. **回收站无特殊处理**（2026-09-17 用户拍板二次修正）：回收站就是普通图标（与"此电脑/控制面板"同级），拖上去不删除、不高亮；删除走右键菜单 / Del 键。原 2026-09-07 的"拖到桌面回收站 / dock 栏回收站 = 移入回收站"整条链路（桌面 `UpdateRecycleDropState`/`_overRecycleBin`/`_recycleBinCell`、dock `UpdateRecycleDropRect`/`UpdateRecycleHoverHighlight`、kernel 共享通道 `DockDropTargets`）**已整体拆除**。

### 3.1b 【四轮补齐】左键拖动 = 原生桌面式（含"拖到空白 = 挪过去"）

真机日志暴露：用户框选 2×3 的 6 个图标后，按住其中一个拖到旁边（1 个格位）松手，**什么都没发生**——
因为按三版规则"落在源图标/空白 = 无操作"，而他的松手点仍在选区范围内。

补齐后的左键语义（= 原生 explorer 完整手感）：

| 落点 | 动作 |
|---|---|
| 文件夹图标 | 移入（Ctrl=复制） |
| exe / 快捷方式图标 | 用该程序打开（文件夹快捷方式则移入其目标） |
| **空白** | **`MoveDragSelectionToPlace`：把被拖图标（整组）挪到这里** —— 主拖图标按 `SnapTarget` 吸附落格、整组保持相对位置，走 `CaptureLayoutSnapshot` → 平移 → `CommitLayout`（含互斥消解）→ 落盘 |
| 自身 / 回收站·此电脑等虚拟项 | 无操作（落点即原位时直接返回，不写盘） |
| 窗口外（资源管理器/其它程序） | 交给系统（OLE FileDrop，"拖出"能力保留） |

- **群体跟随动画（五轮补齐）**：拖动期间被拖整组**实时跟随鼠标**（与右键自由摆放**同一套实现**：
  `UpdateFreeDrag` = 跟随 + 占用者让位 + 落点指示器 + 画布范围）；一旦悬停到可投递目标（文件夹/程序）→
  整组**退回原位**（`ResetDraggedCellsToBase`，文件将被移入/打开，不该留摆放预览）。
  背景：用户实测反馈"**左键视觉上是一个，但操作逻辑是群体**"——四轮时只做了逻辑（松手才落位）、漏了视觉跟随。
- 与「右键长按自由摆放」的分工：**左键带投递语义**（落点是文件夹/程序 → 移入/打开，此时整组退回原位）；
  **右键长按 = 纯摆放**（不看落点，始终跟随）。两者共用同一套跟随/落位/落盘机制。
- 收尾一致性：未落位就结束（外部程序接受 / Esc 取消 / 异常）→ `finally` 里 `ResetDraggedCellsToBase` +
  `EndDragVisual`，绝不留下"停在半空"的图标（`iconPositions` 未写盘）。
- `OnDragOver`（内部拖放）本窗口内**一律返回 `Move`**（Ctrl+文件夹落点除外 → `Copy`）：空白也是有效落点，
  "禁止"光标只应出现在窗口外的无效目标上。
- 统一入口 `HandleInteractionDrop(Point, paths)`：`OnDrop` 与 `DoDragDrop` 返回后的兜底共用（先解析图标落点，解析不到再走落位）。

### 3.2 自由摆放 → 右键长按拖动

- `StartRightHoldWatch(cell)`：右键按下起 350ms `DispatcherTimer`；到点且右键仍按住 → `_rightArmed=true` + 视觉提示（`SetRightHoldCue`）。右键抬起/左键按下 → `StopRightHoldWatch` 复位。
- 统一 `cell.MouseMove`：先判「自由拖动进行中 → 续拖」，再判「左键 = 交互拖放起手（超阈值）」，最后判「右键 + `_rightArmed` = 自由摆放下手（超阈值）」。
- `BeginFreeDrag`（原自由布局起手：`CaptureLayoutSnapshot` + `BeginDragVisual`）、`FinishFreeDrag(commit)`（原左键 Up 里的「回收站判定 → 吸附 → `CommitLayout` → `EndDragVisual` → `ClearDragState`」整段搬过来）。
- 记录按钮：`_dragButton`（Left/Right）；`ResumeDragAfterRebuild` 保留 `_dragButton`，故自动排列下右键长按拖动可「固化瀑布布局 → 退出自动排列 → 续接自由拖动」（复用 `ExitArrangeAndContinueDrag`）。
- **松手仍弹菜单**（用户拍板）：右键 Up 结束自由拖动后 `e.Handled=true`，按**释放点**命中的图标自行 `ShowMenu`（不用捕获源，因为捕获态下 `OriginalSource` 恒为被捕获的 cell）。

### 3.3 模式关系（用户未补充，本计划定）

- `desktop.autoExitArrangeOnDrag`（默认 true）语义从「左键拖动自动退出自动排列」迁移为「**右键长按自由摆放时自动退出自动排列**（保持当前布局）」——左键已不再改变图标位置，旧语义失效；自动排列下要自由摆放必须落到 Canvas，故仍保留该开关与既有的固化+续接链路。
- `desktop.autoArrange` / `desktop.snapToGrid` 语义不变。
- 文案同步：`Sections/DesktopSection.cs` 的两条 `ToggleRow` + 底部 `NoteBlock`（现文案「默认可自由拖动图标改变布局：按住左键拖动即可」必须改，属反假提示红线）。

## 4. Implementation Sequence

1. 计划文档（本文件）。
2. `DesktopIconsControl.cs`：新增字段/长按计时器/`_dragButton`；抽出 `CollectFreeDragCells`、`FindEntryByPath`、`SetRightHoldCue`、`FindCellAt`。
3. `DesktopIconsControl.cs`：重写 `CreateItem` 中的拖动事件接线（左键 Down/Up、右键 Down/Up、统一 Move）。
4. `DesktopIconsControl.cs`：新增交互拖放段（`TryStartInteractionDrag`/`CompleteInteractionDrop`/`OpenFilesWith`/`IsExecutableApp` + GiveFeedback 高亮）；`OnDragOver` 加 `_internalDragActive` 守卫。
5. `DesktopSection.cs` 文案 + `README.md` 交互说明 + 本文件顶部方法级索引注释。
6. 构建：`dotnet build packages/shell/shell-desktop/BetterDesktop.Shell.Desktop.csproj`，再全仓 `BetterDesktop.slnx`；宿主运行时先 `Stop-Process`（DLL 锁定 → MSB3027）。

## 5. 风险

| 风险 | 说明 | 缓解 |
|---|---|---|
| 内部拖放被自家窗口吃掉 | 自绘桌面窗口本身是 OLE 落点（`AllowDrop=true`） | `_internalDragActive` → `OnDragOver` 返回 `None`，`DoDragDrop` 才会回 `None` |
| 长按与右键菜单互相打架 | 长按后拖动若仍走捕获源弹菜单会弹错图标 | 拖动结束时 `e.Handled=true` + 按释放点 `ShowMenu` |
| 图标被移走后位置残留 | 移动成功但 `iconPositions` 仍留旧键 | `CommitLayout`/`LoadPositions` 以现存条目为准（`RebuildFreeLayout` 只按 `_browser.Items` 落位），移动后 `Refresh()` 触发重建 |
| 右键拖动覆盖掉「拖到 dock 回收站」 | 左键改为 OLE 后不再走 `UpdateRecycleDropState` | 落点解析同时查 dock 共享矩形 + 桌面回收站 cell |
| 修饰键在拖放结束时已松开 | Ctrl/Shift 语义丢失 | `GiveFeedback` 期间持续记录 `_dragCopyModifier`，落点判定取「记录值 || 当前键盘」 |

## 6. Definition of Done

- D1 左键拖动桌面文件到**文件夹图标**上松手 → 文件移入该文件夹（Ctrl 按下 → 复制），网格自动刷新。
- D2 左键拖动文件到**快捷方式/exe 图标**上松手 → 用该程序打开这些文件（多选则多文件一起传）。
- D3 左键拖动到**回收站**（桌面图标 / dock 回收站）→ **无动作**（回收站已无特殊处理；光标为禁止符号）；拖到**空白/自身/其它被拖图标** → 无操作。
- D4 拖出到资源管理器窗口 → 仍是复制/移动（OLE 通道未破坏）。
- D5 自由布局下**右键长按 ≥350ms 再拖** → 图标跟随鼠标、其它图标让位、松手吸附并落盘；**短按右键仍弹菜单**。
- D6 自动排列下右键长按拖动 → 固化当前布局、退出自动排列并续接拖动（`autoExitArrangeOnDrag=true` 时）。
- D7 右键长按拖动松手 → 落位生效且右键菜单照常弹出（按释放点图标）。
- D8 左键空白框选、双击空白切换图标显隐、双击图标打开、右键菜单既有项全部无回归。
- D9 构建：`shell-desktop` 单工程 0 错误；全仓 `BetterDesktop.slnx` 0 错误。
- D10 真机走查：宿主启动 → 桌面拖一个文件到文件夹图标上松手 → 文件进入文件夹（`BetterDesktop_debug.log` 有对应落点日志）。

## 7. 实施记录（真机三坑，已修）

| # | 现象 | 根因 | 修复 |
|---|---|---|---|
| R1 | 左键拖放到文件夹图标，日志恒为「落在空白处，无操作」 | **OLE 拖放的模态循环吃掉 WM_MOUSEMOVE** → `Mouse.GetPosition` 返回的是**拖动起点**（日志实证：`from=(170,251)` 与判定点一致） | 新增 `GetCursorInControl()`（`GetCursorPos` + `PointFromScreen`），落点解析与 hover 高亮全部改走它 |
| R2 | 修复 R1 后落点识别正确，但移入被「自嵌套护栏」误拒（桌面任何文件夹都命中） | 首版把**源的父目录**当成「源目录」比较（`folder.StartsWith(srcParent + "\")`）→ 桌面上所有文件夹都满足 | 护栏重写：①目标 == 源所在目录 → 无操作；②源是文件夹且目标 == 源或在源之内 → 拒绝 |
| R3 | 拖到任何图标上光标恒为**禁止符号**、没有任何提示 | 内部拖放期间本窗口 `OnDragOver` 一律返回 `None`（原为防自家 `ImportFiles` 吃掉），且 `cell.AllowDrop=false`；而原生的 `移动到 xxx / 用 xxx 打开` 提示浮层根本没实现 | ① `cell.AllowDrop=true`；② `OnDragOver`（内部）按落点返回 `Move`（Ctrl=`Copy`）/`None`；③ 新增 `ResolveInteractionDropTarget` 统一解析（hover 高亮 / 提示浮层 / 松手执行三处共用）+ 原生风格提示浮层 `_interactionTip`（Popup：强调色字形 `→`/`+` + `移动到 X` / `复制到 X` / `用 X 打开`，跟随光标右下 + WS_EX_TRANSPARENT 点击穿透防抖动）；④ 动作改在 `OnDrop` 执行（`_internalDropHandled` 去重），DoDragDrop 返回后的兜底只在「未被外部接受且未被自家处理」时跑 |
| R4 | 回收站的特殊处理（拖上去=删除 + 跨窗口高亮）按用户要求**整体取消**，与普通图标同级 | 该特判会让"右键长按自由摆放时顺手把图标摆到回收站位置"变成**静默删除文件**（危险），且它本来就有 3 处特判分支（左键落点/自由摆放松手/避让豁免）+ 跨进程共享通道 | 桌面侧：删 `_recycleBinCell`/`_canvas`/`_overRecycleBin`/`UpdateRecycleDropState`/`RecycleBinTarget`/`IsOverDockRecycleBin`/`MoveToRecycleBin`、`ApplyAvoidance` 的回收站让位豁免、`ResolveInteractionDropTarget` 的回收站与 dock 分支、`ExecuteInteractionDrop` 的 `RecycleBin` 动作；dock 侧：删 `_recycleBinContainer`/`_recycleHoverTimer`/`UpdateRecycleDropRect`/`UpdateRecycleHoverHighlight`（回收站回归 `SystemEntries` 表内普通条目）；kernel：删 `DockDropTargets.cs`；文案：设置分区 NoteBlock / README / 文件头索引同步 |
| R5 | 框选后左右键的"整组操作"失效（用户：**被你弄丢了**） | 选中集被高频清空：`window-tracker.DebugLog` 每秒写桌面根的 `BetterDesktop_debug.log` → `FileSystemWatcher` 500ms 防抖 → `Refresh()` → `LoadLocation()` **无条件 `_selection.Clear()`**；真机日志实证 `框选诊断 selCnt=6` → 0.9s 后按下即 `selCnt=0` → 整组收集退化成单项 | `LoadLocation` 改为**保留选中集 + 幸存者过滤**（同目录刷新保留、换目录/文件被删剔除，仅在真变化时抛 `SelectionChanged`）；`OnFsChange`/`OnFsRename` 忽略自产日志文件，去掉每秒重枚举的自噪声 |
| R7 | 左键拖动"视觉上是一个，但操作逻辑是群体"（用户实测） | 四轮只把**逻辑**接上了（松手落位），漏了**视觉**：拖动期间被拖整组没有像右键那样实时跟随（只有系统拖拽影像），用户以为只拖了一个 | `TryStartInteractionDrag` 起手即 `_dragMoving=true` + `CaptureLayoutSnapshot` + `BeginDragVisual`（与右键同一套）；`UpdateInteractionHover` 驱动 `UpdateFreeDrag`（空白处跟随）/ `ResetDraggedCellsToBase`（悬停可投递目标时退回）；`finally` 对未落位的收尾做还原 + `EndDragVisual` |
| R6 | 左键整组拖动"没有成功"（用户：**但是左键没有成功啊**） | 三版规则下"落在源图标/空白 = 无操作"，而真机日志（`from=(50,339)` → `pos=(36,333)`）显示用户松手点仍在选区内部 → 什么也没发生；而用户要的是**原生桌面**：拖到空白处应把图标挪过去 | 新增 `MoveDragSelectionToPlace`（整组按 按下点→松手点 平移 + `SnapTarget` 吸附 + `CommitLayout` 落盘）+ `HandleInteractionDrop` 统一分派（图标落点→交互，否则→落位）；`OnDragOver` 本窗口内一律给 `Move`/`Copy` 光标 |

真机证据（`%LocalAppData%\BetterDesktop\logs\host-20260917.log`）：
`左键拖放 → 文件夹: files=1 copy=False dst=...\bishe` 后紧跟 `Rebuild: items=80→79→78`（文件真的移走了）。

**R3 顺带补的两个洞**：① `effect != None` 说明落点被**外部程序**（资源管理器等）接受 → 兜底不得再执行内部动作（否则同一屏位置上的自家图标会被二次处理）；② `QueryContinueDrag` 的 `DragAction.Cancel`（Esc）必须置 `_interactionCancelled`，否则取消后 `DoDragDrop` 返回 `None`，兜底会误判成"落在无效目标"而照做。

## 7.05 三轮：框选后"整组操作失效"真根因（2026-09-17 用户报障）

**症状**：框选多个图标后，左键拖动 / 右键长按拖动都只作用于**一个**图标（"整组操作被你弄丢了"）。

**真根因不在拖放逻辑，而在选中集被高频清空**：

1. `window-tracker` 的 `DebugLog` 每条同步 `File.AppendAllText` 到**桌面根**的 `BetterDesktop_debug.log`；dock 每秒都在写（`Running: refresh windows=5`）→ 桌面目录 1 次/秒的文件写入事件。
2. `DesktopBrowser` 的 `FileSystemWatcher`（用户桌面 + 公共桌面，500ms 防抖）→ `Refresh()` → `LoadLocation()` **无条件 `_selection.Clear()`**。
3. 于是"框选 → 想整组拖动"时，选中集在 ~1s 内被清空：真机日志实证 `框选诊断 结束后 selCnt=6` → 0.9s 后下一次按下即 `selCnt=0`。左键走 `CollectInteractionPaths`、右键走 `CollectFreeDragCells`，两者都读 `SelectedPaths` → 双双退化成单项。
4. 新版"右键**长按 350ms** 才进入拖动"把窗口进一步拉长，使这次清空几乎必然先发生（旧版左键立即拖动常能抢在刷新前）。

**修复（`packages/shell/shell-desktop/Services/DesktopBrowser.cs`）**：
- `LoadLocation` 不再无条件清空：改为**保留选中集**，枚举完成后用新条目路径集做「幸存者过滤」（同目录刷新 → 原样保留；换目录 / 文件被删 → 自动丢弃），仅在集合真变化时抛 `SelectionChanged`（explorer 语义）。
- `OnFsChange` / `OnFsRename` 忽略**自产日志文件**（`BetterDesktop_debug.log` / `BetterDesktop_crash.log`）：消除"每秒重枚举一次"的自噪声（否则它会和真实变化抢 500ms 防抖窗口）。

**回归面**：`Delete()` 仍显式清空选中集；`MoveIntoFolder` 走 `Refresh()` → 被移走的路径在幸存者过滤里自动剔除。

## 7.08 五轮：桌面「站内导航」退役（用户：菜单栏导航栏 + 右键「后退」都没用了）

**前提变化**：桌面的文件夹现在是**直接交给 explorer 打开**（`DesktopIconsControl.Open` → `StartExplorerFolder`），
自绘桌面不再有站内导航（原 2026-08-31 设计是"菜单栏位置/下载/文档 → 桌面内导航"，2026-09-07 起已改为独立浏览窗口 + explorer）。
于是历史栈恒空、`Location` 恒为桌面 → 以下全是死 UI/死代码，按用户要求整体拆除：

| 位置 | 移除内容 |
|---|---|
| `shell-menu-bar/Windows/FolderToolbar.cs` | 五轮先去掉路径显示（`_browser.Location` + `LocationChanged` 订阅）+ 导航行 `← → ↑`；**六轮直接整条删除**（见下） |
| `shell-desktop/Controls/DesktopIconsControl.cs` | 桌面空白右键菜单的「后退」项 + 其分隔线（保留「刷新」） |
| `packages/api/Desktop/IDesktopBrowser.cs` | `Navigate` / `Back` / `Forward` / `Up` / `CanGoBack` / `CanGoForward` / `LocationChanged` |
| `shell-desktop/Services/DesktopBrowser.cs` | 历史栈 `_back`/`_forward`、上述成员、`LoadLocation` 里的 `LocationChanged` 触发（保留 `Location`/`Refresh`/枚举/文件操作） |

**保持不变**：菜单栏左区「位置 / 下载 / 文档」入口（它们开的是 `FolderBrowserWindow` 独立浏览窗口，自带 `NavigateTo`，与本契约无关）；
桌面文件操作、排序、新建、导入、选中集与 2026-09-17 的"选中集保留"修复全部不动。

## 7.09 六轮：菜单栏文件夹工具条**整条删除**（用户：这些功能自绘右键菜单就行了）

用户指出工具条上剩下的 `刷新 / 剪切 / 复制 / 粘贴 / 重命名 / 删除`（+ 折叠钮）与**自绘桌面右键菜单完全重复**：

| 工具条按钮 | 自绘右键菜单里的等价入口 |
|---|---|
| 剪切 / 复制 / 粘贴 | 图标菜单 剪切·复制·粘贴（`Ctrl+X/C/V`）＋空白菜单 粘贴 |
| 重命名 | 图标菜单 重命名（`F2`） |
| 删除 | 图标菜单 删除（`Delete`） |
| 刷新 | 空白菜单 刷新 |
| （另）属性 / 新建 / 排序 | 图标菜单 属性（`Alt+Enter`）；空白菜单 新建 / 排序方式 |

→ 删除 `FolderToolbar.cs`，并清掉它的整条依赖链：

| 文件 | 改动 |
|---|---|
| `shell-menu-bar/Windows/FolderToolbar.cs` | **删除文件** |
| `shell-menu-bar/Windows/MenuBarLeftZone.cs` | 删 `_folderToolbar`/`_desktopBrowser` 字段 + 构造参数 + Dispose；移除 `using ...Desktop.Contracts` |
| `shell-menu-bar/Windows/MenuBarWindow.cs` | 删 `IDesktopBrowser? desktopBrowser` 参数与透传 + 该 using |
| `shell-menu-bar/MenuBarPlugin.cs` | 删 `context.Get<IDesktopBrowser>()` 与透传 + 该 using（菜单栏不再消费桌面契约） |

左区现在只有：Logo 快捷菜单按钮 + 前台窗口标题 + 位置/下载/文档。

## 7.1 Handoff（§14）

| 项 | 内容 |
|---|---|
| 模式判定 | 无现成技术力文档（检索关键词：桌面图标拖拽/拖放交互/icon drag reorder，命中仅 `desktop-progman-embed`（嵌入相关，不含拖放语义））→ **工程代码权威** |
| 注入清单 | `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs`、`Services/DesktopBrowser.cs`、`packages/shell/shell-context-menu/Services/FileClipboard.cs`、`packages/shell/shell-app-source/Services/ShellLinkResolver.cs` |
| 适配参数 | net8.0-windows / WPF；命名空间 `BetterDesktop.Shell.Desktop.Controls`；设置键 `desktop.autoExitArrangeOnDrag`（语义迁移，键名不变） |
| 禁区 | `DockDropTargets` 已删除（回收站无特殊处理，2026-09-17 用户拍板）——不得重新引入"拖到回收站=删除"通道；不得动 `DesktopMenuPopup`；不得恢复跨进程 DefView 委托 |
| DoD 核销表 | D1…D10（见 §6），实施后逐条勾销 |
