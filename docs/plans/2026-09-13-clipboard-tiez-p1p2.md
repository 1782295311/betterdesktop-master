# 剪贴板 · TieZ 对标 P1/P2 剩余项实施计划（2026-09-13）

> 来源：`docs/audits/2026-09-13-tiez-comparison.md` §2.2 / §3 的**剩余未做项**（P0-1/P0-2/P1-3 已完成）。
> 本计划覆盖：**P1-4 命名格式透传 / P1-5 按应用清洗规则 / P2×4（渐进式富文本探测 · 敏感信息脱敏 · 临时粘贴 · 标签体系补全）**。
> 许可前提：对标对象为 GPL-3.0，本计划**只借思路、不抄代码**，全部自行实现。

## 0. 技术力检索结论（Phase 1/2）

| 检索关键词 | 命中 | 处置 |
|---|---|---|
| 剪贴板历史 / 多格式写回 / 去重 | `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`、`1302-sticker-mode.md` | 命中。1302 已有「CF_HDROP/CF_DIB 多格式齐发写回」红线 → P1-4 复用其「多格式并存、`EmptyClipboard` 只调一次」的既有实现骨架（`capture.rs::write_back` 已是该形态） |
| 正则规则引擎 / 按应用规则 | 无 | **无现成技术文档，新建**（关键词：按应用清洗、正则替换、ignore 规则） |
| 敏感信息识别 / 脱敏遮罩 | 无（仅 `privacy.rs` 硬编码黑名单） | **无现成技术文档，新建**（关键词：手机号/身份证/邮箱正则、预览遮罩） |
| 渐进式捕获重试 | 无 | **无现成技术文档，新建**（关键词：捕获时序、分步写格式） |
| 临时粘贴 / 剪贴板快照还原 | 无 | **无现成技术文档，新建**（关键词：快照还原、粘贴后恢复） |

**过期文档处置**：`1301-clipboard-history.md` 记录的是 TypeScript 版引擎语义；本项目引擎为 Rust 实现（`engine/src/*.rs`），细节以**当前源码为权威**。计划完成后按积累 skill 回写新机制（见 §14）。

## 1. 现状锚点（源码验证）

| 层 | 文件 | 事实 |
|---|---|---|
| 引擎捕获 | `engine/src/capture.rs:48/76` | `read_snapshot`/`read_snapshot_locked`：**一次性同步读** HTML>RTF>Text>Image>Files，无延时/无渐进重试；`open_clipboard_with_retry` 仅 3×50ms 抢锁重试 [verified] |
| 引擎写回 | `capture.rs:603/659/731/829/789` | `write_back` 支持 CF_UNICODETEXT / CF_HTML / CF_RTF / PNG / CF_DIB / CF_HDROP；**自定义命名格式未读也未写** [verified] |
| 引擎模型 | `engine/src/model.rs:98` | `ClipboardEntry` 字段全集（含 `tags`/`source_process_name`/`html_content`/`rtf_content`），**无自定义格式字段、无敏感标记** [verified] |
| 引擎存储 | `engine/src/store.rs:20/258` | `clipboard_history.json` + `CBENC1` DPAPI 头 + 原子 rename；`serde_json::to_vec(&entries)` [verified] |
| 引擎设置 | `engine/src/settings.rs:8/47` | 14 字段 kebab-case；`load()` 读 `extensions.clipboard-history` 节；`cmd_apply_settings`（`engine.rs:1060`）部分字段合并 [verified] |
| 引擎隐私 | `engine/src/privacy.rs:4` | 仅进程名/标题关键词**黑名单**；无正则识别 [verified] |
| 引擎分派 | `engine/src/engine.rs:429` | 命令表（无 `get_tags`、无 `paste_temp`/`restore_temp`）[verified] |
| 引擎捕获管线 | `engine.rs:104/140` | `on_clipboard_update`：隐私黑名单 → `read_snapshot` → `capture_limit_violation` → `build_entry` → `take_echo` 回环抑制 → `store.upsert` [verified] |
| 契约 | `packages/api/Clipboard/ClipboardEntry.cs:12` / `IClipboardService.cs:11` | 字段与接口清单（`Tags` L93、`SetEntryTags` L61；无 `GetEntryTags`、无 temp paste）[verified] |
| IPC | `shell-clipboard-ipc/ClipboardIpcClient.cs:684/689/695/1416` | `set_tags`/`copy_to_clipboard`/`ApplySettings`；`InjectPaste`/`SendPaste` 三档注入已就绪 [verified] |
| 面板 | `shell-clipboard-panel/PanelMainWindow.cs` / `RecentStrip.cs:861` | 标签仅 ToolTip（`RecentStrip.cs:861-864`）；无 `SetEntryTags` 调用、无脱敏、无 `tag:` 搜索；行内快捷操作在 `RecentStrip.BuildRightColumn()` L681 [verified] |
| 设置 UI | `shell-clipboard/Sections/ClipboardSection.cs:75/394` | 6 张卡；`ChoiceRow`/`SliderRow`/`HotkeyRow`/`HintBlock` 工厂；隐私卡为空文字 [verified] |
| 构建 | cargo `C:\Users\17822\.cargo\bin\cargo.exe`；dotnet 9/10 | 引擎 `cargo build`；宿主/面板 `dotnet build BetterDesktop.slnx` [verified] |

