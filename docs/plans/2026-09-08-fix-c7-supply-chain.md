# 开发计划：修复 C7 供应链漏洞（PdfSharpCore→ImageSharp 1.0.0 根除）

> Task: 替换 PdfSharpCore 1.3.2 为 PDFsharp 6.2.4，根除 6 个项目依赖图中的 SixLabors.ImageSharp 1.0.0（3 High 公告），保持 PDF 合并/合成/拆分/加密/解密能力与行为不变。
> 证据基于 commit `31e061c9522368e7a4ff08a34718d67dda31c23e` 验证；技术力文档命中：`markdown-conversion-hub`（66-文档转换，L2 有效，已整份读入）。
> 类别：安全 / 依赖升级（compact→medium；兼容性验证已完成）。

## §1 Objective
用户可感知结果：全解决方案 `dotnet list package --vulnerable` 命中数从 6 个项目归零；PDF 转换（合并/合成/拆分/加密/解密）行为不变，加密产物安全级别不降（实为提升：128 位 → AES-256）。

## §2–3 Current Behaviour / Architecture（合并，≤15 行）
- 漏洞链：`shell-convert` csproj L26/L34 直接引用 `PdfSharpCore 1.3.2` [verified]，其 nuspec 精确锁定 `SixLabors.ImageSharp 1.0.0` + `SixLabors.Fonts 1.0.0-beta0013` [verified]，经项目引用传递进 Host / Shell.MenuBar / 两个测试工程 / Tools.ShellComponentsPlayground，共 6 项目 [verified]。
- 引擎实现：`Services/Engines/PdfComposeEngine.cs`（130 行，Merge/Compose/Split）与 `PdfSecurityEngine.cs`（70 行，Encrypt/Decrypt）[verified]，实现 `IConversionEngine` 契约（Kind/Name/CanHandle/Probe/RunAsync），注册于 ConvertPlugin，矩阵驱动（ConversionMatrix），hub-spoke 两跳。
- 红线约束（markdown-conversion-hub 文档）：NuGet 白名单仅 MIT/BCL；能力诚实（Probe 返回引擎名）；webp 不经 XImage 直接合成（Skia 中转）；安全输出/回读验证等与本次无关，不触碰。

## §4 Findings（仅 load-bearing，均 [verified]）
1. PdfSharpCore 1.3.2 nuspec：`<dependency id="SixLabors.ImageSharp" version="1.0.0" />` 精确锁定，显式覆盖 2.x 属二进制冒险（PdfSharpCore 按 1.0 API 编译），不采用。
2. PDFsharp 6.2.4（MIT，license expression 确认）：依赖仅 Microsoft.Extensions.Logging.Abstractions 8.0.3 + System.Security.Cryptography.Pkcs 8.0.1，**无 ImageSharp、无 System.Drawing.Common**；lib 含 net8.0；自带 `PdfSharp.System` 图像后端（XImage.FromFile 可用）。
3. 反射验证 6.2.4 API：`PdfSecuritySettings` 公开 UserPassword/OwnerPassword（无 DocumentSecurityLevel——该 API 已移除）；`PdfReader.Open(string, string, mode, options=…)` 带密码重载存在（3 参调用合法）；XImage.FromFile / PixelWidth / PixelHeight / XGraphics.FromPdfPage / DrawImage / XUnit.FromPoint 全部存在。
4. 临时工程实测（$TEMP\pdfsharp_probe\verify）：Merge/Compose/Encrypt/Decrypt 四路径仅 `DocumentSecurityLevel` 赋值 1 行编译失败，其余全过；XUnit 的 int 隐式转换在 6.1+ 标记过时（CS0618 警告，项目 0 警告纪律需消除）。
5. shell-convert-tests 无 PdfCompose/PdfSecurity 引擎单测（仅 ConversionServiceTests 用 FakeEngine）[verified]——引擎真实行为无测试锁定，本次补最小内核单测。
6. Probe() 返回字符串 `"PdfSharpCore"` 共 2 处（两引擎）[verified]，能力诚实契约需同步更新；EngineKind.cs L25/L43 注释提及 PdfSharpCore [verified]。

## §5 Constraint Findings
- 矩阵表驱动：**不改 ConversionMatrix、不新增格式分支**（A1 测试失败与本次无关，不处理）。
- 能力诚实：Probe 名称改为 `PDFsharp 6.2.4`；引擎类头注释与 csproj 白名单注释同步。
- 红线 11 白名单：PDFsharp 6.2.4 为 MIT，符合；ImageSharp 从此从依赖图彻底消失，「ImageSharp 仅允许 2.x（未选用）」注释改为「已根除」。
- 加密行为：1.3.2 加密强度上限 128 位（RC4/AES，库内决定）；6.2.4 默认 AES-256（更强）——注释更新，不在菜单宣称具体算法（原有口径不变）。

