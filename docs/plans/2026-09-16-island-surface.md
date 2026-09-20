# 灵动岛表面落地计划（2026-09-16）

> 承接 [2026-09-14-dynamic-island.md](2026-09-14-dynamic-island.md)（活动模型与仲裁）与 [2026-09-14-island-motion-and-rendering.md](2026-09-14-island-motion-and-rendering.md)（动效规格）。
> 上一轮（2026-09-15）已落地 M3：`IActivityService` 契约 + `ActivityService` 仲裁纯逻辑 + 单测。本轮接着做**岛表面本体 + 动效 + 三个既有消息源接线**，即两份文档「实现顺序」的第 3–5、7–8 步。
> 用户口径（2026-09-16）：「继续灵动岛的开发，UI 必须十分现代化、灵动化」。

## 1. 本轮 Objective

把「活动的呈现面」从零建成可运行表面，并让三个已就绪的消息源真正出现在岛上：

1. 活动服务入图：`IActivityService` 由内核 Provide（此前只有实现类，没有 Provide 点），TTL 心跳驱动，变更经 IEventBus 广播。
2. 新包 `shell-island`：岛窗口 + 自绘液态胶囊 + 弹簧动效 + 脉冲进度环 + 命中测试 + 无障碍。
3. 消息源接线：媒体播放（Sticky）、格式转换（Progress/终态）、剪贴板捕获（Transient）。
4. 抑制规则：全屏/游戏/演示模式不弹出（进队列，退出补播）。
5. 设置与装配：动效强度三档、各来源开关、启停开关；`cordis.yml` + `Bootstrap` + `slnx` 装配；机制登记与 README。

## 2. 现状锚点（源码核实，2026-09-16）

| 面 | 状态 | 证据 |
|---|---|---|
| 活动仲裁 | ✅ 已落地 | `packages/api/Activity/ActivityContracts.cs`、`packages/shell/shell-core/Activity/ActivityService.cs`、`shell-core-tests/Activity/ActivityServiceTests.cs` |
| 活动服务 Provide | ⬜ 无 Provide 点 | 全仓无 `Provide<IActivityService>`；`ShellCorePlugin.LoadAsync` 只 Provide `IDesktopSurface` |
| 岛表面 | ⬜ 不存在 | 全仓无 `shell-island` 包、无 `IIslandSurface` 实现 |
| 菜单栏 | 两列 root Grid，高 20 DIP | `shell-menu-bar/Windows/MenuBarWindow.cs:96-123`、`Contracts/MenuBarMetrics.cs` |
| 媒体源 | ✅ 服务可用 | `api/Music/IMediaPlaybackService.cs`（`StatusPlugin` Provide）；无订阅者零开销；进度不触发事件，需轮询快照 |
| 转换源 | ✅ 事件可用 | `api/Convert/Events.cs`（`ConvertProgressEventPayload` 等）；`ConversionService` 经 IEventBus 发 `convert/progress|finished|failed|batch-finished` |
| 剪贴板源 | ✅ 公开面可用 | `api/Clipboard/IClipboardService.cs`（`HistoryChanged` + `GetLastCopiedContent`）；`ClipboardPlugin` 在壳进程已 Provide |
| 按序粘贴预览 | ⛔ 跨进程不可达 | 会话队列在 `ClipboardIpcClient`（面板 exe 进程）私有列表；壳进程客户端的计数恒为 0（见 §6 偏离说明） |
| D3D11/DComp 互操作库 | ⛔ 未拍板 | [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md) §六-6 待拍板；本机 NuGet 缓存无 Vortice |

## 3. 关键决策

### 3.1 渲染路径：WPF 自绘几何（本轮），DComp 自渲染留待拍板

动效规格 §2 的目标路径是「无重定向位图窗口 + DirectComposition + HLSL metaball」。该路径的前置（引入 D3D11/DComp/D2D 互操作库）在索引文档里明确标注**须用户拍板**，且本机 NuGet 缓存无 Vortice、离线环境无法可靠还原。

因此本轮走**规格 §7 降级矩阵的第二档**（几何形变）的增强版，并把它做成与档位无关的**唯一实现**：

