# architecture.md — 架构总览

> 本文是仓库架构的地图：先读这里，再按链接去读各包 README 与决策记录。

## 一句话架构

Better Desktop Cordis = **Cordis 内核（C# 复刻）+ 外壳功能包（一切皆插件）+ 薄宿主**。
内核不区分「内置功能」与「第三方插件」：loader、HMR、logger、ShellBar、Taskbar 全部走同一条插件通道。

## 内核要点（P1 目标，方向已冻结于 ADR-001）

| 概念 | 一句话 |
|---|---|
| Context | 服务图：提供/读取服务、extend/isolate/intercept、托管 effect 与事件 |
| Plugin | 一切皆插件；声明 `inject` 依赖，依赖可用前保持 PENDING，服务变化时自动重载 |
| Fiber | 插件运行时：PENDING→LOADING→ACTIVE→FAILED→UNLOADING→DISPOSED 状态机 |
| Effect | 插件注册的清理器；卸载时逆序并行执行、单条异常隔离 |
| Events | 五种分发：emit / parallel / serial / bail / waterfall（洋葱模型） |
| Loader | 声明式插件树（cordis.yml），**不做拓扑排序**，依赖排序交给 inject |
| HMR | 双 AssemblyLoadContext 切换 + 旧配置迁移 + 状态迁移接口 + 失败回滚 |

## 包分层（规划）

```
kernel/*     内核本体（零 UI 依赖）：kernel / kernel-loader / kernel-hmr / kernel-timer
shell/*      外壳能力（全部是插件）：shell-core / shell-bar / shell-taskbar / shell-desktop / ...
host/        薄宿主 exe：装配内核 + 加载插件树 + 启动自检
```

## 工程纪律（双重纠错）

第一层：文档贴近代码（包级 README 100% 覆盖 + Known Limitations；决策记录；事后分析）。
第二层：机器对齐（`scripts/run-gates.ps1` 统一门禁；每条门禁必须有变红物证）。

## 关键入口

- 宪法与红线：`docs/architecture/ADR-001.md`
- 机制唯一化：`docs/MECHANISMS.md`
- 重建方案与阶段划分：`docs/architecture/PLAN-重建方案.md`
- 门禁契约：`scripts/AGENTS.md`
