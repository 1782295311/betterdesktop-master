//! 组件表 —— core 的唯一业务数据（计划 §6.1-A「服务于未来」的落点）。
//!
//! 监护循环、控制管道、托盘菜单的"启动 X"子菜单全部由本表生成。
//! **新增能力（未来 bd-infer / bd-world / GPU 仲裁 / 安全扫描）只加表项，不改 core 代码结构。**
//!
//! 表来源优先级：
//!   1. `%LOCALAPPDATA%\BetterDesktop\core.components.json`（外部覆盖，便于灰度与不重编扩展）；
//!   2. 内嵌默认表（`core/components.json`，`include_str!`）。
//!
//! 容错纪律（工具型项目侧重：非法输入必须明确处理，不静默）：
//!   - 单条表项非法（缺 name/exe、枚举值未识别、name 重复、`keepAwake=always`）→
//!     **跳过该条并记日志**，不影响其余条目；
//!   - 整份外部表不可用 → 回退内嵌默认表并记日志。
//!
//! schema 的 `tier` / `type` / `power` 三个字段是**为未来留位**（计划 §6.1-A、二审缺口四/五）：
//! 本批只落地"解析 + 监护分派 + 启动日志"，不实现 bd-infer / bd-world / GPU 分配算法。

use serde::Deserialize;

// ───────────────────────────── 枚举 ─────────────────────────────

/// core 对某组件的期望状态。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Desired {
    /// core 保证它在跑（缺则拉起，受 gate 约束）。
    Running,
    /// core 从不主动拉起；只在收到 `start` 请求时拉。
    OnDemand,
    /// core 停掉且不再拉。
    Stopped,
}

impl Desired {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "running" => Some(Self::Running),
            "on-demand" => Some(Self::OnDemand),
            "stopped" => Some(Self::Stopped),
            _ => None,
        }
    }
}

/// 六层架构中的层级归属（计划 §3.1）。决定监护规则。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tier {
    /// ① 地基：core 自身级别，永不缺席。
    Foundation,
    /// ② 基础设施：按需（有消费者才拉起，空闲自退）——如未来的 bd-infer。
    Infrastructure,
    /// ④ 表面：用户开启才跑（gate 主控）——如 bd-shell / bd-world。
    Surface,
    /// ⑤ 扩展：按需。
    Extension,
    /// ⑤ 系统扩展：按需，且可请求 core 代为拉起其它层（AI 管家属此层，**自己不 Process.Start**）。
    SystemExtension,
}

impl Tier {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "foundation" => Some(Self::Foundation),
            "infrastructure" => Some(Self::Infrastructure),
            "surface" => Some(Self::Surface),
            "extension" => Some(Self::Extension),
            "system-extension" => Some(Self::SystemExtension),
            _ => None,
        }
    }
}

/// 组件形态。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ComponentType {
    /// 常驻/按需进程。
    Process,
    /// 一次性工具（跑完即退，不守护）——如未来的安全扫描器。
    Tool,
    /// UI 面板（按需拉起，关闭即退）。
    Panel,
}

impl ComponentType {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "process" => Some(Self::Process),
            "tool" => Some(Self::Tool),
            "panel" => Some(Self::Panel),
            _ => None,
        }
    }
}

/// 组件的存活判据（S4-3 从 Watchdog 迁入）。
///
/// # 为什么需要两种
///
/// 默认的「进程名存在」对**绝大多数**组件是对的，但它有一个已经出过真机事故的例外：
/// 「桌面控制菜单」是**短命进程**、与常驻的桌面服务**同名**（都是 `BetterDesktop.DesktopControl.exe`）——
/// 按进程名判活会把"用户正在弹菜单"读成"服务在运行"，于是真服务死了**永远不被拉起**
/// （自绘桌面消失且不自愈）。那条路径改用「命令管道可达」判活。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Liveness {
    /// 进程名存在于快照中（默认）。
    Process,
    /// **命令管道已就绪**才算存活（管道名见 [`Component::liveness_pipe`]）。
    Pipe,
}

impl Liveness {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "process" => Some(Self::Process),
            "pipe" => Some(Self::Pipe),
            _ => None,
        }
    }
}

/// 睡眠时的动作。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SuspendAction {
    /// 冻结（默认）：什么都不做，唤醒后进程仍是活的。
    Freeze,
    /// 主动停止（唤醒后由 reconcile 按需拉起）。
    Stop,
    /// 只通知（发控制管道消息，不干预）。
    Notify,
}

impl SuspendAction {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "freeze" => Some(Self::Freeze),
            "stop" => Some(Self::Stop),
            "notify" => Some(Self::Notify),
            _ => None,
        }
    }
}

