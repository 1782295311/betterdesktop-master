//! 计划任务：core 崩溃后的兜底拉起（计划 §6.1-D、约束 C13）。
//!
//! # 为什么需要它
//!
//! "所有入口 ensure core 一次"只能覆盖"用户又来触达入口"的场景。若 core 在用户工作中崩溃，
//! 而用户之后**不再触达任何入口**（不看托盘、不按热键、不用右键菜单），core 就永远回不来：
//! 托盘图标消失、热键失效、右键菜单读快照还有但不干活。计划任务补的正是这一个缺口。
//!
//! 任务本身**不占内存**（Task Scheduler 服务按触发时刻启动进程），因此这条兜底的代价约为零。
//!
//! # 五个已定的决策（不许改动，改之前先读这一段）
//!
//! | 决策 | 值 | 理由 |
//! |---|---|---|
//! | 谁注册 | core 自注册，安装程序兜底 | 只靠安装程序 → core 自行搬迁目录就断；只靠自注册 → 装完没启动过 core 就没任务 |
//! | 频率 | **5 分钟** | 1 分钟的恢复优势用户感知不到，日志膨胀是真的；入口 ensure 才是主要恢复路径 |
//! | 身份 | **用户级**（当前用户 SID + `InteractiveToken` + `LeastPrivilege`） | 任务以 SYSTEM 跑会得到一个**不同 SID** 的 core，它建的管道当前用户连不上 —— 直接违反 §6.3 |
//! | 动作 | core 绝对路径 + `--from-task` + 显式工作目录 | 那个参数是**信息**（"本次启动来自兜底"），不是便利开关：core 靠它把"兜底敲门"与"用户显式启动"分开，从而能在用户主动退出后拒绝复活（见下一节）；工作目录不写会落到 `%SystemRoot%\System32` |
//! | 清理 | 卸载程序删 + core 启动时自我修正 | 用户直接删目录时任务会残留，每次触发都失败 |
//!
//! # 停止语义：**崩溃**要拉回来，**用户主动退出**不许拉回来（2026-09-20）
//!
//! 兜底存在的理由只有一条：**崩溃是意外**。用户主动退出不是意外 —— 两者必须能被分开，
//! 而"分开"的判据只能落在 core 自己身上：
//!
//! | 退出方式 | 留下什么 | 后果 |
//! |---|---|---|
//! | 崩溃 / 被杀 / 断电 | **什么都不留**（没有任何代码去写） | 任务照常把它拉回来 ✓ |
//! | 托盘「退出」/ `task unregister` | 写 `core-stopped.flag` + **删掉任务** | 不再被拉回，直到用户显式启动 |
//!
//! 两道防线是刻意的：删任务是**意图**（兜底不该再存在），标记是**保险**（万一删失败，
//! 任务再敲门时 core 自己会拒绝）。[`stop`] 里的顺序是"先写标记、再删任务"，
//! 与 `main::stop_component` 同一条纪律 —— 先做几乎不会失败的那一步。
//!
//! 反向动作：**任何非兜底发起的启动都算"用户要它跑"** ⇒ 清标记（[`resume`]）。
//! 因此启动器 / CLI / 安装器 / 双击 exe **都不需要知道这个标记存在**；只有兜底那一条路
//! 必须带 [`LAUNCH_MARKER`] 自报身份。写入者一个、读取者一个，判据不会漂移。
//!
//! # 两个会咬人的设置（默认值都与"core 常驻"冲突）
//!
//! 1. **`ExecutionTimeLimit` 必须 `PT0S`（无限）**。任务的动作是"跑 core"，而 core 是**常驻进程** ——
//!    若 core 不在跑、由本任务把它拉起来，那么这个任务实例会一直处于"运行中"。
//!    任何有限时限到点都会把 core **杀掉**：于是 core 每 5 分钟被拉起、跑满时限又被杀，无限循环。
//! 2. **`StopOnIdleEnd` 必须 false**。默认 `true` 意思是"计算机退出空闲状态时停止任务" ——
//!    对一个常驻进程而言就是"用户回来动一下鼠标，core 被杀"。
//!
//! 两条都靠显式生成 XML 落地；`schtasks` 命令行参数**无法**设置它们，这正是本模块不用
//! `/sc minute` 那种简写的原因。
//!
//! # 为什么用 XML 而不是 COM `ITaskService`
//!
//! 零新增 crate feature、零新增依赖，且 XML 是 Task Scheduler 的规范表示（可审计、可 diff）。
//! 参数面只有三个由本模块完全掌控的常量（任务名、`/xml` 路径、`/f`），不存在注入面。

use std::path::{Path, PathBuf};
use std::time::Duration;

use crate::components;
use crate::ownership;
use crate::process::{self, RunOutput};
use crate::security;
use crate::shellmenu::path_eq;

/// 任务名（**跨进程契约**：卸载程序、`recovery --clean-autostart`、验证脚本都按它查找）。
pub const TASK_NAME: &str = "BetterDesktop Core Ensure";

