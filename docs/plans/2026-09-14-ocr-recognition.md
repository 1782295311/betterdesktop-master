# OCR 识别（剪贴板扩展）实施计划（2026-09-14）

> 用户需求（2026-09-14 拍板）："截图后可以用 OCR 识别选项，然后截图和识别结果全部保存进剪切板历史，所以这两个是剪切板的扩展功能。"
> 状态：设计已定，待实施。截图侧见 [2026-09-14-screen-capture-clipboard-extension.md](2026-09-14-screen-capture-clipboard-extension.md)；总索引见 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)。

## 0. 判位

OCR 是剪贴板能力的扩展，识别结果**属于图片条目**而不是另立一条文本：把 OCR 文本挂到图片条目上，图片就能被文字搜索命中，这是本功能相对"另一个 OCR 工具"的全部差异化。因为剪贴板引擎默认档把图片原图落盘（`storage-mode=full`），**历史里任意图片条目都能补做 OCR**，不只"刚截的那张"。

## 1. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 条目模型 | `engine/src/model.rs`（`struct ClipboardEntry`） | 字段含 `content` / `html_content` / `rtf_content` / `image_path` / `tags` / `source_process_name` / `file_paths`，**无 OCR 文本字段** |
| 关键字命中 | `engine/src/engine.rs:741` | `keyword_hits`：子串、忽略大小写，覆盖正文 / HTML / 标签 / 来源进程 / 文件路径 |
| 查询下沉 | `engine/src/engine.rs:616` | 搜索与筛选已下沉引擎（keyword/kind/sourceApp/pinned），客户端不再本地过滤 |
| 条目后置更新先例 | 引擎 IPC 动词面 | `set_tags` 是"捕获后更新既有条目"的既有先例，`set_ocr_text` 与之同形 |
| 现有 OCR 代码 | `packages/shell/shell-convert/Services/Engines/TesseractEngine.cs:1,107` | 外部 `tesseract.exe`，`chi_sim+eng`、`--psm 6`，**图片→txt 的文件转换引擎**，无框选、无版式、无屏幕语义 |
| 契约冻结 | `packages/api/Clipboard/IClipboardService.cs`、`docs/plans/2026-09-09-clipboard-public-api.md` | 契约与协议单点维护；新增字段与动词需走契约变更流程 |
| 原生承载判据 | [../cross-language/原生重写候选评估.md](../cross-language/原生重写候选评估.md) | 语言不是收益来源；热路径与窄切面判据决定落点 |

## 2. 引擎三层与归属

| 层 | 引擎 | 定位 | 依赖 |
|---|---|---|---|
| L1 默认 | `Windows.Media.Ocr` | in-box、离线、零模型、中英、词级边框、1080p 区域约 50–150 ms | TFM `net8.0-windows10.0.19041.0` 已带 WinRT 投影 |
| L2 精度 | PP-OCRv5（ONNX + DirectML EP） | 中文精度档，含竖排与表格结构；模型 Apache-2.0 | 新增 ONNX Runtime 依赖 + 模型分发（dist 体积 +20–40 MB） |
| L3 加速 | Windows AI 文本识别（Copilot+ NPU） | 有 NPU 时首选 | Windows App SDK 侧能力，按设备可用性探测 |

外部 `tesseract` 保留在文件转换链路，**不作为屏幕 OCR 后端**：其版式与中文精度不足以支撑"截图即识别"，且外部进程启动成本高于前两层。

**归属：OCR 能力放常驻 Agent（C#），不放进剪贴板引擎。** 理由：三个引擎档（WinRT in-box、DirectML 托管包、NPU 的 Windows App SDK 能力）都在 C#/WinRT 面可用；放进 Rust 引擎会让 L1/L3 两档要么不可用、要么再引一套 WinRT 绑定。引擎侧只承担"存字段 + 可搜索"。

## 3. 数据与契约改动

### 3.1 引擎（Rust）

| 改动 | 内容 |
|---|---|
| 模型 | `ClipboardEntry` 新增 `ocr_text: String`（serde 默认空串，旧文件向后兼容） |
| 动词 | 新增 `set_ocr_text { id, text }`，与 `set_tags` 同形同鉴权面；返回更新后的条目摘要 |
| 搜索 | `keyword_hits` 命中面加入 `ocr_text`；不新增查询参数（沿用 `keyword`） |
| 载荷 | 列表摘要与 `get_entry` 均带 `ocrText`（图片条目的 OCR 文本是用户可见事实，需要随条目走） |
| 版本 | 协议字段只增不改；`ping` 返回的版本号用于诊断（协议协商仍未实现，保持现状） |

### 3.2 C# 契约

