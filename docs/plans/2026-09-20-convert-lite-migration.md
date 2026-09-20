# 计划 · 格式转换去第三方：convert-lite 转正为正式功能（2026-09-20）

> Task：把实验项目 `convert-lite-lab` 迁回主仓，作为格式转换的正式实现，替掉 `engines/` 里打包的
> 第三方二进制（pandoc / LibreOffice / calibre / poppler / ffmpeg / tesseract / dcraw，**实测 2858 MB**）。
> 触发：用户拍板"那个实验项目功能基本能用，可以迁移回来，当做正式功能了"。

## 1. Objective

| | 现状 | 目标 |
|---|---|---|
| 发布体积 | `06-格式转换引擎` = **2858 MB**（第三方全家桶） | **~6 MB**（`convert-engine.exe` 链入 lite 后实测预估），FFmpeg 走可选包 |
| 外部 exe | pandoc / soffice / ebook-convert / pdftoppm / pdftotext / ffmpeg / tesseract / dcraw | **零外部 exe**（音视频除外，见 §7 决策 D3） |
| 进程模型 | 编排子进程 + 解析 stdout | 进程内函数调用 |
| 装机表现 | 缺 `engines/` ⇒ 菜单**整项隐藏**（用户看到"格式转换没了"） | 引擎恒可用 ⇒ 目标恒显示 |

**用户可感知的验收点**：全新安装（无 `engines/`）后，右键任意文档 → 格式转换子菜单**有货、点了能出结果**。

## 2. Current Behaviour（改造对象）

- **`native/convert-engine`（Rust，产物 `convert-engine.exe`）**：**外部引擎编排器**。
  契约 = 子命令 `capabilities | run | probe`；`run` 从 **stdin 读 JSON 请求**，**stdout 出 NDJSON**
  （`progress{source,target,engine,phase,percent?,elapsed_ms}` / `result{ok,outputs?,engine,elapsed_ms,error?,message?}`）。
  引擎定位全在 `src/engines.rs`（`engines_root()` = `<exeDir>\engines`，`engines.rs:213-219`）。
- **`packages/shell/shell-convert`（.NET）**：`RustConvertRunner` 定位 `convert-engine.exe`
  （env `BETTERDESKTOP_CONVERT_ENGINE` → `<BaseDir>\convert-engine.exe`，`RustConvertRunner.cs:34-43`）、
  spawn、解析 NDJSON、映射错误码（`ConversionResult.cs` 六分类）。
  菜单可用性判据 = `IsEngineReady` → `EngineRegistry.Resolve` → 逐引擎 `Probe().Available`
  （`ConvertMenuService.cs:287-291`）；**不可用就整项隐藏**（`:227-231`、`BuildSystemMenuTargets` `:131-134`）。
- **矩阵双份**：`native/convert-engine/src/matrix.rs` ↔ `packages/shell/shell-convert/Services/ConversionMatrix.cs`
  （逐行对照移植）+ `ConversionMatrixTests`（精确断言，改矩阵必然要改测试）。
- **`engines/` 依赖面（全仓实测）**：shell-convert 内 13 处路径构造 + `BetterDesktop.Cli.csproj:26-28`
  的 Content 复制 + `native/convert-engine/src/engines.rs` + `publish-modules.ps1:140-162`（`06` 模块）
  + `install-engines.ps1` + `install-betterdesktop.ps1:367-411`。**没有其它功能包读 `engines/`**。

## 3. 为什么这条迁移路径是现成的

实验室的 `src/lib.rs` 开头就写着它的用途：

> `//! - better-desktop-cordis native/convert-engine：作为库引用，替代 pandoc/soffice/poppler/calibre 等外部 exe 调用`

- 核心入口是一个**完全静默**的库函数：`convert_lite::convert(inputs, target, output, key) -> Result<Option<PathBuf>, String>`
  （无 stdout 副作用）⇒ **进度协议仍由 convert-engine 自己发**，C# 侧协议零改动。