/// 唤醒后的动作。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResumeAction {
    /// 按差集 reconcile（默认）：先探活，只补真缺的。
    ///
    /// **局限**：只能回答"进程在不在"，回答不了"它还能不能用"——故只适合 `extension` 层。
    Reconcile,
    /// **先探测健康，不健康才重新初始化**（`surface` 层专用）。
    ///
    /// 为什么需要它：进程活着 ≠ 可用。
    /// 睡眠唤醒后 DWM 重建 → WPF 窗口可能花屏 / Z-order 错；D3D12 设备丢失 → UE5 渲染不出来；
    /// explorer 重启 → ShellDLL 钩子失效（进程在、右键菜单不工作）。
    /// 探测方式：经控制管道 `@ctl health <name>` 询问组件**自报**健康；无应答/超时/不健康 → 按 `reinit` 处理。
    /// 具体探针由组件自己实现——core 只知道"问"，不判断 WPF/D3D 的细节（分层纪律）。
    ProbeAndReinit,
    /// 强制重启。
    Restart,
    /// 重新初始化（GPU 上下文/D3D12 设备丢失场景）——如未来的 bd-infer / bd-world。
    Reinit,
    /// 只通知。
    Notify,
}

impl ResumeAction {
    fn parse(s: &str) -> Option<Self> {
        match s {
            "reconcile" => Some(Self::Reconcile),
            "probe-and-reinit" => Some(Self::ProbeAndReinit),
            "restart" => Some(Self::Restart),
            "reinit" => Some(Self::Reinit),
            "notify" => Some(Self::Notify),
            _ => None,
        }
    }
}

/// 是否阻止系统睡眠。
///
/// **`always` 被刻意排除在枚举之外**：它是"core 让系统睡不着"的那类 bug 的直接来源
/// （计划 §14 电源禁区：`keepAwake=always` 由 schema 拒绝）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeepAwake {
    /// 不阻止睡眠（默认，所有组件）。
    Never,
    /// 仅用户显式触发的长任务期间阻止，且**必须带超时**。
    UserTriggered,
}

impl KeepAwake {
    /// 从配置值解析。`keepAwake` 在 JSON 里**既可写布尔也可写字符串**：
    /// `false` 是最自然的写法，`"user-triggered"` 是语义取值，`"always"` 必须被拒。
    fn parse(raw: &RawKeepAwake) -> Result<Self, String> {
        match raw {
            RawKeepAwake::Bool(false) => Ok(Self::Never),
            RawKeepAwake::Bool(true) => Err(
                "keepAwake=true is forbidden; use \"user-triggered\" (which requires a timeout)"
                    .to_string(),
            ),
            RawKeepAwake::Str(s) => match s.trim() {
                "false" => Ok(Self::Never),
                "user-triggered" => Ok(Self::UserTriggered),
                "always" | "true" => Err(
                    "'always' is forbidden: core must never unconditionally block system sleep"
                        .to_string(),
                ),
                other => Err(format!(
                    "unknown keepAwake '{other}' (expected false | \"user-triggered\"; 'always' is forbidden)"
                )),
            },
        }
    }
}

/// 电源策略（计划 §6.1-A、二审缺口六）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PowerPolicy {
    pub on_suspend: SuspendAction,
    pub on_resume: ResumeAction,
    pub keep_awake: KeepAwake,
}

impl Default for PowerPolicy {
    fn default() -> Self {
        Self {
            on_suspend: SuspendAction::Freeze,
            on_resume: ResumeAction::Reconcile,
            keep_awake: KeepAwake::Never,
        }
    }
}

/// 一条组件定义（校验通过后的形态）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Component {
    /// 命令词（控制管道 `start <name>` 与托盘菜单都用它）；与 CLI action 词表同源。
    pub name: String,
    /// 菜单显示名。
    pub label: String,
    /// 可执行文件名（相对 core 目录解析）。
    pub exe: String,
    /// 期望状态。
    pub desired: Desired,
    /// 层级归属。
    pub tier: Tier,
    /// 组件形态。
    pub component_type: ComponentType,
    /// 电源策略。
    pub power: PowerPolicy,
    /// 可选的 settings 开关键（扁平键）；为 `None` 表示无开关。
    pub gate: Option<String>,
    /// 额外命令行参数（可为空）。
    pub args: Option<String>,
    /// 「**用户显式停止**」标记文件名（`None` = 无此语义）。
    ///
    /// 存在该标记 ⇒ core **不守护、也不拉起**它（不主动去杀：写标记的一方自己会停进程；
    /// 若它还在跑，说明那次停止还没做完，core 插手只会变成两个人在抢）。
    ///
    /// 这是从 Watchdog 迁入的语义，且**必须迁**：标记由托盘写（"退出主程序" / "停止桌面服务"），
    /// 缺了它 core 会把用户刚停掉的组件**立刻复活**，托盘那句"已停止"被静默推翻。
    /// （本仓库已踩过同类坑：托盘写 `desktop-service-stopped.flag` 而看门狗只认 `desktop-stopped.flag`。）
    pub stop_flag: Option<String>,
    /// 存活判据（默认 [`Liveness::Process`]）。
    pub liveness: Liveness,
    /// `liveness=pipe` 时要探测的管道名（跨进程契约字面量，如 `BetterDesktop.DesktopCmd`）。
    pub liveness_pipe: Option<String>,
    /// **临时条目**的移除时点（如 `S4-5`）。core 启动时会把它打进日志，故它不会静默留在表里。
    ///
    /// 唯一动机是可见性：过渡期必须有"暂时这么配"的条目（如 Host 的临时监护），
    /// 而"暂时"若只写在注释里，到期就会被忘掉。
    pub remove_at: Option<String>,
}

