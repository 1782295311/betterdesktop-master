# Cairo 开发计划 · 剪贴板公共 API 信息源（系统级服务契约落地）

> Task: 在 better-desktop-cordis 落实剪贴板功能。**用户点名约束**：「复制粘贴功能的普适性 → 需要提供**公共的 API 信息源**」。即剪贴板契约必须进入 `packages/api/` 公共契约包（2026-09-08 已独立化的外部扩展唯一依赖面），而非实现包内部接口——任何板块（菜单栏/扩展中心/搜索框/第三方扩展）引用 `BetterDesktop.Api` 即可消费剪贴板能力。
> 蓝图基础：`docs/plans/archive/2026-09-08-clipboard-history-extension.md`（已归档，用户已确认的功能全集 A-N 与版本归属 v1/v1.1/未来、三方融合矩阵、生死线均继续有效）。本计划**只更新其与新公共 API 范式冲突的部分 + 落定本阶段执行范围**，其余引用原档不重抄。
> 证据基线：HEAD `c3cc7f5`（2026-09-09 v1.3.0 统一版本收口，工作区 clean，门禁 14 道全绿）。迁移源三份源码 [verified] 位置确认（见 §2.3）。技术力命中：**1301-clipboard-history**（TS/L2，外部 ZTools：延迟读取/去重/图片落盘/最近复制 API 红线）、**3101-global-hotkey**（C#/L2：RegisterHotKey 0x581/释放纪律）。
> 范式差异（相对归档计划）：归档计划 §3 决策「契约与实现同程序集（shell-settings 模式）」→ **本计划升级为「契约入公共 api 包（BetterDesktop.Api），实现留 shell-clipboard」**——与 2026-09-08 契约包独立化（16 域全量迁入，缺 Clipboard 域）及用户「公共 API 信息源」要求对齐。

## 1. Objective

**产品定位（本阶段）**：剪贴板做成**系统级能力**，其**公共 API 契约层**（接口/模型/事件/枚举）进入 `packages/api/Clipboard/`，作为信息源供所有板块消费；实现包 `packages/shell/shell-clipboard/` 提供 `IClipboardService` 的真实实现并通过内核服务图 `Provide<T>`（ADR-002 D1）注册，服务**常驻**，扩展中心开关只控制监控活性。

用户可感知的结果（场景语言，本阶段 Phase A）：

1. **内核服务图可拿到剪贴板服务**：任意板块（含第三方扩展，仅引用 `BetterDesktop.Api` + kernel）调用 `context.Get<IClipboardService>()` 非 null，可查询历史条目、读取最近复制（`GetLastCopiedContent`）、触发粘贴（`CopyEntryToClipboard/PasteEntryToActiveWindow`）、打开历史面板入口（`OpenHistoryWindow`，UI 后续阶段接线）。
2. **一键开启监控（开关生效）**：扩展中心「+」出现「剪贴板历史」条目并标「已接入 · 开关立即生效」；打开后系统开始记录复制内容（文本/HTML/图片/文件四类），关闭即停止记录（服务本体不注销、历史保留可查）；重启后按上次选择恢复。
3. **历史有底线**：去重置顶、隐私黑名单（进程名+窗口标题双表）、10000 条/收藏 200/单图 5MB/总量 200MB/90 天、DPAPI 加密 JSON 持久化、图片落盘元数据化（JSON 不含 base64）。
4. **契约可加性保证**：v1 契约定稿后，v1.1 功能（按序粘贴/菜单栏入口/系统右键等）以**新增方法/事件**扩展，不修改既有成员，不破坏本阶段消费方。

验收标准：§13 DoD（D1-D4 真机走查 + D5 门禁 + D9 服务契约走查）通过 + 构建 0 警告 0 错误 + 单测全绿。

## 1A. 功能全集与阶段划分

功能全集 A-N（监控/历史/分类/粘贴/搜索/隐私/容量/存储/系统级服务/UI/热键/按序粘贴/OCR/便签）与版本归属**沿用归档计划 1A 节**，不重抄。本计划只落定**执行顺序**：

- **Phase A（本次范围，用户点名「公共 API 信息源」）**：
  - A 组监控捕获全部（A1-A6：监听/四类捕获/三格式/空兜底/抑制/降级）
  - B 组历史管理核心（B1 去重置顶、B2 收藏、B3 删除、B5 复制次数、B6 来源记录、B7 日期分组字段）
  - C 组分类入库（C1 语义分类基础：File>Image>Code>RichText>Text；C3 代码检测；**C2 混合检测/C4 分类标签 UI 属 Phase B**——模型字段本阶段就绪）
  - F 组隐私安全（F1 黑名单、F2 暂停、F3 DPAPI 存储）
  - G 组容量生命周期（G1-G5）
  - H 组存储持久化（H1-H4）
  - I 组系统级服务契约 **全部**（I1 契约、I2 最近复制、I3 面板打开 API、I4 事件、I5 扩展中心开关；I6-I10 消费板块接线属 Phase B）
  - M 组 OCR 录入契约（M1-M3：ImportEntries 接口 + 模型 + 复用管线；M4 复用）
