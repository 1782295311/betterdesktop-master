# Cairo 开发计划 · 剪贴板「表情包模式」（用户自填动图，跨平台粘贴）

> Task：给剪贴板历史新增一个**「表情包」类别**——用户主动把动图（GIF/WebP/APNG…）填进去保存，之后在**任何平台**（微信/QQ/浏览器/编辑器）粘贴使用。
> 需求原话（2026-09-12 用户）："还要再加一个类别，还需要用户自己往里面填入，即表情包模式，可以把保存的动图存在里面，留到任何平台粘贴使用"。
> 基线：`docs/plans/2026-09-11-clipboard-engine-rust-ipc.md`（引擎化 S1-S33 已收口，Rust 82/82、IPC 20/20、契约 87/87、全仓 0 警告 0 错误）。
> 技术库命中：`1301-clipboard-history`（L2，剪贴板历史红线 + Rust 变体）。**未命中**：表情包/动图珍藏与跨应用粘贴（新建文档）。

## 1. Objective（场景语言）

1. **用户主动填入**：面板里点「＋ 表情包」→ 选一个或多个动图文件 → 立即出现在「表情包」类别下，长期保存。
2. **看得见**：面板 chips 新增「表情包」；条目行显示**动图首帧缩略图** + 尺寸/文件名，一眼能认。
3. **任何平台粘贴**：点条目（或双击/合并粘贴/按序粘贴）→ 在微信 / QQ / 浏览器 / 编辑器里 Ctrl+V 能贴出内容；**动图不失动画**（走文件路径），接受"仅支持位图的应用拿到首帧静帧"的降级。
4. **不会被清掉**：表情包是用户珍藏 —— **不参与**过期/容量驱逐，也**不被「清理未收藏」删除**（用户点的"清理"意图是清历史噪声，不是清珍藏）。

## 2. Current Behaviour（[verified] 现状锚点）

