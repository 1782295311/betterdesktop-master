# Cairo 开发计划 · 文档转换矩阵化 + Markdown 枢纽

> Task: 把 shell-convert 从"仅 Office 文档→PDF 单项"重构为"能力矩阵驱动、Markdown 为枢纽 IR、多引擎分层"的完整文件格式转换子系统。
> 证据基于 commit bf9b3d6（工作树 clean）验证；技术力文档命中：66-文档转换 域 5 份（local-engine-orchestration / dependency-on-demand / pdf-edit-safe-output / cli-and-agent-skill / pdf-table-extraction）+ 67-AI辅助写作/unified-multiformat-export。
> 类别：架构改动 / 跨模块重构（shell-convert 服务契约 + 菜单贡献者），form=full；plans-only，本计划不改生产代码。

## 1. Objective

1. 放开已在手引擎的能力：同一 soffice 进程支持 Office 文档族互转（docx/doc/odt/rtf/txt/html/epub、xlsx/xls/ods/csv、pptx/ppt/odp），不再硬编码 `--convert-to pdf`。
2. 建立 **ConversionMatrix（源→可转目标查询）+ EngineRegistry（多引擎定位/探测/缓存）+ 泛化 ConversionService** 的矩阵式核心，新增格式只改表、不动菜单与服务。
3. 以 **Markdown 为转换枢纽 IR（hub-spoke 拓扑）**：md↔html/txt 走 C# 纯托管离线链，md→docx/pdf 走两跳兜底，docx→md/epub/pptx 高质量链留 pandoc（按需下载）。
4. 菜单按 Win10 红线落地：第一层平铺高频"转 PDF"，其余目标进白名单子菜单"转换为 ▸"（与"打开方式 ▸""新建 ▸"同类，非 Win11 式收纳）；支持多选批量。
5. 补齐 shell-convert 测试（当前为 0）；完成后向 TECH-KNOWLEDGE/66 域回写 `markdown-conversion-hub.md` 资产并同步双索引。

## 2. Current Behaviour

- 输入集：ConvertEngineLocator.CategoryOf（ConvertEngine.cs）仅认 15 个 Office 扩展名，分 Word/Spreadsheet/Presentation 三类；`.md/.txt` 被 FileClassifier.cs:55 归 FileKind.Document，**不进转换菜单**；图片/音视频/归档无转换路径。
- 输出集：唯一 `.pdf`。DocumentConversionService.ConvertToPdfAsync 中 soffice 参数硬编码 `--convert-to pdf`（DocumentConversionService.cs:125-131）。
- 引擎链：① LocateSoffice（env BETTERDESKTOP_SOFFICE_PATH → Program Files → 便携目录，仅 File.Exists 判定）；② LocateComProgId（Office/WPS ProgID，ExportAsFixedFormat 仅能出 PDF，STA 后台线程 OfficeComPdfRunner）。
- 菜单：ConvertToPdfContributor 单项平铺；ConvertPlugin.cs 对 DesktopIcon/ShellFile 两 scope 各注册一次；Bootstrap.cs:169 注册插件。
- 已正确落地的红线（**重构必须保留，不重写**）[verified]：
  - UseShellExecute=false + ArgumentList 逐个传参（= execFile 免注入，DocumentConversionService.cs:118-131）；
  - 120s 超时 + Kill(entireProcessTree:true)（:134-143）；
  - 错误六分类 ConvertError（EngineMissing/EngineCrashed/Timeout/ConversionFailed/InputInvalid/OutputFailed，ConvertEngine.cs）；
  - 同卷临时目录 `.bd-convert-{pid}` → File.Move 原子发布 → finally 清理；目标存在自动 (2)(3) 序号不覆盖（:107-174）；
  - InFlight 按源路径去重（:18-28）；IEventBus `convert/started|finished|failed` + ConvertEventPayload（:55-67）；
  - 引擎缺失与文件错误分离提示（:98-99），无引擎整项隐藏（ConvertToPdfContributor HasPdfEngine 门控）。
- 测试：**shell-convert 无测试项目**（全仓检索无 convert-tests 目录）[verified]。

## 3. Relevant Architecture

