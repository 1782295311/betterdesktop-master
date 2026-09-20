# 项目体检报告（2026-09-20）

> 目的：把"路径依赖无处藏身"从口号变成**四层可复跑的检查**，并给出本轮的真实问题清单。
>
> 四层各回答一个问题：静态体检（谁在拥有什么）→ 门禁（有没有越界）→ 跨语言矩阵（同一件事是不是写了两份）→ 真机探针（机器上真实读数是否达标）。
> 落地物见文末"§7 交付物"；本文只报事实与结论。
>
> 最后核实：2026-09-20 21:13（仓内实测，非推算）。

---

## 1. 结论摘要

- **架构基线（结构与边界）是健康的**：67 个 csproj / 228 条边，**0 依赖环、0 反向依赖**；**0 处唤醒请求持有**；**0 处术语禁词**；`powercfg /requests` 无本产品条目。
- **★ P0（当晚新发现）：core 私有工作集单调增长 —— D2（< 8 MB）在长跑下已失守。** 同一 PID 在 75 分钟里 0.53 MB → **14.29 MB**（稳定不回落）→ **36.83 MB**，句柄数 510。详见 **§10.4**。故本报告不再据"早期一次读数"宣称 D2 达标。
- **★ P0（本轮最重要发现）：一条真实的"第二生命周期所有者"被现有门禁**漏掉**了。**
  - 事实：`packages/shell/shell-index-ipc/IndexEngineLauncher.cs` 持有 `EnsureEngine` + `StartDetached`，被 `shell-search/SearchPlugin.cs:74` 与 `shell-app-source/AppSourcePlugin.cs:56` **直接调用** —— 即"壳插件自己拉起进程"，与 core 的生命周期所有权冲突。
  - 它**长期对 `lifecycle-owner` 隐形**：该规则的 exe 名口径是 `BetterDesktop\.[A-Za-z][A-Za-z0-9]*\.exe`，**只匹配两段名**；本文件的目标是 `BetterDesktop.Index.Engine.exe`（**三段**）。同类被遮住的还有 `BetterDesktop.Clipboard.Engine.exe` / `BetterDesktop.Clipboard.Panel.exe`。
  - **是真机探针先抓到证据的**：`BetterDesktop.Index.Engine.exe`（PID 13688，启动于 21:20:09）的父进程**不是 core**（父 PID 15940 已退出），而当时 core 的其余 4 个按需进程父链全部可达 core。
  - 处置：修正则（允许多段）→ 门禁立刻报红 → **按棘轮纪律补登记**（`shell-index-ipc/`，`removeBy: S6`，与同形的 `shell-clipboard-ipc/` 一致）→ 恢复全绿。同时给该规则加了回归钉子单测。
- **第二类问题（静态层查不出、真机才查得出）**：`shell` 组件 **`desired=running` 但 `actual=False`，`health=degraded`**；同一会话内 `desktop` 已重启 **6 次**（core uptime 1154s）。
- **第三类问题（安全档位不一致）**：`engine` / `engine-index` 的 `CreateNamedPipeW` 传 `lpSecurityAttributes = None`（走系统默认 DACL），与 core 的**显式 ACL** 不在同一档。2026-09-14 的 Rust 重写评审已提出，当前仍存在。
- **第四类问题（机检缺口）**：`AGENTS.md` 声称"电源红线的机器校验由 `verify-architecture-guard` 执行"—— 实测该门禁只实现到 **R4**，**没有任何一条规则读电源**。同类缺口还有依赖拓扑、术语禁词、管道服务端归属。
- **第五类问题（已有欠账，已在棘轮清单里）**：`lifecycle-owner` 7 条待收敛（S5×2 / S6×3 / S7×2）、`settings-writer` 1 条（S7）。**这不是新问题，是本轮把它们摊开成了清单。**
- **门禁现状**：18 道，**16 绿 2 红**。两红（`dotnet-format` / `test-coverage`）经核实**与本次改动无关**（证据见 §6）。

---

## 2. 四层怎么查、查什么、判据是什么