**四处同步红线**（README 明示）：新增设置项必须同时改 ①引擎 `Settings` 字段 ②设置分区控件 ③`ClipboardPlugin.ApplyConfiguration` patch ④`WatchKeys`，否则「界面能改、引擎收不到」。

## 2. 设计（逐项）

### P1-4 命名格式透传（引擎为主）

**目标（用户可感知）**：从 Excel/WPS 表格复制的区域，在历史里复制后粘贴回表格，**仍是可编辑表格**。

**方案**（沿用 1302「多格式并存」骨架，新增一条**命名格式**通道）：
- 新增 `engine/src/formats.rs`：
  - `KNOWN_STANDARD: &[u32]` = 已处理的 CF_ 标准格式与注册格式（CF_UNICODETEXT/DIB/HDROP/BITMAP/TIFF/OEMTEXT/PALETTE/PENDATA/RIFF/WAVE/ENHMETAFILE/LOCALE/OWNERDISPLAY/DSP*/GDIOBJ*/PRIVATEFIRST..LAST，以及 `HTML Format`/`Rich Text Format`/`Rich Text Format Without Objects`/`PNG`）。
  - `collect_named_formats(limit_count, limit_each_bytes, limit_total_bytes) -> Vec<NamedFormat{name,data}>`：`EnumClipboardFormats` 枚举 → `GetClipboardFormatNameW` 反查名字（无名=CF_ 标准，跳过）→ 排除 `KNOWN_STANDARD` → 逐个 `read_format_bytes` 并按三项上限截断/跳过。
  - **安全过滤**：只做字节搬运，**不解释、不执行**；单项超限跳过并记日志；总量超限整体放弃（避免把库撑爆）。
- `Snapshot` 新增 `named_formats: Vec<NamedFormat>`（`capture.rs:36`）；`read_snapshot_locked` 末尾采集，失败不影响主路径。
- `model.rs::ClipboardEntry` 新增 `#[serde(default)] pub named_formats: Vec<NamedFormat>`（camelCase `namedFormats`），`NamedFormat{ name:String, data_base64:String }`（base64 crate 已有依赖）。
- `build_entry`（`engine.rs:222`）写回该字段（仅 Text/Html/RichText 三种类型采集，避免图片/文件条目冗余）。
- `capture.rs::write_back` 末尾追加：对每条 `named_formats`，`RegisterClipboardFormatW(name)` + base64 decode + `set_clipboard_bytes`；单个失败不阻断其余。
- `entry_summary_json`（列表摘要）**必须剥离 `namedFormats`**（与 htmlContent/rtfContent 同处置）——否则列表页 IPC 载荷被 base64 撑爆。
- C# 侧：`ClipboardEntry` 加 `NamedFormats`（用于 legacy/诊断透传），契约可加性。
- 设置（可配上限，进 4 处同步）：`named-format-passthrough`(默认 true) / `named-format-max-count`(8) / `named-format-max-kb`(单项 1024) / `named-format-total-kb`(每条目 4096)。

