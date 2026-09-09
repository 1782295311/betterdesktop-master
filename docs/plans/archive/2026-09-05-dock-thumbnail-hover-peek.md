# Dock 缩略图：悬停存续 + 临时置顶（Peek）— 2026-09-05

## 1. 需求（用户原话拆解）

1. **存续收口**：运行区与固定应用区 hover 出的缩略图浮层，**只有鼠标落在「来源图标」或「缩略图浮层」上时才继续存在**，离开二者即消失。
2. **临时置顶（Aero Peek 语义）**：鼠标落在某个缩略图上时，把该窗口**暂时抬到最前**（不抢焦点/不激活）；鼠标移开后**精确还原抬起前的 Z 序**。

## 2. 现状（代码实证）

| 位置 | 现状 |
|---|---|
| `shell-dock/DockWindow.xaml.cs` | `ScheduleOpenPreview`（延迟 = `MouseHoverTime`）→ `OpenRunningPreview` → `new ThumbnailWindow(...).Show()`；关闭靠 `container.MouseLeave` → `ScheduleDeferClose`（350ms）→ 判 `_flyout.IsMouseOver` |
| `shell-window-tracker/Thumbnail/ThumbnailWindow.cs` | 每窗口一个 `DwmThumbnail` 包在 `Border cell` 里；`cell.MouseLeftButtonUp` 激活窗口；窗口 `Topmost=true` |
| `shell-window-tracker/Thumbnail/DwmThumbnail.cs` | `DwmRegisterThumbnail` 实时缩略图渲染 |

**缺口**：
- A. 关闭判定只用 `IsMouseOver`（分层透明窗 + 跨窗移动易漏事件），且**不校验来源图标**——鼠标移到别的图标/别处时旧浮层会滞留；`IsMouseOver` 事件丢失 = 缩略图"赖着不走"。
- B. 无任何 Z 序动作：缩略图只"看"不"抬"，与 Windows 任务栏预览体感不一致。
- C. 无 Peek 能力资产：TECH-KNOWLEDGE 检索 `EnumWindows / HWND_TOPMOST / Z 序 / peek` → **无现成功能，新建**。

## 3. 方案

### 3.1 新建 `shell-window-tracker/Thumbnail/WindowPeek.cs`（Z 序 Peek 能力）

- `Begin(hwnd)`：`EnumWindows` 取 Z 序快照（顶→底，仅可见顶层窗 + `WS_EX_TOPMOST` 位）→ 纯函数 `Plan()` 裁决 → `SetWindowPos(hwnd, HWND_TOP, NOACTIVATE|NOMOVE|NOSIZE|NOOWNERZORDER)`。
- `End()`：`SetWindowPos(hwnd, 还原锚点, 同 flags)` 精确插回原位。
- 纯函数 `Plan(IReadOnlyList<ZOrderEntry>, target)` → `PeekPlan(CanPeek, InsertAfterOnRestore)`，**可单测、不调 Win32**。
- 拒绝 peek 的窗口：不可见 / 最小化 / **本身已置顶** / 属于本进程 / 不在快照中。

### 3.2 `ThumbnailWindow`：缩略图 hover → Peek

- `cell.MouseEnter` → `_peek.Begin(hwnd)`；`cell.MouseLeave` → `_peek.End()`（仅当 peek 的正是自己）。
- `Closed` → `_peek.End()`：**任何退出路径都必须还原 Z 序**（点选激活走 `BeginInvoke(Background)`，还原先于激活执行，不冲突）。
- 删除宿主的 `PreviewMouseLeft` 事件（存续改由宿主看门狗裁定，事件无消费方）。

### 3.3 `DockWindow`：存续看门狗

- 新增 `DispatcherTimer _previewWatchdog`（120ms），浮层打开期间运行：光标不在「来源图标矩形 ∪ 浮层窗口矩形」持续 **300ms** 即关闭（300ms 宽限吸收图标→浮层之间的 8px 空隙穿越）。
- 命中判定用 **物理像素**：`GetCursorPos` + `GetWindowRect(浮层)` + 来源图标容器 `TransformToAncestor × DPI`，比 `IsMouseOver` 稳（分层窗跨窗移动不丢事件）。保留 `IsMouseOver` 作并集兜底。
- `ScheduleOpenPreview` 多收一个 `FrameworkElement anchor`：切到另一个图标时**立刻关掉旧浮层**（旧逻辑要等延迟，出现"两个应用缩略图并存"）。
- `OnAutoHideTick`：`IsCursorOverDock(cursor) || IsPointerOverPreviewOrIcon(cursor)` → `SetDockVisible(true, topmost: true)`。**必须**：dock 平时非置顶，被 peek 抬起的窗口会盖住它。

