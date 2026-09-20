//! 托盘图标 + 弹出菜单。
//!
//! 【红线 C9 · 本项目实证】`TrackPopupMenuEx` 的 owner **必须是桌面关联的顶层窗口**。
//! 用 `HWND_MESSAGE` 消息专用窗口承载时，菜单会**静默不弹**（`TrackPopupMenuEx` 立即返回 0，
//! 无错误码、无异常）——这正是 `NativeMenuPopup` 当初踩过的坑。
//! 因此 core 的窗口是 `WS_POPUP + WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` 的**隐藏顶层窗口**（从不 Show）。
//!
//! 【图标版本】刻意**不调用** `NIM_SETVERSION`（保持 legacy V0 行为）：
//! 此时回调消息的 `wParam` = 图标 id、`lParam` = 鼠标消息（如 `WM_RBUTTONUP`），
//! 语义最简单，不需要解包坐标（坐标走 `GetCursorPos`）。

use windows::Win32::Foundation::{HINSTANCE, HWND, POINT};
use windows::Win32::UI::Shell::{
    NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NOTIFYICONDATAW, Shell_NotifyIconW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyMenu, DestroyWindow, GetCursorPos, HMENU, IDI_APPLICATION,
    IMAGE_ICON, LR_DEFAULTSIZE, LR_LOADFROMFILE, LoadIconW, LoadImageW, MF_CHECKED, MF_POPUP,
    MF_SEPARATOR, MF_STRING, PostMessageW, RegisterWindowMessageW, SetForegroundWindow,
    TPM_RETURNCMD, TPM_RIGHTBUTTON, TrackPopupMenuEx, WM_NULL,
};
use windows::core::{PCWSTR, w};

use crate::components::Component;

/// 托盘回调消息（自定义区间起点）。
pub const WM_TRAYICON: u32 = windows::Win32::UI::WindowsAndMessaging::WM_APP + 1;

/// 菜单命令 id：退出。
pub const CMD_QUIT: usize = 1;
/// 菜单命令 id 基址：`CMD_START_BASE + index` = 启动组件表第 index 条。
pub const CMD_START_BASE: usize = 1000;
/// 菜单命令 id 基址：`CMD_STOP_BASE + index` = 停止组件表第 index 条（S5-3）。
pub const CMD_STOP_BASE: usize = 1200;
/// 菜单命令 id 基址：`CMD_RESTART_BASE + index` = 重启组件表第 index 条（S5-3）。
pub const CMD_RESTART_BASE: usize = 1400;

/// `TaskbarCreated` —— explorer（重新）启动时向所有顶层窗口广播的注册消息。
///
/// 托盘图标归 explorer 的**任务栏**所有：explorer 一重启，我们注册的图标就消失了，
/// 而 core 自己完全看不出异常（`Shell_NotifyIconW` 不会失败、也不会回报）。
/// 唯一可靠的恢复手段就是监听这条消息后**重新 `NIM_ADD`** —— 这是 shell 程序的标准做法。
static TASKBAR_CREATED: std::sync::OnceLock<u32> = std::sync::OnceLock::new();

/// 注册 `TaskbarCreated`（core 启动时调一次）。失败只会损失"explorer 重启后自动恢复图标"，
/// 不影响 core 自身，故记为 warn 而非 error。
pub fn register_taskbar_created() {
    let msg = unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) };
    if msg == 0 {
        crate::log::warn(
            "RegisterWindowMessageW(\"TaskbarCreated\") failed; the tray icon will NOT come back \
             automatically if explorer restarts",
        );
        return;
    }
    let _ = TASKBAR_CREATED.set(msg);
    crate::log::info(format!(
        "registered TaskbarCreated message ({msg}) — tray icon recovers after an explorer restart"
    ));
}

/// 这条消息是不是 `TaskbarCreated`。
///
/// 用 `!TASKBAR_CREATED.is_empty()` 之外还要比 `msg != 0`：注册失败时消息 id 为 0，
/// 而 0 恰好可能出现在别处，不能把"没有 id"误判成"就是它"。
pub fn is_taskbar_created(msg: u32) -> bool {
    msg != 0 && TASKBAR_CREATED.get() == Some(&msg)
}

/// 托盘图标（RAII：Drop 时移除图标 + 释放自载入的图标句柄）。
pub struct TrayIcon {
    hwnd: HWND,
    icon: windows::Win32::UI::WindowsAndMessaging::HICON,
    /// 图标句柄是否由我们载入（`LoadImageW`）而非共享（`LoadIconW`）——只有前者需要 `DestroyIcon`。
    owns_icon: bool,
    /// 提示文本（`heal` 重注册时需要原样再传一次）。
    tip: String,
    added: bool,
}

impl TrayIcon {
    /// 添加托盘图标。图标来源：`<exeDir>\BetterDesktop.ico` → `%LOCALAPPDATA%\BetterDesktop\BetterDesktop.ico`
    /// → 系统默认应用图标（保证**永远有图标**，绝不因为缺资源而"托盘看不见"）。
    pub fn add(hwnd: HWND, tip: &str) -> Self {
        let (icon, owns_icon) = load_icon();
        let mut this = Self {
            hwnd,
            icon,
            owns_icon,
            tip: tip.to_string(),
            added: false,
        };
        if this.register() {
            crate::log::info(format!("tray icon added ({tip})"));
        } else {
            // 失败不得正常化：托盘图标没有 = 用户找不到入口
            crate::log::error("Shell_NotifyIconW(NIM_ADD) failed; tray icon is NOT visible");
        }
        this
    }

    /// 重新注册图标（explorer 重启 / 唤醒后调用）。**幂等**：先删后加，代价约 1ms。
    ///
    /// 为什么先 `NIM_DELETE`：同名同 id 的图标再次 `NIM_ADD` 在部分 shell 版本上会返回失败，
    /// 于是"重加"变成静默无效 —— 那正是本节要修的症状。
    pub fn heal(&mut self, why: &str) {
        self.remove();
        if self.register() {
            crate::log::info(format!("tray icon re-registered ({why})"));
        } else {
            crate::log::error(format!(
                "tray icon could NOT be re-registered ({why}); the user has no tray entry"
            ));
        }
    }

    /// 发一次 `NIM_ADD`（成功即置 `added`）。
    fn register(&mut self) -> bool {
        let mut data = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.hwnd,
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage: WM_TRAYICON,
            hIcon: self.icon,
            ..Default::default()
        };
        copy_wide(&mut data.szTip, &self.tip);
        self.added = unsafe { Shell_NotifyIconW(NIM_ADD, &data).as_bool() };
        self.added
    }

    /// 移除图标（退出路径；幂等）。
    pub fn remove(&mut self) {
        if !self.added {
            return;
        }
        let data = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.hwnd,
            uID: 1,
            ..Default::default()
        };
        let _ = unsafe { Shell_NotifyIconW(NIM_DELETE, &data) };
        self.added = false;
        crate::log::info("tray icon removed");
    }
}