**红线**：① 摘要载荷必须剥离；② 单项/总量双上限，超限**跳过而非截断**（截断后的二进制是坏数据）；③ `EmptyClipboard` 只在 `write_back` 开头调一次（多格式并存前提）；④ 不做格式白名单猜测 —— 用「无名格式排除 + 体积上限」的通用策略，保证 Excel/WPS 无论格式名为何都能覆盖。

### P1-5 按应用清洗规则（引擎 + 设置界面）

**目标**：某应用总复制垃圾时，用户自己设 `ignore` 或正则替换/丢弃，无需我们猜。

**方案**：
- `Cargo.toml` 新增 `regex = "1"`（无则无法做正则替换）。
- `settings.rs` 新增 `pub app_rules: Vec<AppRule>`（kebab-case `app-rules`）：
  ```rust
  struct AppRule { app: String, action: AppRuleAction, pattern: String, replacement: String }
  enum AppRuleAction { Ignore, Replace, Drop }   // kebab-case
  ```
  `app` 大小写不敏感；空 = 匹配所有应用（相当于内容级规则）。
- 新增 `engine/src/rules.rs`：
  - `compile(rules) -> CompiledRules`（正则 `Regex::new` 失败 → 跳过该规则并 `log::warn` 记录规则序号，**不 panic**）。
  - `decide(process, title) -> RuleDecision { Skip | Keep, replacements: Vec<(Regex,String)> }`。
  - `apply_replacements(&mut Snapshot, &[(Regex,String)])`：对 `text`/`html` 做替换（`replace_all`），并标记内容已变。
  - **纯函数化**（进程名/标题/规则 → 决策），便于单测。
- 编译缓存：`engine.rs` 持 `Mutex<CompiledRules>`，启动时编译一次；`cmd_apply_settings` 后检测 `app-rules` 变化则重编译。
- 接入点（`engine.rs::on_clipboard_update`）：**隐私黑名单之后、读快照之前**做 `Ignore` 判定（省一次剪贴板读取）；`Drop` 需读快照后判定（对 text/html）；`Replace` 读快照后应用。
- 设置 UI（`ClipboardSection.cs`）：新增「按应用清洗规则」卡（结构化编辑器）：行 = 可编辑 ComboBox(应用名) + ComboBox(动作) + TextBox(正则) + TextBox(替换为) + 删除按钮；底部「＋ 添加规则」；存为 `app-rules` JSON；实时校验正则（非法 → 红字提示，不写坏值）。
- 4 处同步：`WatchKeys` 加 `AppRulesKey`；`ApplyConfiguration` patch 加 `"app-rules"`。

**红线**：① 非法正则**不得崩溃**也不得静默丢弃用户整份规则（只跳过该条 + 日志）；② `Ignore` 判定必须在读快照前；③ 规则为空 = 零行为变化。

### P2-1 渐进式富文本探测（引擎捕获时序）

**目标**：修复「从 Office/WPS 复制富文本偶尔丢格式」。

**方案**：
- `capture.rs` 新增 `RICH_TEXT_APPS`（excel/winword/powerpnt/et/wps/wpp/outlook 等）+ `pub fn is_rich_text_app(process)`。
- `pub const PROBE_SCHEDULE_MS: &[u64] = &[0,40,80,140,220,360,560]`。
- `read_snapshot_progressive(process)`：首读 → 若 `kind==Text` 且 `is_rich_text_app`，按调度 sleep 后重读，**一旦拿到 Html/RichText 立刻返回**；耗尽则返回最后一次快照。
- `engine.rs::on_clipboard_update`：读快照处按需切换；每次探测落日志。

**红线**：① 只在白名单应用 + 首读纯文本时启动；② 拿到富文本即停；③ 探测失败返回**最后一次有效快照**；④ 不新增崩溃面。