- 岛的胶囊与菜单栏下沿用**同一套自绘几何**（`IslandOutline`）画出「从菜单栏长出来」的凹角肩部；脱离时肩部收窄成颈部（水滴拉丝），完全符合规格 §3 对「表面张力/颈部」的形态要求。
- 弹簧物理、自有帧时钟、脉冲进度、空闲零重绘、降级三档**全部按规格 §4/§5/§7 实现**，与目标路径共用同一套状态机与参数表——将来若引入 DComp，替换的只是「谁把几何画成像素」，状态机与交互零改动。
- 明确**不引入**第二套 UI 框架、不新增第三方依赖（守住 M6 与 §九红线）。

### 3.2 表面形态：独立浮窗（而非菜单栏中置列）

岛文档 §6 设想在 `MenuBarWindow` 里加中置列。本轮改用**独立无焦点浮窗**，理由：

1. 菜单栏条带只有 20 DIP 高（`MenuBarMetrics.MenuBarHeight`），中置列里的胶囊无法长高、无法悬挂、无法承载展开态卡片——「灵动」的空间前提就不存在。
2. 动效规格 §2 的窗口形态本来就是独立窗口（`WS_EX_NOREDIRECTIONBITMAP` 那套），§6 是渲染决策之前写的。
3. 独立窗口对既有菜单栏**零改动**：不改 root Grid、不改扩展契约、不动 AppBar 协商，回归面最小。

代价与对策：浮窗与菜单栏的接缝必须不可见——岛用与菜单栏同源的令牌与材质（`ThemePanelBackground` + `IAppearanceService.Material`），并按菜单栏实际矩形对齐（`IslandAnchor` 用窗口标题找 `BetterDesktop.MenuBar`，失败回退 20 DIP 常量 + 主屏居中）。

### 3.3 岛窗口的三条硬约束（商用级）