```
右键菜单(IMenuService) ──RegisterContributor── ConvertMenuContributor（消费矩阵查询，动态生成项）
                                                      │ 查询 GetTargets(identity)
                                                      ▼
                                              ConversionMatrix（静态表：源类别×目标→引擎指派+filter）
                                                      │ 选引擎
                                                      ▼
                                              EngineRegistry（定位/真实探测/进程级缓存）
                                   ┌────────────┬───────────┴──────────┬─────────────┐
                              SofficeEngine   ComPdfEngine(仅PDF)  ManagedEngines   外部引擎(P2/P3)
                              (文档族互转/     (Office·WPS         (Markdig/Reverse   (Poppler/Pandoc/
                               两跳中转)       ExportAsFixed)      Markdown/绘图/      FFmpeg，按需)
                                                                   PdfSharpCore)
                                                      │
                                                      ▼
                                          ConversionService.ConvertAsync(paths,target)
                                          （校验→临时目录→执行→原子发布→逐文件事件→批量汇总）
```

- 拓扑纪律：**hub-spoke，不做 N² 直连**。任意 A→B 无直连引擎时，允许 A→md/html→B 两跳，两跳链在矩阵中显式登记（标注 hop=2），禁止隐式递归超过两跳。
- shell-convert 仅依赖 shell-context-menu **契约**与 kernel（现状 csproj 两个 ProjectReference 保持），不反向依赖 shell-desktop。
- 包内目录：新增 `Services/Engines/`（各引擎）、`Services/Markdown/`（md 纯托管链）；契约（目标格式描述、引擎接口）放 `Services/` 或新建 `Contracts/`（与 context-menu 包风格一致，推荐 Contracts/）。

## 4. Technical-Knowledge Findings

| 资产 | 本计划取用点 | 现状对照 |
|---|---|---|
| local-engine-orchestration（L2） | EngineRegistry 多引擎一张表：env 覆盖→候选链→execFile 等价→超时→错误四分类 | 已用于单 soffice；扩成多引擎表，模式不变 |
| dependency-on-demand（L2） | 引擎必须**真实执行 `--version` 探测**（文件存在不算）；P3 重引擎多镜像+大小/SHA256+单实例门控+可取消+失败清半包+缓存 | 现仅 File.Exists，缺真实探测；下载器完全缺失 |
| pdf-edit-safe-output（L2） | 输入校验、输出绝不覆盖输入、临时文件原子发布、敏感值不入日志 | 已实现且加强为序号不覆盖；泛化时保留并补"输出≠任一输入"校验（多选批量场景需要） |
| cli-and-agent-skill（L2） | capabilities/targets 解耦思想（=ConversionMatrix.GetTargets）；批量转换；按源格式记忆上次目标；uniqueDestination 序号 | targets/批量/记忆均缺；序号已实现 |
| pdf-table-extraction（L2） | P3 pdf→md/xlsx 反向的分级回退策略（文字坐标→OCR→Raw） | 未用，P3 |
| unified-multiformat-export（67 域，L2） | 统一模型思想（本计划以 md 为 IR 落地）；导出后回读验证+失败重生成一次；文件名清理 Windows 非法字符；PDF 内嵌中文字体；HTML 完整独立页内嵌 CSS；逐项报告成败 | 全部缺，纳入红线 |

**库空白（本计划完成后回写）**：66 域无 Markdown 转换资产，新增 `markdown-conversion-hub.md`（三引擎链对比 + §5 md 红线），并更新 `索引.md`、`index-ai.md`。

## 5. Constraint Findings（生死线与验收约束）

### 5-1 既有红线（继承，违一即回归）
1. 子进程一律 UseShellExecute=false + ArgumentList；禁止任何字符串拼命令行。
2. 长任务必须超时（沿用 120s 常量，图片/PDF 合并可按类型单独配置）+ 杀进程树；stdout/stderr 有界。
3. 永远先写同卷临时目录、成功才原子发布；目标存在序号 (2)(3)，**永不覆盖、输出路径不得等于任一输入**。
4. 引擎缺失 ≠ 文件错误：无可用引擎时该目标项隐藏（隐藏优先），已显示后的运行缺失报"引擎未就绪"。
5. 错误六分类不得合并；事件失败不阻断主流程（M10），但要 DiagnosticLog.Trace。