| 资产 | 现状 |
|---|---|
| `ContentCategory`（api/Clipboard/ContentCategory.cs） | Text=0 / Code=1 / RichText=2 / Image=3 / File=4 —— **无表情包** |
| `ClipboardItemKind` | Text=0 / Image=1 / Files=2 / Html=3 / RichText=4 —— 表情包本质是"文件"，可复用 `Files`（天然拥有 `file_paths` + CF_HDROP 写回） |
| 引擎存储（store.rs） | 图片 `clipboard\images\`、缩略图 `clipboard\thumbs\`、文件副本 `clipboard\files\{id}\`、HTML 提取图 `clipboard\html-images\` —— **无 stickers 目录** |
| 引擎图片解码（Cargo.toml） | `image = { version = "0.25", default-features = false, features = ["png", "jpeg"] }` —— **不能解 GIF/WebP**（缩略图与首帧都做不了） |
| 引擎写回（capture.rs write_back） | `Files` → `CF_HDROP`；`Image` → `PNG` + `CF_DIB` —— 表情包可复用前者，但**动图首帧静帧格式缺失** |
| 驱逐（store.rs evict） | 只对 `!is_pinned` 生效；`clear_unpinned` 同理 —— 表情包若走普通条目**会被清掉** |
| 面板 chips（PanelMainWindow） | `all/text/image/files/code/pinned`（按 `category` 维度，2026-09-12 S13 统一）+ 行内 `CategoryLabel` |
| 面板导入能力 | **无**任何"从文件导入"入口（现有条目全部来自剪贴板捕获） |

## 3. 关键决策（本计划的取舍，实现时以此为准）

| # | 决策 | 理由 |
|---|---|---|
| D1 | **表情包按"原文件珍藏"存储**：把用户选的文件复制到 `clipboard\stickers\{id}.{ext}`，条目 `content_type=Files` + `category=Sticker=5` + `file_paths=[副本绝对路径]` | 动图**只有原文件能保动画**。任何"转码成位图"的方案都会丢帧 → 与需求冲突 |
| D2 | **粘贴多格式齐发**：`CF_HDROP`（副本文件）+ `PNG`（原字节，PNG 家族）+ `CF_DIB`（首帧解码） | 目标应用自选：聊天软件取文件/HTML → **动图**；仅支持位图的编辑器 → 首帧静帧（可接受降级）。单一格式无法兼顾 |
| D3 | **内容哈希去重**：导入时 `sha256(文件字节)` 写入 `content_hash`，命中已有表情包则**跳过并提示**（不重复占空间） | 用户很可能重复导入同一张图；按路径去重无效（副本路径每次不同） |
| D4 | **驱逐与清理豁免**：`evict()` 与 `clear_unpinned` 一律跳过 `category == Sticker` | 用户珍藏的东西被后台静默删掉是最伤信任的失败模式（对齐 S24"用户的历史不能静默丢弃"口径） |
| D5 | **入口 = 面板内「＋ 表情包」按钮（多选文件）+ 现有图片/文件条目"存为表情包"** | "用户自己填入"最直接的形式；后者让"网上看到喜欢的图 → 先复制进历史 → 一键珍藏"闭环 |
| D6 | **不新建独立面板/网格视图**，复用现有列表 + 新增 chip | 用户对"花哨功能"明确反感（台前调度下线的教训）；列表 + 首帧缩略图已满足识别需求 |

## 4. Proposed Changes

### 4.1 引擎（Rust，数据面权威）
| 文件 | 改动 |
|---|---|
| `Cargo.toml` | `image` features 增 `"gif"`、`"webp"`（解码首帧；编码不需要） |
| `model.rs` | `Category` 增 `Sticker = 5`（`from_i32`/`as_i32` 同步）；`fingerprint()` 让 `content_hash` 非空时**优先**用它（Sticker 与图片共用内容指纹） |
| `store.rs` | 新增 `stickers_dir()`（`clipboard\stickers\`）；`remove_entry_files`/驱逐级联清理**必须包含 stickers 副本**；`evict()` 与 `clear_unpinned` 跳过 Sticker |
| `engine.rs` | 新 IPC `add_sticker`（`params: {paths:[...]}`）：校验扩展名白名单 + 存在 + 大小上限 → 算内容哈希去重 → 复制到 stickers → 建条目（含 `image_width/height` 解码首帧、缩略图 generation）→ 落盘 + 广播 `history_changed`；返回 `{ok, added, skipped, ids}` |
| `capture.rs` | `write_back` 对 `category == Sticker`：写 `CF_HDROP`（副本路径）+ `PNG`（原字节，若本身是 PNG）+ `CF_DIB`（GIF/WebP 解码首帧→DIB） |

### 4.2 契约与客户端（C#）
| 文件 | 改动 |
|---|---|
| `api/Clipboard/ContentCategory.cs` | 增 `Sticker = 5`（**只加不改**，旧值不动，JSON 数字向后兼容） |
| `api/Clipboard/IClipboardService.cs` | 新增 `StickerImportResult AddStickers(IEnumerable<string> filePaths)`（可加性纪律） |
| `shell-clipboard-ipc/ClipboardIpcClient.cs` | 实现 `AddStickers` → `add_sticker` IPC；`ClipboardIpcException` 透出失败原因 |
| `shell-clipboard/ClipboardManager.cs`（legacy 回退） | 实现 `AddStickers`：复制到 `clipboard\stickers` + 建条目（不抛 NotImplemented，保持后端等价） |

### 4.3 面板（C#）
| 位置 | 改动 |
|---|---|
| chips | 新增「表情包」chip（`category=Sticker` 过滤，走引擎侧过滤，不本地过滤） |
| 头部/工具条 | 新增「＋ 表情包」按钮 → `Microsoft.Win32.OpenFileDialog`（`Multiselect=true`，过滤 gif/webp/png/apng/jpg）→ `AddStickers` → toast 结果（成功 N 条 / 跳过 M 条重复） |
| 条目行（RecentStrip） | Sticker 条目：缩略图走引擎 `thumb`（GIF 首帧）、角标「动图」/「表情包」、副标题显示文件名与尺寸 |
| 行内菜单/操作 | 对 `File`（单文件且为图片扩展名）与 `Image` 条目提供「存为表情包」动作 |

## 5. 生死线（Constraints & Invariants）

1. **绝不转码**：原文件字节级复制，任何"压缩/转格式"都会毁掉动画（D1）。
2. **删除必须级联**：表情包条目的 stickers 副本、缩略图在条目删除/清理时一并删除，不留孤儿文件（对齐 S19 的"孤儿文件堆积"教训）。
3. **珍藏豁免**：`evict()` / `clear_unpinned` 不得删除 Sticker；新增任何"批量清理"路径都要包含这条豁免。
4. **契约只加不改**：`ContentCategory` 只追加值，`IClipboardService` 只加方法；旧 JSON（数字）反序列化不受影响。
5. **去重按内容**：同一文件重复导入必须命中已有条目（内容哈希），不得产生副本堆积。
6. **失败可见**：导入失败（不支持的格式/超大/读取失败/引擎不可达）必须逐个文件报出原因（toast + 日志），绝不静默跳过 —— 对齐 S32"失败必须可见"红线。

## 6. Testing Plan

| 层 | 用例 |
|---|---|
| Rust 单测 | `add_sticker` 落盘 + 条目字段（category/kind/file_paths/size/content_hash）；**内容哈希去重**（同文件二次导入 → skipped=1）；**扩展名白名单**拒绝（.exe/.txt）；**驱逐豁免**（capacity=1 + 导入表情包 → 表情包不被驱逐）；`clear_unpinned` 不删 Sticker；删除条目级联清理 stickers 副本 |
| C# IPC 单测 | `AddStickers` 请求字段 camelCase（`paths`）+ 返回解析（added/skipped/ids）+ 失败抛 `ClipboardIpcException` |
| 契约测试 | `ContentCategory.Sticker` 数值 = 5；`AddStickers` 存在于接口实现 |
| 真机走查 | 导入 GIF → 面板「表情包」出现 + 首帧缩略图可见 → 点条目复制 → 微信/记事本/浏览器粘贴验证（**动图在微信里应作为图片/文件发出**；若目标仅支持位图则为首帧） |

## 7. Risk

| 风险 | 级别 | 缓解 |
|---|---|---|
| 动图在特定应用里变成"文件卡片"而非动图 | 中 | 多格式齐发（D2）；真机走查覆盖微信/QQ/浏览器；若某应用不吃 `CF_HDROP`，退化为首帧位图（可用） |
| GIF 解码引入新依赖特性后体积/编译变化 | 低 | 只开解码特性，`lto=true` 已开；构建后核对产物体积 |
| APNG / 动图 WebP 只解首帧（image crate 限制） | 低 | 静止帧仍可用；文档明示（原文件收藏不受影响，粘贴走 CF_HDROP 仍是原动图） |
| 表情包占满磁盘（用户大量导入） | 低 | 单文件大小上限（复用 `file_copy_max_mb`）+ toast 提示失败原因；珍藏语义下不做自动清理（用户可手动删） |

## 8. §14 交接节（给实现 agent）

**注入清单**
- `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（红线 + Rust 变体章节：写回抑制、分帧、载入失败禁写、失败必须可见）
- 本计划 §3 决策表 + §5 生死线（实现中不得偏离）
- 代码锚点：`engine/src/store.rs`（evict/remove_entry_files/upsert）、`engine/src/capture.rs`（write_back/png_to_dib）、`engine/src/engine.rs`（dispatch/cmd_import）、`packages/api/Clipboard/ContentCategory.cs`、`packages/shell/shell-clipboard-ipc/ClipboardIpcClient.cs`、`packages/shell/shell-clipboard-panel/PanelMainWindow.cs`