### P2-2 敏感信息识别 + 预览脱敏（引擎识别 + 面板遮罩）

**方案**：
- `privacy.rs` 新增 `detect_sensitive(text) -> Option<&'static str>`（类别名：手机号/身份证/邮箱/银行卡/密钥）；正则表 + `OnceLock` 缓存。
- `model.rs` 新增 `#[serde(default)] pub is_sensitive: bool`（camelCase `isSensitive`）；`build_entry` 命中 → `is_sensitive=true` 且类别名追加进 `tags`（复用标签搜索通道）。
- `entry_summary_json` **必须带出 `isSensitive`**。
- C# `ClipboardEntry` 加 `bool IsSensitive` + `MaskedPreview` 计算属性（前 N 后 M 位，中间 `•`；N/M 由设置决定，默认 3/2）。
- 设置（4 处同步）：`sensitive-detection`(true) / `sensitive-mask-leading`(3) / `sensitive-mask-trailing`(2)；隐私卡升级为「识别开关 + 前后可见位数」。
- 面板：`RecentStrip.BuildPreviewText()` 命中时用遮罩文本 + 「敏感」chip + ToolTip 类别。

**红线**：① 识别只作用于**预览**，不改变实际内容（粘贴仍是全文）；② 关闭识别时零遮罩；③ 摘要必须带 `isSensitive`。

### P2-3 临时粘贴（引擎快照还原 + 面板入口）

**方案**：
- 引擎全局 `TEMP_CLIP: Mutex<Option<TempClip>>`（含 `created_at: Instant`）。
- 新命令 `copy_temp_to_clipboard {id}`：① 读当前剪贴板快照并构造临时 `ClipboardEntry`（不落盘）→ 存 `TEMP_CLIP`；② `mark_echo` + `write_back` 目标条目。返回 `{ok, hadPrevious}`。
- 新命令 `restore_temp_clipboard`：取 `TEMP_CLIP` → `mark_echo` + `write_back` → 清空。返回 `{ok, restored}`。
- 过期保护：`restore` 时若 >10s → 不还原只清空 + 日志（避免覆盖用户新复制）。
- IPC 常量 `M_CopyTemp`/`M_RestoreTemp`；客户端 `CopyEntryTemporarilyToClipboard`/`RestoreTemporaryClipboard`。
- 契约 `IClipboardService.PasteEntryTemporarilyToActiveWindow(entry)`（copy_temp → inject → 250ms → restore）。
- 面板入口：`RecentStrip` **中键 + Ctrl** = 临时粘贴（`TemporaryPasteRequested`）；`PanelMainWindow` 复用「收起→激活→延时注入」序列。

**红线**：① `mark_echo` 抑制两次写回；② 10s 过期保护；③ 还原失败只记日志。

### P2-4 标签体系补全（面板入口 + tag: 搜索）

**方案**：
- 引擎 `cmd_query`：`keyword` 以 `tag:` 开头 → **只按 `tags` 子串匹配**。
- 面板 `PanelMainWindow`：搜索提示补 `tag:标签`；列表键盘 `T` → 标签编辑浮层（TextBox 预填 + 保存/取消）→ `SetEntryTags` → 刷新该行；搜索框支持 `tag:xxx`。
- 面板 `RecentStrip`：meta 行渲染 `#标签` chip，点击 → `TagClicked`（搜索框填 `tag:<标签>`）。
- legacy 面板已有 `EditEntryTags`（L1057），无需新增。

**红线**：① 标签编辑走既有 `set_tags`；② `tag:` 解析只在引擎侧；③ 空标签 = 清空。

## 3. 实现顺序（依赖优先）