- **Phase B（后续轮次，蓝图 = 归档计划）**：J 组历史面板 UI（1596 行移植）、K 组热键、D 组粘贴增强（原格式/纯文本/自动分段/按类写回）、E 组搜索筛选 UI、L 按序粘贴（v1.1）、I6-I10 消费板块、C2/C4 分类展示。
- 实施完成后按技术力积累 skill 回写 1301（补 C# 变体，见 §12）。

## 2. Current Behaviour

### 2.1 公共 API 契约包范式（本计划架构核心，[verified]）

- `packages/api/BetterDesktop.Api.csproj`：`net8.0-windows10.0.19041.0`/x64/UseWPF/TreatWarningsAsErrors/RootNamespace `BetterDesktop.Api`/零项目依赖（仅 BCL+WPF 基础类型），注释明确定位「信息源提供 API 契约包（外部扩展唯一依赖面）」。
- 现有 16 域（AppSource/Calendar/ContextMenu/Convert/Core/Desktop/Dock/Music/Notification/PluginWindow/Recent/Search/Settings/StartMenu/Status/Taskbar/WindowTracker），**无 Clipboard 域** [verified：api 目录枚举]。
- 命名空间惯例：**保留原契约命名空间**（`BetterDesktop.Shell.Xxx.Contracts` / `.Models`），使用方 using 零改动 [verified：ISettingsService.cs 用 `namespace BetterDesktop.Shell.Settings.Contracts`]。剪贴板为全新契约，按同惯例定 `BetterDesktop.Shell.Clipboard.Contracts`（文件物理位置在 `packages/api/Clipboard/`）。

### 2.2 接线点现状（[verified]，均基于 HEAD c3cc7f5）

| 接线点 | 现状 | 追加方式 |
|---|---|---|
| `shell-menu-bar/Contracts/ExtensionCatalog.cs` | `External` 列表 5 项：quick-note/programs-menu/weather/screenshot/dynamic-desktop（`record ExtensionDescriptor(Id, Title, Description, Glyph)`） | 追加 `new ExtensionDescriptor("clipboard-history", "剪贴板历史", "记录剪贴板历史，搜索/收藏/一键粘贴（Ctrl+Shift+V）", "剪")` |
| `shell-menu-bar/Windows/ExtensionsCenterWindow.cs` | `private static readonly HashSet<string> Implemented = new() { "quick-note", "programs-menu" }`；遍历 `ExtensionCatalog.External` 用 `Implemented.Contains(id)` 判「已接入 · 开关立即生效」 | `Implemented.Add("clipboard-history")` |
| `host/Bootstrap.cs` | Factories 字典：`["quick-note"] = () => new QuickNotePlugin(),` | `["clipboard-history"] = () => new ClipboardPlugin(),` + `using BetterDesktop.Shell.Clipboard;` |
| `host/cordis.yml` | `- id: quick-note\n    name: quick-note` | quick-note 后追加同构块 |
| `BetterDesktop.slnx` | `<Folder Name="/packages/shell/shell-quick-note/">` + Project 登记 | 追加 shell-clipboard 与 shell-clipboard-tests 登记块（照 quick-note 模板） |

### 2.3 迁移源（三方源码，位置 [verified]，路径含 `Cairo Desktop` 子目录层）