/// 兜底任务发起启动时携带的命令行标记。
///
/// # 契约
/// **跨模块字面量**：本模块把它写进任务动作的 `<Arguments>`，`main.rs` 用它判断"本次启动来自兜底"
/// （`main` 直接引用本常量，不另抄一份）。失配的表现是本模块要防的那类故障——
/// "任务带着标记、core 认不出" ⇒ 把兜底启动误当成用户显式启动 ⇒ 退了又被拉回来，且**没有任何报错**。
pub const LAUNCH_MARKER: &str = "--from-task";

/// **用户主动停止** core 的标记（`%LOCALAPPDATA%\BetterDesktop\core-stopped.flag`）。
///
/// # 为什么
/// 它存在的唯一理由是"崩溃与正常退出的区别"：崩溃**不写它**（没有任何代码去写），正常退出写它。
/// 与 `components.json` 的 `stopFlag` 是同一种东西 —— 那边的写者是 `main::stop_component`、
/// 读者是 `supervisor`；这边的写者是 [`stop`]、读者是 [`ensure`] 与 `main` 的启动闸门。
///
/// # 资源与生命周期
/// 空文件、零字节；由 [`stop`] 创建、[`resume`] 删除。不持有句柄，删除是幂等的。
pub const STOPPED_FLAG: &str = "core-stopped.flag";

/// 触发间隔（ISO 8601 duration）。
const REPEAT_INTERVAL: &str = "PT5M";

/// `schtasks` 调用超时。实测 < 1s；给足余量但**不无限等**（否则一个卡住的 schtasks 会吊住 core 的启动线程）。
const SCHTASKS_TIMEOUT: Duration = Duration::from_secs(30);

/// 触发起点。取一个明确的过去时刻：配 `Repetition` 且不给 `Duration` 即为"从此刻起无限重复"。
const START_BOUNDARY: &str = "2000-01-01T00:00:00";

/// 任务与期望状态的比对结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TaskState {
    /// 任务不存在。
    Missing,
    /// 存在、且由我们创建、配置与期望一致。
    Current,
    /// 存在但指向别的路径或间隔不对（用户搬迁了目录 / 残留的旧版本 / 人手改过）。
    Stale {
        /// 任务当前的 `<Command>`（可能为空 = 取不到）。
        command: String,
        /// 为什么判定为过期（进日志用）。
        reason: String,
    },
}

/// [`ensure`] 的结局（仅用于日志）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Ensured {
    /// 本次创建了任务。
    Created,
    /// 存在但配置不对，已重建。
    Repaired(String),
    /// 已存在且配置正确，未做任何事。
    AlreadyCurrent,
    /// **拒绝写**（附原因，三种起因）：本进程不是这份部署（测试副本 / 旧版本残留）、
    /// 它的位置会在 build/clean 中消失、或者用户已主动停止 core。
    /// 已存在的任务**原封不动**留给合法的写者。
    Skipped(String),
}

/// 确保任务存在且配置正确（**幂等**）。core 启动时调用，也是 `--core task register` 的实现。
///
/// 注意：这里用 `std::env::current_exe()` 作为任务动作的目标 —— 这正是"core 自行搬迁目录后
/// 下次启动就把任务指回自己"的实现，不需要额外逻辑。
///
/// # 但**先**判本进程有没有资格写它
///
/// 任务是一个**持久的系统级引用**（每 5 分钟跑一次），"谁能写它"因此必须有唯一答案。
/// 判据在 [`crate::ownership`]，两句话：
///
/// - 位置会在 build/clean 中消失（dev bin / `target/` / 打包中间目录）⇒ 不写。
///   写进去的引用每次触发都失败，而那时 core 已经不在了、**没有任何地方会报这条错**。
/// - 产品目录里的**另一份**构建（测试副本 / 旧版本残留）⇒ 也不写。
///   它可以用既有的兜底，但不得创建、改写、删除它。
///
/// 第二条是 2026-09-20 真机事故的修正：此前只有第一条，于是"任务指向了别的 exe"这一分支
/// 成了**无条件夺权** —— 谁最后启动谁拿走系统级任务，表现是"一个测试副本被计划任务反复复活，
/// 只能靠手工删任务停下来"。因果与取证见
/// `.agents/notes/implemented/bug-fix/2026-09-20-task-ownership.md`。
///
/// 与 `shellmenu` 的 dev opt-in 不同的是，这里**没有 `--dev` 例外**：dev 注册右键扩展是
/// "我要测这个功能"，而把一个开发目录写进系统级计划任务是纯负债，没有对应的收益。
pub fn ensure() -> Result<Ensured, String> {
    // 用户主动停过 ⇒ 不装也不修。这是"正常退出不被拉回来"的**第二道防线**
    // （第一道是 [`stop`] 直接删任务）；万一那次删除失败，任务会继续每 5 分钟敲门，
    // 而这条让每次敲门都变成一次便宜的拒绝（`main` 的启动闸门甚至更早，根本不创建实例）。
    // 放在最前：它最便宜，且与位置 / 归属判据无关。
    if is_stopped() {
        return Ok(Ensured::Skipped(format!(
            "core is STOPPED by the user ('{STOPPED_FLAG}' exists) — the fallback is neither \
             created nor repaired; start core explicitly (launcher / bdctl / installer) to arm it"
        )));
    }

    let exe = current_exe()?;
    let install_root = crate::shellmenu::install_root();
    let local_appdata = std::env::var("LOCALAPPDATA").ok().map(PathBuf::from);
    let ownership = ownership::of(&exe, install_root.as_deref(), local_appdata.as_deref());

    // **先判权限、再查任务**：不是主人时连 `schtasks` 都不必跑（省一次进程创建，
    // 也让"副本不碰系统级任务"这条约束在读代码时一眼可见）。
    if let Some(reason) = ownership::refusal_reason(ownership, &exe, install_root.as_deref()) {
        return Ok(Ensured::Skipped(reason));
    }

    // 走到这里 ⇒ 本进程**就是**这份部署（`Owner` 是 `refusal_reason` 返回 `None` 的唯一输入）。
    // 于是 `Stale`（任务指向别的 exe）**必须**夺回：安装根那一份是唯一合法的写者，
    // 而副本在上一行就返回了，不可能反过来抢它。
    match query()? {
        TaskState::Current => Ok(Ensured::AlreadyCurrent),
        TaskState::Missing => {
            register()?;
            Ok(Ensured::Created)
        }
        TaskState::Stale { reason, .. } => {
            register()?;
            Ok(Ensured::Repaired(reason))
        }
    }
}