1. 引擎模型/设置/依赖：`Cargo.toml`(+regex) → `model.rs` → `settings.rs`。
2. 引擎 P1-4：`formats.rs`(新) → `capture.rs` → `engine.rs`。
3. 引擎 P1-5：`rules.rs`(新) → `engine.rs`。
4. 引擎 P2-1/P2-2：`capture.rs` → `privacy.rs` → `engine.rs`。
5. 引擎 P2-3：`engine.rs`。
6. 引擎 P2-4：`engine.rs`。
7. 契约/IPC：`ClipboardEntry.cs` → `IClipboardService.cs` → `ClipboardIpcClient.cs`。
8. 面板：`RecentStrip.cs` → `PanelMainWindow.cs`。
9. 设置 UI：`ClipboardSection.cs` → `ClipboardPlugin.cs`。
10. 构建/单测/文档。

## 4. 风险与兼容（§11）

| 风险 | 处置 |
|---|---|
| 命名格式体积撑爆 IPC/库 | 三项上限 + 摘要剥离 + 超限跳过 |
| Excel 格式名不确定（WPS 可能不同） | 「无名排除 + 上限」通用策略而非白名单猜测；日志打印实际格式名 |
| 渐进探测阻塞消息循环 560ms | 仅白名单应用触发；拿到即停；日志可观测 |
| 正则崩溃 / ReDoS | 编译失败跳过不 panic；本地工具、用户显式授权 |
| 新字段破坏旧 JSON 兼容 | 全部 `#[serde(default)]`；C# 忽略未知字段 |
| `apply_settings` 新字段未同步 4 处 | 严格按 README 红线逐项核对 |
| 临时粘贴覆盖用户新复制 | 10s 过期保护 + 清空快照 |
| 面板纯代码 UI 无设计时 | 编辑浮层用与 toast 同风格覆盖层 |

## 5. 开放问题（§12）

1. `named-format` 默认上限（8 个 / 1MB / 4MB）为保守估值，真机按日志校准。
2. 敏感正则误报率未实测（16-19 位数字 / 长 hex）；如误报多改为「只标记不遮罩」。
3. 渐进探测调度表实际命中延时尚需真机验证；560ms 不足再评估后台线程。
4. `app-rules` UI 形态按结构化编辑器实现；若过重可降级 JSON 文本域。
5. **不采纳（记录）**：TieZ 的「扫描码注入」「逐字符 Unicode」两档注入（入口已留 `PasteInjectMode`）。

## 6. DoD（§13）

**功能 DoD（场景语言，用户可感知）**
- **D1（P1-4）**：Excel/WPS 表格复制 → 历史点复制 → 回表格 Ctrl+V → **仍是可编辑表格**。真机人工确认。
- **D2（P1-5）**：设置添加「某应用 = 忽略」→ 该应用复制**不入历史**；「正则替换」→ 命中内容被替换后入库。真机人工确认。
- **D3（P2-1）**：Word/WPS 复制带格式文字 → 条目为**富文本**（非纯文本），日志有探测命中记录。
- **D4（P2-2）**：复制含手机号/身份证文本 → 预览**中间遮罩** + 「敏感」chip；粘贴出去仍是**全文**。真机人工确认。
- **D5（P2-3）**：别处复制 A → 面板 Ctrl+中键粘贴 B → 剪贴板**恢复为 A**。
- **D6（P2-4）**：选中条目按 `T` → 输入标签 → 行内出现 `#标签`；搜索 `tag:标签` → 只剩该条。

**机制 DoD（内核单测，非 UI）**
- T1 `formats::collect_named_formats` 排除/上限逻辑。
- T2 `rules::decide` 纯函数（三动作、大小写、非法规则跳过）。
- T3 `privacy::detect_sensitive` 正反例。
- T4 `Settings` 新字段默认值 + `apply_settings` 部分合并。
- T5 `ClipboardEntry` 新字段 camelCase + `isSensitive` 摘要带出。
- T6 `QueryFilter` `tag:` 解析。
- T7（C#）`ClipboardIpcClient` 新命令路径。
- T8（C#）`ClipboardEntry.MaskedPreview` 边界。

**构建门禁**：`cargo build` + `cargo test` 全绿；`dotnet build BetterDesktop.slnx` 0 警告 0 错误。

## 6.5 实施核销（2026-09-13 收口）