| 层 | 落地物 | 查什么 | 判据 | 进 CI？ |
|---|---|---|---|---|
| ① 静态体检 | `scripts/health-check.ps1` | 谁在拥有什么、哪些是收敛欠账、哪些层**没有**门禁 | 宽而软（提示 + 交人判） | ❌ 只报不判 |
| ② 门禁 | `scripts/verify-boundaries.ps1` + 既有 17 道，唯一入口 `run-gates.ps1` | 有没有越界 | 窄而硬（退出码非 0 即失败） | ✅ |
| ③ 跨语言矩阵 | `docs/cross-language/所有权矩阵.md` | 同一件事是不是在两种语言各写一份 | 单所有者；其他语言只能请求 | ❌ 人工三问 |
| ④ 真机探针 | `scripts/probe-runtime.ps1` | 进程数 / 私有工作集 / 电源请求 / 管道 E2E / 组件健康 | D1 常驻者唯一、D2 < 8MB、D4 无条目 | ❌ 依赖机器状态 |

一键入口：`pwsh -File scripts/full-audit.ps1`（四层串起来；`-SkipGates` / `-SkipRuntime` 可裁）。

---

## 3. 第一层实测：静态体检（`health-check.ps1`）

### 3.1 三条已有棘轮：现状 + 收敛欠账

不是"有没有越界"（门禁已绿），而是**把清单的 `removeBy` 摊开**：

| 规则 | 命中 | 登记 | 收敛目标 |
|---|---|---|---|
| `lifecycle-owner`（谁拉起我们自己的 exe） | 16 个文件 | 13 条 | **S5**：`tray/ProcessBridge.cs`、`tray/TrayApplicationContext.cs`；**S6**：`host/Bootstrap.cs`、`packages/shell/shell-clipboard-ipc/`、`packages/shell/shell-index-ipc/`（本轮补登记，见 §3.6）；**S7**：`host/App.xaml.cs`、`BetterDesktop.Cli/` |
| `settings-writer`（谁写 settings.json） | 9 个文件 | 7 条 | **S7**：`BetterDesktop.Cli/HeadlessExecutor.cs` |
| `hotkey-registrar`（谁注册全局热键） | 8 个文件 | 7 条 | 全部保留（按存活期分工，已在清单逐条声明理由） |

三条棘轮**均无新增违规、无失效条目**（清单与实现自洽）。

> 注意 `lifecycle-owner` 的命中数是 **15 → 16**：本节首次跑是 15，修正则（§3.6）后变 16。**多出来的那一个就是被规则漏掉的真实所有者。**

### 3.2 管道服务端：**此前没有任何门禁**

| 位置 | 归属 | 判定 |
|---|---|---|
| `core/src/pipe.rs:414` | core 命令通道 | **显式 ACL ✓**（`lpSecurityAttributes = attrs.as_ref().map(...)`） |
| `host/MenuCommandPipe.cs:69` | 非 core（legacy 通道，已与 core 分名） | 有界读 ✓ |
| `packages/shell/shell-desktop-control/DesktopControlEntry.cs:265` | 非 core（桌面服务自有通道） | 有界读 ✓ |
| `engine/src/ipc.rs:46` | 数据面引擎自己 | **`None` / 默认 DACL** ⚠ |
| `engine-index/src/ipc.rs:78` | 数据面引擎自己 | **`None` / 默认 DACL** ⚠ |

> **判据取舍**：`core/src/pipe.rs` 的 ACL 是在 `security.rs` 构造、`pipe.rs` 引用的 —— 按**文件**判会漏；按**实参**判（第 8 个 = `lpSecurityAttributes`）才能区分 `None` 与"传了变量"。同一条规则还要排除 `format!("CreateNamedPipeW(...) failed")` 这类**日志字面量**（首版据此多报了 4 处假服务端）。

### 3.3 电源红线：**此前没有任何门禁**

**0 处持有唤醒请求**。放行与主动睡眠各若干处，全部合法：

- `packages/kernel/kernel/Core/PowerManagement.cs:77` → `SetThreadExecutionState(ES_CONTINUOUS)`，MSDN 标准的"清除唤醒请求、放行睡眠"复位用法；
- `shell-dock` / `shell-start-menu` / `shell-menu-bar` → `SetSuspendState` / `powrprof.dll`，均为**用户显式点击**的睡眠动作。