/// 查询任务状态。
///
/// 判定"是不是我们的"看**三个字段**：`<Command>`、`<Arguments>` 与 `<Interval>`。
///
/// `<Arguments>` 是 2026-09-20 加进来的（见 [`LAUNCH_MARKER`]），它顺带解决了**迁移**：
/// 旧定义没有这个元素 ⇒ 判为 [`TaskState::Stale`] ⇒ 下次 core 启动时被 `Repaired` 成新定义，
/// 不需要任何手工步骤 —— 那正是 `classify` 存在的理由。
///
/// 不逐项比对全部设置 —— 那需要真正的 XML 解析与一个 XML 依赖；而本模块**生成**这份 XML，
/// 知道它的确切形状，所以定点取字段是可靠的。完整配置校对由验证脚本（`schtasks /query /xml`
/// + PowerShell `[xml]`）负责，那才是它该待的地方。
pub fn query() -> Result<TaskState, String> {
    // **必须用 `invoke_schtasks` 而不是 `run_schtasks`**：任务不存在时 schtasks 返回非 0，
    // 而 `run_schtasks` 会把非 0 直接变成 `Err` —— 那样"任务不存在"这条**最常见**的路径
    // 就永远走不到 `Missing`，`ensure()` 每次都会失败，兜底任务永远建不起来。
    // 退出码在这里是**要解释的数据**，不是"成败"。
    let out = invoke_schtasks(&["/query", "/tn", TASK_NAME, "/xml"])?;
    if out.exit_code != 0 {
        return Ok(TaskState::Missing);
    }
    let exe = current_exe()?;
    Ok(classify(&out.stdout, &exe))
}

/// 注册（或覆盖）任务。
pub fn register() -> Result<(), String> {
    let exe = current_exe()?;
    let sid = security::CurrentUser::load()?.sid_string()?;
    let xml = build_xml(&exe, &sid);

    let path = temp_xml_path();
    write_utf16le(&path, &xml)?;

    let result = run_schtasks(&[
        "/create",
        "/tn",
        TASK_NAME,
        "/xml",
        &path.to_string_lossy(),
        "/f", // 存在则覆盖 —— "先看状态再补缺"靠它做到幂等
    ]);

    // 临时文件必须收走（无论成败）。plan 里的 XML 留在盘上只会让人困惑。
    let _ = std::fs::remove_file(&path);
    result.map(|_| ())
}

/// 删除任务。幂等：任务本来就不存在时返回 `Ok`。
///
/// **调用者 = 控制管道的 `task unregister` 动词**（`bdctl --core task unregister`）。
/// 这是第 5 条决策"清理：两者都做"里的**运维/应急那一半**。
///
/// 另一半（卸载程序 / `recovery --clean-autostart`）**刻意不走本函数**，而是各自
/// `schtasks /delete /tn <同名> /f`：
///   · 卸载程序要把 core 的整个目录删掉，走管道就得先"ensure core"——那会把正要被卸掉的进程拉起来；
///   · recovery 是**零依赖**的应急程序（不引用任何 BetterDesktop 工程），调不到本函数。
/// 两处都只是**按名字删**，不生成任何任务定义，因此不构成"第二份实现"；
/// 名字这一个字面量由 `verify-system-integration.ps1` 的跨进程字面量检查钉住。
pub fn unregister() -> Result<(), String> {
    if matches!(query()?, TaskState::Missing) {
        return Ok(());
    }
    run_schtasks(&["/delete", "/tn", TASK_NAME, "/f"]).map(|_| ())
}