## 4. 红线（实现纪律）

1. **绝不用 `HWND_TOPMOST` 抬窗**：会把窗口抬到 dock/浮层/任务栏（都是置顶层）之上，浮层立刻被自己的预览目标盖住。抬到**普通层最前（HWND_TOP）**即可——置顶层恒在普通层之上，天然压住被抬窗口。
2. **还原锚点必须是「抬升前紧邻其上的非置顶窗口」**：拿置顶窗口当锚点会把目标带进置顶层（Z 序分层规则）。找不到 → 回落 `HWND_TOP`。
3. **不激活**：`SWP_NOACTIVATE`，Peek 只改 Z 序不抢焦点（Windows 同款）。
4. 幂等：`Begin` 同句柄无副作用；`End` 可重复调用；目标/锚点窗口已销毁则跳过还原。
5. peek 抬起的窗口**不得盖住 shell 自身 UI**——靠 dock 在预览期保持置顶（3.3 最后一条）保证。
6. `TreatWarningsAsErrors=true`：新增字段必须被读取，无未用变量/未用事件。

## 5. DoD（验收）

- [x] `WindowPeek.Plan` 单测：中间位/普通层最前/目标不在快照/目标已置顶 + 生命周期幂等 共 8 例（window-tracker 10/10 绿）。
- [x] shell-dock + shell-window-tracker 构建 0 错 0 警；dock 单测 11/11 绿。
- [x] 全仓 `dotnet build BetterDesktop.slnx` 0 错 0 警。
- [ ] **人工真机**（需用户走查）：hover 图标出缩略图 → 移入缩略图 → 目标窗口被抬起且浮层仍在最上 → 移开 → Z 序还原、浮层消失；A 图标 → B 图标切换不并存；离开 dock/浮层后 300ms 浮层自动消失。

## 12. 运行区空状态（2026-09-06，用户追加需求）

无运行应用时**不再渲染「（无运行窗口）」占位**，且分隔线与滚动区一并隐藏——
只藏图标不藏分隔线会剩一条孤零零的竖线 + 空位；dock 宽度由 SizeToContent 自动收窄。
保留原状态机的两个关键点：空态短路标志 `_runningEmptyRendered`（避免每秒白做 Clear）、
`_lastRunningKeys.Clear()`（从空恢复后 keys 必变化 → 触发重建，历史根因"运行区不知所踪"的防线）。

## 11. 缩略图右上角关闭按钮（2026-09-06，用户追加需求）

- **位置红线**：按钮**不能**盖在缩略图上。DWM 缩略图由 DWM 合成、画在窗口内容**之上**，
  任何 WPF 控件只要落进 `DwmThumbnail` 的矩形就会被整个盖住（stage-manager 同款约束）。
  → 卡片改为**两行 Grid**：第 0 行是 20px 顶栏（关闭按钮右对齐 = 卡片右上角），
  第 1 行是缩略图，两者区域严格互斥、零重叠。
- **事件截断**：按钮用 `Border + TextBlock` 手搓（不用 Button），
  `MouseLeftButtonUp` 里 `e.Handled = true` —— 否则事件冒泡到 cell 的"点击激活"，
  会出现"关窗口的同时又去激活它"的语义打架。
- **关闭语义**：`PostMessage(WM_CLOSE)` 优雅关闭（应用可提示保存/拒绝），**不强杀进程**；
  PostMessage 而非 SendMessage，无响应应用不会挂死调用方。
- **即时反馈**：发完消息立刻把卡片从浮层移除（WM_CLOSE 异步，等窗口真销毁会有"点了没反应"的观感）；
  若该窗口正处于 peek，移除前 `Cancel()` 放弃还原；最后一张被关掉则直接收掉浮层。
- 技术库已回写：1413 新增已知坑 4、5（控件与缩略图矩形互斥 / WM_CLOSE 而非强杀）。

## 10. 四轮修复（2026-09-06，用户澄清真症状：「任务管理器最小化后 dock 里点它唤不出来」）

> 注意：本轮与"缩略图临时置顶"是**两条链路**——前者是 Z 序展示，本轮是**窗口激活**。
> 症状共同点是"看得见、点不动"，但根因完全不同。

