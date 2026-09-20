# 灵动岛实施计划（2026-09-14）

> 用户需求（2026-09-14 拍板）："灵动岛可以接入程序的所有消息显示，可以作为剪切板按序粘贴的当前 Ctrl+V 的信息预览。可以是音乐播放器，可以是格式转换的进度的显示和完成提示。"
> 状态：M3（活动仲裁）已于 2026-09-15 核销；**岛表面本体与消息源接线已于 2026-09-16 落地**，见 [2026-09-16-island-surface.md](2026-09-16-island-surface.md)（含偏离说明与真机取证；本文 §6 的中置列方案已按该计划 §3.2 改为独立浮窗，§8 实现顺序第 3-5 步已完成，第 6 步与"按序粘贴预览"因前置缺口未交付）。
> 消息源见 [2026-09-14-system-notification-capture.md](2026-09-14-system-notification-capture.md)、[2026-09-14-ocr-recognition.md](2026-09-14-ocr-recognition.md)、[2026-09-14-screen-capture-clipboard-extension.md](2026-09-14-screen-capture-clipboard-extension.md)；总索引见 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)。

## 0. 判位

灵动岛是**视觉表面**，按 [../2026-09-11-resident-architecture.md](../2026-09-11-resident-architecture.md) 的归属原则留在壳体：它显示的所有内容都来自常驻能力（Agent、引擎、其他插件）与壳内视觉插件，壳退出后岛消失是符合语义的。岛本身不产生数据，只做"活动的呈现 + 仲裁"。

## 1. 语言与渲染路径（决策）

**结论：语言 C# / .NET 8，进程与菜单栏同壳；但岛的动画层不走 WPF 视觉树，用"无重定向位图窗口 + DirectComposition 交换链"自渲染。** 完整规格见 [2026-09-14-island-motion-and-rendering.md](2026-09-14-island-motion-and-rendering.md)。

理由按权重排序：

1. **液体动效是硬需求且 WPF 做不到**：收入收出要"像水滴一样丝滑、像水一样融入菜单栏"，这需要 SDF metaball（表面张力颈部）。WPF 只有 `BlurEffect`，**没有阈值化滤镜**，做不出颈部；且本项目窗口普遍是分层窗口（`packages/shell/shell-core/Surface/ShellWindow.cs:79`、`packages/shell/shell-core/Windows/PopupWindowBase.cs:50` 的 `AllowsTransparency = true`），内容走软件合成，逐帧模糊与位图搬运撑不住 120 Hz。
2. **与菜单栏的互动靠几何与令牌对齐，不靠同一条视觉树**：岛占用菜单栏中置区、与菜单栏弹窗互斥让位、共用 `IAppearanceService` 的主题令牌与模糊档；这些都是进程内服务级协作，自渲染层照样能做。
3. **不换 UI 框架**：WinUI 3 与 `Microsoft.UI.Composition` 能力合适，但引入第二套 UI 框架与 Windows App SDK 依赖，与 [../MECHANISMS.md](../MECHANISMS.md) M6"单一 UI 地基"冲突，且为一个小表面换栈不划算。自绘范围限定在岛一个表面，颜色与字体令牌仍取自既有外观服务。
4. **交互复杂的内容回落 WPF**：展开态需要滚动列表/按钮时，下挂既有 `MenuBarPopupWindow` 族弹层承载，只有形变与胶囊本体的绘制留在自渲染层。

因此语言选型的答案是"仍是 C#"，变化的是**渲染路径**；性能由渲染层自有时钟、弹簧积分与帧预算门禁保证。

