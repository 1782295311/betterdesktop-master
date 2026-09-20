# 当前架构快照（2026-09-20）

> 本文回答一个问题：**架构造到哪儿了，此刻机器上跑的是什么。**
> 架构模型本身看 [`六层模型与未来扩展点.md`](六层模型与未来扩展点.md)（**现行**）；
> 完整计划与逐步验收记录看 [`../plans/2026-09-18-on-demand-core-architecture.md`](../plans/2026-09-18-on-demand-core-architecture.md)。

---

## 一、一句话说清"换代"

| | 旧（截至 09-18） | 新（09-19 起，目标态） |
|---|---|---|
| 拓扑 | **8 个进程常驻**，四方互相 `Ensure`（有环） | **1 个进程常驻**：`core`（Rust），其余按需拉起、用完即退 |
| 内存 | 合计 ≈ **393.9 MB**（Host 179 / Agent 43 / Watchdog 23 / Tray 20 …） | 空闲仅 core：**私有工作集 1.18 MB**（S1 实测，目标 < 8 MB） |
| 生命周期所有者 | Host / Tray / Watchdog / Agent / CLI / Launcher **多方** | **只有两个**：`core`（常驻）+ `launcher`（一次性） |
| 谁保证"崩了会回来" | C# Watchdog（守护 5 个目标） | core 的监护器 `supervisor::reconcile`（期望态 × 实际态对账） |

一句话：**"多主 Ensure + 网状互拉" → "单常驻 + 按需 + 单一生命周期所有者"。**

---

## 二、目标架构：六层 + 单向依赖

```
⑥ 入口层     bdctl · bd-launcher · ShellDLL（explorer 内） · 托盘菜单 · 全局热键
⑤ 扩展层     system 扩展（AI 管家，经 core 调基础设施） · 普通扩展 · 技能扩展（out-of-proc）
④ 表面层     bd-shell（WPF 2D：菜单栏 / Dock / 搜索） · bd-world（UE5 3D） —— 用户可见的"脸"
③ 数据面     剪贴板历史引擎 · 索引引擎（Rust，按需 + 空闲自退）
② 基础设施   bd-infer（统一推理网关：LLM + OCR + 翻译 + API 路由）
① 地基       core（Rust，唯一常驻）：托盘 · 热键 · 控制管道 · 监护 · 电源 · 安全 · GPU 仲裁 · 独占能力
```

**依赖方向单向向下**：`⑥⑤ → ②①`，`④ → ②`，`③ → ①`。反向依赖一律禁止。

### core 只做九件事（职责编号稳定，勿重排）

| # | 职责 | 状态 |
|---|---|---|
| A | 托盘图标（`Shell_NotifyIconW` + 隐藏顶层窗口） | ✅ S1 |
| B | 全局热键（含"必须活过壳退出"的全部热键） | ✅ S4-1 |
| C | 控制管道（`BetterDesktop.MenuCmd` 服务端 + `@ctl` 形态） | ✅ S2 |
| D | 监护器（reconcile + 退避 + degraded + 计划任务） | ✅ S3 |
| E | ShellMenu 快照**触发**重建（不写内容） | ◐ S4-2（触发+退避/熔断已做） |
| F | 独占能力（任务栏外观 / 桌面图标钩子） | ◐ S4-3 |
| G | 电源事件处理（`WM_POWERBROADCAST`） | ✅ S3.5 |
| H | GPU 仲裁（数据表留位） | ⬜ S5.5 |
| I | 安全基线（管道 ACL / 调用方校验 / 路径校验） | ✅ S2.5（admin 项待补） |

**禁止清单**（core 越界就会变成"第二个 host"）：不加载 UI 插件 / 不渲染面板 / 不持业务状态 / 不读业务数据文件 / 不引用任何 C# 程序集。

### 组件表 `core/components.json` 是 core 唯一的业务数据

- `tier`：`foundation`（常驻不停） / `infrastructure`（按需） / `surface`（用户开启才跑，关必须停） / `extension`（收到 start 才拉） / `system-extension`（按需，需拉别的层时**不得自己 `Process.Start`**，要经 core）。
- `type`：`process` / `tool`（一次性跑完即退、**不进监护名单**） / `panel`。
- `power`：`onSuspend`（freeze/stop/notify） + `onResume`（`reconcile` / `probe-and-reinit` / `reinit` / `restart` / `notify`） + `keepAwake`（`true`/`"always"` 会被 schema **拒绝**）。

