# build-release.md — 构建与发布规则

> 地位：从源码到用户手里这一段的全部规则（MECHANISMS.md M12）。

## 一、版本策略

1. 语义化版本（Major 破坏 / Minor 新增 / Patch 修复）；版本号单一来源（`version.json` 或同等物），禁止手工散改多处。
2. 破坏性变更的版本跃迁必须与 ADR 对应（extension-rules 已冻结）。

## 二、构建可复现

1. `global.json` 锁定 .NET SDK 版本；依赖锁定文件入库。
2. 任何人在干净环境跑同一 commit 应得到可预期产物；环境差异登记进 `docs/architecture/环境陷阱.md`。

## 三、发布门禁（Release Gate）

发布 = 全量门禁绿 + 冒烟 PASS + 产物清单校验 + 变更日志齐备，缺一不可（P2 落地 `verify-release.ps1`）。

## 四、产物清单与变更日志

1. 发布物清单单一来源（P2 生成器维护，`--check` 保新鲜度）。
2. `CHANGELOG.md` 每次发布维护（Keep a Changelog 格式）；变更日志与 ADR/决策记录互相可追溯。

## 五、分发形态

1. 宿主主程序：单文件自包含优先；插件程序集：**禁止 AOT / 裁剪**（动态加载与 HMR 的前提，与 ADR-001 D2 同一逻辑）。
2. **系统右键菜单命令入口 = `BetterDesktop.Cli.exe`（M3.1）**：必须与宿主同目录部署；注册表命令指向 CLI 的 `--menu-cmd <action>`（无宿主 headless 直执行）。缺失时宿主 `--menu-cmd` fallback 与 `MenuCommandPaths.GetCliPath()` 均回退宿主自身路径——**CLI 缺失 = 无宿主场景右键功能不可用**，发布物清单必须含 CLI（剪贴板历史项例外：命令保持指向 Host.exe，见 DesktopSystemMenuRegistrar）。
3. **格式转换不再依赖第三方引擎**（2026-09-20）：文档/表格/电子书族由 `convert-engine.exe` **进程内 lite 核心**完成（`native/convert-lite`，零外部 exe），`engines\pandoc\`、`libreoffice\`、`calibre\` **已从仓库与发布物删除**（-2372MB）。仍保留的三棵树（`tesseract` OCR / `poppler` PDF / `ffmpeg` 音视频）**不进主包**，只作 `06-可选引擎` 模块由安装器落位到 `<BaseDirectory>\engines\`；缺失只影响 OCR 与 PDF 渲染，转换本身照常。
4. 发布产物签名（P2）；已知限制写各包 Known Limitations。