// ─────────────────── 监护分派（tier 的语义落地，D30） ───────────────────

/// core 是否应**主动保证**该组件在跑（不含用户显式 `start`）。
///
/// 语义（计划 §6.1-A「tier → 监护规则」）：
/// - `type=tool` → **永远 false**（见下）；
/// - `foundation` → 永远 true（地基永不缺席）；
/// - 其余 → 仅当 `desired=running` **且** gate 开。
///   - `infrastructure` / `extension` / `system-extension` 通常声明 `on-demand` → 从不主动拉起；
///   - `surface` 通常声明 `running` + gate → "用户开启后才常驻"。
///
/// `gate_open` 由调用方求值（无 gate 的组件传 `true`）。
///
/// **`type=tool` 的拦截**：一次性工具"跑完即退"，若被纳入"确保在跑"，监护器会以轮询周期
/// 无限重启它（每次都是拉起→退出→下一轮再拉起）。这类条目描述的是"我能被调起"，
/// 不是"我该一直活着"，因此从源头拒绝，而不是靠调用方记得别这么配。
pub fn auto_start(c: &Component, gate_open: bool) -> bool {
    if c.component_type == ComponentType::Tool {
        return false;
    }
    if c.tier == Tier::Foundation {
        return true;
    }
    c.desired == Desired::Running && gate_open
}

/// 是否违反「surface 常驻组件必须显式声明唤醒策略」的纪律（纯函数，供单测）。
///
/// 判据：`tier=surface` + `desired=running`（即唤醒时它**本来就在跑**）+ 未显式声明 `onResume`。
/// 此时默认的 `reconcile` 只会回答"进程在不在"，于是花屏/Z-order 错/钩子失效全被放过。
pub fn resume_discipline_violation(
    tier: Tier,
    desired: Desired,
    resume_explicit: bool,
) -> bool {
    tier == Tier::Surface && desired == Desired::Running && !resume_explicit
}

/// gate 关闭时是否**必须停掉**。
///
/// `surface` 语义的落点：用户关掉的东西不能被守护复活（计划 §6.5）。
/// `foundation` 例外——地基不因开关而停。
pub fn must_stop(c: &Component, gate_open: bool) -> bool {
    c.tier != Tier::Foundation && !gate_open
}

// ───────────────────────────── 加载 ─────────────────────────────

/// 反序列化用的原始形态：字段全可选，交由 [`validate`] 逐条检查并给出明确原因。
#[derive(Deserialize)]
struct RawComponent {
    name: Option<String>,
    label: Option<String>,
    exe: Option<String>,
    desired: Option<String>,
    tier: Option<String>,
    #[serde(rename = "type")]
    component_type: Option<String>,
    power: Option<RawPower>,
    gate: Option<String>,
    args: Option<String>,
    #[serde(rename = "stopFlag")]
    stop_flag: Option<String>,
    liveness: Option<String>,
    #[serde(rename = "livenessPipe")]
    liveness_pipe: Option<String>,
    /// 以 `_` 开头的字段是**给人看的**（与表头的 `_comment` 同款约定）；serde 默认忽略未知字段，
    /// 这里显式接出来只为在启动日志里报一次"这条是临时的，到 X 为止"。
    #[serde(rename = "_removeAt")]
    remove_at: Option<String>,
}

#[derive(Deserialize)]
struct RawPower {
    #[serde(rename = "onSuspend")]
    on_suspend: Option<String>,
    #[serde(rename = "onResume")]
    on_resume: Option<String>,
    /// `false`（布尔）与 `"user-triggered"`（字符串）两种写法都合法 → untagged。
    #[serde(rename = "keepAwake")]
    keep_awake: Option<RawKeepAwake>,
}

/// `keepAwake` 的双形态（见 [`KeepAwake::parse`]）。
#[derive(Deserialize)]
#[serde(untagged)]
enum RawKeepAwake {
    Bool(bool),
    Str(String),
}

#[derive(Deserialize)]
struct RawTable {
    components: Option<Vec<RawComponent>>,
}