// ───────────── 停止语义：崩溃 vs 用户主动退出（见模块头那一节） ─────────────

/// 用户是否主动停止过 core。
///
/// # 做什么
/// 判 `core-stopped.flag` 是否存在。
///
/// # 为什么
/// 这是"崩溃 vs 正常退出"的**唯一判据**：崩溃不会留下标记（没有任何代码去写它），
/// 于是兜底照常把它拉回来；正常退出会写标记（见 [`stop`]），兜底就被压制。
///
/// # 契约
/// `true` = 用户停过、且此后没有显式启动过；`false` = 正常态（含"从未停过"）。
///
/// # 边界与失败路径
/// `%LOCALAPPDATA%` 缺失时返回 `false`（读不到标记 ≠ 处于停止态）。
/// 这是刻意的保守侧：宁可照常兜底，也不要因为读不到一个文件就让崩溃再也不被拉起。
pub fn is_stopped() -> bool {
    components::flag_exists(STOPPED_FLAG)
}

/// 撤掉兜底，并记住"这是用户要的"。调用方 = 托盘「退出」与 `task unregister` 动词。
///
/// # 做什么
/// 写 [`STOPPED_FLAG`]，然后删除计划任务。
///
/// # 为什么
/// 兜底的前提是"崩溃是意外"。用户主动退出不是意外，因此这个前提不再成立 ——
/// 撤掉它既是**意图**的表达，也省掉此后每 5 分钟一次注定被拒的敲门。
///
/// # 契约
/// `Ok(())` = 标记已写、任务已删（任务本来就不存在也算成功）。
/// `Err(原因)` = 其中一步失败；原因点名是哪一步，**调用方必须如实记日志、不得当作成功**
/// （"已停止"被静默推翻正是本仓反复强调的那类故障）。
///
/// # 边界与失败路径
/// **先写标记、再删任务**：删任务依赖 `schtasks`，是两步里更可能失败的一步；
/// 而标记一旦写下，"任务再敲门也被挡住"就已经成立。顺序理由同 `main::stop_component`
/// （"先写标记、再停进程"）。因此删任务失败时返回的 `Err` 里明确写"标记已写"，
/// 让调用方知道防线还剩一道。
pub fn stop() -> Result<(), String> {
    components::write_flag(STOPPED_FLAG)?;
    match unregister() {
        Ok(()) => Ok(()),
        Err(e) => Err(format!(
            "the stop flag '{STOPPED_FLAG}' was written (the task will now be refused), but \
             removing the task failed: {e}"
        )),
    }
}

/// 解除"用户主动停止"。调用方 = `main` 的启动路径（确知本次启动不是兜底发起时）。
///
/// # 为什么返回 `bool` 而不是 `()`
/// "本来就没停过"是最常见的情况，为它每次启动都记一行日志是噪声；调用方只该在**真的**
/// 清掉了东西时说话。故返回"之前是否处于停止态"。
///
/// # 契约
/// `Ok(true)` = 之前是停止态、现已解除；`Ok(false)` = 本来就没被停过（**不是错误**）；
/// `Err(原因)` = 删标记失败（权限等），调用方必须记 ERROR —— 停止态会继续压制兜底。
///
/// # 调用方约束
/// **只准在"本次启动不是兜底发起"时调用**（见 [`LAUNCH_MARKER`]）：否则兜底自己会把用户的
/// 停止意图抹掉，那正是本模块要防的"退了又被拉回来"。
pub fn resume() -> Result<bool, String> {
    let was_stopped = is_stopped();
    components::clear_flag(STOPPED_FLAG)?;
    Ok(was_stopped)
}

/// 显式装回兜底（`task register` 动词）。调用方 = CLI / 安装器。
///
/// # 为什么不能只调 [`ensure`]
/// `ensure()` 在停止态下会**正确地**拒绝 —— 所以"装回"这个意图必须先解除停止态。
/// 顺序不可颠倒：反过来只会得到一个静默的 `Skipped`（命令"成功"了、任务却没回来）。
///
/// # 契约
/// `Ok(outcome)` = 停止态已解除，`ensure()` 的结论原样返回给调用方；
/// `Err(原因)` = 解除停止态失败，或 `ensure()` 失败。
pub fn arm() -> Result<Ensured, String> {
    if resume()? {
        crate::log::info(format!(
            "task register: cleared '{STOPPED_FLAG}' — the fallback was armed again by request"
        ));
    }
    ensure()
}

// ───────────────────────────── 纯逻辑（可单测） ─────────────────────────────

