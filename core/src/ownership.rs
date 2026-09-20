//! 系统级持久引用的**写入权限**：一个 exe 凭什么能把自己写进"每 5 分钟"或"每次开机"都会执行的地方。
//!
//! # 为什么两条引用必须共用同一条判据
//!
//! 消费者有两个，它们是同一类东西：
//!
//! - `task.rs`：计划任务 `BetterDesktop Core Ensure`（core 崩溃后的兜底拉起）；
//! - `autostart.rs`：`HKCU\...\Run` 的 `BetterDesktop.Core` 值（每次开机拉起 core）。
//!
//! 两者都持久化"core 的启动路径"，且都是**单例**（一个任务名、一个值名）。
//! 两套判据必然漂移，而漂移的表现是"引用指着一个已经不存在的 exe，**且没有任何地方会报这条错**"
//! —— 所以判据只有这一份。
//!
//! # 为什么是三态，而不是"稳定 / 不稳定"两态
//!
//! 2026-09-20 真机事故（记录见 `.agents/notes/implemented/bug-fix/2026-09-20-task-ownership.md`）：
//! 旧的单一判据把 `%LOCALAPPDATA%\BetterDesktop` 之下**任何**目录都算"稳定位置"，
//! 而真机上产品目录里本来就并存着多个构建目录（`app\<旧构建>` 与 `app\<新构建>`）。
//! 于是在"任务指向了别的 exe"那一条分支上，`ensure()` 是**无条件夺权**（重写任务指向自己）：
//! 谁最后启动谁拿走系统级任务，另一份下次启动再抢回去。
//! 用户侧的症状是"一个**测试副本**被计划任务反复复活"，而且只能靠手工删任务才停得下来。
//!
//! 三态把"能读"与"能写"分开：[`Ownership::Tenant`] **可以**享用既有的兜底
//! （已存在的引用对它是"存在即可用"，原封不动），但**永远不能**创建、改写或删除它。
//! 于是引用总是收敛到唯一合法的写者 —— `deployment.json` 记录的那一份部署。

use std::path::Path;

use crate::shellmenu::{PRODUCT_FOLDER, is_under};

/// 本进程写入系统级持久引用的权限等级。
///
/// 三态而不是布尔：布尔回答不了"这份 core 能不能**用**既有的兜底"，
/// 而"能读"与"能写"被混为一谈正是本次事故的根因（见模块头）。
///
/// # 资源与生命周期
/// 纯数据（`Copy`）：不持有句柄、不涉及释放、可跨线程传递。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Ownership {
    /// 唯一的写者：本进程**就是**这份部署 —— 可以创建 / 改写 / 删除引用。
    Owner,
    /// 同一台机器上的**另一份** core（测试副本 / 旧版本残留 / 并行装的两份）：
    /// 既有的引用对它"存在即可用"，但它一律不写。
    Tenant,
    /// 位置会在 build / clean 中消失（dev bin、`target/`、打包中间目录）：
    /// 一条系统级引用都不该指向它。
    Rejected,
}