> **`surface` 的判定口径**（消掉"桌面算不算 surface"的反复讨论）：**关掉它，用户会问"我的桌面怎么变了"** → surface；
> **关掉它，用户只是少了个能打开的工具** → extension。按此口径，设置中心 / 剪贴板面板 / 通知面板都是 `extension` + `type=panel`。

---

## 三、此刻机器上真正跑的是什么（过渡期，**两边同时活着**）

```
【新地基】betterdesktop-core.exe          ← S1–S3.5 已实现：托盘/热键/控制管道/监护/电源/安全
              ↑ 管 道：BetterDesktop.MenuCmd（core 是服务端）
              │
【入口】  BetterDesktop.Cli.exe  ──已改指 core（--core <verb>；连不上→ensure core→重试一次）
          BetterDesktop.exe(Launcher) ──❌ 仍是 09-18 的"网状拉起版"（未改指 core）
          ShellDLL(explorer 内) ──读 shellmenu.json 快照，不直连管道
              │
【仍在服役的旧组件】
  ◻ BetterDesktop.Tray.exe        (C#) 待退役 S5 —— 托盘菜单 27 项正在搬到 core
  ◻ BetterDesktop.Watchdog.exe    (C#) 待删   S3/S4-4 —— 监护职责已迁 core
  ◻ BetterDesktop.Agent.exe       (C#) 待删   S4-4 —— 热键/右键自愈/桌面监护已迁 core
  ◻ BetterDesktop.Host.exe        (C#) 保留   —— 但三条 Ensure 待删（S6）
              │
【保留不动】DesktopControl · Settings · Capture · Clipboard.Panel/Engine · Index.Engine
```

### ⚠ 过渡期最危险的一件事：两个守护者在打架

`core` 的监护器已经上线，但旧的 `Agent` / `Watchdog` 还在跑 ——
**Agent 每 5 秒会把桌面服务拉回，与 core 的 gate 直接冲突**（计划 §7.1 明确记录："今天三次遇到旧守护者"）。

---

## 四、迁移进度（S0.5 → S8）

| 步 | 内容 | 状态 |
|---|---|---|
| S0 / S0.5 | 冻结新增连线；边界棘轮（lifecycle-owner / hotkey-registrar / settings-writer）+ 清单 + 14 条 Pester | ✅ 门禁绿（扩展生成器 `new-extension.ps1` 待做） |
| S1 | Rust core 骨架：crate + 托盘 + 隐藏窗口 + 组件表 + 单实例 + 日志 | ✅ 280KB / 私有工作集 1.18MB / 17 单测 |
| S2 | 控制管道：协议契约 + `pipe.rs` 服务端（4 实例、有界读、2s 超时、`status/start/stop/toggle/get`）+ CLI 改指 core | ✅ `bdctl status` 端到端真机通过 |
| S2.5 | 安全基线：1MiB 上限 / 管道 ACL（DENY 先于 GRANT、不收 SYSTEM、拒远程）/ 按 PID 令牌校验调用方 / 路径前缀校验 | ✅ 静态 6/6 + 正向 3/3；⬜ 管理员环境方案 A、`powercfg` 复核 |
| S3 | 监护器 + reconcile + 退避（1→30s）+ 5 分钟 3 次失败→degraded + 计划任务 | ✅ 全部真机：杀目标 3 秒内拉回、core 重启不重复拉起 |
| S3.5 | 电源事件：挂起暂停监护、唤醒时序 ⓪重置计时器→②reconcile→③管道→④托盘、`TaskbarCreated` | ✅ 恢复链真机通过；⬜ 真实睡眠需人工走一遍 |
| S4 | 删 `agent/` + 删 `watchdog/`（热键 + 右键自愈 + 桌面监护迁 core） | **进行中**：S4-1 ✅ / S4-2 ✅ / S4-3 ✅（9/13 验收） / **S4-4 ⬜、S4-5 ⬜** |
| S5 | 托盘菜单 27 项搬到 Rust（开关 11 项 / 组件启停 / 系统集成 / 更新 / 应急恢复 / 卸载） | **进行中**：S5-1～S5-4 ✅（187/187 单测） / **S5-5 ⬜** |
| S5.5 | 数据模型扩展（含 GPU 仲裁表留位） | schema 已提前落地 |
| S6 | Host 去 Ensure + 按需化收口 + ShellMenu 快照触发 | ⬜ |
| S7 | 配置单写者 = **core**（`SettingsService` 降为共享库）+ 老装机死值清理 | ⬜ |
| S8 | 架构测试 + 文档回写 + `deploy-core.ps1` / `publish.ps1` 纳入 core | ⬜ |