### 3.4 依赖拓扑：**此前没有任何门禁**

- **csproj 图：67 节点 / 226 边 → 0 环、0 反向依赖**（层序 `contract < engine < kernel < surface < entry`）。
- 边不仅取 `ProjectReference`，也取 `Reference` 的 `HintPath` 里含 `..` 的仓库内硬引用 —— 后者**没有编译期检查**，是这类规则的天然盲区。

### 3.5 术语禁词与命名漂移

- **术语禁词 0 处**（`TERMINOLOGY.md` §三 自称唯一真相源，此前**无任何门禁读它**）。
  - **判据必须收窄到"声明语境"**：首版直接 grep `\bCairo\w*` 得 5 处命中，**全部是行尾溯源注释**（如 `// cairoshell DesktopIcons.setPosition 同值`）—— 那是正当的出处说明，不是命名漂移。故先剥注释，再只匹配 `namespace` / 类型声明 / `using`。
- **命名漂移 4 组**（同一动作 ≥2 个动词）：
  - **≥3 个（漂移）**：`CreateEngine / EnsureEngine / StartEngine`；`CreateFile / LaunchFile / StartFile`
  - **2 个（候选）**：`LaunchDesktopService / StartDesktopService`；`LaunchHost / StartHost`
  - `StartFile`（双击启动文件）与 `CreateFile`（P/Invoke）属**不同语义**，是启发式的已知噪声 —— 故本段只给线索、不判红。

---

## 3.6 ★ P0：一条被规则漏掉的真实生命周期所有者

**症状（真机先看到）**：探针第 3 段报"1 个进程祖先不可达 core"：

```
BetterDesktop.Index.Engine.exe   PID 13688   父 PID 15940（外部，已退出）   启动 21:20:09
```

同一时刻其余 4 个按需进程的父链**全部**可达 core。所以这是一个**由非 core 发起的拉起**。

**根因（静态追到）**：`packages/shell/shell-index-ipc/IndexEngineLauncher.cs`

- 持有 `EngineExeName = "BetterDesktop.Index.Engine.exe"`、`EnsureEngine(...)`、`StartDetached(...)`；
- 调用方是**壳插件自己**：`packages/shell/shell-search/SearchPlugin.cs:74`、`packages/shell/shell-app-source/AppSourcePlugin.cs:56`。

这正是 `core/src/supervisor.rs` 模块头所否定的形态（"根因就是谁都可以拉起谁"）。

**为什么门禁没拦住**：`lifecycle-owner` 需要两个标记同时命中。`StartDetached` 命中了，但 exe 名口径是

```text
BetterDesktop\.[A-Za-z][A-Za-z0-9]*\.exe        ← 旧：只匹配"两段名"
```

而 `BetterDesktop.Index.Engine.exe` 是**三段**。实测：

```text
'BetterDesktop.DesktopControl.exe'  -match <旧口径>  →  True
'BetterDesktop.Index.Engine.exe'    -match <旧口径>  →  False   ← 规则对他瞎了
```

被同一条盲区遮住的还有 `BetterDesktop.Clipboard.Engine.exe` 与 `BetterDesktop.Clipboard.Panel.exe`。

**处置（按棘轮纪律，未跳步）**：

1. 修正则允许多段：`BetterDesktop(?:\.[A-Za-z][A-Za-z0-9]*)+\.exe`；
2. 门禁立刻由绿转红，报出 **1 条未登记违规** —— 这正是它该做的事；
3. **补登记而不是改代码**：`architecture-allowlist.json` 加 `packages/shell/shell-index-ipc/`，`why` 写清它与 `shell-clipboard-ipc` 同形、收敛方向相同，`removeBy: S6`；
4. 给该规则加**回归钉子**单测（三段名必须命中），防止未来有人把口径"优化"回两段。

门禁恢复全绿，`lifecycle-owner` 命中数 15 → **16**。

**这条发现说明什么**：`verify-boundaries` 的"空集合自检"只防"枚举坏了"，防不了"**规则写窄了**"。两者是不同的失效模式 —— 前者靠门禁自己兜底，后者**只有"另一条独立通道"能发现**（这里就是真机探针）。四层体检里，③④ 两层存在的理由正在于此。

---