/// 加载组件表（外部覆盖 → 内嵌默认 → 空表）。
pub fn load() -> Vec<Component> {
    let list = load_uncached();
    // 临时条目每次都报一遍：这是"过渡期配置"唯一的到期提醒手段（注释没人会去重读）。
    for c in &list {
        if let Some(until) = &c.remove_at {
            crate::log::warn(format!(
                "component '{}' is under TEMPORARY supervision (to be removed at {until}) — \
                 this line is here so the temporary state cannot be forgotten",
                c.name
            ));
        }
    }
    list
}

fn load_uncached() -> Vec<Component> {
    if let Some(path) = external_path()
        && let Ok(text) = std::fs::read_to_string(&path)
    {
        match parse(&text) {
            Ok(list) if !list.is_empty() => {
                crate::log::info(format!(
                    "component table loaded from external override: {} entries ({})",
                    list.len(),
                    path.display()
                ));
                return list;
            }
            Ok(_) => crate::log::warn(format!(
                "external component table is empty; falling back to embedded ({})",
                path.display()
            )),
            Err(e) => crate::log::warn(format!(
                "external component table invalid ({e}); falling back to embedded"
            )),
        }
    }

    match parse(include_str!("../components.json")) {
        Ok(list) => list,
        Err(e) => {
            // 内嵌表坏掉属构建期缺陷（有单测钉住），运行时只能空表 + 显式失败记录
            crate::log::error(format!(
                "EMBEDDED component table invalid ({e}); core has no components"
            ));
            Vec::new()
        }
    }
}

/// 解析 + 逐条校验。返回 `Err` 仅表示**整体 JSON 不可用**；单条非法走跳过 + 日志。
///
/// **BOM 剥离**：Windows 上用 PowerShell 的 `Set-Content -Encoding UTF8`、记事本或若干编辑器
/// 保存的 JSON 都带 `EF BB BF`。`serde_json` 会直接拒绝带 BOM 的输入 —— 那会让外部覆盖表
/// **静默失效并回退内嵌表**（用户改了文件却看不到任何效果，只在日志里留一行 warn）。
/// `settings.rs` 早有这条容错，本模块原先漏了；两个读取者行为必须一致。
///
/// 放在 `parse` 而不是 `load`：内嵌表走的是 `include_str!`，若 `components.json` 被带 BOM 保存，
/// 同样的解析失败会让 core 变成"一个组件都没有"。在解析入口统一剥，两处都被覆盖。
fn parse(text: &str) -> Result<Vec<Component>, String> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let raw: RawTable = serde_json::from_str(text).map_err(|e| format!("json parse failed: {e}"))?;
    let Some(items) = raw.components else {
        return Err("missing 'components' array".to_string());
    };

    let mut out: Vec<Component> = Vec::with_capacity(items.len());
    for (idx, item) in items.into_iter().enumerate() {
        match validate(item, &out) {
            Ok(c) => out.push(c),
            Err(reason) => crate::log::warn(format!("component #{idx} skipped: {reason}")),
        }
    }
    Ok(out)
}

