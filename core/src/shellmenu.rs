//! 系统右键菜单（COM 壳扩展）注册状态检查 —— S4-2 第 1 步：**只读，不写**。
//!
//! # 为什么 core 只"检查"，不"注册"
//!
//! 这是本模块最重要的设计决定，也是四审定的：**core 永不写 `HKCU\Software\Classes`**。
//!
//! 那条路径不是"写注册表"，是**改 explorer 的行为**：CLSID 写错会让 explorer 加载失败、
//! `InprocServer32` 路径错会让 explorer 每次右键都去加载不存在的 DLL。风险比计划任务
//! （写 HKCU Run 键，失败就失败）大一个量级。
//!
//! 而注册逻辑**已经在 C# 里存在且经过验证**（`ComShellExtensionRegistrar`）。
//! 在 Rust 里重写一遍等于把一个已验证的写入路径换成未验证的。故：
//!
//! ```text
//! core   → 只读检查（本模块），发现异常 → 触发一次性 CLI
//! CLI    → 真正写注册表（复用既有 C# 实现），执行完即退
//! ```
//!
//! 这与 S6 的 `--rebuild-shellmenu` 是同一条路径形态（core 触发、CLI 执行）。
//!
//! # 本模块的检查是"启发式"，不是权威
//!
//! 权威是 C# 侧的 `ComShellExtensionRegistrar.IsRegistered()` / `IsFullyRegistered()`。
//! 本模块的 Rust 实现只为一件事存在 —— **不必每 60 秒拉起一个 .NET 进程去问一句**
//! （那与本项目"极致轻量"的前提直接冲突）。
//!
//! 由此产生的偏差是**单向安全**的：本模块若误判为"需要修复"，触发的是一次
//! **幂等的**修复（C# 侧会自己再判一次）；若误判为"已就绪"，最坏结果是少一次自愈。
//! 因此判定刻意**偏保守** —— 任何不确定都归到"需要修复"。
//!
//! # 与留痕 flag 的关系（四审纠正过的一点）
//!
//! `shellmenu-unregistered.flag` 的**写者是 C# 的 `Unregister()`**（用户显式注销时写），
//! core 只**读**不写。语义是"用户主动要求不要这个右键扩展"—— 故该 flag 存在时
//! core **不得**触发任何注册（否则等于把用户的决定覆盖掉）。

use std::path::{Path, PathBuf};
use std::time::Duration;

use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_READ, REG_SZ, RegCloseKey, RegOpenKeyExW, RegQueryValueExW,
};

/// 经典菜单处理器 CLSID（**必须**与 `ComShellExtensionRegistrar.ClassicClsid` 及
/// `native/include/BdShell.h` 的 `kBClassic` 逐字一致）。
pub const CLASSIC_CLSID: &str = "{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71}";

/// 原生扩展 DLL 文件名。
pub const NATIVE_DLL: &str = "BetterDesktopShellMenu.dll";

/// 处理程序键名。
const HANDLER_KEY: &str = "BetterDesktop";

/// 四个目标场景（与 C# `Scenes` 逐字一致）。
const SCENES: [&str; 4] = ["*", "Directory", "Directory\\Background", "DesktopBackground"];

const CLASSES_ROOT: &str = "Software\\Classes";

/// 用户显式注销的留痕（写者是 C# 的 `Unregister()`，core 只读）。
/// 产品数据目录名（`%LOCALAPPDATA%\<它>`）—— `deployment.json`、各留痕 flag、日志都住在里面。
/// 跨进程契约，与 C# 的 `DeploymentInfo.ProductName` 逐字一致。
pub(crate) const PRODUCT_FOLDER: &str = "BetterDesktop";

const UNREGISTERED_FLAG: &str = "shellmenu-unregistered.flag";

/// 「当前注册是一次**有意的** dev 注册」的标记文件名。
///
/// 必须与 C# 侧 `ComShellExtensionRegistrar.DevMarkerFileName` **逐字一致** ——
/// 对不上就等于标记不存在，dev 注册会被自愈反复覆盖（用户看到注册在跳动）。
const DEV_FLAG: &str = "BetterDesktopDev.flag";

/// 右键扩展总开关（settings.json 扁平键）。
const GATE_KEY: &str = "shellmenu.comExtension";

/// 检查结论。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Registration {
    /// 已注册、指向当前部署的 DLL、四个场景键齐全。
    Current,
    /// 需要修复，附细节。**前提：注册表里那一份认得出是我们自己的**（见 [`Self::ForeignPath`]）。
    NeedsRepair(Drift),
    /// 注册表指向的 DLL **不是我们的**（文件名都对不上）—— 只告警，**绝不覆盖**。
    ///
    /// 为什么单列而不是并进 `NeedsRepair`（四审第 2 点"存在但指向别的路径 → 不覆盖"）：
    /// 但"永不覆盖"若按字面实现会**打断版本升级** —— 升级后安装根一变，旧路径就永远"不等于期望路径"，
    /// 于是永远修不了。所以真正的判据不是"是否等于期望路径"，而是**"认不认得出是我们自己"**。
    /// 文件名等于 [`NATIVE_DLL`] ⇒ 是我们的一份（换个版本/换个部署位置而已）⇒ 可安全更新；
    /// 文件名都不是 ⇒ 别人放的 ⇒ 交给人判断。
    ForeignPath {
        /// 注册表里的路径。
        registered: String,
        /// 我们期望的路径。
        expected: String,
    },
    /// 用户显式注销过 —— **不得**自动注册。
    UserUnregistered,
    /// 这是一次**有意为之的 dev 注册**（`BetterDesktopDev.flag` 存在）—— **不得**自动修复。
    ///
    /// 为什么单列而不并进 [`Self::Current`]：它**不表示"状态是对的"**，只表示"别动它"。
    /// `--dev` 会把开发目录的 DLL 写进注册表（显式 opt-in，允许），而按生产规则算出的期望路径是
    /// 安装根 —— 若自愈照常触发，dev 注册会被每 60 秒"修"回去，用户看到注册在跳动、`--dev` 形同虚设。
    DevRegistered,
    /// 开关关闭 —— 用户不想要它。
    GateClosed,
    /// 原生 DLL 本地未部署：注册了 explorer 也加载不到，此时"修复"只是把垃圾写进注册表。
    DllMissing,
}

/// "注册表里的那一份不满足期望" **具体**是什么。
///
/// # 为什么 `registered` / `expected` 是字段，而不是只塞进一句人读文本
///
/// 触发修复与熔断时都要记日志。只给一句 `needs repair: ...` 时，读日志的人（尤其是几周后的自己）
/// **无法判断这是路径漂移、大小写差异，还是别的不一致** —— 而这些的处置完全不同。
/// 故 X（注册值）与 Y（期望值）必须是结构化的、每条日志都带上的。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Drift {
    /// 注册表里记录的 DLL 路径（`None` = 键不存在或值为空）。
    pub registered: Option<String>,
    /// 我们期望的路径（`None` = 本地未部署）。
    pub expected: Option<String>,
    /// 人读原因（措辞被日志依赖，改动需同步测试）。
    pub why: String,
}

impl Drift {
    /// 日志用的一行：`registered=<X> expected=<Y> — <原因>`。**永远带着 X 和 Y**。
    ///
    /// 缺失的那一侧写 `(none)` / `(not deployed)` 而不是省略 ——
    /// "键不存在"本身就是要看的信息（它解释为什么没有 X）。
    pub fn describe(&self) -> String {
        format!(
            "registered={} expected={} — {}",
            self.registered.as_deref().unwrap_or("(none)"),
            self.expected.as_deref().unwrap_or("(not deployed)"),
            self.why
        )
    }
}