- 50 个 `.rs` / **14750 行**，release **4.2 MB**（LTO + strip + panic=abort）。

## 4. Proposed Changes（已执行 + 待执行）

### ✅ 已执行（本轮）

1. **vendored crate**：`native/convert-lite/`（`Cargo.toml` + `Cargo.lock` + `src/*.rs` 50 个，哈希逐一校验一致）。
   刻意**不带**实验室的 `samples/`、`tools/`（144 个 py）、`third_party/`（213 MB FFmpeg dll）—— 见 §7 D3。
   `Cargo.toml` 头注写清了来源、用途，以及"bin 是开发/回归夹具、不随包发布"。
2. **构建验证**：`cargo build --release` 通过（2m41s），产物 `convert-lite.exe` 4298 KB / `libconvert_lite.rlib` 7839 KB。
3. **门禁复核**：`verify-native-convergence` **PASS**（它只数 C# 侧 `DllImport`，新 Rust crate 不影响）；
   `native/convert-lite` 不在任何 workspace 里（与 `engine/`、`engine-index/`、`native/convert-engine/` 同规矩）。

### 待执行（按顺序）

| 步 | 动作 | 验证 |
|---|---|---|
| **C1** ✅ | `native/convert-engine/Cargo.toml` 加 `convert-lite = { path = "../convert-lite" }` | ✅ `cargo build --release` 通过（1m15s，0 错误） |
| **C2** ✅ | `EngineKind::Lite`（`contract.rs`）+ 恒可用探测（`engines.rs::probe_kind` + `KINDS` 15→16）+ 注册（`engines_impl::all_engines`）+ **桥** `src/lite.rs`：`LiteEngine` 实现 `Engine` 三方法，`run` 调 `convert_lite::convert` 写 `job.temp_dir`，错误按 message 归入既有六分类 | ✅ **99 单测全过**（含端到端 `lite_engine_converts_end_to_end`：md→html 真出 `<h1>`/`<strong>`）。C# 侧 `EngineKind.Lite` 成员已同步加（否则 `capabilities` 输出 `"Lite"` 会解析失败） |
| **C3-Rust** ✅ | **矩阵改路由（Rust 侧已做）**：不逐行改表，而是在 `ConversionMatrix::build()` 收尾加一道路由 pass（`matrix.rs::route_through_lite`）：① 摘掉 `Audio`/`Video` 类别目标（D3①）+ 摘掉空行；② 把 `Pandoc`/`Soffice`/`Calibre`/`TwoHop`/`ComPdf` 拥有的、且 `lite::supports` 为真的边改派 `Lite`（`fallback=None`）；③ **不动** `Image`/`PdfCompose`/`PdfSecurity`/`Heic`/`Raw`/`Tesseract`/`Poppler` 的边（保留清单里的功能不许被抢） | ✅ `cargo test --release` **102/102**；新增 3 条路由不变量测试 + 真机冒烟（见下） |
| **C4**（C# 侧，**代码 ✅ / 测试 ⏳**） | C# 镜像同一道路由 pass（`ConversionMatrix.RouteThroughLite`，与 `matrix.rs` 逐一对应）+ 新增 `LiteCapability`（能力声明面镜像）+ `LiteEngine`（`Probe()` 恒 `Ok`、`CanHandle` 用声明面、`RunAsync` 委托 RustConvertRunner）+ 在 `ConvertPlugin` 注册。**菜单逻辑一行不改** —— `IsEngineReady` 变真后目标自然显示 | ✅ 编译 **0 错误 0 警告**；⏳ `dotnet test shell-convert-tests` **72 通过 / 14 失败** —— 失败全部是"期望值仍是旧路由"（见下），**待逐条改为新判据**；真机验收同 C5 之后 |
| **C5** ✅ | **退役 `engines/` 里被替代的三棵树**：① `BetterDesktop.Cli.csproj` 删掉 `..\engines\**\*` 的 Content glob（**全量 dist 3035MB 的主因**）；② `publish-modules.ps1` 的 `06` 分支改为**从仓库根 engines/ 只取保留树**（否则 OCR 会随 dist 一起消失）；③ `install-betterdesktop.ps1` 3b 段注释/Warn 改为事实；④ **物理删除** `libreoffice` 1522MB + `calibre` 627MB + `pandoc` 223MB = **2372 MB**（保留 tesseract 240MB / ffmpeg 238MB / poppler 8MB = 486MB）。另清掉 bin 里 4 份陈旧的 engines 副本 | ✅ **PUBLISH OK：`dist\BetterDesktop-2026.09.20.0946` = 216 MB / 580 文件**（原 3035MB / 20952 文件，**-2819MB，-93%**）；dist 内 `engines` = **False**；`betterdesktop-core.exe` + `BetterDesktop.ico` + `convert-engine.exe`(7048KB，已链入 lite) 齐备；`engines/` 实测 2635MB → **486MB** |
| **C6** ✅ | 文档与代码回写：① `shell-convert/README.md`（矩阵速览/引擎分层/Known Limitations 全部按新事实重写）；② `docs/build-release.md` `§五.3`（pandoc 同目录规则已作废，替换为 lite + 可选引擎口径，**字数还降了**）；③ `THIRD-PARTY-NOTICES.md`（删掉 calibre/LibreOffice/Pandoc 三项——**GPL-3.0 再分发义务随之消失**，补 poppler 与 vendored crate 的 9 个新依赖）；④ **`launcher/Services/ComponentBootstrapper.cs`**：它按**全名字面量**找 `06-格式转换引擎\engines` —— 模块改名后这条会静默失效，已改为**前缀匹配 `06-*`**（与 `install-betterdesktop.ps1` 同口径），并修正"缺 engines 就只剩纯托管图片"的过时文案 | ✅ 门禁：doc-budgets / md-links / system-integration / architecture-guard **全 PASS**；全仓构建 **0 错误 0 警告**；`shell-convert-tests` **80/80** |