## 4. 第三层实测：跨语言矩阵

完整矩阵见 [`../cross-language/所有权矩阵.md`](../cross-language/所有权矩阵.md)。人工三问结论：

1. **有没有功能没有所有者？** 没有。唯一"待人工确认"的是**桌面图标独占**（core 的独占能力 vs `shell-desktop` 自绘桌面）。
2. **有没有功能有两个所有者？** 有，但**全部是已登记的过渡期重复**，且每条都有收敛步骤（S4-4 / S5 / S6 / S7）。
3. **有没有功能的"所有者"放错了层？** 没有。

> **这一层为什么静态脚本查不出**：`health-check.ps1` 的 A8 段只能给出"概念 × 语言 命中文件数"（原料），例如"托盘图标：Rust 11 / C# 26"。**26 个命中里哪个是所有者、哪些是该删的重复**，正则判不出来 —— 必须靠"单所有者"这条语义判据 + 人工三问。

---

## 5. 第四层实测：真机探针（`probe-runtime.ps1`）

> **⚠ 本表读数是会话早期（21:13）的快照；当晚长跑后 D2 已失守 —— 见 §10.4。**
> 保留本表是因为它记录了一个真实教训：**D2 是"长跑曲线"，不是"某一刻的读数"**。

| 项 | 期望 | 实测（21:13 快照） | 结论 |
|---|---|---|---|
| D2 core 私有工作集（**快照，非长跑**） | < 8 MB | **0.78 MB** | ✅ ⚠ 见 §10.4：后来涨到 36.83 MB |
| D1 常驻者唯一 | core 1 个 | core 1 个 | ✅ |
| D1 非 core 进程祖先可达 core | 全部可达 | 4/4 可达 | ✅ |
| D4 `powercfg /requests` | 无本产品条目 | 无 | ✅ |
| 管道 E2E（CLI → core） | 返回 `desired/actual/health` | 有响应 | ✅ |
| **组件健康** | 全部 `ok` | **`shell(health=degraded)`** | ❌ |
| **desired/actual 一致** | 一致 | **`shell(desired=running, actual=False)`** | ❌ |

进程清单（全部是 core 的后代，**不是"多主常驻"**）：

```
betterdesktop-core.exe          PID 39824  父 PID 3148（外部，计划任务）
BetterDesktop.DesktopControl.exe PID 38856  父 betterdesktop-core
BetterDesktop.Clipboard.Engine.exe PID 45924 父 betterdesktop-core
BetterDesktop.Clipboard.Panel.exe PID 29072  父 DesktopControl（孙进程）
BetterDesktop.DesktopControl.exe PID 45912  父 DesktopControl（孙进程）
```

额外读数：`core uptime=1154s restarts={"desktop":6, "clipboard-engine":1, 其余 0}`。

> **读数是快照，不是常量**：同一晚 21:20 复跑时 core 私有工作集降到 0.57–0.78 MB 区间，而"祖先可达 core"由 4/4 变成 **4/5**——多出来的那个正是 §3.6 的 `Index.Engine`。**这正是真机探针该有的样子**：它不保证每次都同一组数，它保证每次都能把"此刻的形态"读出来。

> **§5.1 判据纠偏（重要）**：原方案把 D1 写成"**空闲进程数 = 1**"。本仓的实际语义是"**常驻者唯一** + 其余进程按需、用完即退"，因此 core 拉起 `DesktopControl` / 剪贴板引擎**正在跑是设计行为**，不是违规。用"个数 == 1"判据会把正常工作判红。真正的两条判据是：**① 只有一个 `betterdesktop-core`**；**② 其余每个进程的祖先链都能上溯到 core**（"谁拉的 = core"）。
>
> **§5.2 踩坑（照抄会踩）**：祖先链函数的形参**不能叫 `$Pid`** —— 它是 PowerShell 的只读自动变量，赋值失败被吞成 WriteError，函数照旧返回 `$false`，于是**每个进程都被判成"祖先不可达"**（首版 4/4 全红）。这与 `scripts/probe-processes.ps1` 里 `$pid` 那次是同一个坑。

---

## 6. 门禁现状：18 道，16 绿 2 红

