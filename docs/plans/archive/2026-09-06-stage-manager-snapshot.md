# 计划：控制中心·台前调度（专注助手降级为「快照」子功能）

> ⚠️ **已下线（2026-09-06）**：功能经多轮真机验证后由用户决定整体移除（包/测试/接线/控制中心行均已删除）。
> 本文仅作历史决策与踩坑记录保留；其中最有价值的结论：退场/上场必须用 DWM cloak（最小化/改矩形会破坏
> WINDOWPLACEMENT.rcNormalPosition）、动画只能画在飞行缩略图替身上、透明容器靠 DWM 背景而非分层窗口。

> 日期：2026-09-06 · 类别：功能开发 · 深度：standard · 仓库：better-desktop-cordis
> 目标：控制中心「台前调度」从空壳变为真功能；「专注助手」独立行移除，降级为台前调度面板内的「快照」子功能，仅台前调度激活时可用。

## 1. 目标（场景语言）
1. 用户在控制中心点「台前调度」开关 → 桌面图标隐藏，已运行窗口重排：当前窗口居中为主窗口，其余窗口以卡片列叠放在屏幕左侧（类似 macOS 台前调度/截图效果）。
2. 用户无操作若干秒后，左侧窗口列整体向左滑出屏幕隐藏；鼠标移到屏幕左缘热区再滑回。
3. 点击左侧某窗口 → 该窗升级为主窗口（居中），原主窗口落入左列。
4. 台前调度面板内「拍摄快照」记录当前所有可见窗口布局；之后可一键恢复该布局。窗口数量多时左列以重叠牌组方式全部容纳。
5. 关闭台前调度 → 一切还原（窗口回原位、桌面图标恢复）。

## 2. 技术力检索结论
- 命中：`TECH-KNOWLEDGE/63-桌面渲染/dwm-live-thumbnail.md`（DWM 实时缩略图，**本方案不用**——采用物理搬移窗口，无需缩略图，见 §3 决策）。
- 命中：`shell-window-tracker` 的 `RunningAppDetector.GetRunningWindows()`（可见顶层窗口枚举，已过滤 Progman/WorkerW/工具窗口/cloaked）+ `ActivateWindow()`（AttachThreadInput 前台解锁范式）——直接复用 [verified]。
- 未命中：「窗口布局编排 / 窗口快照持久化 / 空闲检测」无现成功能文档 → **新建**，实现稳定后按模板回写 `TECH-KNOWLEDGE/14-窗口与快捷键/`（阶段排列 stage-manager-window-arrange）。
- 关联红线继承：`SetForegroundWindow` 前台锁 → 复用 `RunningAppDetector.ActivateWindow`，不重写。

> **2026-09-06 修订二（用户再次纠正，以本节为准）**：
> ① 左列不是"窗口列表"——**点左列缩略图 = 选中该窗口在桌面显示，其余窗口同时退场（最小化）**。
> ② **快照 = 多个窗口合并成的同一个状态，同开同关**；左列快照卡片是**多个缩略图叠加**。
> ③ 控制中心「台前调度」与「快照」两行**常态显示**，台前调度没开时快照行**灰着不可点**。
>
> 修订一（左列用缩略图而非缩小真实窗口）仍成立：真实窗口**不被缩放**，只会在"选中上场/同开同关"时改变显示状态（还原/最小化），快照同开时按记录位置还原。

## 3. 关键决策
| 决策 | 选择 | 理由 |
|---|---|---|
| 左列窗口呈现 | **DWM 实时缩略图卡片**（顶部应用图标+标题，下方缩略图），真实窗口不搬不缩 | 用户明确要求；缩略图目标必须是顶层窗口（禁 HwndHost）、窗口禁分层、图标区与缩略图区互斥 |
| 重叠容纳多窗口 | 卡片高度固定，**垂直步距 = min(基准步距, 可用高/(N-1))**，N 大时自动加密重叠 | 任意窗口数都有合理显示（截图的 deck 效果） |
| 空闲隐藏 | `GetLastInputInfo` 空闲阈值 → 左列窗口 x 平移滑出屏外；`GetCursorPos` 命中左缘热区（x<8 物理px）→ 滑回 | 单一 DispatcherTimer(250ms) 轮询两 API，免低级钩子 |
| 前台变化升级 | 同一 timer 轮询 `GetForegroundWindow`，变化且活跃中 → 重排 | 免跨插件事件耦合（不强依赖 IWindowTrackerService 事件） |
| 隐藏桌面 | `Progman/WorkerW→SHELLDLL_DefView→SysListView32` ShowWindow(SW_HIDE/SW_SHOW) | 仓库已知成熟技法；单一写入点、可逆 |
| 快照存储 | `%LocalAppData%\BetterDesktop\StageManager\snapshots.json`（JSON 手写序列化，与 MenuManager backup 同风格） | 不引依赖；换机可拷 |
| 新包 vs 塞进 menu-bar | **新包 `shell-stage-manager`** | 模块化可复用（编排逻辑与 UI 无关）；符合仓库一功能一包惯例 |