impl Registration {
    /// 上线日志用的一行描述。
    pub fn describe(&self) -> String {
        match self {
            Self::Current => "already registered and pointing at the deployed DLL".to_string(),
            Self::NeedsRepair(drift) => format!("needs repair: {}", drift.describe()),
            Self::ForeignPath {
                registered,
                expected,
            } => format!(
                "registry points at '{registered}' which is NOT ours (expected '{expected}') — \
                 will not overwrite; a human should decide"
            ),
            Self::UserUnregistered => {
                "the user explicitly unregistered it (flag present) — will not re-register".to_string()
            }
            Self::DevRegistered => format!(
                "a dev registration is in effect ({DEV_FLAG} present) — self-heal is suspended by design; \
                 re-register without --dev to point back at the install root"
            ),
            Self::GateClosed => "disabled by its switch (shellmenu.comExtension=false)".to_string(),
            Self::DllMissing => {
                format!("{NATIVE_DLL} is not deployed locally; nothing to register")
            }
        }
    }

    /// 是否应当**触发**一次性注册进程。
    ///
    /// 只有"**确认是我们自己的**、但状态不完整"才触发。其余五种都不是"我们该动手"的场景：
    /// 已就绪 / 用户注销 / 开关关闭 / DLL 缺失 / **认不出的外来路径**。
    /// 最后一条尤其重要 —— 自愈去覆盖一个不属于自己的注册，是比"漏修"严重得多的错误。
    ///
    /// **调用者 = [`RepairGate::on_check`]**（S4-2 第 2 步起）。这条策略留在判定模块而不是
    /// 触发侧，是为了避免"什么算该修"散成两处。
    pub fn should_trigger_repair(&self) -> bool {
        matches!(self, Self::NeedsRepair(_))
    }
}

/// 注册表里观察到的原始事实（**纯数据**，便于单测直接构造各种组合）。
#[derive(Debug, Default, Clone)]
pub struct Observed {
    /// `CLSID\{…}\InprocServer32` 默认值（`None` = 键不存在或值为空）。
    pub registered_dll: Option<String>,
    /// 已注册的场景键数量（0..=4）。
    pub scene_count: usize,
    /// 本地找到的、应当指向的原生 DLL（`None` = 未部署）。
    pub expected_dll: Option<PathBuf>,
    /// `shellmenu-unregistered.flag` 是否存在。
    pub user_unregistered: bool,
    /// `BetterDesktopDev.flag` 是否存在（= 当前注册是一次有意的 dev 注册）。
    pub dev_marker: bool,
    /// `shellmenu.comExtension` 开关是否开启。
    pub gate_open: bool,
}

/// 纯判定（无 IO，可被单测完整覆盖）。
///
/// 判定次序即优先级：**用户的显式意愿（开关 / 注销留痕）永远先于"技术上的不完整"**。
/// 一个被用户关掉的扩展即便注册残缺，也不该被自愈拉回来 —— 那正是"守护把用户关掉的东西复活"
/// 这一类 bug 的成因（计划 §6.5）。
pub fn classify(observed: &Observed) -> Registration {
    if !observed.gate_open {
        return Registration::GateClosed;
    }
    if observed.user_unregistered {
        return Registration::UserUnregistered;
    }
    // dev 标记与"显式注销"同属**人工信号**，都必须先于"技术上的不完整"生效 ——
    // 它甚至先于 DllMissing：从安装根跑 core 时 dev 标记下期望路径完全解析得出来，
    // 若排在 DllMissing 之后，这条分支在真实场景里几乎永远不会被走到。
    if observed.dev_marker {
        return Registration::DevRegistered;
    }
    let Some(expected) = &observed.expected_dll else {
        return Registration::DllMissing;
    };

    // 空串与"值不存在"等价，都归"未注册"——**不能**让它掉进下面的"外来路径"分支，
    // 否则一个被清空过的注册项会被判成"别人的东西"，于是永远修不了。
    let Some(registered) = observed
        .registered_dll
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
    else {
        return Registration::NeedsRepair(Drift {
            registered: None,
            expected: Some(expected.display().to_string()),
            why: "CLSID\\InprocServer32 is missing or empty".to_string(),
        });
    };

    if !same_path(registered, expected) {
        // 路径不同 → 再问一句"这认得出是我们自己的吗"（见 `ForeignPath` 的说明）。
        // 文件名对得上 = 我们的一份（换版本 / 换部署位置）→ 可安全更新；
        // 对不上 = 别人放的 → **只告警，绝不覆盖**。
        if !is_our_dll_file_name(registered) {
            return Registration::ForeignPath {
                registered: registered.to_string(),
                expected: expected.display().to_string(),
            };
        }
        return Registration::NeedsRepair(Drift {
            registered: Some(registered.to_string()),
            expected: Some(expected.display().to_string()),
            why: "points at a different DLL than the deployed one".to_string(),
        });
    }

    if observed.scene_count == 0 {
        return Registration::NeedsRepair(Drift {
            registered: Some(registered.to_string()),
            expected: Some(expected.display().to_string()),
            why: "CLSID is registered but no context-menu scene key exists".to_string(),
        });
    }
    if observed.scene_count < SCENES.len() {
        return Registration::NeedsRepair(Drift {
            registered: Some(registered.to_string()),
            expected: Some(expected.display().to_string()),
            why: format!(
                "only {} of {} scene keys are registered",
                observed.scene_count,
                SCENES.len()
            ),
        });
    }

    Registration::Current
}

/// 读注册表 + 文件系统 + 配置，得出当前状态。
pub fn check() -> Registration {
    let settings = crate::settings::Settings::load();
    let gate_open = settings.get_bool(GATE_KEY, true);

    let observed = Observed {
        registered_dll: registered_dll(),
        scene_count: registered_scene_count(),
        expected_dll: resolve_native_dll(),
        user_unregistered: unregistered_flag_exists(),
        dev_marker: dev_flag_exists(),
        gate_open,
    };
    classify(&observed)
}

/// 启动时 + 每次状态变化时记一行（**不每轮都记** —— 60 秒一行的"一切正常"只会淹掉真问题）。
pub fn log_state(state: &Registration) {
    match state {
        // 【这句只说"该修"，不动手】动不动手由 `RepairGate` 决定并单独记一条（含 attempt / next check）。
        // 两件事分开记的理由：状态变化与触发决策是**不同的**事件，混成一句会看不出"卡在退避里"还是"真的没修"。
        Registration::NeedsRepair(drift) => crate::log::warn(format!(
            "shellmenu: {} — repair is due (whether/when it is triggered is decided by RepairGate)",
            drift.describe()
        )),
        // 外来路径是**需要人判断**的事，必须 warn 且说清"我们不会动它"。
        // 用 info 会让它在日志里沉下去，而这正是最该被人看见的一条。
        Registration::ForeignPath { .. } => {
            crate::log::warn(format!("shellmenu: {}", state.describe()))
        }
        other => crate::log::info(format!("shellmenu: {}", other.describe())),
    }
}

// ───────────────────────── 自动修复闸门（S4-2 第 2 步） ─────────────────────────

