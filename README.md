<div align="center">

# 🫀 Better Desktop Cordis

**轻量级 Windows 桌面增强套件** —— Rust 内核常驻，C# 组件按需拉起

零依赖 · 自包含发布 · 六层架构 · 门禁先于业务

`net8.0-windows` · WPF · Rust · 67 工程 · 0 警告 0 错误

</div>

---

## ✨ 特性一览

| 能力 | 说明 |
|---|---|
| 🖥 **桌面外壳** | 托盘 / 启动器 / 设置中心 / 桌面控制 一体化 |
| 🔔 **托盘常驻** | Rust `core` 唯一常驻进程：托盘图标、全局热键、控制管道、监督、电源、安全 |
| ⚡ **按需拉起** | 其余组件随用随启，空闲时仅 1 个进程（D1 达成） |
| 🖱 **右键菜单** | 原生 COM 扩展（in-proc + IExplorerCommand 双路） |
| 📋 **剪贴板** | 剪贴板历史与 IPC 管道 |
| 🔄 **更新与恢复** | 自更新器（带 SHA256 清单校验）+ 崩溃恢复入口 |
| 🧩 **转换引擎** | Rust 原生转换引擎，独立进程调用 |
| 📦 **零依赖部署** | .NET 8 自包含 + VC++ 运行库随包分发，目标机零预装，解压即用 |

---

## 🏗 架构

<div align="center">

```
┌─────────────────────────────────────────────────────────┐
│                        entry 入口层                      │
│   Launcher · Host · Cli · Tray · Settings · Recovery   │
│              Updater · DesktopControl                  │
├─────────────────────────────────────────────────────────┤
│                        engine 引擎层                     │
│              convert-engine (Rust) · 桌面控制           │
├─────────────────────────────────────────────────────────┤
│                        surface 外壳层                    │
│          shell-core · shell-context-menu · …(30+)      │
├─────────────────────────────────────────────────────────┤
│                        kernel 内核层                     │
│        kernel · kernel-hmr · kernel-loader · timer     │
├─────────────────────────────────────────────────────────┤
│                        contract 契约层                   │
│               协议向量 · IPC 契约 · 跨进程字面量         │
└─────────────────────────────────────────────────────────┘
              ▲ 唯一常驻：Rust core（托盘/热键/监督/安全）
```

</div>

- **Rust `core` 唯一常驻**，其余组件按需拉起 —— 替代传统"多进程常驻 + 网状互拉"架构
- **六层架构**（contract → kernel → surface → engine → entry）单向依赖，无环
- 67 个工程 / 228 条依赖边，**依赖无环且单向**由门禁强制

---

## 🚀 快速开始

```powershell
# 构建全部（0 警告 0 错误）
dotnet build BetterDesktop.slnx

# 全量门禁（20+ 条，唯一入口）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1

# 构建 Rust core（唯一常驻进程）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/build-core.ps1

# 发布内测包（自包含，含 manifest/version 清单）
pwsh -NoProfile -ExecutionPolicy Bypass scripts/publish.ps1

# 产物
#   dist/BetterDesktop-<stamp>/  → 579+ 文件，216MB，解压即用
```

---

## 📁 目录结构

```
better-desktop-cordis/
├── packages/               # 功能包（全部功能块收于此处）
│   ├── entry/              #   入口可执行层（8 工程）
│   ├── kernel/             #   Cordis 内核
│   ├── shell/              #   外壳 UI 部件（30+ 包）
│   └── api/                #   契约层
├── core/                   # Rust core（唯一常驻进程）
├── native/                 # 原生组件（右键扩展等）
├── installer/  protocols/  tools/
├── scripts/                # 构建 / 门禁 / 发布脚本
└── docs/                   # 架构文档 / 决策记录 / 审计
```

---

## 🛡 工程纪律

- **门禁先于业务**（P0 地基）：宪法、门禁、决策记录制度先于业务代码
- **20+ 条门禁**：依赖无环、安全基线（5 层防线）、架构守卫、协议契约、格式基线、覆盖率
- **双引擎纠错**：Rust core + C# 组件双重验证
- **AGENTS.md 全仓行为规范**，AI 可协作开发

---

## 📄 文档导航

| 想看什么 | 去哪里 |
|---|---|
| 当前状态与交接 | `docs/architecture/STATUS.md` |
| 架构总览 | `docs/architecture/2026-09-20-current-architecture.md` |
| 实测快照 / 部署流程 | `docs/architecture/2026-09-20-runtime-facts.md` |
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