## 4. 改动清单
**新包 `packages/shell/shell-stage-manager/`（BetterDesktop.Shell.StageManager.csproj，net8.0-windows，引用 shell-window-tracker + shell-core/kernel 契约）**
1. `Contracts/IStageManagerService.cs`：`bool IsActive`、`event EventHandler? StateChanged`、`void Activate()/Deactivate()/Toggle()`、`int IdleSeconds {get;set;}`、`StageSnapshot CaptureSnapshot(string? name)`（非活跃抛 `InvalidOperationException`）、`IReadOnlyList<StageSnapshot> GetSnapshots()`、`int RestoreSnapshot(Guid id)`（返回恢复数）、`void DeleteSnapshot(Guid id)`。
2. `Services/StageLayout.cs`（纯函数）：输入 工作区物理矩形 + 窗口数 + 卡片规格 → 主窗矩形 + 左列槽位数组（多列/重叠步距收敛）。可单测内核。
3. `Native/WindowArranger.cs`：`GetWindowRect/SetWindowPos/GetWindowPlacement(showCmd)/SetWindowPlacement` P/Invoke；`TryMove(hwnd, rect)`（失败记日志返回 false）；`GetLastInputInfo/GetCursorPos` 封装。
4. `Native/DesktopIconsHider.cs`：DefView 链查找 + 幂等 Hide/Show（单一写入点）。
5. `Services/SnapshotStore.cs`：`StageSnapshot` 模型（Id/Name/CreatedAt/Windows[Hwnd,Title,ExePath,L,T,R,B,ShowCmd]）+ Load/Save。
6. `Services/StageManagerService.cs`：编排（激活流程/timer 轮询：空闲折叠、热区展开、前台重排/升级；快照守卫；Deactivate 还原 pre-stage 布局）。UI 线程 DispatcherTimer；全部 try-catch 记日志（M10）。
7. `StageManagerPlugin.cs`：`Provide IStageManagerService`，`Inject` 空。
8. `README.md`。

**shell-menu-bar 改动**
9. `Windows/StageManagerPanelWindow.cs`（新，MenuBarPopupWindow 子类）：台前调度大开关 + 空闲秒数调节 + 快照区（拍摄按钮[非活跃禁用+提示] / 快照列表[名称·时间·恢复·删除]）。
10. `Services/ControlCenterFeatureCatalog.cs`：删「专注助手」行；「台前调度」改为 toggle 行（ToggleAsync→服务）+ OnActivate 打开面板；Build 增 `IStageManagerService?` 参数（3 处调用点同步：MenuBarExtensions / StatusBarMenuBarExtension / PlaygroundPreviewFactory）。
11. `Windows/ControlCenterWindow.cs`：行文案/图标注释同步（台前调度行已有）。

**host / 解决方案**
12. `BetterDesktop.slnx`：+stage-manager 与 +stage-manager-tests（含 x64 Platform）。
13. `host/Bootstrap.cs`：`StageManagerPlugin` 注册在 MenuBarPlugin 之前（约 6.7 节前）。
14. `packages/shell/shell-menu-bar/BetterDesktop.Shell.MenuBar.csproj`：+ProjectReference stage-manager。

**测试**
15. 新工程 `packages/shell/shell-stage-manager-tests/`：StageLayout（正常/单窗/零窗/窗口数溢出重叠收敛/工作区过小）、SnapshotStore 持久化往返、Restore 匹配（hwnd 命中/exe+title 兜底/exe 兜底/无匹配计数）。