/// 触发修复后的退避窗口：**5 分钟内不重复触发**。
///
/// 修复是低频动作，而检查每 60 秒一次 —— 没有退避的话，一次失败的修复会在下一分钟再试一遍：
/// 5 分钟就是 5 次无谓的 CLI 拉起 + 5 条日志。
pub const REPAIR_BACKOFF: Duration = Duration::from_secs(300);

/// 连续多少次"修了仍不一致"就熔断。
pub const REPAIR_MAX_ATTEMPTS: u32 = 3;

/// 修复动作的**窄命令**（只注册右键扩展，不碰自启 / 其它系统集成）。
///
/// **core 自己不写 `HKCU\Software\Classes`**（本模块模块头红线）：注册表写入集中在 C# 侧**一个**
/// 实现里，由 core **触发**、CLI **执行**。两个写入者就是本仓库已经吃过的那类事故。
///
/// 拉起本身走 [`crate::cli`]：`BetterDesktop.Cli.exe` 这个字面量在 core 里**只出现一处**，
/// 这样"core 能拉起哪些 exe"一眼可审（也正好是架构门禁 `lifecycle-owner` 的登记粒度）。
const CLI_REPAIR_ARGS: &str = "--shellmenu-register";

/// 一轮检查的处置（**纯决策**，不含 IO —— 便于完整单测）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RepairDecision {
    /// 什么都不做。
    Idle,
    /// 触发一次修复；`attempt` 从 1 开始计数。
    Trigger {
        /// 这是第几次尝试（1..=[`REPAIR_MAX_ATTEMPTS`]）。
        attempt: u32,
    },
    /// **刚刚**熔断（越阈值那一刻返回一次，之后一律 `Idle`）—— 便于"只记一次 ERROR"。
    Degraded {
        /// 连续失败次数（= 已经尝试过多少次）。
        attempts: u32,
    },
}

/// 自动修复的闸门：退避 / 熔断 / 收敛重置。
///
/// # 四条规则（缺一条就会掉进不同的坑）
///
/// 1. **退避**：触发后 [`REPAIR_BACKOFF`] 内不再触发 —— 否则失败会变成每分钟一次的循环。
/// 2. **熔断**：连续 [`REPAIR_MAX_ATTEMPTS`] 次"修了仍不一致" → `degraded`，停止自动修复并 ERROR 一次。
/// 3. **收敛重置**：状态变为 [`Registration::Current`]（无论是自动修复修好的，还是**用户手动**
///    `bdctl --shellmenu-register` 修好的）→ 计数清零、**解除熔断**。
///    这正是 ERROR 日志里那句 "run 'bdctl --shellmenu-register' to reset" 的实现 ——
///    不需要额外的 IPC 通道去"通知 core 已重置"，**收敛本身就是那个信号**。
/// 4. **dev 标记**：由 [`Registration::should_trigger_repair`] 把关（`DevRegistered` 返回 `false`）——
///    否则 dev 注册会被判漂移 → 触发修复 → 写生产路径 → 用户手动改回 → 再触发，无限循环。
#[derive(Debug, Default, Clone)]
pub struct RepairGate {
    /// 上一次**触发**修复的时刻（`None` = 从未触发过）。
    last_trigger: Option<std::time::Instant>,
    /// 连续"触发了却仍不一致"的次数。
    consecutive_failures: u32,
    /// 熔断：不再自动修复，等人工介入（或状态自己收敛）。
    degraded: bool,
}

impl RepairGate {
    pub fn new() -> Self {
        Self::default()
    }

    /// 是否处于熔断。
    ///
    /// **只被单测使用**，故标 `#[cfg(test)]`。
    ///
    /// 【为什么是 `cfg(test)` 而不是 `allow(dead_code)`】两者都能让 clippy 闭嘴，但语义不同：
    /// `allow` 说的是"这段有存在的理由、只是暂时没人用"（= 预留借口），
    /// `cfg(test)` 说的是"**它的唯一消费者就是测试**"（= 事实）。
    /// 这里的情况是后者 —— 而且 `cfg(test)` 顺带保证它**永不进入发布二进制**。
    /// （`status` 也不经它：管道本身坏了时 `status` 同样拿不到。）
    #[cfg(test)]
    pub fn is_degraded(&self) -> bool {
        self.degraded
    }

    /// 一轮检查后的决策。**时间由调用方注入**（`now`），因此退避/熔断可被单测完全覆盖。
    pub fn on_check(&mut self, state: &Registration, now: std::time::Instant) -> RepairDecision {
        // ① 已收敛 → **完全**归零：计数、熔断标志、以及 `last_trigger`（规则 3）
        if matches!(state, Registration::Current) {
            if self.consecutive_failures > 0 || self.degraded {
                crate::log::info(format!(
                    "shellmenu: repair converged after {} attempt(s); the gate is reset",
                    self.consecutive_failures
                ));
            }
            self.consecutive_failures = 0;
            self.degraded = false;
            // 【`last_trigger` 也必须清掉，别漏】只清计数会让**下一次漂移**被算成
            // "上一次触发过却没收敛"：attempt 从 2 起算，而且熔断阈值被凭空吃掉一格
            // （真需要 3 次才熔断的情形，第 2 次就熔断了）。
            // 它是"这一轮修复还没结束"的标志 —— 收敛了就没有"上一轮"了。
            // 这个漏项是被单测（收敛后再次漂移 → attempt 必须从 1 开始）逮出来的。
            self.last_trigger = None;
            return RepairDecision::Idle;
        }

        // ② 只有"**确认是我们自己**、但状态不完整"才可能触发（规则 4 + 人工信号优先）
        if !state.should_trigger_repair() {
            return RepairDecision::Idle;
        }

        // ③ 熔断中：不再自动修（规则 2）
        if self.degraded {
            return RepairDecision::Idle;
        }

        // ④ 退避窗口内：等下一轮（规则 1）
        if let Some(last) = self.last_trigger
            && now.duration_since(last) < REPAIR_BACKOFF
        {
            return RepairDecision::Idle;
        }

        // ⑤ 上一次触发过、而这一轮**仍未**收敛 → 记一次失败
        if self.last_trigger.is_some() {
            self.consecutive_failures += 1;
            if self.consecutive_failures >= REPAIR_MAX_ATTEMPTS {
                self.degraded = true;
                return RepairDecision::Degraded {
                    attempts: self.consecutive_failures,
                };
            }
        }

        self.last_trigger = Some(now);
        RepairDecision::Trigger {
            attempt: self.consecutive_failures + 1,
        }
    }
}

/// 把闸门决策落到**动作与日志**上（IO 薄层：记日志 + 异步拉起 CLI）。
///
/// 日志必须能回答四个问题，缺一个就会让人卡在"看得见有问题、判断不出是哪一种"：
/// **X**（注册值）/ **Y**（期望值）/ **attempt**（试到第几次）/ **next**（下次什么时候会再试）。
pub fn act(gate: &mut RepairGate, state: &Registration) {
    // 触发/熔断时都要把"具体哪里不一致"带上。非 NeedsRepair 走到这里是不可能触发修复的，
    // 用 describe() 兜底只是为了不写 unwrap。
    let detail = match state {
        Registration::NeedsRepair(drift) => drift.describe(),
        other => other.describe(),
    };

    match gate.on_check(state, std::time::Instant::now()) {
        RepairDecision::Idle => {}

        RepairDecision::Trigger { attempt } => {
            // 第二次起要显式说明"上一次没修好" —— 否则日志里只有一串 attempt，看不出进展或停滞
            let failed_before = if attempt > 1 {
                format!(
                    "repair attempt {}/{} did not converge (drift persists). ",
                    attempt - 1,
                    REPAIR_MAX_ATTEMPTS
                )
            } else {
                String::new()
            };
            crate::log::warn(format!(
                "shellmenu: drift detected. {detail}\n    \
                 ⇒ {failed_before}triggering repair (attempt {attempt}/{REPAIR_MAX_ATTEMPTS}); \
                 next auto-repair no sooner than {}s (checks still run every 60s)",
                REPAIR_BACKOFF.as_secs()
            ));
            trigger_repair_async();
        }

        RepairDecision::Degraded { attempts } => crate::log::error(format!(
            "shellmenu: {attempts} consecutive repair attempts failed to converge. {detail}\n    \
             ⇒ marking degraded; NO further auto-repair. \
             run 'bdctl --shellmenu-register' to reset (a converged state clears this automatically)"
        )),
    }
}