| 文件 | 用途 |
|---|---|
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Infrastructure\Services\ClipboardManager.cs`（1584 行） | 主体：监听/四类捕获/去重/隐私/暂停/收藏/驱逐/过期/DPAPI JSON/粘贴 |
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Infrastructure\Services\ClipboardEntry.cs`（461 行） | 条目模型（本计划改为 api 包公共模型） |
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Interop\ClipboardNative.cs`（142 行） | P/Invoke 收口（CF_UNICODETEXT/CF_HTML/GlobalAlloc） |
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.MenuBar\Clipboard\ClipboardQuickAccessWindow.xaml.cs`（1596 行） | Phase B UI 移植主体 |
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.MenuBar\Services\ClipboardQuickAccessService.cs`（211 行） | Phase B 热键入口 |
| `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Application\Interfaces\Services\IClipboardService.cs`（213 行） | 契约参考 |
| `betterdt\cairoshell原版\Cairo Desktop\CairoDesktop.Infrastructure\Clipboard\ClipboardService.cs`（693 行） | 抑制令牌 `_suppressCapture` + `ReadClipboardSnapshot` 测试接缝（本计划机制出处） |
| `betterdt\cairoshell原版\Cairo Desktop\H5H8Verify\ClipboardSuppressionTests.cs` | 写回抑制回归单测范式 |

### 2.4 技术力资产

- **1301-clipboard-history**（TS/L2）：红线 = 延迟读取兜底、hash 去重、写前取消监听防自我触发、图片落盘元数据化；功能想法 = 最近复制 API `getLastCopiedContent(timeLimit?)`。→ 融合进 IClipboardService（I2）。
- **3101-global-hotkey**（C#/L2）：0x581 冲突捕获、退出必须 Unregister。→ Phase B 热键实现生死线。
- **未命中（Phase B 回写）**：C# 侧 AddClipboardFormatListener + HwndSource 监听文档（1301 标注「仓库暂无」），实施后按积累 skill 补。

## 3. Relevant Architecture（公共 API 信息源）

```
                         ┌────────────────────────────────────────────────┐
                         │  内核服务图 CordisContext（ADR-002 D1）          │
                         │  Provide<T> / Get<T> / Effect / Events / Logger │
                         └────────────────────────────────────────────────┘
         ▲ Provide<IClipboardService>（ClipboardPlugin.LoadAsync，句柄常驻）   ▲ Get<IClipboardService>
         │                                                                 │
┌────────┴──────────────────────────┐        ┌─────────────────────────────┴──────────────┐
│  packages/api/Clipboard/（公共契约）│        │  消费板块（仅引用 BetterDesktop.Api）        │
│  IClipboardService / ClipboardEntry│        │  · 扩展中心（shell-menu-bar）：开关=监控活性  │
│  ClipboardItemKind/ContentCategory │        │  · 搜索框/启动器（v1.1+）：历史检索/最近复制   │
│  LastCopiedContent/ClipboardImportItem│     │  · 菜单栏快捷入口（v1.1）                    │
│  ClipboardEvents 常量              │        │  · 第三方扩展（kernel + Api 即可）           │
└────────┬──────────────────────────┘        └────────────────────────────────────────────┘
         │ 实现（引用 api 包）
