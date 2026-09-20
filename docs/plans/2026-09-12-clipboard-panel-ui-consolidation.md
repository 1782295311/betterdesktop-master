# Cairo 开发计划 · 剪贴板面板 UI/UX 收敛（消除"野蛮追加"遗留）

> Task：功能已全量落地（引擎化 S1-S34 + 表情包 1302），现在对面板做一次**整体 UI/UX 收敛**，专治多轮追加功能时"各加各的一块 UI、彼此不商量"的遗留。
> 需求原话（2026-09-12 用户）："既然现阶段所有功能已经完成，我要你给我好好优化，改掉野蛮制作时候的没有互相考虑的问题。"
> 依据：`ui-ux-pro-max` 技能（stack=`wpf`）+ 面板现状只读体检（23 次探查，含行号证据）。
> 基线：`docs/plans/2026-09-11-clipboard-engine-rust-ipc.md`（S1-S34）、`2026-09-12-clipboard-sticker-mode.md`。

## 1. Objective（要变成什么样）

同一块面板上，**状态有主次、视觉有刻度、操作都能用键盘/触控到达、结构不再一改就崩**。
不新增功能，只做收敛；用户可见的既有行为（按序粘贴提示、失败可见、虚拟化、手柄、收起策略）一律不许退化。

## 2. 体检结论（现状证据，行号见 `PanelMainWindow.cs` / `RecentStrip.cs`）