fn validate(raw: RawComponent, seen: &[Component]) -> Result<Component, String> {
    let name = raw.name.unwrap_or_default().trim().to_string();
    if name.is_empty() {
        return Err("missing 'name'".to_string());
    }
    if seen.iter().any(|c| c.name == name) {
        return Err(format!("duplicate name '{name}'"));
    }
    let exe = raw.exe.unwrap_or_default().trim().to_string();
    if exe.is_empty() {
        return Err(format!("'{name}': missing 'exe'"));
    }
    let desired = {
        let s = raw.desired.as_deref().unwrap_or("on-demand");
        Desired::parse(s.trim())
            .ok_or_else(|| format!("'{name}': unknown desired '{s}' (running|on-demand|stopped)"))?
    };
    let tier = {
        let s = raw.tier.as_deref().unwrap_or("extension");
        Tier::parse(s.trim()).ok_or_else(|| {
            format!(
                "'{name}': unknown tier '{s}' (foundation|infrastructure|surface|extension|system-extension)"
            )
        })?
    };
    let component_type = {
        let s = raw.component_type.as_deref().unwrap_or("process");
        ComponentType::parse(s.trim())
            .ok_or_else(|| format!("'{name}': unknown type '{s}' (process|tool|panel)"))?
    };
    // surface + 常驻 但没显式声明 onResume → 唤醒后"进程活着但窗口已失效"会被静默放过。
    // 不拒绝（配置仍然可用），但必须记警告 —— 静默错默认值正是最难查的那类 bug。
    let resume_explicit = raw
        .power
        .as_ref()
        .and_then(|p| p.on_resume.as_ref())
        .is_some();
    let power = validate_power(&name, raw.power)?;
    if resume_discipline_violation(tier, desired, resume_explicit) {
        crate::log::warn(format!(
            "'{name}': tier=surface + desired=running 但未显式声明 power.onResume（默认 reconcile）—— \
             唤醒后进程活着而窗口可能已失效（DWM 重建 / D3D 设备丢失 / 钩子失效），建议改为 probe-and-reinit"
        ));
    }
    let gate = raw
        .gate
        .map(|g| g.trim().to_string())
        .filter(|g| !g.is_empty());
    let args = raw
        .args
        .map(|a| a.trim().to_string())
        .filter(|a| !a.is_empty());
    let stop_flag = normalize(raw.stop_flag);

    let liveness = {
        let s = raw.liveness.as_deref().unwrap_or("process");
        Liveness::parse(s.trim())
            .ok_or_else(|| format!("'{name}': unknown liveness '{s}' (process|pipe)"))?
    };
    let liveness_pipe = normalize(raw.liveness_pipe);
    // `liveness=pipe` 必须有管道名 —— 否则这条配置**无意义**（探测什么？）。
    // 按容错纪律"单条非法 → 跳过并给出明确原因"，**不静默降级成进程判活**：
    // 静默降级会把"桌面服务永不复活"那个真机事故悄悄带回来，而那正是这个字段存在的理由。
    if liveness == Liveness::Pipe && liveness_pipe.is_none() {
        return Err(format!(
            "'{name}': liveness=pipe requires 'livenessPipe' (which pipe to probe)"
        ));
    }
    // 反向也要拒：写了管道名却声明 process 判活 = 一个**被静默忽略**的字段，
    // 而它的作者显然以为它生效了。
    if liveness != Liveness::Pipe && liveness_pipe.is_some() {
        return Err(format!(
            "'{name}': 'livenessPipe' is set but liveness is not 'pipe' — a silently ignored field is worse than a rejected one"
        ));
    }

    Ok(Component {
        label: raw.label.unwrap_or_else(|| name.clone()),
        name,
        exe,
        desired,
        tier,
        component_type,
        power,
        gate,
        args,
        stop_flag,
        liveness,
        liveness_pipe,
        remove_at: normalize(raw.remove_at),
    })
}

/// 去空白；空串视作"没写"。
fn normalize(value: Option<String>) -> Option<String> {
    value
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty())
}

/// 留痕标记的路径（`%LOCALAPPDATA%\BetterDesktop\<name>`）。
///
/// **标记目录是跨进程契约**：托盘/入口写、core 读。三处（读/写/删）必须从同一个 join 派生 ——
/// 位置对不上就等于标记不存在（本仓踩过"名字只差一个词、停了又被拉回"的坑）。
fn flag_path(name: &str) -> Option<std::path::PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(std::path::PathBuf::from(base).join("BetterDesktop").join(name))
}

/// 留痕标记是否存在。
///
/// `shellmenu.rs` 的 dev / unregistered 标记共用本函数，避免多份 join 各写各的。
pub fn flag_exists(name: &str) -> bool {
    flag_path(name).is_some_and(|p| p.is_file())
}

/// 写留痕标记 —— **"用户显式停止"的落点**（S5-3 的持久语义）。
///
/// # 失败必须如实返回 `Err`
///
/// 写不进去却报"已停止"，等价于把"3 秒后它又被拉回来"变成**静默行为** ——
/// 那正是本仓反复强调的"已停止被静默推翻"。
pub fn write_flag(name: &str) -> Result<(), String> {
    let path = flag_path(name).ok_or_else(|| "LOCALAPPDATA is not set".to_string())?;
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| format!("cannot create {}: {e}", dir.display()))?;
    }
    std::fs::write(&path, b"").map_err(|e| format!("cannot write {}: {e}", path.display()))
}

/// 删留痕标记 —— **"用户显式启动"的落点**（解除 [`write_flag`] 的压制）。
///
/// 标记本来就不在也算成功（幂等）：调用方要的是"它不再被压制"，删完即达成。
pub fn clear_flag(name: &str) -> Result<(), String> {
    let path = flag_path(name).ok_or_else(|| "LOCALAPPDATA is not set".to_string())?;
    match std::fs::remove_file(&path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(format!("cannot remove {}: {e}", path.display())),
    }
}

fn validate_power(name: &str, raw: Option<RawPower>) -> Result<PowerPolicy, String> {
    let Some(raw) = raw else {
        return Ok(PowerPolicy::default());
    };
    let on_suspend = match raw.on_suspend.as_deref() {
        None => SuspendAction::Freeze,
        Some(s) => SuspendAction::parse(s.trim())
            .ok_or_else(|| format!("'{name}': unknown power.onSuspend '{s}' (freeze|stop|notify)"))?,
    };
    let on_resume = match raw.on_resume.as_deref() {
        None => ResumeAction::Reconcile,
        Some(s) => ResumeAction::parse(s.trim()).ok_or_else(|| {
            format!(
                "'{name}': unknown power.onResume '{s}' (reconcile|probe-and-reinit|restart|reinit|notify)"
            )
        })?,
    };
    // keepAwake 的解析本身会拒绝 true/'always'（core 不得无条件阻止睡眠）
    let keep_awake = match raw.keep_awake.as_ref() {
        None => KeepAwake::Never,
        Some(v) => KeepAwake::parse(v).map_err(|e| format!("'{name}': {e}"))?,
    };
    Ok(PowerPolicy {
        on_suspend,
        on_resume,
        keep_awake,
    })
}