**模式判定**：**扩展既有实现**（不新建模块）—— Sticker 复用 `Files` 的存储/粘贴通道，只新增"来源=用户导入"与"珍藏豁免"两条分支。

**适配参数**：扩展名白名单 `gif/webp/apng/png/jpg/jpeg/bmp`；单文件上限 = `file_copy_max_mb`（默认 64MB）；缩略图宽度 = `thumb_width`（默认 480）；sticker 目录 = `<root>\clipboard\stickers`。

**DoD 核销表（2026-09-12 实施完成）**
| # | 验收点 | 状态 | 证据 |
|---|---|---|---|
| D1 | 新增 `ContentCategory.Sticker=5` 且旧值不变、旧 JSON 可加载 | ✅ | `ContentCategory.cs` 只追加值；引擎 `Category::from_i32(5)`；全量 87 契约测试通过（旧数据加载链路未变） |
| D2 | 面板「＋ 表情包」可选多文件导入，成功/跳过/失败均有可见反馈 | ✅ | 底部动作区按钮 + `OpenFileDialog`（多选）→ `BuildStickerImportMessage` 三态 toast + 逐条错误写 `panel.log` |
| D3 | 「表情包」chip 能筛出全部表情包条目，行内有首帧缩略图 | ✅ | chips 加 `sticker`；`RecentStrip` 对 Sticker 走缩略图瓦片 + 大预览；`ClipboardImagePaths` 新增 Sticker 分支（thumb → 原文件） |
| D4 | 粘贴写回 `CF_HDROP` + `CF_DIB`（GIF/WebP 解码首帧），PNG 家族原字节透传 | ✅ | `capture::set_sticker_entry` 多格式齐发；`decode_first_frame_dib` 复用 `rgba_to_dib` |
| D5 | 表情包不参与 `evict`，也不被「清理未收藏」删除 | ✅ | `store::evict` 过滤器 + `cmd_clear_unpinned` 双双豁免；单测 `evict_never_drops_stickers` 锁死 |
| D6 | 重复导入同一文件命中内容哈希去重（不产生副本） | ✅ | 真机：首次 `added=1`，重跑 `skipped=1`；单测 `sticker_hash_lookup` + `sticker_category_and_fingerprint` |
| D7 | 条目删除时级联清理 stickers 副本与缩略图（无孤儿文件） | ✅ | 真机：删除后 stickers 目录空、缩略图消失；单测 `remove_sticker_deletes_copies_only`（**同时锁死"不删用户原文件"安全红线**） |
| D8 | Rust 单测全绿 + IPC/契约测试全绿 + 全仓 0 警告 0 错误 | ✅ | Rust **86/86**、IPC **22/22**、契约 **87/87**、`dotnet build BetterDesktop.slnx` 0 警告 0 错误 |
| D9 | 计划进度行 + 技术库功能文档 + 包 README 回写 | ✅ | 主计划 S34 行 + `TECH-KNOWLEDGE/13-剪贴板/1302-sticker-mode.md` + 三个包 README |

