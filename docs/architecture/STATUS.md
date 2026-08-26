# STATUS.md — 当前阶段与交接指针

> 地位：**新会话接手的第一入口**。任何新 AI 会话/新成员动手前必须先读本文件。
> 更新义务：每阶段结束、每次重大决策落地后更新本文件（阶段、HEAD、下一步、待办）。

## 当前状态

| 项 | 值 |
|---|---|
| 当前阶段 | **P1 已完成；P2 未开始；Dock 骨架进入固定应用扫描/持久化原型阶段** |
| 已完成 | P0 内核重建（v1.2.0）+ P1 内核基础设施（Loader/HMR/Timer/Diagnostics） |
| 测试 | 222 个测试，221 绿 / 1 灰（窗口服务 headless 跳过） |
| 更新时间 | 2026-08-21 |

## 已实现范围（摘要）

- 内核（`packages/kernel/`）已完成 `kernel/kernel`、`kernel-loader`、`kernel-hmr`、`kernel-timer` 四个包。
- Shell 核心（`packages/shell/shell-core`）已完成毛玻璃、桌面几何与基础动画服务。
- 宿主（`host/`）已完成内核装配与 WPF 启动入口。
- Dock（`packages/shell/shell-dock`）当前为固定应用骨架与开始菜单扫描雏形阶段。

## 未实现范围（摘要）

以下模块仍处于设计阶段，尚未具备完整生产可用实现：

- shell-bar / shell-desktop / shell-window-manager / shell-theme
- shell-mission-control / shell-spotlight / shell-status-bar / Companion

## 交接与收工

- 交接：读本文件 → 读 `AGENTS.md` 与 ADR-001 → 跑门禁基线 → 查最新决策记录。
- 收工：门禁全绿 → 决策记录补全 → 更新阶段与下一步 → 提交信息带范围前缀。

## 关键指针

- `docs/architecture/ADR-001.md`、`docs/architecture/ADR-002.md`
- `scripts/run-gates.ps1`、`scripts/AGENTS.md`