/// 一次性、**异步**触发修复：派发 CLI 的 [`CLI_REPAIR_ARGS`]，**不等结果**。
///
/// # 为什么必须异步
///
/// core 是常驻进程。同步等待会把这一轮巡检卡在一个可能卡住的子进程上 ——
/// "CLI 卡住 → core 卡住 → 监护停摆"比"这次没修好"严重得多。
/// 修没修好由**下一轮检查**（≥ [`REPAIR_BACKOFF`]）复查确认，不需要在这里等。
/// （[`crate::cli::dispatch_async`] 本身就是"拉起即返回"，这里只是把它的失败补上语义。）
///
/// # 失败为什么是 ERROR
///
/// "连修复都拉不起来"意味着这条自愈链路彻底断了（CLI 没部署 / 被删 / 拉不起）。
/// 那与"修了但没修好"是两件事，必须分开报 —— 而且此时**没有任何其它地方**会报它。
pub fn trigger_repair_async() -> bool {
    let dispatched = crate::cli::dispatch_async(CLI_REPAIR_ARGS);
    if !dispatched {
        crate::log::error(
            "shellmenu: the repair could NOT be dispatched — drift will be retried after the backoff",
        );
    }
    dispatched
}

// ───────────────────────────── 注册表读取（只读） ─────────────────────────────

/// `HKCU\Software\Classes\CLSID\{…}\InprocServer32` 的默认值。
fn registered_dll() -> Option<String> {
    read_default_string(&format!(
        r"{CLASSES_ROOT}\CLSID\{CLASSIC_CLSID}\InprocServer32"
    ))
}

/// 已注册的场景键数量。
fn registered_scene_count() -> usize {
    SCENES
        .iter()
        .filter(|scene| {
            read_default_string(&format!(
                r"{CLASSES_ROOT}\{scene}\shellex\ContextMenuHandlers\{HANDLER_KEY}"
            ))
            .is_some_and(|v| !v.trim().is_empty())
        })
        .count()
}

/// 读一个键的默认值（`None` = 键不存在 / 值不存在 / 值非字符串 / 读失败）。
fn read_default_string(subkey: &str) -> Option<String> {
    let subkey_wide = to_wide(subkey);
    let mut key = HKEY::default();

    let opened = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            windows::core::PCWSTR(subkey_wide.as_ptr()),
            0,
            KEY_READ,
            &mut key,
        )
    };
    if opened != ERROR_SUCCESS {
        return None;
    }
    let key = OwnedKey(key);

    // 空值名 = 默认值
    let name = to_wide("");
    let mut kind = REG_SZ;
    let mut size = 0u32;

    // 第一次调用只为拿长度
    let queried = unsafe {
        RegQueryValueExW(
            key.0,
            windows::core::PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            None,
            Some(&mut size),
        )
    };
    if queried != ERROR_SUCCESS || size == 0 {
        return None;
    }
    // 注册表里字符串是 UTF-16（可能带结尾 NUL）。这里只支持 REG_SZ：其余类型（DWORD 等）
    // 不是本模块关心的东西，返回 None 比强转成乱码诚实。
    if kind != REG_SZ {
        return None;
    }

    let mut buf = vec![0u8; size as usize];
    let queried = unsafe {
        RegQueryValueExW(
            key.0,
            windows::core::PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            Some(buf.as_mut_ptr()),
            Some(&mut size),
        )
    };
    if queried != ERROR_SUCCESS {
        return None;
    }

    let units: Vec<u16> = buf
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    let end = units.iter().position(|&c| c == 0).unwrap_or(units.len());
    let text = String::from_utf16_lossy(&units[..end]);
    let text = text.trim().to_string();
    if text.is_empty() { None } else { Some(text) }
}

/// 仅持有所有权、离开作用域即关的注册表键。
struct OwnedKey(HKEY);

impl Drop for OwnedKey {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            let _ = unsafe { RegCloseKey(self.0) };
        }
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 该路径的文件名是不是我们的原生 DLL。
///
/// 只比文件名，不比目录：同一份 DLL 会出现在安装根 / 各版本目录 / dist 构建目录等**多个位置**，
/// 用目录判定会把"我们自己换了个位置"误判成"外来路径"，从而永远不自愈。
fn is_our_dll_file_name(path: &str) -> bool {
    std::path::Path::new(path)
        .file_name()
        .map(|n| n.to_string_lossy().eq_ignore_ascii_case(NATIVE_DLL))
        .unwrap_or(false)
}

/// 路径**等价**判定 —— 权威是 `protocols/native-dll-path-test-vectors.json` 的 `_path_equivalence`。
///
/// 归一化 = 分隔符统一为 `\` + 去尾分隔符 + 忽略大小写。
///
/// **它必须与 C# 侧一致**：那边 `ComPathDrifted` 原先用 `string.Equals(..., OrdinalIgnoreCase)`，
/// 只忽略大小写、不归一分隔符 —— 两侧对"等价"的判断不同，就会出现"一侧说漂移、另一侧说没有"
/// 这类分歧（与本契约要解决的是同一类病）。
///
/// **刻意不做**：`.` / `..` 解析、8.3 短名展开（`PROGRA~1`）、文件系统真实大小写查询、符号链接解析。
/// 等价判定过**宽**会让"路径确实变了"被跳过 —— 自愈就不再发生；过**窄**只是多触发一次幂等修复
/// （有 RepairGate 兜底）。**宁可多修，不可漏修。**
pub fn path_eq(a: &str, b: &str) -> bool {
    fn normalize(s: &str) -> String {
        s.trim()
            .replace('/', "\\")
            .trim_end_matches('\\')
            .to_ascii_lowercase()
    }
    normalize(a) == normalize(b)
}

fn same_path(a: &str, b: &Path) -> bool {
    path_eq(a, &b.to_string_lossy())
}

