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
//! | 动作 | core 绝对路径 + **无参数** + 显式工作目录 | 有参数会让任务与 core 的启动路径分叉；工作目录不写会落到 `%SystemRoot%\System32` |
//! | 清理 | 卸载程序删 + core 启动时自我修正 | 用户直接删目录时任务会残留，每次触发都失败 |
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

use crate::process::{self, RunOutput};
use crate::security;
use crate::shellmenu::path_eq;

/// 任务名（**跨进程契约**：卸载程序、`recovery --clean-autostart`、验证脚本都按它查找）。
pub const TASK_NAME: &str = "BetterDesktop Core Ensure";

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
    /// **拒绝注册**：本进程不在稳定位置，已存在的任务也**原封不动**留给它（附原因）。
    Skipped(String),
}

/// 确保任务存在且配置正确（**幂等**）。core 启动时调用，也是 `--core task register` 的实现。
///
/// 注意：这里用 `std::env::current_exe()` 作为任务动作的目标 —— 这正是"core 自行搬迁目录后
/// 下次启动就把任务指回自己"的实现，不需要额外逻辑。
///
/// # 但**先**判这个位置稳不稳定
///
/// 任务是一个**持久的系统级引用**（每 5 分钟跑一次）。若把开发 bin 的路径写进去，那个 bin
/// 一次 clean/重建之后，任务就永远指向一个不存在的 exe —— 每次触发都失败，而且**没有任何地方
/// 会报这条错**（core 已经不在了，日志无从产生）。
///
/// 这与"注册表里的 DLL 路径"是同一类问题，结论也相同：**持久引用必须指向最持久的位置**
/// （见 `protocols/native-dll-path-test-vectors.json` 的 `_rule`）。
/// 所以不在稳定位置时**宁可不注册**：少一条兜底，好过留一条每次必定失败的兜底。
///
/// 与 `shellmenu` 的 dev opt-in 不同的是，这里**没有 `--dev` 例外**：dev 注册右键扩展是
/// "我要测这个功能"，而把一个开发目录写进系统级计划任务是纯负债，没有对应的收益。
pub fn ensure() -> Result<Ensured, String> {
    let exe = current_exe()?;
    let install_root = crate::shellmenu::install_root();
    let local_appdata = std::env::var("LOCALAPPDATA").ok().map(PathBuf::from);

    if !is_stable_location(&exe, install_root.as_deref(), local_appdata.as_deref()) {
        return Ok(Ensured::Skipped(format!(
            "this core lives at {} which is neither under the install root ({}) nor under \
             %LOCALAPPDATA%\\BetterDesktop — refusing to point a system-wide task at it",
            exe.display(),
            install_root
                .as_deref()
                .map(|p| p.display().to_string())
                .unwrap_or_else(|| "not installed".to_string())
        )));
    }

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

/// core 的 exe 是否位于**稳定位置**（安装根之下，或 `%LOCALAPPDATA%\BetterDesktop` 之下）。
///
/// 纯函数：不可注入 IO，因此两个"该拒绝"的分支可以被单测完整覆盖 ——
/// 那正是这条守卫的价值所在（它防的是一个**没人会看到报错**的失败）。
///
/// `pub(crate)`：`autostart.rs`（HKCU\Run）**共用同一条守卫** ——
/// 计划任务与 Run 键都是"持久化 core 启动路径"，把它们指向开发 bin 是同款负债，
/// 守卫也必须只有一份（两套判据必然漂移）。
pub(crate) fn is_stable_location(
    exe: &Path,
    install_root: Option<&Path>,
    local_appdata: Option<&Path>,
) -> bool {
    let Some(dir) = exe.parent() else {
        return false;
    };
    if let Some(root) = install_root
        && crate::shellmenu::is_under(dir, root)
    {
        return true;
    }
    if let Some(base) = local_appdata
        && crate::shellmenu::is_under(dir, &base.join(crate::shellmenu::PRODUCT_FOLDER))
    {
        return true;
    }
    false
}

/// 查询任务状态。
///
/// 判定"是不是我们的"只看**两个字段**：`<Command>` 与 `<Interval>`。
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

/// 路径比较 —— **复用 `shellmenu::path_eq`**，不在这里再写一份。
///
/// 它是那份共享测试向量（`protocols/native-dll-path-test-vectors.json`）的 C 位实现，
/// 决定"什么算同一条路径"。本模块原先自带一个只做 trim/lowercase 的版本，
/// 它**不归一分隔符** —— 于是 `C:/x/core.exe` 与 `C:\x\core.exe` 在这里被判成两条路径
/// （任务会被无谓地反复重建），而 shellmenu 那边判成同一条。同一进程里两套等价规则，
/// 正是本仓库反复吃亏的那类病。

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

    /// 动作**不得带参数**：有参数会让任务与 core 的启动路径分叉，以后加参数要改任务。
    #[test]
    fn action_carries_no_arguments() {
        assert!(
            !sample_xml().contains("<Arguments>"),
            "任务动作必须是无参数的 core 本身（单实例即幂等，不需要 --ensure）"
        );
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

    // ───────────── 稳定位置：拒绝把系统级任务指向开发目录 ─────────────

    /// 三种"该接受"的位置：安装根之下 / `%LOCALAPPDATA%\BetterDesktop` 之下（含其子目录）。
    #[test]
    fn stable_location_accepts_install_root_and_production_folder() {
        let base = Path::new(r"C:\Users\X\AppData\Local");
        let root = Path::new(r"C:\Users\X\AppData\Local\BetterDesktop\app\2026.09.17.1610");

        assert!(is_stable_location(
            &root.join("betterdesktop-core.exe"),
            Some(root),
            Some(base)
        ));
        // 无 deployment.json（未用安装器装过）但仍在生产数据目录之下
        assert!(is_stable_location(
            &base.join(r"BetterDesktop\Standalone\betterdesktop-core.exe"),
            None,
            Some(base)
        ));
    }

    /// **两个必须拒绝的位置** —— 这条守卫存在的全部理由。
    ///
    /// 开发 bin / dist 打包目录都是"一次 clean 就没了"的位置：把系统级任务指过去，
    /// 任务会每 5 分钟失败一次，而那时 core 已经不在了、没有任何地方会报这条错。
    #[test]
    fn stable_location_rejects_build_and_packaging_directories() {
        let base = Path::new(r"C:\Users\X\AppData\Local");
        let root = Path::new(r"C:\Users\X\AppData\Local\BetterDesktop\app\2026.09.17.1610");

        assert!(!is_stable_location(
            Path::new(r"C:\dev\better-desktop\BetterDesktop.Cli\bin\Release\betterdesktop-core.exe"),
            Some(root),
            Some(base)
        ));
        assert!(!is_stable_location(
            Path::new(r"C:\dev\better-desktop\core\target\release\betterdesktop-core.exe"),
            Some(root),
            Some(base)
        ));
        assert!(!is_stable_location(
            Path::new(r"C:\dev\better-desktop\dist\modules\X\BetterDesktop\betterdesktop-core.exe"),
            Some(root),
            Some(base)
        ));

        // 防"字符串前缀"误判：BetterDesktopTrap 不是 BetterDesktop 的子目录
        assert!(!is_stable_location(
            &base.join(r"BetterDesktopTrap\bin\betterdesktop-core.exe"),
            None,
            Some(base)
        ));
    }

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
