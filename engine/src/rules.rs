//! 【P1-5 按应用清洗规则 · 2026-09-13】用户自设的「某应用总复制垃圾」治理规则。
//!
//! 动机（TieZ 对标 §2.2 #1）：我们此前只有硬编码隐私黑名单，无法应对"某应用总是复制垃圾"。
//! 本模块让用户自己设：① `ignore` 忽略整个应用 ② `drop` 正则命中即丢弃 ③ `replace` 正则替换后入库。
//!
//! **设计约束（红线）**：
//!   - 正则**编译失败只跳过该条规则并记日志** —— 绝不 panic，也绝不静默丢弃用户整份规则；
//!   - 规则为空 = **零行为变化**（默认）；
//!   - `ignore` 判定可在**读剪贴板之前**完成（`decide_ignore`），省一次 IO；
//!   - 匹配口径与隐私黑名单一致：进程名 / 窗口标题的**小写子串**（用户不必记精确进程名）。

use regex::Regex;

use crate::capture::Snapshot;
use crate::settings::{AppRule, AppRuleAction};

#[derive(Debug)]
struct CompiledRule {
    /// 小写应用匹配串（空 = 匹配所有应用）。
    app: String,
    action: AppRuleAction,
    regex: Option<Regex>,
    replacement: String,
}

/// 编译后的规则集合（启动与 `apply_settings` 时构建；运行期只读）。
#[derive(Debug, Default)]
pub struct CompiledRules {
    rules: Vec<CompiledRule>,
    /// 编译输入的指纹（规则 JSON 串），供 `apply_settings` 判断是否需要重编译。
    pub source_fingerprint: String,
}

fn fingerprint(rules: &[AppRule]) -> String {
    serde_json::to_string(rules).unwrap_or_default()
}

/// 编译规则表：逐条构造正则，非法规则**跳过并记日志**（不影响其余规则）。
pub fn compile(rules: &[AppRule]) -> CompiledRules {
    let mut out: Vec<CompiledRule> = Vec::new();
    for (i, r) in rules.iter().enumerate() {
        let regex = match r.action {
            AppRuleAction::Ignore => None,
            _ => {
                if r.pattern.trim().is_empty() {
                    crate::log::warn(format!("app-rule #{i}（{:?}）缺少正则，已跳过", r.action));
                    continue;
                }
                match Regex::new(&r.pattern) {
                    Ok(re) => Some(re),
                    Err(e) => {
                        crate::log::warn(format!("app-rule #{i} 正则非法（{e}），已跳过"));
                        continue;
                    }
                }
            }
        };
        out.push(CompiledRule {
            app: r.app.trim().to_lowercase(),
            action: r.action,
            regex,
            replacement: r.replacement.clone(),
        });
    }
    if !out.is_empty() {
        crate::log::info(format!("app-rule: 已编译 {} 条清洗规则", out.len()));
    }
    CompiledRules {
        rules: out,
        source_fingerprint: fingerprint(rules),
    }
}

impl CompiledRules {
    /// 是否没有任何有效规则（`#[allow(dead_code)]`：当前供单测与后置校验使用）。
    #[allow(dead_code)]
    pub fn is_empty(&self) -> bool {
        self.rules.is_empty()
    }
}

fn matches_app(rule_app: &str, process: &str, title: &str) -> bool {
    if rule_app.is_empty() {
        return true; // 空 = 所有应用（内容级规则）
    }
    process.to_lowercase().contains(rule_app) || title.to_lowercase().contains(rule_app)
}

/// `ignore` 判定（**读剪贴板之前**调用）：命中则整个应用的本次复制不入库。
pub fn decide_ignore(process: &str, title: &str, rules: &CompiledRules) -> bool {
    rules
        .rules
        .iter()
        .any(|r| r.action == AppRuleAction::Ignore && matches_app(&r.app, process, title))
}

