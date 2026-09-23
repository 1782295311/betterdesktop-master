<div align="center">

# 🫀 Better Desktop Cordis

**轻量级 Windows 桌面增强套件** —— Rust 内核常驻，组件按需拉起，引擎进程内化

零外部依赖 · 自包含发布 · 六层架构 · 门禁先于业务

`net8.0-windows` · WPF · Rust · 75 .NET 工程 + 8 Rust crate

</div>

---

## ✨ 特性一览

| 能力 | 说明 |
|---|---|
| 🖥 **桌面外壳** | 托盘 / 启动器 / 设置中心 / 桌面控制 / 任务栏 一体化 |
| 🔔 **Rust core 常驻** | 唯一常驻进程：托盘图标、全局热键、控制管道、监督、电源、安全、组件门控 |
| ⚡ **按需拉起** | 其余组件随用随启，空闲时仅 1 个进程常驻 |
| 🖱 **右键菜单** | 原生 COM 扩展（in-proc + IExplorerCommand 双路） |
| 📋 **剪贴板** | 剪贴板历史 + OCR 识别 + IPC 管道 |
| 🔤 **统一 OCR 引擎** | **Rust + ONNX Runtime**（PP-OCRv6，tiny/small/medium），替代外部 tesseract 与 WinRT 单一档 |
| 🧩 **格式转换** | Rust 进程内实现（无外部 exe 依赖），告别 240MB 引擎树 |
| 🌐 **翻译引擎** | 独立 Rust 引擎进程 |
| 🔧 **诊断与故障手册** | `--diagnostics-export` 产品状态采集（部署指针/整合状态/core 门控/关键文件）+ `docs/runbook/` 故障处置手册 |
| 🔄 **更新与恢复** | 自更新器（SHA256 清单校验）+ 崩溃恢复入口 |
| 🛡 **升级兼容** | settings / 组件表 schema 版本化，降级写闸防旧版回写 |

---

## 🏗 架构

<div align="center">

```
┌────────────────────────────────────────────────────────────┐
│                        entry 入口层                         │
│   Launcher · Host · Cli · Tray · Settings · Recovery      │
│              Updater · DesktopControl                     │
├────────────────────────────────────────────────────────────┤
│                        engine 引擎层（Rust 独立进程）        │
│   convert-engine · engine-ocr · engine-translate ·        │
│              engine-index                                 │
├────────────────────────────────────────────────────────────┤
│                        surface 外壳层                       │
│      shell-core · shell-ocr · shell-context-menu · …     │
├────────────────────────────────────────────────────────────┤
│                        kernel 内核层                        │
│        kernel · kernel-hmr · kernel-loader · timer        │
├────────────────────────────────────────────────────────────┤
│                        contract 契约层                      │
│               协议向量 · IPC 契约 · 跨进程字面量             │
└────────────────────────────────────────────────────────────┘
              ▲ 唯一常驻：Rust core（托盘/热键/监督/门控/安全）
```

</div>

- **Rust `core` 唯一常驻**，其余组件按需拉起 —— 替代传统"多进程常驻 + 网状互拉"架构
- **六层架构**（contract → kernel → surface → engine → entry）单向依赖，无环
- **引擎进程内化**：转换 / OCR / 翻译 / 索引均为 Rust 独立进程，无外部 exe 依赖
- 75 个 .NET 工程 + 8 个 Rust crate，依赖无环且单向由门禁强制

---

## 🚀 快速开始

```powershell
# 构建全部
dotnet build BetterDesktop.slnx

# 全量门禁（20+ 条，唯一入口）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1

# 构建 Rust core（唯一常驻进程）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/build-core.ps1

# 发布内测包（自包含，含 manifest/version 清单）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/publish.ps1

# 产物
#   dist/BetterDesktop-<stamp>/  → 自包含，解压即用
```

---

## 📁 目录结构

```
better-desktop-cordis/
├── packages/               # 功能包（全部功能块收于此处）
│   ├── entry/              #   入口可执行层
│   ├── kernel/             #   Cordis 内核
│   ├── shell/              #   外壳 UI 部件（含 shell-ocr）
│   └── api/                #   契约层
├── core/                   # Rust core（唯一常驻进程）
├── engine/  engine-ocr/  engine-translate/  engine-index/
│                           # Rust 独立引擎进程
├── engines/                # 第三方引擎资产（onnxruntime 等）
├── native/                 # 原生组件（右键扩展等）
├── installer/  protocols/  tools/
├── scripts/                # 构建 / 门禁 / 发布脚本
└── docs/                   # 架构文档 / 故障手册 / 审计
```

---

## 🛡 工程纪律

- **门禁先于业务**（P0 地基）：宪法、门禁、决策记录制度先于业务代码
- **20+ 条门禁**：依赖无环、安全基线（5 层防线）、架构守卫、协议契约、格式基线、覆盖率
- **兼容性护栏**：schema 版本化 + 降级写闸，升级回滚路径受控
- **诊断闭环**：产品状态采集 + 故障处置手册，现场可复现
- **AGENTS.md 全仓行为规范**，AI 可协作开发

---

## 📄 文档导航

| 想看什么 | 去哪里 |
|---|---|
| 当前状态与交接 | `docs/architecture/STATUS.md` |
| 架构总览 | `docs/architecture/2026-09-20-current-architecture.md` |
| 实测快照 / 部署流程 | `docs/architecture/2026-09-20-runtime-facts.md` |
| 故障处置手册 | `docs/runbook/`（装完没反应 / 右键菜单不出现 / 托盘或壳面不见了） |
| 架构决策记录 | `docs/architecture/ADR-001.md` |
| 门禁唯一入口 | `scripts/run-gates.ps1` |
| 全仓行为规范 | `AGENTS.md` |

---

## 📜 许可证

**CC BY-NC 4.0**（署名-非商业使用 4.0 国际版）

分享与修改请注明出处，禁止商业用途。详见 [LICENSE](LICENSE)。

<div align="center">

**Better Desktop Cordis** · 2026

</div>