/// 生成任务定义 XML。
///
/// 每个非默认值都在上面「两个会咬人的设置」与「五个已定的决策」里有对应理由，
/// 改动前请先读那两段 —— 这些值看起来可以随便调，实际上每一条都对应一类具体故障。
fn build_xml(exe: &Path, sid: &str) -> String {
    let command = xml_escape(&exe.to_string_lossy());
    let workdir = xml_escape(
        &exe.parent()
            .map(|d| d.to_string_lossy().to_string())
            .unwrap_or_default(),
    );

    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>BetterDesktop core 崩溃兜底：每 5 分钟检查 core 是否在运行，不在则拉起。若已有实例在运行，本次启动会在 100ms 内静默退出（core 是单实例）。由 betterdesktop-core.exe 自行注册并维护，卸载时删除。</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition>
        <Interval>{REPEAT_INTERVAL}</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>{START_BOUNDARY}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{LAUNCH_MARKER}</Arguments>
      <WorkingDirectory>{workdir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"#
    )
}

/// 判定已存在的任务是否就是"我们要的那个"。
fn classify(xml: &str, exe: &Path) -> TaskState {
    let Some(command) = extract_tag(xml, "Command") else {
        return TaskState::Stale {
            command: String::new(),
            reason: "task XML contains no <Command> element".to_string(),
        };
    };

    if !path_eq(&command, &exe.to_string_lossy()) {
        return TaskState::Stale {
            command,
            reason: format!("points at a different executable (expected {})", exe.display()),
        };
    }

    let interval = extract_tag(xml, "Interval").unwrap_or_default();
    if interval != REPEAT_INTERVAL {
        return TaskState::Stale {
            command,
            reason: format!("repeat interval is '{interval}', expected {REPEAT_INTERVAL}"),
        };
    }

    // 标记缺失（老定义）⇒ 必须判过期：那样的任务拉起的 core 自报不出"我来自兜底"，
    // 于是用户的停止标记会被绕过 —— 正是"退了又被拉回来"。这条也是**自动迁移**的落点。
    let args = extract_tag(xml, "Arguments").unwrap_or_default();
    if args != LAUNCH_MARKER {
        return TaskState::Stale {
            command,
            reason: format!(
                "action arguments are '{args}', expected '{LAUNCH_MARKER}' — a task without the \
                 marker cannot be told apart from an explicit start of core"
            ),
        };
    }

    // 工作目录只在任务里写了才比对：写空表示"继承"，不算错
    if let Some(dir) = extract_tag(xml, "WorkingDirectory")
        && !dir.is_empty()
        && let Some(parent) = exe.parent()
        && !path_eq(&dir, &parent.to_string_lossy())
    {
        return TaskState::Stale {
            command,
            reason: format!(
                "working directory is '{dir}', expected {}",
                parent.display()
            ),
        };
    }

    TaskState::Current
}

/// 取 XML 里第一个 `<tag>…</tag>` 的文本（本模块只关心自己生成的这几个定长字段）。
fn extract_tag(xml: &str, tag: &str) -> Option<String> {
    let open = format!("<{tag}>");
    let close = format!("</{tag}>");
    let start = xml.find(&open)? + open.len();
    let end = xml[start..].find(&close)? + start;
    Some(xml[start..end].trim().to_string())
}

// 路径比较 —— **复用 `shellmenu::path_eq`**，不在这里再写一份。
//
// 它是那份共享测试向量（`protocols/native-dll-path-test-vectors.json`）的 C 位实现，
// 决定"什么算同一条路径"。本模块原先自带一个只做 trim/lowercase 的版本，
// 它**不归一分隔符** —— 于是 `C:/x/core.exe` 与 `C:\x\core.exe` 在这里被判成两条路径
// （任务会被无谓地反复重建），而 shellmenu 那边判成同一条。同一进程里两套等价规则，
// 正是本仓库反复吃亏的那类病。
//
// 【为什么是 `//` 而不是 `///`】这段是**决策记录**，不是某个条目的文档 ——
// 写成 `///` 会让它变成"下一个条目的 doc comment"（clippy `empty line after doc comment`
// 抓的正是这个），语义上也是错的。

/// XML 文本转义。Windows 目录名里 `&` 是合法的，不转义会让整份 XML 解析失败。
fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

// ───────────────────────────── 副作用（薄壳） ─────────────────────────────

fn current_exe() -> Result<PathBuf, String> {
    std::env::current_exe().map_err(|e| format!("cannot determine the core executable path: {e}"))
}

fn temp_xml_path() -> PathBuf {
    std::env::temp_dir().join(format!("bd-core-ensure-{}.xml", std::process::id()))
}

/// 以 **UTF-16LE + BOM** 写出 XML（与 Task Scheduler 自己导出的格式一致）。
///
/// 不用 UTF-8：`schtasks /create /xml` 对 UTF-16 的支持是与系统自身导出格式完全一致的路径，
/// 而这文件里含中文描述 —— 编码写错的表现是"任务注册成功但描述乱码/解析失败"。
fn write_utf16le(path: &Path, text: &str) -> Result<(), String> {
    let mut bytes: Vec<u8> = vec![0xFF, 0xFE];
    for unit in text.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    std::fs::write(path, bytes).map_err(|e| format!("cannot write {}: {e}", path.display()))
}