/// 应用 `drop` / `replace`（**读快照之后**调用）。返回 `false` = 应丢弃整条内容。
pub fn apply(process: &str, title: &str, snapshot: &mut Snapshot, rules: &CompiledRules) -> bool {
    for r in &rules.rules {
        if r.action == AppRuleAction::Ignore || !matches_app(&r.app, process, title) {
            continue;
        }
        let Some(re) = &r.regex else {
            continue;
        };
        let hit = re.is_match(&snapshot.text)
            || re.is_match(&snapshot.html)
            || re.is_match(&snapshot.rtf);
        match r.action {
            AppRuleAction::Drop => {
                if hit {
                    crate::log::info(format!(
                        "app-rule: 命中丢弃规则（app='{}'）→ 本次不入库",
                        r.app
                    ));
                    return false;
                }
            }
            AppRuleAction::Replace => {
                if hit {
                    snapshot.text = re.replace_all(&snapshot.text, &r.replacement).to_string();
                    snapshot.html = re.replace_all(&snapshot.html, &r.replacement).to_string();
                    snapshot.rtf = re.replace_all(&snapshot.rtf, &r.replacement).to_string();
                    crate::log::info(format!("app-rule: 已按规则替换（app='{}'）", r.app));
                }
            }
            AppRuleAction::Ignore => {}
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::ItemKind;

    fn text_snapshot(text: &str) -> Snapshot {
        Snapshot {
            kind: ItemKind::Text,
            text: text.to_string(),
            ..Default::default()
        }
    }

    #[test]
    fn ignore_matches_by_process_substring_case_insensitive() {
        let rules = compile(&[AppRule {
            app: "Notepad".into(),
            action: AppRuleAction::Ignore,
            pattern: String::new(),
            replacement: String::new(),
        }]);
        assert!(decide_ignore("notepad.exe", "", &rules));
        assert!(decide_ignore("", "Notepad 标题", &rules));
        assert!(!decide_ignore("chrome", "网页", &rules));
    }

    #[test]
    fn empty_app_matches_all() {
        let rules = compile(&[AppRule {
            app: String::new(),
            action: AppRuleAction::Ignore,
            pattern: String::new(),
            replacement: String::new(),
        }]);
        assert!(decide_ignore("anything", "any title", &rules));
    }

    #[test]
    fn drop_blocks_matching_content() {
        let rules = compile(&[AppRule {
            app: "wechat".into(),
            action: AppRuleAction::Drop,
            pattern: r"^\s*https?://\S+$".into(),
            replacement: String::new(),
        }]);
        let mut snap = text_snapshot("https://example.com/x");
        assert!(!apply("wechat", "", &mut snap, &rules), "纯链接应被丢弃");
        let mut snap2 = text_snapshot("正常内容");
        assert!(apply("wechat", "", &mut snap2, &rules), "非链接应保留");
    }

    #[test]
    fn replace_rewrites_content() {
        let rules = compile(&[AppRule {
            app: String::new(),
            action: AppRuleAction::Replace,
            pattern: r"\d{4}-\d{4}-\d{4}".into(),
            replacement: "[号码]".into(),
        }]);
        let mut snap = text_snapshot("卡号 1234-5678-9012 请勿外传");
        assert!(apply("any", "", &mut snap, &rules));
        assert_eq!(snap.text, "卡号 [号码] 请勿外传");
    }

    /// 非法正则：跳过该条但不影响其余规则，也绝不 panic。
    #[test]
    fn invalid_regex_is_skipped_not_fatal() {
        let rules = compile(&[
            AppRule {
                app: "a".into(),
                action: AppRuleAction::Drop,
                pattern: "(unclosed".into(),
                replacement: String::new(),
            },
            AppRule {
                app: "b".into(),
                action: AppRuleAction::Drop,
                pattern: "^junk$".into(),
                replacement: String::new(),
            },
        ]);
        // 非法规则被丢弃，合法规则仍生效
        let mut ok = text_snapshot("junk");
        assert!(!apply("b", "", &mut ok, &rules));
        let mut keep = text_snapshot("fine");
        assert!(apply("b", "", &mut keep, &rules));
    }

    #[test]
    fn empty_rules_are_noop() {
        let rules = compile(&[]);
        assert!(rules.is_empty());
        assert!(!decide_ignore("x", "y", &rules));
        let mut snap = text_snapshot("keep me");
        assert!(apply("x", "y", &mut snap, &rules));
        assert_eq!(snap.text, "keep me");
    }
}