impl Drop for TrayIcon {
    fn drop(&mut self) {
        self.remove();
        // 图标句柄由 `LoadImageW` 分配 → 归我们所有，必须配对销毁（否则每次启动泄漏一个 GDI 句柄）。
        // `LoadIconW` 返回的是共享系统图标，不可销毁 —— 用 `is_invalid` 无法区分，故只在"我们自己载入成功"
        // 时销毁：`load_icon` 的回退分支返回共享图标，此时 `owns_icon` 为 false。
        if self.owns_icon && !self.icon.is_invalid() {
            let _ = unsafe { windows::Win32::UI::WindowsAndMessaging::DestroyIcon(self.icon) };
        }
    }
}

/// 功能开关项：显示名 / settings 键 / 默认值（S5-2b）。
///
/// **与 C# `tray/TrayApplicationContext.ToggleSpec` 一一对应**：同一批 11 项、同样的键与默认值。
/// 搬运这类表最经典的失败是**漏一项** —— 不报错、不崩溃，只是那个开关从菜单里消失，
/// 故用单测钉住"条数 + 键集合"（见本文件 tests）。
pub struct ToggleSpec {
    label: &'static str,
    key: &'static str,
    default: bool,
}

/// 11 项功能开关（顺序与 C# 托盘一致）。
const TOGGLES: &[ToggleSpec] = &[
    ToggleSpec { label: "自绘桌面", key: "components.desktop", default: true },
    ToggleSpec { label: "隐藏桌面图标", key: "desktop.iconsHidden", default: false },
    ToggleSpec { label: "顶部菜单栏", key: "components.menubar", default: true },
    ToggleSpec { label: "底部 Dock", key: "components.dock", default: true },
    ToggleSpec { label: "任务栏外观", key: "components.wintaskbar", default: true },
    ToggleSpec {
        label: "索引引擎（停止后续按需拉起）",
        key: "extensions.index.enabled",
        default: true,
    },
    ToggleSpec {
        label: "截图工具（需常驻服务在运行）",
        key: "extensions.screenshot.enabled",
        default: true,
    },
    ToggleSpec {
        label: "剪贴板历史",
        key: "extensions.clipboard-history.enabled",
        default: true,
    },
    ToggleSpec { label: "灵动岛", key: "island.enabled", default: true },
    ToggleSpec {
        label: "双击隐藏桌面图标",
        key: "desktop.doubleClickHideIcons",
        default: true,
    },
    ToggleSpec { label: "热键侧板", key: "hotkeys-panel.enabled", default: true },
];

/// 菜单命令 id 基址：`CMD_TOGGLE_BASE + index` = 翻转功能开关表第 index 项。
///
/// 与 [`CMD_START_BASE`] **(1000)** 必须留出区间：id 区间判据若不设上界，
/// 开关项会被"启动组件"分支接走（那条按组件数判界，于是开关被静默丢弃）。
pub const CMD_TOGGLE_BASE: usize = 2000;

/// 「暂停组件监护」勾选项（S5-4）：写 / 删 `user-pause.flag`。
///
/// 与**更新器**的 `watchdog-pause.flag` 是**两个独立标记**（见 `supervisor::USER_PAUSE_FLAG`）：
/// 共用会让先结束的那一方把另一方仍在生效的暂停清掉（用户以为还暂停着，组件已被拉起）。
pub const CMD_PAUSE_TOGGLE: usize = 3000;

/// 「系统集成」子菜单基址（4 项：查看状态 / 注册 / 修复 / 注销）—— **全部经 CLI verb 落地**。
pub const CMD_SYSINT_BASE: usize = 3100;

/// 「更新」子菜单基址（2 项：检查更新 / 下载并安装）—— 同样经 CLI verb。
pub const CMD_UPDATE_BASE: usize = 3200;

/// 应急恢复（经 CLI 派发 `--recovery`）。
pub const CMD_RECOVERY: usize = 3300;

/// 导出诊断包（经 CLI 派发 `--diagnostics-export`）。
pub const CMD_EXPORT_DIAG: usize = 3301;

/// 打开日志目录（core 自己 `ShellExecute`：一次性系统动作，不涉及组件生命周期）。
pub const CMD_OPEN_LOGS: usize = 3302;

/// 开机自启（core 自己写 `HKCU\...\Run`，与计划任务同处）。
pub const CMD_TOGGLE_AUTOSTART: usize = 3303;

/// 关于。
pub const CMD_ABOUT: usize = 3304;

/// 托盘菜单的选中结果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MenuAction {
    /// 启动组件表第 index 条（**并解除该组件的停止标记**）。
    Start(usize),
    /// 停止组件表第 index 条（**持久**：写该组件的停止标记；无标记的组件会被拒绝并弹气泡）。
    Stop(usize),
    /// 重启组件表第 index 条（`stop` + `start`，**不写标记** —— 它是"重来一次"，不是"永久关掉"）。
    Restart(usize),
    /// 翻转功能开关表第 index 项。
    Toggle(usize),
    /// 暂停 / 恢复**组件监护**（写 / 删 `user-pause.flag`）。
    TogglePause,
    /// 系统集成（**经 CLI** `--system-integration <verb>`）。
    SystemIntegration(SysIntegrationAction),
    /// 更新（**经 CLI** `--update <verb>`）。
    Update(UpdateAction),
    /// 应急恢复（**经 CLI** `--recovery`）。
    Recovery,
    /// 导出诊断包（**经 CLI** `--diagnostics-export`）。
    ExportDiagnostics,
    /// 打开日志目录（core 自己 `ShellExecute`）。
    OpenLogs,
    /// 开机自启（core 自己写 `HKCU\...\Run`）。
    ToggleAutoStart,
    /// 关于。
    About,
    /// 退出 core。
    Quit,
}

/// 系统集成动作 —— 一项 = 一条 CLI 窄命令（**不在这里实现任何细节**）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SysIntegrationAction {
    Status,
    Register,
    Repair,
    Unregister,
}

impl SysIntegrationAction {
    /// CLI 窄命令的 verb（`--system-integration <它>`）。
    ///
    /// 必须与 `BetterDesktop.Cli/Program.cs` 的 `SystemIntegration` 分支逐字一致 ——
    /// 对不上就是"点了菜单没反应"，而那种失败不会在任何地方报错。
    pub fn cli_verb(self) -> &'static str {
        match self {
            SysIntegrationAction::Status => "status",
            SysIntegrationAction::Register => "register",
            SysIntegrationAction::Repair => "repair",
            SysIntegrationAction::Unregister => "unregister",
        }
    }
}

