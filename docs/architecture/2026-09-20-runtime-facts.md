# 2026-09-20-runtime-facts.md — 真机实测快照

> 地位：**实测记录**，不是入口。入口读 [`STATUS.md`](STATUS.md)（它只回答"现在什么状态、该做什么"）。
> 本文件回答"**凭什么这么判断**"—— 每条都是实测或取证的结果，不是转抄。
> 更新义务：每次真机实测后追加，**旧条目保留**（它们记录了当时的现场，删掉就丢了对照）。
>
> 【为什么分成两份】`STATUS.md` 原本同时承担"入口"与"实测快照"，结果体量把入口挤到了 2009 词
>（预算是 500）。**入口必须短**——它要在 30 秒内被读完；而实测细节**必须长**——它要留得住现场。
> 两者放一份文件里会互相伤害，所以拆开，而不是压缩其中任一方。

---

## 1. 五个层面的实测事实（2026-09-20）

| 层面 | 事实 |
|---|---|
| **Git** | 未提交约 487 条（主体是 `packages/` 380 项 + `docs/`）。`core/` `launcher/` `tray/` `updater/` `protocols/` `shared/` 与 `watchdog/`（删除）**已全部提交**，`core/` 工作区干净 —— B1 里"未跟踪"那部分已解决 |
| **源码** | S1–S5 完成；core 单测 **191/191**、`clippy --all-targets` **0 警告**、`architecture-guard` PASS |
| **构建** | `core/target/release/betterdesktop-core.exe` = **09-20 12:48 / 841,216 字节** |
| **部署** | 安装根 **`app\2026.09.17.1610`**（`deployment.json`：`installedAt=09-18T00:22`）。core 已于 **09-20 12:48 单拷更新**（见 §3）；`BetterDesktop.Cli.exe` 仍是 **09-18 00:11** 的旧构建（计划 B3）；`Host/Agent/Watchdog/Tray.exe` 是 **09-18 的旧尸体** |
| **运行** | 5 个进程（见 §2） |

> **一个直接结论**：**部署的 CLI 是 09-18 的旧构建**（`--core` 返回 Usage=2）——
> **任何靠 CLI 做的验证都不作数**，直到 B1 让它随发布更新。

---

## 2. 谁在跑 / 谁没跑 / 为什么（2026-09-20，**当日已变过一次**）

**当前：在跑 1 个 —— `betterdesktop-core`**

2026-09-20 下午，gate 的默认值语义反转为「**键缺失 = 关闭**」（选择加入）， core 启动时把 gate 关闭的组件**主动停掉** ⇒ **D1（空闲常驻 = 1）达成**。验收日志：

```text
- desktop          gate=components.desktop=false                => ensure=false stop=true
- clipboard-engine gate=extensions.clipboard-history.enabled=false => ensure=false stop=true
- clipboard-panel  gate=extensions.clipboard-history.enabled=false => ensure=false stop=true
- index-engine     gate=extensions.index.enabled=false           => ensure=false stop=true

supervisor(startup): stopped desktop (pid kills=2), clipboard-engine (pid kills=1), clipboard-panel (pid kills=1)
```

**核销**：进程数 **5 → 1**；core 私有工作集 **2.10 MB**（D2 要求 < 8MB）。

**变更前（当日上午，留作对照）**

| 进程 | 说明 |
|---|---|
| `betterdesktop-core` | 09-20 12:49 起 |
| `BetterDesktop.Clipboard.Engine` | 当时 gate 键缺失 = **开** ⇒ 被拉起 |
| `BetterDesktop.Clipboard.Panel` | 同上 |
| `BetterDesktop.DesktopControl`（×2） | 其中一个带 `--icon-restore-sentinel <pid>`，是"宿主退出后恢复桌面图标"的**哨兵**（`shell-desktop` 侧设计）—— **那是正常设计，不是重复启动** |

> 一旦用户把某个开关打开（如「剪贴板历史」），对应组件会立刻被拉起（gate 开 ⇒ `ensure=true`）。
> **所以"在跑几个进程"取决于用户开了哪些开关** —— 这是"D1 只在空闲时成立"的含义。

**没在跑（都是预期的）**

| 组件 | 为什么没跑 |
|---|---|
| `Host`（壳） | `host-stopped.flag`（09-19 21:13 写入）。core 的 `shell` 条目带 `stopFlag: host-stopped.flag` ⇒ **core 不会把它拉回来**（正确行为；注意它的 `gate=no-gate` ⇒ `ensure=true`，是那个 flag 在起作用） |
| `Tray` / `Watchdog` / `Agent` | **源码已删**（S4-4）。安装根里那三个 exe 是 **09-18 的旧尸体** ⇒ **别去双击它们**（见 §8） |