/// 定位应当被注册的原生 DLL。
///
/// # 顺序**必须**与仓库既有的组件定位链一致：本进程目录 → 安装根 → `%LOCALAPPDATA%\BetterDesktop`
///
/// 这不是风格问题。**顺序不一致会制造无限修复循环**：core 判"期望路径"用一套，CLI 写注册表用另一套，
/// 于是 core 触发修复 → CLI 写入它那套路径 → core 再看，还是"不等于期望" → 再触发 …… 每 60 秒写一次注册表。
///
/// 真机实测到过这个分歧：core 期望 `…\app\<版本>\native\…`（安装根），
/// 而 C# 的 `ResolveNativeDllPath()` **只看自己的 `AppContext.BaseDirectory`**，期望的是
/// `…\BetterDesktop.Cli\bin\Release\…\native\…`。两边各自"自洽"，合起来就是死循环。
///
/// 故此处对齐 `DesktopControlLocator` / `ComponentPaths` 的既有链（进程目录 → 安装根 → LOCALAPPDATA），
/// 并已在计划里记下 C# 侧**同样需要**补上安装根这一步 —— 只改一侧不够。
fn resolve_native_dll() -> Option<PathBuf> {
    let process_dir = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|d| d.to_path_buf()));
    let local_appdata = std::env::var("LOCALAPPDATA").ok().map(PathBuf::from);

    let inputs = PathInputs {
        process_dir,
        install_root: install_root(),
        local_appdata,
        // 运行期的 dev 模式来自 `bdctl --shellmenu-register --dev`；core 自己**从不**以 dev 身份解析，
        // 它只负责"识别注册表里那份是不是 dev 注册"（见 `check` 的 dev 标记分支）。
        dev_mode: false,
    };
    resolve_dll_path(&inputs, &|p| p.is_file())
}

/// 路径解析的输入（**纯数据** —— 因此可以被 `protocols/native-dll-path-test-vectors.json` 直接驱动）。
#[derive(Debug, Clone, Default)]
pub struct PathInputs {
    /// 发起解析的进程所在目录。
    pub process_dir: Option<PathBuf>,
    /// `deployment.json` 记录的安装根；`None` = 指针不可读或目录不存在。
    pub install_root: Option<PathBuf>,
    /// `%LOCALAPPDATA%`。
    pub local_appdata: Option<PathBuf>,
    /// 显式开发者模式（`--dev`）。
    pub dev_mode: bool,
}

/// **注册表路径**的解析规则 —— 权威是 `protocols/native-dll-path-test-vectors.json`。
///
/// ```text
/// ⓪ 输出必须是**绝对路径**；相对候选直接不采信（注册表里的相对路径行为未定义）。
/// ① 安装根有效 → 只认安装根：其中有 DLL 就用，没有则拒绝（不回退）。
/// ② 安装根无效 → 候选目录按序：
///       · 进程目录 —— 仅当 dev_mode 或 它位于 %LOCALAPPDATA%\BetterDesktop\ 之下
///       · %LOCALAPPDATA%\BetterDesktop（生产态兜底位，与"进程碰巧在哪"无关）
/// ③ 目录内优先 native\ 子目录，其次扁平同目录。
/// ```
///
/// **刻意不处理**（见 `_path_equivalence._non_goals`）：`.`/`..` 不展开、8.3 短名不解析、
/// UNC 不特殊处理。这些形式不应出现在注册表里，出现即为异常 —— 按"不等价"处理并告警，
/// 而不是尽力去理解它。写下来是为了防下一个实现者按直觉实现（那正是本契约要消灭的漂移）。
///
/// # 为什么与 `DesktopControlLocator` 的顺序**相反**（不要"统一"它们）
///
/// 那个是**运行时定位**（拉起一个进程），"调用方自己那份永远优先"让开发态 bin 直接跑不受影响。
/// 而这里是决定**写进注册表的持久引用** —— explorer 每次右键都按它加载 DLL。
/// 让"执行进程碰巧在哪"决定它，就等于把 dev bin 路径写进注册表，而 dev bin 一次 clean 菜单就废。
/// 本仓库已实测到过这种互斗（见向量文件头的 D6 记录）。**持久引用必须指向最持久的位置。**
///
/// # 安装根有效却缺 DLL 时**不回退**
///
/// 那说明部署本身不完整。此时回退去注册别处的 DLL，会把"部署不全"掩盖成"注册成功"。
pub fn resolve_dll_path(inputs: &PathInputs, exists: &dyn Fn(&Path) -> bool) -> Option<PathBuf> {
    // ⓪ 绝对路径**前置条件**：相对候选直接不采信（不是"采信后再比较"）。
    //   注册表里的相对路径，explorer 会在任意工作目录下加载它 —— 行为未定义。
    //   故把它挡在候选之外，而不是让它参与后续判定。见 `_path_equivalence._non_goals` 第 3 条。
    let acceptable = |p: &Path| p.is_absolute() && exists(p);

    // ① 安装根有效 → 只认它
    if let Some(root) = &inputs.install_root {
        return candidates(root).into_iter().find(|p| acceptable(p));
    }

    // ② 安装根无效 → 进程目录（受位置/开关约束）+ 生产态兜底位
    let mut dirs: Vec<PathBuf> = Vec::new();

    if let Some(process_dir) = &inputs.process_dir {
        let production_place = inputs
            .local_appdata
            .as_ref()
            .map(|base| is_under(process_dir, &base.join("BetterDesktop")))
            .unwrap_or(false);

        if inputs.dev_mode || production_place {
            dirs.push(process_dir.clone());
        }
    }

    if let Some(base) = &inputs.local_appdata {
        let production = base.join("BetterDesktop");
        if !dirs.contains(&production) {
            dirs.push(production);
        }
    }

    dirs.iter()
        .flat_map(|dir| candidates(dir))
        .find(|p| acceptable(p))
}

/// 某个目录下的候选 DLL（`native\` 子目录优先，其次扁平）。
fn candidates(dir: &Path) -> Vec<PathBuf> {
    vec![dir.join("native").join(NATIVE_DLL), dir.join(NATIVE_DLL)]
}

/// `child` 是否位于 `parent` **之下**（按路径段比较）。
///
/// 必须按段比较而不能用字符串前缀：否则 `…\BetterDesktopTrap\bin` 会被判成
/// `…\BetterDesktop` 之下，于是开发路径被误当成生产路径放行（向量里有这条用例）。
pub(crate) fn is_under(child: &Path, parent: &Path) -> bool {
    let normalize = |p: &Path| {
        p.to_string_lossy()
            .replace('/', "\\")
            .trim_end_matches('\\')
            .to_ascii_lowercase()
    };
    let (c, p) = (normalize(child), normalize(parent));
    if p.is_empty() {
        return false;
    }
    c == p || c.starts_with(&format!("{p}\\"))
}

/// 安装根（`%LOCALAPPDATA%\BetterDesktop\deployment.json` 记录；未用安装器安装时为 `None`）。
pub(crate) fn install_root() -> Option<PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    let path = PathBuf::from(base)
        .join(PRODUCT_FOLDER)
        .join("deployment.json");
    let text = std::fs::read_to_string(path).ok()?;
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let root = serde_json::from_str::<serde_json::Value>(text)
        .ok()?
        .get("installRoot")?
        .as_str()?
        .to_string();
    if root.is_empty() {
        return None;
    }
    Some(PathBuf::from(root))
}

/// `shellmenu-unregistered.flag` 是否存在。
fn unregistered_flag_exists() -> bool {
    let Ok(base) = std::env::var("LOCALAPPDATA") else {
        return false;
    };
    PathBuf::from(base)
        .join("BetterDesktop")
        .join(UNREGISTERED_FLAG)
        .is_file()
}