| 约束 | 做法 |
|---|---|
| 不抢焦点 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`（`ShellWindow.UseNoActivateWindowStyle`）；`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE` 兜底 |
| 不挡点击 | 自建 `WM_NCHITTEST`：命中点不在当前岛轮廓（含 2 DIP 外扩）内 → 返回 `HTTRANSPARENT`，点击落到下层 |
| 不覆盖自家 UI | 订阅 `SurfaceScopeBridge` 得不出事件，改**几何互斥**：菜单栏弹层都在条带下方且 `Topmost`，岛在展开态遇弹层不做抢占；本轮由「岛展开靠悬停、鼠标离开即收」天然规避重叠 |

## 4. 设计规格（落地值）

### 4.1 尺寸与几何

| 量 | 值 | 说明 |
|---|---|---|
| 窗口画布 | 560 × 240 DIP，贴主屏顶、水平居中于菜单栏 | 空白区不绘制 → 自动穿透 |
| 附着线 `attachY` | 菜单栏下沿（窗口内坐标，默认 20） | 岛的顶边永远贴在菜单栏下沿 |
| 收起胶囊 | 高 26，宽 = 文本实测宽 + 内边距，夹取 [128, 268] | 圆角取 `min(h/2, 13)` |
| 展开卡片 | 高 = 26 + 行数 × 20 + 12，宽夹取 [240, 380] | 悬停/点击展开，离开 320 ms 后收起 |
| 肩部凹角 `s` | `clamp(9 + gap × 1.4, 9, w × 0.34)` | 附着时 9（像长出来），脱离时变宽 → 颈部变细 |
| 边缘羽化 | 描边 1 DIP + 顶部 1 px 高光渐变 | 水面反光（规格 §6-2） |

### 4.2 动效（弹簧物理，非 Storyboard）

| 状态转移 | ω | ζ | 备注 |
|---|---|---|---|
| 收入（隐藏 → 胶囊） | 22 | 0.72 | 轻微回弹，水滴落下 |
| 收出（胶囊 → 隐藏） | 22 | 1.0 | 临界阻尼，颈部先断裂 |
| 展开 | 18 | 0.78 | 回弹 ≤6% |
| 收起 | 18 | 0.95 | 内容先淡出再形变 |
| 步进呼吸 | 30 | 0.35 | 计数步进时的一次脉冲 |

- 积分器：半隐式欧拉 + 固定子步 1/240 s（`dt` 上限 50 ms，子步上限 40），实现于 `IslandSpring`（可单测）。
- 时钟：唯一 `CompositionTarget.Rendering` 订阅，**两弹簧都静止且无脉冲时自动退订**（空闲零重绘）。
- 三档强度：`full`（全动效）/`lite`（时长 ×0.5，无呼吸/颗粒）/`off`（瞬切）。默认跟随系统 `UISettings.AnimationsEnabled`。

### 4.3 脉冲进度（规格 §5）

- 环形进度（直径 18）：弧长按进度映射；未完成段保留低亮度轨迹。
- 脉冲：亮度/内发光正弦叠加 + 半径 ±1.5% 呼吸，频率与进度解耦（1.5 Hz）。
- 中心百分比：等宽数字（`Typography.NumeralAlignment = Tabular`），只渲染可见形态；不确定进度用旋转扫描 + 双脉冲，**不显示假百分比**。
- 终态：完成 → 一次脉冲爆发 + 绿色；失败 → 警示红 + 单次抖动（不循环闪烁）。

### 4.4 消息源映射

| 来源 | 触发 | 活动形态 |
|---|---|---|
| 剪贴板 | `IClipboardService.HistoryChanged`（`Added`）+ `GetLastCopiedContent()` | `Transient`/Clipboard：标题「已复制文本」，正文为预览（截断 60 字），TTL 3.5 s |
| 媒体 | `IMediaPlaybackService.MediaPlaybackChanged` + 活动期间 1 s 轮询快照 | `Sticky`/Media：标题+歌手，展开态给播放/暂停/上一首/下一首四个动作 |
| 转换 | `convert/progress`、`convert/finished`、`convert/failed`、`convert/batch-finished` | `Progress` 环 + 阶段文案；完成/失败给终态提示；批量结束折叠为一条计数提示 |

### 4.5 抑制规则

`SHQueryUserNotificationState`：`QUNS_BUSY` / `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE` → `SetSuppressed(true)`（进队列不弹）；`QUNS_ACCEPTS_NOTIFICATIONS` / `QUNS_QUIET_TIME` / `QUNS_NOT_PRESENT` → 解除。1 s 轮询，判定为**纯函数**（可单测）。

## 5. 文件落点

| 文件 | 职责 |
|---|---|
| `packages/shell/shell-island/IslandPlugin.cs`（新） | IPlugin 装配：开关、服务取用、源接线、设置分区注册、退出释放 |
| `.../IslandController.cs`（新） | 活动 → 内容模型 → 表面 的胶水；内容轮询（仅进度/媒体在活动时） |
| `.../Windows/IslandWindow.cs`（新） | 岛窗口：无焦点、命中测试、自有帧时钟、内容元素定位、无障碍 |
| `.../Surface/IslandOutline.cs`（新） | 液态轮廓几何（凹角肩部 + 颈部），零分配可变 PathGeometry |
| `.../Surface/IslandMotion.cs`（新） | `IslandSpring` + `IslandMotionController` + `IslandPose` |
| `.../Surface/PulseRing.cs`（新） | 脉冲进度环自绘元素 |
| `.../Sources/ClipboardActivitySource.cs`（新） | 剪贴板 → 活动 |
| `.../Sources/MediaActivitySource.cs`（新） | 媒体 → 活动 + 动作回调 |
| `.../Sources/ConvertActivitySource.cs`（新） | 转换事件 → 活动 |
| `.../Native/IslandNative.cs`（新） | `SHQueryUserNotificationState`、`FindWindow`、`GetWindowRect` 等最小声明 |
| `.../Services/IslandAnchor.cs`（新） | 菜单栏矩形解析（标题查找 → 回退常量） |
| `.../Services/SuppressionPolicy.cs`（新） | 用户通知状态 → 是否抑制（纯函数） |
| `.../Sections/IslandSection.cs`（新） | 设置分区「灵动岛」 |
| `packages/api/Activity/ActivityContracts.cs`（改） | 新增 `ActivityChangedNotice` 载荷 |
| `packages/api/Core/ShellEvents.cs`（改） | 新增 `shell.activity/changed` 事件名 |
| `packages/shell/shell-core/ShellCorePlugin.cs`（改） | Provide `IActivityService` + TTL 心跳 + Changed → IEventBus 桥 |
| `packages/shell/shell-island-tests/`（新） | 弹簧积分、状态机、轮廓几何、抑制纯函数、仲裁接线 |
| `host/Bootstrap.cs`、`host/cordis.yml`、`BetterDesktop.slnx`（改） | 装配 |
| `docs/MECHANISMS.md`（改） | 新增 M23 岛表面（唯一实现声明） |

## 6. 明确不做 / 偏离

1. **不做 DComp/HLSL metaball 自渲染层**：待「是否引入 D3D11/DComp 互操作库」拍板后单独立项；本轮状态机与参数表可直接复用，替换面仅限「绘制」。
2. **不做按序粘贴预览（DoD D2）**：会话队列在面板 exe 进程的 `ClipboardIpcClient` 内（`private SequentialItem`），壳进程客户端看不到；要在岛上显示必须把按序会话状态搬到引擎侧（新增动词 + 推送），属剪贴板引擎改造，另行立项。本计划把该 DoD 标注为「未交付（有前置）」。
3. **不做系统通知 / 截图 / OCR 进岛**：分别依赖 P4（稀疏包 + 签名）与各自文档，保持批次边界。
4. **不做多屏岛**：主屏优先（按索引 §六-3 与岛文档 §10 的既有倾向），位置解析留 per-monitor 接口位置但不实现。
5. **不改菜单栏**：不加中置列、不动 AppBar 协商、不动既有扩展契约。

## 7. DoD

功能 DoD（真机走查，需用户确认）：

- D1：复制一段文本 → 菜单栏中置出现胶囊（从菜单栏下沿「长出来」），显示预览，约 3.5 s 后收出，颈部先细后断；连点复制不抖动（同源合并）。
- D2：按序粘贴预览 —— **未交付**（见 §6-2）。
- D3：播放音乐 → 常驻胶囊显示标题/歌手，悬停展开出播放/暂停/切歌，操作生效；暂停/切歌实时更新；停止后胶囊收出。
- D4：批量转换 → 环形成长 + 中心百分比 + 脉冲；先后给出「完成 · N 项」或失败终态；期间被更高优先级活动抢占后能回来。
- D5/D6/D7：系统通知 / 全屏抑制 / 弹层互斥 —— 通知未交付；全屏抑制本轮实现（DoD 见 T3）；弹层互斥由悬停展开规避。
- D8：动画期间空闲 5 分钟无持续重绘（时钟自动退订，可用日志计数验证）。
- D9：收入收出呈液态形态，肩部有可辨颈部，240 fps 慢放无跳变/黑边/锯齿 —— 待真机录屏。
- D10：接缝不可见（与菜单栏同底色同材质档）；关掉动效档后状态仍正常切换。

机制 DoD（机检，本轮必须绿）：

- T1：`IslandSpringTests` —— 临界阻尼不超调、欠阻尼超调 ≤6%、静止判定、固定子步下的数值稳定性。
- T2：`IslandOutlineTests` —— 附着态轮廓含卡片中心点、脱离态颈部变窄、圆角半径随高度夹取、几何点命中判定（看得见点得到）。
- T3：`SuppressionPolicyTests` —— `SHQueryUserNotificationState` 值 → 抑制布尔（含未知值保守策略）。
- T4：`ActivityBridgeTests` —— `ActivityChangedNotice` 在 Post/Complete/Tick 后的内容与计数。
- 构建：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；`dotnet test` 新增用例全绿。

## 8. 交接节（供技术力应用执行）

| 项 | 填写 |
|---|---|
| 模式判定 | 「无匹配 → 工程代码权威」：技术库 301-305 为 HTML/JS 参考（状态机/形变语义），只取语义不照抄；渲染机制按本项目代码范式（`ShellWindow` 子类 + 自绘 `OnRender`）。 |
| 注入清单 | 本计划；`2026-09-14-dynamic-island.md` §3/§4/§7；`2026-09-14-island-motion-and-rendering.md` §3-§9（口径以 §7 降级矩阵第二档为准，理由见 §3.1）；源码锚点 `ShellWindow.cs`、`PopupWindowBase.cs`、`ActivityService.cs`、`ThemeBrushes.cs`、`QuickNotePlugin.cs`（装配范式）、`MenuBarSection.cs`（设置分区范式）。 |
| 适配参数 | TFM `net8.0-windows10.0.19041.0`、`TreatWarningsAsErrors`、x64；命名空间 `BetterDesktop.Shell.Island`（实现）/`BetterDesktop.Shell.Island.Tests`（测试）；服务经 `context.Get/Provide`；设置经 `ISettingsService`（键前缀 `island.`）。 |
| 禁区 | 不引入第三方依赖；不新增第二套 UI 框架；不用 WPF `Storyboard`（自有帧时钟）；不动 `MenuBarWindow`；不实现 §6 列出的偏离项；不在动画路径上做分配/布局（子元素定位走 Canvas 直接改坐标）。 |
| DoD 核销 | 见 §7；T1-T4 机检必须绿，D1/D3/D4/D8-D10 待用户真机走查（如实标注，不冒充通过）。 |

## 9. 风险与回归面

| 风险 | 对策 |
|---|---|
| 分层窗口下自绘几何闪烁/残影 | 空闲退订时钟 + 只在状态变化时重绘；窗口 `AllowsTransparency`（与既有弹层同路径） |
| 命中测试与可见区不一致 | 自建 `WM_NCHITTEST` 用同一份几何做 `FillContains`，不依赖系统 alpha 命中 |
| 岛与菜单栏 Z 序（同为 Topmost） | 岛显示后 `SetWindowPos(HWND_TOPMOST)` 抬顶；菜单栏弹层本来就在其下方，不冲突 |
| 与既有桌面/菜单栏抢输入 | `WS_EX_NOACTIVATE` + `HTTRANSPARENT` 双保险，交互只在胶囊内 |
| 新包引用面过大 | 只引用 `kernel`/`api`/`shell-core`（设置分区自建卡片，不引 shell-settings）；不引用剪贴板/菜单栏实现包 |

## 10. 落地核销记录（2026-09-16）

### 10.1 机检（已绿）

| 项 | 结果 |
|---|---|
| `dotnet build packages/shell/shell-island` | 0 警告 0 错误 |
| `dotnet build host/BetterDesktop.Host.csproj`（含全部 shell 包） | 0 警告 0 错误 |
| `dotnet test shell-island-tests` | **37/37 绿**（动效/轮廓/抑制/映射/转换来源接线） |
| 门禁 `package-readme` / `md-links` / `architecture-guard` / `md-wrap` / `doc-budgets` / `no-cross-assembly-event` | 全部 PASS（后两项本轮各抓到一处真问题，见 §10.6） |
| `dotnet build BetterDesktop.slnx` | **未绿**：唯一失败是 `shell-status-tests` 的输出目录被一个**无法结束的历史 `testhost` 进程**（PID 50496，非本会话产生）锁定，属环境性 MSB3021/MSB3027，与本次改动无关 |

### 10.2 真机取证（窗口与三条硬约束）

`EnumWindows` + `GetWindowLong` + `GetWindowRect` 实测（屏幕 2582×1440，DPI 125%）：

```
ISLAND : hwnd=0x2815A4 rect=(941,0)-(1641,300) size=700x300 exStyle=0x08080088
         NOACTIVATE=True TOOLWINDOW=True LAYERED=True