## §6 Proposed Changes
| 文件 | 改动 |
| --- | --- |
| `packages/shell/shell-convert/BetterDesktop.Shell.Convert.csproj` | L34 `PdfSharpCore 1.3.2` → `PDFsharp 6.2.4`；L26/L28 白名单注释更新（移除 ImageSharp 2.x 悬案，注明 PDFsharp 自带图像后端无 ImageSharp 依赖） |
| `packages/shell/shell-convert/Services/Engines/PdfComposeEngine.cs` | using 4 行 `PdfSharpCore.*` → `PdfSharp.*`；L42-43 `page.Width/Height = ximage.PixelWidth/PixelHeight` → `XUnit.FromPoint(...)`（消除 CS0618）；L35 Probe → `"PDFsharp 6.2.4"`；类头注释更新 |
| `packages/shell/shell-convert/Services/Engines/PdfSecurityEngine.cs` | using 3 行命名空间替换；删除 `DocumentSecurityLevel = PdfDocumentSecurityLevel.Encrypted128Bit` 赋值行；L55/L66 注释更新（AES-256 由库决定）；L34 Probe → `"PDFsharp 6.2.4"` |
| `packages/shell/shell-convert/Engines/EngineKind.cs` | L25/L43 注释 PdfSharpCore → PDFsharp |
| `packages/shell/shell-convert-tests/PdfEnginesTests.cs` | 新增：Merge（2×单页→2 页）/ Compose（png→1 页尺寸=图）/ Split（2 页→2 文件；1 页抛 ConversionFailed）/ Encrypt（带密码可开、无密码拒）/ Decrypt（解密后可无密码开） |
| `TECH-KNOWLEDGE/66-文档转换/markdown-conversion-hub.md` | 回写：依赖段 PdfSharpCore→PDFsharp 6.2.4；「ImageSharp 仅 2.x」→已根除；实现来源/多语言择优同步（Phase 4 回写义务） |

## §7 Implementation Sequence
1. csproj 替换包 + 注释（先改依赖，单独验证还原）。
2. 两引擎文件适配（using + XUnit.FromPoint + 删 DocumentSecurityLevel + Probe + 注释）。
3. EngineKind.cs 注释。
4. `dotnet build BetterDesktop.slnx` → 0 错误 0 警告。
5. 新增 PdfEnginesTests.cs → `dotnet test`（仅 shell-convert-tests；A1 已知失败不计）。
6. 场景走查：真实跑合并/合成/加密/解密产物并用 PdfReader 回读验证。
7. `dotnet list package --vulnerable` 全解决方案 → 命中归零。
8. 回写 markdown-conversion-hub.md。

## §8 Test Strategy
- 内核单测（新增 PdfEnginesTests）：正常/边界/异常 case（拆分单页拒绝、加密往返）。
- 构建 0 警告（XUnit 过时转换已处理）。
- 全解决方案漏洞扫描归零。
- 端到端场景：真实 PDF/图片产物生成 + PdfReader 回读页数/加密属性。

## §9–10 影响与文件清单
- 影响项目（依赖图变化）：shell-convert、shell-convert-tests、host、shell-menu-bar、shell-menu-bar-tests、tools/ShellComponentsPlayground（6 个，漏洞全部消除）。
- 对外契约：无 API/CLI/配置变更；引擎名（Probe）字符串变化仅影响菜单「能力诚实」展示，非契约。
- 新增依赖：Microsoft.Extensions.Logging.Abstractions 8.0.3、System.Security.Cryptography.Pkcs 8.0.1（MIT/BCL，全解决方案无直接引用冲突 [verified]）。

## §12 Assumptions and Open Questions
- [assumed] PDFsharp 6.2.4 在 net8.0-windows10.0.19041.0 全量构建无 TFM 冲突——待 build 实证（§13 D1）。
- [assumed] 加密往返（Encrypt→Decrypt）与 1.3.2 行为等价——PDFsharp 6.x 支持读取自身 AES-256 产物，待场景走查实证（§13 D5）。
- 不扩 scope：A1 测试失败（矩阵新增 docx/xlsx 断言过期）不在本次范围，按已知问题保留。

## §13 Definition of Done
- D1 构建：`dotnet build BetterDesktop.slnx` 0 错误 0 警告。
- D2 漏洞归零：`dotnet list BetterDesktop.slnx package --vulnerable` 无命中。
- D3 单测：新增 PdfEnginesTests 全部通过（正常/边界/异常）。
- D4 契约：ConversionMatrix / IConversionEngine / 事件与错误码零改动（git diff 仅限计划文件清单）。
- D5 场景走查（端到端）：真实生成 merged/composed/encrypted/decrypted.pdf，PdfReader 回读验证页数与加密状态。

## §14 Handoff to 技术力应用
**模式判定**：成熟工程增量 + 命中文档（markdown-conversion-hub）双轨——按工程源码锚定实现，文档红线（白名单/能力诚实/矩阵驱动）作为约束层。
**注入清单**：markdown-conversion-hub.md（已整份读入，含红线 10 条 + 已知坑 5 条）；引擎源码 PdfComposeEngine.cs / PdfSecurityEngine.cs（已全读）。
**适配参数**：命名空间 PdfSharpCore.* → PdfSharp.*；XUnit.FromPoint 消除 CS0618；删除 DocumentSecurityLevel 行；Probe → "PDFsharp 6.2.4"；csproj L34 包替换；白名单注释按 §6 文案。
**禁区（不触碰）**：ConversionMatrix.cs、EngineRegistry.cs、ConversionService.cs、IConversionEngine 契约、事件/错误码、其他引擎（Poppler/Pandoc/soffice/Markdig/Skia 路径）、菜单与设置键。
**DoD 核销表**：D1–D5 逐条验证（build/test/vuln/场景），偏离记原因交回计划侧。