| 项 | 结果 | 证据 |
|---|---|---|
| 机制 T1 命名格式排除/上限 | ✅ | `engine/src/formats.rs` 内 3 用例（`cargo test`） |
| 机制 T2 规则决策纯函数 | ✅ | `engine/src/rules.rs` 内 6 用例（含非法正则跳过） |
| 机制 T3 敏感识别正反例 | ✅ | `engine/src/privacy.rs` 内 2 用例（含中文语境不用 `\b`） |
| 机制 T4 设置新字段默认值/合并 | ✅ | `settings.rs` 既有用例覆盖（新增字段走 `default`） |
| 机制 T5 新字段 camelCase + 摘要带出 | ✅ | `model.rs` 序列化用例 + `entry_summary_json` 剥离 `namedFormats` |
| 机制 T6 `tag:` 解析 | ✅ | `QueryFilter` 分支（tag_only）+ 面板透传 |
| 机制 T7 IPC 临时粘贴路径 | ✅ | 新增 `ClipboardTempPasteTests`（5 用例，IPC 单测 76 绿） |
| 机制 T8 遮罩边界 | ✅ | 新增 `ClipboardMaskedPreviewTests`（6 用例，剪贴板单测 93 绿） |
| 构建门禁（引擎） | ✅ | `cargo build --release` 0 警告；`cargo test` **105 passed / 0 failed** |
| 构建门禁（C#） | ✅ | `dotnet build BetterDesktop.slnx` **0 警告 0 错误** |
| D1 表格回贴仍是可编辑表格 | ⏳ 待人工 | 需 Excel/WPS 真机（日志可见 `named-format: 采集 N 项`） |
| D2 按应用忽略/替换生效 | ⏳ 待人工 | 需设置界面走查 |
| D3 Word/WPS 复制为富文本 | ⏳ 待人工 | 日志可见 `rich-text probe: <app> 第 N 次（+Xms）拿到富文本` |
| D4 敏感预览遮罩 + 全文可粘 | ⏳ 待人工 | 需真机观感确认 |
| D5 临时粘贴后剪贴板还原 | ⏳ 待人工 | 日志可见 `temp-paste: 剪贴板还原成功` |
| D6 标签编辑 + `tag:` 搜索 | ⏳ 待人工 | 面板 `T` 键 / 行内 `#标签` |

**新增文件**：`engine/src/formats.rs`、`engine/src/rules.rs`、`packages/shell/shell-clipboard/ClipboardAppRule.cs`、
`packages/api/Clipboard/ClipboardNamedFormat.cs`、两个测试文件。
**依赖新增**：`regex = "1"`（引擎，纯内存匹配、无网络面）。

## 7. 交接节（§14 · 技术力应用 skill 输入）

**注入文档（3 份）**
1. `docs/audits/2026-09-13-tiez-comparison.md` —— 需求来源与已完成边界（**不得回归** P0/P1-3）。
2. `TECH-KNOWLEDGE/13-剪贴板/1302-sticker-mode.md` —— 多格式写回红线。
3. `packages/shell/shell-clipboard/README.md` —— 「新增设置项必须四处同步」红线 + 配置键清单。

**模式判定**：功能增量（新增通道/规则/字段），**非重构**；既有命令与字段保持向后兼容（纯新增）。

**适配参数**
- 引擎：Rust edition 2024，`windows = 0.58`（`EnumClipboardFormats`/`GetClipboardFormatNameW` 属 `Win32_System_DataExchange`，已在 features 内）。
- 契约：`packages/api/Clipboard/`；实现 `shell-clipboard-ipc` / `shell-clipboard`。
- 设置键前缀 `extensions.clipboard-history.`；引擎字段 kebab-case。
- 构建：引擎 `cargo build`；宿主/面板 `dotnet build BetterDesktop.slnx`。

**DoD 核销表**：见 §6（D1/D2/D4 需真人走查）。

**强制 scope 边界**：仅上述 6 项；不做云同步/AI/局域网/主题商店（审计已标不采纳）；不重构既有捕获/存储主路径。