| 改动 | 内容 |
|---|---|
| `packages/api/Clipboard/ClipboardEntry.cs` | 新增 `OcrText` 属性（对齐引擎字段） |
| `packages/shell/shell-clipboard-ipc/` | `ClipboardIpcClient` 新增 `SetOcrTextAsync(id, text)` 与摘要映射 |
| 新增 `packages/api/Ocr/IOcrService.cs` | 见下 |

```csharp
public enum OcrEngineKind { WinRtInBox, PpOcrOnnx, WindowsAiNpu }

public sealed record OcrWord(string Text, PixelRect Box, float Confidence);
public sealed record OcrBlock(string Text, PixelRect Box, int LineCount);
public sealed record OcrResult(
    bool Success, string Text, IReadOnlyList<OcrBlock> Blocks, IReadOnlyList<OcrWord> Words,
    OcrEngineKind EngineUsed, string? DegradeReason);

public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(string imagePath, OcrOptions options, CancellationToken ct);
    Task<OcrResult> RecognizeEntryAsync(string entryId, OcrOptions options, CancellationToken ct);
}
```

`RecognizeEntryAsync(entryId)` 由 OCR 服务经剪贴板客户端取条目图片路径后识别、再回写 `set_ocr_text`——**调用方不需要碰引擎**，面板与 capture exe 都只依赖 `IOcrService`。

## 4. 后处理（决定"可用"与"最优"的分界）

| 处理 | 内容 | 为什么 |
|---|---|---|
| 版式重建 | 段落合并、多栏切分、表格（行×列）→ Markdown 表 | 直接输出断行碎片是"能识别"，不是"能用" |
| 竖排与中英混排 | 竖排中文断行、中英之间去多余空格、标点归一 | 中文场景的高频投诉点 |
| 置信度 | 阈值以下保留原词但标记低置信；UI 以底色提示 | 让用户知道哪里可能错，而不是静默给错字 |
| 条码 | 二维码/条形码单独识别通道（与文字识别并行） | 截图工具的刚需，且比 OCR 快得多 |
| 去噪 | 屏幕伪影行（选中高亮、光标、抗锯齿边缘）过滤 | 屏幕文字与扫描件属性不同 |

## 5. 入口与交互

| 入口 | 交互 |
|---|---|
| 截图工具条 | 截图确认后点"OCR"→ 识别 → 结果面板（可复制/可编辑/可写回条目） |
| 剪贴板面板条目 | 图片条目行内"识别文本"动作；已识别过的条目显示文本摘要，点击展开 |
| 搜索 | OCR 文本参与 `keyword` 命中，搜文字能命中图片条目 |
| CLI | `BetterDesktop.Cli`：对图片路径 headless 输出文本，供脚本与后续 AI 控制面消费 |

失败语义：引擎不可用、无语言包、图片不存在、超时——每种都给可读原因（fail-visible），**绝不返回空文本冒充成功**。

## 6. 红线

- **离线**：识别全本地，不上云、不发网络请求。
- **不落盘副本**：识别只读条目已落盘的图片，不复制图片、不新建缓存目录（缩略图与结果文本除外）。
- **不改图片语义**：识别不修改 `image_path`、不改变条目类型与分类。
- **路径档限制必须明说**：`storage-mode=paths-only` 时原图不落盘，只能对缩略图识别，精度下降——UI 明示并允许改档。
- **隐私**：OCR 文本可能含敏感信息，默认不写诊断日志；需要取证时只记长度与耗时。
- **不做的**：手写体专项、公式转 LaTeX、云端纠错、翻译（翻译属独立能力）。

## 7. 实现顺序

1. 引擎：`ocr_text` 字段 + `set_ocr_text` + `keyword_hits` 覆盖 + Rust 单测（含旧文件兼容）。
2. C#：契约字段 + 客户端方法 + `IOcrService` 契约与 L1（`Windows.Media.Ocr`）实现。
3. 面板与截图工具条接线（识别、结果展示、写回条目）。
4. 后处理（版式、竖排、去噪、条码）。
5. L2（ONNX + DirectML）与 L3（NPU）接入 + 引擎优先级链与降级可见。
6. 准确率回归集与门禁（见下）+ 真机走查。

## 8. 验收：准确率回归集与门禁

- 语料：不少于 60 张真实截图，覆盖中英混排 / 纯中文 / 纯英文 / 表格 / 竖排 / 低对比 / 深色主题 / 高 DPI 缩放 / 截图边缘截断。
- 指标：字符准确率（CER）与整行准确率，按语料分类出表；每档引擎各出一列。
- 门禁：`scripts/verify-ocr-accuracy.ps1` 注册进 `scripts/run-gates.ps1`，基线冻结后回归超阈值即红——与 [../product-quality.md](../product-quality.md) 的性能预算同属"可交付指标"。
- 预算：单次识别 1080p 区域 ≤300 ms（L1，热机）；不达标不得默认启用该档。

## 9. DoD

功能 DoD（真机）：