/// 更新动作（**经 CLI** 派发给更新器）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UpdateAction {
    Check,
    Install,
}

impl UpdateAction {
    /// CLI 窄命令参数（`--update <它>`）。
    pub fn cli_verb(self) -> &'static str {
        match self {
            UpdateAction::Check => "check",
            UpdateAction::Install => "install",
        }
    }
}

/// 翻转第 index 项开关并落盘（**core 自己就是写者**，不需要 IPC —— S5-2a 的直接回报）。
///
/// 返回值是**翻转后的新值**（供调用方日志/后续动作；落盘失败返回 `Err`）。
pub fn apply_toggle(index: usize) -> Result<bool, String> {
    let Some(t) = TOGGLES.get(index) else {
        return Err(format!("toggle index {index} is out of range"));
    };
    let current = crate::settings::Settings::load().get_bool(t.key, t.default);
    let next = !current;
    crate::settings::set_flat(t.key, &serde_json::Value::Bool(next))?;
    crate::log::info(format!(
        "tray: toggled '{}' ({}) -> {}",
        t.label, t.key, next
    ));
    Ok(next)
}

/// 组件子项动作（**纯模型** —— 单测直接断言它，不碰 Win32）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChildAction {
    Start,
    Stop,
    Restart,
}

/// 一个组件在菜单里的模型（父项文案 + 勾选 + 子项）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentEntry {
    /// 组件在表里的下标（命令 id 由它与 [`ChildAction`] 共同决定）。
    pub index: usize,
    /// 父项文案（含 `!` 标记与状态后缀）。
    pub label: String,
    /// 父项是否显示**原生勾选**（`MF_CHECKED`）。
    pub checked: bool,
    /// 子项（顺序即显示顺序）。
    pub children: Vec<(ChildAction, String)>,
}

/// 子项的动作 → 命令 id（纯函数，可单测）。
fn child_cmd_id(index: usize, action: ChildAction) -> usize {
    match action {
        ChildAction::Start => CMD_START_BASE + index,
        ChildAction::Stop => CMD_STOP_BASE + index,
        ChildAction::Restart => CMD_RESTART_BASE + index,
    }
}

/// 组件的菜单模型 —— **纯函数**：三类分组规则都在这里，单测不必真弹菜单。
///
/// # 三类分组（S5-3 定案）
///
/// | 类别 | 判据 | 子项 |
/// |---|---|---|
/// | ① 有停止标记 | 声明了 `stopFlag`（shell / desktop） | 「停止（**直到手动启动**）」+「重启」 —— 这个停止是**持久**的 |
/// | ② 受监护但无标记 | `desired=running` 且无 `stopFlag`（clipboard-engine） | 不给"停止"，只给**指向设置开关**的说明项（点了弹气泡解释） |
/// | ③ 按需 | `desired=on-demand`（panel / settings / index / capture） | **只有「启动」** |
///
/// # 为什么 ②③ 不给"停止"
///
/// 那会**撒谎**：② 停了会被监护在 3 秒内拉回（"已停止"被静默推翻），③ 本来就没在跑。
/// 真正的"停止"只有两种：① 的持久标记，或 ② 的**设置开关**（用户可持久关闭的唯一真相源）。
///
/// # 停止态时只留「启动」
///
/// 对一个已经停着的组件说"停止/重启"没有意义 —— 这也让"启动"成为唯一需要解释的动作：
/// 对 ① 它是"解除停止"，所以文案要写成能自解释的样子（见 `enter_children` 注释）。
pub fn component_entries(
    components: &[Component],
    states: &std::collections::HashMap<String, String>,
    settings: &crate::settings::Settings,
) -> Vec<ComponentEntry> {
    let mut out = Vec::new();
    for (index, c) in components.iter().enumerate() {
        // 地基（core 自身级别）不进菜单：它永不停，也不该给用户一个"停止自己"的按钮。
        if matches!(c.tier, crate::components::Tier::Foundation) {
            continue;
        }
        let state = states.get(&c.name).map(String::as_str).unwrap_or("stopped");
        let gate_open = c
            .gate
            .as_deref()
            .map(|k| settings.get_bool(k, true))
            .unwrap_or(true);

        // 父项：**原生勾选表达"在跑"，文本标记只表达"用户需要知道的异常"**
        //（`!` 没有原生等价物 —— 这是文本唯一正当的用武之地）。
        let label = match state {
            "running" => c.label.clone(),
            "starting" => format!("{}（启动中…）", c.label),
            "retrying" => format!("! {}（重试中）", c.label),
            "degraded" => format!("! {}（已熔断）", c.label),
            _ if !gate_open => format!("{}（已由设置关闭）", c.label),
            _ => c.label.clone(),
        };
        // 勾选 = 在跑或正在起（"启动中"共用勾选 + 后缀：既不撒谎，也不留空）
        let checked = matches!(state, "running" | "starting");

        // 「打开」而不是「启动」（S5-3 真机缺口）：面板是**单实例**，`--open` 的语义是"显示 / 切换" ——
        // 说「启动」会让人以为点一次起一个，而实际点第二次是**切回来**。
        // 只对 `type=panel` 改文案：`tool`（截图）确实就是"现在跑一次"，`process` 是"拉起来"。
        let start_label = if matches!(c.component_type, crate::components::ComponentType::Panel) {
            "打开"
        } else {
            "启动"
        };

        let children = if state == "stopped" {
            vec![(ChildAction::Start, start_label.to_string())]
        } else if c.stop_flag.is_some() {
            vec![
                // 「直到手动启动」必须写进文案：这是**持久**停止，不是"临时停一下"。
                // 少了它，用户会以为过一会儿它自己会回来。
                (ChildAction::Stop, "停止（直到手动启动）".to_string()),
                // 重启是**独立路径**（stop + start，**不写标记**）：否则"重启"就变成"永久关掉再打开"。
                (ChildAction::Restart, "重启".to_string()),
            ]
        } else if matches!(c.desired, crate::components::Desired::Running) {
            vec![(
                ChildAction::Stop,
                format!(
                    "停止（由设置开关管辖：{}）",
                    c.gate.as_deref().unwrap_or("无开关")
                ),
            )]
        } else {
            // 按需件只有一项动作：对工具（capture）= "现在跑一次"，对面板 = "打开"（见 start_label）。
            vec![(ChildAction::Start, start_label.to_string())]
        };

        out.push(ComponentEntry {
            index,
            label,
            checked,
            children,
        });
    }
    out
}