> **执行顺序红线**：C3/C4 **必须**在 C5 之前。先删 `engines/` 会让文档族菜单整项消失
> （`IsEngineReady=false` ⇒ 隐藏），用户看到的是"格式转换坏了"而不是"换了实现"。

### 4.0 C4 落地后的测试对账（2026-09-20，**已收敛**）

`shell-convert-tests`：**79 通过 / 7 失败**（编译 0 错误 0 警告）。演进：14 失败 → **7 失败**，
减掉的 7 条**全部是本迁移引起的判据变更，已按新路由修好**：

| 测试类 | 处理 |
|---|---|
| `ConversionMatrixTests`（4 条） | 期望值改为 `Lite`/`Fallback=null`；音视频断言改为**不存在性断言**（防"悄悄放回来"，回归时会红） |
| `RegistryMarkdownDownloaderTests`（3 条） | 候选链/同类注册序两条改用 **lite 没有 reader 的边**（`.wps` / `.dps`）以保留原测试意图；菜单高亮那条**注册真实 `LiteEngine`** |

**剩下 7 条 `ConversionServiceTests` 是既有欠账，与本迁移无关 —— 已取证**：

- 失败模式是"假引擎的 `Runner` **一次都没被调用**"（`attempts Expected 2 / Actual 0`）⇒ 说明该路径**根本不查注册表**；
- 机制：`ConversionService.ConvertOneAsync` 在 S9「执行核心下沉 Rust」后**提前 `return` 给 `_rustRunner`**，
  其后那段走注册表 + `RunVerifiedAsync` 的代码成了**死路径**（私有方法，编译器不报未使用）；
- 该测试文件最后一次提交是 `d939f97 refactor(shell): 一阶段收口功能定型`（早于本迁移），
  即**测试仍在给死路径打桩**。

⇒ **它需要的是独立一笔活**（二选一：给 `ConversionService` 加一个可注入的 Rust runner 接缝，让测试能打桩 Rust 侧；
或删掉死路径并把这些测试改写成针对 `RustConvertRunner` 的夹具）。
**不要**为了让它们变绿而改回实现 —— 那是把"测试给死路径打桩"这个真问题藏起来。

