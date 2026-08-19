# TERMINOLOGY.md — 术语表（唯一真相源）

> 地位：本仓库命名的唯一真相源（AGENTS.md「命名」条挂靠本表）。与代码冲突时，改术语走决策记录，不改代码。
> 修订流程：新增/修改术语 → 在 `.agents/notes/` 落一篇决策记录，同一变更提交。

## 一、核心术语（Cordis 体系）

| 术语 | 英文 | 定义 | 不得混用 |
|---|---|---|---|
| 内核 | Kernel | Cordis 风格的插件运行时（Context/Plugin/Fiber/Service/Events 的 C# 复刻） | 不叫「核心」「Core」 |
| 上下文 | Context | 服务图与插件生命周期的容器 | 不叫「容器」 |
| 插件 | Plugin | 一切功能的唯一形态；内置功能与第三方扩展同一条通道 | 不叫「扩展 Extension」「模块 Module」 |
| 插件运行时 | Fiber | 单个插件实例的生命周期管理器（状态机 + 托管清理） | **禁止译作「纤程」**（那是协程概念；真实 Cordis 无协程调度器） |
| 服务 | Service | 由插件提供、可被依赖注入的能力单元 | — |
| 效应 | Effect | 插件注册的清理器（卸载时逆序并行执行） | — |
| 加载器 | Loader | 声明式插件树（cordis.yml）的装配器，不做拓扑排序 | — |
| 热模块重载 | HMR | 双 AssemblyLoadContext 切换 + 旧配置迁移 + 失败回滚 | — |
| 包 | Package | 仓库最小交付单元 = 一个 csproj + 同目录 README.md | 不叫「模块」「项目」 |
| 宿主 | Host | 薄启动器 exe：装配内核 + 加载插件树 | 不叫「主程序」 |
| 外壳 | Shell | 用户可见的桌面环境整体（菜单栏/任务栏/桌面等插件之和） | — |
| 门禁 | Gate | `scripts/verify-*.ps1` 机器校验，失败即阻断 | 不叫「检查」 |
| 物证 | Red-proof | 门禁「注入→变红→撤销→恢复绿」的取证记录 | — |
| 棘轮 | Ratchet | 白名单/基线只能减不能增的单向锁 | — |
| 决策记录 | Agent Note | `.agents/notes/` 中的决策文档 | 不叫「会议纪要」 |
| 事后分析 | Post-mortem | `docs/postmortem/NNNN-*.md` 事故复盘 | — |
| 扩展点 | Extension Point | 包对外声明、允许第三方接插的接口/插槽 | 不叫「钩子 Hook」（易与拦截混淆） |
| 语义令牌 | Token | 颜色/字体/圆角/间距等设计值的命名引用 | 不叫「变量」 |
| UI 地基 | UI Foundation | 统一 UI 资产的唯一来源规范与包 | — |
| 外壳窗口基类 | ShellWindow | 全部外壳窗口的统一基类 | 禁止裸用 `Window` |
| 降级上报 | Degradation Report | 能力不可用时显式报告而非静默 | — |
| 默认拒绝 | Fail-Closed | 未显式放行即拒绝的安全原则 | 反义：默认放行 fail-open |
| 冒烟测试 | Smoke Test | 发布/启动最小可用性验证 | — |
| 语义化版本 | SemVer | Major.Minor.Patch 版本契约 | — |
| 角色 | Role | Shell 的功能面（菜单栏/任务栏等），对应内核服务图的一个服务名 | 不叫「组件」 |
| 替换 | Replace | 社区插件以同名服务接管内置角色的行为 | 与「卸载再装」区分（替换是原子切换） |
| 回退锚点 | Fallback Anchor | 内置默认实现，任何时刻可用作回退 | — |
| 熔断 | Circuit Breaker | 连续崩溃后自动禁用插件的保护机制 | — |
| 能力目录 | Capability Catalog | 可被 AI 调用的能力的机器可读登记册 | 不叫「工具清单」 |
| 控制面 | Control Plane | AI 调用程序能力的统一入口与权限边界 | — |
| 人在环 | Human-in-the-loop | 敏感操作必须经用户确认的权限原则 | — |
| 审计日志 | Audit Log | 记录「谁在何时做了什么」的可回溯日志 | 与普通运行日志区分 |

## 二、命名映射（目录 ↔ 命名空间 ↔ 程序集）

- 包目录：`packages/<域>/<名>/`，**目录名一律小写**（多词用连字符，如 `packages/kernel/kernel-loader/`）。
- 命名空间：`BetterDesktop.<域Pascal>.<名Pascal>`（如 `BetterDesktop.Kernel.Loader`）。
- csproj 名 = 包名（PascalCase，如 `BetterDesktop.Kernel.Loader.csproj`）。
- 对应示例：目录 `packages/kernel/kernel/` → csproj `BetterDesktop.Kernel.csproj` → 命名空间 `BetterDesktop.Kernel`。
- 测试包：同目录后缀 `.Tests`（如 `packages/kernel/kernel-tests/`，命名空间 `BetterDesktop.Kernel.Tests`）。

## 三、禁用术语（旧仓语言不得混入新仓）

| 禁用 | 原因 | 替代 |
|---|---|---|
| `Cairo`、`CairoDesktop` 命名空间 | 与旧结构明确切割（ADR-001 D1） | `BetterDesktop` |
| `Extension`、`ICairoPlugin`、`IExtensionService` | 旧仓外挂式扩展宿主模型（已否决） | `Plugin`、`PluginHandle` |
| `纤程` | 对 Fiber 的误读（协程调度器不存在） | `插件运行时` |
| `主程序` | 宿主是薄装配器 | `宿主 Host` |

## 四、缩写

- `DSH` = DeepSeek Harness（方法论来源，仅文档语境使用）。
- `ADR` = Architecture Decision Record（架构决策记录）。
- `ALC` = AssemblyLoadContext（程序集加载上下文）。