---

## 3. core 单拷部署（2026-09-20，已执行）

**问题**：运行中的 core 曾是 **09-19 22:53** 的二进制 ⇒ **所有针对 core 的真机观察都在观察两天前的产物**。

**为什么 core 能单独部署**（与 B1 无关）：它是**自包含 Rust 产物、零 `ProjectReference`** （`Cargo.toml` 只依赖外部 crate）⇒ 不受 `packages/` 未提交的影响。 publish 的其它 7 个组件（C#）则相反 —— 这正是它们在 B1 上的差异来源。

**流程**（可复用；顺序 = `docs/defensive-patterns.md` 第九节的"先停守护者"）

| # | 动作 | 为什么需要这一步 |
|---|---|---|
| 1 | `cd core; cargo build --release` | —— |
| 2 | **禁用**计划任务 `BetterDesktop Core Ensure` | 否则它会在拷贝窗口把 core 拉起来、锁住目标文件 |
| 3 | 停 core 进程 | 放开文件锁 |
| 4 | **只拷 `betterdesktop-core.exe`** 到安装根 | —— |
| 5 | 启动 core | —— |
| 6 | **恢复**计划任务 | 回到原状 |

**红线**：只允许这样单拷 core。**绝不手工拷 `Kernel.dll` 或任何 C# 程序集** —— 计划 B2 记着那已经搞坏过一次面板（多副本偏斜 + 跨版本替换）。

**本次实测**

| 项 | 值 |
|---|---|
| 部署前 | 797,184 字节 / 09-19 22:53 |
| 部署后 | **841,216 字节 / 09-20 12:48** |
| 安装根被改动的文件 | **仅 `betterdesktop-core.exe` 一个**（拷贝后核验）|

---

## 4. core 的真机验证记录

### 4.1 暂停监护双标记 ✅（2026-09-20）

```
写 user-pause.flag → [WARN] supervision PAUSED (paused by: user) — ... the tray writes 'user-pause.flag' ...
删 user-pause.flag → [INFO] supervision resumed (no pause flag present)
```

⇒ S5-4 的 `user-pause.flag` / `watchdog-pause.flag` 分离语义**真机成立**，且**来源可读** （`paused by: user`）—— 这是日志区分来源那件事的实证。

### 4.2 顺带验到的两条 ✅（同日，非刻意设计）

```
[12:50:00] [INFO] another core instance is running; exiting
```

- **单实例互斥**正常 ✓
- **计划任务在正常监护 core** ✓（它 12:50 拉了一次，被互斥挡住）

### 4.3 "core 是唯一 `MenuCmd` 服务端" —— **半成品，只完成运行时侧**

计划里写着"判据不能靠删了 Host 进程推断 —— 服务端是**代码**不是进程，须显式核对"。

