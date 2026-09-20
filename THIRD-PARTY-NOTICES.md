# Better Desktop Cordis — Third-Party Notices（第三方组件声明）

本产品使用了以下开源组件。许可证信息取自各组件实际发布元数据（NuGet nuspec / crates.io Cargo.toml / 随包 LICENSE / 项目官方仓库），完整许可证文本见各组件自带 LICENSE 文件，或访问许可证对应链接。

## 〇、设计参考（源码级借鉴）

以下开源项目的**代码实现**被直接借鉴 / 移植到本产品（代码注释有"参照 / 对齐 / 移植 / 搬运 / 源码依据"等署名）。仅参考功能形态、自行重新设计的项目不在其列。

| 项目 | 许可证 | 借鉴内容 |
|---|---|---|
| [CairoDesktop (CairoShell)](https://github.com/cairoshell/cairoshell) | Apache-2.0 | 菜单栏：Logo 菜单、Stacks 弹层（源码依据 CairoDesktop.MenuBar） |
| [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) | GPL-3.0 | 任务栏外观方案：Win10 走系统 API（SetWindowCompositionAttribute + WCA_ACCENT_POLICY，枚举值与 Windows ABI 对齐）；Win11 直接使用其编译产物 ExplorerTAP.dll（见下） |
| [Open-Shell](https://github.com/Open-Shell/Open-Shell-Menu) | MIT | 高清图标提取（参考 ResourceHelper.cpp 思路）、GDI 资源审计（参考 TrackResources.cpp 思路）、任务栏外观参考 |
| [ContextMenuManager](https://github.com/BluePointLilac/ContextMenuManager) | GPL-3.0 | 右键菜单：路径解析 / CLSID 反查 / 菜单项扩展属性——参考其功能思路自行重写（代码为本项目独立实现，含线程安全缓存等增强） |
| [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) | MIT | 系统强调色读取（参考 Uxtheme.cs 思路实现） |
| Mineradio | 待核实 | 酷狗音乐 API 契约（kugou-api.js → C# 跨语言移植） |

> **GPL 组件再分发说明**：`native/ExplorerTAP.dll` 为 [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB)（GPL-3.0）的**编译产物二进制**，作为独立组件随本产品分发（聚合分发，GPL-3.0 §5）。其源码获取途径：https://github.com/TranslucentTB/TranslucentTB （仓库含 `build_explorertap_locally.cmd` 编译脚本）。本产品其余代码为独立实现：TranslucentTB / ContextMenuManager 的思路被参考，但未复制其源码表达，不构成 GPL 衍生作品。

## 一、第三方引擎（随包分发 / 按需下载）

> **2026-09-20 变更**：格式转换的文档/表格/电子书族已改由**自研进程内 Rust 核心**完成
> （`native/convert-lite`，零外部 exe）⇒ **calibre（GPL-3.0）、LibreOffice（MPL-2.0）、Pandoc（GPL-2.0+）
> 三项已从仓库与发布物中删除**，不再随本产品分发（-2372MB）。相应地，Calibre 的 GPL-3.0 再分发义务
> 也随之消失（开源合规上是净收益）。随包分发的引擎只剩下表三项，模块名由 `06-格式转换引擎` 改为 `06-可选引擎`。

| 组件 | 用途 | 许可证 | 说明 |
|---|---|---|---|
| FFmpeg | 音视频（本轮菜单项隐藏，引擎树保留备将来恢复） | LGPL-2.1 / GPL-2.0 | 随 `06-可选引擎` 分发，目录内含 LICENSE |
| Tesseract OCR | 图片 / 截图文字识别 | Apache-2.0 | 同上（tessdata 语言包为各自许可证） |
| Poppler | PDF 渲染与文本提取 | GPL-2.0 | 同上，目录内含 LICENSE |

## 二、.NET 依赖（NuGet）

| 包 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| ManagedShell | 0.0.370 | Apache-2.0 | Windows Shell 互操作 |
| SkiaSharp | 2.88.9 | MIT | 2D 图形渲染 |
| PDFsharp | 6.2.4 | MIT | PDF 生成 |
| YamlDotNet | 16.0.0 | MIT | YAML 解析 |
| Markdig | 0.37.0 | BSD-2-Clause | Markdown 解析 |
| ReverseMarkdown | 4.3.0 | MIT | Markdown 转换 |
| TinyPinyin.Net | 1.0.2 | 未声明 * | 汉字转拼音 |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | 硬件状态监测 |
| System.Drawing.Common | 8.0.8 | MIT | .NET 平台（微软） |

\* TinyPinyin.Net 的 NuGet 包未附带许可证声明；其核心算法源自 [TinyPinyin](https://github.com/promeG/TinyPinyin)（Apache-2.0）。

## 三、Rust 依赖（引擎）

| crate | 许可证 | 用途 |
|---|---|---|
| windows | MIT / Apache-2.0 | Windows API（微软） |
| serde / serde_json | MIT / Apache-2.0 | 序列化 |
| sha2 / md-5 | MIT / Apache-2.0 | 哈希 |
| regex | MIT / Apache-2.0 | 正则 |
| base64 | MIT / Apache-2.0 | 编码 |
| flate2 | MIT / Apache-2.0 | 压缩 |
| image | MIT / Apache-2.0 | 图像编解码 |
| serde_yaml | MIT / Apache-2.0 | YAML |
| pulldown-cmark | MIT | Markdown |
| quick-xml | MIT | XML |
| lopdf | MIT | PDF |
| zip | MIT | ZIP |
| csv | Unlicense / MIT | CSV |
| **以下为 2026-09-20 vendored 进来的 `native/convert-lite`（格式转换进程内核心）所引入** | | |
| aes-gcm | MIT / Apache-2.0 | AES-256-GCM（文件加密/解密） |
| pbkdf2 | MIT / Apache-2.0 | 口令派生 |
| rand | MIT / Apache-2.0 | 随机数（盐/IV） |
| tar | MIT / Apache-2.0 | tar / tar.gz 归档 |
| encoding_rs | MIT / Apache-2.0 | GBK 等 ANSI 文本解码（旧版 Office） |
| scraper | MIT / ISC | HTML DOM 解析（html→md） |
| ttf-parser | MIT / Apache-2.0 | TTF 字形查询（PDF 字体子集化） |
| font-subset | MIT | TTF 子集化（PDF 只嵌用到的字形） |
| libloading | MIT / Apache-2.0 | 运行时加载 FFmpeg dll（音视频能力，可选） |

## 四、本产品许可证

Better Desktop Cordis 本体：**CC BY-NC 4.0**（署名-非商业使用）——见根目录 LICENSE。