/// 弹一条托盘气泡（`NIM_MODIFY` + `NIF_INFO`，一次性）。
///
/// # 为什么必须有它
///
/// 菜单点击**没有别的反馈通道**：拒绝如果不说话，用户看到的就是"点了没反应" ——
/// 而那正是本仓库用测试钉过的那类失败（"加了菜单项却忘了接线"）。气泡是系统原生、
/// 不打断、也不需要额外窗口的反馈形式。
///
/// 失败只记 warn：气泡丢了只损失"解释"，不影响动作本身。
pub fn notify(icon_hwnd: HWND, title: &str, body: &str) {
    let mut data = NOTIFYICONDATAW {
        cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: icon_hwnd,
        uID: 1,
        uFlags: NIF_INFO,
        ..Default::default()
    };
    copy_wide(&mut data.szInfoTitle, title);
    copy_wide(&mut data.szInfo, body);
    // 信息级（不是错误）：这不是故障，而是"这条路走不通，请走那条"。
    data.dwInfoFlags = NIIF_INFO;
    if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data).as_bool() } {
        crate::log::warn(format!("tray balloon could not be shown: {title} / {body}"));
    }
}

/// 弹出托盘菜单并返回用户选择（`None` = 点空白取消）。
///
/// 顺序红线（缺一不可）：
///   1. `SetForegroundWindow` —— 否则菜单不会因点击别处而消失；
///   2. `TrackPopupMenuEx(TPM_RETURNCMD)` —— 返回值即命令 id，不走 `WM_COMMAND`；
///   3. 关闭后 `PostMessage(WM_NULL)` —— MSDN 记载的必要收尾。
///
/// 勾选态**以真实值为准**：开关读 `settings.json`、组件运行态读监护器 `snapshot()`
/// —— 每次打开菜单重新读，不缓存、不猜（"菜单打开时刷新"）。
///
/// # 组件用**子菜单**（S5-3）
///
/// 父项带勾选/`!` 标记 ⇒ **不开子菜单也看得见状态**；子项才是动作（启动 / 停止 / 重启）。
/// 这样条目数不随组件数翻倍，而"它现在怎么样"与"我能对它做什么"各占一层。
///
/// # 动作归属（S5-4，2026-09-19 定案）
///
/// 菜单项按"**同类动作归同类所有者**"分四档：
///
/// | 档 | 例子 | 落地方式 |
/// |---|---|---|
/// | 组件生命周期 | 启动 / 停止 / 重启 | 交给 `supervisor`（**唯一**生命周期所有者） |
/// | 设置 | 11 项开关 | core **自己写** `settings.json`（它就是设置的单写者） |
/// | 自身持久化 | 开机自启 | core **自己写** `HKCU\...\Run`（与计划任务同处，见 `autostart.rs`） |
/// | 业务细节 | 系统集成 / 更新 / 恢复 / 诊断包 | core **只派发** CLI 窄命令（见 `cli.rs`） |
/// | 一次性系统动作 | 打开日志目录 / 关于 | core 直接做（不涉及任何组件生命周期） |
///
/// 这一层没有"core 自己实现注册表写入 / 更新 / 打包"的分支：那些细节只在 C# 侧一份实现里。
///
/// `user_paused` 由调用方读出（`user-pause.flag` 是否存在）——
/// **不用 supervisor 的暂停判据**：那个把"更新器暂停"也算进去，会让用户在菜单里看到一个
/// 自己从没点过的勾（更新是后台行为）。
pub fn show_menu(
    hwnd: HWND,
    components: &[Component],
    settings: &crate::settings::Settings,
    user_paused: bool,
) -> Option<MenuAction> {
    // 状态来自监护器（它就是"谁在跑"这件事的所有者）。监护器缺席时退化为空表 →
    // 菜单仍可用（至少能点"启动"），只是勾选态不反映现实 —— 比"菜单弹不出来"好。
    let states = match crate::supervisor() {
        Some(sup) => sup.snapshot(settings).state,
        None => {
            crate::log::warn("tray menu: supervisor missing — component states will read as stopped");
            std::collections::HashMap::new()
        }
    };
    let entries = component_entries(components, &states, settings);

    unsafe {
        let menu: HMENU = CreatePopupMenu().ok()?;
        // ① 组件（组件表驱动，每个组件一个子菜单）
        for e in &entries {
            let Ok(sub) = CreatePopupMenu() else {
                // 建不出子菜单：**跳过这一项并记日志**，而不是塞一个点了没用的父项
                crate::log::warn(format!("tray menu: cannot create submenu for '{}'", e.label));
                continue;
            };
            for (action, text) in &e.children {
                let wide = to_wide(text);
                let _ = AppendMenuW(
                    sub,
                    MF_STRING,
                    child_cmd_id(e.index, *action),
                    PCWSTR(wide.as_ptr()),
                );
            }
            let wide = to_wide(&e.label);
            // `MF_POPUP`：`uIDNewItem` 传子菜单句柄（不是命令 id —— 父项本身不产生选择）。
            // 子菜单句柄随父菜单一起销毁，不需要单独 DestroyMenu。
            let flags = if e.checked {
                MF_POPUP | MF_CHECKED
            } else {
                MF_POPUP
            };
            let _ = AppendMenuW(menu, flags, sub.0 as usize, PCWSTR(wide.as_ptr()));
        }
        // ② 功能开关（固定表驱动）。勾选态用**原生复选框**（`MF_CHECKED`）——
        //    与系统菜单一致、随主题、屏幕阅读器会报 "checked"，也是 C# 托盘（`Checked = true`）的语义。
        //
        // 【为什么不省这一处导入】初版图省事用了文本前缀 `[x]`/`[ ]`，理由是"少一处导入改动"——
        // 那是个**弱理由**：它只是一个 flag 位、不引入任何依赖，换来的是用户可见的呈现一致。
        // shell 产品的菜单就该长成系统菜单的样子。
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        for (idx, t) in TOGGLES.iter().enumerate() {
            let on = settings.get_bool(t.key, t.default);
            let flags = if on { MF_STRING | MF_CHECKED } else { MF_STRING };
            let wide = to_wide(t.label);
            let _ = AppendMenuW(menu, flags, CMD_TOGGLE_BASE + idx, PCWSTR(wide.as_ptr()));
        }

        // ③ 暂停组件监护：勾选态取 **user-pause.flag** 的存在性（不含更新器那个标记，见函数文档）。
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let pause = to_wide("暂停组件监护（组件继续运行，不再自动拉起）");
        let pause_flags = if user_paused {
            MF_STRING | MF_CHECKED
        } else {
            MF_STRING
        };
        let _ = AppendMenuW(menu, pause_flags, CMD_PAUSE_TOGGLE, PCWSTR(pause.as_ptr()));

        // ④ 系统集成 ▸ —— 经 CLI。core 不写 `HKCU\Software\Classes`（见 shellmenu.rs 模块头）。
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        append_submenu(
            menu,
            "系统集成（注册到系统）",
            &[
                "查看注册状态…",
                "注册到系统（右键扩展 + 开机自启）",
                "修复注册（按状态补缺）",
                "注销系统右键扩展（保留程序与自启）",
            ],
            CMD_SYSINT_BASE,
        );

        // ⑤ 更新 ▸ —— 经 CLI 派发给更新器。
        append_submenu(
            menu,
            "更新",
            &["检查更新…", "下载并安装更新…"],
            CMD_UPDATE_BASE,
        );

        // ⑥ 一次性动作：应急恢复 / 诊断包（经 CLI）与打开日志目录（core 直接）
        for (id, label) in [
            (CMD_RECOVERY, "应急恢复（显示任务栏 / 清理残留）…"),
            (CMD_EXPORT_DIAG, "导出诊断包（收集日志给开发者）…"),
            (CMD_OPEN_LOGS, "打开日志目录"),
        ] {
            let wide = to_wide(label);
            let _ = AppendMenuW(menu, MF_STRING, id, PCWSTR(wide.as_ptr()));
        }

        // ⑦ 开机自启：core **自己**写 `HKCU\...\Run`（与计划任务同处：都持久化 core 的启动路径）。
        let autostart = to_wide("开机自启（随系统启动）");
        let autostart_flags = if crate::autostart::is_enabled() {
            MF_STRING | MF_CHECKED
        } else {
            MF_STRING
        };
        let _ = AppendMenuW(
            menu,
            autostart_flags,
            CMD_TOGGLE_AUTOSTART,
            PCWSTR(autostart.as_ptr()),
        );

        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let about = to_wide("关于 BetterDesktop Core");
        let _ = AppendMenuW(menu, MF_STRING, CMD_ABOUT, PCWSTR(about.as_ptr()));

        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let quit = to_wide("退出 BetterDesktop Core");
        let _ = AppendMenuW(menu, MF_STRING, CMD_QUIT, PCWSTR(quit.as_ptr()));

        let mut pt = POINT::default();
        if GetCursorPos(&mut pt).is_err() {
            let _ = DestroyMenu(menu);
            crate::log::warn("GetCursorPos failed; tray menu not shown");
            return None;
        }

        let _ = SetForegroundWindow(hwnd);
        let cmd = TrackPopupMenuEx(
            menu,
            (TPM_RETURNCMD | TPM_RIGHTBUTTON).0,
            pt.x,
            pt.y,
            hwnd,
            None,
        );
        let _ = PostMessageW(
            hwnd,
            WM_NULL,
            windows::Win32::Foundation::WPARAM(0),
            windows::Win32::Foundation::LPARAM(0),
        );
        let _ = DestroyMenu(menu);

        // TPM_RETURNCMD 语义：返回值即命令 id（0 = 取消）。注意 windows-rs 把它声明成 BOOL，
        // 但 BOOL 就是 32 位 int —— 直接读 .0 得到命令 id，不可当"真假"用。
        action_for_id(cmd.0 as usize, components.len())
    }
}

