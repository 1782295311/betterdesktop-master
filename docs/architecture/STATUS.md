# STATUS.md — 当前阶段与交接指针

> 地位：**新会话接手的第一入口**。任何新 AI 会话/新成员动手前必须先读本文件。
> 更新义务：每阶段结束、每次重大决策落地后更新（阶段、HEAD、下一步、待办）。

## 当前状态

| 项 | 值 |
|---|---|
| 当前阶段 | **一阶段收尾定型**：内核 + 主要 Shell 功能包已实现，全量审计完成（505 .cs），B/C 类缺陷修复中 |
| 已完成 | P0 内核重建 + P1 内核基础设施（Loader/HMR/Timer/Diagnostics）；Shell 20 个功能包；宿主 WPF 启动入口 |
| 测试 | 15 个测试工程，约 375 用例；已知 1 红（A1 ConversionMatrixTests 断言过期，与功能无关） |
| 更新时间 | 2026-09-08 |

## 已实现范围（摘要）

- 内核（`packages/kernel/`）：CordisContext / PluginHandle / ResourceGovernor；声明式插件树；双 ALC 热重载 + 外部看门狗；定时器。
- Shell 核心（`packages/shell/shell-core`）：毛玻璃、桌面几何、PopupWindow 基类、动画服务。
- Shell 功能包：开始菜单（win10/win11 布局、原生右键）、菜单栏（状态条 + 扩展 + 控制中心/通知/日历/搜索面板）、状态栏（输入法/音频/网络/电池采集）、Dock、任务栏、窗口追踪、通知、转换引擎、日历、搜索、设置、应用源、桌面、固定、最近项、便签、音乐服务层。
- 宿主（`host/`）：内核装配、WPF 启动、命名管道命令、Bootstrap。

## 未实现范围（摘要）

以下模块仍处于设计或骨架阶段：

- shell-window-manager（窗口管理/平铺）、shell-theme（主题引擎）
- shell-mission-control / shell-spotlight / Companion
- shell-music 仅服务层；shell-quick-note / shell-recent 为骨架

## 交接与收工

- 交接：读本文件 → 读 `AGENTS.md` 与 ADR-001 → 跑门禁基线 → 查最新决策记录。
- 收工：门禁全绿 → 决策记录补全 → 更新阶段与下一步 → 提交带范围前缀。

## 关键指针

- `docs/architecture/ADR-001.md`、`docs/architecture/ADR-002.md`
- `scripts/run-gates.ps1`、`scripts/AGENTS.md`
- `人工审计报告-2026-09-08.md`（仓库根，一阶段全量审计与缺陷跟踪）