fn external_path() -> Option<std::path::PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(
        std::path::PathBuf::from(base)
            .join("BetterDesktop")
            .join("core.components.json"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn one(json: &str) -> Vec<Component> {
        parse(json).expect("table must parse")
    }

    #[test]
    fn embedded_table_is_valid_and_unique() {
        let list = parse(include_str!("../components.json")).expect("embedded table must parse");
        assert!(!list.is_empty(), "embedded table must not be empty");
        let mut names: Vec<&str> = list.iter().map(|c| c.name.as_str()).collect();
        names.sort_unstable();
        let before = names.len();
        names.dedup();
        assert_eq!(before, names.len(), "component names must be unique");
        // 契约：core 不得包含 UI 插件类组件（计划 §6.1-B 禁止清单）
        for c in &list {
            assert!(
                !c.exe.contains("Island") && !c.exe.contains("Dock"),
                "component '{}' looks like a shell UI plugin; core must not own UI",
                c.name
            );
        }
        // 内嵌表不得出现"阻止睡眠"的条目
        for c in &list {
            assert_eq!(c.power.keep_awake, KeepAwake::Never, "{}", c.name);
        }
    }

    #[test]
    fn defaults_are_conservative() {
        let list = one(r#"{"components":[{"name":"x","exe":"x.exe"}]}"#);
        let c = &list[0];
        assert_eq!(c.desired, Desired::OnDemand);
        assert_eq!(c.tier, Tier::Extension);
        assert_eq!(c.component_type, ComponentType::Process);
        assert_eq!(c.power, PowerPolicy::default());
        assert_eq!(c.label, "x");
        assert!(c.gate.is_none());
    }

    #[test]
    fn invalid_entries_are_skipped_not_fatal() {
        let list = one(
            r#"{"components":[
                {"name":"ok","exe":"ok.exe"},
                {"name":"","exe":"bad.exe"},
                {"name":"noexe"},
                {"name":"weird","exe":"w.exe","desired":"sometimes"},
                {"name":"badtier","exe":"w.exe","tier":"cloud"},
                {"name":"badtype","exe":"w.exe","type":"daemon"},
                {"name":"badresume","exe":"w.exe","power":{"onResume":"reboot"}},
                {"name":"ok","exe":"dup.exe"}
            ]}"#,
        );
        assert_eq!(list.len(), 1, "only the first valid entry survives");
        assert_eq!(list[0].name, "ok");
    }

    /// 计划 §14 电源禁区：`keepAwake=always` / `true` 必须被 schema 拒绝，而不是被默默接受。
    #[test]
    fn keep_awake_always_and_true_are_rejected() {
        for body in [
            r#"{"keepAwake":"always"}"#,
            r#"{"keepAwake":true}"#,
            r#"{"keepAwake":"true"}"#,
        ] {
            let json = format!(r#"{{"components":[{{"name":"hog","exe":"h.exe","power":{body}}}]}}"#);
            assert!(
                one(&json).is_empty(),
                "keepAwake {body} must be rejected, not accepted"
            );
        }
    }

    #[test]
    fn keep_awake_accepts_bool_false_and_user_triggered() {
        let list = one(
            r#"{"components":[
                {"name":"a","exe":"a.exe","power":{"keepAwake":false}},
                {"name":"b","exe":"b.exe","power":{"keepAwake":"user-triggered"}}
            ]}"#,
        );
        assert_eq!(list[0].power.keep_awake, KeepAwake::Never, "布尔 false 必须可用");
        assert_eq!(list[1].power.keep_awake, KeepAwake::UserTriggered);
    }

    #[test]
    fn missing_components_array_is_error() {
        assert!(parse(r#"{"nope":[]}"#).is_err());
        assert!(parse("not json").is_err());
    }

    /// 安全（计划 §8.3 / C18）：**深层嵌套 JSON 必须被拒，不得栈溢出**。
    ///
    /// 这条守卫的价值在于**锁住默认行为**：`serde_json` 自带递归深度上限（默认 128），
    /// `parse` **不得**调用 `Deserializer::disable_recursion_limit` —— 一旦有人为了"支持更深的配置"
    /// 把它关掉，本用例立即报红。所以这里不需要自己写深度守卫（那是重复造轮子）。
    #[test]
    fn deeply_nested_json_is_rejected_not_stack_overflow() {
        let depth = 10_000;
        let bomb = format!("{}1{}", "[".repeat(depth), "]".repeat(depth));
        assert!(
            parse(&bomb).is_err(),
            "serde_json default recursion limit must stay enabled"
        );
    }

    /// 带 UTF-8 BOM 的配置必须照常解析。
    ///
    /// 这条不是理论洁癖：真机探针就是这么撞上的 —— PowerShell `Set-Content -Encoding UTF8`
    /// 写出的 `core.components.json` 带 BOM，core 静默回退内嵌表、转去监护了完全不相干的组件。
    /// 与 `settings.rs` 的同类容错必须一致，否则"同一个目录里两个配置，一个能读一个不能读"。
    #[test]
    fn bom_prefixed_json_is_accepted() {
        let plain = r#"{"components":[{"name":"probe","exe":"probe.exe","desired":"running"}]}"#;
        let with_bom = format!("\u{feff}{plain}");

        let a = one(plain);
        let b = one(&with_bom);
        assert_eq!(a.len(), 1);
        assert_eq!(b.len(), 1, "BOM 前缀不得导致整份表被判为非法");
        assert_eq!(a[0].name, b[0].name);
        assert_eq!(b[0].desired, Desired::Running);
    }

    #[test]
    fn gate_and_args_normalized() {
        let list = one(
            r#"{"components":[{"name":"a","exe":"a.exe","gate":"  ","args":"  --open  "}]}"#,
        );
        assert!(list[0].gate.is_none(), "blank gate means no gate");
        assert_eq!(list[0].args.as_deref(), Some("--open"));
    }

    #[test]
    fn power_defaults_per_field() {
        let list = one(
            r#"{"components":[{"name":"a","exe":"a.exe","power":{"onResume":"reinit"}}]}"#,
        );
        assert_eq!(list[0].power.on_resume, ResumeAction::Reinit);
        assert_eq!(list[0].power.on_suspend, SuspendAction::Freeze);
        assert_eq!(list[0].power.keep_awake, KeepAwake::Never);
    }

    /// 三审新增：`probe-and-reinit` 必须可解析，且与 `reinit` 是**不同**取值
    /// （前者先探测健康，后者无脑重建 —— 对 Surface 层用错就是每次唤醒都闪一次）。
    #[test]
    fn probe_and_reinit_is_distinct_from_reinit() {
        let list = one(
            r#"{"components":[
                {"name":"surface","exe":"s.exe","tier":"surface","power":{"onResume":"probe-and-reinit"}},
                {"name":"infra","exe":"i.exe","tier":"infrastructure","power":{"onResume":"reinit"}}
            ]}"#,
        );
        assert_eq!(list[0].power.on_resume, ResumeAction::ProbeAndReinit);
        assert_eq!(list[1].power.on_resume, ResumeAction::Reinit);
        assert_ne!(list[0].power.on_resume, list[1].power.on_resume);
    }

    /// 三审纪律：surface + 常驻 必须显式声明 onResume，否则会被静默放过。
    #[test]
    fn surface_resident_requires_explicit_resume_policy() {
        assert!(resume_discipline_violation(
            Tier::Surface,
            Desired::Running,
            false
        ));
        assert!(!resume_discipline_violation(
            Tier::Surface,
            Desired::Running,
            true
        ));
        // surface 但按需（唤醒时本来就不在跑）→ 不适用
        assert!(!resume_discipline_violation(
            Tier::Surface,
            Desired::OnDemand,
            false
        ));
        assert!(!resume_discipline_violation(
            Tier::Infrastructure,
            Desired::Running,
            false
        ));
    }

    /// 内嵌表里的 surface 常驻条目必须显式写了 onResume（否则上面的警告会在真机上天天刷）。
    #[test]
    fn embedded_table_respects_resume_discipline() {
        for c in parse(include_str!("../components.json")).unwrap() {
            let explicit = c.power.on_resume != ResumeAction::Reconcile
                || c.tier != Tier::Surface
                || c.desired != Desired::Running;
            assert!(
                explicit,
                "component '{}' is surface+resident but leaves onResume at default",
                c.name
            );
        }
    }

    /// D30：tier 必须**真的影响监护分派**，而不只是被存下来。
    #[test]
    fn tier_drives_supervision() {
        let mut c = Component {
            name: "x".into(),
            label: "x".into(),
            exe: "x.exe".into(),
            desired: Desired::Running,
            tier: Tier::Surface,
            component_type: ComponentType::Process,
            power: PowerPolicy::default(),
            gate: Some("components.x".into()),
            args: None,
            stop_flag: None,
            liveness: Liveness::Process,
            liveness_pipe: None,
            remove_at: None,
        };

        // surface + desired=running：gate 开才保证在跑
        assert!(auto_start(&c, true));
        assert!(!auto_start(&c, false));
        // gate 关 → 必须停（用户关掉的东西不被守护复活）
        assert!(must_stop(&c, false));
        assert!(!must_stop(&c, true));

        // infrastructure / extension：即便 desired=running 也不是 tier 说了算，仍看 desired+gate；
        // 但声明 on-demand 时永不主动拉起
        c.tier = Tier::Infrastructure;
        c.desired = Desired::OnDemand;
        assert!(!auto_start(&c, true), "on-demand must never be auto-started");

        // foundation：永不缺席、永不停
        c.tier = Tier::Foundation;
        c.desired = Desired::Stopped;
        assert!(auto_start(&c, false), "foundation must always be ensured");
        assert!(!must_stop(&c, false), "foundation must never be stopped");
    }

    /// `liveness=pipe` 必须带管道名；反过来，写了管道名却不声明 pipe 判活也必须被拒。
    ///
    /// 后半条的理由：一个**被静默忽略**的字段比一个被拒绝的字段更糟 ——
    /// 它的作者显然以为它生效了，而系统会按另一套判据运行。
    #[test]
    fn pipe_liveness_requires_a_pipe_name_and_vice_versa() {
        assert!(
            one(r#"{"components":[{"name":"d","exe":"d.exe","liveness":"pipe"}]}"#).is_empty(),
            "liveness=pipe 而没给管道名 → 这条配置无意义，必须被拒而不是降级成进程判活"
        );
        assert!(
            one(r#"{"components":[{"name":"d","exe":"d.exe","livenessPipe":"X"}]}"#).is_empty(),
            "给了管道名却没声明 pipe 判活 → 字段会被静默忽略"
        );

        let ok = one(
            r#"{"components":[{"name":"d","exe":"d.exe","liveness":"pipe","livenessPipe":"BetterDesktop.DesktopCmd"}]}"#,
        );
        assert_eq!(ok[0].liveness, Liveness::Pipe);
        assert_eq!(
            ok[0].liveness_pipe.as_deref(),
            Some("BetterDesktop.DesktopCmd")
        );
    }

    /// 内嵌表必须**保留**从 Watchdog 迁入的语义 —— 这几条是"迁移时最容易漏、漏了就是行为回归"的东西，
    /// 故用测试钉在表上，而不是只写在注释里。
    #[test]
    fn embedded_table_preserves_migrated_watchdog_semantics() {
        let list = parse(include_str!("../components.json")).unwrap();
        let by = |n: &str| {
            list.iter()
                .find(|c| c.name == n)
                .unwrap_or_else(|| panic!("embedded table is missing '{n}'"))
        };

        // desktop：管道判活（审计 #9 —— 短命"桌面控制菜单"与常驻服务同名）+ 用户显式停止标记
        let desktop = by("desktop");
        assert_eq!(
            desktop.liveness,
            Liveness::Pipe,
            "desktop 必须按管道判活，否则『正在弹菜单』会被读成『服务在跑』"
        );
        assert_eq!(
            desktop.liveness_pipe.as_deref(),
            Some("BetterDesktop.DesktopCmd")
        );
        assert_eq!(
            desktop.stop_flag.as_deref(),
            Some("desktop-stopped.flag"),
            "缺了它 core 会把用户刚停掉的桌面服务立刻复活"
        );

        // Host：显式停止标记 + 临时监护（必须自报到期点，否则过渡期配置会被忘掉）
        assert_eq!(by("shell").stop_flag.as_deref(), Some("host-stopped.flag"));
        assert_eq!(by("shell").remove_at.as_deref(), Some("S4-5"));

        // 面板：gate 是 S4-3 补的（原先缺 ⇒ 用户关掉剪贴板历史后入口仍能把它拉起来）
        assert_eq!(
            by("clipboard-panel").gate.as_deref(),
            Some("extensions.clipboard-history.enabled")
        );
    }

    /// `type=tool` 即便声明 `desired=running` 也不得被自动拉起 —— 否则"跑完即退"的工具
    /// 会变成每轮重启一次的无限循环（监护器无法区分"退出了"与"崩了"）。
    #[test]
    fn tool_is_never_auto_started_even_when_marked_running() {
        let mut c = Component {
            name: "scanner".into(),
            label: "scanner".into(),
            exe: "scanner.exe".into(),
            desired: Desired::Running,
            tier: Tier::Foundation, // 连地基层也不例外 —— 拦截必须早于 tier 判断
            component_type: ComponentType::Tool,
            power: PowerPolicy::default(),
            gate: None,
            args: None,
            stop_flag: None,
            liveness: Liveness::Process,
            liveness_pipe: None,
            remove_at: None,
        };
        assert!(!auto_start(&c, true), "tool must never be auto-started");

        // 显式 `start` 仍然可用（那是用户一次性调用，不是守护）
        c.component_type = ComponentType::Process;
        assert!(auto_start(&c, true), "process 形态不受影响");
    }
}