/// 追加一个子菜单（固定项表驱动）。
///
/// 子菜单建不出来时**记 warn 并跳过父项** —— 塞一个点了没用的父项比没有它更糟
/// （用户会以为"功能坏了"，而真相只是这一条没建出来）。
fn append_submenu(menu: HMENU, head: &str, items: &[&str], id_base: usize) {
    unsafe {
        let Ok(sub) = CreatePopupMenu() else {
            crate::log::warn(format!("tray menu: cannot create the '{head}' submenu"));
            return;
        };
        for (offset, label) in items.iter().enumerate() {
            let wide = to_wide(label);
            let _ = AppendMenuW(sub, MF_STRING, id_base + offset, PCWSTR(wide.as_ptr()));
        }
        let wide = to_wide(head);
        // `MF_POPUP`：`uIDNewItem` 传子菜单句柄（父项本身不产生选择），随父菜单一起销毁。
        let _ = AppendMenuW(menu, MF_POPUP, sub.0 as usize, PCWSTR(wide.as_ptr()));
    }
}

/// 命令 id → 动作（**纯函数**，故可单测）。
///
/// 【为什么单独抽出来】写 S5-2b 时我加了开关菜单项与 [`MenuAction::Toggle`]，**却漏改这里的映射** ——
/// 点开关会落进"启动组件"分支、按组件数判界后**静默丢弃**（用户看到"点了没反应"）。
/// 编译器用 `variant Toggle is never constructed` 抓住了它；抽成纯函数后，**同一类漏可以被单测抓住**。
fn action_for_id(id: usize, component_count: usize) -> Option<MenuAction> {
    if id == 0 {
        return None; // 点空白取消
    }
    if id == CMD_QUIT {
        return Some(MenuAction::Quit);
    }
    // 启动区间**必须设上界**：否则开关 id（2000+）会落进这里，被按组件数判界后静默丢弃。
    if (CMD_START_BASE..CMD_STOP_BASE).contains(&id) {
        let idx = id - CMD_START_BASE;
        if idx < component_count {
            return Some(MenuAction::Start(idx));
        }
        crate::log::warn(format!("tray menu returned out-of-range component id {id}"));
        return None;
    }
    // 停止 / 重启同理，各自独立判界 —— 三个区间**不能共用一条判界**
    //（共用会让"停止第 3 条"被读成"启动第 3 条"这类错位，且没有任何编译期信号）。
    if (CMD_STOP_BASE..CMD_RESTART_BASE).contains(&id) {
        let idx = id - CMD_STOP_BASE;
        if idx < component_count {
            return Some(MenuAction::Stop(idx));
        }
        crate::log::warn(format!("tray menu returned out-of-range stop id {id}"));
        return None;
    }
    if (CMD_RESTART_BASE..CMD_TOGGLE_BASE).contains(&id) {
        let idx = id - CMD_RESTART_BASE;
        if idx < component_count {
            return Some(MenuAction::Restart(idx));
        }
        crate::log::warn(format!("tray menu returned out-of-range restart id {id}"));
        return None;
    }
    // 开关区间**必须设上界**：S5-2b 那次"漏改映射 ⇒ 点开关没反应"就是这个坑，
    // 而 S5-4 又往 3000+ 加了新项 —— 无上界的话它们会落进这里、被按开关数判界后**静默丢弃**。
    if (CMD_TOGGLE_BASE..CMD_PAUSE_TOGGLE).contains(&id) {
        let idx = id - CMD_TOGGLE_BASE;
        if idx < TOGGLES.len() {
            return Some(MenuAction::Toggle(idx));
        }
        crate::log::warn(format!("tray menu returned out-of-range toggle id {id}"));
        return None;
    }

    // ── S5-4：暂停监护 / 系统集成 / 更新 / 一次性动作 ──
    if id == CMD_PAUSE_TOGGLE {
        return Some(MenuAction::TogglePause);
    }
    if (CMD_SYSINT_BASE..CMD_UPDATE_BASE).contains(&id) {
        return match id - CMD_SYSINT_BASE {
            0 => Some(MenuAction::SystemIntegration(SysIntegrationAction::Status)),
            1 => Some(MenuAction::SystemIntegration(SysIntegrationAction::Register)),
            2 => Some(MenuAction::SystemIntegration(SysIntegrationAction::Repair)),
            3 => Some(MenuAction::SystemIntegration(SysIntegrationAction::Unregister)),
            _ => {
                crate::log::warn(format!(
                    "tray menu returned out-of-range system-integration id {id}"
                ));
                None
            }
        };
    }
    if (CMD_UPDATE_BASE..CMD_RECOVERY).contains(&id) {
        return match id - CMD_UPDATE_BASE {
            0 => Some(MenuAction::Update(UpdateAction::Check)),
            1 => Some(MenuAction::Update(UpdateAction::Install)),
            _ => {
                crate::log::warn(format!("tray menu returned out-of-range update id {id}"));
                None
            }
        };
    }
    if id == CMD_RECOVERY {
        return Some(MenuAction::Recovery);
    }
    if id == CMD_EXPORT_DIAG {
        return Some(MenuAction::ExportDiagnostics);
    }
    if id == CMD_OPEN_LOGS {
        return Some(MenuAction::OpenLogs);
    }
    if id == CMD_TOGGLE_AUTOSTART {
        return Some(MenuAction::ToggleAutoStart);
    }
    if id == CMD_ABOUT {
        return Some(MenuAction::About);
    }

    crate::log::warn(format!("tray menu returned unknown command id {id}"));
    None
}