## 2. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 菜单栏布局 | `packages/shell/shell-menu-bar/Windows/MenuBarWindow.cs:96-120` | 代码构造的 root `Grid`，**只有两列**：列 0 = `*`（左区 `MenuBarLeftZone`），列 1 = `Auto`（右区 `StackPanel`）。中置区需新增列 |
| 菜单栏扩展契约 | `packages/api/Core/IMenuBarExtension.cs:17` | `GetVisual()` 明确是**右区按钮**；`OpenPopup(anchor)` 是弹窗。岛不是右区按钮，因此**不走这个扩展点** |
| 动画服务 | `packages/shell/shell-core/Animation/AnimationService.cs:11`、`packages/api` 的 `IAnimationService` | 现有面是窗口级：`CreateWindowOpenAnimation` / `Close` / `Minimize` / `Bounce` / `FadeIn|Out` / `SlideIn|Out`。**没有"几何形变"原语**，岛的展开/收起需要新增 |
| 剪贴板事件 | `packages/shell/shell-clipboard-ipc/ClipboardChangedInfo.cs:6` | 轻量复制摘要（`textPreview` / `thumbPath` / `sourceApp` / `copiedAt`），引擎侧截断，供只读订阅方 |
| 按序粘贴状态机 | `packages/shell/shell-clipboard-ipc/ClipboardIpcClient.cs:903,956,872,883` | `BeginSequentialPaste` / `PasteNextSequential` / `IsSequentialPasteActive` / `SequentialRemaining`——**全是 pull**，队列在客户端私有列表里，岛要的"下一条预览"需要新增推送 |
| 媒体源 | `packages/shell/shell-status/StatusPlugin.cs:69` | SMTC 服务按"订阅+快照、无订阅者零开销"提供，注释已写明供菜单栏/灵动岛消费 |
| 转换进度源 | `packages/shell/shell-convert/Services/ConversionService.cs:332` | `convert/progress`、`finished`、`failed`、`batch-finished` 事件，注释写明"供给通知中心与灵动岛" |
| 面板状态条先例 | `packages/shell/shell-clipboard-panel/PanelMainWindow.cs`（`BuildSequentialBar`） | 按序粘贴的会话状态已有一个 UI 呈现，岛与其共用同一数据源 |

## 3. 活动模型（新契约）

新增 `packages/api/Activity/IActivityService.cs`：

```csharp
public enum ActivityPriority { Background = 0, Progress = 1, Notice = 2, Clipboard = 3, Media = 4, Attention = 5 }

public enum ActivityKind { Transient, Sticky, Progress }

public sealed record ActivityAction(string Id, string Label, Func<Task>? Invoke);

public sealed record ActivityItem(
    string Id, string Source, ActivityKind Kind, ActivityPriority Priority,
    string Title, string? Body, string? IconPath, double? Progress,
    DateTimeOffset CreatedAt, TimeSpan TimeToLive, IReadOnlyList<ActivityAction> Actions);

public interface IActivityService
{
    void Post(ActivityItem item);      // 同 Id 再次 Post = 更新（进度用）
    void Complete(string id, string? resultText = null);
    void Dismiss(string id);
    ActivityItem? Current { get; }     // 当前占据胶囊的活动（仲裁结果）
    IReadOnlyList<ActivityItem> Queue { get; }
    event Action? Changed;
}
```

规则：全量订阅者为零时零开销（沿用 `IMediaPlaybackService` 范式）；`Changed` 只在"当前活动"或队列顺序变化时触发，进度更新合并到帧级节流。

### 仲裁（必须显式，否则多源互抢）

| 规则 | 内容 |
|---|---|
| 优先级 | `Attention > Media > Clipboard > Notice > Progress > Background`；同优先级按 `CreatedAt` 后到先显示 |
| 抢占 | 高优先级可抢占低优先级，被抢占项回到队列（不丢失），优先级回落后续播 |
| 粘性 | `Sticky`（如正在播放）常驻直到显式 Complete/Dismiss；`Transient` 到 `TimeToLive` 自动收起 |
| 合并 | 同 `Source` 的连续事件折叠为一条并累计计数（如"5 项转换完成"），避免抖动 |
| 抑制 | 全屏应用/游戏/DND 开启时**不弹出**（进队列，退出后按优先级补播） |

## 4. 消息源映射（"程序的所有消息"）

