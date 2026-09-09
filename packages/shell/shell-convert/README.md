# shell-convert · 文档转换子系统

ConversionMatrix（源→目标矩阵）+ EngineRegistry（多引擎候选链解析/真实探测缓存）+ 泛化 ConversionService 驱动右键菜单的完整文件转换能力（计划：docs/plans/2026-09-03-conversion-matrix-markdown-hub.md）。

## 菜单形态（Win10 六红线，§6.3 形态 A）

- 第一层平铺高频「转 PDF」（矩阵含 pdf 且引擎可用时，多选显示数量）
- 「转换为 ▸」子菜单承载其余目标（白名单 Submenu：集合语义/动态列表）；上次成功目标置顶
- 多选整集：全 PDF ≥2 →「合并 PDF」；全图片 ≥2 →「合成 PDF」（第一层直达）
- 单 PDF 的「拆分 PDF（每页一文件）」走 Shift 扩展位（低频归宿是 Shift 扩展）
- 隐藏优先：无可用引擎的目标整项不显示

## 矩阵速览

| 源 | 目标（引擎主→兜底） |
|---|---|
| Word 族（doc/docx/docm/rtf/odt/wps） | pdf（soffice→COM）、docx、odt、rtf、txt、html、epub（pandoc） |
| Excel 族（xls/xlsx/xlsm/et） | pdf、xlsx、ods、csv（UTF-8 filter） |
| Presentation（ppt/pptx/pps/dps） | pdf、pptx、odp |
| md（枢纽 IR） | html/txt（纯托管 Markdig）；docx（pandoc→两跳）；pdf（两跳） |
| txt/log | md、html（纯托管） |
| html/htm | md（ReverseMarkdown）；pdf（soffice） |
| 图片（png/jpg/bmp/gif/tif/webp） | 互转（webp 经 SkiaSharp）；合成 pdf（PdfSharpCore） |
| pdf | png/jpg/txt（Poppler，按需）；合并/拆分（PdfSharpCore） |
| 音视频 | mp4/mkv/webm/gif/mp3/wav/flac/m4a（FFmpeg，按需） |

## 引擎分层

- SofficeEngine：LibreOffice 子进程，filter 参数化（txt/csv 锁 UTF-8 常量）
- ComPdfEngine：Office/WPS COM ExportAsFixedFormat（仅 pdf 兜底，STA 线程）
- ManagedEngine / TwoHopEngine：纯托管 md 链（零外部依赖）+ html 中转两跳
- ManagedImageEngine / PdfComposeEngine：图片互转（SkiaSharp 补 webp）与 PDF 组合
- PopplerEngine / PandocEngine / FfmpegEngine：外部引擎按需（真实 --version 探测 + env 覆盖）
- EngineDownloader：按需下载五要素（多镜像/SHA256/单实例/可取消/清半包）； 校验和未配置 = 下载禁用（无校验不下载，诚实红线）

## 安全输出（继承不重写）

同卷临时目录 .bd-convert-{pid}-{guid} → 回读验证（失败恰好重试一次）→ File.Move 原子发布 → 重名自动 (2)(3) 序号永不覆盖 → 输出≠任一输入 → 错误六分类 → convert/* 事件（含 batch-finished 计数）。

## 测试

shell-convert-tests：31 用例（矩阵/参数/安全输出/服务批量/md 链/下载器红线），全部不起真进程。


## Known Limitations

- 外部引擎（7-Zip / WinRAR / LibreOffice soffice）需用户自行安装；缺失时对应格式在右键菜单中置灰，不影响内置 zip / 图片转换。
- 解压路径穿越防护（C2）依赖引擎的 list 命令（7z l -ba / UnRAR lb）；极旧版本引擎若不支持 list，降级为正常解压（引擎自身报错）。
- 大文件 / 批量转换耗时与外部引擎相关，UI 不阻塞但任务完成时间不可控。