- D1：截图 → 工具条点 OCR → 文本挂到同一图片条目；面板里该条目显示文本摘要；**用文本搜索能命中这张截图**。
- D2：对历史里任意图片条目（含从浏览器复制的图片）执行识别 → 成功并写回。
- D3：无语言包 / 引擎缺失时给出可读原因，不返回空文本。
- D4：`paths-only` 档下识别路径与提示正确。
- D5：低置信区域有可视标记。

机制 DoD（机检）：

- T1：`set_ocr_text` 往返（写入→摘要→查询命中）；旧库无字段时向后兼容。
- T2：`keyword_hits` 覆盖 OCR 文本（含大小写与子串边界）。
- T3：`IOcrService` 引擎优先级链与降级原因判定（纯函数 + 假引擎）。
- T4：后处理纯函数（多栏切分、竖排断行、中英空格归一、表格→Markdown）。

构建门禁：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；`cargo test`（引擎）全绿；准确率门禁达基线。

## 10. 交接节

**注入文档**：本计划（决策与红线）；[2026-09-11-clipboard-engine-rust-ipc.md](2026-09-11-clipboard-engine-rust-ipc.md)（引擎协议与存储面）；[2026-09-09-clipboard-public-api.md](2026-09-09-clipboard-public-api.md)（契约变更流程）。

**模式判定**：能力增量 + 引擎小改（字段/动词/搜索覆盖）；不改既有动词语义，不做协议协商。

**强制 scope 边界**：只做"图片 → 文本 → 挂回条目 → 可搜索"；不做翻译、不做云、不做公式/手写体专项、不新增独立 OCR 工具链。

---

## 11. 实施核销（2026-09-15，执行：技术力应用）

> 证据均为本机真机实测。GUI 点击路径无法在 headless 会话实走，已如实标注。

### 功能 DoD（真机，在线引擎实测）

| DoD | 状态 | 验证证据 |
|---|---|---|
| D1 截图→OCR→挂同一条目→搜索命中 | **通过（链路实走）** | 合成图 900×260 入库（id b8cc0c88）→ `WindowsMediaOcr` 识别 22 字符（engine=WinRtInBox）→ `set_ocr_text` 挂回**同一 id** → keyword 搜索 `hit=True`；真实截图 2560×1440 入库 → OCR 550 字符挂回 → autoquery（取 OCR 首词）`hitSelf=True`。代码接线在案：CaptureFlow.AttachOcrToEntryBestEffort（轮询 get_last ≤6s 匹配尺寸 → SetEntryOcrText）+ OcrPanelWindow.ResultCaptured。工具条 GUI 点击路径待用户真机走查 |
| D2 历史任意图片条目识别 | **通过（链路）** | 与 D1 同链路（对任意已入库图片条目识别→写回）；GUI 入口待真机 |
| D3 无语言包/引擎缺失可读原因 | 通过（机制） | `OcrOptions.TimeoutMs=10000` + `DegradeReason`；集成测试含占位跳过路径 |
| D4 paths-only 档识别 | **未实现** | `storage-mode=paths-only` 处理逻辑缺失；红线要求 UI 明示并允许改档，未交付 |
| D5 低置信区域可视标记 | **未实现** | 依赖后处理（T4），未交付 |

### 机制 DoD（机检）

| DoD | 状态 | 验证证据 |
|---|---|---|
| T1 set_ocr_text 往返+向后兼容 | 通过 | e2eprobe 实测：动词存在于在线引擎（不存在 id → "entry not found"）；写入→query 摘要回读 ocrLen=22/550；新增字段默认空（旧库兼容） |
| T2 keyword_hits 覆盖 OCR 文本 | 通过 | query keyword=BETTERDESKTOP-E2E-8842 → total=1 命中；真实截图 OCR 首词 autoquery 命中 |
| T3 引擎优先级链与降级原因 | 通过（L1 部分） | WindowsMediaOcr 集成测试真实识别（SkiaSharp 生成 PNG）；无语言包占位跳过 |
| T4 后处理纯函数 | **未实现** | 契约层有 OcrWord.Confidence / OcrBlock（数据就绪），多栏切分/竖排断行/中英空格归一/表格→Markdown 实现体缺失（计划 §7 步骤 4 未交付）；**专项单测待补** |

### 构建门禁

`dotnet build BetterDesktop.slnx` 0 警告 0 错误；引擎 `cargo test` 108/108；`dotnet test`（capture-tests）21/21。

### 交付范围声明（诚实口径）

已交付：引擎字段/动词/搜索覆盖（T1/T2）、C# 契约 + L1 引擎 + OCR 面板 + CaptureFlow 挂条目接线（D1/D2/D3 机制）、部署上线（引擎 release 替换在线旧版并拉起）。
未交付（属计划 §7 步骤 4-6，留待后续轮次）：后处理（T4/D5）、paths-only 处理（D4）、L2/L3 引擎、准确率回归集与门禁（§8）。