/// `BetterDesktopDev.flag` 是否存在（= 当前注册是一次有意的 dev 注册）。
fn dev_flag_exists() -> bool {
    let Ok(base) = std::env::var("LOCALAPPDATA") else {
        return false;
    };
    PathBuf::from(base)
        .join("BetterDesktop")
        .join(DEV_FLAG)
        .is_file()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn deployed() -> PathBuf {
        PathBuf::from(r"C:\Apps\BetterDesktop\native\BetterDesktopShellMenu.dll")
    }

    fn healthy() -> Observed {
        Observed {
            registered_dll: Some(deployed().to_string_lossy().to_string()),
            scene_count: SCENES.len(),
            expected_dll: Some(deployed()),
            user_unregistered: false,
            dev_marker: false,
            gate_open: true,
        }
    }

    #[test]
    fn healthy_state_is_current() {
        assert_eq!(classify(&healthy()), Registration::Current);
        assert!(!classify(&healthy()).should_trigger_repair());
    }

    /// 四审要求的三种基本识别：不存在 / 正确 / 路径错。
    #[test]
    fn recognises_the_three_basic_states() {
        // ① 完全未注册
        let none = Observed {
            registered_dll: None,
            scene_count: 0,
            ..healthy()
        };
        assert!(matches!(classify(&none), Registration::NeedsRepair(_)));

        // ② 已注册且正确
        assert_eq!(classify(&healthy()), Registration::Current);

        // ③ 已注册但指向**我们自己的另一份**（换版本 / 换部署位置）→ 可安全更新
        let moved = Observed {
            registered_dll: Some(r"D:\Old\BetterDesktopShellMenu.dll".to_string()),
            ..healthy()
        };
        let Registration::NeedsRepair(drift) = classify(&moved) else {
            panic!("our own DLL at a stale path must be flagged for update");
        };
        // X / Y 必须是**结构化字段**（不是只在一句话里）—— 日志与诊断直接取它们
        assert_eq!(drift.registered.as_deref(), Some(r"D:\Old\BetterDesktopShellMenu.dll"));
        assert_eq!(
            drift.expected.as_deref(),
            Some(deployed().to_string_lossy().as_ref())
        );
        let text = drift.describe();
        assert!(text.contains("registered="), "{text}");
        assert!(text.contains("expected="), "{text}");
    }

    /// **外来路径绝不覆盖**（四审第 2 点）：文件名都不是我们的 → 只告警、不触发修复。
    ///
    /// 反面对照见上一条：同一文件名换个目录**必须**被判为可修复 ——
    /// 否则"永不覆盖"会把版本升级这条路堵死（升级后安装根一变就永远修不了）。
    #[test]
    fn foreign_dll_is_reported_but_never_overwritten() {
        let foreign = Observed {
            registered_dll: Some(r"C:\OtherProduct\TheirMenu.dll".to_string()),
            ..healthy()
        };
        let state = classify(&foreign);
        assert!(
            matches!(state, Registration::ForeignPath { .. }),
            "认不出的路径必须单列，不能并进 NeedsRepair：{state:?}"
        );
        assert!(
            !state.should_trigger_repair(),
            "外来路径绝不能触发覆盖 —— 那是比漏修严重得多的错误"
        );
        assert!(state.describe().contains("NOT ours"), "{}", state.describe());
        assert!(state.describe().contains("will not overwrite"));
    }

    /// 文件名相同、只有目录不同 ⇒ 仍是我们自己的（大小写不敏感）。
    #[test]
    fn same_file_name_in_another_directory_is_still_ours() {
        for path in [
            r"C:\Somewhere\Else\betterdesktopshellmenu.dll",
            r"D:\dist\modules\2026.09.18\01-主程序\native\BetterDesktopShellMenu.DLL",
        ] {
            let ours = Observed {
                registered_dll: Some(path.to_string()),
                ..healthy()
            };
            assert!(
                matches!(classify(&ours), Registration::NeedsRepair(_)),
                "同名的另一份必须可修复：{path}"
            );
        }
    }

    #[test]
    fn dll_file_name_check_is_robust() {
        assert!(is_our_dll_file_name(r"C:\a\native\BetterDesktopShellMenu.dll"));
        assert!(is_our_dll_file_name(r"C:\a\native\BETTERDESKTOPSHELLMENU.DLL"));
        assert!(!is_our_dll_file_name(r"C:\a\TheirMenu.dll"));
        assert!(!is_our_dll_file_name(r"C:\a\"));
        assert!(!is_our_dll_file_name(""));
    }

    /// **部分注册**：CLSID 在、场景键不全 —— 只看一个场景键会漏掉这种情况。
    #[test]
    fn partial_scene_registration_needs_repair() {
        let partial = Observed {
            scene_count: 1,
            ..healthy()
        };
        let Registration::NeedsRepair(drift) = classify(&partial) else {
            panic!("partial registration must be flagged");
        };
        assert!(drift.why.contains("1 of 4"), "{drift:?}");

        let zero = Observed {
            scene_count: 0,
            ..healthy()
        };
        assert!(matches!(classify(&zero), Registration::NeedsRepair(_)));
    }

    /// **用户的显式意愿优先于技术上的不完整**：开关关掉 / 用户注销过，
    /// 即便注册残缺也不得触发修复 —— 否则就是"守护把用户关掉的东西复活"。
    #[test]
    fn user_intent_wins_over_technical_incompleteness() {
        let broken_but_disabled = Observed {
            registered_dll: None,
            scene_count: 0,
            gate_open: false,
            ..healthy()
        };
        assert_eq!(classify(&broken_but_disabled), Registration::GateClosed);
        assert!(!classify(&broken_but_disabled).should_trigger_repair());

        let broken_but_unregistered_by_user = Observed {
            registered_dll: None,
            scene_count: 0,
            user_unregistered: true,
            ..healthy()
        };
        assert_eq!(
            classify(&broken_but_unregistered_by_user),
            Registration::UserUnregistered
        );
        assert!(!classify(&broken_but_unregistered_by_user).should_trigger_repair());
    }

    /// 原生 DLL 本地未部署时不该触发注册：注册了 explorer 也加载不到，
    /// "修复"只会往注册表里写垃圾。
    #[test]
    fn missing_dll_is_not_a_repair_case() {
        let no_dll = Observed {
            expected_dll: None,
            ..healthy()
        };
        assert_eq!(classify(&no_dll), Registration::DllMissing);
        assert!(!classify(&no_dll).should_trigger_repair());
    }

    /// 大小写与结尾分隔符不影响"指向正确"的判定（Windows 路径语义）。
    #[test]
    fn path_comparison_is_case_insensitive() {
        let cased = Observed {
            registered_dll: Some(r"C:\APPS\BETTERDESKTOP\NATIVE\betterdesktopshellmenu.DLL".to_string()),
            ..healthy()
        };
        assert_eq!(classify(&cased), Registration::Current);
    }

    /// 空字符串（键在但值被清空）必须当作"未注册"，**不能**当成"外来路径" ——
    /// 否则一个被清空过的注册项会被判成"别人的东西"，于是永远修不了。
    #[test]
    fn empty_value_is_treated_as_missing_not_foreign() {
        for blank in [String::new(), "   ".to_string()] {
            let observed = Observed {
                registered_dll: Some(blank.clone()),
                ..healthy()
            };
            let state = classify(&observed);
            assert!(
                matches!(state, Registration::NeedsRepair(_)),
                "空值 {blank:?} 应归「未注册」，实际得到 {state:?}"
            );
            assert!(state.should_trigger_repair());
        }
    }

    /// 常量必须与 C# 侧逐字一致 —— 漂移会让"检查通过但 explorer 加载不到"。
    #[test]
    fn constants_match_the_csharp_and_native_sides() {
        assert_eq!(CLASSIC_CLSID, "{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71}");
        assert_eq!(NATIVE_DLL, "BetterDesktopShellMenu.dll");
        assert_eq!(HANDLER_KEY, "BetterDesktop");
        assert_eq!(SCENES.len(), 4, "场景数必须与 C# 的 Scenes 一致");
        assert_eq!(SCENES[0], "*");
        assert_eq!(SCENES[3], "DesktopBackground");
        // 标记文件名同样是跨语言契约：对不上 = 标记不存在 = dev 注册被自愈反复覆盖
        assert_eq!(UNREGISTERED_FLAG, "shellmenu-unregistered.flag");
        assert_eq!(DEV_FLAG, "BetterDesktopDev.flag");
    }

    /// dev 标记必须先于漂移判定生效 —— 否则 `--dev` 注册会被自愈每 60 秒"修"回去。
    #[test]
    fn dev_marker_suspends_repair_even_when_the_path_looks_drifted() {
        let observed = Observed {
            registered_dll: Some(r"C:\dev\bin\native\BetterDesktopShellMenu.dll".to_string()),
            scene_count: SCENES.len(),
            expected_dll: Some(PathBuf::from(
                r"C:\Users\X\AppData\Local\BetterDesktop\app\1\native\BetterDesktopShellMenu.dll",
            )),
            user_unregistered: false,
            dev_marker: true,
            gate_open: true,
        };

        let state = classify(&observed);
        assert_eq!(state, Registration::DevRegistered);
        assert!(
            !state.should_trigger_repair(),
            "dev 注册绝不能被自动修复"
        );
    }

    /// 同一个观察事实，去掉 dev 标记就**必须**判成需要修复 ——
    /// 否则上面那条测试会因为"路径本来就不算漂移"而假绿。
    #[test]
    fn without_the_dev_marker_the_same_drift_does_trigger_repair() {
        let observed = Observed {
            registered_dll: Some(r"C:\dev\bin\native\BetterDesktopShellMenu.dll".to_string()),
            scene_count: SCENES.len(),
            expected_dll: Some(PathBuf::from(
                r"C:\Users\X\AppData\Local\BetterDesktop\app\1\native\BetterDesktopShellMenu.dll",
            )),
            user_unregistered: false,
            dev_marker: false,
            gate_open: true,
        };

        assert!(matches!(classify(&observed), Registration::NeedsRepair(_)));
    }

    // ───────────────── 路径解析：由**共享向量**驱动 ─────────────────

    #[derive(serde::Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct VectorInput {
        process_dir: Option<String>,
        install_root: Option<String>,
        local_app_data: Option<String>,
        dev_mode: bool,
        existing_files: Vec<String>,
    }

    #[derive(serde::Deserialize)]
    struct VectorCase {
        name: String,
        input: VectorInput,
        expected: Option<String>,
    }

    #[derive(serde::Deserialize)]
    struct EquivalenceCase {
        name: String,
        a: String,
        b: String,
        equivalent: bool,
    }

    #[derive(serde::Deserialize)]
    struct EquivalenceSection {
        cases: Vec<EquivalenceCase>,
    }

    #[derive(serde::Deserialize)]
    struct VectorFile {
        cases: Vec<VectorCase>,
        #[serde(rename = "_path_equivalence")]
        path_equivalence: EquivalenceSection,
    }

    /// 路径归一（与实现同款语义：分隔符统一、忽略大小写、去尾分隔符）。
    fn norm(s: &str) -> String {
        s.replace('/', "\\")
            .trim_end_matches('\\')
            .to_ascii_lowercase()
    }

    /// **契约测试**：实现必须与 `protocols/native-dll-path-test-vectors.json` 逐条一致。
    ///
    /// 这份向量是 core 与 CLI（C#）**共同**的权威 —— 两侧各读一份、各自实现，
    /// 规则一变两侧同时红。这正是为了解决"路径解析分散在两处、没有共享契约"这个根因
    ///（它已经导致过"core 触发修复 → CLI 写另一套路径 → core 再判漂移 → 再触发"的无限写注册表）。
    #[test]
    fn path_resolution_matches_the_shared_vector() {
        let file: VectorFile = serde_json::from_str(include_str!(
            "../../protocols/native-dll-path-test-vectors.json"
        ))
        .expect("共享向量必须是合法 JSON");

        assert!(
            file.cases.len() >= 15,
            "向量用例太少（{}）—— 契约覆盖不足",
            file.cases.len()
        );

        for case in &file.cases {
            let inputs = PathInputs {
                process_dir: case.input.process_dir.as_ref().map(PathBuf::from),
                install_root: case.input.install_root.as_ref().map(PathBuf::from),
                local_appdata: case.input.local_app_data.as_ref().map(PathBuf::from),
                dev_mode: case.input.dev_mode,
            };
            let existing: Vec<String> = case.input.existing_files.iter().map(|s| norm(s)).collect();
            let exists = |p: &Path| existing.contains(&norm(&p.to_string_lossy()));

            let actual = resolve_dll_path(&inputs, &exists);
            let expected = case.expected.as_ref().map(PathBuf::from);

            assert_eq!(
                actual.as_ref().map(|p| norm(&p.to_string_lossy())),
                expected.as_ref().map(|p| norm(&p.to_string_lossy())),
                "向量用例「{}」不符",
                case.name
            );
        }
    }

    /// 向量里必须**真的**覆盖住那两条会把 dev bin 写进注册表的用例 ——
    /// 否则契约可以"全绿"却漏掉 D6 事故形态。
    #[test]
    fn vector_covers_the_d6_accident_shape() {
        let file: VectorFile = serde_json::from_str(include_str!(
            "../../protocols/native-dll-path-test-vectors.json"
        ))
        .unwrap();

        let dev_bin_without_install_root = file.cases.iter().any(|c| {
            c.expected.is_none()
                && c.input.install_root.is_none()
                && !c.input.dev_mode
                && c.input
                    .process_dir
                    .as_ref()
                    .is_some_and(|d| norm(d).contains("\\bin\\release"))
                && c.input.existing_files.iter().any(|f| norm(f).contains("\\bin\\release"))
        });
        assert!(
            dev_bin_without_install_root,
            "向量必须含「安装根无效 + 进程在开发 bin → 拒绝」这条 —— 它就是 D6 的形态"
        );

        let dev_opt_in = file
            .cases
            .iter()
            .any(|c| c.input.dev_mode && c.expected.is_some());
        assert!(dev_opt_in, "向量必须含「devMode=true 时允许开发目录」这条");
    }

    /// **路径等价**也必须与向量逐条一致 —— 它同时决定"漂移判定"与"幂等判定"，
    /// 而两侧此前用的是不同规则（C# 只忽略大小写，Rust 还归一分隔符）。
    #[test]
    fn path_equivalence_matches_the_shared_vector() {
        let file: VectorFile = serde_json::from_str(include_str!(
            "../../protocols/native-dll-path-test-vectors.json"
        ))
        .unwrap();

        assert!(
            file.path_equivalence.cases.len() >= 9,
            "等价用例太少（{}）—— `_non_goals` 那几条（. / .. / 8.3 / UNC）必须有对应用例，否则只是散文",
            file.path_equivalence.cases.len()
        );

        for case in &file.path_equivalence.cases {
            assert_eq!(
                path_eq(&case.a, &case.b),
                case.equivalent,
                "等价用例「{}」不符：a={:?} b={:?}",
                case.name,
                case.a,
                case.b
            );
        }
    }

    #[test]
    fn is_under_compares_path_segments_not_string_prefixes() {
        assert!(is_under(
            Path::new(r"C:\U\AppData\Local\BetterDesktop\app\x"),
            Path::new(r"C:\U\AppData\Local\BetterDesktop")
        ));
        assert!(is_under(
            Path::new(r"c:\u\appdata\local\betterdesktop"),
            Path::new(r"C:\U\AppData\Local\BetterDesktop")
        ));
        assert!(is_under(
            Path::new(r"C:\U\AppData\Local\BetterDesktop\"),
            Path::new(r"C:\U\AppData\Local\BetterDesktop")
        ));
        assert!(
            !is_under(
                Path::new(r"C:\U\AppData\Local\BetterDesktopTrap\bin"),
                Path::new(r"C:\U\AppData\Local\BetterDesktop")
            ),
            "字符串前缀判会把 BetterDesktopTrap 误当成生产目录"
        );
        assert!(!is_under(Path::new(r"C:\Other"), Path::new(r"")));
    }

    /// 真机只读检查：不写任何注册表，只确认"这条路走得通、能给出结论"。
    #[test]
    fn real_machine_check_returns_a_state_without_writing() {
        let state = check();
        // 任何结论都合法，但必须是个明确结论（不得 panic / 不得挂住）
        let text = state.describe();
        assert!(!text.is_empty());
    }

    // ───────────────── 自动修复闸门（S4-2 第 2 步） ─────────────────

    /// 造一个"需要修复"的状态：**我们自己的** DLL，但路径不对（可安全更新）。
    fn drift_state() -> Registration {
        classify(&Observed {
            registered_dll: Some(r"D:\Old\BetterDesktopShellMenu.dll".to_string()),
            ..healthy()
        })
    }

    /// 日志必须**永远**带 X（注册值）与 Y（期望值）—— 缺任何一个，读日志的人都判断不出
    /// "是路径漂移、大小写差异，还是别的不一致"，而这些的处置完全不同。
    #[test]
    fn drift_describe_always_carries_both_values() {
        let both = Drift {
            registered: Some("A".to_string()),
            expected: Some("B".to_string()),
            why: "w".to_string(),
        };
        assert!(both.describe().contains("registered=A"), "{}", both.describe());
        assert!(both.describe().contains("expected=B"), "{}", both.describe());

        let missing = Drift {
            registered: None,
            expected: Some("B".to_string()),
            why: "w".to_string(),
        };
        // 缺失的一侧要**显式写出来**（"键不存在"本身就是要看的信息）
        assert!(
            missing.describe().contains("registered=(none)"),
            "{}",
            missing.describe()
        );
        assert!(missing.describe().contains("expected=B"));
    }

    /// 退避 + 熔断的完整时间线。
    #[test]
    fn gate_backs_off_then_trips_the_circuit_breaker() {
        let mut gate = RepairGate::new();
        let t0 = std::time::Instant::now();
        let drift = drift_state();

        // 第 1 次：立刻触发
        assert_eq!(
            gate.on_check(&drift, t0),
            RepairDecision::Trigger { attempt: 1 }
        );

        // 退避窗口内：检查再频繁也不触发（检查本来就是每 60 秒一次）
        assert_eq!(
            gate.on_check(&drift, t0 + Duration::from_secs(59)),
            RepairDecision::Idle
        );
        assert_eq!(
            gate.on_check(&drift, t0 + Duration::from_secs(299)),
            RepairDecision::Idle
        );

        // 到期：第 2 次（上一次没修好 → attempt 2）
        let t1 = t0 + REPAIR_BACKOFF;
        assert_eq!(
            gate.on_check(&drift, t1),
            RepairDecision::Trigger { attempt: 2 }
        );

        let t2 = t1 + REPAIR_BACKOFF;
        assert_eq!(
            gate.on_check(&drift, t2),
            RepairDecision::Trigger { attempt: 3 }
        );

        // 第 4 次检查：连续 3 次都没收敛 → 熔断（只报一次）
        let t3 = t2 + REPAIR_BACKOFF;
        assert_eq!(
            gate.on_check(&drift, t3),
            RepairDecision::Degraded {
                attempts: REPAIR_MAX_ATTEMPTS
            }
        );
        assert!(gate.is_degraded());

        // 熔断之后：再久也不再触发
        assert_eq!(
            gate.on_check(&drift, t3 + REPAIR_BACKOFF * 10),
            RepairDecision::Idle
        );
    }

    /// 收敛（**无论谁修的**）必须清零并**解除熔断** —— 这正是 ERROR 日志里那句
    /// "run 'bdctl --shellmenu-register' to reset" 的实现：收敛本身就是那个信号，
    /// 不需要额外的 IPC 通道去"通知 core 已重置"。
    #[test]
    fn convergence_resets_the_gate_including_degraded() {
        let mut gate = RepairGate::new();
        let drift = drift_state();
        let mut now = std::time::Instant::now();

        for _ in 0..REPAIR_MAX_ATTEMPTS {
            let _ = gate.on_check(&drift, now);
            now += REPAIR_BACKOFF;
        }
        let _ = gate.on_check(&drift, now);
        assert!(gate.is_degraded(), "连续 {REPAIR_MAX_ATTEMPTS} 次失败后应熔断");

        // 人工把状态修好 → 下一轮检查看到 Current
        let current = classify(&healthy());
        assert_eq!(
            gate.on_check(&current, now + Duration::from_secs(60)),
            RepairDecision::Idle
        );
        assert!(
            !gate.is_degraded(),
            "收敛后必须解除熔断，否则人工修好之后也再不会自愈"
        );

        // 之后再次漂移 → 计数从头开始
        assert_eq!(
            gate.on_check(&drift, now + Duration::from_secs(120)),
            RepairDecision::Trigger { attempt: 1 }
        );
    }

    /// **人工信号与 dev 标记都不得触发**：它们的共同点是"状态不完整，但**不许自动动手**"。
    ///
    /// dev 标记那条尤其关键（少了它就会无限循环）：dev 注册被判漂移 → 触发修复 → 写生产路径 →
    /// 用户手动改回 dev → 再被判漂移 → …… 每 5 分钟一轮，永远收敛不了。
    #[test]
    fn gate_never_triggers_for_human_signals_or_dev_marker() {
        let cases: Vec<(&str, Registration)> = vec![
            (
                "dev 标记",
                classify(&Observed {
                    dev_marker: true,
                    registered_dll: Some(r"D:\Old\BetterDesktopShellMenu.dll".to_string()),
                    ..healthy()
                }),
            ),
            (
                "开关关闭",
                classify(&Observed {
                    gate_open: false,
                    ..healthy()
                }),
            ),
            (
                "用户显式注销",
                classify(&Observed {
                    user_unregistered: true,
                    ..healthy()
                }),
            ),
            (
                "本地未部署",
                classify(&Observed {
                    expected_dll: None,
                    ..healthy()
                }),
            ),
            (
                "认不出的外来路径",
                classify(&Observed {
                    registered_dll: Some(r"D:\Else\some-other-thing.dll".to_string()),
                    ..healthy()
                }),
            ),
        ];

        for (name, state) in cases {
            assert!(
                !state.should_trigger_repair(),
                "{name} 不该被判定为可修复：{state:?}"
            );

            let mut gate = RepairGate::new();
            let t0 = std::time::Instant::now();
            assert_eq!(
                gate.on_check(&state, t0),
                RepairDecision::Idle,
                "{name} 触发了修复"
            );
            // 再久也不触发 —— 证明不是"只是被退避挡住"
            assert_eq!(
                gate.on_check(&state, t0 + REPAIR_BACKOFF * 100),
                RepairDecision::Idle,
                "{name} 在退避之后仍触发了修复"
            );
        }
    }
}