### 5-2 新增红线
6. **文档转 txt/csv 的编码坑** [inferred，实现期实测锁定]：soffice 默认文本编码非 UTF-8，中文必乱码；filter 必须显式带 UTF8（txt 形如 `txt:Text (encoded):UTF8`；csv 的 StarCalc filter 参数含代码页 76=UTF8，精确串以实测为准并写成常量+注释）。验收：中文 docx/xlsx → txt/csv 用 UTF-8 读回无乱码。
7. **md 图片相对路径**：md→docx/pdf/html(独立文件) 时必须解析相对图片（pandoc 传 resource-path；两跳链在 html 中转阶段内联或解析 base/目录）；缺失图片逐张告警，不产出静默残缺文档。
8. **CJK 字体**：md→pdf 链路样式表必须带中文字体栈；HTML 导出为完整独立页（内嵌 CSS，代码块/表格/任务列表样式齐备）。
9. **能力诚实显隐**：标准 md 语法/表格/代码块/任务列表=支持；LaTeX 公式仅 pandoc 链；**Mermaid 任何文本引擎都不支持（需浏览器执行 JS），不出菜单项、不在结果中假装成功**。
10. **YAML front matter**：默认剥离不进正文（设置项可改为转标题区），规则单一、有测试。
11. **NuGet 许可证**（Windows-only net8.0-windows 项目）：常见图片互转优先 BCL `System.Drawing.Common`（Windows 受支持，png/jpg/bmp/tiff/gif，无 webp）；webp 等缺口用 **SkiaSharp（MIT，带原生二进制，x64 锁定）**；若选 ImageSharp **必须锁 2.x（旧 Apache-2.0 许可），v3 为商业许可**；PDF 组合用 **PdfSharpCore（MIT）**。禁止引入 GPL/AGPL 或需商业授权的库（对照 flyingmouse 对 PyMuPDF AGPL 的标注纪律）。
12. 导出后回读验证（存在且大小>0、Office 类可被引擎再识别），失败自动重生成恰好一次，再失败按 failed 上报。
13. 文件名清理 `/\:*?"<>|`；多选批量逐文件出结果事件，末尾 `convert/batch-finished` 带成功/失败计数，**禁止把部分成功当全成功**。

## 6. Proposed Changes

### 6.1 目标格式与引擎指派矩阵（ConversionMatrix 数据）

| 源类别（FileKind/扩展名） | 可转目标 | 引擎指派（主→兜底） | 阶段 |
|---|---|---|---|
| WordDocument（doc/docx/docm/rtf/odt/wps） | pdf / docx / odt / rtf / txt / html / epub | pdf：soffice→COM；其余 soffice | P0 |
| ExcelWorkbook（xls/xlsx/xlsm/et） | pdf / xlsx / ods / csv | 同上（csv 走红线 6） | P0 |
| Presentation（ppt/pptx/pps/dps） | pdf / pptx / odp | soffice→COM(pdf) | P0 |
| Document：md | html / txt / docx / pdf | html/txt：Markdig 纯托管；docx/pdf：md→html→soffice 两跳；epub/pptx：pandoc | P1（epub/pptx P3） |
| Document：txt/log | md / html | txt→md 轻包装（纯托管）；html 经托管 | P1 |
| html（Document/SourceCode 中 .html/.htm） | md / pdf | ReverseMarkdown 纯托管；pdf 经 soffice | P1 |
| Image（png/jpg/bmp/tiff/gif/webp） | 互转、合成 pdf | System.Drawing + SkiaSharp(webp)；合 PDF=PdfSharpCore | P1 |
| 多 PDF | 合并为一个 pdf；拆分 | PdfSharpCore（安全输出层） | P1 |
| PDF | png/jpg、txt | Poppler：pdftoppm / pdftotext | P2 |
| docx | **md（高质量）** | pandoc 主；soffice→html→ReverseMarkdown 离线兜底（样式有损，菜单标注） | P2 兜底 / P3 主 |
| md | docx（高质量）/ epub / pptx | pandoc | P3 |
| Video/Audio | 容器/编码互转、抽音轨、抽帧 | FFmpeg（按需下载） | P3 |
| PDF | md/xlsx（版式/表格还原） | pdf2docx 系（pdf-table-extraction 策略） | P3 |