**验收定义**（D1/D2/D4）：空闲只 1 个进程、core 私有工作集 < 8MB、`powercfg /requests` 为空、唤醒后热键/托盘/管道/独占能力全部自恢复。

---

## 五、当前阻塞（**下一步该先解这四条**，计划 §7.1）

| # | 阻塞 | 影响 |
|---|---|---|
| B1 | **发布路径被阻塞**：`core/`、`agent/`、`launcher/`、`tray/` 未跟踪，且与 `packages/` 的并行未提交工作同树 → 没有"干净检出"能造出这些产物 | 机器上跑着三个"源码已改、部署未更"的组件（Watchdog / Agent / C# 托盘）→ 与 core 打架。**在此之前不要重启 Host** |
| B2 | **`Kernel.dll` 多副本偏斜**（4 份，2 份是 09/16、09/17 旧版） | legacy 命令被发去 core；**手工跨版本替换 dll 会直接搞坏面板**（已付过一次代价）。发布期不变量：消费者目录必须同一份构建 |
| B3 | **部署的 CLI 是旧构建**（`--core` 返回 Usage=2） | 真机上 `status` 的 E2E 校验做不了 |
| B4 | S4-4 / S4-5（删 `agent/` + `watchdog/`）未做 | 旧守护者只要有入口被拉起就会重新打架 —— **这不是清理工作，是止血** |

---

## 六、文档权威性（谁能作准、谁已过期）

| 文档 | 状态 | 说明 |
|---|---|---|
| `docs/architecture/六层模型与未来扩展点.md` | ✅ **现行** | 架构模型骨架，实现/门禁/评审以它为准 |
| `docs/plans/2026-09-18-on-demand-core-architecture.md` | ✅ **现行** | 完整计划 + 逐阶段真机验收记录（§13.x） |
| `docs/coding-standards-core-rust.md` | ✅ 现行 | core 代码规范（层边界 / 禁 Utils·Helper·Common / 注释模板） |
| `docs/threat-model.md` | ✅ 现行 | 信任边界 + 防谁/不防谁 |
| `docs/2026-09-11-resident-architecture.md` | ❌ **已被取代** | 方向是"除 Dock/菜单栏外全部常驻"，与现行相反 |
| `docs/architecture/STATUS.md` | ⚠ **已过期（09-14）** | 自称"新会话接手的第一入口"，但仍把"常驻 Agent / 托盘 / 看门狗"写成当前进程形态，且未提 core 重构 |
| `docs/architecture/entry-point-map.md` | ⚠ **过渡期** | 描述的是"网状时代"（多入口互相 Ensure）。它现在是**过渡期的现状图**，不是终态；S5/S6 落地后须重画 |

---

## 七、实测的"文档 ≠ 实现"三处（建议逐条收口）

1. **core 的可执行体名，两处文档与实物不一致。**
   - 命名表（六层模型 §七）与计划 §6.0 都写 **`BetterDesktop.Core.exe`**；
   - 实际产物是 **`betterdesktop-core.exe`**（`core/Cargo.toml:2` crate 名 `betterdesktop-core` → cargo 按 crate 名出 exe），
     CLI 侧也按这个名字找（`BetterDesktop.Cli/ComponentPathResolver.cs:30`）。
   - 命名表自称"新增运行时资产时以本表为准"，而它已经和实物不一致 —— 要么给 crate 加 `[[bin]] name = "BetterDesktop.Core"`，要么改表。
     （注：架构守卫 R4 只约束**裸名 `BetterDesktop.Core` 不许被 C# 占用**，不冲突，但会让人误以为 exe 就叫这个。）

2. **架构清单把 `launcher/Services/` 的理由写成了终态。**
   清单里写"一次性入口：ensure core 后发 ApplyDesired 即退"，但 `launcher/` 实际仍是 **09-18 的网状拉起版**
   （`launcher/Services/ComponentBootstrapper.cs` 八步：文件自检 → 清留痕 → 系统整合 → Tray → Host → 核对常驻 → Watchdog → 功能件），
   未改指 core。清单的 `why` 是**终态描述**，会被读成现状。