新增 `boundaries` 后，`gate-registry` 报"verify 脚本 18 个全部登记，单测齐全"。

本轮对既有门禁的唯一改动是 `verify-architecture-guard.ps1` 的 `lifecycle-owner` exe 名口径（§3.6）。改动前后它都 PASS —— 区别是**改前它放过了 1 条真违规，改后它看见了并已在清单登记**。两个门禁单测合计 **39/39 绿**。

**两处红项与本次改动无关**，证据：

| 门禁 | 报错 | 证据 |
|---|---|---|
| `dotnet-format` | 新增格式违规 5532 处，绝大多数在 `launcher-tests/*.cs` | `git status --short -- launcher-tests` **为空**（该目录无任何改动）；本次只新增 `.ps1`，未新建/修改任何 `.cs`；`Directory.Build.props` 处于 modified 状态 |
| `test-coverage` | `dotnet test` 退出码 1（`全部包的 TFM 一致为 net8.0-windows`、`业务代码禁止 Console/Debug.WriteLine` 两条架构测试失败） | 与 `STATUS.md` §1 的 **B1**（`packages/` + `host/` 未提交工作 ⇒ 构建/测试不可信）一致；本次未触碰任何 C# 工程 |

---

## 7. 交付物

| 文件 | 作用 | 备注 |
|---|---|---|
| `scripts/health-check.ps1` | 一次性体检（A1–A8），只读只报、退出码恒 0 | **刻意不进 `run-gates.ps1`**：它输出收敛清单，不是判据 |
| `scripts/verify-boundaries.ps1` | 门禁：依赖拓扑（无环/单向）+ 电源红线 + 术语禁词 | 已登记进 `run-gates.ps1`（`Fast` 通道，实测 ~7s） |
| `scripts/verify-boundaries.Tests.ps1` | 该门禁的 Pester 单测 | **19/19 绿**，覆盖四组"非法输入 → 返回违规" |
| `scripts/probe-runtime.ps1` | 真机探针（D1/D2/D4 + 组件健康 + 管道 E2E） | 只读：不启停进程、不写文件、不改注册表 |
| `scripts/full-audit.ps1` | 四层一键串跑 | 阶段 3 打印矩阵路径与人工三问 |
| `docs/cross-language/所有权矩阵.md` | 跨语言所有权判定表 | 含实测矩阵、三问核对、三个待拍板缺口 |
| `scripts/verify-architecture-guard.ps1`（改） | `lifecycle-owner` exe 名口径允许多段 | 修 §3.6 的盲区；一句话的正则修正 |
| `scripts/verify-architecture-guard.Tests.ps1`（改） | 三段名命中回归钉子 | 防口径被"优化"回两段 |
| `scripts/manifests/architecture-allowlist.json`（改） | 补登记 `packages/shell/shell-index-ipc/` | `removeBy: S6`，与 `shell-clipboard-ipc` 同形 |
| `docs/cross-language/README.md`（改） | 目录表加入矩阵文档 | — |

---

## 8. 待决与后续（按优先级）