/// 调 `schtasks`，**保留退出码**（参数逐个传，绝不拼字符串 —— 计划 C20）。
///
/// 只把"压根没跑起来"与"超时"当错误；非零退出码是**数据**，由调用方解释
/// （`/query` 的非零 = 任务不存在，那是正常路径）。
fn invoke_schtasks(args: &[&str]) -> Result<RunOutput, String> {
    let exe = schtasks_path();
    let owned: Vec<String> = args.iter().map(|a| a.to_string()).collect();
    let out = process::run_and_wait(&exe, &owned, SCHTASKS_TIMEOUT)?;

    if out.timed_out {
        return Err(format!(
            "schtasks {args:?} timed out after {}s",
            SCHTASKS_TIMEOUT.as_secs()
        ));
    }
    Ok(out)
}

/// 调 `schtasks` 并要求成功。用于 `/create`、`/delete` 这类"失败就是失败"的动作。
fn run_schtasks(args: &[&str]) -> Result<RunOutput, String> {
    let out = invoke_schtasks(args)?;
    if out.exit_code != 0 {
        // schtasks 把错误写在 stdout（不是 stderr）—— 两边都带上，否则报错信息是空的
        return Err(format!(
            "schtasks {args:?} exited with {}: {}{}",
            out.exit_code,
            out.stdout.trim(),
            out.stderr.trim()
        ));
    }
    Ok(out)
}