| 根因 | 说明 | 修复 |
|---|---|---|
| **① ShowWindowAsync 竞态**（最常见） | `ShowWindowAsync(SW_RESTORE)` 只往目标线程**投递消息**就返回，窗口此刻仍处最小化；紧随其后的 `SetForegroundWindow` 对最小化窗口**必然失败**，且无任何报错 | 改为**同步** `ShowWindow(hwnd, SW_RESTORE)`，保证状态在返回前已更新 |
| **② UIPI 静默失败**（任务管理器专有） | Taskmgr 在管理员账户下自动提权到**高完整性**；本 shell 中等完整性 → `ShowWindow`/`SetForegroundWindow` 对它全部静默失败 | 新增 `ActivateWindowOrRelaunch`：前台设置明确失败 + 320ms 校验仍 `IsIconic` → 回退 `ShellExecute` 该 exe（Taskmgr 是单例，由**它自己**把已有实例唤到前台，不受 UIPI 限制） |
| **③ 返回值被吞** | `SetForegroundWindow` 的返回值全程未读 → 失败零线索、也无法触发回退 | `ActivateWindow` 改为返回 bool；dock 点击与缩略图点击都改用 `ActivateWindowOrRelaunch` |
| **④ 伪窗口抢选中** | 有标题、可见、未最小化但矩形 < 48×48 的隐藏宿主窗口会被当成"运行中的应用"，激活它毫无反应 | `GetRunningWindows` 增加伪窗口过滤（**AND + 小阈值**，最小化的窗口不过滤——它正是要唤出来的目标） |

防误伤设计：回退**不是**无条件重启——只有"前台明确失败 **且** 延迟校验仍最小化"才触发；
窗口已显示只是没抢到焦点时不重启（否则非单例应用会多开窗口，比唤不出来更糟）。

日志判据（桌面 `BetterDesktop_debug.log`，tag=`Activate`）：
- `SetForegroundWindow ok` → 直连成功；
- `SetForegroundWindow retry=false` → 直连失败；
- `relaunch fallback exe=..` → 已触发 UIPI 回退（看到这行 = ②成立）。

验证：shell-window-tracker / shell-dock / host 依赖链 0 错 0 警；dock 11/11、window-tracker 12/12 绿。
技术库新增 `1414-window-activate-restore.md`（双索引已同步）。

## 9. 三轮修复（2026-09-06，用户建议「让 dock 层级等同任务栏」+ 加任务管理器入口）

### 9.1 置顶仍不生效 —— 真根因（两个叠加）
1. **`OnAutoHideTick` 的早退顺序错误**：`if (!_autoHideEnabled) return;` 排在"悬停 → 置顶"判定**之前**，
   自动隐藏关闭的用户永远走不到 `SetDockVisible(topmost:true)` → dock 恒为非置顶
   → peek 抬起的窗口把 dock 整个盖掉（用户感知的"两个置顶打架"）。已把该判定提到所有早退之前。
2. **层级定位错误**：dock 原本 `DefaultTopmost => false`（想"不盖在窗口上"）。按用户拍板改为
   **常驻置顶，层级等同系统任务栏**（`DefaultTopmost => true`，`SetDockVisible` 默认参数同步改 true）。
   AppBar 只解决"避让"（最大化窗口不重叠），不解决"层级"（谁盖谁）——二者正交，都要做。
   已核对副作用：开始菜单 / 菜单栏弹层 / 预览浮层 / 设置窗口本就是 topmost（或在其上），
   应用提取器 760×620 居中不与底部 dock 重叠，桌面图标本就"浮于 dock 之下"，均无回归。

### 9.2 peek 加强：抬完自校验 + 置顶层回退
`RaiseToTop` 抬窗后重新枚举 Z 序自校验：目标不是「普通层最前」→ 判定被前台锁挡住，
回退为 `HWND_TOPMOST` 进置顶层、再插到"抬窗前置顶层最底部窗口之下"
（仍被 dock/任务栏压住，但一定压住所有普通窗口，含前台窗口）。
`End` 用 `HWND_NOTOPMOST` 成对退出（否则第三方窗口被永久置顶）。
日志 `Peek begin ... mode=normal|topmost` 直接告诉排查者走了哪条路。

### 9.3 dock 新增「任务管理器」入口
`SystemEntries` 扩为 6 元组（+IsTaskManager），新增 `dock.systemTaskManager`：
- 图标：`SHParseDisplayName` 可直接吃 exe 路径 → 任务管理器真实图标，无需特例链路。
- 启动三律：① 已运行 → 直接激活（Taskmgr 单例，二次启动不保证置前）；
  ② 用 `Environment.SpecialFolder.System` 绝对路径，不靠 PATH 解析裸名字（宿主环境 PATH 可能被改）；
  ③ 激活一律 `Dispatcher.BeginInvoke(Background)` 延迟派发（MouseUp 中鼠标仍被捕获，SetForegroundWindow 会被拒）；
  冷启动再补一次 700ms 延迟激活，避免"启动了却躲在最大化窗口后面"。