/// 退出时销毁隐藏窗口（触发 `WM_DESTROY` → 消息循环退出）。
pub fn request_quit(hwnd: HWND) {
    unsafe {
        let _ = DestroyWindow(hwnd);
    }
}

/// 图标解析：exe 同目录 → `%LOCALAPPDATA%\BetterDesktop` → 系统默认。
/// 返回 `(句柄, 是否归我们所有)` —— 只有 `LoadImageW` 载入的才需要（且必须）`DestroyIcon`。
fn load_icon() -> (windows::Win32::UI::WindowsAndMessaging::HICON, bool) {
    for path in icon_candidates() {
        let wide = to_wide(&path.to_string_lossy());
        let loaded = unsafe {
            LoadImageW(
                HINSTANCE::default(),
                PCWSTR(wide.as_ptr()),
                IMAGE_ICON,
                0,
                0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE,
            )
        };
        if let Ok(h) = loaded
            && !h.is_invalid()
        {
            crate::log::info(format!("tray icon loaded from {}", path.display()));
            return (windows::Win32::UI::WindowsAndMessaging::HICON(h.0), true);
        }
    }
    crate::log::warn("no BetterDesktop.ico found; falling back to system default icon");
    let shared = unsafe { LoadIconW(HINSTANCE::default(), IDI_APPLICATION).unwrap_or_default() };
    (shared, false)
}

fn icon_candidates() -> Vec<std::path::PathBuf> {
    let mut out = Vec::new();
    if let Ok(exe) = std::env::current_exe()
        && let Some(dir) = exe.parent()
    {
        out.push(dir.join("BetterDesktop.ico"));
    }
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        out.push(
            std::path::PathBuf::from(local)
                .join("BetterDesktop")
                .join("BetterDesktop.ico"),
        );
    }
    out
}