## 5. 交互细节（拍板）
- 主窗口矩形 = 工作区去掉左列宽度后的剩余区域居中（占宽 ~70%、高 ~85%）。
- 左列卡片：宽 = min(240, 工作区 22%物理宽)，高 = 宽×0.62；步距基准 = 卡片高×0.7。
- 快照恢复仅记录/还原 **normal placement**（最大化的窗口记 showCmd，恢复时先还原再摆位）。
- 面板 UI 中台前调度关闭时：拍摄/恢复/删除按钮全禁用 + 一行提示「快照是台前调度的子功能，请先开启台前调度」。
- 设置键：`stage-manager.idleSeconds`（默认 6，ISettingsService 持久化）。

## 6. 边界与异常（工具型项目强制项）
- 最大化窗口：搬移前 SW_RESTORE，记录原 showCmd，Deactivate 时还原。
- 提权窗口 SetWindowPos 失败：TryMove 返回 false + 日志，跳过不中断整批。
- 宿主崩溃残留风险：UnloadAsync 兜底 Deactivate；崩溃残留为已知限制（README Known Limitations，后续可加 watchdog 复原）。
- 窗口 0 个：仍隐藏桌面、正常进活跃态；主窗=无。
- 活跃中新开窗口：下个 timer 周期前台变化触发重排，自动纳入左列。
- 资源释放：timer Stop、事件退订、无句柄缓存（hwnd 即用即取）。
- 快照恢复无匹配窗口：返回恢复数，面板 toast 文案如实显示「恢复 n/m 个窗口」。

## 7. 对外契约兼容
- `ControlCenterFeatureCatalog.Build` 增参：内部 internal API，3 个调用点同仓同批修改，无外部消费者 → 兼容风险低。
- 新设置键、新快照文件均增量，不影响既有 settings.json。

## 8. 风险
| 风险 | 缓解 |
|---|---|
| 部分应用拒绝被缩小（最小尺寸限制） | 卡片尺寸下限 160×100；失败仅日志 |
| 空闲折叠与热区展开抖动 | 折叠后仅热区触发展开，展开需连续 2 个 tick 命中（防抖） |
| explorer 重启期间 DefView 句柄失效 | 每次操作重查链（不缓存 hwnd） |
| 全屏游戏/独占模式被搬移 | 枚举已滤 cloaked；Rust/游戏窗口仍可能异常——记录已知限制 |

## 9. 实现顺序
① 新包骨架 + StageLayout 纯函数 + 单测 → ② WindowArranger/DesktopIconsHider → ③ StageManagerService 编排 → ④ slnx/Bootstrap/csproj 接线 → ⑤ menu-bar 面板 + Catalog 改造 → ⑥ 全量构建 + 测试 → ⑦ 文档回写（README + TECH-KNOWLEDGE 新文档 + 索引）。

## 10. DoD（验收）
1. `dotnet build BetterDesktop.slnx -warnaserror` 0 警告 0 错误（构建前先停 BetterDesktop.Host）。
2. shell-stage-manager-tests 全绿。
3. 真机场景走查：控制中心开台前调度 → 桌面图标消失、窗口左列牌组排布；等 6s 左列滑出；鼠标贴左缘滑回；点左列窗口升级为主窗；拍快照→乱拖窗口→恢复快照布局还原；关台前调度→窗口回原位+图标恢复。
4. 非活跃态面板内快照按钮禁用且有提示文案。

## 11. beyond（可选增强，不进强制 scope）
- 多显示器：按主窗所在显示器编排（MVP 先主屏工作区）。beyond
- Win+Tab 式全屏遮罩预览（DWM 缩略图 overlay 增强视觉）。beyond
- 快照导入/导出、多套布局场景（工作/娱乐）。beyond

## 12. 开放问题
- 无阻塞项。桌面隐藏默认只处理 explorer 图标层；若用户开启自绘桌面（shell.desktop），其桌面窗口为普通 WPF 窗口会被排进左列（running windows 过滤自身进程，不会——SelfExePath 已排除）。[verified：RunningAppDetector 排除自身进程]

## 13. 交接（§14）
- 交接对象：`skills/ability-reuse-alignment/SKILL.md` 按本节执行。
- 注入文档：`dwm-live-thumbnail.md`（备查）、`window-peek-zorder.md`（备查）、本计划 §3/§5/§6 约束。
- 适配参数：仓库根 `better-desktop-cordis/`；构建 `dotnet build BetterDesktop.slnx -warnaserror`；包命名 `BetterDesktop.Shell.*`；日志走 `context.Logger` + 内核 DebugLog 惯例。
- DoD 核销表：§10 逐项核对，场景走查（第 3 条）必须真机执行。
