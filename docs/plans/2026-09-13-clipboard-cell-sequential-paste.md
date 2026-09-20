# 剪贴板 · 表格「按格粘」（单元格级按序粘贴）实施计划（2026-09-13）

> 用户需求（2026-09-13 拍板）：
> "Excel 表格粘贴不仅带格式，还可以选择**按序粘贴**出来，用来应对表格数据填到业务系统中。"
> 决策：**逐单元格**粘 / **自动跳格由用户选**（不写死——"表格内容的形式多种多样，我们固定的形式无法应对"）/
> **条目专用按钮**（行内「按格粘」）/ **现在就做**。

## 0. 与既有能力的关系（判位）

| 场景 | 能力 | 状态 |
|---|---|---|
| Excel → Excel（保住表格结构） | 命名格式透传（P1-4） | 已做（`docs/plans/2026-09-13-clipboard-tiez-p1p2.md`） |
| **Excel → 业务系统表单（逐格填）** | **本计划：单元格级按序粘贴** | 新增 |

两者**互补不冲突**：前者解决"回贴表格"，后者解决"拆开喂表单"。

## 1. 技术力检索（Phase 1/2）

| 关键词 | 命中 | 处置 |
|---|---|---|
| 按序粘贴 / 顺序粘贴 / 队列 | `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（P2-4 节 + `SequentialItem` 快照自包含） | **命中**：复用既有按序粘贴状态机（内容快照自包含 + 每次 Ctrl+V 粘下一条 + 面板状态条 + Esc/取消） |
| 表格拆分 / Tab 分隔 / 单元格 | 无 | **无现成技术文档，新建**（关键词：Tab 分列、CRLF 分行、尾随空清理） |
| 键盘注入 Tab/Enter | `1301` 的按键注入节（`keybd_event` 三档注入） | 复用既有注入范式（`SendCtrlV`/`SendShiftInsert` 同族，新增 `SendTab`/`SendEnter`） |

**重要结论：本功能不依赖 Excel 私有格式。** Excel 复制区域时剪贴板文本本身就是 `\t` 分列、`\r\n` 分行，
该结构已完整落在 `ClipboardEntry.Content` 里 → **引擎无需改动**。

## 2. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 队列状态机 | `shell-clipboard-ipc/ClipboardIpcClient.cs:903` `BeginSequentialPaste` | 逐条同步抓内容快照（`CaptureSnapshot`，2.5s 总预算 + 摘要兜底），存 `SequentialItem(entry, payload)` |
| 单步粘贴 | 同文件 `:956` `PasteNextSequential` | ① 优先 `CopyEntryToClipboard(item.Entry)`（需引擎侧条目存活）→ ② 失败回退 `WriteSnapshotToClipboard(item.Payload)` 自包含写回 → ③ 明确失败 |
| 载荷 | 同文件 `:1703` `SequentialClipboardPayload` | 自包含 `Kind/Text/Html/Rtf/ImagePath/FilePaths` → **纯文本场景够用** |
| 状态查询/取消 | 同文件 `:872` `IsSequentialPasteActive` / `:883` `SequentialRemaining` / `:997` `CancelSequentialPaste` | 面板状态条与取消直接复用 |
| 全文抓取 | 同文件 `:790` `FetchEntryPlainText` | 已处理 `>100KB` 落 bin（`get_content` 回退） |
| 按键注入 | 同文件 `:1504` `SendCtrlV` / `:1517` `SendShiftInsert` | `keybd_event` 范式；单测经 `PasteInjectorHook` 缝替换 |
| 面板多选入口 | `shell-clipboard-panel/PanelMainWindow.cs:2457` `StartSequentialPaste` | 多选 → `BeginSequentialPaste` → `HidePopup()` → 状态条 |
| 面板会话钩子 | 同文件 `OnPasteHotkeyHook`（Ctrl+V 重定向为"粘下一条"）+ `BuildSequentialBar` | 按格粘**完全复用** |
| 行内操作区 | `shell-clipboard-panel/RecentStrip.cs:681` `BuildRightColumn` | 4 个按钮（🎴/📌/⧉/🗑），`CreateRowButton(glyph, tooltip, handler)` 可加第 5 个 |
| 设置分区 | `shell-clipboard/Sections/ClipboardSection.cs` | 卡片 + `ChoiceRow` 工厂；面板直读扁平键的先例 = `paste-back-hotkey` |

## 3. 设计

### 3.1 表格判定与拆分（单一真相源）

新增 `shell-clipboard-ipc/ClipboardTableCells.cs`（`public static`）：

```csharp
public static bool IsTabular(ClipboardEntry entry, out int rows, out int cols, out int count);
public static IReadOnlyList<string> Split(string text);   // 行优先，逐格
```

- **判定**：`ContentType ∈ {Text, Html, RichText}` 且 `Content` 含 `\t` 且存在至少一行含 ≥2 个非空格。
- **拆分**：按 `\r\n` / `\n` 分行 → 按 `\t` 分格 → **行优先**（Excel 视觉顺序）展平。
- **尾随空清理（红线）**：丢弃**每行行尾的连续空格**与**末尾空行** —— Excel 复制区域常带多余 Tab/换行，
  不清理会让用户多按若干次"空粘贴"。
- **空单元格保留**（行内空格）：行为可预测（用户可据此清空目标框）；但**不产生纯空行**。
- **上限**：单元格数 > 2000 拒绝并提示（防误操作把整个工作表当队列）。

### 3.2 按格粘会话（客户端）

`ClipboardIpcClient` 新增：

- `enum CellPasteAutoKey { None, Tab, Enter }`
- `void BeginCellSequentialPaste(ClipboardEntry entry, CellPasteAutoKey autoKey)`
  - 一次性取全文（`FetchEntryPlainText`，含 bin 回退；失败/摘要降级经 `SequentialIssue` 报出）
  - `Split` → 每个单元格构造 `SequentialItem`：**Payload 直接填 `Text`**（自包含）+ 置 `SkipEngineWriteback=true`
    （单元格**不是引擎里的条目**，不能走 `CopyEntryToClipboard`，否则每条一次失败 IPC + 噪声日志）
- `PasteNextSequential`：`SkipEngineWriteback` → 直接 `WriteSnapshotToClipboard(payload)`；粘后按 `autoKey` 注入：
  - `Tab` → `SendTab()`（VK_TAB 0x09）；`Enter` → `SendEnter()`（VK_RETURN 0x0D）；`None` → 不注入
  - **注入时序（红线）**：粘贴是异步的，粘完立刻发 Tab 会被吞 → 先 `Thread.Sleep(80)` 再注入
  - 单测缝：`AutoKeyInjectorHook`（headless 断言，不真注入）
- 会话级 `_cellAutoKey`（`BeginCellSequentialPaste` 时设定；`CancelSequentialPaste` 清空）

**为什么放客户端而不是引擎**：按序粘贴整套状态机本就在客户端（v1.3 设计），单元格只是"换个来源入队"；
引擎不参与 → 零 IPC 新命令、零引擎改动。

### 3.3 面板入口

- **行内按钮**（`RecentStrip.BuildRightColumn`）：`IsTabular` 为真时加第 5 个按钮 `⊞`（ToolTip「按格粘：逐个粘贴表格单元格」），
  事件 `CellPasteRequested`；非表格条目**不显示**（不制造无用入口）。
- **启动选项浮层**（复用标签编辑浮层的叠加形态，不新开窗口）：
  - 信息行：`3 行 × 4 列 = 12 格`（`IsTabular` 的输出，让用户先确认解读正确）
  - **粘完自动**：`不自动` / `Tab` / `Enter`（默认 = 上次选择，首次 = 设置默认）
  - 「开始」→ `_client.BeginCellSequentialPaste(entry, autoKey)` → 记住选择（写扁平键）→ `HidePopup()` → 状态条提示
  - 「取消」/ Esc → 关闭浮层（Esc 优先级高于收起面板，与标签浮层同规）
- **状态条**：复用 `BuildSequentialBar`（显示"按格粘就绪：12 格，去目标窗口按 Ctrl+V 逐格粘" + 剩余数 + 取消）。
- **会话期间**：Ctrl+V 重定向（`OnPasteHotkeyHook`）→ 粘下一格；`SequentialIssue` 提示（含"表格可能不完整：引擎超时降级"）。

### 3.4 设置

- 键：`extensions.clipboard-history.cell-paste-auto-key`，值 `none` / `tab` / `enter`，**默认 `tab`**。
- **只由面板消费**（面板直读扁平键），**不进引擎 `Settings` / `WatchKeys` / `apply_settings`** ——
  与先例 `paste-back-hotkey` 同模式（引擎不消费 = 不需四处同步）。设置分区加一张卡的 ChoiceRow。
- 启动浮层的选择会**回写**该键（记住上次）。

## 4. 实现顺序

1. `ClipboardTableCells.cs`（判定 + 拆分 + 上限）。
2. `ClipboardIpcClient`：`CellPasteAutoKey` + `BeginCellSequentialPaste` + `PasteNextSequential` 分支 + `SendTab/SendEnter` + 单测缝。
3. `RecentStrip`：表格条目「按格粘」按钮 + `CellPasteRequested`。
4. `PanelMainWindow`：接线 + 选项浮层 + 开始/取消 + 状态条 + `PanelTheme` 读默认值。
5. `ClipboardSection`：设置项。
6. 构建 + 单测 + 文档。

## 5. 风险与兼容（§11）

| 风险 | 处置 |
|---|---|
| 尾随 Tab/换行导致"空粘贴"多按几次 | 拆分时清理行尾空格与末尾空行（红线） |
| 粘贴异步 → 立刻发 Tab 被吞 | 粘后 `Sleep(80)` 再注入；`none` 档完全不注入 |
| 自动 Tab 在不需要的系统里"多跳一格" | 默认值可配 + 启动浮层可临时改 → 用户可控（不写死，正是用户本次的要求） |
| 大工作表当队列 | 单元格数 > 2000 拒绝并提示 |
| 引擎超时 → 全文取到摘要（截断） | 走既有 `SequentialIssue` 明示"可能不完整"，不静默 |
| 单元格条目在引擎被删 | Payload 自包含 → 不受影响（既有设计） |
| 契约兼容 | **不改 `IClipboardService`**（面板用具体类型 `ClipboardIpcClient`）；不改引擎 → 零兼容风险 |
| 与"条目级按序粘贴"混淆 | 状态条文案区分（"按格粘"vs"按序粘贴"）；两条会话互斥（新会话覆盖旧会话） |

## 6. 开放问题（§12）

1. 启动浮层默认记住上次选择 —— 若用户觉得"每次都要确认"啰嗦，可加"记住后不再问"选项（本版不做）。
2. **列优先**顺序（一列数据竖着填）本次不做（用户选逐单元格，行优先即 Excel 视觉序）；若日后需要，
   扩展点就在 `ClipboardTableCells.Split` 的排序参数。
3. HTML `<table>` 无纯文本（罕见）时的兜底解析本次不做（Excel 复制必带纯文本）。
4. **不采纳**：把单元格写进引擎（无必要，徒增 IPC 面）。

## 7. DoD（§13）

**功能 DoD（场景语言）**
- **D1**：从 Excel 复制 3×4 区域 → 历史条目出现「⊞ 按格粘」→ 点开显示「3 行 × 4 列 = 12 格」→
  在目标表单里**每按一次 Ctrl+V 粘一个格子**，顺序 = Excel 视觉顺序（行优先）。真机人工确认。
- **D2**：启动时选「粘完自动 Tab」→ 粘一格后光标自动右移；选「不自动」→ 只粘贴不按键。
- **D3**：普通段落条目**不显示**「按格粘」按钮（不制造无用入口）。
- **D4**：会话中点「取消」或面板 Esc → 之后 Ctrl+V 恢复普通粘贴（不残留重定向）。
- **D5**：单元格数超上限的巨型表格 → 明确提示拒绝，不进入会话。

**机制 DoD（内核单测，非 UI）**
- T1 `ClipboardTableCells.Split`：行优先顺序、`\r\n`/`\n` 混用、**行尾 Tab 清理**、**末尾空行丢弃**、
  行内空单元格保留、单列不判为表格。
- T2 `IsTabular` 判定 + 行列计数 + 超限拒绝。
- T3 `BeginCellSequentialPaste`：队列长度 = 格数；`SequentialRemaining` 递减；无引擎往返（用 FakeTransport 断言未发 `get_entry` 之外的写回请求）。
- T4 粘后自动按键：`Tab`/`Enter`/`None` 三档经 `AutoKeyInjectorHook` 断言注入次数与键值。
- T5 `CancelSequentialPaste` 后 `IsSequentialPasteActive=false` 且不再注入。

**构建门禁**：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；剪贴板/ IPC 单测全绿。

## 7.5 实施核销（2026-09-13 收口）

| 项 | 结果 | 证据 |
|---|---|---|
| T1 拆分规则（行优先 / 混用换行 / 行尾空清理 / 空行丢弃 / 行内空保留） | ✅ | `ClipboardCellPasteTests` 5 用例 |
| T2 `IsTabular` 判定 + 行列计数 + 超限拒绝 | ✅ | 2 用例（含 2001 格拒绝、单列/图片不判表格） |
| T3 会话入队且**不向引擎发写回** | ✅ | `Assert.Null(t.LastRequestOf("copy_to_clipboard"))` |
| T4 粘后自动按键三档（Tab / Enter / None） | ✅ | `AutoKeyInjectorHook` 断言 |
| T5 取消复位（不发生残留注入） | ✅ | 取消后 `PasteNextSequential` 无注入 |
| 额外：写回失败**不注入**（防重复粘上一格）still 跳格 | ✅ | 独立用例 |
| 构建门禁 | ✅ | `dotnet build BetterDesktop.slnx` **0 警告 0 错误** |
| 单测 | ✅ | IPC **87**（+11）；核心剪贴板 **93**（无回归） |
| D1 表格条目逐格粘（顺序=行优先） | ⏳ 待人工 | 需 Excel + 业务系统真机走查 |
| D2 自动 Tab / Enter / 不自动 生效 | ⏳ 待人工 | 浮层三选一 |
| D3 非表格条目不显示 `⊞` | ⏳ 待人工 | 视觉确认 |

**与原计划的一处调整（记录）**：原设计"启动浮层的选择回写设置键、下次记住"**已取消** ——
面板是独立 exe，**没有 `ISettingsService`、不写 `settings.json`**（与 `paste-back-hotkey` 同模式：
宿主设置界面写、面板只读）。因此：**浮层选择只对本次会话生效；长期默认值在「设置 → 剪贴板 → 面板与入口」**。
这比"面板偷偷改配置"更符合既有分层。

**新增文件**：`shell-clipboard-ipc/ClipboardTableCells.cs`、`shell-clipboard-ipc/CellPasteAutoKey.cs`、
`shell-clipboard-ipc-tests/ClipboardCellPasteTests.cs`。**引擎与契约零改动**。

## 8. 交接节（§14 · 技术力应用 skill 输入）

**注入文档（3 份）**
1. 本计划（决策与红线来源）。
2. `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md` —— 按序粘贴状态机与按键注入范式。
3. `packages/shell/shell-clipboard-panel/README.md` —— 交互入口表（需同步新增「按格粘」）。

**模式判定**：功能增量，**纯客户端 + 面板**；**不改契约、不改引擎**（零兼容风险）。

**适配参数**
- 工程：`shell-clipboard-ipc`（新文件 + 客户端方法）、`shell-clipboard-panel`（按钮 + 浮层）、`shell-clipboard`（设置分区）。
- 设置键：`extensions.clipboard-history.cell-paste-auto-key`（`none`/`tab`/`enter`，默认 `tab`）—— **面板直读，不进引擎 Settings**。
- 单测缝：`AutoKeyInjectorHook`（同族既有 `PasteInjectorHook` / `SnapshotWriterHook`）。
- 构建：`dotnet build BetterDesktop.slnx`；两个测试工程 `dotnet test`。

**DoD 核销表**：见 §7（D1/D2/D3 需真机；T1-T5 可自动化）。

**强制 scope 边界**：仅「表格条目 → 逐格按序粘贴」；不做列优先、不改引擎、不做 HTML-only 表格解析、不做云/同步。