/// 判定 `exe` 的权限等级。
///
/// # 做什么
/// 把"这个 exe 有没有资格持有系统级持久引用"收敛成一次纯计算。
///
/// # 为什么
/// 判据必须只有一份（计划任务与开机自启共用，见模块头）。抽成纯函数是为了让**每个分支都能被
/// 单测钉住**：这条规则防的失败（引用指向一个已消失的 exe）恰恰属于"没有任何地方会报错"的那类，
/// 只能靠单测提前钉住，而不是等它到用户机器上发作。
///
/// # 契约
/// - `exe`：待判定的可执行体绝对路径（生产调用方传 `std::env::current_exe()` 的结果）。
/// - `install_root`：`deployment.json` 记录的安装根；未用安装器装过时为 `None`。
/// - `local_appdata`：`%LOCALAPPDATA%` 的值；环境变量缺失时为 `None`。
/// - 返回三态之一。**不做 IO、不读环境变量**：三个输入全部由调用方注入。
///
/// # 边界与失败路径
/// - `exe` 没有可用的父目录（裸文件名）⇒ `Rejected`（拼不出可比较的目录）。
/// - 两个入参都是 `None` ⇒ 恒 `Rejected`（无从证明位置稳定）。
/// - `install_root` 为 `None` ⇒ 产品数据目录是唯一锚点，其下的 core 即 `Owner`
///   （"未用安装器装过"是合法形态，不能因为缺少指针就剥夺它的兜底）。
/// - `local_appdata` 为 `None` 但安装根包含 `exe` ⇒ `Owner`（安装根是独立成立的判据）。
/// - 该函数不 panic：全部输入都可能是 `None`，每个分支都有显式结论。
///
/// # 调用方约束
/// 无（纯函数、无副作用）。判定结果怎么用见两个消费者：`task.rs::ensure`、
/// `autostart.rs::require_owner`；"为什么不能写"的措辞见 [`refusal_reason`]。
pub(crate) fn of(
    exe: &Path,
    install_root: Option<&Path>,
    local_appdata: Option<&Path>,
) -> Ownership {
    let Some(dir) = exe.parent() else {
        return Ownership::Rejected;
    };

    // ① 位置守卫（`Rejected` 的唯一来源）：系统级引用要么落在安装根，要么落在产品数据目录。
    //    落在别处（dev bin / target / dist）时，那个目录一次 clean 就不再存在，引用会每 5 分钟
    //    失败一次 —— 而那时 core 已经不在了，没有任何地方会报这条错。
    let in_product = local_appdata
        .map(|base| base.join(PRODUCT_FOLDER))
        .is_some_and(|product| is_under(dir, &product));
    let in_install_root = install_root.is_some_and(|root| is_under(dir, root));

    if !in_product && !in_install_root {
        return Ownership::Rejected;
    }

    // ② 写权（`Owner` 的唯一来源）：**有部署指针时，只有指针指向的那一份是主人**。
    //    产品目录里并存的其他构建（测试副本、旧版本残留）因此只拿到 Tenant 而不是 Owner ——
    //    这一条就是"测试副本不能再夺走系统级任务"的实现。
    if install_root.is_some() && !in_install_root {
        return Ownership::Tenant;
    }
    Ownership::Owner
}