**对 C5 的影响：不阻塞。** C5 的风险是"删掉 engines 后菜单会不会整项消失"，由 `IsEngineReady` 决定，
而它已被上述 7 条绿测覆盖（矩阵路由 + 注册表解析 + 菜单高亮）。

### 4.1 实测记录（2026-09-20，C3-Rust 完成后）

真机冒烟（`convert-engine run`，stdin JSON → NDJSON；**无任何外部引擎、无 `engines/`**）：

| 边 | 原负责引擎（已删树） | 现在 | 结果 |
|---|---|---|---|
| `docx → md` | pandoc | `engine:"Lite"` `ok:true` **24ms** | 产出 a.md |
| `docx → pdf` | LibreOffice soffice | `engine:"Lite"` `ok:true` **11ms** | 产出 a.pdf（11KB） |
| `xlsx → csv` | LibreOffice soffice | `engine:"Lite"` `ok:true` **19ms** | 产出 b.csv |

单测：`cargo test --release` **102 passed / 0 failed**（新增 4 条：路由不变量 3 条 + 端到端 1 条）。

**两条口径教训（都是被测试抓出来的，值得记下）**：

1. **"隐藏音视频"必须按 `TargetCategory::Audio|Video` 判定，不能按"引擎 == Ffmpeg"**。
   按引擎判会连 `png → tga` / `png → avif` 一起摘掉 —— tga/avif 的编解码也在 ffmpeg 里，但它们是**图片**边，
   摘掉是真丢功能。旧测试 `image_interconversion_and_special_targets`（"png 行缺少 tga"）把它抓了出来。
2. **`cargo test` 不会刷新 `target/release/<bin>.exe`**。第一轮冒烟拿到的是旧二进制，于是 `engine` 仍显示
   `Pandoc`/`Soffice`，一度看起来像"路由没生效"。**冒烟前必须先 `cargo build --release`** ——
   这与本仓既有的"部署前必须重建"纪律同源。

## 5. lite 能替代到什么程度（实测矩阵，非推测）

| 能力面 | 现有外部引擎 | 体积 | lite 替代 |
|---|---|---|---|
| 文档格式族（md/docx/docm/doc/epub/html/ipynb/wiki/org/rtf/odt/dbk/jats/tex/latex/txt/fb2/opml/rst/dokuwiki/textile/mobi ↔ 及任意组合） | **pandoc** | ~200 MB | ✅ 纯 Rust（`combo_convert` 已覆盖交叉边） |
| OOXML → html/pdf（docx/xlsx/pptx） | **LibreOffice** | ~700 MB | ✅ 纯 Rust（含字体子集化，PDF 实测 10 KB 级） |
| 旧版 OLE 二进制 `.doc/.xls/.ppt` | **LibreOffice** | 同上 | ✅ 纯 Rust（`ole2.rs` + `*_legacy.rs`，矩阵实测 ✓） |
| 电子书 `epub/mobi → md` | **calibre** | ~150 MB | ✅ 纯 Rust |
| PDF 文字提取 | **poppler** | ~80 MB | ✅ 纯 Rust（`pdf_text.rs`） |
| 压缩 / 解压 / AES-256-GCM / 批量重命名 | （原本没有） | — | ✅ **新增能力** |
| 音视频 probe / 抽帧 / gif / wav / collage | **ffmpeg** | ~100 MB | ◐ 需 FFmpeg **dll**（~141 MB，见 D3） |
| OCR | **tesseract** | ~80 MB | ❌ 仍需（可选包） |
| HEIC / RAW | heic / dcraw | ~30 MB | ❌ 仍需（可选包） |

⇒ **可删的 `engines/` 约 1130 MB（pandoc+LibreOffice+calibre+poppler 部分）**；OCR/HEIC/RAW/ffmpeg 归入可选包。

## 6. 风险