| 来源 | 事件 | 岛的呈现 | 状态 |
|---|---|---|---|
| 剪贴板捕获 | `clipboard_changed` | 短提示：类型 + 摘要（≤200 字）/缩略图 | 已有事件，只需接线 |
| **按序粘贴** | **新增推送 + Peek**（见 §5） | 常驻提示："接下来 Ctrl+V 粘：第 3/12 项 · 内容预览" | 需补推送面 |
| 媒体播放 | `IMediaPlaybackService` | 播放中胶囊：标题/歌手/封面/进度，可播放暂停下一首 | 已有服务 |
| 格式转换 | `convert/progress` 等 | 进度环 + 阶段 + 完成/失败提示 | 已有事件 |
| 系统通知 | `INotificationFeedService.Arrived` | 应用名 + 标题 + 正文摘要，可清除 | 见系统通知文档 |
| 截图 / OCR | capture、`IOcrService` | 截图完成缩略图；OCR 完成"已识别 N 字 · 点击查看" | 见各自文档 |
| 应用源/新装应用 | `IAppSourceService.AppSourceChanged` | 新装应用提示（与现有弹窗二选一，避免双提示） | 已有 |
| 更新/健康 | 更新器、Agent 健康、看门狗 | 更新进度、组件异常提示 | 部分待实现 |
| AI 控制面 | `docs/ai-control.md` 的能力调用 | 任务进行中/结果确认（人在环） | 见该文档 |

## 5. 需要补的两个小面

### 5.1 按序粘贴的推送与预览（`shell-clipboard-ipc`）

```csharp
public sealed record SequentialPasteState(
    bool Active, int Total, int Remaining, string? NextPreviewText,
    string? NextImagePath, string? NextKind);

event Action<SequentialPasteState>? SequentialPasteChanged;   // 会话开始/每步/结束
SequentialPasteState? GetSequentialState();                   // 同步快照
```

队列本身已有 `SequentialItem(entry, payload)`（自包含快照，含 `ImagePath`），因此预览不需要回引擎取内容，条目被删也不影响显示。

### 5.2 形变动效（自渲染层，不是 WPF Storyboard）

形变由岛的渲染层按 [2026-09-14-island-motion-and-rendering.md](2026-09-14-island-motion-and-rendering.md) §3-§4 实现：SDF metaball 描形态，弹簧积分驱动状态转移，自有时钟逐帧提交。**不使用 WPF `Storyboard`**——在分层窗口下它既无法驱动着色器参数，又会把动画拉回软件路径。

现有 `packages/shell/shell-core/Animation/AnimationService.cs` 继续服务其他 WPF 窗口（开关/淡入淡出/滑动/弹跳），两套动效各自归位、互不迁移。

## 6. 接入点（菜单栏中置）

`MenuBarWindow` 的 root `Grid` 当前两列（`:102-103`）。改法：列 0 = `Auto`（左区）、列 1 = `*`（**中置区**）、列 2 = `Auto`（右区）；中置区放一个宿主容器，高度与菜单栏一致，内容由岛的表面元素提供。

反向依赖的处理：`shell-menu-bar` 不引用 `shell-island`。由 `shell-island` 向内核注册一个可选服务（`IIslandSurface`，api 层，`FrameworkElement? GetCenterVisual()`），`MenuBarWindow` 构造时尝试取用，未注册则中置区留空。这样菜单栏对岛零依赖，岛可独立启停。

岛展开后超出菜单栏高度的部分，用菜单栏同款的弹层基座（`MenuBarPopupWindow` 族）承载，保证失焦收起、主题令牌、DPI 行为一致。

## 7. 硬约束（商用级不可妥协项）