/// `&str` → NUL 结尾的 UTF-16。
pub fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 把 `&str` 拷进定长 UTF-16 缓冲（截断而不是溢出）。
fn copy_wide(dst: &mut [u16], s: &str) {
    let wide: Vec<u16> = s.encode_utf16().take(dst.len().saturating_sub(1)).collect();
    dst[..wide.len()].copy_from_slice(&wide);
    for slot in dst.iter_mut().skip(wide.len()) {
        *slot = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::components::{ComponentType, Desired, Tier};

    #[test]
    fn copy_wide_nul_terminates_and_truncates() {
        let mut buf = [0xFFFFu16; 8];
        copy_wide(&mut buf, "abc");
        assert_eq!(&buf[..4], &[b'a' as u16, b'b' as u16, b'c' as u16, 0]);

        let mut small = [0xFFFFu16; 3];
        copy_wide(&mut small, "abcdef");
        assert_eq!(small[2], 0, "must be NUL terminated even when truncated");
        assert_eq!(small[0], b'a' as u16);
    }

    #[test]
    fn to_wide_is_nul_terminated() {
        assert_eq!(to_wide("ab"), vec![97, 98, 0]);
        assert_eq!(to_wide(""), vec![0]);
    }

    /// 开关表**就是契约**：条数与键集合必须与 C# 托盘一致。
    ///
    /// 搬运这类表最经典的失败是**漏一项** —— 不报错、不崩溃，只是那个开关从菜单里消失，
    /// 而"少了一个开关"几乎不会被当成 bug 报上来。
    #[test]
    fn toggle_table_matches_the_csharp_tray() {
        assert_eq!(TOGGLES.len(), 11, "C# 托盘是 11 项");

        let keys: Vec<&str> = TOGGLES.iter().map(|t| t.key).collect();
        for expected in [
            "components.desktop",
            "desktop.iconsHidden",
            "components.menubar",
            "components.dock",
            "components.wintaskbar",
            "extensions.index.enabled",
            "extensions.screenshot.enabled",
            "extensions.clipboard-history.enabled",
            "island.enabled",
            "desktop.doubleClickHideIcons",
            "hotkeys-panel.enabled",
        ] {
            assert!(keys.contains(&expected), "缺少开关 '{expected}'（搬运漏项）");
        }

        // 键必须唯一：重复键会让两个菜单项改同一个设置（用户看到"点了另一个也变了"）
        let mut sorted = keys.clone();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), keys.len(), "开关键必须唯一");
    }

    /// id 区间不得重叠 —— 否则开关项会被"启动组件"分支接走（那条按组件数判界 ⇒ 开关被静默丢弃）。
    #[test]
    fn menu_id_ranges_do_not_overlap() {
        assert!(
            CMD_START_BASE + 64 < CMD_STOP_BASE,
            "启动区间（1000..）必须与停止区间（1200..）留出余量"
        );
        assert!(
            CMD_STOP_BASE + 64 < CMD_RESTART_BASE,
            "停止区间必须与重启区间留出余量"
        );
        assert!(
            CMD_RESTART_BASE + 64 < CMD_TOGGLE_BASE,
            "重启区间必须与开关区间（2000..）留出余量"
        );
        // S5-4：开关区间与 3000+ 的固定/子菜单 id 之间也必须留出余量 ——
        // 挨在一起时，将来多加一个开关就会撞进"暂停监护"的 id。
        assert!(
            CMD_TOGGLE_BASE + 200 < CMD_PAUSE_TOGGLE,
            "开关区间必须与 S5-4 的固定 id（3000..）留出余量"
        );
        assert!(CMD_PAUSE_TOGGLE < CMD_SYSINT_BASE);
        assert!(
            CMD_SYSINT_BASE + 16 < CMD_UPDATE_BASE,
            "系统集成 4 项必须与更新区间留出余量"
        );
        assert!(
            CMD_UPDATE_BASE + 16 < CMD_RECOVERY,
            "更新 2 项必须与一次性动作（3300..）留出余量"
        );

        // 固定 id 必须两两不同（复制粘贴最容易撞，而且撞了不会报错、只会"点 A 弹出 B"）
        let fixed = [
            CMD_PAUSE_TOGGLE,
            CMD_RECOVERY,
            CMD_EXPORT_DIAG,
            CMD_OPEN_LOGS,
            CMD_TOGGLE_AUTOSTART,
            CMD_ABOUT,
            CMD_QUIT,
        ];
        let mut sorted = fixed.to_vec();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), fixed.len(), "固定命令 id 不得重复");
    }

    // ───────────────── S5-3：组件子菜单的模型（纯函数，不弹菜单） ─────────────────

    fn menu_comp(
        name: &str,
        label: &str,
        desired: Desired,
        tier: Tier,
        kind: ComponentType,
        gate: Option<&str>,
        stop_flag: Option<&str>,
    ) -> Component {
        Component {
            name: name.into(),
            label: label.into(),
            exe: format!("{name}.exe"),
            desired,
            tier,
            component_type: kind,
            power: crate::components::PowerPolicy::default(),
            gate: gate.map(str::to_string),
            args: None,
            stop_flag: stop_flag.map(str::to_string),
            liveness: crate::components::Liveness::Process,
            liveness_pipe: None,
            remove_at: None,
        }
    }

    /// **三类分组规则**（S5-3 定案的核心）：直接断言菜单模型，不碰 Win32。
    #[test]
    fn component_menu_follows_the_three_category_rules() {
        let comps = vec![
            menu_comp(
                "shell",
                "主程序",
                Desired::Running,
                Tier::Surface,
                ComponentType::Process,
                None,
                Some("host-stopped.flag"),
            ),
            menu_comp(
                "engine",
                "剪贴板引擎",
                Desired::Running,
                Tier::Infrastructure,
                ComponentType::Process,
                Some("extensions.clipboard-history.enabled"),
                None,
            ),
            menu_comp(
                "panel",
                "剪贴板面板",
                Desired::OnDemand,
                Tier::Extension,
                ComponentType::Panel,
                Some("extensions.clipboard-history.enabled"),
                None,
            ),
            menu_comp(
                "capture",
                "截图",
                Desired::OnDemand,
                Tier::Extension,
                ComponentType::Tool,
                None,
                None,
            ),
            menu_comp(
                "coreish",
                "地基",
                Desired::Running,
                Tier::Foundation,
                ComponentType::Process,
                None,
                None,
            ),
        ];
        let states = std::collections::HashMap::from([
            ("shell".to_string(), "running".to_string()),
            ("engine".to_string(), "running".to_string()),
            ("panel".to_string(), "running".to_string()),
            ("capture".to_string(), "stopped".to_string()),
            ("coreish".to_string(), "running".to_string()),
        ]);
        let entries = component_entries(&comps, &states, &crate::settings::Settings::empty());

        assert_eq!(entries.len(), 4, "地基（Foundation）不得进菜单");
        assert!(!entries.iter().any(|e| e.label == "地基"));

        let find = |label: &str| entries.iter().find(|e| e.label == label).unwrap();

        // ① 有停止标记：停止是**持久**的 ⇒ 文案必须写明，且提供独立的重启
        let shell = find("主程序");
        assert!(shell.checked, "running ⇒ 原生勾选");
        let acts: Vec<ChildAction> = shell.children.iter().map(|(a, _)| *a).collect();
        assert_eq!(acts, vec![ChildAction::Stop, ChildAction::Restart]);
        assert!(
            shell.children[0].1.contains("直到手动启动"),
            "持久语义必须写进文案（否则用户以为是临时停止）：{:?}",
            shell.children[0].1
        );

        // ② 受监护但无标记：不给"停止"，只给**指向设置开关**的说明项
        let engine = find("剪贴板引擎");
        assert_eq!(engine.children.len(), 1);
        assert_eq!(engine.children[0].0, ChildAction::Stop);
        assert!(
            engine.children[0].1.contains("设置开关管辖"),
            "必须点明归属：{:?}",
            engine.children[0].1
        );
        assert!(
            engine.children[0].1.contains("extensions.clipboard-history.enabled"),
            "必须给出具体键名，否则用户不知道去哪儿关"
        );

        // ③ 按需（含正在跑的面板）：只有"启动"
        for e in [find("剪贴板面板"), find("截图")] {
            assert_eq!(e.children.len(), 1, "{}", e.label);
            assert_eq!(e.children[0].0, ChildAction::Start, "{}", e.label);
        }
    }

    /// 停止态时只留「启动」—— 对已经停着的东西说"停止/重启"没有意义。
    #[test]
    fn stopped_component_offers_only_start() {
        let comps = vec![menu_comp(
            "shell",
            "主程序",
            Desired::Running,
            Tier::Surface,
            ComponentType::Process,
            None,
            Some("host-stopped.flag"),
        )];
        let states = std::collections::HashMap::from([("shell".to_string(), "stopped".to_string())]);
        let e = &component_entries(&comps, &states, &crate::settings::Settings::empty())[0];
        assert!(!e.checked);
        assert_eq!(e.children, vec![(ChildAction::Start, "启动".to_string())]);
    }

    /// 面板的动作文案是「打开」而非「启动」—— 单实例**唤起**，不是新起一个（S5-3 真机缺口）。
    /// 同时钉住**不外溢**：工具仍是「启动」（它确实就是"现在跑一次"）。
    #[test]
    fn panel_action_says_open_while_tool_says_start() {
        let comps = vec![
            menu_comp(
                "panel",
                "剪贴板面板",
                Desired::OnDemand,
                Tier::Extension,
                ComponentType::Panel,
                None,
                None,
            ),
            menu_comp(
                "capture",
                "截图",
                Desired::OnDemand,
                Tier::Extension,
                ComponentType::Tool,
                None,
                None,
            ),
        ];
        let states = std::collections::HashMap::from([
            ("panel".to_string(), "running".to_string()),
            ("capture".to_string(), "stopped".to_string()),
        ]);
        let entries = component_entries(&comps, &states, &crate::settings::Settings::empty());
        let panel = entries.iter().find(|e| e.label == "剪贴板面板").unwrap();
        let capture = entries.iter().find(|e| e.label == "截图").unwrap();

        assert_eq!(
            panel.children,
            vec![(ChildAction::Start, "打开".to_string())],
            "面板在跑时也必须给出『打开』这个动作 —— 否则用户唤不出已收起的面板"
        );
        assert_eq!(
            capture.children,
            vec![(ChildAction::Start, "启动".to_string())],
            "工具不是唤起，文案仍是『启动』"
        );
    }

    /// 状态 → 父项呈现：**在跑用原生勾选，异常才用 `!` 文本**（S5-3 定案）。
    #[test]
    fn component_labels_render_state_on_the_parent_item() {
        let comps = vec![menu_comp(
            "engine",
            "剪贴板引擎",
            Desired::Running,
            Tier::Infrastructure,
            ComponentType::Process,
            Some("k"),
            None,
        )];
        let mut states = std::collections::HashMap::new();
        for (state, want_checked, want_mark, want_suffix) in [
            ("running", true, false, ""),
            ("starting", true, false, "启动中"),
            ("retrying", false, true, "重试中"),
            ("degraded", false, true, "已熔断"),
        ] {
            states.insert("engine".to_string(), state.to_string());
            let e = &component_entries(&comps, &states, &crate::settings::Settings::empty())[0];
            assert_eq!(e.checked, want_checked, "{state}: 勾选态");
            assert_eq!(e.label.starts_with('!'), want_mark, "{state}: {:?}", e.label);
            if !want_suffix.is_empty() {
                assert!(e.label.contains(want_suffix), "{state}: {:?}", e.label);
            }
        }
    }

    /// gate 关闭的组件必须**点明**"已由设置关闭" —— 否则用户看到没勾选、又不知道原因。
    #[test]
    fn gate_closed_component_says_so_on_the_parent_item() {
        let comps = vec![menu_comp(
            "panel",
            "剪贴板面板",
            Desired::OnDemand,
            Tier::Extension,
            ComponentType::Panel,
            Some("extensions.clipboard-history.enabled"),
            None,
        )];
        let states = std::collections::HashMap::from([("panel".to_string(), "stopped".to_string())]);
        let shut = crate::settings::Settings::from_str(
            r#"{"extensions":{"clipboard-history":{"enabled":false}}}"#,
        );
        let e = &component_entries(&comps, &states, &shut)[0];
        assert!(e.label.contains("已由设置关闭"), "{:?}", e.label);
    }

    /// **S5-2b 的回归钉子**：id → 动作的映射必须覆盖每一个菜单项。
    ///
    /// 这条测的正是"加了菜单项却忘了接线"那类漏 —— 它没有编译错误、没有运行时报错，
    /// 只是**点了没反应**（本次实测：`Toggle` 变体一度从未被构造，靠编译器 dead-code 警告才发现）。
    #[test]
    fn every_menu_id_maps_to_an_action() {
        assert!(matches!(action_for_id(CMD_QUIT, 7), Some(MenuAction::Quit)));
        assert!(matches!(
            action_for_id(CMD_START_BASE + 3, 7),
            Some(MenuAction::Start(3))
        ));
        assert!(
            matches!(action_for_id(CMD_STOP_BASE + 3, 7), Some(MenuAction::Stop(3))),
            "停止区间必须映射到 Stop —— 错位会让『停止』变成『启动』，且没有编译期信号"
        );
        assert!(matches!(
            action_for_id(CMD_RESTART_BASE + 3, 7),
            Some(MenuAction::Restart(3))
        ));

        for i in 0..TOGGLES.len() {
            assert!(
                matches!(action_for_id(CMD_TOGGLE_BASE + i, 7), Some(MenuAction::Toggle(j)) if j == i),
                "开关 #{i} 必须映射到 Toggle({i})"
            );
        }

        assert!(action_for_id(0, 7).is_none(), "0 = 取消");
        assert!(
            action_for_id(CMD_START_BASE + 9, 7).is_none(),
            "越界组件 id 必须是 None"
        );
        assert!(
            action_for_id(CMD_TOGGLE_BASE + 99, 7).is_none(),
            "越界开关 id 必须是 None"
        );

        // ── S5-4：每一个新菜单项都必须有接线（"加了菜单项却忘了改映射"= 点了没反应） ──
        assert!(matches!(
            action_for_id(CMD_PAUSE_TOGGLE, 7),
            Some(MenuAction::TogglePause)
        ));
        assert!(matches!(
            action_for_id(CMD_SYSINT_BASE, 7),
            Some(MenuAction::SystemIntegration(SysIntegrationAction::Status))
        ));
        assert!(
            matches!(
                action_for_id(CMD_SYSINT_BASE + 3, 7),
                Some(MenuAction::SystemIntegration(SysIntegrationAction::Unregister))
            ),
            "注销那一项最容易漏接线（它是最后一个子项）"
        );
        assert!(matches!(
            action_for_id(CMD_UPDATE_BASE + 1, 7),
            Some(MenuAction::Update(UpdateAction::Install))
        ));
        for (id, label) in [
            (CMD_RECOVERY, "应急恢复"),
            (CMD_EXPORT_DIAG, "导出诊断包"),
            (CMD_OPEN_LOGS, "打开日志目录"),
            (CMD_TOGGLE_AUTOSTART, "开机自启"),
            (CMD_ABOUT, "关于"),
        ] {
            assert!(
                action_for_id(id, 7).is_some(),
                "{label}（id={id}）必须映射到动作"
            );
        }

        // 子菜单越界 id 一律 None（不得溢出到下一个区间）
        assert!(action_for_id(CMD_SYSINT_BASE + 9, 7).is_none());
        assert!(action_for_id(CMD_UPDATE_BASE + 9, 7).is_none());
    }

    /// **开关区间的上界是承重墙**。
    ///
    /// S5-2b 加开关项时曾漏改映射（点开关落进"启动组件"分支被静默丢弃），
    /// S5-4 又往 3000+ 加了新项 —— 若开关判断仍是 `id >= CMD_TOGGLE_BASE`（无上界），
    /// 它们会被"按 11 项判界"整个吞掉，表现为**点了没反应**且没有任何报错。
    #[test]
    fn toggle_range_has_an_upper_bound_so_new_items_are_not_swallowed() {
        let resolved = action_for_id(CMD_PAUSE_TOGGLE, 7);
        assert_eq!(resolved, Some(MenuAction::TogglePause));
        assert!(
            !matches!(resolved, Some(MenuAction::Toggle(_))),
            "暂停项被当成开关 ⇒ 会被按开关数判界后静默丢弃"
        );

        // 开关区间**内部**的越界 id 仍必须是 None —— 上界收紧后不得反向溢出
        assert!(action_for_id(CMD_TOGGLE_BASE + TOGGLES.len(), 7).is_none());
        assert!(action_for_id(CMD_PAUSE_TOGGLE - 1, 7).is_none());
    }
}
