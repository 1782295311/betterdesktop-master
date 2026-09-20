# shell-convert · 文档转换子系统

ConversionMatrix（源→目标矩阵）+ EngineRegistry（多引擎候选链解析/真实探测缓存）+ 泛化 ConversionService 驱动右键菜单的完整文件转换能力（计划：docs/plans/2026-09-03-conversion-matrix-markdown-hub.md）。

## 菜单形态（Win10 六红线，§6.3 形态 A）

- 第一层平铺高频「转 PDF」（矩阵含 pdf 且引擎可用时，多选显示数量）
- 「转换为 ▸」子菜单承载其余目标（白名单 Submenu：集合语义/动态列表）；上次成功目标置顶
- 多选整集：全 PDF ≥2 →「合并 PDF」；全图片 ≥2 →「合成 PDF」（第一层直达）
- 单 PDF 的「拆分 PDF（每页一文件）」走 Shift 扩展位（低频归宿是 Shift 扩展）
- 隐藏优先：无可用引擎的目标整项不显示

## 矩阵速览（节选）

> **2026-09-20 起：格式转换不再依赖第三方引擎。** 文档/表格/电子书族由 `convert-engine.exe` 的**进程内 lite 核心**完成（`native/convert-lite`，零外部 exe）；`engines\pandoc\` / `libreoffice\` / `calibre\` **已从仓库与发布物删除**（-2372MB，全量 dist 3035MB → 216MB）。路由由矩阵的 `Prefer` 决定，两侧同口径：`matrix.rs::route_through_lite` ↔ `ConversionMatrix.RouteThroughLite`。

| 源 | 目标（引擎主→兜底） |
|---|---|
| Word 族（doc/docx/docm/rtf/odt） | pdf/docx/odt/rtf/txt/html/epub/md（**Lite**）；`.wps/.wpt/.wpd` 是 lite 没有 reader 的源，仍指 soffice（该树已删 ⇒ 目标自动隐藏） |
| Excel 族（xls/xlsx/xlsm/et） | pdf/xlsx/ods/csv（**Lite**，csv 带 UTF-8 filter） |
| Presentation（ppt/pptx/pps/dps） | pdf/pptx/odp/html（**Lite**，pptx 族） |
| md（枢纽 IR） | html（纯托管 Markdig）；docx/pdf/pptx/epub 等（**Lite**） |
| 电子书/文档互转（epub/mobi/azw3/docbook/jats/tex/rst/org/rtf/odt/ipynb/mediawiki…） | 任意组合（**Lite** 的 `combo_convert`：A→md→B） |
| 图片（png/jpg/bmp/gif/tif/webp/heic/RAW） | 互转（webp 经 SkiaSharp；HEIC/RAW 经专用引擎解码）；合成 pdf（PdfSharpCore）；→文本（Tesseract OCR，需可选引擎） |
| pdf | png/jpg（Poppler，需可选引擎）、txt（Poppler/TextPdf）；合并/拆分/加密（PdfSharpCore/PdfSecurity） |
| 音视频 | **本轮摘掉**（D3①：不带 FFmpeg 依赖 ⇒ 菜单项隐藏；`engines\ffmpeg` 树保留备将来恢复） |
| 压缩包 | 解压（zip/rar/7z，内置 zip + 外部引擎，路径穿越防护）与压缩（ArchiveService） |

## 引擎分层

- **LiteEngine（2026-09-20 起的主力）**：进程内 Rust（`convert-engine.exe` 链入 `native/convert-lite`），**恒可用** —— 可用性不再取决于磁盘上有没有第三方目录。它**只负责可用性与路由**；真正的执行在 Rust 进程内（`ConversionService` 的正常路径直接调 `IRustConvertRunner`→`convert-engine.exe`）。
- ManagedEngine / TwoHopEngine：纯托管 md 链（零外部依赖）+ html 中转两跳
- ManagedImageEngine / PdfComposeEngine：图片互转（SkiaSharp 补 webp）与 PDF 组合
- **PopplerEngine / TesseractEngine**：保留清单里的外部引擎（PDF / OCR，lite 替代不了）；随 `06-可选引擎` 模块分发，缺失时**只有对应目标隐藏**（OCR/PDF 渲染），转换本身照常
- SofficeEngine / ComPdfEngine / PandocEngine / CalibreEngine / FfmpegEngine：**类还在但引擎树已删/本轮不使用** —— 它们仍注册在 `EngineRegistry` 里，探针自然报不可用；矩阵里已无 lite 能做的边指向它们（回归由 Rust 侧 `no_lite_capable_edge_left_on_external_engines` 与 C# 侧矩阵测试双向钉住）
- HeicEngine / RawDecodeEngine（相机格式）/ TextPdfEngine / PdfSecurityEngine（PDF 加密）/ ImageTargetWriter
- ArchiveService：压缩/解压门面（内置 zip + 外部 7z/WinRAR，配 PasswordPrompt）
- EngineDownloader：按需下载五要素（多镜像/SHA256/单实例/可取消/清半包）；校验和未配置 = 下载禁用（无校验不下载，诚实红线）

## 安全输出（继承不重写）

同卷临时目录 .bd-convert-{pid}-{guid} → 回读验证（失败恰好重试一次）→ File.Move 原子发布 → 重名自动 (2)(3) 序号永不覆盖 → 输出≠任一输入 → 错误六分类 → convert/* 事件（含 batch-finished 计数）。

## 测试

shell-convert-tests：37+ 用例（矩阵/参数/安全输出/服务批量/md 链/归档/PDF 合并合成拆分/YAML↔JSON/下载器红线），全部不起真进程（Theory 展开后更多）。

## Known Limitations

- **lite 的诚实边界**（与仓库"失败不得正常化"的纪律一致，宁可声明也不伪装）：`md→docx` 支持标题/段落/粗体，**不支持图片/表格/代码块/列表编号**；`docx→pdf` 是 MVP 排版，**无浮动图片/页眉页脚/表格线**，且因未写 PDF `/W` 宽度数组，数字与英文偏宽（中文全角正常）。
- 外部引擎**不是"需用户自行安装"**：保留的三棵（tesseract/poppler/ffmpeg）随 `06-可选引擎` 模块分发，缺失时对应目标**整项隐藏**（不是置灰）—— 这是既有契约（`ConvertMenuService.cs` 的 `IsEngineReady`）。
- 解压路径穿越防护（C2）依赖引擎的 list 命令（7z l -ba / UnRAR lb）；极旧版本引擎若不支持 list，降级为正常解压（引擎自身报错）。
- 大文件 / 批量转换耗时在 lite 路径下是进程内计算（实测 docx→md 24ms、docx→pdf 11ms、xlsx→csv 19ms 的样例级）；真实大文件仍受 CPU 影响，UI 不阻塞但完成时间不可控。
