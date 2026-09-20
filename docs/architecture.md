# architecture.md — 架构总览

> 本文是仓库架构的地图：先读这里，再按链接去读各包 README 与决策记录。

## 一句话架构

Better Desktop Cordis = **Cordis 内核（C# 复刻）+ 外壳插件（一切皆插件）+ 壳与常驻能力分离**。UI 与内核走同一条插件通道；性能敏感与不可信执行下沉为独立原生进程。

## 内核要点（方向冻结于 ADR-001）

| 概念 | 一句话 |
|---|---|
| Context | 服务图：提供/读取服务、extend/isolate/intercept、托管 effect 与事件 |
| Plugin | 一切皆插件；声明 `inject` 依赖，依赖可用前保持 PENDING，服务变化时自动重载 |
| Fiber | 运行时状态机：PENDING→LOADING→ACTIVE→FAILED→UNLOADING→DISPOSED |
| Effect | 注册的清理器；卸载时逆序执行（Enumerable.Reverse + foreach，非并行）、单条异常隔离 |
| Events | 五种分发：emit / parallel / serial / bail / waterfall（洋葱模型） |
| Loader | 声明式插件树（cordis.yml），不做拓扑排序，依赖排序交给 inject |
| HMR | 双 AssemblyLoadContext 切换 + 配置迁移 + 失败回滚（ADR-003 D1 追认） |
| 替换 | 角色插槽：同名服务 provide 即替换、先立后破、崩溃熔断回退（`docs/pluginization.md`） |
| AI 控制面 | 能力目录 + 分级授权 + 人在环 + 可审计（`docs/ai-control.md`） |

## 分层

```
kernel/*      内核本体（零 UI 依赖）
api/*         契约层：各能力域接口与模型
shell/*       外壳能力与数据服务（插件）
host/         薄宿主 exe：装配内核 + 视觉插件 + 启动自检
agent/ tray/  常驻能力宿主与托盘控制面
BetterDesktop.Cli/    命令入口：headless + 转发
core/                 Rust 地基：唯一常驻（托盘 / 热键 / 管道 / 监护 / 电源）
engine*/ native/      原生进程（剪贴板 / 索引 / 转换，命名管道 IPC）
```

目标形态（core 唯一常驻、按需六层）见 [六层模型与未来扩展点](architecture/六层模型与未来扩展点.md)。

## 工程纪律

第一层：文档贴近代码；第二层：机器对齐。细则见 [../AGENTS.md](../AGENTS.md)。

## 关键入口

- 阶段与交接：`docs/architecture/STATUS.md`
- 常驻归属：`docs/2026-09-11-resident-architecture.md`
- 跨语言承载判定：`docs/cross-language/原生重写候选评估.md`
- 宪法与红线：`docs/architecture/ADR-001.md`
- 机制唯一化：`docs/MECHANISMS.md`
- 门禁契约：`scripts/AGENTS.md`