**实施记录（2026-09-12）**
- **自测抓到的真 bug（值得记）**：`Store::has_sticker_by_hash` 最初用裸 `sticker:{hash}` 做索引键，而索引里实际存的是 `sha256(payload)[..16]` —— 两处各写一遍 key 构造 → **导入判重永远查不到**（重复导入会持续堆积副本）。修法：抽出 `ClipboardEntry::sticker_fingerprint()` 作**单一真相源**，`fingerprint()` 与判重查询共用。教训：任何"同一逻辑两处实现"的键/标识构造，都要先合并成一处。
- **真机走查**（隔离探针，用完即删）：生成 80×80 GIF（1294B）→ `add_sticker` 首次 `added=1` / 重跑 `skipped=1` / `.txt` 被拒并回中文原因 → `query category=5` 返回 `contentType=2(Files)`、`category=5`、`contentHash=85bb21ae…`、`filePaths=[…\clipboard\stickers\….gif]`、`imageWidth/Height=80`（**GIF 解码生效**）→ `thumbs\.jpg` 3016B 生成 → **按 id 精确删除**（S30 红线）→ 条目归零、stickers 副本与缩略图**双双级联清理**。
- **未覆盖（需用户真机）**：把表情包粘贴进微信/QQ/浏览器验证"发出去的是动图"（探针无法代劳，取决于目标应用对 `CF_HDROP`/`CF_DIB` 的取舍）。