- 目标描述统一值对象 `ConversionTarget(string Format, string Label, string? Filter, int Hops, EngineKind Prefer)`；矩阵为不可变静态表 + 按"当前实际可用引擎"运行期过滤（GetTargets 返回的每一项必须当下可执行，隐藏优先）。
- soffice filter 短名（docx/xlsx/pptx/odt/ods/odp/rtf/html/epub）[inferred，LibreOffice 接受短名，实现期以 `--convert-to <x> 实测产物可打开为准并落测试样例]；编码类 filter 按红线 6 锁常量。

### 6.2 类型与文件改动（P0 骨架，后续阶段增量）

| 文件 | 动作 | 职责 |
|---|---|---|
| Contracts/ConversionTarget.cs、Contracts/EngineKind.cs、Contracts/ConversionResult.cs | 新建 | 目标描述、引擎枚举、单文件结果（含错误分类/耗时/产物路径） |
| Services/Engines/IConversionEngine.cs | 新建 | `bool CanHandle(srcExt,target)` / `Task<ConversionResult> RunAsync(req,ct)` / `EngineAvailability Probe()` |
| Services/Engines/SofficeEngine.cs | 新建（迁出逻辑） | 从 DocumentConversionService 抽出 soffice 子进程逻辑，filter 参数化，保留全部红线 |
| Services/Engines/ComPdfEngine.cs | 新建（迁出 OfficeComPdfRunner） | 仅 pdf；STA 线程逻辑原样搬迁 |
| Services/EngineRegistry.cs | 新建 | 多引擎注册、候选链+env、**--version 真实探测（P2 补齐，P0 先保留 Exists 并留 TODO 挂钩）**、进程级缓存、按 target 选引擎 |
| Services/ConversionMatrix.cs | 新建 | 静态矩阵 + GetTargets(FileIdentity) / ResolveEngine(src,target)；两跳登记 |
| Services/ConversionService.cs | 新建（替代 DocumentConversionService） | `ConvertAsync(IReadOnlyList<string> paths, string target, ct)`；旧 ConvertToPdfAsync 改为调它的薄封装（内部一处调用方同步改），或直接更名并改调用方（仅 ConvertToPdfContributor，一处） |
| Services/ConvertMenuContributor.cs | 新建（替代 ConvertToPdfContributor） | 一层"转 PDF"（若矩阵含 pdf 且引擎可用）+ Submenu"转换为 ▸"动态子项；按操作记忆置顶（P1）；多选整集 |
| ConvertPlugin.cs | 改 | 注册新 contributor（两 scope 不变）；持有 ConversionService 单例 |
| Services/Markdown/*（MarkdigHtmlExporter、MarkdownTextExporter、HtmlToMarkdownExporter、两跳编排） | P1 新建 | 纯托管 md 链；Markdig/ReverseMarkdown NuGet |
| Services/Engines/ManagedImageEngine.cs、PdfComposeEngine.cs | P1 新建 | 见红线 11 选型 |
| Services/Engines/PopplerEngine.cs、PandocEngine.cs、FfmpegEngine.cs、EngineDownloader.cs | P2/P3 新建 | 按需下载走 dependency-on-demand 全套 |
| shell-convert-tests（新测试项目，入 sln） | P0 建 | 见 §8 |

### 6.3 菜单形态（Win10 六红线协调，默认方案 A，§12 可改）
- 文档/图片等可转换对象：第一层 Contribution 组平铺 **"转 PDF"**（矩阵含 pdf 且引擎可用时，高频直达）；其后一个 **"转换为 ▸" Submenu**，子项=GetTargets 去掉 pdf 后的动态列表，子菜单末尾分隔线+"上次选择"置顶（P1）。
- md 文件：一层无 pdf 直达时（无 soffice 且无 COM），"转换为 ▸"仍保留纯托管项（html/txt）——**纯托管项不依赖外部引擎，必须独立可用**。
- 子菜单属白名单合法形态（MenuItemKind.Submenu 注释明确"集合语义/动态列表允许"），不违反永远单层红线；禁止把第三方/系统项再塞二级。

## 7. Implementation Sequence（每步结束可编译可回退）

- **P0-A 契约与引擎抽象**：新增 Contracts 三类型 + IConversionEngine；SofficeEngine/ComPdfEngine 从现文件**等价搬迁**（行为零变化，先不增格式），ConversionService 与旧服务同行为；build 绿。
- **P0-B 矩阵与泛化**：ConversionMatrix 填 Office 族全目标；ConversionService.ConvertAsync 支持 filter 参数化与多选批量；txt/csv 编码常量；事件补 batch-finished；输出≠输入校验。
- **P0-C 菜单替换**：ConvertMenuContributor 上线、删 ConvertToPdfContributor；一层 PDF + "转换为 ▸"；FileClassifier 无需改（md 留 P1 接）。
- **P0-D 测试项目**：建 shell-convert-tests，矩阵/参数构造/序号命名/引擎选择/批量聚合单测（mock IConversionEngine，不起真进程）。**P0 验收门槛：全 sln build 0 错 + 新测试全绿 + 手工 docx→pdf 行为与重构前一致（回归）。**
- **P1-A md 纯托管链**：Markdig md→html/txt、ReverseMarkdown html→md、front matter 处理；md 进入 ConversionMatrix 与菜单。
- **P1-B md 两跳**：md→html→soffice→docx/pdf；图片相对路径处理、CJK 样式、独立 HTML、回读验证+一次重生成。
- **P1-C 图片与 PDF 组合**：System.Drawing 互转（锁 SkiaSharp 补 webp）、图片合 PDF、PDF 合并/拆分；文件名清理。
- **P1-D 操作记忆**：设置键 `convert.last-target.<ext>`，子菜单置顶。
- **P2 Poppler + 引擎真实探测**：PDF→图片/txt；EngineRegistry 全引擎 --version 探测替换 File.Exists。
- **P3 按需重引擎**：EngineDownloader（多镜像/SHA256/单实例/可取消/清半包）→ pandoc（docx↔md/epub/pptx）→ FFmpeg 音视频 → pdf2docx/OCR。
- **收尾**：回写 66 域 markdown-conversion-hub.md + 双索引；更新 shell-convert README。

## 8. Test Strategy

新建 `packages/shell/shell-convert-tests`（xunit，参照现有 *-tests 项目风格；TreatWarningsAsErrors 一致）：
- **矩阵纯逻辑（不起进程，核心覆盖）**：每类源 GetTargets 返回集与引擎指派；无引擎时目标被过滤；两跳登记正确；md 在无 soffice 时仍出 html/txt。
- **参数构造**：SofficeEngine 命令参数序列（Assert ArgumentList 顺序与 filter 常量；中文路径/带空格路径/特殊字符路径不串参）；txt/csv filter 含 UTF8 标识。
- **安全输出**：序号 (2)(3) 生成；输出=输入之一时拒绝；批量中一项失败不影响其他、计数正确；临时目录 finally 清理（模抛异常路径）。
- **md 链（P1）**：front matter 剥离；代码块/表格/任务列表往返 md→html→md 结构保留；缺图片告警路径；非法文件名清理。
- **引擎选择**：soffice 存在走 soffice、不存在回退 COM(pdf)；非 pdf 目标无 soffice 时隐藏。
- **集成（手工/标记 Category=Integration，需本机引擎）**：docx→pdf 回归、docx→txt 中文无乱码、xlsx→csv、md→docx 打开验证、png→jpg、图片合 PDF；每样留最小测试文件。
- 验证命令（真实存在）：`dotnet build BetterDesktop.slnx -c Debug`；`dotnet test packages/shell/shell-convert-tests`。

## 9. Risk and Impact Analysis

- **行为回归风险（中）**：P0 搬迁现有 PDF 链。缓解：等价搬迁先于功能扩展（P0-A 单独一步），保留原红线代码结构，集成测试做前后对照。
- **soffice filter 兼容性（中）**：不同 LibreOffice 版本 filter 名/编码参数有差异 [inferred]。缓解：短名优先、编码常量集中、Integration 测试锁版本行为，失败回退提示而非静默。
- **两跳质量损失（低-中）**：md→html→docx 不如 pandoc 直转。缓解：菜单对兜底链结果不做"高质量"承诺，P3 pandoc 到位后矩阵自动切换首选引擎，调用方无感。
- **菜单膨胀（低）**：目标过多时"转换为 ▸"变长。缓解：每源类别目标本就有限（≤7），操作记忆置顶；不新增二级以上层级。
- **NuGet 许可证与原生体积（中）**：见红线 11，选型写入 csproj 注释；SkiaSharp 原生 dll 随 RID x64 发布，验证打包体积。
- **依赖下载安全（P3，高关注）**：严格走 dependency-on-demand（SHA256/多镜像/单实例/取消/清半包），P0-P2 不引入任何下载行为。
- **d=1 直接影响面**：ConvertPlugin（注册）、Bootstrap（无需改，插件契约不变）、IEventBus 订阅方（convert/* payload 字段保持兼容，仅新增 batch-finished 事件名）、FileClassifier（不改）、右键渲染（Submenu 已支持，无需改）。
- **并发**：沿用 InFlight 去重并扩展为 (path,target) 键；批量内同文件不重复入队。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| Contracts/ConversionTarget.cs 等 3 个新文件 | ConversionTarget/EngineKind/ConversionResult | 矩阵数据契约 |
| Engines/IConversionEngine + Soffice/ComPdf | Run/Probe/CanHandle | 引擎抽象与等价搬迁 |
| EngineRegistry.cs、ConversionMatrix.cs | Locate/Probe/GetTargets/ResolveEngine | 矩阵核心 |
| ConversionService.cs（替代 DocumentConversionService.cs） | ConvertAsync | 泛化+批量 |
| ConvertMenuContributor.cs（替代 ConvertToPdfContributor.cs） | Build | 动态菜单 |
| ConvertPlugin.cs | LoadAsync | 换注册 |
| Markdown/*、ManagedImage/PdfCompose/Poppler/Pandoc/Ffmpeg/EngineDownloader | 分阶段 | P1-P3 |
| shell-convert-tests/* | — | 补零测试 |
| TECH-KNOWLEDGE/66-文档转换/markdown-conversion-hub.md、索引.md、index-ai.md | — | 库空白回写（收尾） |

## 11. Reusable Implementation Context（机器可读 context pack）

- commit: bf9b3d6（clean）；TFM net8.0-windows10.0.19041.0 / x64 / UseWPF / TreatWarningsAsErrors。
- cited manifest：ConvertEngine.cs（全）、DocumentConversionService.cs（全）、ConvertPlugin.cs、ConvertToPdfContributor.cs、MenuPrimitives.cs（MenuGroup/MenuItemKind）、FileClassifier.cs:55、Bootstrap.cs:168-169、BuiltInOpsContributor.cs（贡献者写法范式）、MenuService.cs（Submenu/Extended 过滤）。
- 复用范式：贡献者结构照 BuiltInOpsContributor（Priority/Scope/Build + DiagnosticLog.Trace）；子进程/安全输出照现 DocumentConversionService 搬迁；引擎下载照 dependency-on-demand；事件照 ConvertEventPayload 兼容扩展。
- 红线常量集中点：超时、临时目录前缀 `.bd-convert-{pid}`、序号规则、txt/csv UTF8 filter、设置键前缀 `convert.`。

## 12. Assumptions and Open Questions

- [采用默认，待确认] **菜单形态 A**：一层"转 PDF"+"转换为 ▸"。备选 B：只留子菜单、PDF 内加粗默认。若选 B 只改 ConvertMenuContributor，矩阵不动。
- [采用默认，待确认] **pandoc/FFmpeg 走按需下载（P3）**，P0-P2 完全离线、零下载；若要求彻底离线，则 P3 改为仅探测本机已装引擎，删除下载器。
- [采用默认] md→docx P1 走两跳兜底（不自研 OpenXML 映射，省成本），高质量等 pandoc；备选自研 OpenXML（可控但工作量大，不推荐）。
- [assumed] soffice 短 filter 名与 txt/csv 编码串在实现期以实测锁定（已列入 P0/P1 测试），本计划不把未实测参数写成既定事实。
- [deferred] PDF 加密/解密、OCR 语种包管理、音视频参数预设（码率/分辨率 UI）、CLI 批处理入口（cli-and-agent-skill 的 CLI 部分本计划不做，仅借其 targets/批量思想）。
- [deferred] Mermaid 渲染（需嵌入式浏览器引擎，单独立项）。

## 13. Definition of Done

1. P0：Office 三族可转矩阵全部目标；一层 PDF + "转换为 ▸"动态菜单；多选批量；中文 txt/csv 无乱码；全 sln 0 错 0 警；shell-convert-tests 全绿（含 §8 纯逻辑项）；docx→pdf 与重构前手工对照无回归。
2. P1：md↔html/txt 离线可用（无任何外部引擎）；md→docx/pdf 两跳；图片互转+图片合 PDF+PDF 合并；红线 7-13 逐条有测试或手工记录；操作记忆生效。
3. P2：PDF→图片/txt；引擎全部真实 --version 探测。
4. P3：pandoc/FFmpeg 下载-校验-取消闭环；docx↔md 高质量、epub/pptx、音视频互转各有一个 Integration 样例。
5. 收尾：66 域新资产 + 双索引同步；README 更新；无 ConvertToPdf* 旧名残留、无死代码、无注释掉的旧链。

## 14. 交接节（移交 ability-reuse-alignment 执行）

### 14.1 注入清单（实现 agent 直接消费，禁止重新调研）
- 技术库资产（先读）：TECH-KNOWLEDGE/66-文档转换/{local-engine-orchestration,dependency-on-demand,pdf-edit-safe-output,cli-and-agent-skill}.md、67-AI辅助写作/unified-multiformat-export.md。
- 现状源码（权威）：本计划 §11 cited manifest，全部位于 commit bf9b3d6；行号若漂移以符号名定位。
- 范式样板：贡献者=BuiltInOpsContributor.cs；安全子进程=DocumentConversionService.cs（搬迁母本）；菜单 Submenu=MenuPrimitives/MenuStyling 既有支持。
- NuGet 白名单：Markdig、ReverseMarkdown（P1）；SkiaSharp（MIT，webp 缺口）或 System.Drawing.Common（BCL）；PdfSharpCore（MIT）；**ImageSharp 仅允许 2.x**；P2/P3 外部引擎走进程不引 NuGet。

### 14.2 模式判定
- P0-A/B = 等价搬迁+参数化（重构模式：先搬后扩，禁止一步混做）；
- 矩阵/菜单 = 表驱动+贡献者扩展点（复用既有 IContextMenuContributor，不开新扩展机制）；
- md 链 = 纯托管库优先 + 两跳兜底 + 重引擎 P3（分层混合，禁止 P0-P2 引入网络下载）；
- 安全输出 = 直接复用已验证的临时目录+原子发布+序号，不重新发明。

### 14.3 适配参数
- TFM/x64/UseWPF/TreatWarningsAsErrors 沿用；命名空间根 BetterDesktop.Shell.Convert(.Services/.Contracts/.Engines/.Markdown)。
- 超时默认 120_000ms；临时目录前缀 `.bd-convert-{pid}`；设置键前缀 `convert.`；事件名沿用 convert/* 并新增 convert/batch-finished。
- 菜单 Priority：沿用现贡献者 -200 基数（一层 PDF 与子菜单相邻，不插队到用户自定义项之前）。
- txt/csv UTF8 filter 为具名常量，落测试；soffice 短 filter 名以实测为准。

### 14.4 DoD 核销表（实现完成逐项打勾，缺项不得宣称完成）
- [ ] P0-A 等价搬迁，docx→pdf 行为对照无回归（附手工记录）
- [ ] Office 三族矩阵目标齐全，filter 参数化，中文 txt/csv UTF8 无乱码
- [ ] 一层"转 PDF"+"转换为 ▸"动态生成；无引擎目标隐藏；多选批量+batch-finished
- [ ] 输出≠任一输入、序号不覆盖、临时目录清理、(path,target) 去重
- [ ] shell-convert-tests 建立且 §8 纯逻辑用例全绿；全 sln build 0 错 0 警
- [ ] P1：md 纯托管链离线可用；两跳 docx/pdf；红线 7-13 逐条核销
- [ ] P1：图片互转/合 PDF、PDF 合并（许可证白名单合规，csproj 注释选型）
- [ ] P2：Poppler + 全引擎 --version 真实探测
- [ ] P3：下载器五要素（多镜像/SHA256/单实例/可取消/清半包）+ pandoc/FFmpeg 样例
- [ ] 旧 ConvertToPdf* 符号清理无残留；66 域资产回写+双索引同步；README 更新