| 约束 | 做法 |
|---|---|
| 不抢焦点 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`；不进任务栏、不进 Alt+Tab |
| 不挡点击 | 仅在可见胶囊区域命中测试（不可见区域 `HTTRANSPARENT`）；展开卡片范围内正常命中 |
| 全屏/游戏抑制 | `SHQueryUserNotificationState` 判定（`QUNS_BUSY` / `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE`）→ 不弹出、进队列 |
| 不覆盖自家 UI | 与菜单栏弹窗互斥：弹窗展开期间岛收起为胶囊；与 Dock 预览、启动台同理 |
| 帧预算 | 动画期间 p95 ≤8 ms/帧（120Hz）；空闲零重绘（渲染层不提交新帧）——测量口径与门禁见 [2026-09-14-island-motion-and-rendering.md](2026-09-14-island-motion-and-rendering.md) §8 |
| 动效强度 | 三档（完整 / 精简 / 关闭），默认尊重系统 `UISettings.AnimationsEnabled`；远程桌面自动降档 |
| 无障碍 | 胶囊状态可被屏幕阅读器读出（`AutomationProperties.Name` 同步当前活动标题） |

## 8. 实现顺序

1. `IActivityService` + 仲裁纯逻辑 + 单测（不涉及 UI）。
2. 自渲染层骨架：无重定向位图窗口 + DComp 交换链 + D2D 静态胶囊（见动效规格 §10 第 1-2 步），先验证透明与命中测试。
3. `MenuBarWindow` 中置列 + `IIslandSurface` 挂载点，接入自渲染表面。
4. 液体动效与弹簧状态机 + 脉冲进度 + 帧预算实测（动效规格 §3-§8）。
5. 接线三个既有源（剪贴板捕获、媒体、转换进度）＋ 按序粘贴推送与预览。
6. 系统通知、截图、OCR 接入（依赖各自文档）。
7. 抑制与让位规则（全屏、DND、菜单栏弹窗互斥）+ 动效三档 + 多屏策略。
8. 设置项（启停、位置微调、各来源开关、动效强度）+ 真机走查。

## 9. DoD

功能 DoD（真机）：

- D1：复制文本 → 岛出现短提示并自动收起；连点复制不抖动（合并生效）。
- D2：开始按序粘贴 → 岛常驻显示"第 N/M 项 + 内容预览"；每按一次 Ctrl+V 计数与预览同步；取消后立即消失。
- D3：播放音乐 → 常驻胶囊显示标题/进度，按钮可控制；暂停/切歌实时更新。
- D4：批量转换 → 进度环推进，完成/失败给出终态提示；期间被高优先级活动抢占后能回来。
- D5：新系统通知到达 → 岛提示且可清除，与系统通知中心状态一致（未授权时给可读提示）。
- D6：进入全屏游戏/演示模式 → 不弹；退出后按优先级补播。
- D7：菜单栏弹窗展开时岛收起为胶囊，不出现双层重叠。
- D8：动画期间帧时间 p95 ≤8 ms（真机实测留证），空闲 5 分钟无持续重绘。
- D9：收入收出呈液体形态——与菜单栏接触处有可辨的"颈部"，收出末段有吸附感；240 fps 慢放逐帧检查无跳变、黑边、白边或锯齿。
- D10：与菜单栏的接缝不可见（同底色与同模糊档），展开态脉冲进度与中心百分比清晰可读；关闭动画档后状态仍可正常切换。

机制 DoD（机检）：

- T1：仲裁纯函数（优先级、抢占、同源合并、TTL 过期、粘性不淘汰）。
- T2：`SequentialPasteState` 推送序列（开始/每步/结束/取消）与预览取值的边界（图片项、超长文本截断、条目被删）。
- T3：抑制判定映射（`SHQueryUserNotificationState` 值 → 是否显示）纯函数。
- T4：帧预算门禁（动画基准：形变 60 帧的帧时间中位数与 p95，纳入基准工程与 `scripts/run-gates.ps1`）。

构建门禁：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；剪贴板 IPC 与新增服务测试全绿。

## 10. 开放问题

1. 多显示器：主屏优先还是每屏一个岛（后者需要 per-monitor 表面与位置记忆）。当前倾向主屏优先，留接口。
2. 岛与菜单栏中置区的宽度分配：左区内容较长时是否会挤压岛；需要一条最小宽度与"左区优先"的降级规则。
3. 是否提供"仅胶囊可点击、展开区不接收输入"的只读模式（游戏/演示时）。
4. 活动队列的历史上限与"已忽略活动"是否需要持久化。

## 11. 交接节

**注入文档**：本计划；[2026-09-14-island-motion-and-rendering.md](2026-09-14-island-motion-and-rendering.md)（渲染与动效规格，§1 以此为准）；[2026-09-14-system-notification-capture.md](2026-09-14-system-notification-capture.md)；[../2026-09-11-resident-architecture.md](../2026-09-11-resident-architecture.md)（归属原则）；`packages/shell/shell-clipboard-panel/README.md`（按序粘贴交互先例）。

**模式判定**：新视觉包 + 一个 api 契约 + 两处小改动（剪贴板 IPC 推送面、shell-core 动画原语、菜单栏中置列）。

**强制 scope 边界**：只做"活动接入 + 仲裁 + 中置表面 + 形变动画 + 既有源接线 + 抑制规则"；不做第二套通知系统、不做独立悬浮窗方案、不接管菜单栏既有右区扩展、不做多屏二期。
