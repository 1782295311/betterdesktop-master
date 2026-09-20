# Agent Note: 三层解耦——UI 壳 / 常驻 Agent / 托盘控制面 + CLI 作为唯一命令入口

Status: implemented

## Problem

全部能力原先装配在宿主进程内（WPF 窗口 + 钩子 + DispatcherTimer），宿主退出即全部停止。用户诉求是"退出主程序后大部分功能仍正常工作"，参照形态是功能不放在主程序里、而放在注入件与独立常驻进程里。同时，任何"需要宿主动作"的命令都依赖宿主在场，右键与命令行入口在宿主未运行时只能报错。

## Decision

按"是否需要 WPF 视觉"把能力分三层，并引入两个新可执行体：

| 层 | 可执行体 | 内容 |
|---|---|---|
| UI 壳 | `BetterDesktop.Host.exe` | 自绘桌面/菜单栏/Dock/开始菜单/弹层等视觉面；可随时退出重启 |
| 常驻能力 | `BetterDesktop.Agent.exe`（`agent/`） | 无窗口 Cordis 宿主：`status` / `app-source` / `pinning` / `search` / `convert` 常驻；按 `agent.yml` 装配 |
| 控制面 | `BetterDesktop.Tray.exe`（`tray/`） | 托盘图标与菜单：功能开关、启停宿主、检查更新、自启、组件状态、应急恢复 |
| 命令入口 | `BetterDesktop.Cli.exe`（`BetterDesktop.Cli/`） | headless 执行能力（转换/压缩/解压/桌面切换）与"需宿主"命令的转发；右键注册一律指向它 |

关键约定：

- **独占能力门控**：桌面图标显隐与任务栏外观不能被两个进程同时持有。`HostPresenceWatcher` 以廉价轮询探测壳是否在场，`ExclusiveCapabilityHost` 据此动态装载/卸载（装载走 `context.Plugin(...)`，卸载走 `DisposeAsync()`），探测失败时**保守按"壳在场"处理**。
- **装配纪律**：`agent.yml` 只列 `Program.cs` 已注册工厂的 name（LoaderService 对未注册 name fail-closed）；视觉插件不得出现在 `agent.yml`；两进程共享同一份 `settings.json`，各自持有实例并且只经既有 SaveMutex 落盘互斥。
- **不新建 IPC 服务框架**：跨进程消费能力时按需二选一——文件/管道命令，或两边各起一份只读廉价数据。壳要消费 Agent 服务暂不做服务代理层。
- **命令契约兼容**：`--menu-cmd` 三入口语义不变，CLI 只做路由（headless 直执行 / 需宿主则转发并提示），绝不偷偷拉起 WPF Application 或静默宿主。

## Alternatives considered

- **把插件在进程间热迁移**：不存在可搬移"运行中插件"的进程间机制，命令管道是单向单次命令、没有服务图交接能力，热迁移不可行；改为启动时按依赖分流加载。
- **追求"能力包零 WPF 引用"**：实测几乎所有能力包经 `shell-core`/`shell-settings` 间接拖入 WPF，且多数用不到窗口；真正的约束是"是否创建窗口 / 是否触碰 `Application.Current`"，因此 Agent 采用无窗口 `Application` + 静默装配范式，而非强行剥离 WPF 引用。
- **新建跨进程服务代理层**：当前没有足够消费方，属于凭空造框架，明确不做。
- **让宿主后台常驻以维持功能**：与"壳可随时退出"的诉求相反，且宿主带着全部视觉与钩子，常驻代价与崩溃面都更大。

## Consequences

- 能力归属成为常驻能力与壳的硬边界：新功能必须先判定归属，放错层会导致"壳退出后功能消失"或"两进程重复加载"。
- `agent/` 与 `host/` 的插件清单需要互斥；当前 `host/cordis.yml` 仍是全量清单，`status`/`app-source`/`pinning`/`search`/`convert` 与 `agent.yml` 重复加载，收敛到 `host.yml` 属未完成项。
- 独占能力的交接存在短暂窗口（探测周期 2s），期间可能出现钩子/外观主导权的临界状态，故探测失败一律保守让位。
- CLI 成为右键与自动化的公共入口，其行为等价性（headless 能力覆盖、参数语义）成为对外契约的一部分。
- 托盘是控制面而非能力面：它只经 CLI 契约与进程探活工作，自身零包引用，避免成为第二个功能宿主。