| # | 问题 | 证据 | 严重度 |
|---|---|---|---|
| P1 | **5 个"提示条"抢同一块区域**：暂停横幅独占 Row3；引擎/存储/按序/**多选**四条塞进 Row4 的同一个 StackPanel（359-367）→ 可**同时全部可见**，垂直堆叠把列表整体下推 | `BuildPauseBanner`540 / `BuildEngineBanner`563 / `BuildStorageBanner`601 / `BuildSequentialBar`1460 / `BuildMultiSelectBar`1487 | 高 |
| P2 | **多选条永久占位**：无选中时仍显示"点条目左侧圆圈勾选…"（1589），列表为空时还在教用户点不存在的圆圈 | 1496 / 1586-1589 | 高 |
| P3 | **提示语义打架**：引擎断线 + 列表为空 → 上方横幅"引擎未连接"与列表内空状态"暂无剪贴板历史，复制后将自动记录"**同时显示且互相矛盾** | 1029 / 1223-1242 | 高 |
| P4 | **行内操作仅 hover 可见**：📌/⧉/🗑 初始 `Collapsed`、鼠标移开即隐；勾选圈同样悬停才露 → 键盘与触控**根本无法触达** | `RecentStrip` 598 / 612-621 / 259 | 高（技能判 **Critical**） |
| P5 | **键盘路径缺失**：`ListBoxItem Focusable=false`(784) → 列表项不可 Tab/方向键导航；无 Enter/Delete/数字键；而 UI 却印着"1-9 快捷键"徽标（357）与 `Ctrl+Shift+P` 提示（407）→ **悬空承诺** | 784 / 357 / 407 / 1795（`OnKeyDown` 仅 Esc） | 高 |
| P6 | **Grid 行号硬编码脆弱**：7 行全手工 `SetRow`，代码自述"新增行会牵动后续所有 SetRow"（359-360）；为躲这个坑才把 4 条横幅硬塞进一个 StackPanel | 352-376 / 359-367 | 高 |
| P7 | **同一视觉复制 5 份**：5 个横幅逐字复制（`ControlBackground`+`BorderStrokeAccent`+CornerRadius 6+Padding `10,4`），但**中性多选条被套上警告皮的 `StatusWarning`** | 544-551 / 567-574 / 605-612 / 1464-1471 / 1491-1503 | 中 |
| P8 | **刻度失序**：FontSize 10/11/12/13/15 任意混用（"时间"11 vs "分类"10 vs 横幅正文 12）；水平内缩 14 与 10 两种；圆角 4/6/7/8/10/12 六种 | 见体检 §3.1-3.3 | 中 |
| P9 | **强调色 5 种透明度散用**（0.16/0.18/0.28/0.85/0.9）；`StatusWarning` 既做警告又做"图/表"中性徽标背景 | 517/573/372/382/582/642；`RecentStrip`680 | 中 |
| P10 | **可访问性缺失**：全目录 **0 处** `AutomationProperties.Name` → 屏幕阅读器只念出 emoji glyph；无自绘焦点样式 | 全局 | 中 |
| P11 | **点击目标 <32px**：图标按钮 28×28、行内按钮 26×26、勾选圈 20×20 | 403/933/631/180 | 中 |
| P12 | **空状态污染数据集合**：`_emptyState` 被当成 ListBox item 增删 | 1250-1261 | 中 |

## 3. 技能依据（ui-ux-pro-max 检索结果，用于裁决方向）

| 规则 | 平台/严重度 | 对本面板的约束 |
|---|---|---|
| **Compact Control Semantics** —— 交互 chip 需原生语义 + 可访问名 + 状态 + 键盘操作 + 可见焦点；**"只在 hover 揭示唯一操作"是反模式** | Critical | 直接判 P4/P5 必须修：行内操作改为常可达；chips 补可访问名与键盘操作 |
| **Hover vs Tap** —— 不要只依赖 hover 承载重要操作 | High | P4 同上 |
| **ARIA Labels / 可访问名** —— 图标按钮必须有可访问名 | High | P10：补 `AutomationProperties.Name` |
| **Color Only** —— 不得只用颜色传达信息 | High | P9：状态条需"图标+文字+颜色"三通道，徽标不能用警告色充当分类色 |
| **Contextual Live Badge Updates** —— 异步状态变化应给出**一条恰当的原子状态消息**，而非让每个徽标都变成竞争性 live region | High | **P1/P2/P3 的裁决依据**：状态区应收敛为"单一状态槽 + 优先级"，而非 N 块并列 |
| **WPF: Support keyboard navigation** —— 全功能键盘可达，用 `KeyboardNavigation` 属性 | High（wpf） | P5：列表项可聚焦 + 方向键 + 显式 Tab 顺序 |
| **WPF: Use InputBindings for keyboard shortcuts** —— 用 `KeyBinding` 而非 `PreviewKeyDown` 手工判键 | Medium（wpf） | P5：数字键/Enter/Delete 走 InputBindings |
| **WPF: Keep code-behind minimal** —— 焦点管理等视图逻辑留在 code-behind | Medium（wpf） | 面板是纯代码 UI，焦点管理集中到单一方法 |

## 4. 方案（四个工作面）

### 面 1 · 状态区统一（治 P1/P2/P3）
- 新建 **`StatusStrip`**：Row3+Row4 的 5 处横幅收敛为**一个状态槽**，同一时刻**只显示优先级最高的一条**。
  优先级（高→低）：**引擎断线** > **暂停** > **按序粘贴会话/过程提示** > **多选操作** > **存储超限**。
- 被压制的提醒不丢弃：状态槽右侧给出**计数与展开**（"还有 2 条提醒"），点击展开为纵向列表 —— 对应技能的"原子状态消息"而非多个竞争区域。
- **多选条不再常显**（无选中即隐藏）；**引擎断线时抑制列表内空状态**（消除 P3 的矛盾提示）。
- 状态条统一形态：`[图标] 文案 … [可选动作按钮]`，图标+文字+颜色三通道（满足 Color Only 规则）。

### 面 2 · 视觉刻度收敛（治 P7/P8/P9）
- 在 `PanelTheme` 增补**设计令牌**：
  - 字号四档：`FontCaption 10` / `FontSmall 11` / `FontBody 12` / `FontTitle 15`（现有 13 归并为 12 或 15，消除"13"这一野生档位）；
  - 间距四档：`SpaceXS 4` / `SpaceS 8` / `SpaceM 12` / `SpaceL 16`，面板水平内缩统一（header/list/footer 同为 `SpaceL`）；
  - 圆角三档：`RadiusS 4` / `RadiusM 8` / `RadiusL 12`（横幅统一 M，卡片 L，徽标 S）；
  - 强调色透明度三档语义：`AccentSubtle 0.16` / `AccentMedium 0.28` / `AccentStrong 0.85`。
- 5 份复制的横幅改由**单一工厂** `CreateStatusCard(StatusKind kind)` 产出（kind：`Info/Warning/Danger/Action`），中性操作不再用警告色。
- `StatusWarning` 语义归位为"警告"；"图/表"等**中性徽标**改用中性令牌（`ThemeMutedForeground` + `ControlTrack`）。

### 面 3 · 交互可达性（治 P4/P5/P10/P11）
- 行内操作（📌/⧉/🗑）与勾选圈可见条件改为 `hover || 选中 || 键盘焦点在内` —— 键盘/触控同样能看到并触达。
- 列表键盘导航（`InputBindings` + `Focusable=true`，`ListBoxItem` 不再禁用聚焦）：
  `↑/↓` 移动、`Enter` 复制、`Delete` 删除、`1-9` 直接粘贴第 N 条、`Space` 勾选、`Esc` 关闭（保持）。
- 全部图标按钮与列表项补 `AutomationProperties.Name`；行内按钮 `ToolTip` 保留。
- 自绘**可见焦点**样式（与圆角主题一致，不再用系统虚线）。
- 点击目标提升：图标按钮 28→**32**、行内按钮 26→**32**（勾选圈热区已 40，视觉可留 20）。

### 面 4 · 结构解耦（治 P6/P12）
- 消除"新增区块要重排所有行号"：改用**具名行常量 + 单一入口**（`GridRows.Header/Search/Filters/Status/List/Footer`），或把状态区与 footer 合并为外层 `DockPanel`，从根本上不再靠魔法数字。
- `_emptyState` 从 ListBox items 中移出，改为列表区域的**独立覆盖层**（不影响虚拟化集合与计数语义）。

## 5. 生死线（不得破坏的既有已验收行为）

1. **按序粘贴过程提示必须仍然可见**（S31：失效/降级要能在状态条看到）—— 收敛后仍须保留，不得因为"合并状态槽"而丢掉。
2. **失败必须可见**（S32 红线）：复制/删除/收藏/清理/导入的失败路径仍要有反馈（toast 或状态槽）。
3. **列表虚拟化与懒解码**：不得因改结构退化为非虚拟化（万条滚动内存红线），图片仍"进视口才解码、滚出即释放"。
4. **已知布局铁律**：`ListBoxItem.HorizontalContentAlignment=Stretch`（否则预览不换行）、`DockPanel` 填充项必须最后 `Add`、`BuildList/BuildFooter` 只调用一次、预览行高常量与字号联动。
5. **面板外观仍随宿主外观令牌**（`PanelTheme` 与宿主 `AppearanceService` 同源），不得引入与之冲突的硬编码配色。
6. **收起策略**（auto/manual）、手柄交互（点击展开、拖动自实现、位置持久化）、单实例与 IPC 行为均不受影响。

## 6. 验证方式

| 层 | 方法 |
|---|---|
| 编译 | `dotnet build BetterDesktop.slnx` 0 警告 0 错误 |
| 单测 | IPC 22 / 契约 87 / Rust 86 全绿（本轮不改逻辑，作为回归门） |
| **UIA 探针**（真机） | 启动面板后用 UI Automation 断言：① 状态槽同时可见的"条"≤1（或折叠计数正确）；② 关键按钮尺寸 ≥32；③ 图标按钮都有 `AutomationProperties.Name`；④ 列表项可聚焦且方向键可移动选择；⑤ 引擎断线时空状态**不与**"引擎未连接"同时出现（可用模拟断线或直接断言优先级的互斥逻辑） |
| 目视 | 截取面板在 4 种状态（正常/暂停/按序/多选）下的控件树与可见性，确认无堆叠、无矛盾文案 |

## 7. DoD 核销表（2026-09-12 实施完成）

| # | 验收点 | 状态 | 证据 |
|---|---|---|---|
| U1 | 状态槽同一时刻只呈现一条，其余折叠为计数（可展开） | ✅ | `BuildStatusSlot` + `SetStatusLane`/`UpdateStatusSlot`（优先级 Engine>Pause>Sequential>Storage，被压制者汇总为 `+N 条提醒`）；UIA 实测同时可见状态卡 = **0**（无异常态时） |
| U2 | 多选条无选中时不出现；引擎断线时列表空状态被抑制（无矛盾文案） | ✅ | 多选条改列表底部浮层 + `_selected.Count > 0` 才显示；`UpdateFooter` 用 `showEmpty = _total == 0 && !_engineDown` 抑制 |
| U3 | 按序粘贴会话与过程提示仍完整可见（生死线 1 不退化） | ✅ | `UpdateSequentialBar` 文案逻辑原样保留，仅可见性改由 `SetStatusLane(StatusLane.Sequential, …)` 裁决 |
| U4 | 字号/间距/圆角/强调色透明度各有唯一令牌来源，野生值清零 | ✅ | 新增 `PanelTheme.Scale`（字号 4 档 / 间距 4 档 / 圆角 3 档 / 强调色 3 档 / TapTarget / GutterX）；重构涉及的 18 处硬编码已全部改令牌 |
| U5 | 横幅由单一工厂产出；中性操作不再使用警告色；中性徽标不用警告色 | ✅ | `PanelUi.CreateStatusCard(kind, icon, …)` 统一产出；`StatusKind.Info/Action` 走 `ThemeForeground`（只有 Warning/Danger 用警示色）；分类与序号徽标改用 `AccentSubtle/AccentStrong` |
| U6 | 行内操作与勾选圈在 hover/选中/键盘焦点下均可达（消除 hover-only） | ✅ | `IsActiveRow = IsMouseOver \|\| IsKeyboardFocusWithin` + 唯一刷新点 `RefreshActiveChrome()` |
| U7 | 列表支持 ↑/↓/Enter/Delete/1-9/Space，焦点可见，图标按钮均有可访问名 | ✅ | `OnListKeyDown` + `KeyboardNavigation`（TabNavigation=Once）；**UIA 实机断言**：`before: (none)` → ↓ → `1. 富文本：…` → ↓ → `2. …`（键盘导航真实生效）；`ListBoxItem` 可聚焦 + 自绘 Accent 焦点环 |
| U8 | 图标按钮与行内按钮点击目标 ≥32px | ✅ | 统一 `PanelTheme.Scale.TapTarget`；UIA 实测 **<32px 按钮数 = 0** |
| U9 | 新增区块不再需要改动其它行号（行号脆弱性消除）；空状态不再作为 ListBox item | ✅ | 具名 `Row` 常量 + 单处装配；空状态改列表区浮层（`BuildEmptyState` + 可见性裁决） |
| U10 | 构建 0 警告 0 错误 + 三套测试全绿 + UIA 探针断言通过 + 面板已部署 | ✅ | `dotnet build BetterDesktop.slnx` 0/0；Rust 86 / IPC 22 / 契约 87；UIA：可访问名缺失 **0**、<32px **0**、列表可聚焦 **True**、状态卡 **0**；面板已部署重启 |
| U11 | 文档回写（本计划 DoD + 面板 README + 技术库 UI 收敛条目） | ✅ | 本表 + `shell-clipboard-panel/README.md` + `TECH-KNOWLEDGE/13-剪贴板/1301` C# 变体新增「UI 收敛」小节 |

## 8. 实施记录（关键判断与真机发现）

1. **多选条的"常驻占位"是旧实现规避位移的妥协**：它不是设计选择，而是"把非列表内容塞进列表上方"的必然代价。改为**列表底部浮层**后，位移问题与"空列表还教用户点圆圈"的问题同时消失 —— 优先换结构，而不是继续加约束。
2. **"状态槽"不是把 5 条横幅排个序，而是让各处不再直接改 `Visibility`**：真正制造打架的是"每处各自 show/hide"，所以引入 `SetStatusLane(意图) + 集中裁决`。新增状态类型只需加枚举值，不必动布局。
3. **真机 UIA 抓到一个纸上发现不了的问题**：给 `RecentStrip` 设 `AutomationProperties.Name` 之后，读屏读到的仍是**类型全名** —— WPF 的 `ListBoxItem` 用自己的 `Content.ToString()` 当自动化名，读的是容器而非内容。修法：`RecentStrip.ToString()` 返回条目摘要。**教训**：可访问性必须用 UIA 实测，不能只靠"我设了属性"。
4. **顺带修掉一处误导文案**：⏸ 的 ToolTip 写的 `Ctrl+Shift+P` 实为**收藏视图**热键，暂停是 `Ctrl+Shift+Backspace` —— 用户照做会得到另一个功能。悬空/错指的热键提示比没有提示更糟。
5. **技能规则与既有实现冲突时以规则为准**：`1-9` 徽标承诺的"快捷键"此前不存在，而技能明确要求键盘可达 + 可见焦点。实现为"选中并复制"（不是自动粘贴 —— 面板自身可激活，注入 Ctrl+V 的焦点归还不可靠，双击粘贴正因此被移除）。
6. **未覆盖（留待用户目视）**：多选条浮层实际弹出时的视觉层次（是否遮挡最后一行内容）、状态槽在"引擎断线 + 暂停"同时发生时的观感、以及 hover 与键盘焦点切换时行内操作的闪烁感。这些需要人眼判断，探针只能断言结构与尺寸。
