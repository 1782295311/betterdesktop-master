# known-exceptions.md — 已知例外登记表

> 地位：**破例的集中账本**。与 [`docs/threat-model.md`](threat-model.md) 的「负空间」是同一手法 ——
> 不写下负空间，破例就会被未来的实现者当成「**可以再破一次**」的先例。
> 每条例外都必须在这里有一条记录；散落在模块注释里的理由**不够**（半年后问「这系统破过几次例」，
> 答案不该是一次十几次 `grep`）。

## 怎么用这份表

- **新增例外** = 在本表加一节 + 在代码处留一句 `见 docs/known-exceptions.md #N`。 - **每条必须有「触发重审」**：一句可判定的条件（不是「以后有空看看」）。   没有重审条件的例外会永久留下来 —— 那正是本表要防的事。 - 例外**不等于**免责：它仍是债务，只是被**明码标价**地记着。

---

## 1. 卸载不经 CLI 派发（core 自己拉起脚本）

- **位置**：`core/src/uninstall.rs`（模块头） - **是什么**：core 托盘「卸载 BetterDesktop…」直接拉起 `powershell.exe` 跑   `uninstall-betterdesktop.ps1`，**绕过** S5-4 定下的「业务细节 → 只派发 CLI 窄命令」规则。 - **为什么**：`uninstall-betterdesktop.ps1` 的第一步就是**停掉 core 自己**（它还删 core 的计划任务）。   若改经 CLI：core 派发 CLI → CLI 停 core → 而 CLI 是 core 的子进程 ⇒ **循环依赖**。   这不是偷懒，是结构性的：**"终结自己"这个动作无法由被终结者派发的子进程完成**。 - **触发重审**：卸载器成为一个**独立于 core 与 CLI 的 exe**（届时由 CLI 拉起它就无循环了）。

## 2. core 读业务数据（禁止清单的括号例外）

- **位置**：`core/src/main.rs` 模块头的禁止清单   （"读业务数据文件（除 `settings.json` 的扁平键、`components.json`、留痕 flag）"）；   实际读点在 `core/src/hotkeys.rs` - **是什么**：core 明面上禁止读业务数据，但被允许读**三类**：   `settings.json` 的扁平键（开关）、`components.json`（组件表）、留痕 flag。 - **为什么**：这三类是 **core 自己的运行依据**，不是业务数据 —— 它得靠它们决定"监护什么、启不启"。   真正的业务数据（剪贴板内容、索引、应用列表）core 一概不碰。   热键是其中最边缘的一例：**持有者**归 core（唯一注册点），**编辑入口**归截图插件 ⇒ core 必须是读者。 - **触发重审**：core 的运行依据改成独立文件（如 `core-state.json`）时，本例外随之消失。

## 3. `--dev` 放宽注册表路径校验

- **位置**：`core/src/shellmenu.rs`（`resolve_native_dll` 的 `PathInputs::dev_mode`） - **是什么**：dev 模式下，进程目录可以**不在** `%LOCALAPPDATA%\BetterDesktop\` 之下却仍被采信；   否则只认安装根（回退链被刻意收窄）。 - **为什么**：开发态要能测"注册 → 检查 → 修复"这条链，而开发者的构建输出不在生产目录里。 - **触发重审**：dev 模式被移除，或改为"独立测试入口 + 显式注册目标路径"时。

## 4. 两个暂停标记分离（`watchdog-pause` / `user-pause`）

- **位置**：`core/src/supervisor.rs`（`UPDATER_PAUSE_FLAG` / `USER_PAUSE_FLAG`） - **是什么**：同一个"暂时别拉回组件"的意图，用**两个独立文件**表达，判据是"任一存在即暂停"。 - **为什么**：两者**生命周期不同** —— 更新器替换完文件**自己清**；用户要**显式恢复**才清。   共用文件有一个真实的 race：先结束的一方会把另一方**仍在生效**的暂停清掉   （用户以为还暂停着，组件已被拉起）。 - **触发重审**：**S7**（配置单写者 + 入口收口）时统一到 `pause\` 目录（`updater` / `user` 两个空文件），   那时改名成本最低。**现在不改**：改名要联动更新器，而收益只是一个更准的名字。

## 5. `recovery` 的清理名单保留已退役的组件名

- **位置**：`recovery/Program.cs`（`OurProcessNames` / `OurRunValues` 里的   `BetterDesktop.Watchdog`、`BetterDesktop.Agent`） - **是什么**：一份含**已不存在**的组件名的清单 —— 看起来像"S4-4 没清干净的残留引用"。 - **为什么**：它是**历史清理名单**，不是当前组件表。S4-4 之后本机不再产生这两个进程，   但**从旧版升级上来的机器**上它们可能仍在跑、仍留着 Run 自启值。   本程序的职责正是清掉这一层残留 ⇒ 删掉这些名字 = "旧版残留不再被应急恢复清理"   —— 一个只有老用户才会碰到、且**没人会报**的故障。 - **触发重审**：安装器不再支持从 S4 之前的老版本升级。

## 6. `updater/Applier.cs` 的 Agent 重启逻辑（死代码，未删）

- **位置**：`updater/Applier.cs`（`AgentProcessName` / `StopAgentGracefully` / `StartAgent`） - **是什么**：S4-4 之后 Agent 已不存在，但这套"停/重启 Agent"的代码仍在。 - **为什么**：**取证发现它已是死代码** —— `updater/Program.cs` 的判据是   `agentOk = !agentWasRunning || StartAgent(...)`，而 `agentWasRunning` 的前置是   `IsRunningFrom("BetterDesktop.Agent", …)`，Agent 不存在 ⇒ 恒 `false` ⇒ `agentOk` 恒 `true`   ⇒ **不会报"重启异常"**，不构成用户可见故障。改它反而要动 updater 的替换流程（风险 > 收益）。 - **触发重审**：B1 之后清理 updater 时随批删除。

## 7. 卸载脚本的定位链**不回退**数据目录

- **位置**：`core/src/uninstall.rs`（`locate_script`） - **是什么**：只找「进程目录 → 安装根」，**不**像其它组件那样回退到 `%LOCALAPPDATA%\BetterDesktop`。 - **为什么**：那是**数据目录**。卸载脚本是**程序文件**；从数据目录里翻出一个   "可能是旧版本留下的"脚本去删当前程序，是最危险的那类"看起来很贴心"的回退   （旧脚本删新程序，或者更糟）。宁可如实报"未找到"，也不猜。 - **触发重审**：不预期。这是**刻意的非目标**，登记在此以免后人"顺手补全"。

---

## 关联

- 「不能做什么」的禁止清单见 [`docs/coding-standards.md`](coding-standards.md) 第三节。
- 通用工程铁律见 [`docs/engineering-conventions.md`](engineering-conventions.md)。
- 缺陷类别与防复发规则见 [`docs/defensive-patterns.md`](defensive-patterns.md)。