MENUBAR: rect=(0,0)-(2582,25) → 中心 1291 = 岛中心 (941+1641)/2 → 水平居中 ✓ 顶边贴屏幕顶 ✓
Z 序  : 岛(第 8 位) 在 菜单栏(第 14 位) 之上 ✓
```

即：不抢焦点（NOACTIVATE）、不进任务栏（TOOLWINDOW）、分层透明、按 DIP 换算尺寸正确、与菜单栏几何对齐全部成立。

### 10.3 真机取证（端到端链路）

宿主日志（`%LocalAppData%\BetterDesktop\logs\host-*.log`）实证剪贴板来源到岛表面的完整链路：

```
20:34:56.249 [Info] shell.island: 剪贴板提示已发布（已复制文本，预览 14 字）
20:34:56.255 [Info] shell.island: 上屏活动 clipboard/island.clipboard（已复制文本）
20:35:00.985 [Info] shell.island: 岛已收出并静止（本次动画累计 127 帧，此后零重绘）
```

最后一行同时是 **D8"空闲零重绘"的证据**：收出动画结束后帧时钟自动退订，此后不再产生帧。

视觉取证（截屏，逐帧人工核对）：

- 收起态：深色胶囊悬于菜单栏下沿、左右肩部与菜单栏融合（"长出来"），左侧单线夹板字形 + 标题文本；
- 展开态（鼠标悬停）：卡片向下生长出第二行预览文本，文字未被裁切、圆角与描边正常。

留证（工作区根目录，`_` 前缀沿用既有取证文件命名约定）：`_island-collapsed.png`（收起态）、`_island-expanded.png`（悬停展开态）。

### 10.4 本轮真机抓出并修复的三个坑（已回写代码注释）

1. **服务挂到死代码上**：`ShellCorePlugin` 在 cordis.yml 迁移后全仓无引用（`grep` 只有自身定义），把 `IActivityService` Provide 在它里面等于没 Provide——真机日志 `shell.island: IActivityService 未入图` 即实证。修复：新建 `packages/shell/shell-core/Activity/ActivityPlugin.cs` 并在 `cordis.yml` + `Bootstrap.Factories` 登记。教训：新增跨包服务必须挂到**实际被加载的**插件上。
2. **IPC 事件线程上发同步请求必然超时**：`HistoryChanged` 在剪贴板客户端自己的工作线程上派发，该客户端是"单线程 drain + 读帧"模型；在回调里再发同步 `get_last` 就是在等自己去读回响应 → 真机 `ipc timeout (5000ms): get_last`。修复：取内容改走 `Task.Run`。
3. **取证时机的陷阱**：`Set-Clipboard` 在引擎占用剪贴板时会阻塞若干秒，按"复制时刻 + 固定延时"截屏会系统性错过窗口期；正确姿势是按日志里"上屏时刻"对齐取证。

### 10.5 DoD 状态

| DoD | 状态 |
|---|---|
| D1 剪贴板短提示（长出、自动收出、连点不堆积） | ✅ 真机取证（日志 + 截图）；连点走"新 Id 顶替旧 Id + 显式 Complete"，不产生队列回锅 |
| D2 按序粘贴预览 | ⛔ **未交付（有前置）**：会话队列活在面板 exe 进程，需引擎侧新增会话状态与推送动词 |
| D3 媒体常驻胶囊与播放控制 | ⚠️ 接线完成、**未真机走查**（需真实播放会话） |
| D4 转换进度环与终态 | ⛔ **当前收不到事件（有前置）**：转换由 `BetterDesktop.Cli` 在 CLI 进程内执行且未带事件总线，而 `IEventBus` 是进程内总线；需"转换改在宿主执行"或"跨进程事件桥"二选一 |
| D5 系统通知 | ⛔ 未接入（依赖 P4 稀疏包 + 签名） |
| D6 全屏/演示抑制 | ⚠️ 判定与轮询已实现、单测绿；**未用真全屏应用走查** |
| D7 弹层互斥 | ⚠️ 由"悬停展开 + 离开即收"规避重叠，未做显式互斥 |
| D8 空闲零重绘 | ✅ 帧时钟自动退订 + 日志实证（127 帧后静止；空闲期无日志、无帧） |
| D9 液体形态慢放检查 | ⚠️ 已实现肩部/颈部，**未做 240 fps 慢放逐帧** |
| D10 接缝不可见 / 关闭动效仍可用 | ⚠️ 接缝由"同令牌同底色"保证（截图观感一致）；`off` 档有单测（瞬切） |
| T1-T4 机检 | ✅ 全绿 |
| D11 存在感（休眠形态） | ✅ **用户反馈追加**：无活动时保留矮胶囊常驻，日志与截图双证（见 §10.7） |

### 10.6 门禁抓到的两处真问题（已修）

1. `verify-no-cross-assembly-event` FAIL：`MediaActivitySource` 订阅了 `IMediaPlaybackService.MediaPlaybackChanged`（event 定义于 api、消费于 shell-island）——ADR-002 D4 禁止的跨程序集裸 event。修复：改为自持 1 s 慢轮询 + 快照签名比对（与实现层原本的 1 s 检测粒度一致，代价等价），门禁转 PASS。
2. `verify-doc-budgets` FAIL：`docs/MECHANISMS.md` 加 M23 后 2156 词超 2000 上限。修复：**不放宽上限**，而是压缩本表自身的冗词（M6/M9/M17/M19-M23 与表头，只删修饰、不复述事实），压到 1998 词后 PASS；同时修正 M22 中"心跳在 `ShellCorePlugin`"的过期描述（实为 `ActivityPlugin`）。
3. `verify-dotnet-format` FAIL：72 处新增违规——① 新建的 `.cs` 是 CRLF，而 `.editorconfig` 要求 `end_of_line = lf`（整个文件每行都算违规）；② 参数列表里夹注释触发 `WHITESPACE` 规则。修复：全部新文件转 LF（`[IO.File]::WriteAllText` + 无 BOM UTF-8），并把两处参数列表内的注释提到 `Post(...)` 之前；`dotnet format <sln> --verify-no-changes` 现为 0 新增违规。**纪律**：本仓新建文件必须 LF，`dotnet format` 门禁只对"新增违规"红，历史长尾在基线内。

### 10.7 二轮反馈：融合 / 隐私 / 按序粘贴会话

三项落地（细节见 shell-island README）：融合=纵向渐变让顶边无描边无接缝；收纳=收出原地收薄；隐私=删"复制即上屏"；按序粘贴=面板经 MenuCmd 上报进度 → 宿主广播 → 岛显示 2/5 与进度环。

### 10.7.1 用户反馈追加：休眠形态（2026-09-16 晚）

**反馈**：用户报告"暂时我没有看到灵动岛的痕迹"。真机核对：宿主在跑、岛窗口存在且 `visible=True`（`rect=(941,0)-(1641,300)`）、几何与 Z 序全部正确、日志有完整的"发布 → 上屏 → 收出"链——问题不在功能，而在**可见性设计**：岛只在有活动时出现 3.5 s，空闲时完全不可见，而菜单栏没有刘海这种天然标记，用户无从判断功能是否存在。

**落地**（新增，不在原 §7 DoD 内）：

1. **休眠形态**：无活动时保留一枚 76×20 DIP 的矮胶囊常驻菜单栏中置。高度 20 是刻意取值——内容层不透明度按"几何高度 20→26"推导，20 恰好让文字与字形不淡入，于是"只画形状不画内容"由几何保证，不需要第二套渲染分支。
2. 休眠态**不参与命中**（无活动时 `WM_NCHITTEST` 一律 `HTTRANSPARENT`）：静止胶囊不挡菜单栏点击。
3. 设置分区新增 `island.idle-visible`（默认开）+ 说明文案；`IslandController.ApplyOptions` 让开关即时生效（不必等下一次活动）。
4. **尺寸弹簧化**：原先宽高只由 `reveal`（显形）与 `expand`（展开）插值直接给出，于是"休眠 → 活动"与"换一条更长/更短的活动"时宽度是瞬间跳变的。现在尺寸是**状态**（`ComputePose` 记录尺寸、`Advance` 按当前 `expand` 插值驱动一对宽高弹簧），`off` 档保持瞬切。
5. 单测新增 `IslandIdlePoseTests` 三条：休眠尺寸有形状但内容不淡入 / 休眠静止后不产帧 / 从活动回到休眠是"收缩"而非跳变（测试总数 37 → **40**）。

**真机取证**（同一实例连续三段，日志 + 截图）：

```
22:20:40.432 岛已收为休眠形态并静止（累计 41 帧，此后零重绘）        ← 启动即进入休眠
22:21:09.767 剪贴板提示已发布（休眠胶囊 + 活动长出来 22:21:09）
22:21:09.775 上屏活动 clipboard/island.clipboard.1（…）
22:21:14.627 岛已收为休眠形态并静止（累计 276 帧，此后零重绘）        ← 收成休眠而不是消失
```

留证（工作区根目录）：`_island-idle-pill.png`（休眠）、`_island-active.png`（活动）、`_island-back-to-idle.png`（收成休眠）、`_island-expanded.png`（悬停展开）。

**命中门控的机检实证**（直接向岛窗口发 `WM_NCHITTEST(0x0084)`，坐标取胶囊中心屏幕点 (1291,35)）：

```
休眠态     → HTTRANSPARENT   （纯装饰：点击放行到菜单栏）
活动态     → HTCLIENT        （可交互：岛接管点击，悬停/点击展开生效）
回到休眠   → HTTRANSPARENT
```

即"看得见但点不到（休眠）/ 看得见就点得到（活动）"这条红线在真机上成立。

**待用户确认的产品取舍**：休眠胶囊默认开（存在感优先）。若觉得常驻碍眼，可在设置里关掉，或后续改为"仅首次启动后一段时间内显示"。