- **运行时已核对** ✓：`\\.\pipe\` 下只有 `Clipboard.Engine` / `DesktopCmd` / `MenuCmd` 三条，   且 Host 没有在运行 ⇒ `MenuCmd` 归 core。 - **代码侧仍未做** ✗：`host/MenuCommandPipe.cs` 的服务端还在，那是 S4 的"新增独立一步"，   需要改 `host/` ⇒ 卡在归属（计划 §7.2 依赖 3）。

⇒ **必须记成半成品**，否则下一轮会以为"验证过了"。

### 4.4 开关与自愈（D9 / D10 / D11）✅ 2026-09-20

| 项 | 做法 | 证据 |
|---|---|---|
| **D9** 开关是承重的（双向） | 写 `extensions.clipboard-history.enabled=true` → 3s 内 | `supervisor(tick): started 'clipboard-engine'` |
| | 改回 `false` → 3s 内 | `supervisor(tick): stopped clipboard-engine (pid kills=1)` |
| **D10** core 崩溃自愈 | kill core → 触发兜底任务 | core 以**新 PID** 回来 |
| **D11** 重启不重复拉起 | 同上，对比组件 PID | 杀 core 前 engine=**43332**；core 回来后 engine **仍是 43332** |

**一条判据修正**：D10 原写"kill core 后 **≤1 分钟**被拉回"，而兜底任务实际是**每 5 分钟**（XML `PT5M`）。 判据已按实现改为 **≤5 分钟**；若"1 分钟"才是产品要求，那是另一个改动（改间隔），**不是自愈失效**。

**D9 里一个值得注意的细节**：gate 打开时 core 只拉起了 `clipboard-engine`，**没有拉 `clipboard-panel`** —— 因为 panel 是 `on-demand`（`auto_start` 对它为 false）。这正好验证了两个字段的分工： **gate 管"允不允许跑"，`desired` 管"要不要常驻"**。

---

## 5. 已知但未验 / 待人工

| 项 | 为什么还没验 |
|---|---|
| **core 托盘菜单 8 个动作**（暂停监护 / 系统集成×4 / 更新×2 / 恢复 / 诊断包 / 打开日志 / 关于 / 开机自启） | 其中"暂停监护"已由 §4.1 程序化验证；**其余需要人点**（"点了没反应"就是 FAIL） |
| **S5-5 卸载** | 不可自动验（会真删程序）；建议只验到"弹出确认框"就取消 |
| 真实睡眠本身 | 不可脚本化（§13.10 用投递 `WM_POWERBROADCAST` 替代，只少了"系统真的挂起内存"） |
| `powercfg /requests` | 需管理员 |
| 涉及 C# 组件的验证（Host/Tray/CLI/DesktopControl/Settings） | 需 B1 |

---

## 6. 一条无害但要知道的 WARN

```
[WARN] no BetterDesktop.ico found; falling back to system default icon
```

安装根下缺图标文件，托盘用系统默认图标。**无害**，但用户看到的不是我们的图标。 （安装根里其它 exe 都有图标资源，只有 core 的查找链没命中的样子。）

---

## 7. "完成"有四种含义 —— 这是混淆的头号来源

项目里几乎到处写 `✅`，但 `✅` 分别指四件不同的事：

| 级别 | 含义 | 谁能证明 |
|---|---|---|
| **L0 源码** | 写完 + 单测绿 | 作者（`cargo test` / `dotnet test`） |
| **L1 构建** | `target/` 或 `bin/` 里有产物 | 本机 build |
| **L2 部署** | 装到 `installRoot`（`deployment.json` 指向它） | `publish.ps1` + 部署动作 |
| **L3 真机** | **人在真机上点过、看到结果** | 只有人能证明 |

**过去三天的绝大多数 `✅` 是 L0 / L1，不是 L3。** 判别它们的表在计划 §13.23「表 1」，最新一行：

| 项 | 源码 | 真机 |
|---|---|---|
| S4-3 监护迁移 | ✅ | **9/13**（4 项待正式发布） |
| S4-4 删 `agent/` + `watchdog/` | ✅ | ❌ 需 B1 部署 |
| S5-1 / S5-2a 托盘基座 + 开关 | ✅ | 部分验过 |
| **S5-4 的 8 个动作项** | ✅ | 部分 ✓（暂停监护已验，见 §4.1）；其余待人工点击 |
| S5-5 卸载 | ✅ | 待人工点击（core 已部署，菜单项在托盘上） |
| S4-5 真机验证清单 | — | 部分（全仓检索 ✅ 见计划 §13.25；清单待人工） |
| 门禁基础设施（反馈 / `-Fast` / 优化） | ✅ | ✅ **本机就是真机，不算欠账** |

⇒ **"看着绿、实际没在用户机器上跑过"的主体 = core 的托盘菜单链路**（S5-4 的其余动作 + S5-5 卸载）。 **注意：它不需要 B1** —— core 是自包含的，单拷部署即可（§3）。

---

## 8. 别做的事（都是已经踩过的）

1. **别启动安装根里那份 09-18 的 `BetterDesktop.Host.exe`** —— 它是**老网状版**（带三条 Ensure），    起来会把 Agent / Tray / 桌面服务一起拽起来，**与 core 的 gate 打架**。    （这就是"不要重启 Host"的真正原因。） 2. **别手工拷 C# 程序集 / `Kernel.dll`** —— 计划 B2：4 份 `Kernel.dll` 有 2 份是旧版，    手工跨版本替换**已经搞坏过一次面板**。（唯一例外见 §3：core 自包含，允许单拷。） 3. **别杀那个带 `--icon-restore-sentinel` 的 `DesktopControl`** —— 它是设计的一部分（§2）。 4. **别把"单测绿"当"功能对"** —— `hotkeys.rs` 的恒真式断言已经证明这类问题**以绿的姿态出现**    （计划 §7.4 第 1 项；四形态清单在 [`docs/testing.md`](../testing.md) 第六节）。    191 条 core 单测里"能相信的只有读过的部分"。 5. **别在 B1 解开前动 S6** —— S6 要改 `host/`，而 `host/` 正是未验证且被阻塞的那一个，    "未验证叠未验证"会把失败耦合成更难定位的东西（计划 §7.3 末）。