### 9.4 验证状态
- shell-dock / shell-window-tracker / host 依赖链构建 **0 错 0 警**；dock 11/11、window-tracker 12/12 绿。
- ⚠️ 全仓 `BetterDesktop.slnx` 构建**被并行的 WIP 包阻断**：8 个错误全在
  `packages/shell/shell-stage-manager/Windows/StageStripWindow.cs`（Thickness 运算 / `WindowIconExtractor.Get` 重载），
  文件最后修改时间 2026-09-06 00:33–00:37，与本次改动无关，按"勿扰"原则未动。
- 技术库已回写：1413 新增红线 7 回退段 + 红线 10（条带层级），
  **并纠正 1412 的错误结论**（原文"条带无需置顶即可常驻可见" → 改为"AppBar 只保证避让、不保证层级"）。

## 8. 二轮修复（2026-09-06，用户实测反馈「只成功一次」+「最小化窗口也要能置顶」）

| 症状/诉求 | 根因 | 修复 |
|---|---|---|
| 只成功一次，其它都没成功 | **非前台进程裸调 `SetWindowPos(HWND_TOP)`**：系统只把窗口放到「前台窗口**之后**」，前台窗口仍压在上面 → 视觉上等于没抬；只有目标恰好没被前台窗口压住时才看得出来 | `RaiseToTop()`：抬窗前 `AttachThreadInput(本线程, 前台线程, true)`，抬完立刻解绑（与本项目 `RunningAppDetector.ActivateWindow` 同款已验证范式） |
| 最小化窗口不生效 | `IsPeekable` 里 `IsIconic` 直接拒绝（且最小化窗口的 Z 序操作本身无意义，必须先显示） | Begin：最小化 → `ShowWindowAsync(SW_SHOWNOACTIVATE=4)` 不激活地显示 → 再抬 Z 序；End：先还原 Z 序，再 `SetWindowPlacement(原placement)` 收回最小化（`ShowWindow(SW_MINIMIZE)` 会激活"下一个"窗口，禁用） |
| 点选缩略图时"闪一下" | End 先收回最小化、ActivateWindow 再还原 | 新增 `WindowPeek.Cancel()`：点选时放弃还原，由激活流程接管 |
| 触发不稳定 | 依赖 `MouseEnter/MouseLeave`，分层透明窗跨窗口移动会丢事件或滞留 | 改为**鼠标移动 + 命中测试**驱动（`HitTestAndPeek`），每次移动重算；`MouseLeave` 仅兜底；浮层 `Loaded` 时用 `Mouse.GetPosition` 补一次命中测试（浮层可能盖在静止光标下弹出，此时无任何 MouseMove） |
| 看不出"到底成没成" | 目标窗口本来就在最前时，抬窗毫无视觉变化 | 被 peek 的缩略图描边高亮为强调色（反映 `WindowPeek.Target`，= 真正生效的目标） |
| 无法定位失败 | 失败静默 | Begin 失败写 `Peek skip hwnd=.. reason=hidden/already-topmost/own-process/invalid-handle/not-in-zorder`；`SetWindowPos` 失败写 LastError。日志在桌面 `BetterDesktop_debug.log` |

新增单测 2 例（最小化窗口仍在 Z 序快照、Cancel 后 End 为 no-op），window-tracker 12/12、dock 11/11、全仓 0 错 0 警。

## 7. 实现补充（落地时新增/调整）

1. **锚点抗重建**：运行区每秒可能重建容器，预览来源图标改按 `DockItemId` 记录（运行区卡片补 `Tag = item.Id`），容器失效后 `FindItemContainer` 重新定位，否则一次重建就会误判"光标已离场"把预览关掉。
2. **回到图标不重启打开计时**：`ScheduleOpenPreview` 命中同一 `DockItemId` 时直接返回——否则「图标→浮层→回图标」会在 400ms 后销毁重建浮层（闪烁 + DWM 缩略图重注册），与"持续存在"语义相悖。
3. **存续判定纯几何**：弃用 `_flyout.IsMouseOver`（分层透明窗命中态可能滞留为真 → 浮层永不关闭，正是"赖着不走"的成因），改用 `GetCursorPos` + `GetWindowRect` + 图标矩形×DPI 的物理像素判定。
4. **预览期 dock 保持置顶**：`OnAutoHideTick` 的悬停分支加入 `IsPointerOverPreviewOrIcon(cursor)`——dock 平时非置顶，被 peek 抬起的窗口会盖住它。
5. 已回写技术库：`TECH-KNOWLEDGE/14-窗口与快捷键/1413-window-peek-zorder.md`（双索引同步）。

## 6. 交接（实现约束）

- 只动 3 个文件 + 1 个测试文件，不碰 dock 布局/AppBar/菜单。
- 不改 `ThumbnailQuality` 设置语义，不新增设置项（Peek 无开关，与需求一致）。