| # | 风险 | 处置 |
|---|---|---|
| R1 | **矩阵双份**（`matrix.rs` ↔ `ConversionMatrix.cs`）改一处必改另一处 | 两侧同批改 + 两侧测试都跑；C3 单独成步，不和 C2 混 |
| R2 | `ConversionMatrixTests` 有精确断言（如"pdf 源全目标"），矩阵改路由会让一批断言失败 | 预期内，属于"改判据不是改实现"；逐条核对而不是直接改期望值 |
| R3 | lite 的**输出质量**低于 LibreOffice（无浮动/页眉页脚/表格线；PDF 无 `/W` 宽度数组 ⇒ 数字偏宽） | 诚实边界已在 `README` 声明；先按"轻量优先"上线，复杂排版回退列为后续 |
| R4 | vendored crate **29 条编译警告**（9 未用变量 / 6 多余 mut / 3 未用 import / 3 个死函数） | 不阻塞；建议 C2 一并清掉（与 core 的 clippy-0 纪律对齐） |
| R5 | edition：lite 是 `2021`，主仓其余 Rust crate 是 `2024` | 先保持 2021（升 2024 是机械但面广的重构）；列入 C6 后的独立项 |
| R6 | 第三方依赖合规（`serde/zip/image/pulldown-cmark/scraper/aes-gcm/tar/...`） | C6 补 `THIRD-PARTY-NOTICES.md`；许可证需逐项核对 |

## 7. 决策记录（2026-09-20 用户拍板）

- **D1（已定）：不做双路径，直接删 `engines/` 里被替代的部分。**
  理由（用户原话）："反正这个部分的功能对于用户来说是新的，没用过带外部功能的。"
  **保留清单（用户指定）：`tesseract`（OCR，lite 唯一替代不了）+ `ffmpeg`（音视频）+ `poppler`（PDF，8MB）。**
  **删除清单：`libreoffice` 1522MB + `calibre` 627MB + `pandoc` 223MB = 2372 MB。**
  `heic` / `dcraw` 本就不在 `engines/`（按需下载），不受影响。
  仍保留"先做 C3/C4 再删"的顺序 —— 这不是双路径，是**避免中间态出现"功能坏了"的假象**。
- **D3（已定）：音视频本轮不带 FFmpeg dll**（选 ①）。ffmpeg 目录保留，但**音视频转换从矩阵里摘掉 ⇒ 菜单项自然隐藏**；
  将来接 DLL 时恢复矩阵行即可（`FfmpegEngine` 与目录都还在）。
- **D2（建议：本轮只做引擎侧）**：压缩/解压/加密/批量重命名是 lite 白送的新能力，但接进右键菜单要新增
  菜单项 + 加密口令输入 UI —— 建议 UI 另立一项，本轮先只保证引擎侧可用（矩阵里不登记 ⇒ 菜单不出现）。
- **D4（建议：标注）**：lite 的边界（`md→docx` 不支持图片/表格/代码块；`docx→pdf` 无浮动/页眉页脚）
  建议在菜单里以"轻量转换"分组标注 —— 与仓库"失败不得正常化 / 诚实边界"的纪律一致。

## 8. Definition of Done

1. 全新安装（**无 `engines/`**）后：右键任意 `.docx`/`.md`/`.epub`/`.doc` → 格式转换子菜单**有货**、点击**出结果**。
2. `convert-engine run` 对矩阵里 lite 边的输出**含 `engine:"lite"`** 且 `result.ok=true`。
3. `cargo test`（convert-lite + convert-engine）与 `dotnet test shell-convert-tests` 全绿。
4. `publish.ps1` / `publish-modules.ps1` 出包冒烟：**主包无 `engines/`**，`06-可选引擎` 只含 tesseract/poppler/ffmpeg（486MB）。
5. 全量门禁绿（含 `verify-architecture-guard`、`verify-system-integration`、`verify-md-*`）。
6. 文档回写完成：`shell-convert/README.md`、`docs/build-release.md`、`THIRD-PARTY-NOTICES.md`。