┌────────┴──────────────────────────┐
│  packages/shell/shell-clipboard/   │
│  ClipboardManager（监听/历史/持久化）│
│  ClipboardNative（internal P/Invoke）│
│  ClipboardPlugin（观察者 + Provide） │
└───────────────────────────────────┘
```

- **依赖方向**：api/Clipboard（零依赖，仅 BCL）← shell-clipboard（api + kernel + shell-core）← 消费板块（api + kernel）。shell-clipboard **禁止**引用 shell-menu-bar（面板定位自实现，避免反向依赖）。
- **服务生命周期**：`ClipboardPlugin.LoadAsync` 即 `context.Provide<IClipboardService>(manager)`，返回句柄由 `context.Effect` 托管（QuickNote 同款），卸载时 dispose——服务常驻；扩展中心开关（`extensions.clipboard-history.enabled`）只控制 `manager.Start()/Stop()`（监听活性，历史保留）。[verified：IContext.cs `IDisposable Provide<T>(T service)` / `T? Get<T>()` / `Effect(Func<IDisposable>)`]
- **契约可加性**：v1.1+ 新能力以新增方法/事件扩展 `IClipboardService`，不改既有成员。

## 4. Technical-Knowledge Findings

| 文档 | 定位 | 对本计划的作用 |
|---|---|---|
| 1301-clipboard-history（L2） | 外部 ZTools 剪贴板历史 | 延迟读取兜底/去重/图片落盘元数据化/最近复制 API/总量预算（融合矩阵见归档计划 §2.4） |
| 3101-global-hotkey（L2） | RegisterHotKey 全局热键 | Phase B 热键生死线（0x581 捕获、退出 Unregister） |
| 拆析/拆析-cairoshell-最初开源版 | cairoshell 架构 | 命令系统/插件加载范式佐证（迁移源为探索版，非此拆析对象，注意区分） |

## 5. Constraints & Invariants（生死线，沿用归档计划 + 契约层新约束）

1. **防自我触发（写回抑制）**：写剪贴板前 `Volatile.Write(_suppressCapture, 1)`，`OnClipboardUpdate` 入口 `Interlocked.Exchange(..., 0)` 消费式清零并跳过；写失败立即复位；保留内容对比双保险；必须配抑制回归单测（原版 ClipboardSuppressionTests 同款）。
2. **捕获判据不靠立即读**：内容非空 + 四类格式存在为判据；空结果 30ms×3 短重试兜底；不引入固定 180ms 硬延迟（C# 侧两版源码均以内容判据为准）。
3. **热键配对释放（Phase B）**：Activate 注册 / Deactivate 注销，缺一则 0x581 永久失效。
4. **隐私黑名单命中即跳过**：进程名表 + 窗口标题关键词表（密码/网银/验证码）双通道。
5. **存储不落明文 + 图片元数据化**：DPAPI CurrentUser + `CBENC1\0` 头；JSON 只存图片路径/宽高/尺寸，字节落 `clipboard\images\{id}.png`，总量 200MB 淘汰；禁止 base64 入 JSON。
6. **不破坏扩展中心既有契约**：ExtensionCatalog/Implemented 只增不改；quick-note/programs-menu/weather/screenshot/dynamic-desktop 行为零变化。
7. **公共契约层纪律（本计划新增）**：api/Clipboard 内**只允许**接口/不可变模型/枚举/事件常量，零业务实现、零 P/Invoke、零 WPF 控件依赖；模型字段稳定可序列化（N1 便签/工作站取数基础）；契约一旦合入 v1.3 分支即视为定稿，v1.1 只可加不可改。
8. **分类正确性（Phase B 合适粘贴前置）**：代码条目禁止 HTML/RTF 写回；混合内容禁止丢图；分类入库只算一次；本阶段 C1/C3 模型字段就绪、判定逻辑纯函数可单测。

**验收标准（做对了的定义）**：§13 D1-D4 + D9 + 门禁 0 警告 0 错误 + §8 单测全绿 + `packages/api/Clipboard/` 零实现依赖检查通过。

## 6. Proposed Changes

### 6.0 公共 API 契约层 `packages/api/Clipboard/`（Phase A 核心，用户点名）

8 个新文件，随 BetterDesktop.Api.csproj 编译（csproj 零改动，SDK 自动包含）：

| 文件 | 命名空间 | 内容 |
|---|---|---|
| `IClipboardService.cs` | `BetterDesktop.Shell.Clipboard.Contracts` | 系统级服务契约（见下） |
| `ClipboardEntry.cs` | 同 | 公共条目模型（Id/Content/HtmlContent/RtfContent/Timestamp/IsPinned/ContentType/ImagePath/FilePaths/ImageWidth/Height/SizeBytes/CopyCount/SourceProcessName/SourceWindowTitle/Tags/ContentCategory/HasImages/HasTable/IsCode + 只读派生预览字段） |
| `ClipboardItemKind.cs` | 同 | enum Text/Image/Files/Html/RichText |
| `ContentCategory.cs` | 同 | enum Text/Code/RichText/Image/File |
| `LastCopiedContent.cs` | 同 | record { ClipboardItemKind Type; string? ContentOrPath; DateTime Timestamp }（1301 融合） |
| `ClipboardImportItem.cs` | 同 | record（OCR 批量录入契约，M1：Kind/Content/Html/ImagePath/FilePaths/SourceApp） |
| `ClipboardEvents.cs` | 同 | 事件名常量（HistoryChanged/PauseStateChanged/MonitoringStateChanged——跨程序集走内核 IEventBus + ShellEvents 模式，归档计划违规 1 纪律） |
| `ClipboardHistoryChangedEventArgs.cs` | 同 | record（HistoryChanged 载荷：ChangeKind Added/Removed/Cleared/Updated + EntryId） |

`IClipboardService`（公共契约面，Phase A 方法集）：
- 查询：`GetFilteredEntries(ClipboardItemKind? kind, ContentCategory? category, string? keyword, string? sourceApp)`、`GetSourceApps()`、`GetLastCopiedContent(TimeSpan? timeLimit)`
- 变更：`PinEntry/UnpinEntry/TogglePin/DeleteEntry/DeleteEntries/ClearAllUnpinned/SetEntryTags`
- 粘贴（Phase A 提供基础路径）：`CopyEntryToClipboard(entry)`、`CopyEntryAsPlainText(entry)`、`PasteEntryToActiveWindow(entry)`、`OpenFileLocation`
- 录入：`ImportEntries(IEnumerable<ClipboardImportItem>)`（OCR 预留）
- 状态：`IsMonitoringEnabled`、`PauseTemporarily()/Resume()`、`OpenHistoryWindow()`
- 事件：`event Action<ClipboardHistoryChangedEventArgs>? HistoryChanged` 等 3 个（同程序集裸 event 也可，但为消费板块跨程序集，按内核事件总线规范；实现侧注册事件源）

> 命名空间说明：api 包惯例保留原契约命名空间，剪贴板全新契约按 `BetterDesktop.Shell.Clipboard.Contracts` 落位，与全库 `BetterDesktop.Shell.Xxx.Contracts` 一致；消费方 using 后仅依赖 BetterDesktop.Api.dll。

### 6.1 实现包 `packages/shell/shell-clipboard/`

**csproj**（`BetterDesktop.Shell.Clipboard.csproj`，照 quick-note 模板）：TFM `net8.0-windows10.0.19041.0`、Nullable/ImplicitUsings、TreatWarningsAsErrors、`Platforms=x64`、`UseWPF=true`；ProjectReference：`packages/kernel/kernel/BetterDesktop.Kernel.csproj` + `packages/api/BetterDesktop.Api.csproj` + `packages/shell/shell-core/BetterDesktop.Shell.Core.csproj`。命名空间 `BetterDesktop.Shell.Clipboard`（实现），契约用 api 包 `BetterDesktop.Shell.Clipboard.Contracts`。

**ClipboardNative.cs**（new，internal static P/Invoke 收口，照迁移源）：`AddClipboardFormatListener/RemoveClipboardFormatListener`、`GetForegroundWindow/GetWindowThreadProcessId/GetWindowText`、`keybd_event`、`SetText/SetHtmlText`（CF_UNICODETEXT=13 / "HTML Format" + GlobalAlloc 系列）。

**ClipboardManager.cs**（new，探索版移植 + 原版机制 + 外部想法；契约实现类）：
- 监听：HwndSource 1x1 隐藏窗 + AddClipboardFormatListener + WM_CLIPBOARDUPDATE（A1）；四类优先级 HTML>Text>Image>FileList（A2）；Html+Rtf+Text 三格式并存（A3，原版模型）；内容非空判据 + 30ms×3 短重试（A4）；抑制令牌 + 内容对比双保险（A5，原版 `_suppressCapture`）；监听失败 try/catch 降级（A6）。
- 历史：Add*Entry 去重置顶（B1）；Pin/Unpin/Delete/DeleteEntries/ClearAllUnpinned/SetEntryTags（B2/B3）；CopyCount（B5）；来源进程名+窗口标题+emoji（B6）；Timestamp 日期分组字段（B7）。
- 分类（C1/C3）：`ContentAnalyzer.Analyze(html, text) → ContentProfile{Category, HasImages, HasTable, IsCode}`，顺序 File>Image>Code>RichText>Text，代码检测 = 缩进占比+关键字密度+注释特征+括号配对；入库只算一次。
- 隐私/暂停（F1/F2）：进程名+窗口标题双表黑名单；PauseTemporarily(60s)/Resume。
- 容量/生命周期（G1-G5）：10000 驱逐最旧非收藏 / 收藏 200 / 单图 5MB / 图片落盘 + 总量 200MB 淘汰 / 90 天过期 + 孤儿图片清理（每小时）。
- 存储（H1-H4）：System.Text.Json + DPAPI（CBENC1 头 + 明文旧格式回退）；损坏自愈空历史；800ms 保存节流；重启恢复。
- 粘贴基础路径（D1/D2 简化版，Phase B 增强）：`CopyEntryToClipboard`（按类别写回基础：Text→SetText/Code→SetText 强制纯文本/RichText→Html+Rtf+Text/Image→位图/File→FileDropList）、`CopyEntryAsPlainText`、`PasteEntryToActiveWindow`（写剪贴板 + SendPaste）、`OpenFileLocation`。
- 录入（M1-M3）：`ImportEntries` 复用去重/分类/落盘。
- 测试接缝（原版）：`protected virtual ReadClipboardSnapshot()` 快照探针 + `internal OnClipboardUpdate` + `InternalsVisibleTo("BetterDesktop.Shell.Clipboard.Tests")`。
- 服务面：实现 api 包 `IClipboardService` 全部成员；`Start()/Stop()`（幂等）控制监听活性。

**ClipboardPlugin.cs**（new，quick-note 同构 + Provide）：
- `Name = "shell.clipboard"`；`EnabledKey = "extensions.clipboard-history.enabled"`。
- LoadAsync：`Get<ISettingsService>()` → `new ClipboardManager(...)` → `context.Provide<IClipboardService>(manager)`（句柄托管进 context.Effect）→ 订阅 SettingsChanged（key==EnabledKey）→ 按 `Get(EnabledKey, false)` Activate/Deactivate。
- Activate = `manager.Start()`；Deactivate = `manager.Stop()`（停监听，历史保留）；UnloadAsync 释放 Provide 句柄 + 关闭面板（若有）。

### 6.2 扩展中心注册（2 处小改）

- `ExtensionCatalog.cs` External 追加 `clipboard-history` 条目（§2.2 表）。
- `ExtensionsCenterWindow.cs` Implemented 追加 `"clipboard-history"`。

### 6.3 宿主注册（3 处小改）

- `host/Bootstrap.cs`：using + `["clipboard-history"] = () => new ClipboardPlugin(),`
- `host/cordis.yml`：quick-note 后追加 `- id: clipboard-history\n    name: clipboard-history`
- `BetterDesktop.slnx`：登记 shell-clipboard 项目（照 quick-note 块）

### 6.4 单测项目 `packages/shell/shell-clipboard-tests/`

csproj 照 shell-context-menu-tests 模板（xunit 2.9.3 + Test.Sdk，引用 shell-clipboard + kernel）；`InternalsVisibleTo` 目标程序集名 `BetterDesktop.Shell.Clipboard.Tests`。

## 7. Impact Analysis

- **零既有行为破坏**：ExtensionCatalog/Implemented/Bootstrap/cordis.yml/slnx 均追加；quick-note 等 5 项外部扩展与 15 项系统功能不动 [verified：ExtensionCatalog 现状]。
- **新增服务类型，无覆盖**：`Provide<IClipboardService>` 为新类型，不覆盖既有服务 [verified：CordisContext Provide 语义]；依赖感知仅通知未来 Inject IClipboardService 的插件。
- **api 包**：新增 8 文件编译进 BetterDesktop.Api.dll；纯契约零依赖，不影响 api 包「最底层契约层」定位 [verified：csproj 注释]。
- **系统级副作用**：默认关闭（EnabledKey=false，启动零监听零存储）；存储文件仅开关开启后创建；服务常驻开销 = 单实例对象 + 事件订阅。
- **性能**：监听回调仅内存操作 + 800ms 保存节流；每小时清理定时器仅监控开启时运行；面板懒创建（Phase B）。

## 8. Testing Plan（内核逻辑单测，测非 UI）

`shell-clipboard-tests`（xunit），Phase A 用例：

| 测试文件 | 用例（正常/边界/异常） |
|---|---|
| ClipboardSuppressionTests.cs | 抑制令牌置位 → OnClipboardUpdate 不触发 HistoryChanged、令牌消费归零、历史不写入；未抑制对照正常捕获（经快照接缝注入） |
| ClipboardCaptureTests.cs | 快照接缝注入四类 → 正确建条目；空快照不记录；捕获顺序 HTML>Text |
| ClipboardDedupeTests.cs | 同内容复制置顶不新增；不同内容新增；HTML 与文本同源优先 HTML 单条 |
| ClipboardEvictionTests.cs | 超 10000 驱逐非收藏尾部；收藏超 200 驱逐最旧收藏 |
| ClipboardPrivacyTests.cs | 黑名单进程名/窗口标题跳过；正常来源记录 |
| ClipboardCleanupTests.cs | 90 天前非收藏被清（含落盘图片文件删除）、收藏保留 |
| ImageStorageTests.cs | 图片捕获落盘 `images\{id}.png` + JSON 无 base64；超 200MB 淘汰最旧未固定；孤儿图片同步删除 |
| ClipboardFilterTests.cs | kind/category/keyword/sourceApp 过滤；searchKeyword 命中 Content/Preview/Tags/FilePaths |
| ContentAnalyzerTests.cs | 代码文本→Code；正文→Text；HTML 含 img/table→HasImages/HasTable；File/Image 优先级；分类顺序稳定 |
| JsonPersistenceTests.cs | 往返序列化（含 imagePath）；CBENC1 头；损坏数据空历史不抛；图片字节不在 JSON |
| ClipboardServiceContractTests.cs | api 包契约可被实现满足（反射检查接口成员）；GetFilteredEntries/GetLastCopiedContent/Pin/Delete 语义 |

## 9. Build & Verification

- 门禁：`pwsh -NoExecutionPolicy Bypass scripts/run-gates.ps1`（14 道，基线 c3cc7f5 全绿）。
- 构建：`dotnet build BetterDesktop.slnx`（0 警告 0 错误，TreatWarningsAsErrors 全局）。
- 单测：`dotnet test`（新增项目全绿）。
- 契约零依赖检查：`packages/api/Clipboard/` 文件无 using 引用 shell-*/kernel 实现类型。

## 10. Rollout

1. 6.0 api/Clipboard 契约层（8 文件）→ 构建（api 包独立可编译）
2. 6.1 shell-clipboard 实现包（csproj → Native → Entry/Analyzer → Manager → Plugin）
3. 6.4 单测项目（随 Manager 同步写）
4. 6.2 扩展中心 + 6.3 宿主注册 → 构建门禁 + 单测
5. §13 D1-D4 + D9 真机走查
6. 技术库回写（§12 D6：1301 补 C# 变体）

## 11. Risks

| 风险 | 等级 | 缓解 |
|---|---|---|
| 自粘贴触发历史污染 | 高 | 原版抑制令牌 + 内容对比双保险 + ClipboardSuppressionTests 回归锁定 |
| 剪贴板被占用读/写失败 | 低 | try/catch 静默降级，空内容不记录 |
| api 契约误入实现细节（P/Invoke/UI 依赖） | 中 | §5-7 纪律 + 契约零依赖检查进 DoD；ClipboardNative 强制 internal |
| 与既有 5 项扩展/系统功能冲突 | 低 | 只追加不改；接线前核对工作区 clean（已确认） |
| 历史 JSON 膨胀 | 低 | 图片落盘 + 元数据化 + 200MB 预算，JSON 永不携带图片字节 |
| 契约定稿后 v1.1 需要改接口 | 中 | §5-7 可加性纪律：新增方法/事件，不改既有成员（§12 登记按序粘贴等 v1.1 扩展点） |

## 12. Open Questions & Deferred

1. **Phase B 范围确认**：历史面板 UI（J 组 1596 行移植）、热键（K 组）、自动分段/按类写回（D 组）、搜索筛选（E 组）是否下轮继续按归档计划执行——本计划 Phase A 不包含，契约已预留。
2. **热键冲突交互（K5）**：v1.1 设置分区换键；Phase B 仅日志。
3. **容量/过期配置化（G6）**：v1.1 进 shell-settings 分区。
4. **消费板块接线（I6-I10）**：菜单栏快捷入口/自绘右键/系统右键/搜索框联动，Phase B+ 逐步接入（契约已就绪）。
5. **D6 技术库回写**：实施完成后更新 1301-clipboard-history.md 补「C# 变体」（AddClipboardFormatListener/HwndSource/System.Text.Json/图片落盘/抑制令牌/内核服务 Provide 模式/公共契约包模式）。
6. **hash 去重加速缓存**：万条级内容比较已够快，量级增大再引入（1301 lastSavedHash 想法）。

## 13. Definition of Done

- **D1 端到端主场景（真机走查）**：启动 → 菜单栏「+」→「剪贴板历史」显示「已接入 · 开关立即生效」→ 打开开关 → 复制 "abc" → `GetLastCopiedContent()` 返回 "abc"（内核服务非 null）→ 再复制 "abc" → 历史条数不变（去重）→ 关闭开关 → 服务仍可 Get、历史仍可查（监控停止）。
- **D2 类型场景（真机走查）**：截图复制 → 图片条目含 ImagePath → `%LOCALAPPDATA%\BetterDesktop\clipboard\images\` 出现 PNG 且历史 JSON 无 base64；资源管理器复制文件 → Files 条目；`CopyEntryToClipboard` 后目标应用粘贴成功。
- **D3 隐私/暂停场景（真机走查）**：复制密码管理器窗口内容 → 无该条目；`PauseTemporarily()` → 60s 内不记录 → `Resume()` 恢复。
- **D4 持久化（真机走查）**：重启后开关状态保持、历史在、存储文件 CBENC1 头非明文、图片文件可解码。
- **D5 构建门禁**：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；`dotnet test` 全绿（§8）；门禁 14 道全绿。
- **D9 系统级服务契约（真机走查）**：`context.Get<IClipboardService>()` 非 null；api/Clipboard 契约文件零实现依赖（grep 检查无 shell-*/kernel using）；`ImportEntries` 批量录入复用去重/分类/落盘。

## 14. Handoff（交接给「技术力应用」skill）

### 注入清单（实现前必读）
1. 技术库：`TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（红线）、`TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md`（Phase B 用）。
2. **公共契约包范式（本计划核心差异，必须读）**：`packages/api/BetterDesktop.Api.csproj`（零依赖约束/命名空间惯例）+ 任一现有域契约（如 `packages/api/Settings/ISettingsService.cs`、`packages/api/Dock/`）作为文件组织模板。
3. 迁移源（探索版 = 主框架，路径含 `Cairo Desktop` 子目录）：
   - `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Infrastructure\Services\ClipboardManager.cs`（监听/隐私/收藏/驱逐/过期/持久化/粘贴主体）
   - `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Infrastructure\Services\ClipboardEntry.cs`（模型参考，本计划迁入 api 包）
   - `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Interop\ClipboardNative.cs`（原生写入收口）
   - `betterdt\cairoshell-master\Cairo Desktop\CairoDesktop.Application\Interfaces\Services\IClipboardService.cs`（契约参考）
