# Agent Note: 系统级持久引用的写权收敛为三态（谁最后启动谁抢任务的事故）

Status: implemented — 2026-09-20，`core` 单测 204/204 绿（新增 14 条），release 产物已构建

## Problem

现象：一份**测试副本**的 core 被反复复活 —— 手工杀掉它，5 分钟内它又回来了，只能靠手工删计划任务才停得下来。

真机取证（这台机器）：

- 计划任务 `BetterDesktop Core Ensure`：`<Interval>PT5M</Interval>`、`State: Enabled`、`Last Run 18:20:02`、`Next Run 18:25:00`，动作是 `…\app\2026.09.20.1350\betterdesktop-core.exe`。
- 当时常驻 core 的**父进程是 `svchost.exe`**，`CreationDate` 与任务 `Last Run` 同一秒 ⇒ 它是被任务计划拉起来的，不是被任何用户入口。
- `core-20260920.log` 里启动间隔**恰好** 299.9–300.1 秒，连续 17 次。
- 同一张日志里 `another core instance is running; exiting` 出现 76 次（09-19）+ 20 次（09-20）。
- 产品目录下确实并存两份构建：`app\2026.09.20.1350\`（`deployment.json` 的 `installRoot`，714,240 字节）与 `app\2026.09.20.1500\`（更新的 845,312 字节）。

根因是两条既有实现叠加：

1. `task.rs::is_stable_location` 的第二条判据接受 `%LOCALAPPDATA%\BetterDesktop` 之下**任何**目录，而不只是 `deployment.json` 记录的安装根。真机上产品目录里本来就并存多个构建目录 ⇒ 那份测试副本同样被判为"稳定位置"。
2. `task::ensure()` 在 `TaskState::Stale`（任务 `<Command>` 不等于 `current_exe()`）时**无条件** `register()`，而 `register()` 写的是 `current_exe()`。

两者相乘 ⇒ 系统级任务变成"**谁最后启动谁占有**"：测试副本一旦成为常驻实例，启动 2 秒后（`main.rs` 的 `spawn_task_ensure`）就把任务改写成指向自己；杀掉它，任务 5 分钟内把它拉回来。安装版 core 下次启动再把任务抢回去，两份**互相改写**。

"一测就必然踩到"的原因也在代码里：单实例互斥量是固定字面量 `Local\BetterDesktop.Core.SingleInstance-…`（`main.rs`），与路径无关 ⇒ 测试副本与安装版**无法共存**，后启动的那个 100ms 内静默退出。于是"测新构建"被迫先停安装版 —— 而这恰好是劫持发生的前提。

## Decision

把"一个 exe 凭什么能把自己写进'每 5 分钟'/'每次开机'都会执行的地方"抽成一个独立模块 `core/src/ownership.rs`，判据与措辞都只有这一份，计划任务（`task.rs`）与开机自启（`autostart.rs`）共用。

`ownership::of(exe, install_root, local_appdata)` 是三态纯函数（无 IO、无环境变量读取，三个输入由调用方注入）：

- `Owner` —— 本进程**就是**这份部署：`deployment.json` 有记录且 exe 在其 `installRoot` 之下；或没有记录（未用安装器装过）而 exe 在产品数据目录之下。唯一的写者。
- `Tenant` —— 产品目录下、但不是安装根的那一份（测试副本 / 旧版本残留 / 并行两份）。既有的引用对它是"存在即可用"，但它**不创建、不改写、不删除**。
- `Rejected` —— 位置会在 build/clean 中消失（dev bin / `target/` / dist）。一条系统级引用都不该指向它。

`ensure()` 因此变成"**先判权限、再查任务**"：非 `Owner` 直接返回 `Ensured::Skipped`，连 `schtasks` 都不跑。`autostart.rs` 的 `enable()` 与 `disable()` 都要求 `Owner` —— "删"也要判，否则副本会关掉**部署的**开机自启，而没有任何日志说明是谁干的。

拒绝理由由 `ownership::refusal_reason` 统一措辞（谁不是主人、主人是谁），两个消费者的日志/气泡只说"是哪条引用"，不各自造句。

同时三处措辞与语义修正：`Ensured::Skipped` 的文档从"不在稳定位置"扩成"不是这份部署**或**位置不稳定"；`main.rs` 的日志不再写死 `NOT registered`（任务可能**存在且健康**，只是归别的那一份所有），改为 `not written`；`clean_legacy_values` **刻意**不受写权管（它只删已退役组件的值名，动不到活引用，属"清死引用"而非"接管引用"），这一点写进了模块头以免下次被顺手改掉。

## Alternatives considered

- **只加"旧目标已不存在才接管"这一条（最小改法）**：否。它把所有权问题留在隐式状态里，且让"一份已废弃但文件还在的部署"成为永久阻塞者 —— 任务再也建不起来，而 `ensure()` 只会一遍遍报同一个 `Skipped`。三态是把这件事**显式化**。
- **把产品目录那条判据收窄为只认 `installRoot`**：否。它破坏"未用安装器装过"这一合法形态（`deployment.json` 缺席时产品目录是唯一锚点），而该形态有单测明确支持。
- **新增一个 `Ensured` 变体（如 `NotOwner`）区分两种拒绝**：否。`main.rs` 与 `pipe.rs` 两处穷尽匹配要跟着改，而 `Skipped(String)` 的契约本来就是"拒绝 + 原因"，原因串已经把信息带全了 —— 为不增加信息的区分去改两个调用点，是净负债。
- **给测试副本换一个互斥量名，让两者能并存**：否。那只遮住冲突，不动所有权规则；而且一份能并存的副本**仍然**不该拥有系统级任务。互斥量名与本次事故是两个问题，本次不动它。
- **不改代码，只写"测试时先禁用任务"的说明**：否。实测这条说明已经在 `deploy-core.ps1` 里存在（它必须在停 core 前禁用任务），而缺陷依旧：任何一份落在产品目录的副本都会悄悄成为这台机器的生命周期所有者，且**没有任何地方会报错**。

## Consequences

- 用户侧行为变化：测试副本启动后日志出现 `scheduled task 'BetterDesktop Core Ensure': not written — this core lives at …1500… — it is a copy on this machine, not the deployment; the deployment lives at …1350….`，且**不再**改写任务。杀掉副本后，5 分钟内回来的是**部署那一份**（`Owner`），不再是副本。
- 自愈方向明确：部署那份启动时看到任务被（历史或人为）指向别处 ⇒ `Stale` ⇒ `Repaired` ⇒ 夺回。副本永远走不到这一步，因此**不存在 ping-pong**。
- 不变量落地为一句可查的话：**`deployment.json` 的 `installRoot` 就是这台机器上系统级持久引用的唯一主人**。测试新构建的正规路径因此是"把它部署成部署"（`deploy-core.ps1` 复制到 `installRoot`，或跑安装器重写 `deployment.json`），而不是在产品目录里另起一份。
- 已知边界（有意接受）：`deployment.json` 不存在时产品目录本身成为锚点，此时放在那里的副本也会是 `Owner`。这与"未用安装器装过"的合法形态无法区分，属已记录的限制，不为它再加第三个锚点。
- 取证命令（可复现）：`schtasks /query /tn "BetterDesktop Core Ensure" /xml`；`Get-CimInstance Win32_Process -Filter "Name='betterdesktop-core.exe'"` 看父进程是否为 `svchost`；`%LOCALAPPDATA%\BetterDesktop\logs\core-*.log` 看 `core starting` 的时间间隔是否为 5 分钟整数倍。
- 单测：`ownership.rs` 14 条覆盖三态的正常/边界/异常，其中 `a_second_copy_on_the_same_machine_is_only_a_tenant` 是本次事故的回归钉子；`task.rs` 里随判据搬走的两组 `is_stable_location` 测试已删除（测试跟着规则走），`cargo test` 204/204 绿。
- 未跟进（不在本次范围）：`core-20260919.log` 里 `CreateNamedPipeW` 失败 1198 次、以及 `cannot locate BetterDesktop.Cli.exe` 两处，均为独立问题。
- `docs/threat-model.md` 的字数上限 975 → 1005（按 AGENTS.md 要求在此说明理由）：T6 那一行记的是**威胁面**，本次事故给它新增了一条（"或被执行同机另一份 core 抢走"），是信息增加而非字数膨胀；该文件当时已顶在上限上，故按实测值棘轮调整，理由同步记进 `scripts/manifests/doc-budgets.manifest.json` 的 `note`。