3. **`STATUS.md` 的"当前阶段"仍是 09-14 的"功能收尾"。**
   它是声明过的"新会话第一入口"，而新会话第一眼会看到**已经准备删除的进程形态**。建议至少加一行指针到本文件与六层模型。

---

## 七之补、发布链路：core 已是一等成员（2026-09-20）

`docs/build-release.md` 的预算只有 600 词（中文按字计词），装不下这套规则，故落在这里 —— **`build-release.md` 的"分发形态"一节应视为不完整，core 的规则以本节为准。**

**为什么要写下来**：在这之前 `publish.ps1` / `publish-modules.ps1` / `install` / `uninstall` **四处都没有 core**（实测 grep 命中 0），
core 能跑只是因为有人在开发机上**手工单拷**了一份 ⇒ **新机器装不出这套架构**。

| 项 | 规则 | 落点 |
|---|---|---|
| 一等成员 | 缺 `betterdesktop-core.exe` = 包没有生命周期所有者，整条按需架构在用户机器上不存在 | `publish.ps1` / `publish-modules.ps1` / `install-betterdesktop.ps1` 三份 `$required`（门禁 `verify-system-integration` 逐字校验三者同步） |
| 同目录 | core 按**自身目录**解析兄弟 exe（`exe_search_dirs()`：同目录优先，其次 `%LOCALAPPDATA%\BetterDesktop`）⇒ 放进子目录就找不到任何组件 | 01-主程序 / 安装根 |
| 图标 | core 的托盘图标查找链是 `<exeDir>\BetterDesktop.ico` → `%LOCALAPPDATA%\BetterDesktop\BetterDesktop.ico`；缺了只打 WARN、回退系统默认图标（"没有品牌的脸"） | 与 core 同目录，且进 `$required` |
| 构建 | **显式步骤，`publish.ps1` 只收集不构建**（与 `convert-engine.exe` / `engine/` / `engine-index/` 同规矩，见 `BetterDesktop.Shell.Convert.csproj` 的"本仓不代跑 cargo"） | `scripts\build-core.ps1` |
| 命名 | 文件名**故意是小写连字符**（cargo crate 名，CLI 的 `ComponentPathResolver.CoreExeName` 按它解析）⇒ **改名必须连 crate 一起改** | ⚠ 命名表（六层模型 §七）仍写 `BetterDesktop.Core.exe`，属文档与实现不一致 |
| 启动 | 常驻启动靠计划任务 `BetterDesktop Core Ensure`（core 首次启动自注册 + 安装器经 `Cli --core task register` 委派补齐），**不是 HKCU\Run**；任务定义只有 `core/src/task.rs` 一份 | `core/src/autostart.rs` / `task.rs` / `install` 步骤 5b |
| 卸载 | 必须**先删计划任务 → 再停 core → 再停其余**：core 是监护者（会把组件拉回），且它活着就锁住自己的 exe ⇒ 安装目录删不净 | `uninstall-betterdesktop.ps1` 步骤 0 |
| 装机启动 | 安装脚本第 7 步起 **core**（不再是托盘：核心已接管托盘图标/热键/管道/监护；同时起托盘会出现两个图标）；老包无 core 时**回退**起托盘 | `install-betterdesktop.ps1` 步骤 7 |

**日常改 core 的快捷通道**：`pwsh -File scripts\deploy-core.ps1`（建 → 禁计划任务 → 停 core → **只拷 exe + 图标** → 起 core → 恢复任务）。
core 是自包含 Rust 产物、零 `ProjectReference` ⇒ 它**不受 B1 影响**，是唯一能立刻恢复"改一行跑一次"闭环的组件。
**红线：只允许这样单拷 core；绝不手工拷 `Kernel.dll` 或任何 C# 程序集**（B2 记着那已经搞坏过一次面板）。

---

## 八、这张图什么时候要改

- 任何一步 S4-4 / S5-5 / S6 / S7 / S8 落地；
- `components.json` 的 tier/type 语义或监护规则变化；
- 旧守护者（Agent / Watchdog）任一处被删除或停手；
- core 的可执行体名或管道名变化；
- `launcher` 改指 core。