4. 机制吸收源（官方原版，**必须读**）：
   - `betterdt\cairoshell原版\Cairo Desktop\CairoDesktop.Infrastructure\Clipboard\ClipboardService.cs`（抑制令牌 `_suppressCapture` + `ReadClipboardSnapshot` 测试接缝）
   - `betterdt\cairoshell原版\Cairo Desktop\H5H8Verify\ClipboardSuppressionTests.cs`（写回抑制回归单测范式）
5. 本项目范式（观察者插件 + 服务提供）：`packages/shell/shell-quick-note/QuickNotePlugin.cs`（Activate/Deactivate/Effect/EventBus 全套）+ `packages/kernel/kernel/` 下 `IContext.cs`/`CordisContext.cs`（`Provide<T>`/`Get<T>`/`Effect` 语义）。
6. 本项目接线点：`shell-menu-bar/Contracts/ExtensionCatalog.cs`（External 5 项现状）、`shell-menu-bar/Windows/ExtensionsCenterWindow.cs`（Implemented HashSet）、`host/Bootstrap.cs`（Factories）、`host/cordis.yml`、`BetterDesktop.slnx`（均见 §2.2 现状表）。

### 模式判定与适配参数
- **模式**：**公共契约层（api 包）+ 系统级服务插件** = 契约入 `packages/api/Clipboard/`（`BetterDesktop.Shell.Clipboard.Contracts` 命名空间，零实现依赖）+ `context.Provide<IClipboardService>`（ADR-002 D1 服务常驻）+ QuickNotePlugin 观察者模式控制监控活性（扩展中心开关 → ISettingsService 键 → Activate/Deactivate，历史保留）。
- **适配参数**：实现命名空间 `BetterDesktop.Shell.Clipboard`；TFM `net8.0-windows10.0.19041.0`/x64/UseWPF/TreatWarningsAsErrors；设置键 `extensions.clipboard-history.enabled`（默认 false）；存储 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`（DPAPI + CBENC1 头 + System.Text.Json）+ 图片 `%LOCALAPPDATA%\BetterDesktop\clipboard\images\{id}.png`；容量 10000/收藏 200/单图 5MB/总量 200MB/90 天；Name `shell.clipboard`（cordis id/name `clipboard-history`）；测试程序集名 `BetterDesktop.Shell.Clipboard.Tests`（InternalsVisibleTo 目标）；分类顺序 File>Image>Code>RichText>Text、代码强制纯文本写回（Phase A 基础版）；**Phase B 项（UI/热键/分段/搜索/消费板块）本阶段不做，蓝图 = 归档计划 2026-09-08-clipboard-history-extension.md**。

### DoD 核销表（实现方逐项自检后由验收方确认）
| # | 核销项 | 结果 |
|---|---|---|
| 1 | §6.0 api/Clipboard 契约层 8 文件落地（零实现依赖，grep 验证无 shell-*/kernel using） | ☐ |
| 2 | §6.1 shell-clipboard 实现包（csproj + Native + Manager + Plugin）落地，Provide<IClipboardService> 注册 | ☐ |
| 3 | §6.2 扩展中心 2 处注册（External + Implemented） | ☐ |
| 4 | §6.3 宿主 3 处注册（Bootstrap/cordis.yml/slnx） | ☐ |
| 5 | §8 单测全绿（11 文件覆盖抑制/捕获/去重/驱逐/隐私/清理/落盘/过滤/分类/持久化/契约） | ☐ |
| 6 | 门禁 14 道全绿 + 构建 0 警告 0 错误 | ☐ |
| 7 | §13 D1-D4 + D9 真机走查通过 | ☐ |

---

## § 可选增强 / 超越需求建议（beyond，不混入强制 scope）

- **beyond-1 · 契约三件套范式推广**：api/Clipboard 的「接口+模型+事件常量」组织可作后续新系统能力（OCR、便利签）的公共契约模板，与现有 16 域对齐。收益：外部扩展生态统一依赖面。
- **beyond-2 · 来源应用统计驱动常用置顶**：`GetSourceApps` 已按计数排序，Phase B 面板可加「常用来源」快捷筛选（依赖现成方法）。
- **beyond-3 · 复制次数「常用」排序视图**：CopyCount 字段已就绪，Phase B 筛选 chips 可加「常用」档，零成本增强检索。
- **beyond-4 · 契约快照自检**：可在门禁中加一道「api/Clipboard 无实现依赖」静态检查脚本（grep 断言），防止后续契约包腐烂（对齐 2026-09-08 契约包独立化收口纪律）。