1. **★ `shell-index-ipc` 的生命周期所有权收敛（S6）** —— 现已在棘轮清单登记，但**登记不是修复**：`shell-search` / `shell-app-source` 仍在直接拉起索引引擎。收敛方向 = 改走 core 控制管道（与 `shell-clipboard-ipc` 同一条路）。**这是本轮唯一有真机物证的越权。**
2. **`shell` 组件 `degraded`（真机）** —— `desired=running` 但 `actual=False`。先查 `%LOCALAPPDATA%\BetterDesktop\logs\` 与 core 日志里 shell 的拉起失败原因；顺带查 `desktop` 为何在 1154s 内重启 6 次。
3. **同一条盲区的第二处**：`BetterDesktop.Clipboard.Engine.exe` / `BetterDesktop.Clipboard.Panel.exe` 也是三段名 —— 本轮修正则后它们的发起方（`shell-clipboard-ipc/ClipboardEngineLauncher.cs`）已命中，但**其它尚未出现的三段名 exe 可能仍无人看管**。建议新增 exe 时把"是否被门禁覆盖"作为一步（或者反过来：让规则从 `core/components.json` 读组件名清单，而不是靠正则猜名字）。
4. **电源红线的机检归属** —— `AGENTS.md` 声称由 `verify-architecture-guard` 执行，实测该门禁无此规则。现已由 `verify-boundaries` 承担；**`AGENTS.md` 的措辞应同步更正**（否则下一个人会去错的门禁里找）。
5. **引擎管道 ACL（安全档位）** —— `engine` / `engine-index` 的 `None`：要么接受并登记进 `docs/known-exceptions.md`，要么补齐 ACL。**需要一次显式决策**。
6. **管道服务端归属门禁** —— 终态"只允许 core 持服务端"目前无门禁，且两条非 core 服务端未登记。**先拍板"数据面引擎的管道算不算命令通道"**，再加规则。
7. **`dotnet-format` / `test-coverage` 两红** —— 属 B1（`packages/` + `host/` 归属未定）的下游症状，按 `STATUS.md` 的既定顺序解开 B1 后再看。
8. **命名漂移 4 组** —— 交人工判：`CreateEngine/EnsureEngine/StartEngine` 若要统一，先落一篇决策记录（术语变更须走决策记录），再改代码。

---

## 9. 与原方案的差异（判据纠偏记录）

> 原方案是写给某个"core/ + launcher/ + extensions/ + surface/"布局的仓库的。本仓是
> `core`(Rust) + `packages`(C# 插件) + `host` + entry 层，且**已有一套带棘轮的 17 道门禁**。
> 照抄会得到一份"每行都报红"的清单。以下逐条列出**改动与理由**：

| 原方案 | 本仓落地 | 理由 |
|---|---|---|
| §1.1/1.2/1.4 新建检查 | **复用**既有 `architecture-guard` 的 R1/R2/R3 判据（dot-source 同一份函数） | 判据已存在且带棘轮；体检的价值是**摊开清单的 `removeBy`**（收敛欠账），不是重算一遍是否越界 |
| §1.3「管道服务端只允许 `core/src/pipe.rs`」 | 改成**多所有者盘点 + ACL 档位标注** | 本仓实际有 5 类服务端（命令通道 / legacy 通道 / 桌面服务 / 两个数据面引擎）。按"只允许一处"判会全红，且掩盖了真正的问题（ACL 档位不一致） |
| §1.5 门禁「`SetThreadExecutionState` 只允许 core」 | 改成「**不得持有唤醒请求**（REQUIRED 类标志 / `PowerSetRequest`）」 | **原判据方向反了**：会把合法的 `ES_CONTINUOUS` 放行用法判红，同时放过 core 内持有 `ES_SYSTEM_REQUIRED` 的写法 |
| §1.6 门禁「扩展→core / surface→core」 | 改成**本仓层序** `contract < engine < kernel < surface < entry` 的单向校验 + 无环 | core 是 **Rust 程序集，连 .NET assembly 都不是**，C# 侧不可能引用它；原判据在本仓恒真，等于没查 |
| §1.8「同一动作三个以上名字」 | 实现为 A7b（≥3 判"漂移"、=2 判"候选"），并补 A7 术语禁词机检 | `TERMINOLOGY.md` 自称唯一真相源却无机检，是更基础的缺口 |
| §2「门禁规则 = 体检规则，只是从报告变成失败即红」 | **采纳**，但按本仓纪律落地：新增门禁必须**当前全绿** + Pester 单测 + 登记注册表 | 棘轮是给"有历史欠账"的规则用的；本四条实测无欠账，故做成硬规则（棘轮用错会得到一份"永不收缩的清单"） |
| §4 判据「空闲进程数 = 1」 | 改成「常驻者唯一 + 其余进程祖先可达 core」 | 见 §5.1 |
| —— | **新增** `verify-boundaries` 的"空集合自检" | 见下 |
| —— | 修正既有门禁 `lifecycle-owner` 的 exe 名口径（两段 → 任意段） | 见 §3.6：**规则写窄**与**枚举为空**是两种不同的失效，前者只有独立通道（真机探针）能发现 |

### §9.1 本轮最值得记的一条：**门禁最危险的失效是"静默通过"**

实现过程中三次踩到同一个失效模式 —— **枚举返回空集，于是所有断言都成立、门禁打印 PASS**：

1. 排除目录正则结尾漏了一个反斜杠 → 运行时抛 `Illegal \ at end of pattern` → 异常被 `Where-Object` 吞掉 → 门禁打印 **"PASS（0 项目 / 0 边 / 0 文件）"**；
2. 排除匹配用了**绝对路径**，而单测的临时树在 `%TEMP%` 下 → 整个临时树被 `\Temp\` 排除 → 单测 7 条全红（**这一条正是被单测抓住的**）；
3. 首版 A8 段把"枚举 0 个文件"读成"一切正常"。

对应的防线已落地：门禁主体验证 `csproj 节点 < 10 即判失败`（"枚举逻辑本身失效，本次判定无效"）；单测每条规则都配"非法输入 → 必须返回违规"用例。

**另有一种更隐蔽的失效：规则写窄了。** 枚举是好的、异常也没吞，只是**判据本身覆盖不到**（`lifecycle-owner` 的 exe 名只认两段，于是三段名全隐形）。这种失效**门禁自己发现不了** —— 它的输出是"合规"，与真的合规长得一模一样。本轮它由**真机探针**发现（一个父进程不是 core 的索引引擎进程），这也是四层体检必须同时保留"静态规则"与"独立通道"的原因。

**结论：门禁只要有可能"什么都没查却报绿"，那它就不算存在。**

---

## 10. 收敛轮次：让"从 core 出发可以到达任何功能"（2026-09-20 当晚落地）

> 触发：§3.6 的 P0（壳内引擎启动器自己拉起进程）。用户要求"赶紧改掉，确保从 core 出发可以达到任何功能，
> 并确保冷启动的速度"。本节记录**做了什么、凭什么说做到了、代价多少**。

### 10.1 做了什么

1. **新增壳侧唯一入口** `packages/kernel/kernel/Core/CoreComponents.cs`：组件名常量 + `Start/Stop/Toggle`
   + `TryGetRunning`（读 core 的 `status`）。它**只发请求**，不持有 exe 路径、不持有任何进程操作原语。
   kernel 是零依赖契约层 ⇒ `surface → kernel` 合法、不产生环（依赖图 226 → 228 条边，**0 环 / 0 反向依赖**）。
2. **五个拉起点全部收敛**：`shell-index-ipc`、`shell-clipboard-ipc`（含自行 `Kill` 与 `deployment.json` 解析）、
   `shell-clipboard-panel/EngineProber`、`shell-desktop-control/DesktopControlEntry`、
   `BetterDesktop.Cli/HeadlessExecutor` 的三条（桌面控制菜单 / 桌面服务 / 宿主）。
3. **可达性补缺**：`core/components.json` 7 → 9 个组件（新增 `desktop-controls`、`clipboard-panel-open`；
   `clipboard-panel` 去掉 `args`）。细节见 [`../cross-language/所有权矩阵.md`](../cross-language/所有权矩阵.md) §五之二。

### 10.2 凭什么说"做到了"（三条机器证据，不是自述）

| 证据 | 读数 |
|---|---|
| `lifecycle-owner` 命中数 | **16 → 13**；两条清单条目因"现实中不再命中"被门禁**强制删除**（棘轮收缩第一次真正被执行） |
| 新门禁 **B5**（组件名双向对账） | `boundaries` PASS：表 9 个 / C# 9 个，双向一致 |
| 依赖图 | 67 项目 / 228 边，**0 环、0 反向依赖**；`security` 800 文件 7 条规则 PASS |
| 编译 | 改动的 8 个工程（含 host / Cli / 面板 / 桌面控制）**全部 0 警告 0 错误** |
| 单测 | `shell-index-ipc-tests` **33/33**；`boundaries` + `architecture-guard` Pester **45/45** |

### 10.3 代价多少（冷启动 / 热路径）——**这是本轮的验收条件，不是顺带看看**

收敛把"每次确保引擎在跑"从一次 `CreateProcess` 换成一次管道往返，必须有读数兜底：

| 测量 | 读数 | 判据 |
|---|---|---|
| 控制往返（`--core status` × 8，**含 CLI 进程启动开销**，是上界） | min 129 / avg 135 / **max 141 ms** | < 500 ms ✅ |
| 组件冷启动（`stop index-engine` → `start` → 轮询到 `actual=True`） | `start` 受理 218 ms；**就绪共 360 ms** | < 3000 ms（一个 reconcile 周期内）✅ |

> 两条判据与新增的测量段已写进 `scripts/probe-runtime.ps1`（第 6 节常开、第 6b 节需 `-ColdStart`）。
> 注意 141 ms 里绝大部分是 **CLI 进程启动**（.NET 启动 + 反射），壳内部走 `CoreControlClient` 不付这份开销 ——
> 也就是说**管道那一跳本身不是瓶颈**，收敛没有拿交互手感换架构整洁。

### 10.4 ★ 本轮新发现的 P0：core 私有工作集**单调增长**（D2 已失守）

测冷启动时顺带读到的数字，与 §5 的早期读数放在一起看：

| 时刻（core 同一 PID 39824，启动于 20:45） | 私有工作集 | 句柄数 |
|---|---|---|
| ~21:00（会话早期） | **0.53 / 0.73 / 0.78 MB** | — |
| ~22:00 | **14.29 MB**（连测 4 次、每次间隔 15 s，**稳定不回落**） | — |
| ~22:10 | **36.83 MB**（WS 43.3 MB） | **510** |

**事实**：不是瞬时峰值 —— 间隔 15 s 连测 4 次读数完全一致（14.29），随后继续上涨；不回落。
**排除**：`running_exe_names()` 的 `CreateToolhelp32Snapshot` **没有**泄漏（`OwnedHandle` 正确释放，已核对源码）。
**候选**：`supervisor.rs:579` 每次 reconcile（3 秒一次）都 `Settings::load()` —— 读文件 + 解析 JSON。
该处注释明确写着这是**有意取舍**（"每 3 秒读一次小 JSON（毫秒级）…值得"）——
但那个取舍是拿**延迟**换来的，**没有量过内存与句柄**。本段补上的正是这个缺口。

**边界（不夸大成"已定位"）**：这只证明"某处在长"，不证明就是 `Settings::load()`。可执行的下一步是给探针加"长跑采样"
（现在探针已能量：控制往返 + 冷启动 + 私有 WS + 句柄），并优先试
"用 core 已有的 `start_config_watch`（文件变更通知）驱动失效，而不是每 3 秒读盘" —— 这条改动同时省掉
每秒 20 次的读盘、堆分配与碎片化，且**不改变**"关掉开关立即生效"的语义（变更通知比轮询更快）。

> ⚠ 本节**推翻** §5 表里 D2 的 ✅：D2（< 8 MB）在**长跑**下不成立。§1 摘要的"架构基线是健康的"应读作
> "**结构与边界**健康；**长跑资源曲线**不健康"。

### 10.5 一次自伤与它换来的加固（记下来，别再犯）

写本节时我把 `intent` 的说明**用直引号 `"` 包了中文词组** —— 那是合法的 Markdown/XML 习惯，
**非法的 JSON**。后果不是"这条规则失效"，而是 `architecture-guard` 报出 **29 条"未登记"**
（每一条都是假的：清单整个解析不出来 → 全部条目视为不存在）。**真正的原因一行都没说。**

这与 §9.1 记的"静默通过"是**同一枚硬币的两面**：

| 失效模式 | 门禁的输出 | 危险之处 |
|---|---|---|
| 枚举为空（§9.1） | PASS（0 项） | 看起来像"全绿" |
| 判据写窄（§3.6） | PASS（漏掉真违规） | 看起来像"全绿" |
| **输入清单坏掉（本次）** | **FAIL × 29，全部指向错误的文件** | 看起来像"代码坏了"，人会去改**没错的代码** |

**加固（已落地）**：两条门禁读 JSON 清单时一律 `try/catch`，解析失败直接报
"清单无法解析（JSON 语法错误）+ 本次判定无效"，**禁止**退化成"所有条目都不存在"。
`components.json`（B5 读它）与 `architecture-allowlist.json`（R1/R2/R3 读它）都已加固。

> 教训一句话：**门禁报错时，"它为什么这么说"比"它说了什么"更重要** ——
> 报错指向错误的方向，比不报错更浪费时间（它让人去改没错的地方）。