/// 非 `Owner` 时给出"为什么本进程不该写这条引用"的说明；`Owner` 返回 `None`。
///
/// # 做什么
/// 把"不能写"这件事的**措辞**也收进同一处 —— 两个消费者的日志/气泡都要说这句话。
///
/// # 为什么
/// 措辞分家等于判据分家：同一件事在任务日志里说一套、在托盘气泡里说另一套，
/// 排查的人就得先猜哪一句才是真的。这里只写事实（谁不是主人、主人是谁），
/// 具体"是哪条引用"由调用方在自己的上下文里补（`scheduled task '...'` / 开机自启）。
///
/// # 契约
/// - `ownership`：由 [`of`] 得出的判定。
/// - `exe` / `install_root`：仅用于把两条路径写进说明里。
/// - 返回 `None` ⇒ 本进程是 `Owner`，可以写；`Some(说明)` ⇒ 不许写，说明可直接进日志。
///   调用方据此**先判后查**：拿到 `Some` 时连 `schtasks` / 注册表都不必读。
///
/// # 边界与失败路径
/// 纯格式化，不涉及 IO；没有可失败的操作。`install_root` 为 `None` 时说明里写"未用安装器装过"，
/// 不写 `None` 这种给机器看的字面量。
///
/// # 可复现证据
/// 真机上触发的原文（`core-*.log`，`Tenant` 分支）：
/// `scheduled task 'BetterDesktop Core Ensure': NOT registered — …a copy on this machine…`。
pub(crate) fn refusal_reason(
    ownership: Ownership,
    exe: &Path,
    install_root: Option<&Path>,
) -> Option<String> {
    let deployment = install_root.map_or_else(
        || "no install root is recorded (not installed by the installer)".to_string(),
        |root| format!("the deployment lives at {}", root.display()),
    );

    match ownership {
        Ownership::Owner => None,
        // 措辞刻意点明"这不是失败，是权限结论"：用户看到 WARN 时应该知道该去哪儿找主人。
        Ownership::Tenant => Some(format!(
            "this core lives at {} — it is a copy on this machine, not the deployment; {deployment}. \
             A copy may use an existing reference but never creates or rewrites one, so the \
             reference stays with the deployment (point the deployment at this build instead of \
             running a stray copy)",
            exe.display()
        )),
        Ownership::Rejected => Some(format!(
            "this core lives at {} which is neither under the install root ({}) nor under \
             %LOCALAPPDATA%\\BetterDesktop — refusing to point a system-wide reference at a \
             directory that a build/clean can delete",
            exe.display(),
            install_root.map_or_else(
                || "not installed".to_string(),
                |root| root.display().to_string()
            )
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    const LOCAL: &str = r"C:\Users\X\AppData\Local";

    /// 产品数据目录下的某个版本目录（真机形态：`…\BetterDesktop\app\2026.09.20.1350`）。
    fn version_dir(version: &str) -> PathBuf {
        Path::new(LOCAL)
            .join(PRODUCT_FOLDER)
            .join("app")
            .join(version)
    }

    /// 某个版本目录里的 core（真机的可执行体名是小写连字符的 crate 名）。
    fn core_in(version: &str) -> PathBuf {
        version_dir(version).join("betterdesktop-core.exe")
    }

    fn local() -> Option<&'static Path> {
        Some(Path::new(LOCAL))
    }

    // ───────────────────── 正常路径：谁是主人 ─────────────────────

    /// 指针指向的那一份就是主人 —— 这是唯一允许写系统级引用的一方。
    #[test]
    fn the_core_inside_the_recorded_install_root_is_the_owner() {
        let root = version_dir("2026.09.20.1350");
        assert_eq!(
            of(&core_in("2026.09.20.1350"), Some(&root), local()),
            Ownership::Owner
        );
    }

    /// 安装根**等于** exe 所在目录也算包含（`is_under` 的边界：相等即命中）。
    #[test]
    fn the_install_root_may_be_the_directory_itself() {
        let root = version_dir("2026.09.20.1350");
        let exe = root.join("betterdesktop-core.exe");
        assert_eq!(of(&exe, Some(&root), local()), Ownership::Owner);
    }

    /// 大小写与分隔符不影响归属 —— 否则同一份 core 会在两次启动间"变成另一份"，
    /// 于是它每次启动都重写一遍任务（本仓同族教训：路径等价规则必须只有一份）。
    #[test]
    fn ownership_ignores_case_and_path_separators() {
        let root = version_dir("2026.09.20.1350");
        let exe = PathBuf::from(
            r"c:/users/x/appdata/local/betterdesktop/APP/2026.09.20.1350/BETTERDESKTOP-CORE.EXE",
        );
        assert_eq!(of(&exe, Some(&root), local()), Ownership::Owner);
    }

    /// 安装根不在产品数据目录里（如装到 `Program Files`）时它仍然成立，
    /// 且 `%LOCALAPPDATA%` 缺失也不影响 —— 两条判据是**并列**的，不是"必须先有数据目录"。
    #[test]
    fn the_install_root_stands_on_its_own() {
        let root = Path::new(r"C:\Program Files\BetterDesktop");
        let exe = root.join("betterdesktop-core.exe");
        assert_eq!(of(&exe, Some(root), None), Ownership::Owner);
        assert_eq!(of(&exe, Some(root), local()), Ownership::Owner);
    }

    /// 没有部署指针时，产品数据目录是唯一锚点 —— "未用安装器装过"是合法形态，
    /// 不能因为缺指针就剥夺它的崩溃兜底。
    #[test]
    fn the_product_folder_is_the_anchor_when_no_deployment_is_recorded() {
        let exe = Path::new(LOCAL)
            .join(PRODUCT_FOLDER)
            .join("Standalone")
            .join("betterdesktop-core.exe");
        assert_eq!(of(&exe, None, local()), Ownership::Owner);
    }

    // ─────────────── 回归钉子：同一台机器上的第二份只能是租客 ───────────────

    /// **本次事故的钉子。** 产品目录里并存的另一个构建（测试副本 / 旧版本残留）
    /// 只能拿到 `Tenant` —— 它不得创建、改写、删除系统级引用。
    ///
    /// 真机形态：`deployment.json` 指 `…\app\2026.09.20.1350`，而磁盘上还有一份
    /// `…\app\2026.09.20.1500`（更新的测试构建）。旧代码给它 `Owner`，于是它启动 2 秒后
    /// 把计划任务改写成指向自己 ⇒ 每次杀掉它都被任务在 5 分钟内复活。
    #[test]
    fn a_second_copy_on_the_same_machine_is_only_a_tenant() {
        let root = version_dir("2026.09.20.1350");
        assert_eq!(
            of(&core_in("2026.09.20.1500"), Some(&root), local()),
            Ownership::Tenant,
            "产品目录里的另一份构建不是部署，不得持有系统级引用"
        );
    }

    /// 部署装在 `Program Files` 时，产品目录里的任何 core 同样是租客 ——
    /// "落在产品目录"本身**不**构成主人身份，只有"落在指针指向的目录"才构成。
    #[test]
    fn a_copy_in_the_product_folder_is_a_tenant_even_if_the_install_lives_elsewhere() {
        let root = Path::new(r"C:\Program Files\BetterDesktop");
        assert_eq!(
            of(&core_in("2026.09.20.1500"), Some(root), local()),
            Ownership::Tenant
        );
    }

    // ───────────────────── 拒绝：位置会在 clean 后消失 ─────────────────────

    /// 三类"该拒绝"的位置。把开发目录写进系统级引用是纯负债：一次 clean 之后
    /// 引用每 5 分钟失败一次，而那时没有任何地方能报这条错。
    #[test]
    fn build_and_packaging_directories_are_rejected() {
        let root = version_dir("2026.09.20.1350");

        for exe in [
            r"C:\dev\better-desktop\BetterDesktop.Cli\bin\Debug\net8.0-windows\betterdesktop-core.exe",
            r"C:\dev\better-desktop\core\target\release\betterdesktop-core.exe",
            r"C:\dev\better-desktop\dist\modules\X\BetterDesktop\betterdesktop-core.exe",
        ] {
            assert_eq!(
                of(Path::new(exe), Some(&root), local()),
                Ownership::Rejected,
                "{exe} 会被 build/clean 删掉，不该持有系统级引用"
            );
        }
    }

    /// 防"字符串前缀"误判：`BetterDesktopTrap` 不是 `BetterDesktop` 的子目录。
    #[test]
    fn a_sibling_whose_name_merely_starts_with_the_product_name_is_rejected() {
        let exe = Path::new(LOCAL)
            .join("BetterDesktopTrap")
            .join("bin")
            .join("betterdesktop-core.exe");
        assert_eq!(of(&exe, None, local()), Ownership::Rejected);
    }

    /// 两个锚点都缺失 ⇒ 无从证明位置稳定，一律拒绝（宁可没有兜底，也不留一条注定失败的引用）。
    #[test]
    fn nothing_anchorable_means_rejected() {
        assert_eq!(
            of(
                Path::new(r"C:\somewhere\else\betterdesktop-core.exe"),
                None,
                None
            ),
            Ownership::Rejected
        );
    }

    /// 裸文件名（没有父目录）不得让判定 panic，也不得被当成"等价于任何目录"。
    #[test]
    fn a_bare_file_name_without_a_parent_is_rejected() {
        assert_eq!(
            of(Path::new("betterdesktop-core.exe"), None, local()),
            Ownership::Rejected
        );
    }

    // ───────────────────── 说明措辞：只对主人放行 ─────────────────────

    /// `Owner` 是**唯一**不产生拒绝理由的等级 —— 这条断言就是"谁能写"的完整定义。
    #[test]
    fn only_the_owner_gets_no_refusal() {
        let root = version_dir("2026.09.20.1350");
        let exe = core_in("2026.09.20.1350");

        assert_eq!(refusal_reason(Ownership::Owner, &exe, Some(&root)), None);
        assert!(refusal_reason(Ownership::Tenant, &exe, Some(&root)).is_some());
        assert!(refusal_reason(Ownership::Rejected, &exe, Some(&root)).is_some());
    }

    /// 拒绝理由必须**两条路径都写清**（本进程在哪、部署在哪）：只说"不允许"的日志
    /// 会让人无从下手，而这条规则本来就少见，排查的人需要一次看懂。
    #[test]
    fn refusal_reasons_name_both_paths() {
        let root = version_dir("2026.09.20.1350");
        let copy = core_in("2026.09.20.1500");

        let tenant = refusal_reason(Ownership::Tenant, &copy, Some(&root)).unwrap();
        assert!(tenant.contains("2026.09.20.1500"), "{tenant}");
        assert!(tenant.contains("2026.09.20.1350"), "{tenant}");

        let rejected = refusal_reason(Ownership::Rejected, &copy, Some(&root)).unwrap();
        assert!(rejected.contains("2026.09.20.1500"), "{rejected}");
        assert!(rejected.contains("2026.09.20.1350"), "{rejected}");
    }

    /// 没有部署指针时不许把 `None` 这种机器字面量漏进给人看的说明里
    /// （`Option` 的 `Debug` 输出会让人以为程序坏了）。
    #[test]
    fn a_missing_install_root_is_explained_in_words() {
        let exe = core_in("2026.09.20.1500");
        let text = refusal_reason(Ownership::Rejected, &exe, None).unwrap();
        assert!(text.contains("not installed"), "{text}");
        assert!(!text.contains("None"), "{text}");
    }
}