fn schtasks_path() -> PathBuf {
    let root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    Path::new(&root).join("System32").join("schtasks.exe")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_exe() -> PathBuf {
        PathBuf::from(r"C:\Apps\BetterDesktop\betterdesktop-core.exe")
    }

    fn sample_xml() -> String {
        build_xml(&sample_exe(), "S-1-5-21-1-2-3-1001")
    }

    // ───────────── 电源与生命周期：三个会造成真实故障的设置 ─────────────

    /// 有限时限会把常驻的 core 按点杀掉（拉起 → 跑满 → 被杀 → 再拉起）。
    #[test]
    fn execution_time_limit_is_unlimited() {
        assert!(
            sample_xml().contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"),
            "core 是常驻进程，任务时限必须是「无限」，否则 core 会被周期性杀掉"
        );
    }

    /// 默认 `StopOnIdleEnd=true` = "用户回来动一下鼠标就杀掉 core"。
    #[test]
    fn never_stops_on_idle_end() {
        let xml = sample_xml();
        assert!(xml.contains("<StopOnIdleEnd>false</StopOnIdleEnd>"));
        assert!(xml.contains("<RunOnlyIfIdle>false</RunOnlyIfIdle>"));
    }

    /// C13：不得唤醒机器、不得补跑错过的触发。
    #[test]
    fn does_not_wake_the_machine_or_backfill() {
        let xml = sample_xml();
        assert!(xml.contains("<WakeToRun>false</WakeToRun>"), "不得唤醒机器");
        assert!(
            xml.contains("<StartWhenAvailable>false</StartWhenAvailable>"),
            "睡眠错过的触发不补跑 —— core 唤醒后会自己 reconcile"
        );
        assert!(xml.contains("<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>"));
    }

    /// 上一次还在跑时忽略新触发，避免 core 崩溃时任务堆积。
    #[test]
    fn ignores_new_instances_and_runs_on_battery() {
        let xml = sample_xml();
        assert!(xml.contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"));
        assert!(xml.contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"));
        assert!(xml.contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"));
    }

    // ───────────── 身份：用户级 ─────────────

    /// 以 SYSTEM 运行会得到一个**不同 SID** 的 core，它与用户的 core 互相连不上管道。
    #[test]
    fn runs_as_the_current_user_interactively() {
        let xml = sample_xml();
        assert!(xml.contains("<UserId>S-1-5-21-1-2-3-1001</UserId>"));
        assert!(xml.contains("<LogonType>InteractiveToken</LogonType>"));
        assert!(
            xml.contains("<RunLevel>LeastPrivilege</RunLevel>"),
            "用户级任务不需要管理员权限"
        );
        assert!(!xml.to_ascii_uppercase().contains("SYSTEM"));
    }

    // ───────────── 动作：绝对路径 + 无参数 + 显式工作目录 ─────────────

    #[test]
    fn action_is_absolute_and_has_a_working_directory() {
        let xml = sample_xml();
        assert!(xml.contains(r"<Command>C:\Apps\BetterDesktop\betterdesktop-core.exe</Command>"));
        assert!(
            xml.contains(r"<WorkingDirectory>C:\Apps\BetterDesktop</WorkingDirectory>"),
            "不写工作目录会落到 %SystemRoot%\\System32，core 就找不到自己的组件"
        );
    }

    /// 动作**必须**带 `--from-task`：core 靠它把"兜底敲门"与"用户显式启动"分开 ——
    /// 没有它，用户在托盘里按的「退出」会在 5 分钟内被兜底推翻。
    ///
    /// 这条测试此前断言的是**相反**的事（"动作必须无参数"）。改动的理由见模块头「停止语义」
    /// 那一节：那个参数不是便利开关（`--ensure` 那类），而是 core 判断自己**来源**的唯一信息来源。
    #[test]
    fn action_carries_the_from_task_marker() {
        assert!(
            sample_xml().contains(&format!("<Arguments>{LAUNCH_MARKER}</Arguments>")),
            "任务动作必须带上标记，否则 core 分不出兜底启动与显式启动"
        );
    }

    /// 标记是**跨模块字面量**（`main.rs` 判定 argv 时引用本常量，不另抄一份）。
    /// 钉住它 = 同时钉住"任务定义"与"启动判定"两侧 —— 改一处漏另一处的表现是
    /// "任务带了标记但 core 认不出"，没有任何报错，且退回"退了又被拉回来"。
    #[test]
    fn launch_marker_is_the_cross_module_literal() {
        assert_eq!(LAUNCH_MARKER, "--from-task");
        assert!(LAUNCH_MARKER.is_ascii());
        assert!(LAUNCH_MARKER.starts_with("--"));
    }

    /// 停止标记名是**跨进程字面量**（文档、排查的人、`%LOCALAPPDATA%` 路径都按它查找）。
    #[test]
    fn stopped_flag_name_is_pinned() {
        assert_eq!(STOPPED_FLAG, "core-stopped.flag");
        assert!(STOPPED_FLAG.is_ascii(), "flag 名要进文件路径，必须纯 ASCII");
    }

    // ───────────── 迁移与拒绝：任务定义里的标记 ─────────────

    /// **迁移钉子**：老定义（没有 `<Arguments>`）必须判过期 —— 否则它会长期绕过用户的停止标记。
    /// 判过期 ⇒ 下次 core 启动时被 `Repaired` 成新定义，不需要任何手工步骤。
    #[test]
    fn classify_rejects_a_task_without_the_marker() {
        let xml = sample_xml().replace(&format!("<Arguments>{LAUNCH_MARKER}</Arguments>"), "");
        assert!(!xml.contains("<Arguments>"), "测试前提：标记真的被摘掉了");

        let TaskState::Stale { reason, .. } = classify(&xml, &sample_exe()) else {
            panic!("a task without the launch marker must be judged stale");
        };
        assert!(reason.contains(LAUNCH_MARKER), "{reason}");
    }

    /// 反面：带**别的**参数也算过期（人手改过、或未来换了标记名）。
    #[test]
    fn classify_rejects_a_task_with_foreign_arguments() {
        let xml = sample_xml().replace(LAUNCH_MARKER, "--something-else");
        let TaskState::Stale { reason, .. } = classify(&xml, &sample_exe()) else {
            panic!("foreign arguments must be judged stale");
        };
        assert!(reason.contains("--something-else"), "{reason}");
    }

    /// 停止态的判定只读、不改动任何东西（真机上"标记不存在"是绝大多数情况）。
    ///
    /// 单测**不得**制造真实的停止态（那会让用户机器上的 core 再也不被拉起），
    /// 所以这里只断言"能给出结论、不 panic"；停止语义的真机验证见决策记录。
    #[test]
    fn probing_the_stop_flag_never_writes() {
        let _ = is_stopped();
    }

    #[test]
    fn interval_is_five_minutes() {
        assert!(sample_xml().contains("<Interval>PT5M</Interval>"));
    }

    /// 任务名是**跨进程契约**：安装器、卸载器、`recovery --clean-autostart` 都按它查找。
    ///
    /// 改这里而没改那三处 = 第 5 条决策点名的故障："卸载后任务残留，每次触发都失败"。
    /// 这个字面量由 `scripts/verify-system-integration.ps1` 在门禁里交叉核对（四处必须逐字一致），
    /// 所以它在这里被钉住时会**同时**把另外三处一起钉住。
    #[test]
    fn task_name_is_the_cross_process_contract_literal() {
        assert_eq!(TASK_NAME, "BetterDesktop Core Ensure");
        // 名字里不许出现非 ASCII：PS1 是纯 ASCII 文件（PS5.1 按 ANSI 读取），
        // 一个中文任务名会让脚本解析失败 —— 而失败发生在卸载路径上，最难被发现的地方。
        assert!(TASK_NAME.is_ascii(), "task name must stay ASCII: {TASK_NAME}");
    }

    /// 路径里的 `&` 是合法的 Windows 目录名，不转义会让整份 XML 解析失败。
    #[test]
    fn xml_escaping_of_paths() {
        let exe = PathBuf::from(r"C:\a&b\<x>\betterdesktop-core.exe");
        let xml = build_xml(&exe, "S-1-5-21-1");
        assert!(xml.contains(r"<Command>C:\a&amp;b\&lt;x&gt;\betterdesktop-core.exe</Command>"));
        assert!(!xml.contains("<x>"), "未转义的尖括号会破坏 XML 结构");
    }

    // ───────────── 识别：是不是"我们那个"任务 ─────────────

    #[test]
    fn classify_accepts_our_own_xml() {
        assert_eq!(classify(&sample_xml(), &sample_exe()), TaskState::Current);
    }

    /// 大小写不同不影响判定（Windows 路径语义）。
    #[test]
    fn classify_is_case_insensitive_about_paths() {
        let xml = build_xml(
            &PathBuf::from(r"C:\APPS\BetterDesktop\betterdesktop-core.exe"),
            "S-1-5-21-1",
        );
        assert_eq!(
            classify(
                &xml,
                &PathBuf::from(r"c:\apps\betterdesktop\BETTERDESKTOP-CORE.EXE")
            ),
            TaskState::Current
        );
    }

    /// 用户搬迁了 core 目录 → 必须判为过期并重建（这就是"core 启动时自我修正"的落点）。
    #[test]
    fn classify_rejects_task_pointing_elsewhere() {
        let xml = build_xml(&PathBuf::from(r"D:\Other\betterdesktop-core.exe"), "S-1-5-21-1");
        let state = classify(&xml, &sample_exe());
        let TaskState::Stale { command, reason } = state else {
            panic!("a task pointing at another exe must be judged stale");
        };
        assert!(command.contains("Other"));
        assert!(reason.contains("different executable"), "{reason}");
    }

    #[test]
    fn classify_rejects_wrong_interval() {
        let xml = sample_xml().replace("PT5M", "PT1M");
        let TaskState::Stale { reason, .. } = classify(&xml, &sample_exe()) else {
            panic!("wrong interval must be judged stale");
        };
        assert!(reason.contains("PT5M"), "{reason}");
    }

    #[test]
    fn classify_rejects_xml_without_command() {
        assert!(matches!(
            classify("<Task></Task>", &sample_exe()),
            TaskState::Stale { .. }
        ));
    }

    /// 工作目录写空（= 继承）不算错：那是合法的表达方式，不该触发无谓的重建。
    #[test]
    fn classify_tolerates_missing_working_directory() {
        let xml = sample_xml().replace(
            r"<WorkingDirectory>C:\Apps\BetterDesktop</WorkingDirectory>",
            "",
        );
        assert_eq!(classify(&xml, &sample_exe()), TaskState::Current);
    }

    #[test]
    fn extract_tag_returns_first_occurrence() {
        let xml = "<a><Command>x</Command><Command>y</Command></a>";
        assert_eq!(extract_tag(xml, "Command").as_deref(), Some("x"));
        assert_eq!(extract_tag(xml, "Nope"), None);
    }

    /// 路径等价用的是**共享契约**的那一份（`shellmenu::path_eq`），不是本模块自带的版本。
    /// 这也是回归钉子：任务里的 `C:/x/core.exe` 与 `C:\x\core.exe` 必须判为同一条路径，
    /// 否则每次 `ensure()` 都会把任务重建一遍。
    #[test]
    fn path_comparison_uses_the_shared_contract_rule() {
        assert!(path_eq(r" C:\A\b\ ", r"c:\a\B"));
        assert!(path_eq(r"C:/A/b/core.exe", r"C:\a\B\core.exe")); // 分隔符归一
        assert!(!path_eq(r"C:\A\b", r"C:\A\c"));
        // 空值不得被当成"与任何路径等价"
        assert!(!path_eq("", r"C:\A\b"));
    }

    /// `classify` 必须按上面那条规则判 —— 分隔符不同**不算**漂移（否则任务每次启动都被重建）。
    ///
    /// 用 `/` 写路径不是臆想：人手改过任务、或用别的工具导出再导回时就会出现。
    #[test]
    fn classify_tolerates_forward_slashes_in_the_task() {
        let exe = sample_exe();
        let with_backslashes = exe.to_string_lossy().to_string();
        let with_slashes = with_backslashes.replace('\\', "/");

        let xml = build_xml(&exe, "S-1-5-21-1").replace(&with_backslashes, &with_slashes);
        assert!(xml.contains(&with_slashes), "test setup must actually swap the path");

        assert_eq!(classify(&xml, &exe), TaskState::Current);
    }

    // ───────────── 写权：判据与测试都在 `crate::ownership` ─────────────
    //
    // 原先这里有两组 `is_stable_location` 的测试。它们随判据一起搬到了 `ownership.rs`
    // （并在那里扩成三态：`Owner` / `Tenant` / `Rejected`）—— 规则的唯一性不止体现在实现上，
    // 也体现在"测试跟着规则走"：判据搬走而测试留下，下一个人就会以为这里还有第二条规则。

    // ───────────── 真机：只读探测（不注册、不删除） ─────────────

    /// 只验证"查询这条路走得通、能拿到回话"，**不注册也不删除任何任务** ——
    /// 单测不该改动真机系统状态。注册/删除由 S3 的真机验收脚本覆盖。
    #[test]
    fn query_reaches_schtasks_and_reports_a_state() {
        match query() {
            Ok(TaskState::Missing) | Ok(TaskState::Current) | Ok(TaskState::Stale { .. }) => {}
            // 非 Windows 或无 schtasks 时跳过（本仓库只在 Windows 上构建，这里只是防御）
            Err(e) if e.contains("cannot") => {}
            Err(e) => panic!("query must not fail hard: {e}"),
        }
    }
}
